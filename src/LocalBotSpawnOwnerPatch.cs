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

    private static void Prefix(InnerNetObject netObjParent, ref int ownerId)
    {
        var client = AmongUsClient.Instance;
        if (!Plugin.Settings.Enabled.Value ||
            !client ||
            client.NetworkMode != NetworkModes.LocalGame ||
            !client.AmHost)
        {
            return;
        }

        if (ownerId is >= LocalBotClientIdStart and <= LocalBotClientIdEnd)
        {
            RewriteToHostOwner(netObjParent, ref ownerId, "reserved DeepBot client id");
            return;
        }

        var player = FindDeepBotPlayer(netObjParent);
        if (player)
        {
            var matchedPlayer = player!;
            var playerName = matchedPlayer.Data?.PlayerName ?? "DeepBot PlayerControl";
            RewriteToHostOwner(netObjParent, ref ownerId, playerName);
        }
    }

    private static void RewriteToHostOwner(InnerNetObject obj, ref int ownerId, string reason)
    {
        var client = AmongUsClient.Instance;
        if (!client || ownerId == client.ClientId)
        {
            return;
        }

        Plugin.LogSource.LogInfo($"DeepBot spawn owner rewrite: object={obj.GetType().Name}, reason={reason}, from={ownerId}, toHost={client.ClientId}.");
        ownerId = client.ClientId;
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

        return player.Data.PlayerName.StartsWith("DeepBot ", StringComparison.Ordinal) ? player : null;
    }
}
