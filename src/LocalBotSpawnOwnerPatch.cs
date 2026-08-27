using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using InnerNet;
using UnityEngine;

namespace AmongUsDeepSeekBots;

[HarmonyPatch]
internal static class LocalBotSpawnOwnerPatch
{
    private const int LocalBotClientIdStart = 64;
    private const int LocalBotClientIdEnd = 95;

    private static MethodBase? TargetMethod()
    {
        return AccessTools.Method(
            typeof(InnerNetClient),
            "CreateSpawnMessage",
            [typeof(InnerNetObject), typeof(int), typeof(SpawnFlags)]);
    }

    private static void Prefix(InnerNetObject netObjParent, ref int ownerId, ref SpawnFlags flags)
    {
        var client = AmongUsClient.Instance;
        if (!Plugin.Settings.Enabled.Value ||
            !client ||
            client.NetworkMode != NetworkModes.LocalGame ||
            !Plugin.AllowsWorldAuthority(client.AmHost))
        {
            return;
        }

        // During a native late-join snapshot, CreateSpawnMessage may be called
        // after SendInitialData has entered its body. Unknown reserved owner
        // ids make the guest wait forever in "Delay spawn for unowned". Send
        // bot controls as host-owned presentation objects and deliberately
        // clear IsClientCharacter so they cannot replace the host character.
        if (DeepBotLateJoinInitialDataPatch.IsPreparingVirtualBotSnapshot)
        {
            if (FindDeepBotPlayer(netObjParent) is not null ||
                ownerId is >= LocalBotClientIdStart and <= LocalBotClientIdEnd)
            {
                ownerId = client.ClientId;
                flags &= ~SpawnFlags.IsClientCharacter;
            }

            return;
        }

        if (ownerId is >= LocalBotClientIdStart and <= LocalBotClientIdEnd)
        {
            RewriteToHostOwner(netObjParent, ref ownerId, ref flags, "reserved DeepBot client id");
            return;
        }

        var player = FindDeepBotPlayer(netObjParent);
        if (player)
        {
            var matchedPlayer = player!;
            var playerName = matchedPlayer.Data?.PlayerName ?? "DeepBot PlayerControl";
            RewriteToHostOwner(netObjParent, ref ownerId, ref flags, playerName);
        }
    }

    private static void RewriteToHostOwner(
        InnerNetObject obj,
        ref int ownerId,
        ref SpawnFlags flags,
        string reason)
    {
        var client = AmongUsClient.Instance;
        if (!client)
        {
            return;
        }

        var previousOwner = ownerId;
        ownerId = client.ClientId;
        flags &= ~SpawnFlags.IsClientCharacter;
        Plugin.LogSource.LogInfo(
            $"DeepBot spawn owner rewrite: object={obj.GetType().Name}, reason={reason}, " +
            $"from={previousOwner}, toHost={client.ClientId}, isClientCharacter=false.");
    }

    private static PlayerControl? FindDeepBotPlayer(InnerNetObject obj)
    {
        if (!obj)
        {
            return null;
        }

        var player = ((Il2CppObjectBase)obj).TryCast<PlayerControl>();
        if (player is null || !player || player.Data is null)
        {
            return null;
        }

        return DeepBotIdentity.IsBot(player) ? player : null;
    }

}
