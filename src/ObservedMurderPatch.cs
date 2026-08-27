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
        TorCachedLocalPlayerGuard.EnsureMatches(
            PlayerControl.LocalPlayer,
            Plugin.LogSource,
            "before murder presentation");
        __state = Plugin.Runtime?.CapturePotentialMurderWitnessMask(__instance, target) ?? 0;
        var local = PlayerControl.LocalPlayer;
        var cached = TorCachedLocalPlayerGuard.GetCachedControl();
        Plugin.LogSource.LogInfo(
            $"DeepBot murder local-identity preflight: killer={Describe(__instance)}, victim={Describe(target)}, " +
            $"victimAmOwner={target && target.AmOwner}, local={Describe(local)}, torCached={Describe(cached)}, " +
            $"localIsVictim={SamePlayer(local, target)}, cachedIsVictim={SamePlayer(cached, target)}.");
    }

    private static void Postfix(
        PlayerControl __instance,
        PlayerControl target,
        int __state)
    {
        TorCachedLocalPlayerGuard.EnsureMatches(
            PlayerControl.LocalPlayer,
            Plugin.LogSource,
            "after murder presentation");
        if (target && target.Data is not null && target.Data.IsDead)
        {
            Plugin.Runtime?.RecordObservedMurder(__instance, target, __state);
        }

        var local = PlayerControl.LocalPlayer;
        var cached = TorCachedLocalPlayerGuard.GetCachedControl();
        Plugin.LogSource.LogInfo(
            $"DeepBot murder local-identity result: victim={Describe(target)}, local={Describe(local)}, " +
            $"torCached={Describe(cached)}, localIsVictim={SamePlayer(local, target)}, " +
            $"cachedIsVictim={SamePlayer(cached, target)}.");
    }

    private static bool SamePlayer(PlayerControl? left, PlayerControl? right) =>
        left && right && left!.Pointer == right!.Pointer;

    private static string Describe(PlayerControl? player) =>
        player && player!.Data is not null
            ? $"{player.Data.PlayerName}({player.PlayerId},dead={player.Data.IsDead},owner={player.OwnerId})"
            : "missing";
}
