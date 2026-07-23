using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;

namespace AmongUsDeepSeekBots;

internal sealed class BotEvolutionDirector
{
    private readonly ManualLogSource _log;
    private readonly DeepSeekDecisionClient _deepSeek;
    private readonly BotMatchMemory _memory;
    private readonly BotEvolutionSkillStore _skills;
    private int _lastQueuedMatchSerial;
    private MatchEndSnapshot? _pendingSnapshot;

    internal BotEvolutionDirector(
        ManualLogSource log,
        DeepSeekDecisionClient deepSeek,
        BotMatchMemory memory,
        BotEvolutionSkillStore skills)
    {
        _log = log;
        _deepSeek = deepSeek;
        _memory = memory;
        _skills = skills;
    }

    internal void CaptureGameEnding()
    {
        var matchSerial = _memory.MatchSerial;
        if (matchSerial <= 0 || matchSerial <= _lastQueuedMatchSerial) return;

        var players = new List<FinalPlayerSnapshot>();
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (!player || player.Data is null || player.Data.Disconnected) continue;
            var hasTorRole = TorRoleAdapter.TryGetRole(player, out var torRole);
            var role = hasTorRole
                ? $"TOR:{torRole.Name}/{torRole.Alignment}"
                : $"native:{player.Data.Role?.NiceName}";
            players.Add(new FinalPlayerSnapshot(
                player.PlayerId,
                player.Data.PlayerName,
                role,
                !player.Data.IsDead));
        }

        var bots = new List<FinalBotSnapshot>();
        foreach (var bot in EnumerateDeepBots())
        {
            var hasTorRole = TorRoleAdapter.TryGetRole(bot, out var torRole);
            bots.Add(new FinalBotSnapshot(
                bot.PlayerId,
                bot.Data?.PlayerName ?? $"DeepBot {bot.PlayerId}",
                hasTorRole ? torRole.Name : IsImpostor(bot) ? "Impostor" : "Crewmate",
                _memory.BuildIdentity(bot),
                _memory.BuildRawTimeline(bot.PlayerId, Plugin.Settings.MaxMemoryEvents.Value)));
        }

        _pendingSnapshot = new MatchEndSnapshot(matchSerial, players, bots);
        _log.LogInfo($"DeepBot pre-reset end snapshot captured: match={matchSerial}, players={players.Count}, bots={bots.Count}.");
    }

    internal void OnGameEnded(EndGameResult endGameResult)
    {
        var matchSerial = _memory.MatchSerial;
        if (matchSerial <= 0 || matchSerial <= _lastQueuedMatchSerial) return;
        _lastQueuedMatchSerial = matchSerial;

        var winners = ReadWinnerNames();
        var snapshot = _pendingSnapshot is { MatchSerial: var serial } && serial == matchSerial
            ? _pendingSnapshot
            : BuildLiveSnapshot(matchSerial);
        _pendingSnapshot = null;
        var roster = BuildFinalRoster(snapshot.Players, winners);
        var reason = endGameResult is null
            ? EndGameResult.CachedGameOverReason.ToString()
            : endGameResult.GameOverReason.ToString();
        var existingSkills = _skills.BuildReflectionReference();
        var prompts = new List<(BotReflectionPrompt Prompt, string Role, bool Won)>();

        foreach (var bot in snapshot.Bots)
        {
            var won = winners.Contains(bot.Name);
            prompts.Add((
                new BotReflectionPrompt(
                    matchSerial,
                    bot.PlayerId,
                    bot.Name,
                    bot.Identity,
                    won ? "won" : "lost",
                    reason,
                    roster,
                    bot.Timeline,
                    existingSkills),
                bot.Role,
                won));
        }

        _log.LogInfo(
            $"DeepBot post-match reflection queued: match={matchSerial}, bots={prompts.Count}, " +
            $"reason={reason}, winners={string.Join(",", winners)}.");
        foreach (var item in prompts)
        {
            _ = ReflectOneAsync(item.Prompt, item.Role, item.Won);
        }
    }

    private async Task ReflectOneAsync(BotReflectionPrompt prompt, string role, bool won)
    {
        try
        {
            BotReflectionDecision? decision = null;
            var attempts = DeepSeekDecisionClient.LoadHostApiKey() is null ? 0 : 2;
            for (var attempt = 1; attempt <= attempts && decision is null; attempt++)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    decision = await _deepSeek.GetReflectionAsync(prompt, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        $"DeepBot post-match reflection request error: match={prompt.MatchSerial}, " +
                        $"bot={prompt.BotName}({prompt.BotId}), attempt={attempt}/{attempts}, " +
                        $"error={ex.GetBaseException().Message}");
                }
                if (decision is null && attempt < attempts)
                {
                    _log.LogWarning(
                        $"DeepBot post-match reflection retry: match={prompt.MatchSerial}, bot={prompt.BotName}({prompt.BotId}), attempt={attempt + 1}/{attempts}.");
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2)).ConfigureAwait(false);
                }
            }

            var usedFallback = decision is null;
            decision ??= BuildFallbackReflection(prompt, won);

            var lessons = decision.Lessons ?? [];
            var merge = _skills.Merge(lessons, prompt.MatchSerial, prompt.BotId, role, won);
            _log.LogInfo(
                $"DeepBot post-match reflection applied: match={prompt.MatchSerial}, bot={prompt.BotName}({prompt.BotId}), " +
                $"outcome={(won ? "win" : "loss")}, role={role}, summary={CleanSummary(decision.Summary)}, " +
                $"source={(usedFallback ? "local-fallback" : "agnes")}, newSkills={merge.Added}, " +
                $"reinforcedSkills={merge.Reinforced}, skillFile={merge.JsonPath}.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                $"DeepBot post-match reflection failed: match={prompt.MatchSerial}, bot={prompt.BotName}({prompt.BotId}), error={ex.GetBaseException().Message}");
        }
    }

    private static HashSet<string> ReadWinnerNames()
    {
        var winners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cached = EndGameResult.CachedWinners;
        if (cached is null) return winners;
        for (var index = 0; index < cached.Count; index++)
        {
            var winner = cached[index];
            if (winner is not null && !string.IsNullOrWhiteSpace(winner.PlayerName))
            {
                winners.Add(winner.PlayerName);
            }
        }
        return winners;
    }

    private MatchEndSnapshot BuildLiveSnapshot(int matchSerial)
    {
        var players = new List<FinalPlayerSnapshot>();
        var bots = new List<FinalBotSnapshot>();
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (!player || player.Data is null || player.Data.Disconnected) continue;
            var hasTorRole = TorRoleAdapter.TryGetRole(player, out var torRole);
            var role = hasTorRole
                ? $"TOR:{torRole.Name}/{torRole.Alignment}"
                : $"native:{player.Data.Role?.NiceName}";
            players.Add(new FinalPlayerSnapshot(player.PlayerId, player.Data.PlayerName, role, !player.Data.IsDead));
            if (DeepBotIdentity.IsBot(player))
            {
                bots.Add(new FinalBotSnapshot(
                    player.PlayerId,
                    player.Data.PlayerName,
                    hasTorRole ? torRole.Name : IsImpostor(player) ? "Impostor" : "Crewmate",
                    _memory.BuildIdentity(player),
                    _memory.BuildRawTimeline(player.PlayerId, Plugin.Settings.MaxMemoryEvents.Value)));
            }
        }
        return new MatchEndSnapshot(matchSerial, players, bots);
    }

    private static string BuildFinalRoster(
        IReadOnlyList<FinalPlayerSnapshot> players,
        HashSet<string> winners)
    {
        var lines = players.Select(player =>
            $"id={player.PlayerId};name={player.Name};role={player.Role};" +
            $"alive={player.Alive};winner={winners.Contains(player.Name)}").ToArray();
        return lines.Length == 0 ? "final roster unavailable" : string.Join("\n", lines);
    }

    private static BotReflectionDecision BuildFallbackReflection(BotReflectionPrompt prompt, bool won)
    {
        var lessons = new List<BotEvolutionLesson>();
        var timeline = prompt.PrivateTimeline ?? string.Empty;
        if (!won && timeline.Contains("voted skip", StringComparison.OrdinalIgnoreCase))
        {
            lessons.Add(new BotEvolutionLesson(
                "voting.avoid_reflexive_skip",
                "voting",
                "有可核验线索时不能把弃票当作默认安全选择。",
                "会议记忆中已有目击、矛盾或可信指认，但自己仍准备弃票时。",
                "比较证据强度与角色性格阈值，达到阈值就投给具体候选人。"));
        }
        if (!won &&
            prompt.GameOverReason.Contains("Sabotage", StringComparison.OrdinalIgnoreCase) &&
            timeline.Contains("chose not to respond", StringComparison.OrdinalIgnoreCase))
        {
            lessons.Add(new BotEvolutionLesson(
                "emergency.reconsider_before_timeout",
                "emergency",
                "拒绝响应紧急任务后必须随倒计时和可见响应人数持续复核。",
                "紧急任务仍未解除且自己看不到足够响应者时。",
                "立即中断普通目标并前往尚未占用的有效面板。"));
        }
        if (!won &&
            timeline.Contains("[murder]", StringComparison.OrdinalIgnoreCase) &&
            timeline.Contains("exposure=visible", StringComparison.OrdinalIgnoreCase))
        {
            lessons.Add(new BotEvolutionLesson(
                "murder.require_clean_exit",
                "murder",
                "有目击风险的击杀必须取消，安全击杀后必须立刻离场。",
                "目标附近出现可见玩家或撤离路线不明确时。",
                "放弃当前击杀；若已经击杀则沿无目击路线快速离开尸体。"));
        }

        var summary = won
            ? "本局获胜；接口不可用，已用本地可核验事件检查是否存在可复用的新经验。"
            : "本局失败；接口不可用，已用本地可核验事件提取保守且不泄露隐藏信息的经验。";
        return new BotReflectionDecision(summary, lessons.Take(3).ToArray());
    }

    private static string CleanSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "none";
        var clean = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length <= 180 ? clean : clean[..180];
    }

    private static IEnumerable<PlayerControl> EnumerateDeepBots()
    {
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (DeepBotIdentity.IsBot(player))
            {
                yield return player;
            }
        }
    }

    private static bool IsImpostor(PlayerControl player)
    {
        return player && player.Data?.Role is not null && player.Data.Role.IsImpostor;
    }

    private sealed record MatchEndSnapshot(
        int MatchSerial,
        IReadOnlyList<FinalPlayerSnapshot> Players,
        IReadOnlyList<FinalBotSnapshot> Bots);

    private sealed record FinalPlayerSnapshot(byte PlayerId, string Name, string Role, bool Alive);

    private sealed record FinalBotSnapshot(
        byte PlayerId,
        string Name,
        string Role,
        string Identity,
        string Timeline);
}
