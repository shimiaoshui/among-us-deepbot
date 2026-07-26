using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AmongUsDeepSeekBots;

/// <summary>
/// Keeps TOR's authoritative crew-only task count and bucket count, but prevents
/// ProgressTracker.FixedUpdate from visually jumping straight to the new value.
/// TOR currently applies its smoothing in a postfix after the original method has
/// already overwritten curValue, so the old displayed value must be captured in a
/// prefix and restored progressively in the final postfix.
/// </summary>
[HarmonyPatch(typeof(ProgressTracker), nameof(ProgressTracker.FixedUpdate))]
internal static class SmoothTaskProgressPatch
{
    private const float FillPerSecond = 0.55f;

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(ProgressTracker __instance, out float __state)
    {
        __state = __instance ? Mathf.Clamp01(__instance.curValue) : 0f;
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ProgressTracker __instance, float __state)
    {
        if (!__instance || !__instance.TileParent)
        {
            return;
        }

        var authoritativeTarget = Mathf.Clamp01(__instance.curValue);
        var displayed = NextDisplayedValue(
            Mathf.Clamp01(__state),
            authoritativeTarget,
            Time.fixedDeltaTime,
            FillPerSecond);
        __instance.curValue = displayed;

        var material = __instance.TileParent.material;
        var buckets = Mathf.Max(1f, material.GetFloat("_Buckets"));
        material.SetFloat("_Percent", displayed);
        material.SetFloat("_FullBuckets", displayed * buckets);
    }

    private static float NextDisplayedValue(float previous, float target, float deltaTime, float fillPerSecond)
    {
        previous = Mathf.Clamp01(previous);
        target = Mathf.Clamp01(target);
        if (target + 0.001f < previous)
        {
            // New round, task-scope recomputation, or a TOR role conversion.
            // A decrease must not leave stale progress rendered above truth.
            return target;
        }

        return Mathf.MoveTowards(previous, target, Mathf.Max(0f, deltaTime) * Mathf.Max(0f, fillPerSecond));
    }

    internal static void LogSelfTest(ManualLogSource log)
    {
        var risesGradually = Mathf.Approximately(NextDisplayedValue(0.2f, 0.8f, 0.02f, 0.55f), 0.211f);
        var neverOvershoots = Mathf.Approximately(NextDisplayedValue(0.79f, 0.8f, 1f, 0.55f), 0.8f);
        var decreaseSnapsToTruth = Mathf.Approximately(NextDisplayedValue(0.8f, 0.2f, 0.02f, 0.55f), 0.2f);
        log.LogInfo(
            $"DeepBot task progress rendering self-test: " +
            $"level={(risesGradually && neverOvershoots && decreaseSnapsToTruth ? "ok" : "error")}, " +
            $"risesGradually={risesGradually}, neverOvershoots={neverOvershoots}, " +
            $"decreaseSnapsToTruth={decreaseSnapsToTruth}.");
    }
}
