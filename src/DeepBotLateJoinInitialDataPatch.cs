using System;
using System.Collections.Generic;
using AmongUs.InnerNet.GameDataMessages;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using InnerNet;

namespace AmongUsDeepSeekBots;

/// <summary>
/// Keeps host-created virtual bot characters in the vanilla late-join snapshot.
///
/// DeepBot uses reserved client ids for bot identity, while the host owns only
/// the bot movement components.  The ordinary live-spawn path temporarily
/// rewrites a bot spawn to the host transport id.  Reusing that owner id in a
/// late-join snapshot makes every bot look like another character belonging to
/// the host.  Among Us then repeatedly rebinds the host ClientData.Character;
/// guests lose the host's cosmetics and the bot roster is not reconstructed.
///
/// During SendInitialData every bot PlayerControl is sent as a host-owned
/// non-client-character object. The reserved id remains in GameData and is
/// reconstructed as a presentation-only ClientData proxy by the guest plugin.
/// This avoids both failure modes: an unknown virtual owner stalls the native
/// spawn queue, while IsClientCharacter would rebind the host's own character.
/// </summary>
[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.SendInitialData))]
internal static class DeepBotLateJoinInitialDataPatch
{
    [ThreadStatic]
    private static int _virtualOwnerScopeDepth;

    internal static bool IsPreparingVirtualBotSnapshot => _virtualOwnerScopeDepth > 0;

    private static void Prefix(InnerNetClient __instance, int clientId)
    {
        if (!ShouldPrepare(__instance, clientId))
        {
            return;
        }

        _virtualOwnerScopeDepth++;
        try
        {
            // Send the presentation proxy roster before native initial data.
            // The guest consumes it without creating GameData entries, so the
            // authoritative snapshot can bind each bot exactly once.
            DeepBotGuestRosterSync.SendRosterTo(clientId, "pre-initial-data");
            PrepareSnapshot(__instance, clientId);
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogError(
                $"DeepBot late-join snapshot preparation failed for client={clientId}: {ex}");
        }
    }

    private static void Postfix(InnerNetClient __instance, int clientId)
    {
        if (ShouldPrepare(__instance, clientId))
        {
            DeepBotGuestRosterSync.SendRosterTo(clientId, "post-initial-data");
        }
    }

    private static Exception? Finalizer(Exception? __exception)
    {
        if (_virtualOwnerScopeDepth > 0)
        {
            _virtualOwnerScopeDepth--;
        }

        return __exception;
    }

    private static bool ShouldPrepare(InnerNetClient? client, int targetClientId)
    {
        return Plugin.Settings.Enabled.Value &&
               client is not null &&
               client &&
               Plugin.AllowsWorldAuthority(client.AmHost) &&
               client.NetworkMode == NetworkModes.LocalGame &&
               targetClientId >= 0 &&
               targetClientId != client.ClientId &&
               !DeepBotIdentity.IsReservedClientId(targetClientId);
    }

    private static void PrepareSnapshot(InnerNetClient client, int targetClientId)
    {
        var bots = new List<PlayerControl>();
        for (var index = 0; index < client.allClients.Count; index++)
        {
            var candidate = client.allClients[index];
            if (candidate is null ||
                !DeepBotIdentity.IsReservedClientId(candidate.Id) ||
                !candidate.Character ||
                candidate.Character.Data is null)
            {
                continue;
            }

            bots.Add(candidate.Character);
        }

        var cached = client.sendInitialDataSpawnGameDataMessages;
        if (cached is null)
        {
            Plugin.LogSource.LogWarning(
                $"DeepBot late-join snapshot has no native spawn cache: target={targetClientId}, bots={bots.Count}.");
            return;
        }

        var cachedPlayerIds = new HashSet<byte>();
        var repairedOwners = 0;
        for (var index = 0; index < cached.Count; index++)
        {
            var message = cached[index];
            var bot = FindBotCharacter(message);
            if (bot is null || !bot || bot.Data is null)
            {
                continue;
            }

            cachedPlayerIds.Add(bot.PlayerId);
            if (!DeepBotIdentity.IsReservedClientId(bot.Data.ClientId))
            {
                continue;
            }

            if (message.ownerId != client.ClientId ||
                (message.flags & SpawnFlags.IsClientCharacter) != 0)
            {
                message.ownerId = client.ClientId;
                message.flags &= ~SpawnFlags.IsClientCharacter;
                repairedOwners++;
            }
        }

        var added = 0;
        foreach (var bot in bots)
        {
            if (cachedPlayerIds.Contains(bot.PlayerId) || bot.Data is null)
            {
                continue;
            }

            var message = client.CreateSpawnMessage(
                bot,
                client.ClientId,
                (SpawnFlags)0);
            if (message is null)
            {
                continue;
            }

            // Be explicit even if another Harmony prefix or the native method
            // normalizes the owner while the message is being built.
            message.ownerId = client.ClientId;
            message.flags &= ~SpawnFlags.IsClientCharacter;
            cached.Add(message);
            cachedPlayerIds.Add(bot.PlayerId);
            added++;
        }

        Plugin.LogSource.LogInfo(
            $"DeepBot late-join snapshot prepared: target={targetClientId}, bots={bots.Count}, " +
            $"cachedBotSpawns={cachedPlayerIds.Count}, hostOwnedPresentationSpawns={repairedOwners}, " +
            $"added={added}, totalSpawnMessages={cached.Count}.");
    }

    private static PlayerControl? FindBotCharacter(SpawnGameDataMessage? message)
    {
        if (message?.childNetObjects is null)
        {
            return FindBotFromSpawnIdentity(message);
        }

        foreach (var child in message.childNetObjects)
        {
            if (child is null || !child)
            {
                continue;
            }

            var player = ((Il2CppObjectBase)child).TryCast<PlayerControl>();
            if (player is not null && player && DeepBotIdentity.IsBot(player))
            {
                return player;
            }
        }

        return FindBotFromSpawnIdentity(message);
    }

    private static PlayerControl? FindBotFromSpawnIdentity(SpawnGameDataMessage? message)
    {
        if (message is null)
        {
            return null;
        }

        // On this game build the native spawn cache may retain the parent
        // PlayerControl separately from childNetObjects.  spawnTypeId is the
        // stable parent spawn identity; accept both SpawnId and NetId because
        // TOR/Reactor revisions have used each representation in their
        // generated interop wrappers.
        var spawnIdentity = message.spawnTypeId;
        foreach (var candidate in PlayerControl.AllPlayerControls)
        {
            if (!candidate || candidate.Data is null || !DeepBotIdentity.IsBot(candidate))
            {
                continue;
            }

            if (candidate.SpawnId == spawnIdentity || candidate.NetId == spawnIdentity)
            {
                return candidate;
            }
        }

        return null;
    }
}
