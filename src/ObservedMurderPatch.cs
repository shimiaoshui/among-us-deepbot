using HarmonyLib;

namespace AmongUsDeepSeekBots;

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
internal static class ObservedMurderPatch
{
    private static void Prefix(
        PlayerControl __instance,
        PlayerControl target,
        out int __state)
    {
        __state = Plugin.Runtime?.CapturePotentialMurderWitnessMask(__instance, target) ?? 0;
    }

    private static void Postfix(
        PlayerControl __instance,
        PlayerControl target,
        int __state)
    {
        if (target && target.Data is not null && target.Data.IsDead)
        {
            Plugin.Runtime?.RecordObservedMurder(__instance, target, __state);
        }
    }
}
