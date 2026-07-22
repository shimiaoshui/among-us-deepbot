using HarmonyLib;

namespace AmongUsDeepSeekBots;

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
internal static class BotEvolutionOnGameEndPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix()
    {
        // TOR clears its static role holders in its OnGameEnd postfix. Capture
        // each bot's true final role and private memory before that reset.
        Plugin.Runtime?.CaptureGameEnding();
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix([HarmonyArgument(0)] ref EndGameResult endGameResult)
    {
        Plugin.Runtime?.OnGameEnded(endGameResult);
    }
}
