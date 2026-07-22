using HarmonyLib;

namespace AmongUsDeepSeekBots;

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
internal static class ObservedMurderPatch
{
    private static void Prefix(PlayerControl __instance, PlayerControl target)
    {
        Plugin.Runtime?.RecordObservedMurder(__instance, target);
    }
}
