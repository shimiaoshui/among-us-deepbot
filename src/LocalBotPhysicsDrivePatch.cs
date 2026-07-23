using System;
using HarmonyLib;
using InnerNet;
using UnityEngine;

namespace AmongUsDeepSeekBots;

[HarmonyPatch(typeof(PlayerPhysics), "FixedUpdate")]
internal static class LocalBotPhysicsDrivePatch
{
    private static bool Prefix(PlayerPhysics __instance)
    {
        if (!Plugin.Settings.Enabled.Value || !IsHostAuthority())
        {
            return true;
        }

        var player = __instance.myPlayer;
        if (!DeepBotIdentity.IsBot(player))
        {
            return true;
        }

        // Host owns bot physics for LAN sync, but the vanilla FixedUpdate reads host input
        // for every host-owned PlayerPhysics. Reapply the director's desired velocity at
        // physics cadence before suppressing the local human-input path.
        Plugin.Runtime?.ApplyPhysicsMovement(__instance);
        return false;
    }

    private static bool IsHostAuthority()
    {
        var client = AmongUsClient.Instance;
        return client && client.NetworkMode == NetworkModes.LocalGame && client.AmHost;
    }
}
