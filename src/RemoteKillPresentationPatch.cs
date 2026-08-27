using HarmonyLib;

namespace AmongUsDeepSeekBots;

/// <summary>
/// Host-created bots execute authoritative kills on the host process. That
/// must not make an unrelated human observer receive the local killer/victim
/// cutscene. TOR's own no-animation path uses source=target; mirror that
/// presentation-only behavior while leaving the native murder result, corpse,
/// role rules and witness memory untouched.
/// </summary>
[HarmonyPatch(typeof(KillAnimation), nameof(KillAnimation.CoPerformKill))]
internal static class RemoteKillPresentationPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(ref PlayerControl source, ref PlayerControl target)
    {
        var local = PlayerControl.LocalPlayer;
        TorCachedLocalPlayerGuard.EnsureMatches(local, Plugin.LogSource, "kill animation presentation");
        if (!local || !source || !target || SamePlayer(local, source) || SamePlayer(local, target))
        {
            return;
        }

        var actualSource = source;
        source = target;
        Plugin.LogSource.LogInfo(
            $"DeepBot suppressed unrelated local kill cutscene: " +
            $"viewer={Describe(local)}, killer={Describe(actualSource)}, victim={Describe(target)}; " +
            "native death/corpse state preserved.");
    }

    private static bool SamePlayer(PlayerControl left, PlayerControl right) =>
        left.Pointer == right.Pointer;

    private static string Describe(PlayerControl player) =>
        player.Data is not null
            ? $"{player.Data.PlayerName}({player.PlayerId})"
            : $"playerId={player.PlayerId}";
}

/// <summary>
/// Final presentation boundary for the full-screen red kill overlay.  The
/// world murder/corpse simulation has already happened before this UI method;
/// skipping it for a viewer who is neither killer nor victim cannot change
/// game state and prevents host-owned bots from borrowing the host's screen.
/// </summary>
[HarmonyPatch(
    typeof(KillOverlay),
    nameof(KillOverlay.ShowKillAnimation),
    [typeof(NetworkedPlayerInfo), typeof(NetworkedPlayerInfo)])]
internal static class RemoteKillOverlayPresentationPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(NetworkedPlayerInfo killer, NetworkedPlayerInfo victim)
    {
        return ShouldShowForLocalViewer(killer, victim, "default overlay");
    }

    internal static bool ShouldShowForLocalViewer(
        NetworkedPlayerInfo? killer,
        NetworkedPlayerInfo? victim,
        string source)
    {
        var local = DeepBotIdentity.FindLocalHumanPlayer() ?? PlayerControl.LocalPlayer;
        if (!local || local.Data is null || killer is null || victim is null ||
            local.PlayerId == killer.PlayerId || local.PlayerId == victim.PlayerId)
        {
            return true;
        }

        Plugin.LogSource.LogInfo(
            $"DeepBot suppressed unrelated full-screen kill overlay: source={source}, " +
            $"viewer={local.Data.PlayerName}({local.PlayerId}), killer={killer.PlayerName}({killer.PlayerId}), " +
            $"victim={victim.PlayerName}({victim.PlayerId}); world death state preserved.");
        return false;
    }
}

[HarmonyPatch(
    typeof(KillOverlay),
    nameof(KillOverlay.ShowKillAnimation),
    [typeof(OverlayKillAnimation), typeof(NetworkedPlayerInfo), typeof(NetworkedPlayerInfo)])]
internal static class RemoteSpecificKillOverlayPresentationPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(NetworkedPlayerInfo killer, NetworkedPlayerInfo victim)
    {
        return RemoteKillOverlayPresentationPatch.ShouldShowForLocalViewer(killer, victim, "specific overlay");
    }
}
