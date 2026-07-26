using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AmongUs.GameOptions;
using BepInEx.Logging;
using InnerNet;
using UnityEngine;

namespace AmongUsDeepSeekBots;

internal sealed class BotSocialDirector
{
    private const float SocialUpdateInterval = 0.25f;
    private const float VoteQuietSeconds = 5.5f;
    private const float MeetingLlmRequestSpacingSeconds = 1.15f;
    private const int MaxMeetingMessagesPerBot = 4;
    private const int MaxAutonomousMeetingMessagesPerBot = 3;
    private const int MaxMeetingDecisionRoundsPerBot = 5;
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

    private static readonly string[] ExonerationWords =
    [
        "清白", "洗清", "排除", "不是内鬼", "不是凶手", "可以信", "可信", "没嫌疑", "无嫌疑",
        "innocent", "cleared", "clear", "not impostor", "safe"
    ];

    private readonly ManualLogSource _log;
    private readonly DeepSeekDecisionClient _deepSeek;
    private readonly BotMatchMemory _memory;
    private readonly Dictionary<byte, SocialState> _states = [];
    private readonly Dictionary<byte, int> _accusations = [];
    private readonly Dictionary<byte, HashSet<byte>> _accusationSourcesByTarget = [];
    private readonly Dictionary<byte, HashSet<byte>> _supportSourcesByTarget = [];
    private readonly Dictionary<byte, PublicConcreteClaim> _latestConcreteClaimsByTarget = [];
    private readonly HashSet<byte> _publicJesterClaims = [];
    private readonly Dictionary<byte, float> _bodyClaimsUntil = [];
    private readonly List<TranscriptEntry> _transcript = [];
    private float _nextUpdateAt;
    private bool _meetingActive;
    private bool _injectingChat;
    private int _meetingSerial;
    private int _transcriptVersion;
    private int _humanTranscriptVersion;
    private float _lastTranscriptAt;
    private float _nextMeetingLlmRequestAt;
    private float _nextMeetingVoteRepairAt;
    private int _meetingCompletionChecks;
    private byte? _lastReporterId;
    private string? _lastReportNode;
    private float _lastReportCapturedAt;
    private int _claimMatchSerial = -1;

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
        if (_claimMatchSerial != _memory.MatchSerial ||
            !_lastReporterId.HasValue ||
            Time.time - _lastReportCapturedAt > 8f)
        {
            // An emergency-button meeting has no body reporter. Never reuse
            // the reporter from a previous round or previous match.
            _lastReporterId = null;
            _lastReportNode = null;
            _lastReportCapturedAt = 0f;
        }
        if (_claimMatchSerial != _memory.MatchSerial)
        {
            _claimMatchSerial = _memory.MatchSerial;
            _publicJesterClaims.Clear();
        }
        _transcript.Clear();
        _accusations.Clear();
        _accusationSourcesByTarget.Clear();
        _supportSourcesByTarget.Clear();
        _latestConcreteClaimsByTarget.Clear();
        _transcriptVersion = 0;
        _humanTranscriptVersion = 0;
        _lastTranscriptAt = Time.time;
        _nextMeetingVoteRepairAt = Time.time + 0.75f;
        _meetingCompletionChecks = 0;

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
            state.BodyObservationStartedAt = 0f;
            state.PendingReportStrategy = string.Empty;
            state.BodyIgnoreUntil.Clear();
            state.DecisionInFlight = false;
            state.DecisionCompleted = false;
            state.DecisionApplied = false;
            state.DecisionReadyAt = 0f;
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
            state.PendingNativeVoteId = null;
            state.VoteCommandSentAt = 0f;
            state.BeliefScores.Clear();
            if (state.PersistentBeliefMatchSerial != _memory.MatchSerial)
            {
                state.PersistentPublicSuspicion.Clear();
                state.PersistentBeliefMatchSerial = _memory.MatchSerial;
            }

            foreach (var staleTargetId in state.PersistentPublicSuspicion.Keys
                         .Where(targetId =>
                         {
                             var target = FindPlayer(targetId);
                             return target is null || !IsAlive(target) || targetId == bot.PlayerId;
                         })
                         .ToArray())
            {
                state.PersistentPublicSuspicion.Remove(staleTargetId);
            }

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

    internal void RecordBodyReporter(PlayerControl reporter, NetworkedPlayerInfo victim)
    {
        if (!reporter || reporter.Data is null || victim is null)
        {
            return;
        }

        _lastReporterId = reporter.PlayerId;
        _lastReportCapturedAt = Time.time;
        var body = UnityEngine.Object.FindObjectsOfType<DeadBody>()
            .FirstOrDefault(candidate => candidate && candidate.ParentId == victim.PlayerId);
        var position = body ? body!.TruePosition : reporter.GetTruePosition();
        _lastReportNode = SkeldPathGraph.Instance.NearestNode(position).Name;
        _log.LogInfo(
            $"DeepBot authoritative body reporter captured: reporter={reporter.Data.PlayerName}({reporter.PlayerId}), " +
            $"victim={victim.PlayerName}({victim.PlayerId}), location={_lastReportNode}.");
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

            CarryForwardPublicSuspicion(bot, state);
        }

        _transcript.Clear();
        _accusations.Clear();
        _accusationSourcesByTarget.Clear();
        _supportSourcesByTarget.Clear();
        _latestConcreteClaimsByTarget.Clear();
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
            state.BodyObservationStartedAt = 0f;
            state.PendingReportStrategy = string.Empty;
            state.BodyIgnoreUntil.Clear();
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

    private static void CarryForwardPublicSuspicion(PlayerControl bot, SocialState state)
    {
        var targetIds = state.PersistentPublicSuspicion.Keys
            .Concat(state.BeliefScores.Keys)
            .Distinct()
            .ToArray();
        foreach (var targetId in targetIds)
        {
            var target = FindPlayer(targetId);
            if (target is null || !IsAlive(target) || targetId == bot.PlayerId)
            {
                state.PersistentPublicSuspicion.Remove(targetId);
                continue;
            }

            var previous = state.PersistentPublicSuspicion.GetValueOrDefault(targetId);
            var current = state.BeliefScores.GetValueOrDefault(targetId);
            var carried = CalculateCarriedSuspicion(previous, current);
            if (carried < 0.04f)
            {
                state.PersistentPublicSuspicion.Remove(targetId);
            }
            else
            {
                state.PersistentPublicSuspicion[targetId] = carried;
            }
        }
    }

    private static float CalculateCarriedSuspicion(float previous, float current)
    {
        // Public testimony remains a fallible prior, not permanent proof. It
        // fades between meetings, accumulates when new concrete claims point in
        // the same direction, and can be reduced by later exonerating claims.
        return Mathf.Clamp(previous * 0.90f + current * 0.78f, 0f, 1.35f);
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
        var explicitAccusation = !supportiveClaim && mentionedPlayers.Length > 0 && IsExplicitAccusation(clean);
        var concreteEvidenceClaim = IsConcreteEvidenceClaim(clean);
        var claimTargets = ResolveClaimTargets(
            clean,
            mentionedPlayers,
            supportiveClaim || explicitAccusation || concreteEvidenceClaim);
        var mentionedPlayer = claimTargets.FirstOrDefault() ?? mentionedPlayers.FirstOrDefault();
        if (source && IsSelfJesterClaim(clean))
        {
            _publicJesterClaims.Add(sourceId);
            _log.LogInfo(
                $"DeepBot public Jester-risk claim recorded: meeting={_meetingSerial}, " +
                $"player={sourceName}({sourceId}), source=self-claim.");
        }
        var firstAccusationTargets = new HashSet<byte>();
        var firstSupportTargets = new HashSet<byte>();
        foreach (var player in claimTargets.Where(IsAlive))
        {
            if (hasSuspicionLanguage || explicitAccusation)
            {
                if (!_accusationSourcesByTarget.TryGetValue(player.PlayerId, out var sources))
                {
                    sources = [];
                    _accusationSourcesByTarget[player.PlayerId] = sources;
                }

                if (sources.Add(sourceId))
                {
                    firstAccusationTargets.Add(player.PlayerId);
                }

                // Public corroboration is the number of independent speakers,
                // not how many times one speaker repeats the same accusation.
                _accusations[player.PlayerId] = sources.Count;
            }

            if (supportiveClaim)
            {
                if (!_supportSourcesByTarget.TryGetValue(player.PlayerId, out var supportSources))
                {
                    supportSources = [];
                    _supportSourcesByTarget[player.PlayerId] = supportSources;
                }

                if (supportSources.Add(sourceId))
                {
                    firstSupportTargets.Add(player.PlayerId);
                }
            }

            if (explicitAccusation && concreteEvidenceClaim)
            {
                _latestConcreteClaimsByTarget[player.PlayerId] = new PublicConcreteClaim(
                    sourceId,
                    sourceName,
                    ClassifyConcreteClaimAction(clean));
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
                foreach (var player in claimTargets.Where(IsAlive))
                {
                    if (supportiveClaim && player.PlayerId != bot.PlayerId)
                    {
                        // One speaker may clarify or repeat themselves, but a repeated
                        // "X is clear" is still one public claim. Counting every repeat
                        // allowed a single persuasive speaker to erase a valid prior.
                        if (!firstSupportTargets.Contains(player.PlayerId))
                        {
                            continue;
                        }

                        var personallyWitnessedKill = _memory.TryGetLatestWitnessedKiller(bot.PlayerId, out var killerId) &&
                            killerId == player.PlayerId;
                        if (!personallyWitnessedKill)
                        {
                            var trustInfluence = 0.16f * Mathf.Lerp(
                                0.35f,
                                0.85f,
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

                    var claimStrength = CalculatePublicClaimStrength(
                        explicitAccusation,
                        concreteEvidenceClaim);
                    if (!firstAccusationTargets.Contains(player.PlayerId))
                    {
                        claimStrength *= 0.08f;
                    }
                    if (isBotSource)
                    {
                        claimStrength *= 0.90f;
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
            if (!isBotSource && directlyAddressed)
            {
                state.PendingHumanReactionVersion = _humanTranscriptVersion;
                state.PendingHumanText = clean;
                state.PendingHumanSourceId = sourceId;
                state.HumanReactionAt = Time.time + GetMeetingThoughtDelay(
                    bot.PlayerId,
                    directlyAddressed,
                    explicitAccusation || selfIncriminating || hasSuspicionLanguage,
                    state.DecisionRounds);
            }

            var delay = GetMeetingThoughtDelay(
                bot.PlayerId,
                directlyAddressed,
                explicitAccusation || selfIncriminating || hasSuspicionLanguage,
                state.DecisionRounds);
            state.SpeakAt = Math.Min(
                Math.Max(state.SpeakAt, Time.time + delay),
                Math.Max(Time.time, state.ForceVoteAt - 1.5f));
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
            if (!IsAlive(bot) || TorRoleAdapter.ShouldReserveBodyForAbility(bot))
            {
                continue;
            }

            var state = GetState(bot);
            if (state.PendingBodyId.HasValue)
            {
                Stop(bot);
            }
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
                    _bodyClaimsUntil.GetValueOrDefault(body.ParentId) > Time.time ||
                    state.BodyIgnoreUntil.GetValueOrDefault(body.ParentId) > Time.time)
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

                var nearbyObservers = CountVisibleLivingPlayersNearBody(bot, body);
                if (!TryChooseBodyReportPlan(
                        bot,
                        body.ParentId,
                        nearbyObservers,
                        out var observationDelay,
                        out var strategy))
                {
                    state.BodyIgnoreUntil[body.ParentId] = Time.time + UnityEngine.Random.Range(4f, 8f);
                    _memory.RecordAction(
                        bot,
                        "report_decision",
                        $"chose not to report body playerId={body.ParentId} yet; nearbyObservers={nearbyObservers}; strategy={strategy}");
                    _log.LogInfo(
                        $"DeepBot body report held by faction strategy: bot={bot.Data?.PlayerName}, victim={body.ParentId}, " +
                        $"nearbyObservers={nearbyObservers}, reason={strategy}.");
                    continue;
                }

                state.PendingBodyId = body.ParentId;
                state.BodyObservationStartedAt = Time.time;
                state.PendingReportStrategy = strategy;
                state.ReportAt = Time.time + observationDelay;
                _bodyClaimsUntil[body.ParentId] = state.ReportAt + 2f;
                Stop(bot);
                _memory.RecordAction(
                    bot,
                    "report_reaction",
                    $"paused to inspect body playerId={body.ParentId}, distance={distance:0.0}, " +
                    $"observationWindow={observationDelay:0.0}s, nearbyObservers={nearbyObservers}, strategy={strategy}");
                _log.LogInfo(
                    $"DeepBot body scene observation queued: bot={bot.Data?.PlayerName}, victim={body.ParentId}, " +
                    $"distance={distance:0.00}, wait={observationDelay:0.0}s, nearbyObservers={nearbyObservers}, strategy={strategy}.");
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
        var observationStartedAt = state.BodyObservationStartedAt;
        var reportStrategy = state.PendingReportStrategy;
        state.PendingBodyId = null;
        state.ReportAt = 0f;
        state.BodyObservationStartedAt = 0f;
        state.PendingReportStrategy = string.Empty;
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
            var sceneSummary = BuildBodySceneSummary(bot, body);
            _memory.RecordAction(
                bot,
                "body_scene",
                $"observed for {Mathf.Max(0f, Time.time - observationStartedAt):0.0}s before reporting; " +
                $"strategy={reportStrategy}; {sceneSummary}");
            bot.CmdReportDeadBody(victim);
            _memory.RecordAction(bot, "report", $"reported body of {victim.PlayerName}({victim.PlayerId})");
            _log.LogInfo($"DeepBot reported body through native RPC: bot={bot.Data?.PlayerName}, victim={victim.PlayerName}, location={_lastReportNode}.");
        }
        catch (Exception ex)
        {
            _log.LogWarning($"DeepBot body report RPC failed: bot={bot.Data?.PlayerName}, victim={bodyId.Value}, error={ex.Message}");
        }
    }

    private bool TryChooseBodyReportPlan(
        PlayerControl bot,
        byte bodyId,
        int nearbyObservers,
        out float observationDelay,
        out string strategy)
    {
        var personality = BotPersonalityCatalog.ForPlayer(bot.PlayerId);
        var reactionBias = Mathf.Lerp(0.5f, -0.35f, personality.EmergencyResponsiveness);
        if (TorRoleAdapter.TryGetRole(bot, out var role))
        {
            if (role.Name is "Vulture" or "Janitor" or "Cleaner")
            {
                observationDelay = 0f;
                strategy = $"{role.Name} preserves the body for its role ability instead of reporting";
                return false;
            }

            if (role.IsNeutral)
            {
                var meetingObjective = role.Name is "Jester" or "Prosecutor" or "Lawyer";
                var shouldReport = meetingObjective ||
                    nearbyObservers > 0 && StableBodyReportChoice(bot.PlayerId, bodyId, 48);
                observationDelay = UnityEngine.Random.Range(2.6f, 4.8f) + reactionBias;
                strategy = meetingObjective
                    ? $"neutral {role.Name} deliberately creates a meeting that can advance its objective"
                    : shouldReport
                        ? $"neutral {role.Name} reports for cover because another player can observe the scene"
                        : $"neutral {role.Name} avoids advancing crew information without objective value";
                return shouldReport;
            }

            if (role.IsImpostorTeam)
            {
                var chance = GetImpostorBodyReportChance(nearbyObservers);
                var shouldReport = StableBodyReportChoice(bot.PlayerId, bodyId, chance);
                observationDelay = UnityEngine.Random.Range(3.0f, 5.4f) + reactionBias;
                strategy = shouldReport
                    ? $"impostor self-report chosen for cover with {nearbyObservers} visible scene observers"
                    : nearbyObservers == 0
                        ? "impostor leaves an unwitnessed body instead of creating an unnecessary meeting"
                        : "impostor delays or leaves rather than risk an implausible self-report";
                return shouldReport;
            }
        }

        if (IsImpostor(bot))
        {
            var chance = GetImpostorBodyReportChance(nearbyObservers);
            var shouldReport = StableBodyReportChoice(bot.PlayerId, bodyId, chance);
            observationDelay = UnityEngine.Random.Range(3.0f, 5.4f) + reactionBias;
            strategy = shouldReport
                ? $"base impostor self-report chosen for cover with {nearbyObservers} visible scene observers"
                : nearbyObservers == 0
                    ? "base impostor leaves an unwitnessed body instead of creating an unnecessary meeting"
                    : "base impostor delays or leaves rather than risk an implausible self-report";
            return shouldReport;
        }

        observationDelay = UnityEngine.Random.Range(2.4f, 4.6f) + reactionBias;
        strategy = "crew pauses to inspect who is near the body before reporting";
        return true;
    }

    private static bool StableBodyReportChoice(byte botId, byte bodyId, int chancePercent)
    {
        var bucket = (botId * 37 + bodyId * 17 + Mathf.FloorToInt(Time.time / 12f) * 11) % 100;
        return bucket < Math.Clamp(chancePercent, 0, 100);
    }

    private static int GetImpostorBodyReportChance(int nearbyObservers)
    {
        return nearbyObservers switch
        {
            >= 2 => 68,
            1 => 38,
            _ => 10
        };
    }

    private static int CountVisibleLivingPlayersNearBody(PlayerControl observer, DeadBody body)
    {
        var observerPosition = observer.GetTruePosition();
        var vision = BotPerceptionPolicy.GetCurrentVisionDistance(observer);
        var count = 0;
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (!player ||
                player.PlayerId == observer.PlayerId ||
                player.Data is null ||
                player.Data.IsDead ||
                player.Data.Disconnected ||
                BotPerceptionPolicy.IsConcealedByVent(player))
            {
                continue;
            }

            var playerPosition = player.GetTruePosition();
            if (Vector2.Distance(observerPosition, playerPosition) <= vision &&
                Vector2.Distance(body.TruePosition, playerPosition) <= vision + 1f &&
                !PhysicsHelpers.AnythingBetween(observerPosition, playerPosition, Constants.ShipOnlyMask, false))
            {
                count++;
            }
        }

        return count;
    }

    private static string BuildBodySceneSummary(PlayerControl observer, DeadBody body)
    {
        var observerPosition = observer.GetTruePosition();
        var vision = BotPerceptionPolicy.GetCurrentVisionDistance(observer);
        var visible = new List<string>();
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (!player ||
                player.PlayerId == observer.PlayerId ||
                player.Data is null ||
                player.Data.IsDead ||
                player.Data.Disconnected ||
                BotPerceptionPolicy.IsConcealedByVent(player))
            {
                continue;
            }

            var playerPosition = player.GetTruePosition();
            var observerDistance = Vector2.Distance(observerPosition, playerPosition);
            if (observerDistance > vision ||
                PhysicsHelpers.AnythingBetween(observerPosition, playerPosition, Constants.ShipOnlyMask, false))
            {
                continue;
            }

            visible.Add(
                $"{player.Data.PlayerName}({player.PlayerId}) observerDistance={observerDistance:0.0}, " +
                $"bodyDistance={Vector2.Distance(body.TruePosition, playerPosition):0.0}");
        }

        return visible.Count == 0
            ? "no other living player remained personally visible around the body"
            : $"personally visible near body: {string.Join("; ", visible)}";
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

            if (state.DecisionCompleted &&
                !state.DecisionApplied &&
                Time.time >= state.DecisionReadyAt)
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
                Time.time >= state.SpeakAt &&
                Time.time >= _nextMeetingLlmRequestAt)
            {
                _nextMeetingLlmRequestAt = Time.time + MeetingLlmRequestSpacingSeconds;
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

        RepairVirtualMeetingVotesAndCompletion();
    }

    private void RepairVirtualMeetingVotesAndCompletion()
    {
        var meeting = MeetingHud.Instance;
        if (!meeting || Time.time < _nextMeetingVoteRepairAt)
        {
            return;
        }

        _nextMeetingVoteRepairAt = Time.time + 0.5f;
        foreach (var area in meeting.playerStates)
        {
            if (!area)
            {
                continue;
            }

            var actuallyDead = IsActuallyDeadForMeeting(area.TargetPlayerId);
            if (actuallyDead && !area.AmDead)
            {
                area.SetDead(area.DidReport, true);
                _log.LogWarning(
                    $"DeepBot repaired stale meeting death state: playerId={area.TargetPlayerId}, " +
                    $"reported={area.DidReport}.");
            }
        }

        foreach (var bot in EnumerateDeepBots().Where(IsAlive))
        {
            var state = GetState(bot);
            if (!state.Voted ||
                !state.PendingNativeVoteId.HasValue ||
                Time.time - state.VoteCommandSentAt < 0.9f ||
                meeting.DidVote(bot.PlayerId))
            {
                continue;
            }

            var voteId = state.PendingNativeVoteId.Value;
            meeting.CastVote(bot.PlayerId, voteId);
            meeting.CheckForEndVoting();
            state.VoteCommandSentAt = Time.time + 30f;
            _log.LogWarning(
                $"DeepBot repaired unacknowledged virtual vote through native host CastVote: " +
                $"bot={bot.Data?.PlayerName}({bot.PlayerId}), vote={(voteId == SkipVoteId ? "skip" : voteId.ToString())}.");
        }

        if (meeting.CurrentState is MeetingHud.VoteStates.Results or MeetingHud.VoteStates.Proceeding)
        {
            return;
        }

        var unresolved = meeting.playerStates.ToArray()
            .Where(area => area &&
                           !area.DidVote &&
                           !area.AmDead &&
                           !IsActuallyDeadForMeeting(area.TargetPlayerId))
            .Select(area => area.TargetPlayerId)
            .ToArray();
        if (unresolved.Length > 0)
        {
            return;
        }

        _meetingCompletionChecks++;
        meeting.CheckForEndVoting();
        _nextMeetingVoteRepairAt = Time.time + 1.25f;
        _log.LogInfo(
            $"DeepBot native meeting completion check requested: meeting={_meetingSerial}, " +
            $"attempt={_meetingCompletionChecks}, allDeadOrVoted=true.");
    }

    private static bool IsActuallyDeadForMeeting(byte playerId)
    {
        var gamePlayer = GameData.Instance?.GetPlayerById(playerId);
        if (gamePlayer is null || gamePlayer.IsDead || gamePlayer.Disconnected)
        {
            return true;
        }

        var control = PlayerControl.AllPlayerControls.ToArray()
            .FirstOrDefault(player => player && player.PlayerId == playerId);
        if (control is null || !control || control.Data is null)
        {
            return true;
        }

        var data = control.Data;
        return data.IsDead ||
               data.Disconnected ||
               data.RoleType is RoleTypes.CrewmateGhost or RoleTypes.ImpostorGhost;
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
        state.DecisionReadyAt = 0f;
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
        state.PendingNativeVoteId = null;
        state.VoteCommandSentAt = 0f;
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
        var deliberationEndsAt = Time.time + GetDecisionApplyDelay(bot.PlayerId, state.DecisionRounds);
        state.DecisionReadyAt = Math.Min(
            deliberationEndsAt,
            Math.Max(Time.time, state.ForceVoteAt - 0.8f));
        var prompt = BuildMeetingPrompt(bot, state, config);
        _log.LogInfo(
            $"DeepBot meeting API queued: meeting={_meetingSerial}, bot={bot.Data?.PlayerName}({bot.PlayerId}), " +
            $"round={state.DecisionRounds + 1}, transcriptVersion={transcriptVersion}, " +
            $"team={(IsImpostor(bot) ? "impostor" : "crewmate")}, model={config.Model.Value}, memoryEvents={config.MeetingMemoryEvents.Value}, " +
            $"deliberateFor={Math.Max(0f, state.DecisionReadyAt - Time.time):0.0}s.");

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
            .Where(player => !TorRoleAdapter.ShouldProtectMeetingTarget(bot, player))
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
        var analyzedNewHumanStatement =
            state.PendingHumanTranscriptVersion > state.LastAnalyzedHumanTranscriptVersion;
        if (state.MeetingDecision is not null)
        {
            state.MeetingDecision = EnforceProtectedAllyMeetingDecision(bot, state.MeetingDecision);
        }
        var receivedDecision = state.PendingMeetingDecision;
        if (receivedDecision is not null)
        {
            receivedDecision = EnforceProtectedAllyMeetingDecision(bot, receivedDecision);
            receivedDecision = StabilizeMeetingDecision(bot, state, receivedDecision);
            receivedDecision = EnforceGroundedMeetingDecision(bot, state, receivedDecision);
            receivedDecision = EnforceCrossMeetingSuspicionContinuity(bot, state, receivedDecision);
            receivedDecision = EnforceProtectedAllyMeetingDecision(bot, receivedDecision);
        }
        if (TryGetActionableWitnessedKiller(bot, out var witnessedKiller))
        {
            receivedDecision = new BotMeetingDecision(
                $"我亲眼看到{witnessedKiller.Data!.PlayerName}杀了人，这能证明其拥有杀人能力；我会投他。",
                witnessedKiller.PlayerId,
                false,
                $"Hard eyewitness evidence: personally witnessed playerId={witnessedKiller.PlayerId} kill.",
                1f,
                witnessedKiller.PlayerId,
                "suspect");
            _log.LogInfo(
                $"DeepBot meeting eyewitness override applied: meeting={_meetingSerial}, " +
                $"bot={bot.Data?.PlayerName}({bot.PlayerId}), killer={witnessedKiller.Data.PlayerName}({witnessedKiller.PlayerId}).");
        }
        // A transient API failure must not erase a valid conclusion from an
        // earlier round.  That was the main reason bots reverted to rules/skip
        // at the hard voting deadline.
        if (receivedDecision is not null)
        {
            state.MeetingDecision = receivedDecision;
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
            (state.MessagesSent < MaxAutonomousMeetingMessagesPerBot || analyzedNewHumanStatement) &&
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
            state.SpeakAt = Time.time + GetMeetingThoughtDelay(
                bot.PlayerId,
                false,
                false,
                state.DecisionRounds);
        }
    }

    private void SendMeetingLine(PlayerControl bot, SocialState state, string line, string source)
    {
        state.Spoken = true;
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        line = RewriteProtectedAllyDisclosure(bot, line, out var allyDisclosureRewritten);
        if (allyDisclosureRewritten)
        {
            source += "-ally-secrecy-guard";
        }

        line = EnforcePrivateEvidenceBoundary(bot, line, out var evidenceRewritten);
        if (evidenceRewritten)
        {
            source += "-evidence-guard";
        }

        line = RewriteImpossibleLivingPlayerClaim(bot, line, out var impossibleClaimRewritten);
        if (impossibleClaimRewritten)
        {
            source += "-living-state-guard";
        }

        line = RewriteUnsafeSpeakerAttribution(bot, state, line, out var attributionRewritten);
        if (attributionRewritten)
        {
            source += "-speaker-attribution-guard";
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            return;
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

    private BotMeetingDecision EnforceProtectedAllyMeetingDecision(
        PlayerControl bot,
        BotMeetingDecision decision)
    {
        var targetId = GetDecisionTarget(decision);
        var voteTarget = targetId.HasValue ? FindPlayer(targetId.Value) : null;
        var protectedVote = voteTarget is not null &&
                            TorRoleAdapter.ShouldProtectMeetingTarget(bot, voteTarget);
        var protectedMention = string.IsNullOrWhiteSpace(decision.Message)
            ? null
            : EnumerateLivingPlayers().FirstOrDefault(player =>
                player.PlayerId != bot.PlayerId &&
                TorRoleAdapter.ShouldProtectMeetingTarget(bot, player) &&
                MentionsPlayer(decision.Message!, player));
        if (!ShouldBlockProtectedAllyDisclosure(protectedMention is not null, protectedVote))
        {
            return decision;
        }

        var protectedPlayer = voteTarget is not null && protectedVote ? voteTarget : protectedMention;
        _log.LogWarning(
            $"DeepBot protected-ally meeting guard blocked disclosure/vote: meeting={_meetingSerial}, " +
            $"bot={bot.Data?.PlayerName}({bot.PlayerId}), ally={protectedPlayer?.Data?.PlayerName}({protectedPlayer?.PlayerId}), " +
            $"messageMention={protectedMention is not null}, voteTarget={protectedVote}.");
        return decision with
        {
            Message = protectedMention is null
                ? decision.Message
                : "这条说法没有给出可核对的现场目击，我先看尸体附近的公开线索。",
            VotePlayerId = protectedVote ? null : decision.VotePlayerId,
            SkipVote = protectedVote || decision.SkipVote,
            Reason = "Protected ally guard: private teammate identity or ability knowledge cannot be used for public accusation or exile.",
            Confidence = protectedVote ? Mathf.Min(decision.Confidence, 0.45f) : decision.Confidence,
            FollowPlayerId = protectedVote ? null : decision.FollowPlayerId,
            FollowIntent = protectedVote ? "none" : decision.FollowIntent
        };
    }

    private string RewriteProtectedAllyDisclosure(PlayerControl bot, string line, out bool rewritten)
    {
        rewritten = false;
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var protectedMention = EnumerateLivingPlayers().FirstOrDefault(player =>
            player.PlayerId != bot.PlayerId &&
            TorRoleAdapter.ShouldProtectMeetingTarget(bot, player) &&
            MentionsPlayer(line, player));
        if (!ShouldBlockProtectedAllyDisclosure(protectedMention is not null, false))
        {
            return line;
        }

        rewritten = true;
        _log.LogWarning(
            $"DeepBot protected-ally speech guard removed public ally disclosure: meeting={_meetingSerial}, " +
            $"bot={bot.Data?.PlayerName}({bot.PlayerId}), ally={protectedMention!.Data?.PlayerName}({protectedMention.PlayerId}), " +
            $"original={line}.");
        return "这条说法没有给出可核对的现场目击，我先看尸体附近的公开线索。";
    }

    private static bool ShouldBlockProtectedAllyDisclosure(
        bool protectedAllyMentioned,
        bool protectedAllyVote)
    {
        return protectedAllyMentioned || protectedAllyVote;
    }

    private string RewriteUnsafeSpeakerAttribution(
        PlayerControl bot,
        SocialState state,
        string line,
        out bool rewritten)
    {
        rewritten = false;
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        // A model can merge two correctly-labelled transcript entries into a
        // fabricated "you just said ..." sentence. Do not publish free-form
        // paraphrases whose speaker cannot be mechanically verified. Fall back
        // to this bot's own sighting or its already-validated current vote.
        var unsafeAttribution = new[]
        {
            "你刚说", "你刚才说", "你之前说", "你上轮说",
            "你又说", "你说过", "你声称", "你提到"
        };
        if (!unsafeAttribution.Any(marker =>
                line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return line;
        }

        rewritten = true;
        if (_memory.TryGetLatestWitnessedKiller(bot.PlayerId, out var killerId))
        {
            var killer = FindPlayer(killerId);
            if (killer?.Data is not null && !killer.Data.IsDead && !killer.Data.Disconnected)
            {
                return $"我亲眼看到{killer.Data.PlayerName}杀人，我投他。";
            }
        }

        var voteId = state.MeetingDecision?.VotePlayerId;
        if (voteId.HasValue)
        {
            var target = voteId.Value is >= byte.MinValue and <= byte.MaxValue
                ? FindPlayer((byte)voteId.Value)
                : null;
            if (target?.Data is not null && !target.Data.IsDead && !target.Data.Disconnected)
            {
                return $"我目前仍偏向{target.Data.PlayerName}，这轮没有新的亲眼目击让我改票。";
            }
        }

        // Silence is safer than inventing which public speaker owned a claim.
        return string.Empty;
    }

    private string RewriteImpossibleLivingPlayerClaim(PlayerControl bot, string line, out bool rewritten)
    {
        rewritten = false;
        if (!bot || bot.Data is null || bot.Data.IsDead || string.IsNullOrWhiteSpace(line))
        {
            return line;
        }

        var impossibleSelfDeathPhrases = new[]
        {
            "目睹我死亡", "看到我死亡", "看见我死亡", "说我死了", "我已经死了"
        };
        var namesSelfAsDead = line.Contains(bot.Data.PlayerName, StringComparison.OrdinalIgnoreCase) &&
                              (line.Contains($"{bot.Data.PlayerName}死亡", StringComparison.OrdinalIgnoreCase) ||
                               line.Contains($"{bot.Data.PlayerName}死了", StringComparison.OrdinalIgnoreCase));
        if (!namesSelfAsDead &&
            !impossibleSelfDeathPhrases.Any(phrase => line.Contains(phrase, StringComparison.Ordinal)))
        {
            return line;
        }

        rewritten = true;
        var previous = _transcript.LastOrDefault(entry => entry.PlayerId != bot.PlayerId);
        var replacement = previous is null
            ? "我还活着，当前公开信息里没有我的死亡事件。"
            : $"我还活着。{previous.Name}刚才说的是位置或路线，不是我的死亡信息。";
        _log.LogWarning(
            $"DeepBot meeting living-state guard rewrote impossible self-death claim: " +
            $"bot={bot.Data.PlayerName}({bot.PlayerId}), original={line}, replacement={replacement}");
        return replacement;
    }

    private string EnforcePrivateEvidenceBoundary(PlayerControl bot, string line, out bool rewritten)
    {
        rewritten = false;
        var mustConcealHiddenActions = IsImpostor(bot) ||
            TorRoleAdapter.TryGetRole(bot, out var speakingRole) && speakingRole.IsNeutral;
        if (BotBehaviorPolicy.ShouldRewriteSelfIncriminatingSecret(line, mustConcealHiddenActions))
        {
            rewritten = true;
            var secrecyReplacement = TryGetActionableWitnessedKiller(bot, out var witnessedKiller)
                ? $"我亲眼看到{witnessedKiller.Data!.PlayerName}杀了人，我会投他。"
                : "这轮我没亲眼看清是谁动手，先核对具体路线和时间。";
            _log.LogWarning(
                $"DeepBot meeting secrecy guard rewrote self-incriminating line: " +
                $"bot={bot.Data?.PlayerName}({bot.PlayerId}), role={(mustConcealHiddenActions ? "hidden" : "public")}, " +
                $"original={line}, replacement={secrecyReplacement}");
            return secrecyReplacement;
        }

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
        var mentionedPlayers = EnumeratePlayers()
            .Where(player => MentionsPlayer(humanText, player))
            .ToArray();
        var target = ResolveClaimTargets(
                humanText,
                mentionedPlayers,
                IsSupportiveClaim(humanText) || IsExplicitAccusation(humanText) || IsConcreteEvidenceClaim(humanText))
            .FirstOrDefault() ?? mentionedPlayers.FirstOrDefault();
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

    private static float GetMeetingThoughtDelay(
        byte playerId,
        bool directlyAddressed,
        bool urgentEvidence,
        int completedRounds)
    {
        var personality = BotPersonalityCatalog.ForPlayer(playerId).Name;
        var (minimum, maximum) = GetMeetingThoughtWindow(
            personality,
            directlyAddressed,
            urgentEvidence,
            completedRounds);
        return UnityEngine.Random.Range(minimum, maximum);
    }

    private static (float Minimum, float Maximum) GetMeetingThoughtWindow(
        string personality,
        bool directlyAddressed,
        bool urgentEvidence,
        int completedRounds)
    {
        var window = personality switch
        {
            "急性子" => (2.8f, 4.8f),
            "认真派" => (4.0f, 6.4f),
            "社交派" => (3.5f, 5.8f),
            "谨慎派" => (5.0f, 7.8f),
            "懒散派" => (6.0f, 9.0f),
            _ => (3.8f, 6.8f)
        };
        var urgencyScale = (directlyAddressed ? 0.78f : 1f) * (urgentEvidence ? 0.88f : 1f);
        var roundDelay = Math.Min(Math.Max(completedRounds, 0), 3) * 0.35f;
        return (
            window.Item1 * urgencyScale + roundDelay,
            window.Item2 * urgencyScale + roundDelay);
    }

    private static float GetDecisionApplyDelay(byte playerId, int completedRounds)
    {
        var personality = BotPersonalityCatalog.ForPlayer(playerId).Name;
        var window = personality switch
        {
            "急性子" => (1.4f, 2.4f),
            "认真派" => (2.0f, 3.5f),
            "社交派" => (1.7f, 3.0f),
            "谨慎派" => (2.6f, 4.3f),
            "懒散派" => (3.2f, 5.2f),
            _ => (2.0f, 3.8f)
        };
        var roundDelay = Math.Min(Math.Max(completedRounds, 0), 3) * 0.2f;
        return UnityEngine.Random.Range(window.Item1 + roundDelay, window.Item2 + roundDelay);
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
        if (TryGetActionableWitnessedKiller(bot, out var witnessedKiller))
        {
            return $"我亲眼看到{witnessedKiller!.Data!.PlayerName}杀了人，这能证明其拥有杀人能力；我会投他。";
        }

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

    private bool TryGetActionableWitnessedKiller(PlayerControl bot, out PlayerControl witnessedKiller)
    {
        witnessedKiller = null!;
        var hasWitnessedKiller =
            _memory.TryGetLatestWitnessedKiller(bot.PlayerId, out var witnessedKillerId);
        var candidate = hasWitnessedKiller
            ? FindPlayer(witnessedKillerId)
            : null;
        var protectedAlly =
            candidate is not null &&
            TorRoleAdapter.ShouldProtectMeetingTarget(bot, candidate);
        if (!BotBehaviorPolicy.ShouldUseWitnessedKillerInMeeting(
                hasWitnessedKiller,
                candidate is not null && IsAlive(candidate),
                candidate?.PlayerId == bot.PlayerId,
                protectedAlly))
        {
            return false;
        }

        witnessedKiller = candidate!;
        return true;
    }

    private void SubmitVote(PlayerControl bot, SocialState state)
    {
        if (!MeetingHud.Instance || MeetingHud.Instance.DidVote(bot.PlayerId))
        {
            state.Voted = true;
            return;
        }

        if (TryUseEvidenceBackedGuesserShot(bot, out var guesserOutcome))
        {
            _memory.RecordAction(bot, "meeting_ability", guesserOutcome);
            _log.LogInfo($"DeepBot TOR evidence-backed Guesser ability used: bot={bot.Data?.PlayerName}, outcome={guesserOutcome}.");
            if (bot.Data is null || bot.Data.IsDead)
            {
                state.Voted = true;
                return;
            }
        }

        var voteId = ChooseVote(bot, state);
        try
        {
            MeetingHud.Instance.CmdCastVote(bot.PlayerId, voteId);
            state.PendingNativeVoteId = voteId;
            state.VoteCommandSentAt = Time.time;
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

    private bool TryUseEvidenceBackedGuesserShot(PlayerControl bot, out string outcome)
    {
        outcome = string.Empty;
        if (!TorRoleAdapter.TryGetRole(bot, out var role) ||
            role.Name is not ("NiceGuesser" or "EvilGuesser"))
        {
            return false;
        }

        var candidate = EnumerateLivingPlayers()
            .Where(player => player.PlayerId != bot.PlayerId)
            .Where(player => !TorRoleAdapter.ShouldProtectMeetingTarget(bot, player))
            .Select(player => new
            {
                Player = player,
                Evidence = _memory.GetPersonalRoleEvidence(bot.PlayerId, player.PlayerId)
            })
            .Where(item => item.Evidence.InferredRoleName is not null &&
                           TorRoleAdapter.TryGetPublicRoleAlignment(item.Evidence.InferredRoleName, out var alignment) &&
                           BotBehaviorPolicy.ShouldAttemptGuesserInference(
                               role.Name,
                               alignment,
                               item.Evidence.InferenceConfidence,
                               item.Evidence.LastObservedSecondsAgo))
            .OrderByDescending(item => item.Evidence.InferenceConfidence)
            .ThenByDescending(item => item.Evidence.HostilityScore)
            .ThenBy(item => item.Player.PlayerId)
            .FirstOrDefault();
        return candidate is not null &&
               TorRoleAdapter.TryUseEvidenceBackedGuesserShot(
                   bot,
                   candidate.Player,
                   candidate.Evidence.InferredRoleName!,
                   candidate.Evidence.InferenceConfidence,
                   out outcome);
    }

    private byte ChooseVote(PlayerControl bot, SocialState state)
    {
        if (TorRoleAdapter.TryGetStrategicMeetingVoteTarget(bot, out var strategicTargetId))
        {
            return strategicTargetId;
        }

        var hasWitnessedKiller =
            _memory.TryGetLatestWitnessedKiller(bot.PlayerId, out var witnessedKillerId);
        if (hasWitnessedKiller)
        {
            var witnessedKiller = FindPlayer(witnessedKillerId);
            var protectedAlly =
                witnessedKiller is not null &&
                TorRoleAdapter.ShouldProtectMeetingTarget(bot, witnessedKiller);
            if (BotBehaviorPolicy.ShouldUseWitnessedKillerInMeeting(
                    true,
                    witnessedKiller is not null && IsAlive(witnessedKiller),
                    witnessedKiller?.PlayerId == bot.PlayerId,
                    protectedAlly))
            {
                return witnessedKiller!.PlayerId;
            }
        }

        var personality = BotPersonalityCatalog.ForPlayer(bot.PlayerId);
        var confidenceThreshold = BotBehaviorPolicy.GetMeetingVoteConfidenceThreshold(personality.VoteBoldness);
        var decision = state.MeetingDecision;
        if (decision is not null)
        {
            if (!decision.SkipVote &&
                decision.VotePlayerId.HasValue &&
                decision.VotePlayerId.Value is >= byte.MinValue and <= byte.MaxValue)
            {
                var target = FindPlayer((byte)decision.VotePlayerId.Value);
                var validTarget = target is not null &&
                                  IsAlive(target) &&
                                  target.PlayerId != bot.PlayerId &&
                                  !TorRoleAdapter.ShouldProtectMeetingTarget(bot, target) &&
                                  !ShouldProtectClaimedJester(bot, target.PlayerId);
                if (target is not null)
                {
                    var personallyWitnessed = hasWitnessedKiller && witnessedKillerId == target.PlayerId;
                    var crewAligned = IsCrewAligned(bot);
                    var groundedThreshold = GetCrewDecisionThreshold(personality);
                    var groundedEvidence = crewAligned
                        ? GetGroundedEvidenceScore(bot, state, target)
                        : 0f;
                    if (BotBehaviorPolicy.ShouldCommitMeetingCandidate(
                            validTarget,
                            decision.SkipVote,
                            crewAligned,
                            personallyWitnessed,
                            decision.Confidence,
                            confidenceThreshold,
                            groundedEvidence,
                            groundedThreshold))
                    {
                        return target.PlayerId;
                    }
                }
            }
        }

        var candidates = EnumerateLivingPlayers()
            .Where(player => player.PlayerId != bot.PlayerId)
            .Where(player => !TorRoleAdapter.ShouldProtectMeetingTarget(bot, player))
            .Where(player => !ShouldProtectClaimedJester(bot, player.PlayerId))
            .ToArray();
        if (candidates.Length == 0)
        {
            return SkipVoteId;
        }

        var accused = candidates
            .Select(player => new
            {
                Player = player,
                Score = GetGroundedEvidenceScore(bot, state, player)
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
            .Where(player => !TorRoleAdapter.ShouldProtectMeetingTarget(bot, player))
            .Select(player => new
            {
                Player = player,
                PublicClaimScore = state.BeliefScores.GetValueOrDefault(player.PlayerId),
                PriorSuspicion = state.PersistentPublicSuspicion.GetValueOrDefault(player.PlayerId),
                GroundedScore = GetGroundedEvidenceScore(bot, state, player),
                Contact = _memory.GetPersonalContactEvidence(bot.PlayerId, player.PlayerId),
                RoleEvidence = _memory.GetPersonalRoleEvidence(bot.PlayerId, player.PlayerId),
                PublicClaims = _accusations.GetValueOrDefault(player.PlayerId)
            })
            .OrderByDescending(item => item.GroundedScore)
            .ThenByDescending(item => item.PublicClaims)
            .ThenBy(item => item.Player.PlayerId)
            .Take(5)
            .Select(item =>
                $"{item.Player.Data!.PlayerName}({item.Player.PlayerId}): grounded_suspicion={item.GroundedScore:0.00}, " +
                $"current_public_claim_score={item.PublicClaimScore:0.00}, " +
                $"carried_prior_suspicion={item.PriorSuspicion:0.00}, independent_accusers={item.PublicClaims}, " +
                $"personal_contact={(item.Contact.IsRecent ? $"recent;continuous={item.Contact.ContinuousSeconds:0.0}s;total={item.Contact.TotalVisibleSeconds:0.0}s;lastSeen={item.Contact.LastSeenSecondsAgo:0.0}s" : "not_recent")}, " +
                $"personal_role_evidence={(item.RoleEvidence.HasObservation ? $"hostility={item.RoleEvidence.HostilityScore:0.00};inferredRole={item.RoleEvidence.InferredRoleName ?? "unknown"};inferenceConfidence={item.RoleEvidence.InferenceConfidence:0.00};age={item.RoleEvidence.LastObservedSecondsAgo:0.0}s;{item.RoleEvidence.Summary}" : "none")}, " +
                $"jester_exile_risk={_publicJesterClaims.Contains(item.Player.PlayerId)}")
            .ToArray();

        var earlier = state.MeetingDecision is null
            ? "No valid earlier model conclusion."
            : $"Last valid conclusion: vote={DescribeVote(state.MeetingDecision)}, " +
              $"confidence={state.MeetingDecision.Confidence:0.00}, reason={state.MeetingDecision.Reason ?? "none"}.";
        var previousMeeting = _memory.TryGetPostMeetingIntent(bot.PlayerId, out var priorIntent) &&
                              priorIntent.MeetingSerial < _meetingSerial
            ? $" Previous meeting belief (continuity only, not new proof): vote=" +
              $"{(priorIntent.VotedPlayerId.HasValue ? priorIntent.VotedPlayerId.Value.ToString() : "skip")}, " +
              $"follow={priorIntent.FollowIntent}:{(priorIntent.FollowPlayerId.HasValue ? priorIntent.FollowPlayerId.Value.ToString() : "none")}, " +
              $"confidence={priorIntent.Confidence:0.00}."
            : string.Empty;
        return lines.Length == 0
            ? earlier + previousMeeting + " No public candidate evidence has accumulated."
            : earlier + previousMeeting + "\n" + string.Join("\n", lines) +
              "\nCarried prior suspicion is a fallible cross-meeting belief, not new proof: keep it unless " +
              "new counter-evidence weakens it, and let personality determine willingness to act on it. " +
              "A model conclusion never becomes new evidence; anger, confidence, repetition, and insults add zero evidence.";
    }

    private BotMeetingDecision EnforceGroundedMeetingDecision(
        PlayerControl bot,
        SocialState state,
        BotMeetingDecision decision)
    {
        var targetId = GetDecisionTarget(decision);
        var target = targetId.HasValue ? FindPlayer(targetId.Value) : null;
        if (target is null || !IsAlive(target))
        {
            return decision;
        }

        var personallyWitnessedTarget =
            _memory.TryGetLatestWitnessedKiller(bot.PlayerId, out var witnessedKillerId) &&
            witnessedKillerId == target.PlayerId;
        if (ShouldProtectClaimedJester(bot, target.PlayerId) && !personallyWitnessedTarget)
        {
            _log.LogWarning(
                $"DeepBot strategic vote guard blocked Jester-risk exile: meeting={_meetingSerial}, " +
                $"bot={bot.Data?.PlayerName}({bot.PlayerId}), target={target.Data?.PlayerName}({target.PlayerId}).");
            return decision with
            {
                Message = $"{target.Data!.PlayerName}自己说是小丑；没硬证据就投他等于送胜利，我不跟这票。",
                VotePlayerId = null,
                SkipVote = true,
                Reason = $"Faction win protected: public Jester self-claim by playerId={target.PlayerId} and no personal kill witness.",
                Confidence = Mathf.Min(decision.Confidence, 0.62f),
                FollowPlayerId = target.PlayerId,
                FollowIntent = "suspect"
            };
        }

        if (!IsCrewAligned(bot) || personallyWitnessedTarget)
        {
            return decision;
        }

        var personality = BotPersonalityCatalog.ForPlayer(bot.PlayerId);
        var groundedScore = GetGroundedEvidenceScore(bot, state, target);
        var threshold = GetCrewDecisionThreshold(personality);
        if (groundedScore >= threshold)
        {
            return decision;
        }

        var contact = _memory.GetPersonalContactEvidence(bot.PlayerId, target.PlayerId);
        var message = contact.IsRecent && contact.ContinuousSeconds >= 5f
            ? $"我刚连续看着{target.Data!.PlayerName}约{Mathf.RoundToInt(contact.ContinuousSeconds)}秒，那段没见他动手；这票先撤。"
            : $"对{target.Data!.PlayerName}现在只有口头指认，没到我的投票线。";
        _log.LogWarning(
            $"DeepBot grounded vote guard blocked unsupported crew vote: meeting={_meetingSerial}, " +
            $"bot={bot.Data?.PlayerName}({bot.PlayerId}), target={target.Data?.PlayerName}({target.PlayerId}), " +
            $"grounded={groundedScore:0.00}, threshold={threshold:0.00}, " +
            $"contactContinuous={contact.ContinuousSeconds:0.0}, contactAge={contact.LastSeenSecondsAgo:0.0}.");
        return decision with
        {
            Message = message,
            VotePlayerId = null,
            SkipVote = true,
            Reason = $"Grounded evidence {groundedScore:0.00} stayed below crew threshold {threshold:0.00}; model confidence is not evidence.",
            Confidence = Mathf.Min(decision.Confidence, 0.58f)
        };
    }

    private BotMeetingDecision EnforceCrossMeetingSuspicionContinuity(
        PlayerControl bot,
        SocialState state,
        BotMeetingDecision decision)
    {
        if (string.IsNullOrWhiteSpace(decision.Message) ||
            !ExonerationWords.Any(word => decision.Message.Contains(word, StringComparison.OrdinalIgnoreCase)))
        {
            return decision;
        }

        var unsupportedClear = EnumerateLivingPlayers()
            .Where(player => player.PlayerId != bot.PlayerId)
            .Where(player => MentionsPlayer(decision.Message!, player))
            .Select(player => new
            {
                Player = player,
                Prior = state.PersistentPublicSuspicion.GetValueOrDefault(player.PlayerId),
                Current = state.BeliefScores.GetValueOrDefault(player.PlayerId),
                RoleEvidence = _memory.GetPersonalRoleEvidence(bot.PlayerId, player.PlayerId)
            })
            .Where(item => ShouldBlockUnsupportedExoneration(
                item.Prior,
                item.Current,
                item.RoleEvidence.HostilityScore))
            .OrderByDescending(item => Mathf.Max(item.Prior, item.RoleEvidence.HostilityScore))
            .FirstOrDefault();
        if (unsupportedClear is null)
        {
            return decision;
        }

        var name = unsupportedClear.Player.Data?.PlayerName ?? $"{unsupportedClear.Player.PlayerId}号";
        _log.LogWarning(
            $"DeepBot unsupported exoneration blocked: meeting={_meetingSerial}, " +
            $"bot={bot.Data?.PlayerName}({bot.PlayerId}), target={name}({unsupportedClear.Player.PlayerId}), " +
            $"prior={unsupportedClear.Prior:0.00}, current={unsupportedClear.Current:0.00}, " +
            $"roleEvidence={unsupportedClear.RoleEvidence.HostilityScore:0.00}.");
        return decision with
        {
            Message = $"{name}上一轮的疑点还在；这轮没有新反证，我不会直接把他洗清。",
            FollowPlayerId = unsupportedClear.Player.PlayerId,
            FollowIntent = "suspect",
            Reason = $"Cross-meeting suspicion retained for playerId={unsupportedClear.Player.PlayerId}; no explicit counter-evidence supported exoneration.",
            Confidence = Mathf.Max(decision.Confidence, 0.58f)
        };
    }

    private static bool ShouldBlockUnsupportedExoneration(
        float carriedPrior,
        float currentPublicDelta,
        float personalHostility)
    {
        return (carriedPrior >= 0.34f || personalHostility >= 0.55f) &&
               currentPublicDelta > -0.18f;
    }

    private BotMeetingDecision StabilizeMeetingDecision(
        PlayerControl bot,
        SocialState state,
        BotMeetingDecision received)
    {
        var previous = state.MeetingDecision;
        if (previous is null || previous.Confidence < 0.55f)
        {
            return received;
        }

        var previousTargetId = GetDecisionTarget(previous);
        var receivedTargetId = GetDecisionTarget(received);
        if (!previousTargetId.HasValue || previousTargetId == receivedTargetId)
        {
            return received;
        }

        var previousTarget = FindPlayer(previousTargetId.Value);
        if (previousTarget is null || !IsAlive(previousTarget))
        {
            return received;
        }

        var personallyWitnessedNewTarget = receivedTargetId.HasValue &&
            _memory.TryGetLatestWitnessedKiller(bot.PlayerId, out var witnessedKillerId) &&
            witnessedKillerId == receivedTargetId.Value;
        var personality = BotPersonalityCatalog.ForPlayer(bot.PlayerId);
        var previousScore = GetGroundedEvidenceScore(bot, state, previousTarget!);
        var receivedScore = receivedTargetId.HasValue
            ? FindPlayer(receivedTargetId.Value) is { } receivedTarget
                ? GetGroundedEvidenceScore(bot, state, receivedTarget)
                : 0f
            : 0f;
        if (receivedTargetId.HasValue &&
            ShouldAcceptCandidateFlip(
                previous.Confidence,
                received.Confidence,
                previousScore,
                receivedScore,
                personality.SocialSuggestibility,
                personallyWitnessedNewTarget))
        {
            return received;
        }

        var targetName = previousTarget!.Data?.PlayerName ?? $"{previousTargetId.Value}号";
        _log.LogWarning(
            $"DeepBot meeting stance flip blocked: meeting={_meetingSerial}, bot={bot.Data?.PlayerName}({bot.PlayerId}), " +
            $"kept={previousTargetId.Value}, rejected={(receivedTargetId?.ToString() ?? "skip")}, " +
            $"previousConfidence={previous.Confidence:0.00}, receivedConfidence={received.Confidence:0.00}, " +
            $"previousEvidence={previousScore:0.00}, receivedEvidence={receivedScore:0.00}, " +
            $"personallyWitnessedNewTarget={personallyWitnessedNewTarget}.");
        return previous with
        {
            Message = $"我仍然更怀疑{targetName}；刚才只是重复指控，没有足够的新证据让我改票。",
            Reason = $"Stance preserved: no evidence delta justified changing from playerId={previousTargetId.Value}.",
            Confidence = Mathf.Max(previous.Confidence, Mathf.Min(received.Confidence, previous.Confidence + 0.05f))
        };
    }

    private static byte? GetDecisionTarget(BotMeetingDecision decision)
    {
        return !decision.SkipVote &&
               decision.VotePlayerId is >= byte.MinValue and <= byte.MaxValue
            ? (byte)decision.VotePlayerId.Value
            : null;
    }

    private static bool ShouldAcceptCandidateFlip(
        float previousConfidence,
        float receivedConfidence,
        float previousEvidence,
        float receivedEvidence,
        float socialSuggestibility,
        bool personallyWitnessedNewTarget)
    {
        if (personallyWitnessedNewTarget)
        {
            return true;
        }

        var suggestibility = Mathf.Clamp01(socialSuggestibility);
        var requiredEvidenceLead = Mathf.Lerp(0.48f, 0.18f, suggestibility);
        var requiredConfidenceGain = Mathf.Lerp(0.22f, 0.10f, suggestibility);
        return receivedEvidence >= previousEvidence + requiredEvidenceLead &&
               receivedConfidence >= previousConfidence - 0.05f ||
               receivedEvidence >= previousEvidence &&
               receivedConfidence >= previousConfidence + requiredConfidenceGain;
    }

    internal static void LogSelfTest(ManualLogSource log)
    {
        var repeatedCounterAccusationBlocked = !ShouldAcceptCandidateFlip(
            0.75f, 0.85f, 1.01f, 0.70f, 0.88f, false);
        var corroboratedEvidenceAllowsChange = ShouldAcceptCandidateFlip(
            0.65f, 0.82f, 0.70f, 1.20f, 0.55f, false);
        var witnessedKillAllowsChange = ShouldAcceptCandidateFlip(
            0.90f, 0.70f, 1.25f, 0.10f, 0.20f, true);
        var bareAccusationLowWeight = CalculatePublicClaimStrength(true, false) <= 0.25f;
        var concreteEyewitnessHigherWeight = CalculatePublicClaimStrength(true, true) >= 0.75f;
        var sustainedContactIsCounterEvidence = CalculateContactDefense(18f, 24f, 1f) >= 0.65f;
        var carriedSuspicionPersists = CalculateCarriedSuspicion(0f, 0.90f) >= 0.60f &&
                                       CalculateCarriedSuspicion(0.90f, 0f) >= 0.60f;
        var counterEvidenceReducesPrior = CalculateCarriedSuspicion(0.80f, -0.35f) <
                                          CalculateCarriedSuspicion(0.80f, 0f);
        var noEvidenceDoesNotErasePrior = CalculateCarriedSuspicion(0.80f, 0f) >= 0.70f;
        var firstPersonActionCountsWithoutRoom = IsConcreteEvidenceClaim("我亲眼看到粉色跳管了");
        var unsupportedExonerationBlocked = ShouldBlockUnsupportedExoneration(0.62f, 0f, 0f) &&
                                            !ShouldBlockUnsupportedExoneration(0.62f, -0.30f, 0f);
        var freshVentFallbackIsConcrete =
            BuildObservedActionFallback("粉色", "enter a vent", false).Contains("进管", StringComparison.Ordinal) &&
            !BuildObservedActionFallback("粉色", "enter a vent", false).Contains("还没", StringComparison.Ordinal);
        var personalityVoteThresholdsDiffer =
            GetCrewDecisionThreshold(BotPersonalityCatalog.ForPlayer(1)) <
            GetCrewDecisionThreshold(BotPersonalityCatalog.ForPlayer(4));
        var factionReportStrategyVaries =
            GetImpostorBodyReportChance(0) < GetImpostorBodyReportChance(1) &&
            GetImpostorBodyReportChance(1) < GetImpostorBodyReportChance(2);
        var urgentDirectWindow = GetMeetingThoughtWindow("急性子", true, true, 0);
        var cautiousWindow = GetMeetingThoughtWindow("谨慎派", false, false, 0);
        var deliberateReplies = urgentDirectWindow.Minimum >= 1.8f &&
                                cautiousWindow.Minimum > urgentDirectWindow.Minimum &&
                                cautiousWindow.Maximum > cautiousWindow.Minimum;
        var protectedAllyDisclosureBlocked =
            ShouldBlockProtectedAllyDisclosure(true, false) &&
            ShouldBlockProtectedAllyDisclosure(false, true) &&
            !ShouldBlockProtectedAllyDisclosure(false, false);
        var level = repeatedCounterAccusationBlocked &&
                    corroboratedEvidenceAllowsChange &&
                    witnessedKillAllowsChange &&
                    bareAccusationLowWeight &&
                    concreteEyewitnessHigherWeight &&
                    sustainedContactIsCounterEvidence &&
                    carriedSuspicionPersists &&
                    counterEvidenceReducesPrior &&
                    noEvidenceDoesNotErasePrior &&
                    firstPersonActionCountsWithoutRoom &&
                    unsupportedExonerationBlocked &&
                    freshVentFallbackIsConcrete &&
                    personalityVoteThresholdsDiffer &&
                    factionReportStrategyVaries &&
                    deliberateReplies &&
                    protectedAllyDisclosureBlocked
            ? "ok"
            : "error";
        log.LogInfo(
            $"DeepBot meeting stance self-test: level={level}, " +
            $"repeatedCounterAccusationBlocked={repeatedCounterAccusationBlocked}, " +
            $"corroboratedEvidenceAllowsChange={corroboratedEvidenceAllowsChange}, " +
            $"witnessedKillAllowsChange={witnessedKillAllowsChange}, " +
            $"bareAccusationLowWeight={bareAccusationLowWeight}, concreteEyewitnessHigherWeight={concreteEyewitnessHigherWeight}, " +
            $"sustainedContactIsCounterEvidence={sustainedContactIsCounterEvidence}, " +
            $"carriedSuspicionPersists={carriedSuspicionPersists}, counterEvidenceReducesPrior={counterEvidenceReducesPrior}, " +
            $"noEvidenceDoesNotErasePrior={noEvidenceDoesNotErasePrior}, " +
            $"firstPersonActionCountsWithoutRoom={firstPersonActionCountsWithoutRoom}, " +
            $"unsupportedExonerationBlocked={unsupportedExonerationBlocked}, " +
            $"freshVentFallbackIsConcrete={freshVentFallbackIsConcrete}, " +
            $"personalityVoteThresholdsDiffer={personalityVoteThresholdsDiffer}, " +
            $"factionReportStrategyVaries={factionReportStrategyVaries}, " +
            $"deliberateReplies={deliberateReplies}, protectedAllyDisclosureBlocked={protectedAllyDisclosureBlocked}, " +
            $"fastestReplyMin={urgentDirectWindow.Minimum:0.0}s.");
    }

    private string BuildContextualFallbackMeetingLine(PlayerControl bot, SocialState state)
    {
        var personality = BotPersonalityCatalog.ForPlayer(bot.PlayerId);
        var top = EnumerateLivingPlayers()
            .Where(player => player.PlayerId != bot.PlayerId)
            .Where(player => !TorRoleAdapter.ShouldProtectMeetingTarget(bot, player))
            .Select(player => new
            {
                Player = player,
                Score = GetGroundedEvidenceScore(bot, state, player),
                Current = state.BeliefScores.GetValueOrDefault(player.PlayerId),
                Prior = state.PersistentPublicSuspicion.GetValueOrDefault(player.PlayerId),
                RoleEvidence = _memory.GetPersonalRoleEvidence(bot.PlayerId, player.PlayerId)
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Player.PlayerId)
            .FirstOrDefault();
        if (top is not null && top.Score > 0.01f)
        {
            var name = top.Player.Data?.PlayerName ?? $"{top.Player.PlayerId}号";
            var ready = top.Score >= GetCrewDecisionThreshold(personality);
            if (top.RoleEvidence.HasObservation)
            {
                return BuildObservedActionFallback(name, top.RoleEvidence.Summary, ready);
            }

            if (top.Current > 0.01f)
            {
                if (_latestConcreteClaimsByTarget.TryGetValue(top.Player.PlayerId, out var concreteClaim))
                {
                    return BuildPublicConcreteClaimFallback(
                        personality,
                        concreteClaim.SourceName,
                        name,
                        concreteClaim.Action,
                        ready,
                        top.Prior > 0.12f);
                }

                return ready
                    ? $"刚才对{name}的具体指认影响了我的判断，我这票投他。"
                    : $"刚才有人指认{name}，我先记作嫌疑，不当成实锤。";
            }

            if (top.Prior <= 0.01f)
            {
                return string.Empty;
            }

            return ready
                ? personality.Name switch
                {
                    "急性子" => $"{name}前后不太对，我这票押他。",
                    "认真派" => $"{name}的说法没把疑点解释掉，我投他。",
                    "社交派" => $"我还是更怀疑{name}，这票按自己的判断来。",
                    "谨慎派" => $"我没完全确定，但{name}的嫌疑没有洗掉。",
                    "懒散派" => $"{name}最不对劲，我先投他。",
                    _ => $"我这轮更怀疑{name}，投他。"
                }
                : personality.Name switch
                {
                    "急性子" => $"{name}还是可疑，我先盯他，不算洗清。",
                    "社交派" => $"我还怀疑{name}，有反证就直接说。",
                    "谨慎派" => $"{name}的疑点还在，但这轮我先不定票。",
                    "懒散派" => $"{name}有点怪，我先记着。",
                    _ => $"我对{name}的怀疑还没消失。"
                };
        }

        // When the model is rate-limited and the bot has no concrete evidence,
        // silence is more human and safer than a procedural route-report
        // template.  This also prevents a fresh match from sounding as though
        // it inherited a previous meeting.
        return string.Empty;
    }

    private static string BuildObservedActionFallback(string name, string? summary, bool readyToVote)
    {
        var observation = summary ?? string.Empty;
        if (observation.Contains("vent", StringComparison.OrdinalIgnoreCase))
        {
            return readyToVote
                ? $"我看到{name}进管了；能跳管的不只内鬼，但我这轮倾向投他。"
                : $"我看到{name}进管了；先记作能跳管，不直接定成内鬼。";
        }

        if (observation.Contains("kill", StringComparison.OrdinalIgnoreCase) ||
            observation.Contains("murder", StringComparison.OrdinalIgnoreCase))
        {
            return $"我亲眼看到{name}杀人，我投他。";
        }

        return readyToVote
            ? $"我亲眼看到{name}用了可疑能力，这票我押他。"
            : $"我亲眼看到{name}用了特殊能力，先记下但不乱定职业。";
    }

    private static string BuildPublicConcreteClaimFallback(
        BotPersonalityProfile personality,
        string sourceName,
        string targetName,
        string action,
        bool readyToVote,
        bool matchesPrior)
    {
        var priorText = matchesPrior ? "，也和我之前的疑点对得上" : string.Empty;
        if (!readyToVote)
        {
            return personality.Name switch
            {
                "急性子" => $"{sourceName}说亲眼看到{targetName}{action}{priorText}；我先盯住{targetName}，但这不是我亲眼所见。",
                "社交派" => $"{sourceName}指认自己看到{targetName}{action}{priorText}；我会把这条算进判断，但还想听{targetName}回应。",
                "谨慎派" => $"{sourceName}说看到{targetName}{action}{priorText}；这是具体口供，不是我的亲眼证据，这轮先保留。",
                _ => $"{sourceName}说看到{targetName}{action}{priorText}；我记下了，但不会把转述当实锤。"
            };
        }

        return personality.Name switch
        {
            "急性子" => $"{sourceName}说亲眼看到{targetName}{action}{priorText}，我这票投{targetName}。",
            "认真派" => $"{sourceName}给出了对{targetName}{action}的具体目击{priorText}；结合现有疑点，我投{targetName}。",
            "社交派" => $"{sourceName}的目击把矛头指向{targetName}{priorText}，我倾向跟这条具体信息投{targetName}。",
            "谨慎派" => $"我没有亲眼看到，但{sourceName}对{targetName}{action}的描述足够具体{priorText}；这次我投{targetName}。",
            _ => $"{sourceName}说看到{targetName}{action}{priorText}，目前{targetName}最可疑，我投他。"
        };
    }

    private static string ClassifyConcreteClaimAction(string text)
    {
        if (text.Contains("杀", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("刀", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("kill", StringComparison.OrdinalIgnoreCase))
        {
            return "杀人";
        }

        if (text.Contains("跳管", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("进管", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("vent", StringComparison.OrdinalIgnoreCase))
        {
            return "进通风管";
        }

        if (text.Contains("变形", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("morph", StringComparison.OrdinalIgnoreCase))
        {
            return "变形";
        }

        return "使用特殊能力";
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

    private static bool IsSelfJesterClaim(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var firstPerson = text.Contains("我是", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("我就是", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("i am", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("i'm", StringComparison.OrdinalIgnoreCase);
        var jester = text.Contains("小丑", StringComparison.OrdinalIgnoreCase) ||
                     text.Contains("jester", StringComparison.OrdinalIgnoreCase);
        return firstPerson && jester;
    }

    private static bool IsConcreteEvidenceClaim(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] observationWords =
        [
            "亲眼", "我看到", "我看见", "当着我", "就在我旁边", "目击", "i saw", "witnessed"
        ];
        string[] actionWords =
        [
            "杀", "刀", "击杀", "跳管", "进管", "放炸弹", "放置", "变形", "隐身", "吃尸体", "清尸体",
            "kill", "vent", "bomb", "morph", "invisible"
        ];
        string[] contextWords =
        [
            "食堂", "电气", "仓库", "反应堆", "氧气", "医务", "导航", "通讯", "武器", "安保", "下引擎", "上引擎",
            "走廊", "门口", "去污", "温室", "发射台", "caf", "electrical", "storage", "reactor", "o2", "hallway"
        ];
        var observed = observationWords.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
        var action = actionWords.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
        // A first-person report of a concrete visible action is already an
        // eyewitness claim.  A room name or timestamp makes it easier to
        // cross-check, but its absence must not demote "I saw X kill/vent" to
        // an unsupported accusation.
        var contextualized = contextWords.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)) ||
                             text.Any(char.IsDigit);
        return observed && action && (contextualized || text.Length >= 4);
    }

    private static float CalculatePublicClaimStrength(bool explicitAccusation, bool concreteEvidence)
    {
        if (!explicitAccusation)
        {
            return concreteEvidence ? 0.34f : 0.10f;
        }

        return concreteEvidence ? 0.78f : 0.24f;
    }

    private static float CalculateContactDefense(
        float continuousSeconds,
        float totalVisibleSeconds,
        float lastSeenSecondsAgo)
    {
        if (lastSeenSecondsAgo < 0f || lastSeenSecondsAgo > 6f)
        {
            return 0f;
        }

        var continuousDefense = continuousSeconds switch
        {
            >= 18f => 0.72f,
            >= 10f => 0.52f,
            >= 5f => 0.28f,
            _ => 0f
        };
        var familiarityDefense = totalVisibleSeconds >= 35f ? 0.10f : 0f;
        return Mathf.Min(0.82f, continuousDefense + familiarityDefense);
    }

    private float GetGroundedEvidenceScore(PlayerControl bot, SocialState state, PlayerControl target)
    {
        var currentPublicClaimScore = state.BeliefScores.GetValueOrDefault(target.PlayerId);
        var carriedPriorSuspicion = state.PersistentPublicSuspicion.GetValueOrDefault(target.PlayerId);
        var contact = _memory.GetPersonalContactEvidence(bot.PlayerId, target.PlayerId);
        var contactDefense = CalculateContactDefense(
            contact.ContinuousSeconds,
            contact.TotalVisibleSeconds,
            contact.LastSeenSecondsAgo);
        var personalRoleEvidence = _memory.GetPersonalRoleEvidence(bot.PlayerId, target.PlayerId);
        // Recent companionship can refute an unsupported story about what the
        // target allegedly did during that same window. It can weaken, but not
        // silently erase, suspicion carried from an earlier meeting, and it
        // cannot erase an ability the observer personally saw.
        var personality = BotPersonalityCatalog.ForPlayer(bot.PlayerId);
        var subjectivePriorWeight = GetSubjectivePriorWeight(personality);
        var interpretedPublicSuspicion = Mathf.Max(
            0f,
            currentPublicClaimScore - contactDefense + carriedPriorSuspicion * subjectivePriorWeight);
        return Mathf.Max(interpretedPublicSuspicion, personalRoleEvidence.HostilityScore);
    }

    private bool ShouldProtectClaimedJester(PlayerControl bot, byte targetId)
    {
        if (!_publicJesterClaims.Contains(targetId))
        {
            return false;
        }

        if (!TorRoleAdapter.TryGetRole(bot, out var role))
        {
            return true;
        }

        return !role.IsNeutral;
    }

    private static bool IsCrewAligned(PlayerControl bot)
    {
        if (TorRoleAdapter.TryGetRole(bot, out var role))
        {
            return string.Equals(role.Alignment, "crewmate", StringComparison.OrdinalIgnoreCase);
        }

        return !IsImpostor(bot);
    }

    private static float GetRuleVoteThreshold(BotPersonalityProfile personality)
    {
        var subjectiveCommitment = Mathf.Clamp01(
            personality.VoteBoldness * 0.70f + personality.SocialSuggestibility * 0.30f);
        return Mathf.Lerp(
            1.20f,
            0.42f,
            subjectiveCommitment);
    }

    private static float GetCrewDecisionThreshold(BotPersonalityProfile personality)
    {
        // Bold and intuitive personalities are allowed to act on a strong,
        // explicitly fallible inference.  Cautious personalities still demand
        // substantially more grounded evidence; this avoids one universal
        // proof-chain threshold that made every crew bot skip alike.
        return Mathf.Clamp(GetRuleVoteThreshold(personality), 0.42f, 1.25f);
    }

    private static float GetSubjectivePriorWeight(BotPersonalityProfile personality)
    {
        var intuition = Mathf.Clamp01(
            personality.VoteBoldness * 0.65f + personality.SocialSuggestibility * 0.35f);
        return Mathf.Lerp(0.52f, 0.92f, intuition);
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

            var delay = GetMeetingThoughtDelay(bot.PlayerId, false, false, state.DecisionRounds);
            state.SpeakAt = Math.Min(
                Math.Max(state.SpeakAt, Time.time + delay),
                Math.Max(Time.time, state.ForceVoteAt - 1.5f));
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
            var latestDirectlyAddressesBot = MentionsPlayer(latest.Text, bot);
            var currentTarget = state.MeetingDecision?.VotePlayerId is >= byte.MinValue and <= byte.MaxValue
                ? FindPlayer((byte)state.MeetingDecision.VotePlayerId.Value)
                : null;
            return latestDirectlyAddressesBot ||
                   currentTarget is not null && MentionsPlayer(latest.Text, currentTarget) ||
                   IsExplicitAccusation(latest.Text) ||
                   IsSupportiveClaim(latest.Text) ||
                   IsSelfJesterClaim(latest.Text) ||
                   IsConcreteEvidenceClaim(latest.Text);
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
            GameRuleSettings.IsDeepBotSupportedMap() &&
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
        return TorRoleAdapter.IsImpostorTeam(player);
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

    private static PlayerControl[] ResolveClaimTargets(
        string text,
        PlayerControl[] mentionedPlayers,
        bool containsClaim)
    {
        if (!containsClaim || mentionedPlayers.Length <= 1)
        {
            return mentionedPlayers;
        }

        var compact = new string(text
            .Where(ch => !char.IsWhiteSpace(ch))
            .Select(char.ToLowerInvariant)
            .ToArray());
        string[] explicitMultiTargetMarkers = ["都是", "都可疑", "两个人", "两个", "both", "and"];
        if (explicitMultiTargetMarkers.Any(marker => compact.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return mentionedPlayers;
        }

        string[] claimWords =
        [
            "杀人", "击杀", "下刀", "跳管", "进管", "通风管", "变形", "放炸弹", "放置", "隐身",
            "内鬼", "凶手", "可疑", "好人", "船员", "可信", "清白",
            "kill", "vent", "morph", "bomb", "impostor", "sus", "innocent", "clear"
        ];
        var pivots = claimWords
            .SelectMany(word => FindAllIndexes(compact, word))
            .ToArray();
        if (pivots.Length == 0)
        {
            return mentionedPlayers;
        }

        var ranked = mentionedPlayers
            .Select(player => new
            {
                Player = player,
                AliasIndex = GetPlayerAliasIndex(text, player)
            })
            .Where(item => item.AliasIndex >= 0)
            .Select(item => new
            {
                item.Player,
                Distance = pivots.Min(pivot => Math.Abs(item.AliasIndex - pivot))
            })
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Player.PlayerId)
            .ToArray();
        return ranked.Length == 0 ? mentionedPlayers : [ranked[0].Player];
    }

    private static IEnumerable<int> FindAllIndexes(string text, string value)
    {
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(value, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                yield break;
            }

            yield return index;
            start = index + Math.Max(1, value.Length);
        }
    }

    private static int GetPlayerAliasIndex(string text, PlayerControl player)
    {
        if (!player || player.Data is null)
        {
            return -1;
        }

        var outfit = player.Data.DefaultOutfit;
        var colorId = outfit is null ? player.CurrentOutfit.ColorId : outfit.ColorId;
        return BotBehaviorPolicy.FindPlayerAliasIndex(
            text,
            player.PlayerId,
            player.Data.PlayerName,
            colorId,
            player.Data.ColorName);
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
        public float BodyObservationStartedAt { get; set; }
        public string PendingReportStrategy { get; set; } = string.Empty;
        public Dictionary<byte, float> BodyIgnoreUntil { get; } = [];
        public float NextReportCheckAt { get; set; }
        public bool DecisionInFlight { get; set; }
        public bool DecisionCompleted { get; set; }
        public bool DecisionApplied { get; set; }
        public float DecisionReadyAt { get; set; }
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
        public byte? PendingNativeVoteId { get; set; }
        public float VoteCommandSentAt { get; set; }
        public Dictionary<byte, float> BeliefScores { get; } = [];
        public int PersistentBeliefMatchSerial { get; set; } = -1;
        public Dictionary<byte, float> PersistentPublicSuspicion { get; } = [];
    }

    private sealed record TranscriptEntry(byte PlayerId, string Name, string Text);
    private sealed record PublicConcreteClaim(byte SourceId, string SourceName, string Action);
}
