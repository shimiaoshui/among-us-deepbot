using System;
using System.Collections.Generic;
using System.Reflection;
using AmongUs.GameOptions;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using InnerNet;
using UnityEngine;

namespace AmongUsDeepSeekBots;

internal sealed class SafeLocalBotSpawner
{
    private const float LobbyBotCountStableSeconds = 2f;
    private const float PassiveRosterSyncSeconds = 0.35f;
    private static readonly MethodInfo? CreatePlayerMethod =
        AccessTools.Method(typeof(AmongUsClient), "CreatePlayer");

    private readonly ManualLogSource _log;
    private readonly List<TrackedBotClient> _tracked = [];
    private readonly HashSet<byte> _visibilityRestored = [];
    private readonly HashSet<byte> _renderDiagnosticsLogged = [];
    private readonly HashSet<byte> _disabledBotLightIds = [];
    private readonly HashSet<int> _passiveProxyClientIds = [];
    private readonly Dictionary<int, string> _appliedLobbyAppearances = [];
    private readonly LobbyBotCountStabilizer _botCountStabilizer = new(LobbyBotCountStableSeconds);
    private float _nextSpawnAt;
    private float _nextStatusAt;
    private float _nextGuestStatusAt;
    private float _nextPassiveRosterSyncAt;
    private bool _spawnBlocked;
    private bool _startedSpawnBlockLogged;
    private bool _hostLightRepairLogged;
    private PlayerControl? _hostPlayer;

    public SafeLocalBotSpawner(ManualLogSource log)
    {
        _log = log;
    }

    public void Tick(PluginConfig config)
    {
        var client = AmongUsClient.Instance;
        if (IsPassiveLanGuest(client))
        {
            MaintainPassiveClientView();
            return;
        }

        if (!IsLocalHostReady(client, out var reason))
        {
            if (Time.time >= _nextStatusAt)
            {
                _nextStatusAt = Time.time + 8f;
                _log.LogInfo($"DeepBot spawn preflight waiting: {reason}");
            }

            ResetTransientState();
            return;
        }

        var observedRequestedCount = TorRoleAdapter.GetLobbyConfiguredBotCount(config.LocalBotCount.Value);
        var requestedCount = _botCountStabilizer.Observe(
            observedRequestedCount,
            Time.time,
            client.GameState == InnerNetClient.GameStates.Started,
            out var countTransition);
        if (countTransition is not null)
        {
            _log.LogInfo($"DeepBot lobby bot-count stabilized: {countTransition}");
        }
        CaptureHostPlayer(client);
        var existingCount = CountManagedClients(client);
        var realPlayerCount = CountNonManagedClients(client);
        var lobbyCapacity = GameRuleSettings.GetMaxPlayers();
        var targetCount = Mathf.Clamp(
            requestedCount,
            0,
            Mathf.Max(0, lobbyCapacity - realPlayerCount));
        if (client.GameState != InnerNetClient.GameStates.Started && existingCount > targetCount)
        {
            PruneExcessLobbyBots(client, targetCount);
            existingCount = CountManagedClients(client);
        }
        if (client.GameState != InnerNetClient.GameStates.Started)
        {
            PruneOrphanedManagedPlayerInfos(client, targetCount);
        }
        ConfigureTrackedClients(client);
        if (client.GameState != InnerNetClient.GameStates.Started)
        {
            RefreshLobbyAppearances(client);
        }
        EnsureHostLocalPlayer(client, "spawner tick");

        if (Time.time >= _nextStatusAt)
        {
            _nextStatusAt = Time.time + 8f;
            _log.LogInfo(
                $"DeepBot spawn preflight ok: dryRun={config.DryRun.Value}, requested={requestedCount}, target={targetCount}, " +
                $"existing={existingCount}, realPlayers={realPlayerCount}, capacity={lobbyCapacity}, " +
                $"clients={client.allClients.Count}, gameState={client.GameState}.");
        }

        if (client.GameState == InnerNetClient.GameStates.Started && existingCount < targetCount)
        {
            if (!_startedSpawnBlockLogged)
            {
                _startedSpawnBlockLogged = true;
                _log.LogError(
                    $"DeepBot refused unsafe mid-intro spawn: configured={targetCount}, ready={existingCount}. " +
                    "Bots must finish native lobby creation before BeginGame so TOR cannot bind HUD/role state to a temporary bot LocalPlayer.");
            }
            return;
        }

        if (config.DryRun.Value || _spawnBlocked || existingCount >= targetCount || Time.time < _nextSpawnAt)
        {
            return;
        }

        _nextSpawnAt = Time.time + 0.75f;
        TryCreateOne(client, FindNextBotIndex(client, targetCount));
    }

    internal static bool AreConfiguredLobbyBotsReady(out string reason)
    {
        reason = string.Empty;
        var client = AmongUsClient.Instance;
        if (!client || !client.AmHost || client.NetworkMode != NetworkModes.LocalGame)
        {
            return true;
        }

        var requested = TorRoleAdapter.GetLobbyConfiguredBotCount(Plugin.Settings.LocalBotCount.Value);
        var realPlayers = 0;
        var readyBots = 0;
        for (var index = 0; index < client.allClients.Count; index++)
        {
            var candidate = client.allClients[index];
            if (candidate is null)
            {
                continue;
            }

            if (!DeepBotIdentity.TryGetBotIndex(candidate, out _))
            {
                realPlayers++;
                continue;
            }

            if (candidate.InScene && candidate.IsReady && !candidate.IsBeingCreated &&
                candidate.Character && candidate.Character.Data is not null &&
                candidate.Character.MyPhysics && candidate.Character.NetTransform)
            {
                readyBots++;
            }
        }

        var target = Mathf.Clamp(requested, 0, Mathf.Max(0, GameRuleSettings.GetMaxPlayers() - realPlayers));
        var managedPlayerInfos = GameData.Instance
            ? GameData.Instance.AllPlayers.ToArray().Count(info =>
                info is not null && DeepBotIdentity.IsReservedClientId(info.ClientId))
            : readyBots;
        if (IsExactLobbyRosterReady(readyBots, managedPlayerInfos, target))
        {
            return true;
        }

        reason = readyBots > target || managedPlayerInfos > target
            ? $"configured={target}, nativeLobbyReady={readyBots}, playerInfos={managedPlayerInfos}; removing excess AI entries"
            : $"configured={target}, nativeLobbyReady={readyBots}, playerInfos={managedPlayerInfos}; wait for the remaining {Math.Max(0, target - readyBots)} bot(s)";
        return false;
    }

    private static bool IsExactLobbyRosterReady(int readyBots, int managedPlayerInfos, int target)
    {
        return readyBots == target && managedPlayerInfos == target;
    }

    private void PruneOrphanedManagedPlayerInfos(AmongUsClient client, int targetCount)
    {
        if (!GameData.Instance)
        {
            return;
        }

        var liveByClientId = new Dictionary<int, byte>();
        for (var index = 0; index < client.allClients.Count; index++)
        {
            var candidate = client.allClients[index];
            if (candidate is null || !DeepBotIdentity.TryGetBotIndex(candidate, out var botIndex) || botIndex >= targetCount ||
                !candidate.Character || candidate.Character.Data is null)
            {
                continue;
            }

            liveByClientId[candidate.Id] = candidate.Character.PlayerId;
        }

        foreach (var info in GameData.Instance.AllPlayers.ToArray())
        {
            if (info is null || !DeepBotIdentity.IsReservedClientId(info.ClientId))
            {
                continue;
            }

            var belongsToLiveClient = liveByClientId.TryGetValue(info.ClientId, out var livePlayerId) &&
                                      livePlayerId == info.PlayerId;
            if (belongsToLiveClient)
            {
                continue;
            }

            GameData.Instance.RemovePlayer(info.PlayerId);
            _log.LogWarning(
                $"DeepBot pruned orphaned lobby player info: client={info.ClientId}, playerId={info.PlayerId}, " +
                $"target={targetCount}, remaining={GameData.Instance.AllPlayers.Count}.");
        }
    }

    private void PruneExcessLobbyBots(AmongUsClient client, int targetCount)
    {
        var managed = new List<(ClientData Client, int BotIndex)>();
        for (var i = 0; i < client.allClients.Count; i++)
        {
            var candidate = client.allClients[i];
            if (candidate is null || candidate.Id == client.ClientId)
            {
                continue;
            }

            if (DeepBotIdentity.TryGetBotIndex(candidate, out var botIndex))
            {
                managed.Add((candidate, botIndex));
            }
        }

        foreach (var item in managed
                     .OrderByDescending(item => item.BotIndex)
                     .ThenByDescending(item => item.Client.Id)
                     .Take(Math.Max(0, managed.Count - targetCount)))
        {
            var character = item.Client.Character;
            var playerId = character && character.Data is not null
                ? (byte?)character.PlayerId
                : null;
            try
            {
                // Use the game's own client-removal path first. It removes the
                // ClientData, network object and GameData.PlayerInfo together.
                // The previous Destroy + allClients.RemoveAt left stale player
                // infos behind, so changing 8 -> 1 -> 8 displayed 16/15.
                client.RemovePlayer(item.Client.Id, DisconnectReasons.Destroy);
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    $"DeepBot native lobby removal failed for client={item.Client.Id}; applying local cleanup: {ex.GetBaseException().Message}");
            }

            if (playerId.HasValue && GameData.Instance)
            {
                GameData.Instance.RemovePlayer(playerId.Value);
            }

            var remainingIndex = -1;
            for (var index = 0; index < client.allClients.Count; index++)
            {
                if (client.allClients[index]?.Id == item.Client.Id)
                {
                    remainingIndex = index;
                    break;
                }
            }

            if (remainingIndex >= 0)
            {
                client.allClients.RemoveAt(remainingIndex);
            }

            if (character)
            {
                client.RemoveNetObject(character);
                UnityEngine.Object.Destroy(character.gameObject);
            }

            _tracked.RemoveAll(tracked => tracked.ClientId == item.Client.Id);
            _appliedLobbyAppearances.Remove(item.Client.Id);
            _log.LogInfo(
                $"DeepBot lobby roster reduced: removed=DeepBot {item.BotIndex + 1}, client={item.Client.Id}, " +
                $"playerId={playerId?.ToString() ?? "unknown"}, target={targetCount}, " +
                $"gameDataPlayers={(GameData.Instance ? GameData.Instance.AllPlayers.Count : -1)}.");
        }
    }

    public void MaintainHostLocalView()
    {
        var client = AmongUsClient.Instance;
        if (client is null || !client || !client.AmHost || client.ClientId < 0)
        {
            return;
        }

        CaptureHostPlayer(client);
        EnsureHostLocalPlayer(client, "runtime frame");
        EnsureHostCameraTarget(client, "runtime frame");
        EnsureHostVisionLight(client, "runtime frame");
    }

    public void MaintainPassiveClientView()
    {
        var client = AmongUsClient.Instance;
        if (!IsPassiveLanGuest(client))
        {
            ClearPassiveGuestProxies(client);
            return;
        }

        var now = Time.realtimeSinceStartup;
        if (now < _nextPassiveRosterSyncAt)
        {
            return;
        }
        _nextPassiveRosterSyncAt = now + PassiveRosterSyncSeconds;

        var reservedInfos = 0;
        var matchedControls = 0;
        var createdProxies = 0;
        if (GameData.Instance)
        {
            foreach (var info in GameData.Instance.AllPlayers)
            {
                if (info is null || !DeepBotIdentity.IsReservedClientId(info.ClientId))
                {
                    continue;
                }

                reservedInfos++;
                var character = FindPlayerControl(info.PlayerId);
                if (character)
                {
                    matchedControls++;
                }

                var proxy = FindClientData(client!, info.ClientId);
                if (proxy is null)
                {
                    var platform = new PlatformSpecificData
                    {
                        Platform = Platforms.StandaloneSteamPC,
                        PlatformName = "DeepBot Host Proxy"
                    };
                    proxy = new ClientData(info.ClientId, info.PlayerName, platform, 5u, string.Empty, string.Empty)
                    {
                        InScene = true,
                        IsReady = true,
                        IsBeingCreated = false,
                        Character = character,
                        ColorId = info.DefaultOutfit.ColorId
                    };
                    client!.allClients.Add(proxy);
                    _passiveProxyClientIds.Add(info.ClientId);
                    createdProxies++;
                }
                else
                {
                    proxy.InScene = true;
                    proxy.IsReady = true;
                    proxy.IsBeingCreated = false;
                    proxy.PlayerName = info.PlayerName;
                    proxy.ColorId = info.DefaultOutfit.ColorId;
                    if (character && proxy.Character != character)
                    {
                        proxy.Character = character;
                    }
                }
            }
        }

        PruneMissingPassiveGuestProxies(client!);
        EnsureLivePlayersVisible(client!, botsOnly: true, allowLobby: true);

        if (Time.time >= _nextGuestStatusAt)
        {
            _nextGuestStatusAt = Time.time + 5f;
            _log.LogInfo(
                $"DeepBot LAN guest presentation sync: clientId={client!.ClientId}, hostId={client.HostId}, " +
                $"reservedInfos={reservedInfos}, matchedControls={matchedControls}, proxyClients={_passiveProxyClientIds.Count}, " +
                $"createdNow={createdProxies}, clients={client.allClients.Count}. Host remains sole AI authority.");
        }
    }

    private void PruneMissingPassiveGuestProxies(AmongUsClient client)
    {
        foreach (var clientId in _passiveProxyClientIds.ToArray())
        {
            var stillPresent = false;
            if (GameData.Instance)
            {
                foreach (var info in GameData.Instance.AllPlayers)
                {
                    if (info is not null && info.ClientId == clientId)
                    {
                        stillPresent = true;
                        break;
                    }
                }
            }

            if (stillPresent)
            {
                continue;
            }

            RemoveClientDataLocally(client, clientId);
            _passiveProxyClientIds.Remove(clientId);
        }
    }

    private void ClearPassiveGuestProxies(AmongUsClient? client)
    {
        if (client is not null && client)
        {
            foreach (var clientId in _passiveProxyClientIds)
            {
                RemoveClientDataLocally(client, clientId);
            }
        }
        _passiveProxyClientIds.Clear();
        _nextPassiveRosterSyncAt = 0f;
    }

    private static void RemoveClientDataLocally(AmongUsClient client, int clientId)
    {
        for (var index = client.allClients.Count - 1; index >= 0; index--)
        {
            if (client.allClients[index]?.Id == clientId)
            {
                client.allClients.RemoveAt(index);
            }
        }
    }

    private static ClientData? FindClientData(AmongUsClient client, int clientId)
    {
        for (var index = 0; index < client.allClients.Count; index++)
        {
            var candidate = client.allClients[index];
            if (candidate?.Id == clientId)
            {
                return candidate;
            }
        }
        return null;
    }

    private static PlayerControl? FindPlayerControl(byte playerId)
    {
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (player && player.PlayerId == playerId)
            {
                return player;
            }
        }
        return null;
    }

    private void TryCreateOne(AmongUsClient client, int botIndex)
    {
        if (CreatePlayerMethod is null)
        {
            _spawnBlocked = true;
            _log.LogError("DeepBot spawn blocked: AmongUsClient.CreatePlayer was not found.");
            return;
        }

        var clientId = FindAvailableClientId(client);
        if (clientId < 0)
        {
            _spawnBlocked = true;
            _log.LogError("DeepBot spawn blocked: no free reserved client id in 64..95.");
            return;
        }

        var appearance = TorRoleAdapter.GetLobbyAppearance(botIndex);
        var displayName = ResolveUniqueLobbyName(botIndex, appearance.NameSelection);
        var platform = new PlatformSpecificData
        {
            Platform = Platforms.StandaloneSteamPC,
            PlatformName = "DeepSeek Bot"
        };

        var botClient = new ClientData(clientId, displayName, platform, 5u, string.Empty, string.Empty)
        {
            InScene = true,
            IsReady = true,
            IsBeingCreated = false
        };

        try
        {
            client.allClients.Add(botClient);
            var created = CreatePlayerMethod.Invoke(client, [botClient]);
            StartCreatePlayerCoroutine(client, created, botClient);

            // Unity starts a coroutine immediately up to its first yield. The
            // game's CreatePlayer routine can therefore replace LocalPlayer and
            // the camera target before the next runtime Update. Restore both in
            // the same call stack so no bot-view frame reaches the renderer.
            EnsureHostLocalPlayer(client, $"queued {displayName}");
            EnsureHostCameraTarget(client, $"queued {displayName}");

            _tracked.Add(new TrackedBotClient(clientId, botIndex, botClient));
            _log.LogInfo($"DeepBot queued local bot: index={botIndex + 1}, clientId={clientId}, hostClientId={client.ClientId}, clients={client.allClients.Count}.");
        }
        catch (Exception ex)
        {
            client.allClients.Remove(botClient);
            _spawnBlocked = true;
            _log.LogError($"DeepBot spawn failed for {displayName} ({clientId}): {ex}");
        }
    }

    private void ConfigureTrackedClients(AmongUsClient client)
    {
        for (var i = _tracked.Count - 1; i >= 0; i--)
        {
            var tracked = _tracked[i];
            var character = tracked.Client.Character;
            if (!character || character.Data is null || !character.MyPhysics || !character.NetTransform)
            {
                continue;
            }

            var appearance = TorRoleAdapter.GetLobbyAppearance(tracked.Index);
            var name = ResolveUniqueLobbyName(tracked.Index, appearance.NameSelection);
            tracked.Client.InScene = true;
            tracked.Client.IsReady = true;
            tracked.Client.IsBeingCreated = false;
            tracked.Client.PlayerName = name;

            character.isDummy = false;
            character.Data.PlayerName = name;
            character.Data.Disconnected = false;
            character.Data.IsDead = false;
            character.SetName(name);
            character.RpcSetName(name);

            var color = DeepBotAppearance.ResolveColor(tracked.Index, appearance.ColorSelection);
            character.SetColor(color);
            character.RpcSetColor((byte)color);
            var spawnPoint = GetSpawnPoint(client, tracked.Index, out var spawnSource);
            character.NetTransform.SnapTo(spawnPoint);

            AssignRuntimeOwnership(client, character, tracked.ClientId);
            EnsureHostLocalPlayer(client, $"configured {name}");
            EnsureHostCameraTarget(client, $"configured {name}");

            _log.LogInfo(
                $"DeepBot local bot ready: player={character.PlayerId}, client={tracked.ClientId}, " +
                $"owner={character.OwnerId}, netOwner={character.NetTransform.OwnerId}, physicsOwner={character.MyPhysics.OwnerId}, " +
                $"name={character.Data.PlayerName}, spawn={spawnPoint}, spawnSource={spawnSource}.");

            _tracked.RemoveAt(i);
        }
    }

    private void RefreshLobbyAppearances(AmongUsClient client)
    {
        for (var i = 0; i < client.allClients.Count; i++)
        {
            var candidate = client.allClients[i];
            if (!DeepBotIdentity.TryGetBotIndex(candidate, out var botIndex) ||
                candidate.Character is not { } character ||
                !character ||
                character.Data is null)
            {
                continue;
            }

            var appearance = TorRoleAdapter.GetLobbyAppearance(botIndex);
            var name = ResolveUniqueLobbyName(botIndex, appearance.NameSelection);
            var color = DeepBotAppearance.ResolveColor(botIndex, appearance.ColorSelection);
            var gameId = appearance.OutfitSelection == 3 || appearance.NamePlateSelection == 3 ? client.GameId : 0;
            var signature = $"{name}:{appearance.NameSelection}:{appearance.ColorSelection}:{appearance.OutfitSelection}:{appearance.NamePlateSelection}:{gameId}";
            if (_appliedLobbyAppearances.TryGetValue(candidate.Id, out var applied) && applied == signature)
            {
                continue;
            }

            var nameChanged = !string.Equals(candidate.PlayerName, name, StringComparison.Ordinal) ||
                              !string.Equals(character.Data.PlayerName, name, StringComparison.Ordinal);
            if (nameChanged)
            {
                candidate.PlayerName = name;
                character.Data.PlayerName = name;
                character.SetName(name);
                character.RpcSetName(name);
            }

            var colorChanged = character.Data.DefaultOutfit.ColorId != color;
            if (colorChanged)
            {
                character.SetColor(color);
                character.RpcSetColor((byte)color);
            }
            DeepBotAppearance.ApplyOutfit(character, _hostPlayer, botIndex, appearance.OutfitSelection, _log);
            DeepBotAppearance.ApplyNamePlate(character, _hostPlayer, botIndex, appearance.NamePlateSelection, _log);
            _appliedLobbyAppearances[candidate.Id] = signature;
            _log.LogInfo(
                $"DeepBot lobby appearance applied: bot={botIndex + 1}, client={candidate.Id}, " +
                $"name={name}, color={color}, nameChanged={nameChanged}, colorChanged={colorChanged}, " +
                $"outfit={appearance.OutfitSelection}, nameplate={appearance.NamePlateSelection}.");
        }
    }

    private static string ResolveUniqueLobbyName(int botIndex, int nameSelection)
    {
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            // Never use a bot's current display name to decide its next display
            // name. Doing that made Iris -> Iris 1 -> Iris on alternating frames
            // after runtime ownership moved to the host, creating an RPC storm.
            if (player && player.Data is not null && !DeepBotIdentity.IsBot(player))
            {
                occupied.Add(player.Data.PlayerName);
            }
        }

        for (var index = 0; index <= botIndex; index++)
        {
            var appearance = index == botIndex
                ? TorRoleAdapter.GetLobbyAppearance(botIndex) with { NameSelection = nameSelection }
                : TorRoleAdapter.GetLobbyAppearance(index);
            var configuredName = DeepBotAppearance.ResolveName(index, appearance.NameSelection);
            var resolvedName = ResolveStableUniqueName(configuredName, index, occupied);
            if (index == botIndex)
            {
                return resolvedName;
            }

            occupied.Add(resolvedName);
        }

        return DeepBotAppearance.ResolveName(botIndex, nameSelection);
    }

    private static string ResolveStableUniqueName(string configuredName, int botIndex, ISet<string> occupied)
    {
        if (!occupied.Contains(configuredName))
        {
            return configuredName;
        }

        var suffix = botIndex + 1;
        string candidate;
        do
        {
            candidate = $"{configuredName} {suffix++}";
        }
        while (occupied.Contains(candidate));

        return candidate;
    }

    private static void StartCreatePlayerCoroutine(AmongUsClient client, object? created, ClientData botClient)
    {
        if (created is Il2CppSystem.Collections.IEnumerator il2CppEnumerator)
        {
            client.StartCoroutine(il2CppEnumerator);
            return;
        }

        if (created is System.Collections.IEnumerator enumerator)
        {
            client.StartCoroutine(BepInEx.Unity.IL2CPP.Utils.Collections.CollectionExtensions.WrapToIl2Cpp(enumerator));
            return;
        }

        throw new InvalidOperationException($"CreatePlayer returned unsupported value for {botClient.Id}: {created?.GetType().FullName ?? "null"}.");
    }

    private static bool IsLocalHostReady(AmongUsClient? client, out string reason)
    {
        if (client is null || !client)
        {
            reason = "AmongUsClient missing";
            return false;
        }

        if (client.NetworkMode != NetworkModes.LocalGame)
        {
            reason = $"networkMode={client.NetworkMode}";
            return false;
        }

        if (!GameRuleSettings.IsDeepBotSupportedMap())
        {
            reason = $"mapId={GameRuleSettings.GetMapId()} ({GameRuleSettings.GetMapName()}; supported=The Skeld,MIRA HQ)";
            return false;
        }

        if (!client.AmHost || client.ClientId < 0 || client.HostId != client.ClientId)
        {
            reason = $"not local host: amHost={client.AmHost}, clientId={client.ClientId}, hostId={client.HostId}";
            return false;
        }

        if (!PlayerControl.LocalPlayer || PlayerControl.LocalPlayer.Data is null || !PlayerControl.LocalPlayer.MyPhysics || !PlayerControl.LocalPlayer.NetTransform)
        {
            reason = "local PlayerControl not ready";
            return false;
        }

        if (!GameData.Instance || !GameManager.Instance)
        {
            reason = "GameData/GameManager not ready";
            return false;
        }

        if (!LobbyBehaviour.Instance && client.GameState != InnerNetClient.GameStates.Started)
        {
            reason = $"lobby not ready and gameState={client.GameState}";
            return false;
        }

        reason = "ok";
        return true;
    }

    private static bool IsPassiveLanGuest(AmongUsClient? client)
    {
        return client is not null &&
            client &&
            client.NetworkMode == NetworkModes.LocalGame &&
            !client.AmHost &&
            client.ClientId >= 0;
    }

    private void CaptureHostPlayer(AmongUsClient client)
    {
        for (var i = 0; i < client.allClients.Count; i++)
        {
            var candidate = client.allClients[i];
            if (candidate is null ||
                candidate.Id != client.ClientId ||
                !candidate.Character ||
                candidate.Character.Data is null ||
                DeepBotIdentity.IsBot(candidate.Character))
            {
                continue;
            }

            _hostPlayer = candidate.Character;
            return;
        }

        var local = PlayerControl.LocalPlayer;
        if (local &&
            local.Data is not null &&
            !DeepBotIdentity.IsBot(local) &&
            local.OwnerId == client.ClientId)
        {
            _hostPlayer = local;
        }
    }

    private void EnsureHostLocalPlayer(AmongUsClient client, string reason)
    {
        var host = _hostPlayer;
        if (host is null ||
            !host ||
            host.Data is null ||
            host.OwnerId != client.ClientId ||
            DeepBotIdentity.IsBot(host))
        {
            CaptureHostPlayer(client);
            host = _hostPlayer;
        }

        if (host is null || !host || PlayerControl.LocalPlayer == host)
        {
            return;
        }

        var replaced = PlayerControl.LocalPlayer;
        PlayerControl.LocalPlayer = host;
        _log.LogWarning(
            $"DeepBot restored host LocalPlayer: reason={reason}, " +
            $"from={replaced?.Data?.PlayerName ?? "missing"}({replaced?.PlayerId.ToString() ?? "none"}), " +
            $"to={host.Data?.PlayerName ?? "host"}({host.PlayerId}), hostClientId={client.ClientId}.");
    }

    private void EnsureHostCameraTarget(AmongUsClient client, string reason)
    {
        var host = _hostPlayer;
        if (host is null || !host || host.Data is null || host.OwnerId != client.ClientId)
        {
            return;
        }

        if (!DestroyableSingleton<HudManager>.InstanceExists)
        {
            return;
        }

        var camera = DestroyableSingleton<HudManager>.Instance.PlayerCam;
        if (!camera)
        {
            return;
        }

        var replaced = camera.Target;
        var replacedPlayer = replaced ? replaced.TryCast<PlayerControl>() : null;
        var recoverMissingOrBotTarget =
            !replaced ||
            (replacedPlayer is not null &&
             replacedPlayer &&
             replacedPlayer.Data is not null &&
                DeepBotIdentity.IsBot(replacedPlayer));
        if (!recoverMissingOrBotTarget)
        {
            return;
        }

        camera.SetTarget(host);
        var unlockRecoveredCamera =
            client.GameState == InnerNetClient.GameStates.Started &&
            ShipStatus.Instance &&
            camera.Locked;
        if (unlockRecoveredCamera)
        {
            camera.Locked = false;
        }

        camera.SnapToTarget();
        _log.LogWarning(
            $"DeepBot restored host camera: reason={reason}, recoveredMissingOrBotTarget={recoverMissingOrBotTarget}, unlockedRecoveredCamera={unlockRecoveredCamera}, " +
            $"from={DescribeCameraTarget(replaced)}, to={host.Data.PlayerName}({host.PlayerId}).");
    }

    private static string DescribeCameraTarget(MonoBehaviour? target)
    {
        if (!target)
        {
            return "missing";
        }

        var activeTarget = target!;
        var player = activeTarget.TryCast<PlayerControl>();
        if (player is not null && player && player.Data is { } data)
        {
            return $"{data.PlayerName}({player.PlayerId})";
        }

        return activeTarget.GetType().Name;
    }

    private void EnsureHostVisionLight(AmongUsClient client, string reason)
    {
        if (client.GameState != InnerNetClient.GameStates.Started || !ShipStatus.Instance)
        {
            _disabledBotLightIds.Clear();
            _hostLightRepairLogged = false;
            return;
        }

        var host = _hostPlayer;
        if (host is null ||
            !host ||
            host.Data is null ||
            host.OwnerId != client.ClientId ||
            DeepBotIdentity.IsBot(host))
        {
            return;
        }

        var lightWasMissing = !host.lightSource;
        if (lightWasMissing)
        {
            host.AdjustLighting();
        }

        var hostLight = host.lightSource;
        if (hostLight)
        {
            var targetTransform = host.transform;
            var lightTransform = hostLight.transform;
            var parentChanged = lightTransform.parent != targetTransform;
            var wasInactive = !hostLight.gameObject.activeSelf || !hostLight.enabled;
            var expectedWorldPosition = targetTransform.TransformPoint(hostLight.LightOffset);
            var positionDrifted = Vector3.Distance(lightTransform.position, expectedWorldPosition) > 0.08f;

            if (parentChanged)
            {
                lightTransform.SetParent(targetTransform, false);
            }

            if (parentChanged || positionDrifted)
            {
                lightTransform.localPosition = hostLight.LightOffset;
            }

            if (wasInactive)
            {
                hostLight.gameObject.SetActive(true);
                hostLight.enabled = true;
            }

            // Do not call AdjustLighting every frame. Reinitializing the native
            // shadow mesh while crossing a doorway can leave a stale wide wedge.
            // Only rebuild a broken binding, then keep the radius aligned with
            // ShipStatus so room vision settings and a live lights sabotage win.
            if (parentChanged || wasInactive)
            {
                host.AdjustLighting();
                hostLight = host.lightSource;
                if (!hostLight)
                {
                    return;
                }
            }

            var expectedRadius = ShipStatus.Instance.CalculateLightRadius(host.Data);
            var actualRadius = hostLight ? hostLight.ViewDistance : float.NaN;
            var radiusRepaired = hostLight &&
                BotBehaviorPolicy.ShouldRepairVisionDistance(actualRadius, expectedRadius);
            if (radiusRepaired)
            {
                hostLight.SetViewDistance(expectedRadius);
            }

            if (!_hostLightRepairLogged &&
                (lightWasMissing || parentChanged || wasInactive || positionDrifted || radiusRepaired))
            {
                _hostLightRepairLogged = true;
                _log.LogWarning(
                    $"DeepBot restored host vision light: reason={reason}, player={host.Data.PlayerName}({host.PlayerId}), " +
                    $"missing={lightWasMissing}, parentChanged={parentChanged}, wasInactive={wasInactive}, " +
                    $"positionDrifted={positionDrifted}, radiusRepaired={radiusRepaired}, " +
                    $"radius={actualRadius:0.00}->{expectedRadius:0.00}, offset={hostLight.LightOffset}.");
            }
        }

        for (var i = 0; i < client.allClients.Count; i++)
        {
            var character = client.allClients[i]?.Character;
            if (character is null ||
                !character ||
                character == host ||
                character.Data is null ||
                !DeepBotIdentity.IsBot(character))
            {
                continue;
            }

            var botLight = character.lightSource;
            if (!botLight)
            {
                continue;
            }

            var wasActive = botLight.gameObject.activeSelf || botLight.enabled;
            botLight.enabled = false;
            botLight.gameObject.SetActive(false);

            if (wasActive && _disabledBotLightIds.Add(character.PlayerId))
            {
                _log.LogWarning(
                    $"DeepBot disabled bot vision light: player={character.Data.PlayerName}({character.PlayerId}), " +
                    $"host={host.Data.PlayerName}({host.PlayerId}).");
            }
        }
    }

    private void EnsureLivePlayersVisible(AmongUsClient client, bool botsOnly = false, bool allowLobby = false)
    {
        var matchStarted = client.GameState == InnerNetClient.GameStates.Started;
        if ((!matchStarted && !allowLobby) || (matchStarted && !ShipStatus.Instance))
        {
            _visibilityRestored.Clear();
            return;
        }

        for (var i = 0; i < client.allClients.Count; i++)
        {
            var character = client.allClients[i]?.Character;
            if (character is null ||
                !character ||
                character.Data is null ||
                character.Data.IsDead ||
                character.Data.Disconnected ||
                (botsOnly && !DeepBotIdentity.IsBot(character)))
            {
                continue;
            }

            var legitimatePhantomInvisibility =
                matchStarted &&
                character.Data.RoleType == RoleTypes.Phantom &&
                character.shouldAppearInvisible;
            var legitimateTorConcealment = matchStarted &&
                TorRoleAdapter.IsTorVisualConcealmentEffectActive(character);
            if (legitimatePhantomInvisibility || legitimateTorConcealment)
            {
                continue;
            }

            var cosmetics = character.cosmetics;
            var body = cosmetics ? cosmetics.currentBodySprite : null;
            var renderer = body?.BodySprite;
            var cosmeticsMissing = cosmetics is null || !cosmetics;
            var rendererMissing = renderer is null || !renderer;
            var phantomAlpha = 1f;
            if (!cosmeticsMissing)
            {
                try
                {
                    phantomAlpha = cosmetics!.GetPhantomRoleAlpha();
                }
                catch
                {
                    // Older game builds may not expose the phantom material state.
                }
            }

            if (_renderDiagnosticsLogged.Add(character.PlayerId))
            {
                var rendererState = rendererMissing
                    ? "missing"
                    : $"sprite={(renderer!.sprite ? renderer.sprite.name : "missing")},layer={renderer.gameObject.layer}," +
                      $"sorting={renderer.sortingLayerName}/{renderer.sortingOrder},pos={renderer.transform.position}," +
                      $"scale={renderer.transform.lossyScale},bounds={renderer.bounds.center}/{renderer.bounds.size}," +
                      $"active={renderer.gameObject.activeInHierarchy},enabled={renderer.enabled},alpha={renderer.color.a:0.00}";
                _log.LogWarning(
                    $"DeepBot live render diagnostic: player={character.Data.PlayerName}({character.PlayerId}), " +
                    $"role={character.Data.RoleType},owner={character.OwnerId},local={character == _hostPlayer}," +
                    $"rootPos={character.transform.position},truePos={character.GetTruePosition()},rootLayer={character.gameObject.layer}," +
                    $"scene={character.gameObject.scene.name},dummy={character.isDummy},notReal={character.notRealPlayer}," +
                    $"moveable={character.moveable},visible={character.Visible},invisibleFlag={character.shouldAppearInvisible}," +
                    $"bodyType={character.BodyType},cosmeticsVisible={(cosmeticsMissing ? "missing" : cosmetics!.Visible.ToString())}," +
                    $"cosmeticsBodyType={(cosmeticsMissing ? "missing" : cosmetics!.bodyType.ToString())},phantomAlpha={phantomAlpha:0.00}," +
                    $"normalBody={(cosmeticsMissing || cosmetics!.normalBodySprite is null ? "missing" : cosmetics.normalBodySprite.Visible.ToString())}," +
                    $"usingNormalBody={(cosmeticsMissing ? "missing" : (cosmetics!.currentBodySprite == cosmetics.normalBodySprite).ToString())}," +
                    $"bodyVisible={body?.Visible.ToString() ?? "missing"}," +
                    $"renderer=[{rendererState}].");
            }

            var needsRestore =
                !_visibilityRestored.Contains(character.PlayerId) ||
                !character.gameObject.activeInHierarchy ||
                !character.enabled ||
                !character.Visible ||
                character.shouldAppearInvisible ||
                (int)character.BodyType != 0 ||
                cosmeticsMissing ||
                !cosmetics!.gameObject.activeInHierarchy ||
                !cosmetics.enabled ||
                !cosmetics.Visible ||
                (int)cosmetics.bodyType != 0 ||
                phantomAlpha < 0.95f ||
                (cosmetics.normalBodySprite is not null && cosmetics.currentBodySprite != cosmetics.normalBodySprite) ||
                (cosmetics.normalBodySprite is not null && !cosmetics.normalBodySprite.Visible) ||
                body is null ||
                !body.Visible ||
                rendererMissing ||
                !renderer!.gameObject.activeInHierarchy ||
                !renderer.enabled ||
                renderer.color.a < 0.95f;
            if (!needsRestore)
            {
                continue;
            }

            var before =
                $"role={character.Data.RoleType},invisibleFlag={character.shouldAppearInvisible}," +
                $"playerActive={character.gameObject.activeInHierarchy},playerEnabled={character.enabled},playerVisible={character.Visible}," +
                $"cosmetics={(!cosmeticsMissing ? $"active={cosmetics!.gameObject.activeInHierarchy},enabled={cosmetics.enabled},visible={cosmetics.Visible}" : "missing")}," +
                $"body={(body is not null ? $"visible={body.Visible}" : "missing")}," +
                $"renderer={(!rendererMissing ? $"active={renderer!.gameObject.activeInHierarchy},enabled={renderer.enabled},alpha={renderer.color.a:0.00}" : "missing")}";

            RestoreAliveVisuals(character);

            _visibilityRestored.Add(character.PlayerId);
            _log.LogWarning(
                $"DeepBot restored live player rendering: player={character.Data.PlayerName}({character.PlayerId}), " +
                $"owner={character.OwnerId}, local={character == _hostPlayer}, before=[{before}].");
        }
    }

    private static void RestoreAliveVisuals(PlayerControl player)
    {
        player.gameObject.SetActive(true);
        player.enabled = true;
        player.Visible = true;
        player.SetInvisibility(false);
        player.SetRoleInvisibility(false, false, false);
        player.SetHatAndVisorAlpha(1f);

        var colorId = player.Data?.DefaultOutfit.ColorId ?? player.CurrentOutfit.ColorId;
        var cosmetics = player.cosmetics;
        if (cosmetics)
        {
            cosmetics.gameObject.SetActive(true);
            cosmetics.enabled = true;
            cosmetics.Visible = true;
            cosmetics.SetForcedVisible(true);
            cosmetics.SetBodyCosmeticsVisible(true);
            cosmetics.SetPhantomRoleAlpha(1f);
            cosmetics.SetColor(colorId);
            cosmetics.SetBodyColor(colorId);
            cosmetics.UpdateBodyMaterial();
            RestoreNormalBodySprite(cosmetics, colorId);
            cosmetics.UpdateVisibility();
        }

        if (player.MyPhysics)
        {
            player.MyPhysics.SetBodyType((PlayerBodyTypes)0);
            player.MyPhysics.ResetAnimState();
        }
    }

    private static void RestoreNormalBodySprite(CosmeticsLayer cosmetics, int colorId)
    {
        cosmetics.EnsureInitialized((PlayerBodyTypes)0);
        cosmetics.bodyType = (PlayerBodyTypes)0;
        cosmetics.alwaysDrawNormalPlayer = true;

        var normalBody = cosmetics.normalBodySprite;
        if (normalBody is null)
        {
            return;
        }

        cosmetics.currentBodySprite = normalBody;
        normalBody.Visible = true;
        var renderer = normalBody.BodySprite;
        if (renderer)
        {
            renderer.gameObject.SetActive(true);
            renderer.enabled = true;
            var color = renderer.color;
            color.a = 1f;
            renderer.color = color;
            PlayerMaterial.SetColors(colorId, renderer);
        }

        foreach (var candidate in cosmetics.bodySprites)
        {
            if (candidate is not null && candidate != normalBody)
            {
                candidate.Visible = false;
            }
        }

        cosmetics.UpdateBodyMaterial();
    }

    private int CountManagedClients(AmongUsClient client)
    {
        var managedIds = new HashSet<int>();
        for (var i = 0; i < client.allClients.Count; i++)
        {
            var candidate = client.allClients[i];
            if (candidate is not null &&
                candidate.Id != client.ClientId &&
                DeepBotIdentity.IsBot(candidate))
            {
                managedIds.Add(candidate.Id);
            }
        }

        foreach (var tracked in _tracked)
        {
            managedIds.Add(tracked.ClientId);
        }

        return managedIds.Count;
    }

    private static int CountNonManagedClients(AmongUsClient client)
    {
        var clientIds = new HashSet<int>();
        for (var index = 0; index < client.allClients.Count; index++)
        {
            var candidate = client.allClients[index];
            if (candidate is not null &&
                !DeepBotIdentity.IsBot(candidate))
            {
                clientIds.Add(candidate.Id);
            }
        }

        return clientIds.Count;
    }

    private int FindNextBotIndex(AmongUsClient client, int targetCount)
    {
        var occupied = new HashSet<int>();
        for (var i = 0; i < client.allClients.Count; i++)
        {
            var candidate = client.allClients[i];
            if (candidate is null || candidate.Id == client.ClientId)
            {
                continue;
            }

            if (DeepBotIdentity.TryGetBotIndex(candidate, out var index))
            {
                occupied.Add(index);
            }
        }

        foreach (var tracked in _tracked)
        {
            occupied.Add(tracked.Index);
        }

        for (var index = 0; index < targetCount; index++)
        {
            if (!occupied.Contains(index))
            {
                return index;
            }
        }

        return occupied.Count == 0 ? 0 : occupied.Max() + 1;
    }

    private static int FindAvailableClientId(AmongUsClient client)
    {
        for (var id = DeepBotIdentity.ReservedClientIdStart; id <= DeepBotIdentity.ReservedClientIdEnd; id++)
        {
            var used = false;
            for (var i = 0; i < client.allClients.Count; i++)
            {
                if (client.allClients[i]?.Id == id)
                {
                    used = true;
                    break;
                }
            }

            if (!used)
            {
                return id;
            }
        }

        return -1;
    }

    private static void AssignRuntimeOwnership(AmongUsClient client, PlayerControl player, int virtualClientId)
    {
        player.OwnerId = virtualClientId;
        player.NetTransform.OwnerId = client.ClientId;
        player.MyPhysics.OwnerId = client.ClientId;
    }

    private static Vector2 GetSpawnPoint(AmongUsClient client, int index, out string source)
    {
        var lobby = LobbyBehaviour.Instance;
        if (client.GameState != InnerNetClient.GameStates.Started &&
            lobby &&
            lobby.SpawnPositions is { Length: > 0 } lobbySpawns)
        {
            source = "native-lobby";
            return lobbySpawns[Mathf.Abs(index) % lobbySpawns.Length];
        }

        var spawnNodeIds = SkeldPathGraph.Instance.SpawnNodeIds;
        var nodeId = spawnNodeIds[Mathf.Abs(index) % spawnNodeIds.Count];
        source = $"map:{GameRuleSettings.GetMapName()}:{nodeId}";
        return SkeldPathGraph.Instance.FindNode(nodeId)?.Position ?? SkeldPathGraph.Instance.NearestNode(Vector2.zero).Position;
    }

    internal static void LogLobbySpawnSelfTest(ManualLogSource log)
    {
        var stabilizer = new LobbyBotCountStabilizer(LobbyBotCountStableSeconds);
        var initialEight = stabilizer.Observe(8, 0f, false, out _) == 8;
        var transientOneHeld = stabilizer.Observe(1, 0.5f, false, out _) == 8;
        var restoredEightHeld = stabilizer.Observe(8, 0.75f, false, out _) == 8;
        var deliberateOnePending = stabilizer.Observe(1, 2f, false, out _) == 8;
        var deliberateOneAccepted = stabilizer.Observe(1, 4.1f, false, out _) == 1;
        var startedMatchFrozen = stabilizer.Observe(8, 8f, true, out _) == 1;
        stabilizer.Reset();
        var resetAcceptsFreshValue = stabilizer.Observe(5, 9f, false, out _) == 5;
        var exactRosterRequired = IsExactLobbyRosterReady(5, 5, 5) &&
                                  !IsExactLobbyRosterReady(8, 8, 5) &&
                                  !IsExactLobbyRosterReady(5, 7, 5);
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Human" };
        var firstName = ResolveStableUniqueName("Iris", 0, occupied);
        occupied.Add(firstName);
        var duplicateName = ResolveStableUniqueName("Iris", 1, occupied);
        var repeatOccupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Human" };
        var repeatedFirstName = ResolveStableUniqueName("Iris", 0, repeatOccupied);
        repeatOccupied.Add(repeatedFirstName);
        var repeatedDuplicateName = ResolveStableUniqueName("Iris", 1, repeatOccupied);
        var stableAppearanceNames = firstName == "Iris" && duplicateName == "Iris 2" &&
                                    repeatedFirstName == firstName && repeatedDuplicateName == duplicateName;
        var level = initialEight && transientOneHeld && restoredEightHeld && deliberateOnePending &&
                    deliberateOneAccepted && startedMatchFrozen && resetAcceptsFreshValue && exactRosterRequired &&
                    stableAppearanceNames
            ? "ok"
            : "error";
        log.LogInfo(
            $"DeepBot lobby spawn self-test: level={level}, transientCountHeld={transientOneHeld && restoredEightHeld}, " +
            $"deliberateCountAccepted={deliberateOneAccepted}, startedMatchFrozen={startedMatchFrozen}, " +
            $"resetAcceptsFreshValue={resetAcceptsFreshValue}, exactRosterRequired={exactRosterRequired}, " +
            $"stableAppearanceNames={stableAppearanceNames}, nativeLobbySpawnRequired=true.");
    }

    private void ResetTransientState()
    {
        _tracked.Clear();
        _spawnBlocked = false;
        _startedSpawnBlockLogged = false;
        _nextSpawnAt = 0f;
        _hostPlayer = null;
        _visibilityRestored.Clear();
        _renderDiagnosticsLogged.Clear();
        _disabledBotLightIds.Clear();
        _passiveProxyClientIds.Clear();
        _appliedLobbyAppearances.Clear();
        _hostLightRepairLogged = false;
        _botCountStabilizer.Reset();
    }

    private sealed record TrackedBotClient(int ClientId, int Index, ClientData Client);

    private sealed class LobbyBotCountStabilizer
    {
        private readonly float _stableSeconds;
        private int? _stableValue;
        private int? _pendingValue;
        private float _pendingSince;

        internal LobbyBotCountStabilizer(float stableSeconds)
        {
            _stableSeconds = Mathf.Max(0f, stableSeconds);
        }

        internal int Observe(int candidate, float now, bool freeze, out string? transition)
        {
            transition = null;
            candidate = Mathf.Clamp(candidate, 1, 8);
            if (!_stableValue.HasValue)
            {
                _stableValue = candidate;
                _pendingValue = null;
                transition = $"initialized={candidate}";
                return candidate;
            }

            if (freeze)
            {
                _pendingValue = null;
                return _stableValue.Value;
            }

            if (candidate == _stableValue.Value)
            {
                _pendingValue = null;
                return _stableValue.Value;
            }

            if (_pendingValue != candidate)
            {
                _pendingValue = candidate;
                _pendingSince = now;
                transition = $"pending={candidate}, stable={_stableValue.Value}, hold={_stableSeconds:0.0}s";
                return _stableValue.Value;
            }

            if (now - _pendingSince < _stableSeconds)
            {
                return _stableValue.Value;
            }

            var previous = _stableValue.Value;
            _stableValue = candidate;
            _pendingValue = null;
            transition = $"accepted={candidate}, previous={previous}, stableFor={now - _pendingSince:0.0}s";
            return candidate;
        }

        internal void Reset()
        {
            _stableValue = null;
            _pendingValue = null;
            _pendingSince = 0f;
        }
    }
}

[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.BeginGame))]
internal static class DeepBotLobbyReadyStartGuard
{
    private static float _nextLogAt;

    [HarmonyPrefix]
    private static bool Prefix(GameStartManager __instance)
    {
        if (!Plugin.Settings.Enabled.Value || Plugin.Settings.DryRun.Value ||
            SafeLocalBotSpawner.AreConfiguredLobbyBotsReady(out var reason))
        {
            return true;
        }

        if (__instance)
        {
            var startText = AccessTools.Property(typeof(GameStartManager), "GameStartText")?.GetValue(__instance) ??
                            AccessTools.Field(typeof(GameStartManager), "GameStartText")?.GetValue(__instance);
            if (startText is not null)
            {
                AccessTools.Property(startText.GetType(), "text")?.SetValue(
                    startText,
                    $"AI 玩家仍在生成，请稍候\n{reason}");
                AccessTools.Property(startText.GetType(), "color")?.SetValue(startText, Color.yellow);
            }
        }
        if (Time.time >= _nextLogAt)
        {
            _nextLogAt = Time.time + 1.5f;
            Plugin.LogSource.LogWarning($"DeepBot blocked premature lobby start: {reason}.");
        }
        return false;
    }
}
