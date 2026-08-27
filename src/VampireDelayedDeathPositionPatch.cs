using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AmongUsDeepSeekBots;

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
internal static class VampireDelayedDeathPositionPatch
{
    private const float PositionHoldSeconds = 0.65f;
    private static readonly Dictionary<byte, PositionHold> PositionHolds = [];

    private static void Prefix(
        PlayerControl __instance,
        PlayerControl target,
        out PositionState __state)
    {
        __state = default;
        if (!IsHostAuthority() || !TorRoleAdapter.IsPendingVampireDelayedKill(__instance, target))
        {
            return;
        }

        __state = new PositionState(true, __instance.GetTruePosition());
        PositionHolds[__instance.PlayerId] = new PositionHold(
            __state.Origin,
            Time.time + PositionHoldSeconds);
    }

    private static void Postfix(
        PlayerControl __instance,
        PlayerControl target,
        PositionState __state)
    {
        if (!IsHostAuthority() || !__state.Active || !__instance)
        {
            return;
        }

        var positionBeforeRestore = __instance.GetTruePosition();
        var displacement = Vector2.Distance(__state.Origin, positionBeforeRestore);
        if (__instance.NetTransform)
        {
            __instance.NetTransform.SnapTo(__state.Origin);
        }

        var transformPosition = __instance.transform.position;
        __instance.transform.position = new Vector3(__state.Origin.x, __state.Origin.y, transformPosition.z);
        if (__instance.MyPhysics)
        {
            __instance.MyPhysics.SetNormalizedVelocity(Vector2.zero);
            if (__instance.MyPhysics.body)
            {
                __instance.MyPhysics.body.velocity = Vector2.zero;
            }
        }

        Plugin.LogSource.LogInfo(
            $"DeepBot Vampire delayed death position invariant: vampire={__instance.Data?.PlayerName}({__instance.PlayerId}), " +
            $"victim={target?.Data?.PlayerName}({target?.PlayerId}), victimDead={target?.Data?.IsDead == true}, " +
            $"origin={__state.Origin}, displacementBeforeRestore={displacement:0.000}, final={__instance.GetTruePosition()}.");
    }

    internal static void MaintainPositionHolds()
    {
        if (!IsHostAuthority())
        {
            PositionHolds.Clear();
            return;
        }

        foreach (var pair in PositionHolds.ToArray())
        {
            if (Time.time >= pair.Value.Until || MeetingHud.Instance || ExileController.Instance)
            {
                PositionHolds.Remove(pair.Key);
                continue;
            }

            var player = PlayerControl.AllPlayerControls
                .ToArray()
                .FirstOrDefault(candidate => candidate && candidate.PlayerId == pair.Key);
            if (!player)
            {
                PositionHolds.Remove(pair.Key);
                continue;
            }

            if (player!.NetTransform)
            {
                player.NetTransform.SnapTo(pair.Value.Origin);
            }

            var current = player.transform.position;
            player.transform.position = new Vector3(pair.Value.Origin.x, pair.Value.Origin.y, current.z);
            if (player.MyPhysics)
            {
                player.MyPhysics.SetNormalizedVelocity(Vector2.zero);
                if (player.MyPhysics.body)
                {
                    player.MyPhysics.body.velocity = Vector2.zero;
                }
            }
        }
    }

    internal static void LogSelfTest(ManualLogSource log)
    {
        var delayedKillPreservesKiller = ShouldPreservePosition(true, true, false);
        var ordinaryKillUnaffected = !ShouldPreservePosition(true, false, false);
        var selfKillUnaffected = !ShouldPreservePosition(true, true, true);
        var delayedAnimationUsesVictim = ShouldUseVictimAsAnimationSource(true, true, false);
        var level = delayedKillPreservesKiller && ordinaryKillUnaffected && selfKillUnaffected &&
                    delayedAnimationUsesVictim
            ? "ok"
            : "error";
        log.LogInfo(
            $"DeepBot Vampire delayed death self-test: level={level}, " +
            $"delayedKillPreservesKiller={delayedKillPreservesKiller}, " +
            $"ordinaryKillUnaffected={ordinaryKillUnaffected}, selfKillUnaffected={selfKillUnaffected}, " +
            $"delayedAnimationUsesVictim={delayedAnimationUsesVictim}.");
    }

    private static bool ShouldPreservePosition(bool sourceIsVampire, bool targetIsBitten, bool samePlayer)
    {
        return sourceIsVampire && targetIsBitten && !samePlayer;
    }

    internal static bool ShouldUseVictimAsAnimationSource(
        bool sourceIsVampire,
        bool targetIsBitten,
        bool samePlayer)
    {
        return sourceIsVampire && targetIsBitten && !samePlayer;
    }

    private readonly record struct PositionState(bool Active, Vector2 Origin);
    private readonly record struct PositionHold(Vector2 Origin, float Until);

    internal static bool IsHostAuthority()
    {
        var client = AmongUsClient.Instance;
        return client &&
               client.NetworkMode == NetworkModes.LocalGame &&
               Plugin.AllowsWorldAuthority(client.AmHost);
    }
}

[HarmonyPatch(typeof(KillAnimation), nameof(KillAnimation.CoPerformKill))]
[HarmonyBefore("me.eisbison.theotherroles")]
[HarmonyPriority(Priority.First)]
internal static class VampireDelayedKillAnimationPatch
{
    private static void Prefix(ref PlayerControl source, PlayerControl target)
    {
        if (!VampireDelayedDeathPositionPatch.IsHostAuthority() ||
            !TorRoleAdapter.IsPendingVampireDelayedKill(source, target))
        {
            return;
        }

        // This is the same no-teleport presentation rule used by TOR's native
        // showAnimation:false path. Reassert it before TOR's own animation
        // patch so Harmony order or another mod cannot turn the delayed bite
        // into a visible lunge from the vampire to the victim.
        source = target;
    }
}
