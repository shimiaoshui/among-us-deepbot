using BepInEx.Logging;

namespace AmongUsDeepSeekBots;

internal static class MeetingBehaviorDirectives
{
    private static readonly string[] TaskRushPhrases =
    [
        "快点做任务", "赶紧做任务", "抓紧做任务", "去做任务", "任务快做",
        "别摸鱼", "别偷懒", "加快任务", "finish tasks", "do tasks"
    ];

    private static readonly HashSet<byte> TargetedTaskRushBots = [];
    private static int _matchSerial = -1;
    private static bool _globalTaskRush;

    internal static void ResetForMatch(int matchSerial)
    {
        if (_matchSerial == matchSerial)
        {
            return;
        }

        _matchSerial = matchSerial;
        _globalTaskRush = false;
        TargetedTaskRushBots.Clear();
    }

    internal static bool TryRecordHumanDirective(
        string text,
        IEnumerable<PlayerControl> bots,
        int matchSerial,
        ManualLogSource log)
    {
        ResetForMatch(matchSerial);
        if (!TaskRushPhrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var mentioned = bots
            .Where(bot => bot && bot.Data is not null)
            .Where(bot => text.Contains(bot.Data.PlayerName, StringComparison.OrdinalIgnoreCase))
            .Select(bot => bot.PlayerId)
            .Distinct()
            .ToArray();
        if (mentioned.Length == 0)
        {
            _globalTaskRush = true;
        }
        else
        {
            foreach (var playerId in mentioned)
            {
                TargetedTaskRushBots.Add(playerId);
            }
        }

        log.LogInfo(
            $"DeepBot post-meeting task directive recorded: match={matchSerial}, " +
            $"scope={(mentioned.Length == 0 ? "all-bots" : string.Join(',', mentioned))}, source=human-chat.");
        return true;
    }

    internal static bool ShouldRushTasks(byte playerId, int matchSerial)
    {
        ResetForMatch(matchSerial);
        return _globalTaskRush || TargetedTaskRushBots.Contains(playerId);
    }
}
