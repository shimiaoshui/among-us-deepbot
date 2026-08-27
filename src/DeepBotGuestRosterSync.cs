using System;
using System.Collections.Generic;
using HarmonyLib;
using Hazel;
using InnerNet;
using UnityEngine;

namespace AmongUsDeepSeekBots;

/// <summary>
/// Supplies the presentation-only GameData/ClientData records that vanilla
/// late-join snapshots do not create for DeepBot's host-owned PlayerControls.
/// The host remains the only simulation authority. Guests receive only public
/// roster state needed to render the already networked PlayerControl objects.
/// </summary>
internal static class DeepBotGuestRosterSync
{
    internal const byte RpcId = 250;
    private const byte ProtocolVersion = 1;
    private const float HostResendSeconds = 2f;

    private static readonly Dictionary<byte, RosterEntry> Pending = [];
    private static readonly Dictionary<byte, string> AppliedSignatures = [];
    private static readonly Dictionary<int, float> NextSendByClient = [];
    private static int _lastSessionClientId = int.MinValue;
    private static int _lastSessionHostId = int.MinValue;
    private static int _lastGuestGameId = int.MinValue;
    private static bool _receivedLogged;
    private static bool _guestEndGameCleanupActive;

    internal static void SendRosterTo(int targetClientId, string reason)
    {
        var client = AmongUsClient.Instance;
        var sender = PlayerControl.LocalPlayer;
        if (!CanHostSend(client, sender) ||
            targetClientId < 0 ||
            targetClientId == client!.ClientId ||
            DeepBotIdentity.IsReservedClientId(targetClientId))
        {
            return;
        }

        var entries = CaptureHostRoster(client!);
        if (entries.Count == 0)
        {
            return;
        }

        try
        {
            var writer = client!.StartRpcImmediately(sender!.NetId, RpcId, SendOption.Reliable, targetClientId);
            writer.Write(ProtocolVersion);
            writer.Write((byte)Math.Min(entries.Count, byte.MaxValue));
            foreach (var entry in entries)
            {
                WriteEntry(writer, entry);
            }
            client.FinishRpcImmediately(writer);
            if ((string.Equals(reason, "pre-initial-data", StringComparison.Ordinal) ||
                 string.Equals(reason, "post-initial-data", StringComparison.Ordinal)) &&
                GameData.Instance)
            {
                // The custom packet creates passive ClientData proxies first.
                // Mark the host's native GameData dirty afterwards so the
                // authoritative PlayerInfo rows arrive after those proxies.
                GameData.Instance.DirtyAllData();
            }
            Plugin.LogSource.LogInfo(
                $"DeepBot guest roster sent: target={targetClientId}, entries={entries.Count}, reason={reason}.");
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogWarning(
                $"DeepBot guest roster send failed: target={targetClientId}, reason={reason}, " +
                $"error={ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void TickHost()
    {
        var client = AmongUsClient.Instance;
        var sender = PlayerControl.LocalPlayer;
        if (!CanHostSend(client, sender))
        {
            ResetHostSession(client);
            return;
        }

        if (client!.ClientId != _lastSessionClientId || client.HostId != _lastSessionHostId)
        {
            _lastSessionClientId = client.ClientId;
            _lastSessionHostId = client.HostId;
            NextSendByClient.Clear();
        }

        var now = Time.realtimeSinceStartup;
        for (var index = 0; index < client.allClients.Count; index++)
        {
            var target = client.allClients[index];
            if (target is null ||
                target.Id < 0 ||
                target.Id == client.ClientId ||
                DeepBotIdentity.IsReservedClientId(target.Id))
            {
                continue;
            }

            if (NextSendByClient.TryGetValue(target.Id, out var nextAt) && now < nextAt)
            {
                continue;
            }

            NextSendByClient[target.Id] = now + HostResendSeconds;
            SendRosterTo(target.Id, "periodic-lobby-repair");
        }
    }

    internal static bool TryReceive(MessageReader reader)
    {
        var client = AmongUsClient.Instance;
        if (client is null || !client || client.NetworkMode != NetworkModes.LocalGame || client.AmHost)
        {
            return false;
        }

        try
        {
            EnsureGuestSession(client);
            if (client.GameState == InnerNetClient.GameStates.Joined)
            {
                _guestEndGameCleanupActive = false;
            }
            var version = reader.ReadByte();
            if (version != ProtocolVersion)
            {
                Plugin.LogSource.LogWarning(
                    $"DeepBot guest roster protocol mismatch: received={version}, expected={ProtocolVersion}.");
                return true;
            }

            var count = reader.ReadByte();
            var received = new HashSet<byte>();
            for (var index = 0; index < count; index++)
            {
                var entry = ReadEntry(reader);
                if (!DeepBotIdentity.IsReservedClientId(entry.ClientId))
                {
                    continue;
                }

                Pending[entry.PlayerId] = entry;
                received.Add(entry.PlayerId);
            }

            foreach (var playerId in Pending.Keys.ToArray())
            {
                if (!received.Contains(playerId))
                {
                    Pending.Remove(playerId);
                    AppliedSignatures.Remove(playerId);
                }
            }

            // Do not touch allClients before the native initial snapshot has
            // created and bound this guest's own ClientData/PlayerControl.
            // Pre-creating the eight presentation proxies shifts the native
            // client roster while it is still deserializing and can leave the
            // guest's LocalPlayer carrying a bot PlayerId. TOR then assigns
            // the guest's role and ability buttons to the wrong object. Keep
            // only the roster payload here; ApplyPendingRoster materializes
            // proxies after the authoritative human identity is stable.

            if (!_receivedLogged)
            {
                _receivedLogged = true;
                Plugin.LogSource.LogInfo(
                    $"DeepBot guest roster RPC received: entries={received.Count}, deferredProxies=true, " +
                    $"clientId={client.ClientId}, hostId={client.HostId}.");
            }
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogWarning(
                $"DeepBot guest roster receive failed: {ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    internal static void ApplyPendingRoster(AmongUsClient client)
    {
        if (!client ||
            client.NetworkMode != NetworkModes.LocalGame ||
            client.AmHost ||
            Pending.Count == 0 ||
            !GameData.Instance ||
            _guestEndGameCleanupActive)
        {
            return;
        }

        EnsureGuestSession(client);
        if (!EnsurePassiveGuestLocalPlayer(client, "apply-roster"))
        {
            return;
        }

        var controls = 0;
        var appearancesApplied = 0;
        var duplicateInfosRemoved = RemoveDuplicateReservedInfos();
        var staleProxyClientsRemoved = RemoveReservedGuestClientData(client);

        foreach (var entry in Pending.Values)
        {
            var info = FindReservedInfo(entry);
            var character = FindBotPlayerControl(client, entry, info);
            if (!character)
            {
                continue;
            }
            controls++;

            if (info is null || !info)
            {
                continue;
            }

            var signature = entry.Signature;
            if (!AppliedSignatures.TryGetValue(entry.PlayerId, out var applied) ||
                !string.Equals(applied, signature, StringComparison.Ordinal))
            {
                ApplyAppearance(character!, entry);
                AppliedSignatures[entry.PlayerId] = signature;
                appearancesApplied++;
            }
        }

        if (duplicateInfosRemoved > 0 || staleProxyClientsRemoved > 0 || appearancesApplied > 0)
        {
            Plugin.LogSource.LogInfo(
                $"DeepBot guest roster applied: entries={Pending.Count}, controls={controls}, " +
                $"nativePlayerInfosOnly=true, duplicateInfosRemoved={duplicateInfosRemoved}, " +
                $"proxyClientsCreated=0, staleProxyClientsRemoved={staleProxyClientsRemoved}, " +
                $"appearancesApplied={appearancesApplied}.");
        }
    }

    internal static int PreparePassiveGuestEndGame(AmongUsClient client)
    {
        if (!client || client.AmHost)
        {
            return 0;
        }

        _guestEndGameCleanupActive = true;
        var removed = 0;
        for (var index = client.allClients.Count - 1; index >= 0; index--)
        {
            var candidate = client.allClients[index];
            if (candidate is null || !DeepBotIdentity.IsReservedClientId(candidate.Id))
            {
                continue;
            }

            candidate.Character = null;
            client.allClients.RemoveAt(index);
            removed++;
        }

        Pending.Clear();
        AppliedSignatures.Clear();
        _receivedLogged = false;
        EnsurePassiveGuestLocalPlayer(client, "pre-endgame");
        return removed;
    }

    internal static bool IsGuestEndGameCleanupActive => _guestEndGameCleanupActive;

    internal static int RemoveReservedGuestClientData(AmongUsClient client)
    {
        if (!client || client.AmHost)
        {
            return 0;
        }

        var removed = 0;
        for (var index = client.allClients.Count - 1; index >= 0; index--)
        {
            var candidate = client.allClients[index];
            if (candidate is null || !DeepBotIdentity.IsReservedClientId(candidate.Id))
            {
                continue;
            }

            candidate.Character = null;
            client.allClients.RemoveAt(index);
            removed++;
        }
        return removed;
    }

    private static int RemoveDuplicateReservedInfos()
    {
        if (!GameData.Instance)
        {
            return 0;
        }

        // Native deserialization appends the authoritative row after any
        // obsolete local row made by 0.10.8. Keep the newest row for each bot
        // and remove only duplicate reserved-player rows. Human entries are
        // never touched.
        var seen = new HashSet<byte>();
        var removed = 0;
        var players = GameData.Instance.AllPlayers;
        for (var index = players.Count - 1; index >= 0; index--)
        {
            var info = players[index];
            if (info is null || !DeepBotIdentity.IsReservedClientId(info.ClientId))
            {
                continue;
            }

            if (seen.Add(info.PlayerId))
            {
                continue;
            }

            players.RemoveAt(index);
            removed++;
        }

        if (removed > 0)
        {
            Plugin.LogSource.LogWarning(
                $"DeepBot guest duplicate roster repair removed {removed} stale reserved PlayerInfo rows.");
        }
        return removed;
    }

    private static bool CanHostSend(AmongUsClient? client, PlayerControl? sender)
    {
        return Plugin.Settings.Enabled.Value &&
               client is not null &&
               client &&
               sender &&
               Plugin.AllowsWorldAuthority(client.AmHost) &&
               client.NetworkMode == NetworkModes.LocalGame &&
               client.ClientId >= 0;
    }

    private static List<RosterEntry> CaptureHostRoster(AmongUsClient client)
    {
        var entries = new List<RosterEntry>();
        if (!GameData.Instance)
        {
            return entries;
        }

        foreach (var info in GameData.Instance.AllPlayers)
        {
            if (info is null ||
                !DeepBotIdentity.IsReservedClientId(info.ClientId) ||
                info.Disconnected ||
                info.DefaultOutfit is null)
            {
                continue;
            }

            var outfit = info.DefaultOutfit;
            entries.Add(new RosterEntry(
                info.PlayerId,
                info.ClientId,
                info.PlayerName ?? string.Empty,
                outfit.ColorId,
                outfit.HatId ?? string.Empty,
                outfit.SkinId ?? string.Empty,
                outfit.VisorId ?? string.Empty,
                outfit.PetId ?? string.Empty,
                outfit.NamePlateId ?? string.Empty,
                info.IsDead));
        }
        return entries;
    }

    private static void WriteEntry(MessageWriter writer, RosterEntry entry)
    {
        writer.Write(entry.PlayerId);
        writer.Write(entry.ClientId);
        writer.Write(entry.PlayerName);
        writer.Write(entry.ColorId);
        writer.Write(entry.HatId);
        writer.Write(entry.SkinId);
        writer.Write(entry.VisorId);
        writer.Write(entry.PetId);
        writer.Write(entry.NamePlateId);
        writer.Write(entry.IsDead);
    }

    private static RosterEntry ReadEntry(MessageReader reader)
    {
        return new RosterEntry(
            reader.ReadByte(),
            reader.ReadInt32(),
            reader.ReadString(),
            reader.ReadInt32(),
            reader.ReadString(),
            reader.ReadString(),
            reader.ReadString(),
            reader.ReadString(),
            reader.ReadString(),
            reader.ReadBoolean());
    }

    private static ClientData CreateProxy(AmongUsClient client, RosterEntry entry, PlayerControl? character)
    {
        var platform = new PlatformSpecificData
        {
            Platform = Platforms.StandaloneSteamPC,
            PlatformName = "DeepBot Host Proxy"
        };
        var proxy = new ClientData(
            entry.ClientId,
            entry.PlayerName,
            platform,
            5u,
            string.Empty,
            string.Empty);
        UpdateProxy(proxy, entry, character);
        client.allClients.Add(proxy);
        return proxy;
    }

    private static void UpdateProxy(ClientData proxy, RosterEntry entry, PlayerControl? character)
    {
        proxy.InScene = true;
        proxy.IsReady = true;
        proxy.IsBeingCreated = false;
        proxy.PlayerName = entry.PlayerName;
        proxy.ColorId = entry.ColorId;
        if (character)
        {
            proxy.Character = character;
        }
    }

    private static void EnsureGuestSession(AmongUsClient client)
    {
        if (client.ClientId == _lastSessionClientId &&
            client.HostId == _lastSessionHostId &&
            client.GameId == _lastGuestGameId)
        {
            return;
        }

        _lastSessionClientId = client.ClientId;
        _lastSessionHostId = client.HostId;
        _lastGuestGameId = client.GameId;
        Pending.Clear();
        AppliedSignatures.Clear();
        _receivedLogged = false;
        _guestEndGameCleanupActive = false;
    }

    internal static bool EnsurePassiveGuestLocalPlayer(AmongUsClient client, string reason)
    {
        if (!client || client.ClientId < 0 || !GameData.Instance)
        {
            return false;
        }

        NetworkedPlayerInfo? authoritativeInfo = null;
        foreach (var info in GameData.Instance.AllPlayers)
        {
            if (info is not null &&
                info.ClientId == client.ClientId &&
                !DeepBotIdentity.IsReservedClientId(info.ClientId))
            {
                authoritativeInfo = info;
                break;
            }
        }

        if (authoritativeInfo is null || !authoritativeInfo)
        {
            return false;
        }

        PlayerControl? ownedHuman = null;
        var ownClient = FindClientData(client, client.ClientId);
        var ownCharacter = ownClient?.Character;
        var authoritativeCharacter = authoritativeInfo.Object;
        if (authoritativeCharacter is not null &&
            authoritativeCharacter &&
            authoritativeCharacter.OwnerId == client.ClientId)
        {
            ownedHuman = authoritativeCharacter;
        }
        else if (ownCharacter is not null && ownCharacter && ownCharacter.OwnerId == client.ClientId)
        {
            ownedHuman = ownCharacter;
        }

        if (!ownedHuman)
        {
            foreach (var candidate in PlayerControl.AllPlayerControls)
            {
                if (candidate && candidate.OwnerId == client.ClientId)
                {
                    ownedHuman = candidate;
                    break;
                }
            }
        }

        if (!ownedHuman)
        {
            return false;
        }

        var human = ownedHuman!;
        var replaced = PlayerControl.LocalPlayer;
        var oldPlayerId = human.PlayerId;
        var changed = false;
        if (human.PlayerId != authoritativeInfo.PlayerId)
        {
            human.PlayerId = authoritativeInfo.PlayerId;
            changed = true;
        }
        if (PlayerControl.LocalPlayer != human)
        {
            PlayerControl.LocalPlayer = human;
            changed = true;
        }
        if (ownClient is not null && ownClient.Character != human)
        {
            ownClient.Character = human;
            changed = true;
        }

        // No reserved presentation client may ever own or reference the local
        // human character. Clear a stale binding before TOR evaluates AmOwner,
        // Data.Role, kill/vent buttons, death animation, or the intro banner.
        for (var index = 0; index < client.allClients.Count; index++)
        {
            var candidate = client.allClients[index];
            if (candidate is not null &&
                DeepBotIdentity.IsReservedClientId(candidate.Id) &&
                candidate.Character == human)
            {
                candidate.Character = null;
                changed = true;
            }
        }

        if (changed)
        {
            Plugin.LogSource.LogWarning(
                $"DeepBot restored authoritative guest identity: reason={reason}, clientId={client.ClientId}, " +
                $"authoritativePlayerId={authoritativeInfo.PlayerId}, oldPlayerId={oldPlayerId}, " +
                $"from={DescribePlayer(replaced)}, to={DescribePlayer(human)}.");
        }
        return human.PlayerId == authoritativeInfo.PlayerId &&
               PlayerControl.LocalPlayer == human &&
               human.Data is not null &&
               human.Data.ClientId == client.ClientId;
    }

    private static void ApplyAppearance(PlayerControl character, RosterEntry entry)
    {
        try
        {
            character.SetName(entry.PlayerName);
            character.SetColor(entry.ColorId);
            character.SetHat(entry.HatId, entry.ColorId);
            character.SetSkin(entry.SkinId, entry.ColorId);
            character.SetVisor(entry.VisorId, entry.ColorId);
            character.SetPet(entry.PetId, entry.ColorId);
            character.SetNamePlate(entry.NamePlateId);
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogWarning(
                $"DeepBot guest appearance apply failed for playerId={entry.PlayerId}: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static NetworkedPlayerInfo? FindReservedInfo(RosterEntry entry)
    {
        if (!GameData.Instance)
        {
            return null;
        }

        foreach (var info in GameData.Instance.AllPlayers)
        {
            if (info is not null &&
                info.PlayerId == entry.PlayerId &&
                info.ClientId == entry.ClientId)
            {
                return info;
            }
        }
        return null;
    }

    private static PlayerControl? FindBotPlayerControl(
        AmongUsClient client,
        RosterEntry entry,
        NetworkedPlayerInfo? info)
    {
        var infoObject = info?.Object;
        if (infoObject is not null && infoObject && IsSafeBotControl(client, entry, infoObject))
        {
            return infoObject;
        }

        PlayerControl? hostOwnedFallback = null;
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (!player ||
                player.PlayerId != entry.PlayerId ||
                player == PlayerControl.LocalPlayer ||
                player.OwnerId == client.ClientId)
            {
                continue;
            }

            if (player.Data is not null && player.Data.ClientId == entry.ClientId)
            {
                return player;
            }

            if (player.OwnerId == client.HostId)
            {
                hostOwnedFallback = player;
            }
        }
        return hostOwnedFallback;
    }

    private static bool IsSafeBotControl(AmongUsClient client, RosterEntry entry, PlayerControl player)
    {
        if (!player ||
            player == PlayerControl.LocalPlayer ||
            player.OwnerId == client.ClientId ||
            player.PlayerId != entry.PlayerId)
        {
            return false;
        }

        return (player.Data is not null && player.Data.ClientId == entry.ClientId) ||
               player.OwnerId == client.HostId;
    }

    private static string DescribePlayer(PlayerControl? player)
    {
        if (!player)
        {
            return "missing";
        }

        return $"{player!.Data?.PlayerName ?? "unbound"}({player.PlayerId})/owner={player.OwnerId}";
    }

    private static ClientData? FindClientData(AmongUsClient client, int clientId)
    {
        for (var index = 0; index < client.allClients.Count; index++)
        {
            var candidate = client.allClients[index];
            if (candidate is not null && candidate.Id == clientId)
            {
                return candidate;
            }
        }
        return null;
    }

    private static void ResetHostSession(AmongUsClient? client)
    {
        if (client is not null &&
            client &&
            client.ClientId == _lastSessionClientId &&
            client.HostId == _lastSessionHostId)
        {
            return;
        }

        _lastSessionClientId = client?.ClientId ?? int.MinValue;
        _lastSessionHostId = client?.HostId ?? int.MinValue;
        NextSendByClient.Clear();
    }

    private sealed record RosterEntry(
        byte PlayerId,
        int ClientId,
        string PlayerName,
        int ColorId,
        string HatId,
        string SkinId,
        string VisorId,
        string PetId,
        string NamePlateId,
        bool IsDead)
    {
        internal string Signature => string.Join(
            "\u001f",
            ClientId,
            PlayerName,
            ColorId,
            HatId,
            SkinId,
            VisorId,
            PetId,
            NamePlateId);
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
internal static class DeepBotGuestRosterRpcPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(byte callId, MessageReader reader)
    {
        var client = AmongUsClient.Instance;
        if (client is not null &&
            client &&
            client.NetworkMode == NetworkModes.LocalGame &&
            !client.AmHost)
        {
            DeepBotGuestRosterSync.EnsurePassiveGuestLocalPlayer(client, $"pre-rpc-{callId}");
        }

        if (callId != DeepBotGuestRosterSync.RpcId)
        {
            return true;
        }

        DeepBotGuestRosterSync.TryReceive(reader);
        return false;
    }
}
