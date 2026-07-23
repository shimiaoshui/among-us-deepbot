using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BepInEx.Logging;
using InnerNet;
using UnityEngine;

namespace AmongUsDeepSeekBots;

internal sealed class BotSocialDirector
{
    private const float SocialUpdateInterval = 0.25f;
    private const float VoteQuietSeconds = 5.5f;
    private const int MaxMeetingMessagesPerBot = 7;
    private const int MaxMeetingDecisionRoundsPerBot = 6;
    private const byte SkipVoteId = 253;

    private static readonly string[] SuspicionWords =
    [
        "可疑", "怀疑", "觉得", "认为", "指认", "凶手", "内鬼", "击杀", "杀", "刀",
        "投", "票", "就是", "认狼", "自爆", "sus", "suspicious", "impostor", "kill"
    ];

    private static readonly string[] SelfIncriminatingWords =
    [
        "我要把你们全杀", "我要杀你们", "我会杀你们", "把你们全杀", "杀掉你们",
        "我是内鬼", "我就是内鬼", "我认狼", "认狼", "自爆"
    ];

    private readonly ManualLogSource _log;
    private readonly DeepSeekDecisionClient _deepSeek;
    private readonly BotMatchMemory _memory;
    private readonly Dictionary<byte, SocialState> _states = [];
    private readonly Dictionary<byte, int> _accusations = [];
    private readonly Dictionary<byte, float> _bodyClaimsUntil = [];
    private readonly List<TranscriptEntry> _transcript = [];
    private float _nextUpdateAt;
    private bool _meetingActive;
    private bool _injectingChat;
    private int _meetingSerial;
    private int _transcriptVersion;
    private int _humanTranscriptVersion;
    private float _lastTranscriptAt;
    private byte? _lastReporterId;
    private string? _lastReportNode;

    public BotSocialDirector(ManualLogSource log, DeepSeekDecisionClient deepSeek, BotMatchMemory memory)
    {
        _log = log;
        _deepSeek = deepSeek;
        _memory = memory;
    }

    public void Update(PluginConfig config)
    {
        if (!ShouldRun(config) || Time.time < _nextUpdateAt)
        {
            return;
        }

        _nextUpdateAt = Time.time + SocialUpdateInterval;
        if (MeetingHud.Instance && !_meetingActive)
        {
            OnMeetingStarted();
        }
        else if (!MeetingHud.Instance && _meetingActive)
        {
            OnMeetingEnded();
        }

        if (_meetingActive)
        {
            TickMeeting(config);
        }
        else if (config.AutoReportBodies.Value)
        {
            TickNearbyBodyReports();
        }
    }

    public void Tick(PluginConfig config)
    {
        if (!ShouldRun(config))
        {
            return;
        }

        var activeIds = EnumerateDeepBots().Select(bot => bot.PlayerId).ToHashSet();
        foreach (var stale in _states.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            _states.Remove(stale);
        }
    }

    public void OnMeetingStarted()
    {
        if (_meetingActive || !IsHostAuthority())
        {
            return;
        }

        _meetingActive = true;
        _meetingSerial++;
        _transcript.Clear();
        _accusations.Clear();
        _transcriptVersion = 0;
        _humanTranscriptVersion = 0;
        _lastTranscriptAt = Time.time;

        var alive = 0;
        foreach (var bot in EnumerateDeepBots())
        {
            var state = GetState(bot);
            state.MeetingSerial = _meetingSerial;
            state.Spoken = false;
            state.MessagesSent = 0;
            state.DecisionRounds = 0;
            state.Voted = MeetingHud.Instance && MeetingHud.Instance.DidVote(bot.PlayerId);
            state.SpeakAt = Time.time + GetOpeningSpeakDelay(bot.PlayerId);
            ConfigureMeetingVoteTimes(bot, state);
            state.PendingBodyId = null;
            state.ReportAt = 0f;
            state.DecisionInFlight = false;
            state.DecisionCompleted = false;
            state.DecisionApplied = false;
            state.PendingMeetingDecision = null;
            state.MeetingDecision = null;
            state.LastAnalyzedTranscriptVersion = -1;
            state.PendingTranscriptVersion = -1;
            state.LastAnalyzedHumanTranscriptVersion = 0;
            state.PendingHumanTranscriptVersion = -1;
            state.LastHumanReactionVersion = 0;
            state.PendingHumanReactionVersion = -1;
            state.PendingHumanText = string.Empty;
            state.PendingHumanSourceId = byte.MaxValue;
            state.HumanReactionAt = 0f;
            state.RequestGeneration = 0;
            state.PendingGeneration = 0;
            state.LastMessage = string.Empty;
            state.HumanReconsiderRequested = false;
            state.LastSubmittedVoteId = null;
            state.BeliefScores.Clear();

            if (IsAlive(bot))
            {
                alive++;
                _memory.RecordAction(bot, "meeting", $"meeting {_meetingSerial} started");
            }

            Stop(bot);
        }

        _log.LogInfo(
            $"DeepBot meeting state started: serial={_meetingSerial}, aliveBots={alive}, " +
            $"model={Plugin.Settings.Model.Value}, deepSeek={Plugin.Settings.MeetingUseDeepSeek.Value}, nativeRpc=true.");
    }

    public void OnMeetingEnded()
    {
        if (!_meetingActive)
        {
            return;
        }

        _meetingActive = false;
        foreach (var bot in EnumerateDeepBots())
        {
            if (!_states.TryGetValue(bot.PlayerId, out var state))
            {
                continue;
            }

            var decision = state.MeetingDecision;
            var followPlayerId = ValidateFollowPlayerId(bot, decision?.FollowPlayerId);
            var followIntent = NormalizeFollowIntent(decision?.FollowIntent);
            if (!followPlayerId.HasValue)
            {
                followIntent = "none";
            }

            _memory.RecordMeetingConclusion(
                bot,
                _meetingSerial,
                state.LastSubmittedVoteId,
                followPlayerId,
                followIntent,
                decision?.Confidence ?? 0f,
                decision?.Reason ?? "meeting ended without model conclusion");
        }

        _transcript.Clear();
        _accusations.Clear();
        _transcriptVersion = 0;
        _humanTranscriptVersion = 0;
        foreach (var state in _states.Values)
        {
            state.Spoken = false;
            state.MessagesSent = 0;
            state.DecisionRounds = 0;
            state.Voted = false;
            state.PendingBodyId = null;
            state.ReportAt = 0f;
            state.DecisionInFlight = false;
            state.DecisionCompleted = false;
            state.DecisionApplied = false;
            state.PendingMeetingDecision = null;
            state.MeetingDecision = null;
            state.LastAnalyzedTranscriptVersion = -1;
            state.PendingTranscriptVersion = -1;
            state.LastAnalyzedHumanTranscriptVersion = 0;
            state.PendingHumanTranscriptVersion = -1;
            state.LastHumanReactionVersion = 0;
            state.PendingHumanReactionVersion = -1;
            state.PendingHumanText = string.Empty;
            state.PendingHumanSourceId = byte.MaxValue;
            state.HumanReactionAt = 0f;
            state.RequestGeneration++;
            state.PendingGeneration = 0;
            state.LastMessage = string.Empty;
            state.HumanReconsiderRequested = false;
            state.BeliefScores.Clear();
        }

        _log.LogInfo($"DeepBot meeting state ended: serial={_meetingSerial}.");
    }

    public void OnChat(PlayerControl source, string text)
    {
        if (_injectingChat || !_meetingActive || !IsHostAuthority() || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var clean = text.Trim();
        var sourceId = source ? source.PlayerId : byte.MaxValue;
        var sourceName = source && source.Data is not null ? source.Data.PlayerName : "Unknown";
        var isBotSource = source && DeepBotIdentity.IsBot(source);
        _transcript.Add(new TranscriptEntry(sourceId, sourceName, clean));
        TrimTranscript();
        _transcriptVersion++;
        if (!isBotSource)
        {
            _humanTranscriptVersion++;
        }
        _lastTranscriptAt = Time.time;
        _log.LogInfo(
            $"DeepBot meeting transcript received: meeting={_meetingSerial}, source={sourceName}({sourceId}), " +
            $"human={!isBotSource}, transcriptVersion={_transcriptVersion}, humanVersion={_humanTranscriptVersion}, text={clean}");

        var supportiveClaim = IsSupportiveClaim(clean);
        var hasSuspicionLanguage = !supportiveClaim &&
            SuspicionWords.Any(word => clean.Contains(word, StringComparison.OrdinalIgnoreCase));
        var selfIncriminating = SelfIncriminatingWords.Any(word => clean.Contains(word, StringComparison.OrdinalIgnoreCase));
        var mentionedPlayers = EnumeratePlayers()
            .Where(player => MentionsPlayer(clean, player))
            .ToArray();
        var mentionedPlayer = mentionedPlayers.FirstOrDefault();
        var explicitAccusation = !supportiveClaim && mentionedPlayers.Length > 0 && IsExplicitAccusation(clean);
        foreach (var player in EnumerateLivingPlayers())
        {
            if ((hasSuspicionLanguage || explicitAccusation) &&
                MentionsPlayer(clean, player))
            {
                _accusations[player.PlayerId] = _accusations.GetValueOrDefault(player.PlayerId) + 1;
            }
        }

        var fastResponderIds = new HashSet<byte>();
        if (!isBotSource)
        {
            var livingBots = EnumerateDeepBots().Where(IsAlive).ToList();
            foreach (var addressedBot in livingBots.Where(bot =>
                         MentionsPlayer(clean, bot)))
            {
                fastResponderIds.Add(addressedBot.PlayerId);
            }

            var desiredResponses = supportiveClaim
                ? 1
                : mentionedPlayer is not null || hasSuspicionLanguage || clean.Contains('?') || clean.Contains('？')
                ? 2
                : 1;
            foreach (var responder in livingBots
                         .OrderBy(bot => BotBehaviorPolicy.MeetingResponderOrder(bot.PlayerId, _humanTranscriptVersion)))
            {
                if (fastResponderIds.Count >= Math.Min(desiredResponses, livingBots.Count))
                {
                    break;
                }

                fastResponderIds.Add(responder.PlayerId);
            }
        }

        foreach (var bot in EnumerateDeepBots())
        {
            var state = GetState(bot);
            if (sourceId != bot.PlayerId)
            {
                var personality = BotPersonalityCatalog.ForPlayer(bot.PlayerId);
                foreach (var player in mentionedPlayers.Where(IsAlive))
                {
                    if (supportiveClaim && player.PlayerId != bot.PlayerId)
                    {
                        var personallyWitnessedKill = _memory.TryGetLatestWitnessedKiller(bot.PlayerId, out var killerId) &&
                            killerId == player.PlayerId;
                        if (!personallyWitnessedKill)
                        {
                            var trustInfluence = 0.5f * Mathf.Lerp(
                                0.35f,
                                1.0f,
                                personality.SocialSuggestibility);
                            state.BeliefScores[player.PlayerId] =
                                state.BeliefScores.GetValueOrDefault(player.PlayerId) - trustInfluence;
                        }
                        continue;
                    }

                    if ((!hasSuspicionLanguage && !explicitAccusation) ||
                        player.PlayerId == bot.PlayerId)
                    {
                        continue;
                    }

                    var claimStrength = explicitAccusation ? 0.9f : 0.42f;
                    if (isBotSource)
                    {
                        claimStrength *= 0.78f;
                    }

                    var influence = claimStrength * Mathf.Lerp(
                        0.55f,
                        1.15f,
                        personality.SocialSuggestibility);
                    state.BeliefScores[player.PlayerId] =
                        state.BeliefScores.GetValueOrDefault(player.PlayerId) + influence;
                }

                if (selfIncriminating &&
                    source &&
                    IsAlive(source) &&
                    source.PlayerId != bot.PlayerId)
                {
                    var influence = 0.68f * Mathf.Lerp(
                        0.55f,
                        1.15f,
                        personality.SocialSuggestibility);
                    state.BeliefScores[source.PlayerId] =
                        state.BeliefScores.GetValueOrDefault(source.PlayerId) + influence;
                }
            }

            if (state.Voted || state.MessagesSent >= MaxMeetingMessagesPerBot)
            {
                if (!isBotSource && !state.Voted && state.DecisionRounds < MaxMeetingDecisionRoundsPerBot)
                {
                    state.HumanReconsiderRequested = true;
                }
                else
                {
                    continue;
                }
            }

            if (!isBotSource && state.DecisionRounds < MaxMeetingDecisionRoundsPerBot)
            {
                state.HumanReconsiderRequested = true;
            }

            var directlyAddressed =
                MentionsPlayer(clean, bot);
            if (!isBotSource && fastResponderIds.Contains(bot.PlayerId))
            {
                state.PendingHumanReactionVersion = _humanTranscriptVersion;
                state.PendingHumanText = clean;
                state.PendingHumanSourceId = sourceId;
                state.HumanReactionAt = Time.time + (directlyAddressed
                    ? UnityEngine.Random.Range(0.55f, 1.15f)
                    : UnityEngine.Random.Range(0.9f, 2.1f));
            }

            var delay = directlyAddressed
                ? UnityEngine.Random.Range(1.0f, 2.3f)
                : UnityEngine.Random.Range(2.0f, 4.2f);
            state.SpeakAt = Math.Min(state.SpeakAt, Time.time + delay);
            state.VoteAt = Math.Max(state.VoteAt, Time.time + VoteQuietSeconds + UnityEngine.Random.Range(1f, 3f));
        }
    }

    private void TickNearbyBodyReports()
    {
        if (!GameData.Instance || MeetingHud.Instance)
        {
            return;
        }

        var bodies = UnityEngine.Object.FindObjectsOfType<DeadBody>();
        foreach (var bot in EnumerateDeepBots())
        {
            if (!IsAlive(bot) || IsImpostor(bot) || TorRoleAdapter.ShouldReserveBodyForAbility(bot))
            {
                continue;
            }

            var state = GetState(bot);
            if (state.PendingBodyId.HasValue && Time.time >= state.ReportAt)
            {
                TrySubmitReport(bot, state, bodies);
            }

            if (state.PendingBodyId.HasValue || Time.time < state.NextReportCheckAt)
            {
                continue;
            }

            state.NextReportCheckAt = Time.time + 0.45f;
            var reportDistance = DeadBodyPerception.GetReportDistance(bot);
            for (var i = 0; i < bodies.Length; i++)
            {
                var body = bodies[i];
                if (!DeadBodyPerception.IsVisibleAndReportable(body) ||
                    _bodyClaimsUntil.GetValueOrDefault(body.ParentId) > Time.time)
                {
                    continue;
                }

                if (!DeadBodyPerception.CanObserve(
                        bot,
                        body,
                        reportDistance,
                        out var distance,
                        out _))
                {
                    continue;
                }

                state.PendingBodyId = body.ParentId;
                state.ReportAt = Time.time + UnityEngine.Random.Range(0.55f, 1.45f);
                _bodyClaimsUntil[body.ParentId] = state.ReportAt + 2f;
                Stop(bot);
                _memory.RecordAction(bot, "report_reaction", $"queued report for body playerId={body.ParentId}, distance={distance:0.0}");
                _log.LogInfo($"DeepBot body report reaction queued: bot={bot.Data?.PlayerName}, victim={body.ParentId}, distance={distance:0.00}.");
                break;
            }
        }
    }

    private void TrySubmitReport(
        PlayerControl bot,
        SocialState state,
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<DeadBody> bodies)
    {
        var bodyId = state.PendingBodyId;
        state.PendingBodyId = null;
        state.ReportAt = 0f;
        state.NextReportCheckAt = Time.time + 3f;
        if (!bodyId.HasValue || !GameData.Instance)
        {
            return;
        }

        DeadBody? body = null;
        for (var i = 0; i < bodies.Length; i++)
        {
            if (DeadBodyPerception.IsVisibleAndReportable(bodies[i]) &&
                bodies[i].ParentId == bodyId.Value)
            {
                body = bodies[i];
                break;
            }
        }

        if (body is null ||
            !body ||
            Vector2.Distance(bot.GetTruePosition(), body.TruePosition) >
            DeadBodyPerception.GetReportDistance(bot) + 0.15f)
        {
            return;
        }

        var victim = GameData.Instance.GetPlayerById(bodyId.Value);
        if (victim is null || !victim.IsDead)
        {
            return;
        }

        try
        {
            _lastReporterId = bot.PlayerId;
            _lastReportNode = SkeldPathGraph.Instance.NearestNode(body.TruePosition).Name;
            bot.CmdReportDeadBody(victim);
            _memory.RecordAction(bot, "report", $"reported body of {victim.PlayerName}({victim.PlayerId})");
            _log.LogInfo($"DeepBot reported body through native RPC: bot={bot.Data?.PlayerName}, victim={victim.PlayerName}, location={_lastReportNode}.");
        }
        catch (Exception ex)
        {
            _log.LogWarning($"DeepBot body report RPC failed: bot={bot.Data?.PlayerName}, victim={bodyId.Value}, error={ex.Message}");
        }
    }

    private void TickMeeting(PluginConfig config)
    {
        if (!MeetingHud.Instance)
        {
            return;
        }

        foreach (var bot in EnumerateDeepBots())
        {
            if (!IsAlive(bot))
            {
                continue;
            }

            var state = GetState(bot);
            EnsureCurrentMeetingState(bot, state);
            Stop(bot);

            if (config.MeetingChat.Value)
            {
                TrySendHumanReaction(bot, state);
            }

            if (state.DecisionCompleted && !state.DecisionApplied)
            {
                ApplyMeetingDecision(bot, state, config);
            }

            var needsMeetingBrain = config.MeetingChat.Value || config.MeetingVote.Value;
            if (needsMeetingBrain &&
                config.MeetingUseDeepSeek.Value &&
                !state.DecisionInFlight &&
                !state.DecisionCompleted &&
                state.DecisionRounds < MaxMeetingDecisionRoundsPerBot &&
                NeedsAnotherMeetingDecision(bot, state) &&
                Time.time >= state.SpeakAt)
            {
                RequestMeetingDecision(bot, state, config);
            }
            else if (config.MeetingChat.Value &&
                !config.MeetingUseDeepSeek.Value &&
                !state.Spoken &&
                Time.time >= state.SpeakAt)
            {
                SendMeetingLine(bot, state, BuildPersonalityFallbackMeetingLine(bot), "fallback-rules");
            }

            if (config.MeetingVote.Value &&
                !state.Voted &&
                IsVotingOpen())
            {
                var forceVote = Time.time >= state.ForceVoteAt;
                if (forceVote)
                {
                    state.RequestGeneration++;
                    state.DecisionInFlight = false;
                    state.DecisionCompleted = false;
                    state.DecisionApplied = false;
                    _log.LogInfo(
                        $"DeepBot meeting hard vote deadline reached: bot={bot.Data?.PlayerName}, " +
                        $"rounds={state.DecisionRounds}, transcriptVersion={_transcriptVersion}, " +
                        $"analyzedTranscriptVersion={state.LastAnalyzedTranscriptVersion}.");
                    SubmitVote(bot, state);
                    continue;
                }

                if (Time.time < state.VoteAt)
                {
                    continue;
                }

                var canReconsider = state.DecisionRounds < MaxMeetingDecisionRoundsPerBot;
                var conversationStillChanging =
                    Time.time - _lastTranscriptAt < VoteQuietSeconds ||
                    (canReconsider &&
                     NeedsAnotherMeetingDecision(bot, state));
                if (state.DecisionInFlight || state.DecisionCompleted || conversationStillChanging)
                {
                    state.VoteAt = Time.time + 1f;
                    continue;
                }

                SubmitVote(bot, state);
            }
        }
    }

    private void EnsureCurrentMeetingState(PlayerControl bot, SocialState state)
    {
        if (state.MeetingSerial == _meetingSerial)
        {
            return;
        }

        state.MeetingSerial = _meetingSerial;
        state.Spoken = false;
        state.MessagesSent = 0;
        state.DecisionRounds = 0;
        state.Voted = false;
        state.SpeakAt = Time.time + UnityEngine.Random.Range(2f, 5f);
        ConfigureMeetingVoteTimes(bot, state);
        state.DecisionInFlight = false;
        state.DecisionCompleted = false;
        state.DecisionApplied = false;
        state.PendingMeetingDecision = null;
        state.MeetingDecision = null;
        state.LastAnalyzedTranscriptVersion = -1;
        state.PendingTranscriptVersion = -1;
        state.LastAnalyzedHumanTranscriptVersion = 0;
        state.PendingHumanTranscriptVersion = -1;
        state.LastHumanReactionVersion = 0;
        state.PendingHumanReactionVersion = -1;
        state.PendingHumanText = string.Empty;
        state.PendingHumanSourceId = byte.MaxValue;
        state.HumanReactionAt = 0f;
        state.RequestGeneration = 0;
        state.PendingGeneration = 0;
        state.LastMessage = string.Empty;
        state.HumanReconsiderRequested = false;
        state.LastSubmittedVoteId = null;
        state.BeliefScores.Clear();
    }

    private void RequestMeetingDecision(PlayerControl bot, SocialState state, PluginConfig config)
    {
        state.DecisionInFlight = true;
        state.DecisionApplied = false;
        var meetingSerial = _meetingSerial;
        var transcriptVersion = _transcriptVersion;
        var humanTranscriptVersion = _humanTranscriptVersion;
        var generation = ++state.RequestGeneration;
        state.HumanReconsiderRequested = false;
        var prompt = BuildMeetingPrompt(bot, state, config);
        _log.LogInfo(
            $"DeepBot meeting API queued: meeting={_meetingSerial}, bot={bot.Data?.PlayerName}({bot.PlayerId}), " +
            $"round={state.DecisionRounds + 1}, transcriptVersion={transcriptVersion}, " +
            $"team={(IsImpostor(bot) ? "impostor" : "crewmate")}, model={config.Model.Value}, memoryEvents={config.MeetingMemoryEvents.Value}.");

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            BotMeetingDecision? decision = null;
            try
            {
                decision = await _deepSeek.GetMeetingDecisionAsync(prompt, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"DeepBot meeting API failed: bot={prompt.BotName}({prompt.BotId}), error={ex.Message}");
            }
            finally
            {
                if (_meetingActive &&
                    meetingSerial == _meetingSerial &&
                    generation == state.RequestGeneration)
                {
                    state.PendingMeetingDecision = decision;
                    state.PendingTranscriptVersion = transcriptVersion;
                    state.PendingHumanTranscriptVersion = humanTranscriptVersion;
                    state.PendingGeneration = generation;
                    state.DecisionCompleted = true;
                    state.DecisionApplied = false;
                    state.DecisionInFlight = false;
                }
            }
        });
    }

    private BotMeetingPrompt BuildMeetingPrompt(PlayerControl bot, SocialState state, PluginConfig config)
    {
        var publicPlayerLines = new List<string>();
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (!player || player.Data is null || player.Data.Disconnected)
            {
                continue;
            }

            publicPlayerLines.Add(
                $"{player.Data.PlayerName}({player.PlayerId}): {(player.Data.IsDead ? "dead" : "alive")}, " +
                $"color={GetPlayerColorDescription(player)}");
        }

        var publicPlayers = string.Join("\n", publicPlayerLines);
        var evidenceLedger = BuildEvidenceLedger(bot, state);
        var legalVoteIds = EnumerateLivingPlayers()
            .Where(player => player.PlayerId != bot.PlayerId)
            .Where(player => !IsImpostor(bot) || !IsImpostor(player))
            .Select(player => player.PlayerId.ToString())
            .ToArray();
        var transcript = _transcript.Count == 0
            ? "no meeting chat yet"
            : string.Join("\n", _transcript.TakeLast(20).Select(entry => $"{entry.Name}({entry.PlayerId}): {entry.Text}"));
        var latestHuman = _transcript.LastOrDefault(entry =>
            !DeepBotIdentity.IsBotPlayerId(entry.PlayerId));
        var conversationFocus = latestHuman is null
            ? "No newer human statement; compare the current evidence ledger with earlier bot claims."
            : $"Latest human statement: {latestHuman.Name}({latestHuman.PlayerId}): {latestHuman.Text}. " +
              (MentionsPlayer(latestHuman.Text, bot)
                  ? "This bot was directly addressed and must answer the concrete claim first."
                  : "Respond only if this changes, supports, or contradicts this bot's current evidence and vote.");
        var meetingReason = _lastReporterId.HasValue
            ? $"Body reported by playerId={_lastReporterId.Value} near {_lastReportNode ?? "unknown location"}."
            : "Emergency meeting or reporter is unknown.";

        return new BotMeetingPrompt(
            bot.PlayerId,
            bot.Data?.PlayerName ?? $"DeepBot {bot.PlayerId}",
            TorRoleAdapter.TryGetRole(bot, out var torRole) ? torRole.Alignment : IsImpostor(bot) ? "impostor" : "crewmate",
            BotPersonalityCatalog.ForPlayer(bot.PlayerId).MeetingPrompt,
            _memory.BuildIdentity(bot),
            _memory.BuildKnownRoleInformation(bot),
            _memory.BuildTimeline(bot.PlayerId, config.MeetingMemoryEvents.Value),
            publicPlayers,
            evidenceLedger,
            meetingReason,
            transcript,
            conversationFocus,
            legalVoteIds.Length == 0 ? "none" : string.Join(",", legalVoteIds),
            state.DecisionRounds + 1,
            state.MeetingDecision is null
                ? "no earlier decision"
                : $"message={state.LastMessage}; vote={DescribeVote(state.MeetingDecision)}; " +
                  $"follow={state.MeetingDecision.FollowPlayerId?.ToString() ?? "none"}:" +
                  $"{NormalizeFollowIntent(state.MeetingDecision.FollowIntent)}; " +
                  $"private_reason={state.MeetingDecision.Reason ?? "none"}");
    }

    private void ApplyMeetingDecision(PlayerControl bot, SocialState state, PluginConfig config)
    {
        if (state.PendingGeneration != state.RequestGeneration)
        {
            state.DecisionCompleted = false;
            state.DecisionApplied = false;
            state.PendingMeetingDecision = null;
            return;
        }

        state.DecisionApplied = true;
        var receivedDecision = state.PendingMeetingDecision;
        // A transient API failure must not erase a valid conclusion from an
        // earlier round.  That was the main reason bots reverted to rules/skip
        // at the hard voting deadline.
        if (receivedDecision is not null)
        {
            state.MeetingDecision = receivedDecision;
            BlendDecisionIntoBeliefs(bot, state, receivedDecision);
        }
        state.PendingMeetingDecision = null;
        state.LastAnalyzedTranscriptVersion = state.PendingTranscriptVersion;
        if (state.PendingHumanTranscriptVersion >= 0)
        {
            state.LastAnalyzedHumanTranscriptVersion = state.PendingHumanTranscriptVersion;
        }
        state.DecisionRounds++;

        var decision = state.MeetingDecision;
        var message = SanitizeMeetingMessage(receivedDecision?.Message);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = BuildContextualFallbackMeetingLine(bot, state);
        }

        if (IsGenericMeetingFiller(message) &&
            _lastReporterId != bot.PlayerId &&
            !_memory.TryGetLatestWitnessedKiller(bot.PlayerId, out _))
        {
            message = string.Empty;
        }

        if (config.MeetingChat.Value &&
            state.MessagesSent < MaxMeetingMessagesPerBot &&
            !string.Equals(message, state.LastMessage, StringComparison.Ordinal))
        {
            SendMeetingLine(bot, state, message, receivedDecision is null ? "api-null-preserved" : "deepseek");
        }
        else
        {
            state.Spoken = true;
        }

        _log.LogInfo(
            $"DeepBot meeting API applied: meeting={_meetingSerial}, bot={bot.Data?.PlayerName}({bot.PlayerId}), round={state.DecisionRounds}, messages={state.MessagesSent}, " +
            $"decision={(receivedDecision is null ? (decision is null ? "fallback" : "preserved") : "deepseek")}, vote={DescribeVote(decision)}, confidence={decision?.Confidence ?? 0f:0.00}, " +
            $"follow={decision?.FollowPlayerId?.ToString() ?? "none"}:{NormalizeFollowIntent(decision?.FollowIntent)}, " +
            $"reason={decision?.Reason ?? "none"}.");
        state.DecisionCompleted = false;
        state.DecisionApplied = false;
        if (state.DecisionRounds < MaxMeetingDecisionRoundsPerBot &&
            NeedsAnotherMeetingDecision(bot, state))
        {
            state.SpeakAt = Time.time + UnityEngine.Random.Range(1.4f, 3.2f);
        }
    }

    private void SendMeetingLine(PlayerControl bot, SocialState state, string line, string source)
    {
        state.Spoken = true;
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        line = EnforcePrivateEvidenceBoundary(bot, line, out var evidenceRewritten);
        if (evidenceRewritten)
        {
            source += "-evidence-guard";
        }

        try
        {
            _injectingChat = true;
            var sent = bot.RpcSendChat(line);
            _transcript.Add(new TranscriptEntry(bot.PlayerId, bot.Data?.PlayerName ?? $"DeepBot {bot.PlayerId}", line));
            TrimTranscript();
            _transcriptVersion++;
            _lastTranscriptAt = Time.time;
            state.MessagesSent++;
            state.LastMessage = line;
            state.LastAnalyzedTranscriptVersion = _transcriptVersion;
            ScheduleUnspokenBotsAfterSpeech(bot.PlayerId);
            _memory.RecordAction(bot, "meeting_speech", line);
            _log.LogInfo($"DeepBot meeting chat sent: bot={bot.Data?.PlayerName}, source={source}, accepted={sent}, text={line}");
        }
        catch (Exception ex)
        {
            _log.LogWarning($"DeepBot meeting chat failed: bot={bot.Data?.PlayerName}, error={ex.Message}");
        }
        finally
        {
            _injectingChat = false;
        }
    }

    private string EnforcePrivateEvidenceBoundary(PlayerControl bot, string line, out bool rewritten)
    {
        rewritten = false;
        var referencedTarget = EnumerateLivingPlayers()
            .FirstOrDefault(player => player.PlayerId != bot.PlayerId && MentionsPlayer(line, player));
        if (referencedTarget is null)
        {
            var previousSpeaker = _transcript.LastOrDefault(entry => entry.PlayerId != bot.PlayerId);
            if (previousSpeaker is not null)
            {
                referencedTarget = FindPlayer(previousSpeaker.PlayerId);
            }
        }

        var hasPersonalWitness = _memory.TryGetLatestWitnessedKiller(bot.PlayerId, out var witnessedKillerId);
        var referencedTargetMatchesWitness = referencedTarget is null || referencedTarget.PlayerId == witnessedKillerId;
        if (!BotBehaviorPolicy.ShouldRewriteUnsupportedMurderFact(
                line,
                hasPersonalWitness,
                referencedTargetMatchesWitness))
        {
            return line;
        }

        rewritten = true;
        var targetName = referencedTarget?.Data?.PlayerName;
        var replacement = hasPersonalWitness && referencedTargetMatchesWitness && !string.IsNullOrWhiteSpace(targetName)
            ? $"我亲眼看见{targetName}动手，我会投{targetName}。"
            : string.IsNullOrWhiteSpace(targetName)
                ? "我没有亲眼看到击杀，只能按公开发言和路线判断。"
                : $"我没有亲眼看见{targetName}杀人，只是怀疑。{targetName}请解释上一轮的位置。";
        _log.LogWarning(
            $"DeepBot meeting evidence guard rewrote unsupported private claim: bot={bot.Data?.PlayerName}({bot.PlayerId}), " +
            $"target={targetName ?? "unknown"}, original={line}, replacement={replacement}");
        return replacement;
    }

    private void TrySendHumanReaction(PlayerControl bot, SocialState state)
    {
        if (state.Voted ||
            state.MessagesSent >= MaxMeetingMessagesPerBot ||
            state.PendingHumanReactionVersion <= state.LastHumanReactionVersion ||
            Time.time < state.HumanReactionAt)
        {
            return;
        }

        var version = state.PendingHumanReactionVersion;
        var humanText = state.PendingHumanText;
        var humanSourceId = state.PendingHumanSourceId;
        state.LastHumanReactionVersion = version;
        state.PendingHumanReactionVersion = -1;
        state.PendingHumanText = string.Empty;
        state.PendingHumanSourceId = byte.MaxValue;
        var line = BuildHumanReactionLine(bot, state, humanText, humanSourceId);
        if (!IsGenericMeetingFiller(line) &&
            !string.Equals(line, state.LastMessage, StringComparison.Ordinal))
        {
            SendMeetingLine(bot, state, line, "human-reaction-rules");
        }
    }

    private string BuildHumanReactionLine(PlayerControl bot, SocialState state, string humanText, byte humanSourceId)
    {
        var personality = BotPersonalityCatalog.ForPlayer(bot.PlayerId);
        var target = EnumeratePlayers()
            .FirstOrDefault(player => MentionsPlayer(humanText, player));
        if (target?.Data is not null)
        {
            var targetName = target.Data.PlayerName;
            if (target.Data.IsDead)
            {
                return personality.Name switch
                {
                    "急性子" => $"{targetName}已经死了，这轮投不了。你是说谁杀了他？",
                    "社交派" => $"{targetName}已经死了呀。你是怀疑谁杀了他，还是名字说错了？",
                    "懒散派" => $"等等，{targetName}都死了，没法投。你具体指谁？",
                    _ => $"{targetName}已经死亡，不能成为本轮投票目标。请说清你的指认依据。"
                };
            }

            if (IsSupportiveClaim(humanText))
            {
                if (_memory.TryGetLatestWitnessedKiller(bot.PlayerId, out var killerId) && killerId == target.PlayerId)
                {
                    return $"不对，我亲眼看见{targetName}杀人，这条洗白和我的目击冲突。";
                }

                return target.PlayerId == bot.PlayerId
                    ? "收到，但别只凭身份猜测；请说你信我的具体依据。"
                    : $"你是把{targetName}当船员。依据是路线互证，还是亲眼看到他做事？";
            }

            if (target.PlayerId == bot.PlayerId)
            {
                return personality.Name switch
                {
                    "急性子" => "你在指我？把地点、时间和你看到的动作直接说清楚。",
                    "社交派" => "你怀疑我吗？可以，把你看到的路线说出来，我们逐段对。",
                    "懒散派" => "投我？先说说我哪里露馅了，别只报个名字啊。",
                    _ => "你在指认我？请把具体地点、时间和目击内容说清楚。"
                };
            }

            var belief = state.BeliefScores.GetValueOrDefault(target.PlayerId);
            if (belief >= GetRuleVoteThreshold(personality))
            {
                return personality.Name switch
                {
                    "急性子" => $"我目前也偏投{targetName}，这条指认算加重疑点。",
                    "认真派" => $"我已把{targetName}列为首要嫌疑，这条说法会并入现有证据。",
                    "社交派" => $"我现在也更怀疑{targetName}，还有相反路线或不在场证明吗？",
                    "谨慎派" => $"对{targetName}的疑点在累积，但我仍区分转述与亲眼证据。",
                    "懒散派" => $"行，{targetName}现在确实最可疑，我暂时票他。",
                    _ => $"这条指认强化了我对{targetName}的怀疑，我会据此调整投票。"
                };
            }

            return personality.Name switch
            {
                "急性子" => $"先记{targetName}，但你马上补地点和时间。",
                "认真派" => $"你指认{targetName}的依据是什么？请给出地点、时间和目击动作。",
                "社交派" => $"你怀疑{targetName}？还有谁见过他，大家把路线接上。",
                "谨慎派" => $"我听到你指认{targetName}了，但我需要具体证据才会改票。",
                "懒散派" => $"行，{targetName}先记一笔，不过光报名字还不够。",
                _ => $"你是在指认{targetName}吗？请补充具体证据。"
            };
        }

        if (SelfIncriminatingWords.Any(word => humanText.Contains(word, StringComparison.OrdinalIgnoreCase)))
        {
            var speaker = FindPlayer(humanSourceId);
            var speakerName = speaker?.Data?.PlayerName ?? "你";
            return personality.Name switch
            {
                "急性子" => $"{speakerName}这句威胁我记下了；像认狼，但还要结合路线。",
                "认真派" => $"{speakerName}的自证式威胁会提高嫌疑，但不能替代目击证据。",
                "社交派" => $"{speakerName}，你这是玩笑还是认狼？先把刚才路线说清楚。",
                "谨慎派" => $"这句威胁值得警惕，不过我不会只凭一句话定罪。",
                "懒散派" => $"这种话挺招怀疑的，{speakerName}先进入观察名单吧。",
                _ => $"{speakerName}的威胁性发言会增加嫌疑，但仍需与路线核对。"
            };
        }

        return personality.Name switch
        {
            "急性子" => "这句话暂时指向不明，我先按已有路线和嫌疑判断。",
            "社交派" => "我听到了；如果是新线索，补上对象就能和前面的路线对照。",
            "谨慎派" => "对象不明确，这句话暂不改变我的证据权重。",
            "懒散派" => "这句没具体目标，我先不跟着改票。",
            _ => "这条信息对象不明确，我会保留原有判断。"
        };
    }

    private string BuildPersonalityFallbackMeetingLine(PlayerControl bot)
    {
        var personality = BotPersonalityCatalog.ForPlayer(bot.PlayerId);
        if (_lastReporterId == bot.PlayerId)
        {
            return personality.Name switch
            {
                "急性子" => $"我在{_lastReportNode ?? "附近"}报的尸体，没看清谁动手，先对路线。",
                "懒散派" => $"尸体在{_lastReportNode ?? "附近"}，我真没看见是谁，别急着乱票。",
                _ => $"我在{_lastReportNode ?? "附近"}发现尸体，但没有亲眼看到凶手。"
            };
        }

        var topAccused = _accusations
            .Where(pair => pair.Key != bot.PlayerId)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .FirstOrDefault();
        if (topAccused.Value > 0)
        {
            var target = FindPlayer(topAccused.Key);
            if (target?.Data is not null)
            {
                return personality.Name switch
                {
                    "急性子" => $"{target.Data.PlayerName}先把路线说清楚，我目前偏怀疑你。",
                    "社交派" => $"{target.Data.PlayerName}，你刚才具体在哪？大家把时间线对一下。",
                    "谨慎派" => $"我也注意到{target.Data.PlayerName}，但现有说法还不足以下结论。",
                    "懒散派" => $"{target.Data.PlayerName}有点怪，不过就这点信息我还不想硬票。",
                    _ => $"目前对{target.Data.PlayerName}的疑点最多，但我会继续听新证据。"
                };
            }
        }

        if (_transcript.Any(entry => entry.PlayerId != bot.PlayerId))
        {
            return personality.Name switch
            {
                "急性子" => "都直接报位置和遇到的人，别讲没用的。",
                "社交派" => "谁最后见过死者？大家按顺序把路线接起来吧。",
                "谨慎派" => "先区分亲眼所见和别人转述，再决定投票。",
                "懒散派" => "信息还太少吧，我先听着，别这么快乱票。",
                _ => "先把各自路线说清楚，没有硬证据就谨慎投票。"
            };
        }

        var location = SkeldPathGraph.Instance.NearestNode(bot.GetTruePosition()).Name;
        return personality.Name switch
        {
            "急性子" => $"我刚在{location}，谁经过那里直接说。",
            "社交派" => $"我在{location}附近，有人能互证路线吗？",
            "谨慎派" => $"我只能确认自己刚在{location}附近，其他信息暂不确定。",
            "懒散派" => $"我刚才在{location}晃着，没看到特别的。",
            _ => $"我会按自己在{location}附近看到的情况判断。"
        };
    }

    private static float GetOpeningSpeakDelay(byte playerId)
    {
        return BotPersonalityCatalog.ForPlayer(playerId).Name switch
        {
            "急性子" => UnityEngine.Random.Range(2.5f, 4.5f),
            "认真派" => UnityEngine.Random.Range(4.0f, 6.5f),
            "社交派" => UnityEngine.Random.Range(3.5f, 7.0f),
            "谨慎派" => UnityEngine.Random.Range(6.0f, 9.5f),
            "懒散派" => UnityEngine.Random.Range(7.5f, 11.0f),
            _ => UnityEngine.Random.Range(3.5f, 9.0f)
        };
    }

    private static void ConfigureMeetingVoteTimes(PlayerControl bot, SocialState state)
    {
        var discussionSeconds = GameRuleSettings.GetDiscussionTime(15);
        var votingSeconds = GameRuleSettings.GetVotingTime(120);
        var boldness = bot
            ? BotPersonalityCatalog.ForPlayer(bot.PlayerId).VoteBoldness
            : 0.5f;
        var deliberateSeconds = Mathf.Lerp(12f, 3.5f, boldness) + UnityEngine.Random.Range(0.5f, 3.5f);
        if (votingSeconds > 0)
        {
            deliberateSeconds = Mathf.Min(deliberateSeconds, Mathf.Max(1f, votingSeconds - 4f));
        }

        state.VoteAt = Time.time + discussionSeconds + deliberateSeconds;
        state.ForceVoteAt = votingSeconds > 0
            ? Time.time + discussionSeconds + Mathf.Max(1f, votingSeconds - 2.5f)
            : Time.time + discussionSeconds + 300f;
    }

    private string BuildFallbackMeetingLine(PlayerControl bot)
    {
        if (_lastReporterId == bot.PlayerId)
        {
            return $"我在{_lastReportNode ?? "附近"}发现尸体，没看清是谁动的手。";
        }

        var topAccused = _accusations
            .Where(pair => pair.Key != bot.PlayerId)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .FirstOrDefault();
        if (topAccused.Value > 0)
        {
            var target = FindPlayer(topAccused.Key);
            if (target?.Data is not null)
            {
                return $"我也注意到{target.Data.PlayerName}，但目前证据还不够。";
            }
        }

        if (_transcript.Any(entry => entry.PlayerId != bot.PlayerId))
        {
            return "先把各自路线说清楚，没有硬证据就别乱票。";
        }

        var location = SkeldPathGraph.Instance.NearestNode(bot.GetTruePosition()).Name;
        return $"我会按自己在{location}附近看到的情况判断。";
    }

    private void SubmitVote(PlayerControl bot, SocialState state)
    {
        if (!MeetingHud.Instance || MeetingHud.Instance.DidVote(bot.PlayerId))
        {
            state.Voted = true;
            return;
        }

        var voteId = ChooseVote(bot, state);
        try
        {
            MeetingHud.Instance.CmdCastVote(bot.PlayerId, voteId);
            if (TorRoleAdapter.TryUseStrategicMeetingVoteAbility(bot, voteId, out var meetingAbilityOutcome))
            {
                _memory.RecordAction(bot, "meeting_ability", meetingAbilityOutcome);
                _log.LogInfo($"DeepBot TOR meeting ability used: bot={bot.Data?.PlayerName}, outcome={meetingAbilityOutcome}.");
            }
            MeetingHud.Instance.CheckForEndVoting();
            state.Voted = true;
            state.LastSubmittedVoteId = voteId == SkipVoteId ? null : voteId;
            _memory.RecordAction(bot, "vote", voteId == SkipVoteId ? "voted skip" : $"voted playerId={voteId}");
            _log.LogInfo(
                $"DeepBot vote submitted through native RPC: bot={bot.Data?.PlayerName}, " +
                $"source={(state.MeetingDecision is null ? "rules" : "deepseek")}, vote={(voteId == SkipVoteId ? "skip" : voteId.ToString())}.");
        }
        catch (Exception ex)
        {
            state.VoteAt = Time.time + 2f;
            _log.LogWarning($"DeepBot vote RPC failed: bot={bot.Data?.PlayerName}, error={ex.Message}");
        }
    }

    private byte ChooseVote(PlayerControl bot, SocialState state)
    {
        if (_memory.TryGetLatestWitnessedKiller(bot.PlayerId, out var witnessedKillerId))
        {
            var witnessedKiller = FindPlayer(witnessedKillerId);
            if (witnessedKiller is not null &&
                IsAlive(witnessedKiller) &&
                witnessedKiller.PlayerId != bot.PlayerId &&
                !TorRoleAdapter.AreLoverPartners(bot, witnessedKiller) &&
                (!IsImpostor(bot) || !IsImpostor(witnessedKiller)))
            {
                return witnessedKiller.PlayerId;
            }
        }

        var personality = BotPersonalityCatalog.ForPlayer(bot.PlayerId);
        var confidenceThreshold = BotBehaviorPolicy.GetMeetingVoteConfidenceThreshold(personality.VoteBoldness);
        var decision = state.MeetingDecision;
        if (decision is not null)
        {
            if (!decision.SkipVote &&
                decision.VotePlayerId.HasValue &&
                decision.Confidence >= confidenceThreshold &&
                decision.VotePlayerId.Value is >= byte.MinValue and <= byte.MaxValue)
            {
                var target = FindPlayer((byte)decision.VotePlayerId.Value);
                if (target is not null &&
                    IsAlive(target) &&
                    target.PlayerId != bot.PlayerId &&
                    (!IsImpostor(bot) || !IsImpostor(target)))
                {
                    return target.PlayerId;
                }
            }
        }

        var candidates = EnumerateLivingPlayers()
            .Where(player => player.PlayerId != bot.PlayerId)
            .Where(player => !IsImpostor(bot) || !IsImpostor(player))
            .ToArray();
        if (candidates.Length == 0)
        {
            return SkipVoteId;
        }

        var accused = candidates
            .Select(player => new
            {
                Player = player,
                Score = state.BeliefScores.GetValueOrDefault(player.PlayerId)
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Player.PlayerId)
            .First();
        var publicClaimThreshold = GetRuleVoteThreshold(personality);
        return accused.Score >= publicClaimThreshold
            ? accused.Player.PlayerId
            : SkipVoteId;
    }

    private string BuildEvidenceLedger(PlayerControl bot, SocialState state)
    {
        var lines = EnumerateLivingPlayers()
            .Where(player => player.PlayerId != bot.PlayerId)
            .Where(player => !IsImpostor(bot) || !IsImpostor(player))
            .Select(player => new
            {
                Player = player,
                Score = state.BeliefScores.GetValueOrDefault(player.PlayerId),
                PublicClaims = _accusations.GetValueOrDefault(player.PlayerId)
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.PublicClaims)
            .ThenBy(item => item.Player.PlayerId)
            .Take(5)
            .Select(item =>
                $"{item.Player.Data!.PlayerName}({item.Player.PlayerId}): personal_public_claim_score={item.Score:0.00}, " +
                $"named_accusations={item.PublicClaims}")
            .ToArray();

        var earlier = state.MeetingDecision is null
            ? "No valid earlier model conclusion."
            : $"Last valid conclusion: vote={DescribeVote(state.MeetingDecision)}, " +
              $"confidence={state.MeetingDecision.Confidence:0.00}, reason={state.MeetingDecision.Reason ?? "none"}.";
        return lines.Length == 0
            ? earlier + " No public candidate evidence has accumulated."
            : earlier + "\n" + string.Join("\n", lines) +
              "\nScores summarize public claims only and are not personal eyewitness facts.";
    }

    private static void BlendDecisionIntoBeliefs(PlayerControl bot, SocialState state, BotMeetingDecision decision)
    {
        if (decision.SkipVote ||
            !decision.VotePlayerId.HasValue ||
            decision.VotePlayerId.Value is < byte.MinValue or > byte.MaxValue)
        {
            return;
        }

        var targetId = (byte)decision.VotePlayerId.Value;
        var target = FindPlayer(targetId);
        if (target is null || !IsAlive(target) || targetId == bot.PlayerId || (IsImpostor(bot) && IsImpostor(target)))
        {
            return;
        }

        state.BeliefScores[targetId] = Mathf.Max(
            state.BeliefScores.GetValueOrDefault(targetId),
            Mathf.Clamp01(decision.Confidence) * 1.35f);
    }

    private string BuildContextualFallbackMeetingLine(PlayerControl bot, SocialState state)
    {
        var personality = BotPersonalityCatalog.ForPlayer(bot.PlayerId);
        var top = EnumerateLivingPlayers()
            .Where(player => player.PlayerId != bot.PlayerId)
            .Where(player => !IsImpostor(bot) || !IsImpostor(player))
            .Select(player => new { Player = player, Score = state.BeliefScores.GetValueOrDefault(player.PlayerId) })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Player.PlayerId)
            .FirstOrDefault();
        if (top is not null && top.Score > 0.01f)
        {
            var name = top.Player.Data?.PlayerName ?? $"{top.Player.PlayerId}号";
            var ready = top.Score >= GetRuleVoteThreshold(personality);
            return ready
                ? personality.Name switch
                {
                    "急性子" => $"目前证据权重最高的是{name}，我这轮倾向直接投。",
                    "认真派" => $"综合前面陈述，我暂把{name}列为首要投票目标。",
                    "社交派" => $"现在线索更多指向{name}，除非有人能补出相反时间线。",
                    "谨慎派" => $"现有疑点以{name}最高，我会带着保留意见投票。",
                    "懒散派" => $"听下来{name}最像，我先跟这个判断。",
                    _ => $"综合已有信息，我目前倾向投{name}。"
                }
                : personality.Name switch
                {
                    "急性子" => $"{name}的疑点最多，但还差一条能对上的路线。",
                    "社交派" => $"我更留意{name}，谁能补一段相关路线？",
                    "谨慎派" => $"{name}目前权重最高，但还没达到我的定票标准。",
                    "懒散派" => $"{name}有点怪，我先记着，不急着硬票。",
                    _ => $"目前更怀疑{name}，但我还在等待交叉证据。"
                };
        }

        return BuildPersonalityFallbackMeetingLine(bot);
    }

    private static bool IsExplicitAccusation(string text)
    {
        var indicators = new[] { "投", "票", "就是", "内鬼", "凶手", "认狼", "自爆", "impostor", "sus" };
        return indicators.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSupportiveClaim(string text)
    {
        string[] hostileOverrides = ["不是船员", "不像船员", "假船员", "船员面具", "装船员"];
        if (hostileOverrides.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string[] supportivePhrases =
        [
            "是船员", "像船员", "可能是船员", "应该是船员", "是好人", "像好人",
            "可能是好人", "可信", "我信", "可以信", "不怀疑", "不像内鬼", "不是内鬼"
        ];
        return supportivePhrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGenericMeetingFiller(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        string[] fillerPhrases =
        [
            "先把各自路线说清楚", "都直接报位置", "信息还太少", "没有硬证据",
            "谨慎投票", "先听着", "别这么快乱票", "先区分亲眼所见",
            "谁最后见过死者", "大家按顺序", "把路线接起来"
        ];
        return fillerPhrases.Any(phrase => text.Contains(phrase, StringComparison.Ordinal));
    }

    private static float GetRuleVoteThreshold(BotPersonalityProfile personality)
    {
        return Mathf.Lerp(
            1.25f,
            0.45f,
            personality.SocialSuggestibility * personality.VoteBoldness);
    }

    private SocialState GetState(PlayerControl bot)
    {
        if (!_states.TryGetValue(bot.PlayerId, out var state))
        {
            state = new SocialState();
            _states[bot.PlayerId] = state;
        }

        return state;
    }

    private void TrimTranscript()
    {
        while (_transcript.Count > 24)
        {
            _transcript.RemoveAt(0);
        }
    }

    private void ScheduleUnspokenBotsAfterSpeech(byte speakerId)
    {
        foreach (var bot in EnumerateDeepBots())
        {
            if (bot.PlayerId == speakerId)
            {
                continue;
            }

            var state = GetState(bot);
            if (state.Voted ||
                state.DecisionRounds >= 1 ||
                state.DecisionRounds >= MaxMeetingDecisionRoundsPerBot)
            {
                continue;
            }

            state.SpeakAt = Math.Min(state.SpeakAt, Time.time + UnityEngine.Random.Range(1.8f, 4.0f));
            state.VoteAt = Math.Max(state.VoteAt, Time.time + VoteQuietSeconds + UnityEngine.Random.Range(0.5f, 2f));
        }
    }

    private bool NeedsAnotherMeetingDecision(PlayerControl bot, SocialState state)
    {
        if (state.DecisionRounds == 0 || state.HumanReconsiderRequested)
        {
            return true;
        }

        if (!BotBehaviorPolicy.HasUnanalyzedMeetingTranscript(
                state.LastAnalyzedTranscriptVersion,
                _transcriptVersion) ||
            _transcript.Count == 0)
        {
            return false;
        }

        var latest = _transcript[^1];
        if (!DeepBotIdentity.IsBotPlayerId(latest.PlayerId))
        {
            return true;
        }

        var directlyAddressesBot = MentionsPlayer(latest.Text, bot);
        var currentCandidate = state.MeetingDecision?.VotePlayerId is >= byte.MinValue and <= byte.MaxValue
            ? FindPlayer((byte)state.MeetingDecision.VotePlayerId.Value)
            : null;
        var mentionsCurrentCandidate = currentCandidate is not null &&
            MentionsPlayer(latest.Text, currentCandidate);
        return BotBehaviorPolicy.ShouldReconsiderBotMeetingLine(
            directlyAddressesBot,
            mentionsCurrentCandidate);
    }

    private static string NormalizeFollowIntent(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "trust" => "trust",
            "suspect" => "suspect",
            _ => "none"
        };
    }

    private static byte? ValidateFollowPlayerId(PlayerControl bot, int? requestedPlayerId)
    {
        if (!requestedPlayerId.HasValue || requestedPlayerId.Value is < byte.MinValue or > byte.MaxValue)
        {
            return null;
        }

        var target = FindPlayer((byte)requestedPlayerId.Value);
        if (target is null ||
            !IsAlive(target) ||
            target.PlayerId == bot.PlayerId ||
            (IsImpostor(bot) && IsImpostor(target)))
        {
            return null;
        }

        return target.PlayerId;
    }

    private static string SanitizeMeetingMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var clean = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (clean.Length > 90)
        {
            clean = clean[..90];
        }

        var readableForbidden = new[] { "AI", "人工智能", "模型", "提示词", "插件", "API", "代码" };
        if (readableForbidden.Any(word => clean.Contains(word, StringComparison.OrdinalIgnoreCase)))
        {
            return string.Empty;
        }

        var forbidden = new[] { "AI", "人工智能", "模型", "提示词", "插件", "API", "代码" };
        return forbidden.Any(word => clean.Contains(word, StringComparison.OrdinalIgnoreCase))
            ? string.Empty
            : clean;
    }

    private static string DescribeVote(BotMeetingDecision? decision)
    {
        if (decision is null)
        {
            return "fallback";
        }

        return decision.SkipVote || !decision.VotePlayerId.HasValue
            ? "skip"
            : decision.VotePlayerId.Value.ToString();
    }

    private static bool ShouldRun(PluginConfig config)
    {
        return config.Enabled.Value &&
            config.SocialInteraction.Value &&
            !config.DryRun.Value &&
            GameRuleSettings.IsSkeldMap() &&
            IsHostAuthority();
    }

    private static bool IsHostAuthority()
    {
        var client = AmongUsClient.Instance;
        return client &&
            client.NetworkMode == NetworkModes.LocalGame &&
            client.AmHost &&
            client.ClientId >= 0 &&
            client.ClientId == client.HostId;
    }

    private static bool IsVotingOpen()
    {
        if (!MeetingHud.Instance)
        {
            return false;
        }

        var state = (int)MeetingHud.Instance.CurrentState;
        return state is 2 or 3;
    }

    private static bool IsAlive(PlayerControl player)
    {
        return player &&
            player.Data is not null &&
            !player.Data.IsDead &&
            !player.Data.Disconnected;
    }

    private static bool IsImpostor(PlayerControl player)
    {
        return player.Data is not null &&
            player.Data.Role is not null &&
            player.Data.Role.IsImpostor;
    }

    private static void Stop(PlayerControl bot)
    {
        if (!bot || !bot.MyPhysics)
        {
            return;
        }

        bot.MyPhysics.SetNormalizedVelocity(Vector2.zero);
        if (bot.MyPhysics.body)
        {
            bot.MyPhysics.body.velocity = Vector2.zero;
        }
    }

    private static PlayerControl? FindPlayer(byte playerId)
    {
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (player && player.PlayerId == playerId)
            {
                return player;
            }
        }

        return null;
    }

    private static bool MentionsPlayer(string text, PlayerControl player)
    {
        if (!player || player.Data is null)
        {
            return false;
        }

        var outfit = player.Data.DefaultOutfit;
        var colorId = outfit is null ? player.CurrentOutfit.ColorId : outfit.ColorId;
        return BotBehaviorPolicy.MentionsPlayerAlias(
            text,
            player.PlayerId,
            player.Data.PlayerName,
            colorId,
            player.Data.ColorName);
    }

    private static string GetPlayerColorDescription(PlayerControl player)
    {
        if (!player || player.Data is null)
        {
            return "unknown";
        }

        var outfit = player.Data.DefaultOutfit;
        var colorId = outfit is null ? player.CurrentOutfit.ColorId : outfit.ColorId;
        return $"{player.Data.ColorName}(colorId={colorId})";
    }

    private static IEnumerable<PlayerControl> EnumerateLivingPlayers()
    {
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (IsAlive(player))
            {
                yield return player;
            }
        }
    }

    private static IEnumerable<PlayerControl> EnumeratePlayers()
    {
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (player && player.Data is not null && !player.Data.Disconnected)
            {
                yield return player;
            }
        }
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

    private sealed class SocialState
    {
        public int MeetingSerial { get; set; }
        public bool Spoken { get; set; }
        public int MessagesSent { get; set; }
        public int DecisionRounds { get; set; }
        public bool Voted { get; set; }
        public float SpeakAt { get; set; }
        public float VoteAt { get; set; }
        public float ForceVoteAt { get; set; }
        public byte? PendingBodyId { get; set; }
        public float ReportAt { get; set; }
        public float NextReportCheckAt { get; set; }
        public bool DecisionInFlight { get; set; }
        public bool DecisionCompleted { get; set; }
        public bool DecisionApplied { get; set; }
        public BotMeetingDecision? PendingMeetingDecision { get; set; }
        public BotMeetingDecision? MeetingDecision { get; set; }
        public int LastAnalyzedTranscriptVersion { get; set; } = -1;
        public int PendingTranscriptVersion { get; set; } = -1;
        public int PendingHumanTranscriptVersion { get; set; } = -1;
        public int LastAnalyzedHumanTranscriptVersion { get; set; }
        public int LastHumanReactionVersion { get; set; }
        public int PendingHumanReactionVersion { get; set; } = -1;
        public string PendingHumanText { get; set; } = string.Empty;
        public byte PendingHumanSourceId { get; set; } = byte.MaxValue;
        public float HumanReactionAt { get; set; }
        public int RequestGeneration { get; set; }
        public int PendingGeneration { get; set; }
        public string LastMessage { get; set; } = string.Empty;
        public bool HumanReconsiderRequested { get; set; }
        public byte? LastSubmittedVoteId { get; set; }
        public Dictionary<byte, float> BeliefScores { get; } = [];
    }

    private sealed record TranscriptEntry(byte PlayerId, string Name, string Text);
}
