using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using BepInEx.Logging;
using InnerNet;
using UnityEngine;

namespace AmongUsDeepSeekBots;

internal sealed class BotAbilityDirector
{
    private const float TickInterval = 0.5f;
    private static readonly HashSet<string> LlmSequenceCheckpointRoles =
    [
        "Morphling", "Portalmaker", "Trickster", "Ninja", "Warlock", "Yoyo"
    ];
    private readonly ManualLogSource _log;
    private readonly BotMatchMemory _memory;
    private readonly BotActionDirector _actions;
    private readonly DeepSeekDecisionClient _deepSeek;
    private readonly Dictionary<byte, AbilityState> _states = [];
    private int _observedMatchSerial = -1;
    private float _nextTickAt;
    private float _nextGlobalLlmRequestAt;

    public BotAbilityDirector(
        ManualLogSource log,
        BotMatchMemory memory,
        BotActionDirector actions,
        DeepSeekDecisionClient deepSeek)
    {
        _log = log;
        _memory = memory;
        _actions = actions;
        _deepSeek = deepSeek;
    }

    public void Update(PluginConfig config)
    {
        SynchronizeMatchState();
        if (!config.BotUseRoleAbilities.Value ||
            Time.time < _nextTickAt ||
            !IsHostAuthority() ||
            !_actions.IsSharedActionWindowOpen())
        {
            return;
        }

        _nextTickAt = Time.time + TickInterval;
        foreach (var bot in EnumerateDeepBots())
        {
            if (bot.Data is null || bot.Data.Disconnected || bot.Data.Role is null)
            {
                continue;
            }

            var state = GetState(bot);
            if (HandlePendingVentEntry(bot, state))
            {
                continue;
            }

            if (bot.inVent)
            {
                ConfirmVentEntry(bot, state);
                if (state.ActiveVentId.HasValue &&
                    (Time.time >= state.ExitVentAt ||
                     Time.time >= state.MinimumVentHoldUntil &&
                     ShouldExitVentForAmbush(bot, state.ActiveVentId.Value, state.VentAmbushTargetId)))
                {
                    ExitVent(bot, state);
                }

                continue;
            }

            if (bot.walkingToVent || Time.time < state.NextAbilityAt)
            {
                continue;
            }

            if (!bot.moveable)
            {
                continue;
            }

            var roleType = bot.Data.RoleType;
            if (bot.Data.IsDead && roleType != RoleTypes.GuardianAngel)
            {
                continue;
            }

            if (!SupportsStrategicAbility(bot))
            {
                continue;
            }

            if (bot.Data.RoleType != RoleTypes.Shapeshifter)
            {
                ClearShapeshifterSequence(state);
            }
            else if (state.PendingShapeshiftTargetId.HasValue)
            {
                ContinueShapeshifterSequence(bot, state);
                continue;
            }

            if (state.DecisionCompleted)
            {
                ApplyAbilityDecision(bot, state);
                continue;
            }

            if (!state.DecisionInFlight)
            {
                RequestAbilityDecision(bot, state, config);
            }
        }
    }

    private void SynchronizeMatchState()
    {
        var serial = _memory.MatchSerial;
        if (serial <= 0 || serial == _observedMatchSerial)
        {
            return;
        }

        _observedMatchSerial = serial;
        _states.Clear();
        _nextTickAt = 0f;
        _nextGlobalLlmRequestAt = 0f;
        _log.LogInfo($"DeepBot ability state reset for new match: match={serial}.");
    }

    private void RequestAbilityDecision(PlayerControl bot, AbilityState state, PluginConfig config)
    {
        var availableTorRoles = TorRoleAdapter.GetAbilityRoles(bot);
        var hasTorRole = TrySelectTorAbilityRole(bot, state, out var torRole);
        if (availableTorRoles.Count > 0 && !hasTorRole)
        {
            state.NextAbilityAt = Time.time + 1f;
            return;
        }
        if (hasTorRole && TorRoleAdapter.TryGetAbilitySequencePlan(bot, torRole, out var sequencePlan))
        {
            if (!sequencePlan.ShouldUse)
            {
                state.NextAbilityAt = Time.time + Mathf.Max(0.35f, sequencePlan.RecheckSeconds);
                if (Time.time >= state.NextRouteLogAt)
                {
                    state.NextRouteLogAt = Time.time + 2.5f;
                    _log.LogInfo(
                        $"DeepBot multi-stage ability waiting: bot={bot.Data?.PlayerName}({bot.PlayerId}), " +
                        $"role={torRole.Name}, reason={sequencePlan.Reason}.");
                }
                return;
            }

            var sequenceVentReady = string.Equals(sequencePlan.AbilityAction, "vent", StringComparison.Ordinal) &&
                                    TorRoleAdapter.CanUseVents(bot, torRole);
            var sequenceRouteReady = sequencePlan.AbilityAction is "cover" or "evade";
            var sequenceRoleContinuationReady = sequencePlan.ShouldUse && sequencePlan.AbilityAction == "role";
            if (!TorRoleAdapter.IsAbilityReady(bot, torRole) && !sequenceVentReady && !sequenceRouteReady && !sequenceRoleContinuationReady)
            {
                state.NextAbilityAt = Time.time + Mathf.Max(0.35f, sequencePlan.RecheckSeconds);
                return;
            }

            if (TryQueueSequenceCheckpoint(bot, state, config, torRole, sequencePlan))
            {
                return;
            }

            state.RequestedRole = bot.Data.RoleType;
            state.RequestedTorRole = torRole.Name;
            state.AbilityRoleCursor++;
            state.PendingDecision = new BotAbilityDecision(
                true,
                sequencePlan.TargetPlayerId,
                sequencePlan.Reason,
                sequencePlan.Confidence,
                sequencePlan.AbilityAction);
            state.DecisionCompleted = true;
            state.DecisionInFlight = false;
            _log.LogInfo(
                $"DeepBot multi-stage ability continuation armed: bot={bot.Data?.PlayerName}({bot.PlayerId}), " +
                $"role={torRole.Name}, action={sequencePlan.AbilityAction}, " +
                $"target={sequencePlan.TargetPlayerId?.ToString() ?? "none"}, reason={sequencePlan.Reason}.");
            return;
        }
        state.SequenceCheckpointKey = null;
        state.SequenceCheckpointConsumed = false;
        state.PendingSequencePlan = null;
        var torVentReady = hasTorRole && TorRoleAdapter.CanUseVents(bot, torRole);
        if ((hasTorRole && !TorRoleAdapter.IsAbilityReady(bot, torRole) && !torVentReady) ||
            (!hasTorRole && IsRoleCoolingDown(bot.Data.Role)))
        {
            state.NextAbilityAt = Time.time + 2f;
            return;
        }

        if (Time.time < _nextGlobalLlmRequestAt)
        {
            state.NextAbilityAt = _nextGlobalLlmRequestAt + UnityEngine.Random.Range(0.05f, 0.35f);
            return;
        }

        _nextGlobalLlmRequestAt = Time.time + Mathf.Clamp(config.AbilityRequestSpacingSeconds.Value, 0.35f, 5f);
        state.DecisionInFlight = true;
        state.RequestedRole = bot.Data.RoleType;
        state.RequestedTorRole = hasTorRole ? torRole.Name : null;
        if (hasTorRole)
        {
            state.AbilityRoleCursor++;
        }
        var prompt = BuildAbilityPrompt(bot, hasTorRole ? torRole : null);
        _log.LogInfo(
            $"DeepBot ability brain queued: bot={bot.Data?.PlayerName}({bot.PlayerId}), " +
            $"role={prompt.Role}, purpose={prompt.AbilityPurpose}.");
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            BotAbilityDecision? decision = null;
            try
            {
                decision = await _deepSeek
                    .GetAbilityDecisionAsync(prompt, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    $"DeepBot ability brain failed: bot={prompt.BotName}({prompt.BotId}), error={ex.Message}");
            }
            finally
            {
                state.PendingDecision = decision;
                state.DecisionCompleted = true;
                state.DecisionInFlight = false;
            }
        });
    }

    private bool TryQueueSequenceCheckpoint(
        PlayerControl bot,
        AbilityState state,
        PluginConfig config,
        TorRoleInfo role,
        TorAbilitySequencePlan plan)
    {
        if (!RequiresLlmSequenceCheckpoint(role.Name, plan.AbilityAction))
        {
            return false;
        }

        var checkpointKey = BuildSequenceCheckpointKey(role.Name, plan);
        if (!string.Equals(state.SequenceCheckpointKey, checkpointKey, StringComparison.Ordinal))
        {
            state.SequenceCheckpointKey = checkpointKey;
            state.SequenceCheckpointConsumed = false;
        }

        if (state.SequenceCheckpointConsumed)
        {
            return false;
        }

        if (Time.time < _nextGlobalLlmRequestAt)
        {
            state.NextAbilityAt = _nextGlobalLlmRequestAt + UnityEngine.Random.Range(0.05f, 0.30f);
            return true;
        }

        _nextGlobalLlmRequestAt = Time.time + Mathf.Clamp(config.AbilityRequestSpacingSeconds.Value, 0.35f, 5f);
        state.DecisionInFlight = true;
        state.RequestedRole = bot.Data.RoleType;
        state.RequestedTorRole = role.Name;
        state.PendingSequencePlan = plan;
        state.AbilityRoleCursor++;
        var prompt = BuildAbilityPrompt(bot, role, plan);
        _log.LogInfo(
            $"DeepBot multi-stage ability checkpoint queued: bot={bot.Data?.PlayerName}({bot.PlayerId}), " +
            $"role={role.Name}, action={plan.AbilityAction}, target={plan.TargetPlayerId?.ToString() ?? "none"}, " +
            $"reason={plan.Reason}.");
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            BotAbilityDecision? decision = null;
            try
            {
                decision = await _deepSeek
                    .GetAbilityDecisionAsync(prompt, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    $"DeepBot multi-stage ability checkpoint failed: bot={prompt.BotName}({prompt.BotId}), error={ex.Message}");
            }
            finally
            {
                state.PendingDecision = decision;
                state.DecisionCompleted = true;
                state.DecisionInFlight = false;
            }
        });
        return true;
    }

    private static bool RequiresLlmSequenceCheckpoint(string roleName, string requiredAction)
    {
        return LlmSequenceCheckpointRoles.Contains(roleName) &&
               requiredAction is "role" or "vent";
    }

    private static string BuildSequenceCheckpointKey(string roleName, TorAbilitySequencePlan plan)
    {
        return $"{roleName}|{plan.AbilityAction}|{plan.TargetPlayerId?.ToString() ?? "none"}|{plan.Reason}";
    }

    private void ApplyAbilityDecision(PlayerControl bot, AbilityState state)
    {
        state.DecisionCompleted = false;
        var requestedSequencePlan = state.PendingSequencePlan;
        state.PendingSequencePlan = null;
        var torRole = default(TorRoleInfo);
        var hasTorRole = !string.IsNullOrWhiteSpace(state.RequestedTorRole) &&
                         TorRoleAdapter.TryGetAbilityRole(bot, state.RequestedTorRole!, out torRole);
        var currentTorRole = hasTorRole ? torRole.Name : null;
        if (!string.Equals(state.RequestedTorRole, currentTorRole, StringComparison.Ordinal))
        {
            state.PendingDecision = null;
            state.SequenceCheckpointKey = null;
            state.SequenceCheckpointConsumed = false;
            state.RequestedTorRole = null;
            state.RequestedRole = null;
            state.NextAbilityAt = Time.time + 4f;
            _log.LogInfo(
                $"DeepBot discarded stale TOR ability plan after role change: bot={bot.Data?.PlayerName}, " +
                $"currentRole={currentTorRole ?? bot.Data?.RoleType.ToString()}.");
            return;
        }

        if (state.RequestedRole.HasValue && state.RequestedRole.Value != bot.Data.RoleType)
        {
            state.PendingDecision = null;
            state.SequenceCheckpointKey = null;
            state.SequenceCheckpointConsumed = false;
            state.RequestedTorRole = null;
            state.RequestedRole = null;
            state.NextAbilityAt = Time.time + 4f;
            _log.LogInfo(
                $"DeepBot discarded stale ability plan after role change: bot={bot.Data?.PlayerName}, " +
                $"currentRole={bot.Data?.RoleType}.");
            return;
        }

        if (requestedSequencePlan.HasValue &&
            (!hasTorRole ||
             !TorRoleAdapter.TryGetAbilitySequencePlan(bot, torRole, out var liveSequencePlan) ||
             !liveSequencePlan.Active ||
             !liveSequencePlan.ShouldUse ||
             !string.Equals(
                 BuildSequenceCheckpointKey(torRole.Name, liveSequencePlan),
                 state.SequenceCheckpointKey,
                 StringComparison.Ordinal)))
        {
            state.PendingDecision = null;
            state.RequestedTorRole = null;
            state.RequestedRole = null;
            state.SequenceCheckpointKey = null;
            state.SequenceCheckpointConsumed = false;
            state.NextAbilityAt = Time.time + 0.75f;
            _log.LogInfo(
                $"DeepBot discarded stale multi-stage checkpoint: bot={bot.Data?.PlayerName}({bot.PlayerId}), " +
                $"role={(hasTorRole ? torRole.Name : "changed")}, reason=native sequence stage changed while model was thinking.");
            return;
        }

        state.RequestedTorRole = null;
        state.RequestedRole = null;
        var modelDecision = state.PendingDecision;
        var decision = modelDecision ??
                       (requestedSequencePlan.HasValue
                           ? BuildSequenceCheckpointFallback(requestedSequencePlan.Value)
                           : BuildStrategicFallback(bot, hasTorRole ? torRole : null));
        state.PendingDecision = null;
        var torVentReady = hasTorRole && TorRoleAdapter.CanUseVents(bot, torRole);
        var torSequenceReady = hasTorRole &&
                               TorRoleAdapter.TryGetAbilitySequencePlan(bot, torRole, out var currentSequence) &&
                               currentSequence.ShouldUse;
        if ((hasTorRole && !TorRoleAdapter.IsAbilityReady(bot, torRole) && !torVentReady && !torSequenceReady) ||
            (!hasTorRole && IsRoleCoolingDown(bot.Data.Role)))
        {
            state.NextAbilityAt = Time.time + 3f;
            _log.LogInfo(
                $"DeepBot ability plan expired before execution: bot={bot.Data?.PlayerName}, " +
                $"role={(hasTorRole ? torRole.Name : bot.Data?.RoleType.ToString())}, reason=ability entered cooldown.");
            return;
        }

        if (hasTorRole)
        {
            if (requestedSequencePlan.HasValue)
            {
                decision = ConstrainSequenceCheckpointDecision(
                    bot,
                    torRole,
                    requestedSequencePlan.Value,
                    decision);
                state.SequenceCheckpointConsumed = true;
                _log.LogInfo(
                    $"DeepBot multi-stage ability checkpoint applied: bot={bot.Data?.PlayerName}({bot.PlayerId}), " +
                    $"role={torRole.Name}, source={(modelDecision is null ? "safe-fallback" : "llm")}, " +
                    $"action={NormalizeAbilityAction(decision.AbilityAction)}, " +
                    $"target={decision.TargetPlayerId?.ToString() ?? "none"}, use={decision.Use}, " +
                    $"reason={decision.Reason}.");
            }

            decision = ApplyObjectiveCriticalOverride(bot, torRole, decision);
        }

        var abilityAction = NormalizeAbilityAction(decision.AbilityAction);
        if (!decision.Use || abilityAction == "hold" || decision.Confidence < 0.52f)
        {
            var urgentBodyReview = hasTorRole &&
                                   torRole.Name == "Vulture" &&
                                   TorRoleAdapter.FindVisibleUsableBody(bot);
            var plannedRecheck = Mathf.Clamp(
                decision.RecheckSeconds ?? (urgentBodyReview ? UnityEngine.Random.Range(2.5f, 4f) : UnityEngine.Random.Range(7f, 12f)),
                urgentBodyReview ? 1.5f : 2f,
                urgentBodyReview ? 4f : 12f);
            state.NextAbilityAt = Time.time + plannedRecheck;
            _log.LogInfo(
                $"DeepBot ability held for strategy: bot={bot.Data?.PlayerName}, role={(hasTorRole ? torRole.Name : bot.Data?.RoleType.ToString())}, " +
                $"confidence={decision.Confidence:0.00}, urgentBodyReview={urgentBodyReview}, recheck={plannedRecheck:0.0}s, " +
                $"goal={decision.PlanGoal ?? "none"}, next={decision.NextStage ?? "none"}, " +
                $"abort={decision.AbortCondition ?? "none"}, reason={decision.Reason ?? "no useful purpose"}.");
            if (hasTorRole && torRole.Name == "Arsonist" && !TorRoleAdapter.IsArsonistReadyToIgnite(bot))
            {
                _actions.TryRouteToRoleSearch(bot, torRole.Name);
            }
            return;
        }

        var targetId = decision.TargetPlayerId is >= byte.MinValue and <= byte.MaxValue
            ? (byte?)decision.TargetPlayerId.Value
            : null;
        _memory.RecordAction(
            bot,
            "ability_plan",
            $"role={(hasTorRole ? torRole.Name : bot.Data?.RoleType.ToString())}, target={targetId?.ToString() ?? "none"}, " +
            $"goal={decision.PlanGoal ?? "none"}, next={decision.NextStage ?? "none"}, abort={decision.AbortCondition ?? "none"}, " +
            $"reason={decision.Reason}, confidence={decision.Confidence:0.00}");
        _log.LogInfo(
            $"DeepBot ability brain approved: bot={bot.Data?.PlayerName}, role={(hasTorRole ? torRole.Name : bot.Data?.RoleType.ToString())}, " +
            $"target={targetId?.ToString() ?? "none"}, confidence={decision.Confidence:0.00}, reason={decision.Reason}.");

        if (abilityAction == "vent")
        {
            var canVent = hasTorRole
                ? TorRoleAdapter.CanUseVents(bot, torRole)
                : bot.Data?.Role?.CanVent == true;
            if (canVent)
            {
                var ambushTargetId = targetId ?? TryGetRecentVentAmbushTarget(bot)?.PlayerId;
                TryUseOrRouteVent(
                    bot,
                    state,
                    hasTorRole ? torRole.Name : bot.Data?.RoleType.ToString() ?? "unknown",
                    ambushTargetId);
            }
            else
            {
                state.NextAbilityAt = Time.time + 6f;
                _log.LogInfo($"DeepBot LLM vent plan rejected by role rules: bot={bot.Data?.PlayerName}, role={(hasTorRole ? torRole.Name : bot.Data?.RoleType.ToString() ?? "unknown")}.");
            }
            return;
        }

        if (hasTorRole && abilityAction == "evade")
        {
            if (TorRoleAdapter.TryGetAbilityEscapeDestination(
                    bot,
                    torRole,
                    out var escapePosition,
                    out var escapeStage,
                    out var escapeArrivalDistance))
            {
                var routed = _actions.TryRouteToRoleEscape(
                    bot,
                    escapePosition,
                    $"ABILITY_ESCAPE_{torRole.Name.ToUpperInvariant()}_{escapeStage}",
                    escapeArrivalDistance);
                state.NextAbilityAt = Time.time + (routed ? 1.0f : 2.5f);
                _memory.RecordAction(
                    bot,
                    "ability_escape",
                    $"role={torRole.Name}; stage={escapeStage}; routed={routed}; reason={decision.Reason}");
                _log.LogInfo(
                    $"DeepBot multi-stage ability escape: bot={bot.Data?.PlayerName}, role={torRole.Name}, " +
                    $"stage={escapeStage}, routed={routed}.");
            }
            else
            {
                state.NextAbilityAt = Time.time + 1.5f;
            }
            return;
        }

        if (hasTorRole)
        {
            if (abilityAction is "role" or "cover" &&
                TorRoleAdapter.TryGetAbilityStagingDestination(
                    bot,
                    torRole,
                    out var stagingPosition,
                    out var stagingLabel,
                    out var stagingArrivalDistance))
            {
                var stagingPhase = stagingLabel.Split(':')[0];
                var stagingKey = $"{torRole.Name}:{stagingPhase}";
                if (string.Equals(state.TorStagingKey, stagingKey, StringComparison.Ordinal) &&
                    state.TorStagingPosition.HasValue &&
                    SkeldPathGraph.Instance.FindTopRoutes(
                        bot.GetTruePosition(),
                        state.TorStagingPosition.Value,
                        1).Count > 0)
                {
                    stagingPosition = state.TorStagingPosition.Value;
                    stagingLabel = state.TorStagingLabel ?? stagingLabel;
                    stagingArrivalDistance = state.TorStagingArrivalDistance;
                }
                else
                {
                    state.TorStagingKey = stagingKey;
                    state.TorStagingPosition = stagingPosition;
                    state.TorStagingLabel = stagingLabel;
                    state.TorStagingArrivalDistance = stagingArrivalDistance;
                    _log.LogInfo(
                        $"DeepBot multi-stage target locked: bot={bot.Data?.PlayerName}({bot.PlayerId}), " +
                        $"role={torRole.Name}, phase={stagingPhase}, target={stagingLabel}@{stagingPosition}.");
                }

                var stagingDistance = Vector2.Distance(bot.GetTruePosition(), stagingPosition);
                var stagingBlocked = PhysicsHelpers.AnythingBetween(
                    bot.GetTruePosition(),
                    stagingPosition,
                    Constants.ShipOnlyMask,
                    false);
                if (stagingDistance > stagingArrivalDistance || stagingBlocked)
                {
                    var routed = _actions.TryRouteToRoleAbility(
                        bot,
                        stagingPosition,
                        $"ABILITY_STAGE_{torRole.Name.ToUpperInvariant()}_{stagingLabel}",
                        stagingArrivalDistance);
                    state.NextAbilityAt = Time.time + (routed ? 0.8f : 2.5f);
                    _memory.RecordAction(
                        bot,
                        "ability_stage",
                        $"role={torRole.Name}; stage={stagingLabel}; distance={stagingDistance:0.0}; routed={routed}");
                    _log.LogInfo(
                        $"DeepBot multi-stage ability route: bot={bot.Data?.PlayerName}, role={torRole.Name}, " +
                        $"stage={stagingLabel}, distance={stagingDistance:0.0}, routed={routed}.");
                    return;
                }

                if (abilityAction == "cover")
                {
                    _actions.CompleteRoleAbilityRoute(bot, $"{torRole.Name}-cover-position-reached");
                    state.NextAbilityAt = Time.time + 2.5f;
                    _memory.RecordAction(bot, "ability_cover", $"role={torRole.Name}; stage={stagingLabel}; position reached");
                    return;
                }
            }

            if (abilityAction == "cover")
            {
                state.NextAbilityAt = Time.time + 2.5f;
                return;
            }

            if (torRole.Name is "Sheriff" or "Deputy")
            {
                var requiredConfidence = torRole.Name == "Sheriff" ? 0.78f : 0.62f;
                var evidenceTarget = FindEvidenceBackedSuspect(bot, requiredConfidence);
                if (evidenceTarget is null || !targetId.HasValue || evidenceTarget.PlayerId != targetId.Value)
                {
                    state.NextAbilityAt = Time.time + 4f;
                    _log.LogInfo(
                        $"DeepBot evidence-gated ability rejected: bot={bot.Data?.PlayerName}, role={torRole.Name}, " +
                        $"requestedTarget={targetId?.ToString() ?? "none"}, reason=no matching high-confidence meeting suspect.");
                    return;
                }
            }

            if (torRole.Name is "Vulture" or "Cleaner" or "Janitor" &&
                !TorRoleAdapter.HasNearbyUsableBody(bot) &&
                TorRoleAdapter.FindVisibleUsableBody(bot) is { } visibleBody)
            {
                var routed = _actions.TryRouteToRoleAbility(
                    bot,
                    visibleBody.TruePosition,
                    $"ABILITY_VULTURE_BODY_{visibleBody.ParentId}",
                    Mathf.Clamp(DeadBodyPerception.GetReportDistance(bot) - 0.2f, 0.75f, 1.35f));
                state.NextAbilityAt = Time.time + (routed ? 2f : 5f);
                _memory.RecordAction(
                    bot,
                    "ability_route",
                    $"{torRole.Name} prioritized visible body playerId={visibleBody.ParentId}; llmReason={decision.Reason}");
                _log.LogInfo(
                    $"DeepBot {torRole.Name} body route approved: bot={bot.Data?.PlayerName}, victim={visibleBody.ParentId}, routed={routed}.");
                return;
            }

            if (torRole.Name == "Medium" &&
                TorRoleAdapter.TryGetNearestMediumSoulPosition(bot, out var soulPosition) &&
                Vector2.Distance(bot.GetTruePosition(), soulPosition) > 1.15f)
            {
                var routed = _actions.TryRouteToRoleAbility(
                    bot,
                    soulPosition,
                    "ABILITY_MEDIUM_SOUL",
                    0.95f);
                state.NextAbilityAt = Time.time + (routed ? 1f : 4f);
                _memory.RecordAction(bot, "ability_route", $"Medium routed to a personally usable soul; routed={routed}");
                return;
            }

            if (TorRoleAdapter.CurrentAbilityStageRequiresCasterProximity(bot, torRole) &&
                targetId.HasValue && FindPlayer(targetId.Value) is { } roleTarget && roleTarget && roleTarget.Data is not null &&
                BotPerceptionPolicy.CanBeOrdinarilyObserved(roleTarget))
            {
                var useRange = TorRoleAdapter.GetAbilityUseRange(torRole.Name);
                var distance = Vector2.Distance(bot.GetTruePosition(), roleTarget.GetTruePosition());
                var blocked = PhysicsHelpers.AnythingBetween(
                    bot.GetTruePosition(),
                    roleTarget.GetTruePosition(),
                    Constants.ShipOnlyMask,
                    false);
                if (distance > useRange * 0.85f || blocked)
                {
                    var routed = _actions.TryRouteToRoleAbility(
                        bot,
                        roleTarget.GetTruePosition(),
                        $"ABILITY_{torRole.Name.ToUpperInvariant()}_{roleTarget.PlayerId}",
                        Mathf.Clamp(useRange * 0.72f, 0.8f, 1.5f),
                        roleTarget.PlayerId);
                    state.NextAbilityAt = Time.time + (routed ? 1f : 3f);
                    _memory.RecordAction(
                        bot,
                        "ability_route",
                        $"role={torRole.Name}; target={roleTarget.Data.PlayerName}({roleTarget.PlayerId}); distance={distance:0.0}; routed={routed}");
                    _log.LogInfo(
                        $"DeepBot target ability route: bot={bot.Data?.PlayerName}, role={torRole.Name}, " +
                        $"target={roleTarget.Data.PlayerName}({roleTarget.PlayerId}), distance={distance:0.0}, routed={routed}.");
                    return;
                }
            }

            if (TorRoleAdapter.TryUseAbility(bot, torRole, targetId, out var outcome))
            {
                state.ClearTorStagingTarget();
                if (!TorRoleAdapter.IsAbilitySequencePending(bot, torRole))
                {
                    TorRoleAdapter.RegisterConfiguredCooldown(bot, torRole);
                }
                state.NextAbilityAt = Time.time + 0.75f;
                _memory.RecordAction(bot, "ability", $"TOR {torRole.Name}: {outcome}");
                _log.LogInfo(
                    $"DeepBot TOR ability used: bot={bot.Data?.PlayerName}({bot.PlayerId}), role={torRole.Name}, outcome={outcome}.");
            }
            else
            {
                state.NextAbilityAt = Time.time + 6f;
                _log.LogInfo(
                    $"DeepBot TOR ability held: bot={bot.Data?.PlayerName}({bot.PlayerId}), role={torRole.Name}, reason={outcome}.");
            }

            return;
        }

        switch (bot.Data!.RoleType)
        {
            case RoleTypes.Scientist:
                TryUseScientistVitals(bot, state);
                break;
            case RoleTypes.Engineer:
                TryUseEngineerVent(bot, state);
                break;
            case RoleTypes.Tracker:
                TryUseTargetedAbility(bot, state, "tracker", targetId);
                break;
            case RoleTypes.GuardianAngel:
                TryUseTargetedAbility(bot, state, "guardian-protect", targetId);
                break;
            case RoleTypes.Phantom:
                TryUsePhantom(bot, state);
                break;
            case RoleTypes.Shapeshifter:
                StartShapeshifterSequence(bot, state, targetId);
                break;
            case RoleTypes.Detective:
                TryUseTargetedAbility(bot, state, "detective-interrogate", targetId);
                break;
            default:
                if (bot.Data.Role.CanVent && TorRoleAdapter.IsImpostorTeam(bot))
                {
                    TryUseImpostorVent(bot, state);
                }
                else
                {
                    state.NextAbilityAt = Time.time + 10f;
                }
                break;
        }
    }

    private static BotAbilityDecision BuildSequenceCheckpointFallback(TorAbilitySequencePlan plan)
    {
        return new BotAbilityDecision(
            plan.ShouldUse,
            plan.TargetPlayerId,
            $"ability API unavailable; safely continue TOR's validated sequence stage: {plan.Reason}",
            plan.Confidence,
            plan.AbilityAction,
            "complete the current native multi-stage ability",
            plan.Reason,
            "abort if TOR no longer reports this stage as legal",
            plan.RecheckSeconds);
    }

    private static BotAbilityDecision ConstrainSequenceCheckpointDecision(
        PlayerControl bot,
        TorRoleInfo role,
        TorAbilitySequencePlan plan,
        BotAbilityDecision decision)
    {
        var action = NormalizeAbilityAction(decision.AbilityAction);
        var canVent = TorRoleAdapter.CanUseVents(bot, role);
        if (!decision.Use || action == "hold")
        {
            return decision with
            {
                TargetPlayerId = plan.TargetPlayerId,
                AbilityAction = "hold",
                RecheckSeconds = Mathf.Clamp(decision.RecheckSeconds ?? plan.RecheckSeconds, 0.5f, 4f)
            };
        }

        if (!IsAllowedSequenceCheckpointAction(plan.AbilityAction, action, canVent))
        {
            return decision with
            {
                Use = false,
                TargetPlayerId = plan.TargetPlayerId,
                AbilityAction = "hold",
                Reason = $"model proposed illegal sequence action={action}; native stage requires={plan.AbilityAction}",
                Confidence = Mathf.Min(decision.Confidence, 0.50f),
                RecheckSeconds = 1f
            };
        }

        // The model may choose timing, cover, evasion, or a legal vent detour,
        // but it cannot silently replace the target fixed by TOR's current
        // sequence stage (sampled identity, marked victim, curse target, etc.).
        return decision with
        {
            TargetPlayerId = plan.TargetPlayerId,
            AbilityAction = action
        };
    }

    private static bool IsAllowedSequenceCheckpointAction(
        string requiredAction,
        string proposedAction,
        bool canVent)
    {
        return proposedAction == requiredAction ||
               proposedAction is "hold" or "cover" or "evade" ||
               proposedAction == "vent" && canVent;
    }

    private void TryUseScientistVitals(PlayerControl bot, AbilityState state)
    {
        var scientist = bot.Data.Role.TryCast<ScientistRole>();
        if (scientist is null || scientist.IsCoolingDown || scientist.currentCharge <= 0.05f)
        {
            state.NextAbilityAt = Time.time + 4f;
            return;
        }

        var deaths = GameData.Instance?.AllPlayers
            .ToArray()
            .Where(player => player is not null && player.IsDead && !player.Disconnected)
            .Select(player => $"{player.PlayerName}({player.PlayerId})")
            .ToArray() ?? [];
        scientist.currentCharge = Mathf.Max(0f, scientist.currentCharge - 1.5f);
        scientist.currentCooldown = Mathf.Max(scientist.currentCooldown, scientist.RoleCooldownValue);
        state.NextAbilityAt = Time.time + UnityEngine.Random.Range(18f, 30f);
        var result = deaths.Length == 0 ? "no deaths shown" : $"dead={string.Join(",", deaths)}";
        _memory.RecordAction(bot, "ability", $"scientist checked vitals: {result}");
        _log.LogInfo(
            $"DeepBot role ability used strategically: bot={bot.Data?.PlayerName}, role=Scientist, {result}.");
    }

    private void TryUseEngineerVent(PlayerControl bot, AbilityState state)
    {
        var engineer = bot.Data.Role.TryCast<EngineerRole>();
        if (engineer is null || engineer.IsCoolingDown || engineer.usesRemaining == 0)
        {
            state.NextAbilityAt = Time.time + 2f;
            return;
        }

        var vent = FindClosestVent(bot, 1.45f);
        if (vent is null)
        {
            state.NextAbilityAt = Time.time + 5f;
            _log.LogInfo(
                $"DeepBot approved Engineer vent held: bot={bot.Data?.PlayerName}, reason=no nearby legal vent.");
            return;
        }

        try
        {
            bot.MyPhysics.RpcEnterVent(vent.Id);
            if (engineer.usesRemaining > 0)
            {
                engineer.usesRemaining--;
            }
            engineer.SetCooldown();
            _actions.CompleteRoleAbilityRoute(bot, $"engineer-entered-vent-{vent.Id}");
            BeginVentEntryTracking(bot, state, vent.Id, "Engineer");
            state.VentAmbushTargetId = null;
            state.MinimumVentHoldUntil = Time.time + 0.8f;
            state.ExitVentAt = Time.time + UnityEngine.Random.Range(2.5f, 5f);
            state.NextAbilityAt = Time.time + UnityEngine.Random.Range(22f, 38f);
            _memory.RecordAction(bot, "ability", $"engineer entered vent {vent.Id}");
            _log.LogInfo($"DeepBot role ability used: bot={bot.Data.PlayerName}, role=Engineer, vent={vent.Id}.");
        }
        catch (Exception ex)
        {
            state.NextAbilityAt = Time.time + 5f;
            _log.LogWarning($"DeepBot engineer vent failed: bot={bot.Data.PlayerName}, vent={vent.Id}, error={ex.Message}");
        }
    }

    private void TryUseImpostorVent(PlayerControl bot, AbilityState state)
    {
        var vent = FindClosestVent(bot, 1.35f);
        if (vent is null)
        {
            state.NextAbilityAt = Time.time + 5f;
            _log.LogInfo(
                $"DeepBot approved impostor vent held: bot={bot.Data?.PlayerName}, reason=no nearby legal vent.");
            return;
        }

        try
        {
            bot.MyPhysics.RpcEnterVent(vent.Id);
            _actions.CompleteRoleAbilityRoute(bot, $"impostor-entered-vent-{vent.Id}");
            BeginVentEntryTracking(bot, state, vent.Id, bot.Data.RoleType.ToString());
            state.VentAmbushTargetId = null;
            state.MinimumVentHoldUntil = Time.time + 1.2f;
            state.ExitVentAt = Time.time + UnityEngine.Random.Range(2f, 4.5f);
            state.NextAbilityAt = Time.time + UnityEngine.Random.Range(25f, 45f);
            _memory.RecordAction(bot, "ability", $"impostor entered vent {vent.Id}");
            _log.LogInfo($"DeepBot role ability used: bot={bot.Data.PlayerName}, role={bot.Data.RoleType}, vent={vent.Id}.");
        }
        catch (Exception ex)
        {
            state.NextAbilityAt = Time.time + 6f;
            _log.LogWarning($"DeepBot impostor vent failed: bot={bot.Data.PlayerName}, vent={vent.Id}, error={ex.Message}");
        }
    }

    private void ExitVent(PlayerControl bot, AbilityState state)
    {
        try
        {
            bot.MyPhysics.RpcExitVent(state.ActiveVentId!.Value);
            _memory.RecordAction(bot, "ability", $"exited vent {state.ActiveVentId.Value}");
            _log.LogInfo($"DeepBot exited vent through native RPC: bot={bot.Data?.PlayerName}, vent={state.ActiveVentId.Value}.");
        }
        catch (Exception ex)
        {
            _log.LogWarning($"DeepBot exit vent failed: bot={bot.Data?.PlayerName}, vent={state.ActiveVentId}, error={ex.Message}");
        }
        finally
        {
            state.ActiveVentId = null;
            state.VentAmbushTargetId = null;
            state.MinimumVentHoldUntil = 0f;
            state.ExitVentAt = 0f;
        }
    }

    private void StartShapeshifterSequence(PlayerControl bot, AbilityState state, byte? requestedTargetId)
    {
        var target = requestedTargetId.HasValue
            ? FindPlayer(requestedTargetId.Value)
            : FindAbilityTarget(bot, impostor: true);
        if (!IsLegalShapeshiftIdentity(bot, target))
        {
            state.NextAbilityAt = Time.time + 4f;
            _log.LogInfo(
                $"DeepBot Shapeshifter plan held: bot={bot.Data?.PlayerName}, " +
                $"requestedTarget={requestedTargetId?.ToString() ?? "none"}, reason=identity target unavailable or allied.");
            return;
        }

        var staging = PickShapeshiftStagingPosition(bot, null);
        if (!staging.HasValue)
        {
            state.NextAbilityAt = Time.time + 3f;
            _log.LogInfo($"DeepBot Shapeshifter plan held: bot={bot.Data?.PlayerName}, reason=no reachable concealed staging node.");
            return;
        }

        state.PendingShapeshiftTargetId = target!.PlayerId;
        state.PendingShapeshiftStagePosition = staging;
        state.ShapeshiftStageAttempts = 0;
        state.NextAbilityAt = Time.time + 0.5f;
        var routed = _actions.TryRouteToRoleAbility(
            bot,
            staging.Value,
            $"ABILITY_STAGE_SHAPESHIFTER_{target.PlayerId}",
            0.85f);
        _memory.RecordAction(
            bot,
            "ability_stage",
            $"Shapeshifter selected identity={target.Data?.PlayerName}({target.PlayerId}); routedToConcealment={routed}");
        _log.LogInfo(
            $"DeepBot Shapeshifter multi-stage plan started: bot={bot.Data?.PlayerName}, " +
            $"identity={target.Data?.PlayerName}({target.PlayerId}), stage={staging.Value}, routed={routed}.");
    }

    private void ContinueShapeshifterSequence(PlayerControl bot, AbilityState state)
    {
        var shapeshifter = bot.Data?.Role?.TryCast<ShapeshifterRole>();
        var target = state.PendingShapeshiftTargetId.HasValue
            ? FindPlayer(state.PendingShapeshiftTargetId.Value)
            : null;
        if (shapeshifter is null || !IsLegalShapeshiftIdentity(bot, target))
        {
            ClearShapeshifterSequence(state);
            state.NextAbilityAt = Time.time + 4f;
            _log.LogInfo($"DeepBot Shapeshifter sequence cancelled: bot={bot.Data?.PlayerName}, reason=role or identity target changed.");
            return;
        }

        if (shapeshifter.IsCoolingDown || shapeshifter.durationSecondsRemaining > 0.05f)
        {
            ClearShapeshifterSequence(state);
            state.NextAbilityAt = Time.time + 2f;
            return;
        }

        var staging = state.PendingShapeshiftStagePosition ?? PickShapeshiftStagingPosition(bot, null);
        if (!staging.HasValue)
        {
            ClearShapeshifterSequence(state);
            state.NextAbilityAt = Time.time + 3f;
            return;
        }

        state.PendingShapeshiftStagePosition = staging;
        var distance = Vector2.Distance(bot.GetTruePosition(), staging.Value);
        var blocked = PhysicsHelpers.AnythingBetween(
            bot.GetTruePosition(),
            staging.Value,
            Constants.ShipOnlyMask,
            false);
        if (distance > 0.95f || blocked)
        {
            var routed = _actions.TryRouteToRoleAbility(
                bot,
                staging.Value,
                $"ABILITY_STAGE_SHAPESHIFTER_{target!.PlayerId}",
                0.85f);
            state.NextAbilityAt = Time.time + (routed ? 0.55f : 1.5f);
            return;
        }

        var witnesses = CountPlayersWhoCanSee(bot);
        if (witnesses > 0)
        {
            var nextStage = PickShapeshiftStagingPosition(bot, staging);
            state.PendingShapeshiftStagePosition = nextStage;
            state.ShapeshiftStageAttempts++;
            state.NextAbilityAt = Time.time + (nextStage.HasValue ? 0.6f : 1.5f);
            if (nextStage.HasValue)
            {
                _actions.TryRouteToRoleAbility(
                    bot,
                    nextStage.Value,
                    $"ABILITY_RESTAGE_SHAPESHIFTER_{target!.PlayerId}_{state.ShapeshiftStageAttempts}",
                    0.85f);
            }
            _log.LogInfo(
                $"DeepBot Shapeshifter concealment recheck: bot={bot.Data?.PlayerName}, witnesses={witnesses}, " +
                $"restage={nextStage?.ToString() ?? "none"}, attempts={state.ShapeshiftStageAttempts}.");
            return;
        }

        try
        {
            _actions.CompleteRoleAbilityRoute(bot, "Shapeshifter-concealment-reached");
            shapeshifter.SetPlayerTarget(target);
            shapeshifter.UseAbility();
            _memory.RecordAction(
                bot,
                "ability",
                $"Shapeshifter transformed into {target!.Data?.PlayerName}({target.PlayerId}) after concealed staging");
            _log.LogInfo(
                $"DeepBot Shapeshifter sequence completed: bot={bot.Data?.PlayerName}, " +
                $"identity={target.Data?.PlayerName}({target.PlayerId}), witnesses=0.");
            ClearShapeshifterSequence(state);
            state.NextAbilityAt = Time.time + 2f;
        }
        catch (Exception ex)
        {
            ClearShapeshifterSequence(state);
            state.NextAbilityAt = Time.time + 4f;
            _log.LogWarning($"DeepBot Shapeshifter transform failed: bot={bot.Data?.PlayerName}, error={ex.Message}");
        }
    }

    private static bool IsLegalShapeshiftIdentity(PlayerControl bot, PlayerControl? target)
    {
        return target &&
               target!.PlayerId != bot.PlayerId &&
               target.Data is not null &&
               !target.Data.IsDead &&
               !target.Data.Disconnected &&
               !TorRoleAdapter.IsImpostorTeam(target);
    }

    private static int CountPlayersWhoCanSee(PlayerControl subject)
    {
        var subjectPosition = subject.GetTruePosition();
        return EnumerateLivingPlayers().Count(observer =>
            observer.PlayerId != subject.PlayerId &&
            !BotPerceptionPolicy.IsConcealedByVent(observer) &&
            Vector2.Distance(observer.GetTruePosition(), subjectPosition) <= BotPerceptionPolicy.GetCurrentVisionDistance(observer) &&
            !PhysicsHelpers.AnythingBetween(
                observer.GetTruePosition(),
                subjectPosition,
                Constants.ShipOnlyMask,
                false));
    }

    private static Vector2? PickShapeshiftStagingPosition(PlayerControl bot, Vector2? excluded)
    {
        var botPosition = bot.GetTruePosition();
        var observers = EnumerateLivingPlayers()
            .Where(player => player.PlayerId != bot.PlayerId)
            .ToArray();
        var candidates = SkeldPathGraph.Instance.Nodes
            .Where(node => node.Kind is NodeKind.Corner or NodeKind.Landmark)
            .Where(node => !excluded.HasValue || Vector2.Distance(node.Position, excluded.Value) >= 2.5f)
            .Select(node => new
            {
                Node = node,
                Travel = Vector2.Distance(botPosition, node.Position),
                NearestObserver = observers.Length == 0
                    ? 12f
                    : observers.Min(observer => Vector2.Distance(observer.GetTruePosition(), node.Position)),
                OcclusionRatio = observers.Length == 0
                    ? 1f
                    : observers.Count(observer => PhysicsHelpers.AnythingBetween(
                        observer.GetTruePosition(),
                        node.Position,
                        Constants.ShipOnlyMask,
                        false)) / (float)observers.Length
            })
            .Where(item => item.Travel is >= 1.25f and <= 14f)
            .Where(item => SkeldPathGraph.Instance.FindTopRoutes(botPosition, item.Node.Id, 1).Count > 0)
            .OrderByDescending(item => ScoreShapeshiftStageCandidate(item.NearestObserver, item.Travel, item.OcclusionRatio))
            .ThenBy(item => item.Node.Id, StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var index = (bot.PlayerId + Mathf.FloorToInt(Time.time / 7f)) % candidates.Length;
        return candidates[index].Node.Position;
    }

    private static float ScoreShapeshiftStageCandidate(float nearestObserverDistance, float travelDistance, float occlusionRatio)
    {
        return Mathf.Clamp(nearestObserverDistance, 0f, 12f) * 1.6f +
               Mathf.Clamp01(occlusionRatio) * 5f -
               Mathf.Max(0f, travelDistance) * 0.28f;
    }

    private static void ClearShapeshifterSequence(AbilityState state)
    {
        state.PendingShapeshiftTargetId = null;
        state.PendingShapeshiftStagePosition = null;
        state.ShapeshiftStageAttempts = 0;
    }

    internal static void LogSelfTest(ManualLogSource log)
    {
        var multiStageActionsPreserved =
            NormalizeAbilityAction("role") == "role" &&
            NormalizeAbilityAction("vent") == "vent" &&
            NormalizeAbilityAction("hold") == "hold" &&
            NormalizeAbilityAction("cover") == "cover" &&
            NormalizeAbilityAction("evade") == "evade" &&
            NormalizeAbilityAction("unexpected") == "hold";
        var concealedStageScoresHigher =
            ScoreShapeshiftStageCandidate(9f, 6f, 1f) >
            ScoreShapeshiftStageCandidate(3f, 3f, 0f);
        var llmSequenceCheckpointGuard =
            RequiresLlmSequenceCheckpoint("Morphling", "role") &&
            RequiresLlmSequenceCheckpoint("Trickster", "vent") &&
            !RequiresLlmSequenceCheckpoint("Vampire", "evade") &&
            IsAllowedSequenceCheckpointAction("role", "role", false) &&
            IsAllowedSequenceCheckpointAction("role", "cover", false) &&
            !IsAllowedSequenceCheckpointAction("role", "vent", false) &&
            IsAllowedSequenceCheckpointAction("role", "vent", true) &&
            !IsAllowedSequenceCheckpointAction("role", "unexpected", true);
        log.LogInfo(
            $"DeepBot ability sequence self-test: level={(concealedStageScoresHigher && multiStageActionsPreserved && llmSequenceCheckpointGuard ? "ok" : "error")}, " +
            $"shapeshifterConcealmentScoring={concealedStageScoresHigher}, " +
            $"multiStageActionsPreserved={multiStageActionsPreserved}, " +
            $"llmSequenceCheckpointGuard={llmSequenceCheckpointGuard}.");
    }

    private static string NormalizeAbilityAction(string? action)
    {
        var normalized = string.IsNullOrWhiteSpace(action)
            ? "role"
            : action.Trim().ToLowerInvariant();
        return normalized is "role" or "vent" or "hold" or "cover" or "evade"
            ? normalized
            : "hold";
    }

    private void TryUseTargetedAbility(
        PlayerControl bot,
        AbilityState state,
        string ability,
        byte? requestedTargetId)
    {
        var role = bot.Data.Role;
        var target = requestedTargetId.HasValue
            ? FindPlayer(requestedTargetId.Value)
            : FindAbilityTarget(bot, TorRoleAdapter.IsImpostorTeam(bot));
        if (target is null)
        {
            state.NextAbilityAt = Time.time + 6f;
            _log.LogInfo(
                $"DeepBot approved targeted ability held: bot={bot.Data?.PlayerName}, role={bot.Data?.RoleType}, " +
                $"requestedTarget={requestedTargetId?.ToString() ?? "none"}, reason=target unavailable.");
            return;
        }

        var distance = Vector2.Distance(bot.GetTruePosition(), target.GetTruePosition());
        if (distance > 3.5f ||
            !BotPerceptionPolicy.CanBeOrdinarilyObserved(target) ||
            (TorRoleAdapter.IsImpostorTeam(bot) && TorRoleAdapter.IsImpostorTeam(target)))
        {
            state.NextAbilityAt = Time.time + 6f;
            _log.LogInfo(
                $"DeepBot approved targeted ability held: bot={bot.Data?.PlayerName}, role={bot.Data?.RoleType}, " +
                $"target={target.Data?.PlayerName}({target.PlayerId}), distance={distance:0.0}, reason=target not legal now.");
            return;
        }

        try
        {
            role.SetPlayerTarget(target);
            role.UseAbility();
            state.NextAbilityAt = Time.time + UnityEngine.Random.Range(24f, 42f);
            _memory.RecordAction(bot, "ability", $"{ability} target={target.Data?.PlayerName}({target.PlayerId})");
            _log.LogInfo($"DeepBot role ability used: bot={bot.Data.PlayerName}, role={bot.Data.RoleType}, target={target.Data?.PlayerName}({target.PlayerId}).");
        }
        catch (Exception ex)
        {
            state.NextAbilityAt = Time.time + 6f;
            _log.LogWarning($"DeepBot targeted ability failed: bot={bot.Data.PlayerName}, role={bot.Data.RoleType}, error={ex.Message}");
        }
    }

    private void TryUsePhantom(PlayerControl bot, AbilityState state)
    {
        var phantom = bot.Data.Role.TryCast<PhantomRole>();
        if (phantom is null || phantom.IsCoolingDown || phantom.IsInvisible || phantom.IsFading)
        {
            state.NextAbilityAt = Time.time + 2f;
            return;
        }

        var nearbyCrew = EnumerateLivingPlayers()
            .Any(player =>
                player.PlayerId != bot.PlayerId &&
                BotPerceptionPolicy.CanBeOrdinarilyObserved(player) &&
                !TorRoleAdapter.IsImpostorTeam(player) &&
                Vector2.Distance(bot.GetTruePosition(), player.GetTruePosition()) <= 4.5f &&
                !PhysicsHelpers.AnythingBetween(
                    bot.GetTruePosition(),
                    player.GetTruePosition(),
                    Constants.ShipAndObjectsMask,
                    false));
        if (!nearbyCrew)
        {
            state.NextAbilityAt = Time.time + 2f;
            return;
        }

        try
        {
            phantom.UseAbility();
            state.NextAbilityAt = Time.time + UnityEngine.Random.Range(25f, 40f);
            _memory.RecordAction(bot, "ability", "phantom vanish used near crew");
            _log.LogInfo($"DeepBot role ability used: bot={bot.Data.PlayerName}, role=Phantom.");
        }
        catch (Exception ex)
        {
            state.NextAbilityAt = Time.time + 6f;
            _log.LogWarning($"DeepBot phantom ability failed: bot={bot.Data.PlayerName}, error={ex.Message}");
        }
    }

    private BotAbilityPrompt BuildAbilityPrompt(
        PlayerControl bot,
        TorRoleInfo? selectedTorRole = null,
        TorAbilitySequencePlan? sequenceCheckpoint = null)
    {
        var hasTorRole = selectedTorRole.HasValue;
        var torRole = selectedTorRole.GetValueOrDefault();
        var position = bot.GetTruePosition();
        var visible = EnumerateLivingPlayers()
            .Where(player => player.PlayerId != bot.PlayerId)
            .Where(BotPerceptionPolicy.CanBeOrdinarilyObserved)
            .Select(player => new
            {
                Player = player,
                Distance = Vector2.Distance(position, player.GetTruePosition()),
                Blocked = PhysicsHelpers.AnythingBetween(
                    position,
                    player.GetTruePosition(),
                    Constants.ShipOnlyMask,
                    false)
            })
            .Where(item => item.Distance <= 6f && !item.Blocked)
            .Select(item => $"{item.Player.Data?.PlayerName}({item.Player.PlayerId}) distance={item.Distance:0.0}")
            .ToArray();
        var nearestVent = UnityEngine.Object.FindObjectsOfType<Vent>()
            .Where(vent => vent && IsVentEligibleForRole(vent, hasTorRole ? torRole.Name : null))
            .Select(vent => Vector2.Distance(position, vent.transform.position))
            .DefaultIfEmpty(float.MaxValue)
            .Min();
        var livingPlayers = EnumerateLivingPlayers().ToArray();
        var visibleBodies = UnityEngine.Object.FindObjectsOfType<DeadBody>()
            .Where(DeadBodyPerception.IsVisibleAndReportable)
            .Select(body => new
            {
                Body = body,
                Distance = Vector2.Distance(position, body.TruePosition),
                Blocked = PhysicsHelpers.AnythingBetween(position, body.TruePosition, Constants.ShipAndObjectsMask, false),
                Witnesses = livingPlayers.Count(player =>
                    player.PlayerId != bot.PlayerId &&
                    BotPerceptionPolicy.CanBeOrdinarilyObserved(player) &&
                    Vector2.Distance(player.GetTruePosition(), body.TruePosition) <= 3.25f &&
                    !PhysicsHelpers.AnythingBetween(player.GetTruePosition(), body.TruePosition, Constants.ShipAndObjectsMask, false))
            })
            .Where(item => item.Distance <= 6f && !item.Blocked)
            .Select(item => $"body victim={item.Body.ParentId} distance={item.Distance:0.0} nearbyWitnesses={item.Witnesses}")
            .ToArray();
        var ventAccess = hasTorRole
            ? TorRoleAdapter.CanUseVents(bot, torRole)
            : bot.Data?.Role?.CanVent == true;
        return new BotAbilityPrompt(
            bot.PlayerId,
            bot.Data?.PlayerName ?? $"DeepBot {bot.PlayerId}",
            hasTorRole ? torRole.Alignment : TorRoleAdapter.IsImpostorTeam(bot) ? "impostor" : "crewmate",
            hasTorRole ? torRole.Name : bot.Data?.RoleType.ToString() ?? "unknown",
            hasTorRole
                ? TorRoleAdapter.BuildStrategicRoleBrief(bot, torRole) +
                  (torRole.ActiveAbility
                      ? string.Empty
                      : " This role has no separate active role button in the ability controller; choose only vent or hold here. Ordinary kills, tasks, meetings, and cover movement are handled by their dedicated controllers.") +
                  (sequenceCheckpoint.HasValue
                      ? $" CURRENT NATIVE SEQUENCE CHECKPOINT: action={sequenceCheckpoint.Value.AbilityAction}; " +
                        $"fixedTarget={sequenceCheckpoint.Value.TargetPlayerId?.ToString() ?? "none"}; " +
                        $"stage={sequenceCheckpoint.Value.Reason}. Decide whether to continue now, briefly hold, " +
                        "take cover, evade, or use a legal vent detour. Never restart stage one or replace the fixed target."
                      : string.Empty)
                : DescribeAbilityPurpose(bot),
            $"position={position}; node={SkeldPathGraph.Instance.NearestNode(position).Id}; " +
            $"killCooldown={bot.killTimer:0.0}; ventAccess={ventAccess}; nearestVent={nearestVent:0.0}; " +
            $"emergency={HasActiveEmergency(bot)}; roomRules=[{GameRuleSettings.CaptureSnapshot().Describe()}]",
            visible.Length == 0 ? "none" : string.Join("; ", visible),
            visibleBodies.Length == 0 ? "no visible usable body" : string.Join("; ", visibleBodies),
            _memory.BuildTimeline(bot.PlayerId, 20));
    }

    private static string DescribeAbilityPurpose(PlayerControl bot)
    {
        return bot.Data?.RoleType switch
        {
            RoleTypes.Engineer => "Engineer vent: take a meaningful shortcut, escape a persistent follower, or rotate to an emergency; never vent randomly in front of witnesses.",
            RoleTypes.Scientist => "Scientist vitals: spend limited battery only when checking whether a recent disappearance or danger corresponds to a death.",
            RoleTypes.Tracker => "Tracker: mark a useful trusted or suspicious player so their movement can be followed later.",
            RoleTypes.GuardianAngel => "Guardian Angel: protect a living player who appears isolated or likely to be attacked.",
            RoleTypes.Phantom => "Phantom: become invisible to escape witnesses, conceal a rotation, or approach an isolated target.",
            RoleTypes.Shapeshifter => "Shapeshifter: copy a crew appearance before a planned deception or kill, preferably while unobserved.",
            RoleTypes.Detective => "Detective: investigate a player whose recent behavior or meeting claims create a useful suspicion.",
            _ when bot.Data?.Role?.CanVent == true && TorRoleAdapter.IsImpostorTeam(bot) =>
                "Impostor vent: covertly escape a dangerous scene or reposition for a planned kill; never vent with witnesses.",
            _ => "No strategically useful active ability."
        };
    }

    private BotAbilityDecision BuildStrategicFallback(PlayerControl bot, TorRoleInfo? selectedTorRole = null)
    {
        var hasTorRole = selectedTorRole.HasValue;
        var torRole = selectedTorRole.GetValueOrDefault();
        var botIsImpostor = hasTorRole ? torRole.IsImpostorTeam : TorRoleAdapter.IsImpostorTeam(bot);
        var target = FindAbilityTarget(bot, botIsImpostor);
        var visibleCrew = EnumerateLivingPlayers()
            .Where(player =>
                player.PlayerId != bot.PlayerId &&
                BotPerceptionPolicy.CanBeOrdinarilyObserved(player) &&
                (!TorRoleAdapter.IsImpostorTeam(player) || !botIsImpostor) &&
                Vector2.Distance(bot.GetTruePosition(), player.GetTruePosition()) <= 5f &&
                !PhysicsHelpers.AnythingBetween(
                    bot.GetTruePosition(),
                    player.GetTruePosition(),
                    Constants.ShipOnlyMask,
                    false))
            .ToArray();
        if (hasTorRole)
        {
            var recentVentTarget = TryGetRecentVentAmbushTarget(bot);
            if (TorRoleAdapter.CanUseVents(bot, torRole) &&
                recentVentTarget is not null &&
                visibleCrew.Length == 0 &&
                FindClosestVent(bot, 1.35f, torRole.Name) is not null &&
                (torRole.IsImpostorTeam || torRole.Name is "Jackal" or "Sidekick" or "Thief"))
            {
                return new BotAbilityDecision(
                    true,
                    recentVentTarget.PlayerId,
                    $"hide in a nearby vent and ambush recently seen opponent {recentVentTarget.Data?.PlayerName} without entering in view",
                    0.69f,
                    "vent");
            }

            var torTarget = torRole.Name == "Arsonist"
                ? TorRoleAdapter.FindArsonistPursuitTarget(bot) ?? TorRoleAdapter.FindPreferredAbilityTarget(bot, torRole) ?? target
                : TorRoleAdapter.FindPreferredAbilityTarget(bot, torRole) ?? target;
            var sheriffTarget = FindEvidenceBackedSuspect(bot, 0.78f);
            var deputyTarget = FindEvidenceBackedSuspect(bot, 0.62f);
            var mayorTarget = FindEvidenceBackedSuspect(bot, 0.86f);
            var thiefTarget = FindObservedRoleTarget(
                bot,
                (roleName, alignment) =>
                    string.Equals(alignment, "impostor", StringComparison.Ordinal) ||
                    roleName is "Jackal" or "Sidekick",
                0.88f);
            var observedCrewSpecialist = FindObservedRoleTarget(
                bot,
                (_, alignment) => string.Equals(alignment, "crewmate", StringComparison.Ordinal),
                0.90f);
            return torRole.Name switch
            {
                "Medic" when torTarget is not null =>
                    new BotAbilityDecision(true, torTarget.PlayerId, "shield a nearby vulnerable or strategically useful player", 0.61f),
                "Portalmaker" when visibleCrew.Length <= 2 =>
                    new BotAbilityDecision(true, null, "place a portal in a separated useful room to improve later rotations", 0.62f),
                "Engineer" when HasActiveEmergency(bot) =>
                    new BotAbilityDecision(true, null, "spend one limited repair charge because a dangerous sabotage is active", 0.84f),
                "Mayor" when mayorTarget is not null =>
                    new BotAbilityDecision(true, null, "call the limited remote meeting because a personally retained high-confidence suspect is still alive", 0.88f),
                "Mayor" =>
                    new BotAbilityDecision(false, null, "preserve the limited remote meeting until personal evidence crosses a high threshold", 0.78f),
                "Sheriff" when sheriffTarget is not null =>
                    new BotAbilityDecision(true, sheriffTarget.PlayerId, "shoot the high-confidence suspect retained from the latest meeting conclusion", 0.82f),
                "Sheriff" =>
                    new BotAbilityDecision(false, null, "do not shoot without a high-confidence meeting or witnessed-behavior case", 0.72f),
                "Deputy" when deputyTarget is not null =>
                    new BotAbilityDecision(true, deputyTarget.PlayerId, "handcuff the evidence-backed suspect retained from the latest meeting", 0.70f),
                "Deputy" =>
                    new BotAbilityDecision(false, null, "do not handcuff a random nearby player without a meeting-backed suspicion", 0.64f),
                "Tracker" when torTarget is not null =>
                    new BotAbilityDecision(true, torTarget.PlayerId, "track a nearby player whose route can provide evidence", 0.60f),
                "Morphling" when torTarget is not null && visibleCrew.Length <= 1 =>
                    new BotAbilityDecision(true, torTarget.PlayerId, "copy an isolated player before a concealed deception", 0.63f),
                "Vampire" when torTarget is not null && visibleCrew.Length <= 1 =>
                    new BotAbilityDecision(true, torTarget.PlayerId, "bite an isolated target whose delayed death will not immediately expose the attacker", 0.68f),
                "Warlock" when torTarget is not null && visibleCrew.Length <= 2 =>
                    new BotAbilityDecision(true, torTarget.PlayerId, "curse a mobile carrier who can approach another isolated opponent and conceal the caster", 0.64f),
                "Ninja" when torTarget is not null && visibleCrew.Length <= 1 =>
                    new BotAbilityDecision(true, torTarget.PlayerId, "mark or strike an isolated target using invisibility as a planned escape", 0.67f),
                "Jackal" when torTarget is not null =>
                    new BotAbilityDecision(true, torTarget.PlayerId, "recruit or eliminate this isolated target according to the current Jackal phase", 0.60f),
                "Sidekick" when torTarget is not null && visibleCrew.Length <= 2 =>
                    new BotAbilityDecision(true, torTarget.PlayerId, "take a safe isolated kill for the Jackal faction", 0.62f),
                "Arsonist" when TorRoleAdapter.IsArsonistReadyToIgnite(bot) =>
                    new BotAbilityDecision(true, null, "all other living players are doused; ignite now to complete the independent win", 0.99f),
                "Arsonist" when torTarget is not null =>
                    new BotAbilityDecision(true, torTarget.PlayerId, "channel the next required douse on an undoused nearby player", 0.72f),
                "Pursuer" when torTarget is not null && visibleCrew.Length <= 2 =>
                    new BotAbilityDecision(true, torTarget.PlayerId, "blank a nearby danger to improve survival odds", 0.60f),
                "Thief" when thiefTarget is not null =>
                    new BotAbilityDecision(
                        true,
                        thiefTarget.PlayerId,
                        $"attempt theft only because a personally witnessed role action strongly identifies {thiefTarget.Data?.PlayerName} as an eligible hostile",
                        0.92f),
                "Thief" =>
                    new BotAbilityDecision(false, null, "do not risk a blind theft without personally witnessed role evidence because an illegal target causes suicide", 0.84f),
                "Eraser" when observedCrewSpecialist is not null =>
                    new BotAbilityDecision(
                        true,
                        observedCrewSpecialist.PlayerId,
                        $"erase personally identified crew specialist {observedCrewSpecialist.Data?.PlayerName} at the next resolution",
                        0.86f),
                "Eraser" =>
                    new BotAbilityDecision(false, null, "hold erasure until a high-value opposing role is personally evidenced", 0.72f),
                "Witch" when torTarget is not null && visibleCrew.Length <= 2 =>
                    new BotAbilityDecision(true, torTarget.PlayerId, "spell an isolated high-value opponent without exposing the caster", 0.62f),
                "Shifter" when observedCrewSpecialist is not null =>
                    new BotAbilityDecision(
                        true,
                        observedCrewSpecialist.PlayerId,
                        $"shift with personally identified crew specialist {observedCrewSpecialist.Data?.PlayerName} instead of choosing blindly",
                        0.84f),
                "Shifter" =>
                    new BotAbilityDecision(false, null, "hold the one-shot role exchange until a useful target role is personally evidenced", 0.76f),
                "TimeMaster" when visibleCrew.Length is >= 1 and <= 3 =>
                    new BotAbilityDecision(true, null, "raise a time shield while nearby players create credible danger", 0.60f),
                "Camouflager" when visibleCrew.Length is >= 1 and <= 3 =>
                    new BotAbilityDecision(true, null, "conceal identities before a planned hostile rotation", 0.62f),
                "Hacker" when GameData.Instance?.AllPlayers.ToArray().Any(player => player is not null && player.IsDead) == true =>
                    new BotAbilityDecision(true, null, "spend a vitals charge after a death to update the evidence timeline", 0.68f),
                "Hacker" when visibleCrew.Length == 0 =>
                    new BotAbilityDecision(true, null, "spend an admin charge while isolated to learn anonymous room occupancy", 0.61f),
                "Medium" when TorRoleAdapter.TryGetNearestMediumSoulPosition(bot, out _) =>
                    new BotAbilityDecision(true, null, "approach and question a recorded soul so its TOR-generated clue enters the evidence timeline", 0.67f),
                "Cleaner" or "Janitor" or "Vulture" when TorRoleAdapter.HasNearbyUsableBody(bot) =>
                    new BotAbilityDecision(true, null, "remove a nearby unwitnessed body before it can be reported", 0.66f),
                "Trapper" when visibleCrew.Length >= 1 =>
                    new BotAbilityDecision(true, null, "place an information trap at the current occupied chokepoint", 0.60f),
                "Trickster" when visibleCrew.Length <= 2 =>
                    new BotAbilityDecision(true, null, "expand the concealed box network or use its darkness for a concrete hostile play", 0.63f),
                "SecurityGuard" when visibleCrew.Length >= 1 =>
                    new BotAbilityDecision(true, null, "spend screws on a camera at this occupied chokepoint", 0.60f),
                "Bomber" when visibleCrew.Length is >= 1 and <= 3 =>
                    new BotAbilityDecision(true, null, "plant a bomb where nearby traffic creates a deliberate split or elimination", 0.65f),
                "Yoyo" when visibleCrew.Length <= 2 =>
                    new BotAbilityDecision(true, null, "mark or use a return point for a planned alibi and concealed rotation", 0.62f),
                _ => new BotAbilityDecision(false, null, "no meaningful custom-role ability purpose in the current situation", 0.58f)
            };
        }

        return bot.Data.RoleType switch
        {
            RoleTypes.Scientist when GameData.Instance?.AllPlayers.ToArray().Any(player => player is not null && player.IsDead) == true =>
                new BotAbilityDecision(true, null, "check vitals after a death may have occurred", 0.61f),
            RoleTypes.Engineer when HasActiveEmergency(bot) && FindClosestVent(bot, 1.45f) is not null =>
                new BotAbilityDecision(true, null, "use vent as an emergency rotation shortcut", 0.62f),
            RoleTypes.Tracker when target is not null =>
                new BotAbilityDecision(true, target.PlayerId, "track a nearby player for later route evidence", 0.60f),
            RoleTypes.GuardianAngel when target is not null && visibleCrew.Length <= 2 =>
                new BotAbilityDecision(true, target.PlayerId, "protect an isolated nearby player", 0.64f),
            RoleTypes.Phantom when visibleCrew.Length is >= 1 and <= 2 =>
                new BotAbilityDecision(true, null, "vanish near a small number of crew to conceal movement", 0.63f),
            RoleTypes.Shapeshifter when target is not null && visibleCrew.Length == 1 =>
                new BotAbilityDecision(true, target.PlayerId, "copy an isolated crew member for a planned deception", 0.62f),
            RoleTypes.Detective when target is not null =>
                new BotAbilityDecision(true, target.PlayerId, "investigate the nearest useful encounter", 0.58f),
            _ when bot.Data.Role.CanVent &&
                   TorRoleAdapter.IsImpostorTeam(bot) &&
                   TryGetRecentVentAmbushTarget(bot) is { } recentTarget &&
                   FindClosestVent(bot, 1.35f) is not null &&
                   visibleCrew.Length == 0 &&
                   bot.killTimer <= 0.05f =>
                new BotAbilityDecision(true, recentTarget.PlayerId, "hide in a nearby vent and wait for a recently seen target to return", 0.67f, "vent"),
            _ when bot.Data.Role.CanVent &&
                   TorRoleAdapter.IsImpostorTeam(bot) &&
                   FindClosestVent(bot, 1.35f) is not null &&
                   bot.killTimer > 0f &&
                   visibleCrew.Length <= 1 =>
                new BotAbilityDecision(true, null, "vent covertly while kill cooldown runs", 0.62f),
            _ => new BotAbilityDecision(false, null, "no meaningful ability purpose in the current situation", 0.58f)
        };
    }

    private PlayerControl? TryGetRecentVentAmbushTarget(PlayerControl bot)
    {
        if (!_memory.TryGetRecentPersonallySeenLivingPlayer(bot.PlayerId, 8f, out var targetId))
        {
            return null;
        }

        var target = FindPlayer(targetId);
        if (!target ||
            target!.PlayerId == bot.PlayerId ||
            TorRoleAdapter.IsImpostorTeam(target) ||
            TorRoleAdapter.AreLoverPartners(bot, target))
        {
            return null;
        }

        return target;
    }

    private static bool ShouldExitVentForAmbush(PlayerControl bot, int ventId, byte? plannedTargetId)
    {
        if (!bot ||
            bot.Data is null ||
            bot.Data.RoleType == RoleTypes.Engineer)
        {
            return false;
        }

        var hasTorRole = TorRoleAdapter.TryGetRole(bot, out var torRole);
        var isHostileAmbusher = hasTorRole
            ? torRole.IsImpostorTeam || torRole.Name is "Jackal" or "Sidekick" or "Thief"
            : TorRoleAdapter.IsImpostorTeam(bot);
        if (!isHostileAmbusher || bot.killTimer > 0.05f)
        {
            return false;
        }

        var vent = UnityEngine.Object.FindObjectsOfType<Vent>().FirstOrDefault(item => item && item.Id == ventId);
        if (!vent)
        {
            return false;
        }

        var ventPosition = (Vector2)vent!.transform.position;
        var nearbyOpponents = EnumerateLivingPlayers()
            .Where(player =>
                player.PlayerId != bot.PlayerId &&
                !TorRoleAdapter.AreKnownAllies(bot, player) &&
                !TorRoleAdapter.AreLoverPartners(bot, player) &&
                BotPerceptionPolicy.CanBeOrdinarilyObserved(player) &&
                Vector2.Distance(ventPosition, player.GetTruePosition()) <= 2.35f &&
                !PhysicsHelpers.AnythingBetween(ventPosition, player.GetTruePosition(), Constants.ShipOnlyMask, false))
            .ToArray();
        if (nearbyOpponents.Length == 0 || nearbyOpponents.Length > 2)
        {
            return false;
        }

        return !plannedTargetId.HasValue || nearbyOpponents.Any(player => player.PlayerId == plannedTargetId.Value);
    }

    private BotAbilityDecision ApplyObjectiveCriticalOverride(
        PlayerControl bot,
        TorRoleInfo role,
        BotAbilityDecision decision)
    {
        if (role.Name is "Trickster" or "Portalmaker" &&
            TorRoleAdapter.TryGetAbilityStagingDestination(
                bot,
                role,
                out _,
                out var setupStage,
                out _))
        {
            // Setup stages are prerequisites for the role to become useful.
            // An API outage or cautious model response may delay them briefly,
            // but must not leave the role permanently without boxes/portals.
            return new BotAbilityDecision(
                true,
                null,
                $"native setup progression override: continue {setupStage}",
                0.88f,
                "role",
                $"complete {role.Name} setup",
                setupStage,
                "abort if TOR reports the setup stage is no longer legal",
                1.0f);
        }

        if (role.Name == "Engineer" && HasActiveEmergency(bot) &&
            BotBehaviorPolicy.ShouldOverrideForRoleObjective(role.Name, true))
        {
            return new BotAbilityDecision(
                true,
                null,
                "hard-rule override: spend the limited repair because an unresolved sabotage is active",
                0.94f);
        }

        if (role.Name == "Arsonist")
        {
            if (TorRoleAdapter.IsArsonistReadyToIgnite(bot) &&
                BotBehaviorPolicy.ShouldOverrideForRoleObjective(role.Name, true))
            {
                return new BotAbilityDecision(
                    true,
                    null,
                    "hard-rule override: every other living player is doused, so ignite now",
                    0.99f);
            }

            var target = TorRoleAdapter.FindArsonistPursuitTarget(bot);
            if (target is not null &&
                BotBehaviorPolicy.ShouldOverrideForRoleObjective(role.Name, true))
            {
                return new BotAbilityDecision(
                    true,
                    target.PlayerId,
                    $"hard-rule override: approach undoused visible target {target.Data?.PlayerName} and channel douse",
                    0.90f);
            }
        }

        if (role.Name is "Vulture" or "Cleaner" or "Janitor" &&
            TorRoleAdapter.FindVisibleUsableBody(bot) is { } body)
        {
            var personallyVisibleWitnesses = EnumerateLivingPlayers()
                .Count(player =>
                    player.PlayerId != bot.PlayerId &&
                    BotPerceptionPolicy.CanBeOrdinarilyObserved(player) &&
                    Vector2.Distance(bot.GetTruePosition(), player.GetTruePosition()) <= 6f &&
                    Vector2.Distance(player.GetTruePosition(), body.TruePosition) <= 3.25f &&
                    !PhysicsHelpers.AnythingBetween(
                        bot.GetTruePosition(),
                        player.GetTruePosition(),
                        Constants.ShipOnlyMask,
                        false));
            var objectiveOpportunity = role.Name == "Vulture" || personallyVisibleWitnesses == 0;
            if (BotBehaviorPolicy.ShouldOverrideForRoleObjective(role.Name, objectiveOpportunity))
            {
                return new BotAbilityDecision(
                    true,
                    null,
                    $"hard-rule override: visible body advances {role.Name} objective; personallyVisibleWitnesses={personallyVisibleWitnesses}",
                    role.Name == "Vulture" ? 0.96f : 0.88f);
            }
        }

        return decision;
    }

    private PlayerControl? FindEvidenceBackedSuspect(PlayerControl bot, float minimumConfidence)
    {
        if (_memory.TryGetLatestWitnessedKiller(bot.PlayerId, out var witnessedKillerId))
        {
            var witnessedKiller = FindPlayer(witnessedKillerId);
            if (IsPersonallyReachableTarget(bot, witnessedKiller) &&
                !TorRoleAdapter.AreKnownAllies(bot, witnessedKiller))
            {
                return witnessedKiller;
            }
        }

        if (!_memory.TryGetPostMeetingIntent(bot.PlayerId, out var intent) ||
            intent.FollowIntent != "suspect" ||
            !intent.FollowPlayerId.HasValue ||
            intent.Confidence < minimumConfidence)
        {
            return null;
        }

        var target = FindPlayer(intent.FollowPlayerId.Value);
        if (!BotPerceptionPolicy.CanBeOrdinarilyObserved(target) || target!.PlayerId == bot.PlayerId)
        {
            return null;
        }

        var distance = Vector2.Distance(bot.GetTruePosition(), target.GetTruePosition());
        if (distance > 6f || PhysicsHelpers.AnythingBetween(
                bot.GetTruePosition(),
                target.GetTruePosition(),
                Constants.ShipOnlyMask,
                false))
        {
            return null;
        }

        return target;
    }

    private PlayerControl? FindObservedRoleTarget(
        PlayerControl bot,
        Func<string, string, bool> acceptsPublicRole,
        float minimumInferenceConfidence)
    {
        return EnumerateLivingPlayers()
            .Where(player => player.PlayerId != bot.PlayerId)
            .Where(player => !TorRoleAdapter.AreKnownAllies(bot, player))
            .Where(player => IsPersonallyReachableTarget(bot, player))
            .Select(player => new
            {
                Player = player,
                Evidence = _memory.GetPersonalRoleEvidence(bot.PlayerId, player.PlayerId)
            })
            .Where(item =>
                item.Evidence.HasObservation &&
                item.Evidence.InferenceConfidence >= minimumInferenceConfidence &&
                !string.IsNullOrWhiteSpace(item.Evidence.InferredRoleName) &&
                TorRoleAdapter.TryGetPublicRoleAlignment(item.Evidence.InferredRoleName!, out var alignment) &&
                acceptsPublicRole(item.Evidence.InferredRoleName!, alignment))
            .OrderByDescending(item => item.Evidence.InferenceConfidence)
            .ThenBy(item => Vector2.Distance(bot.GetTruePosition(), item.Player.GetTruePosition()))
            .Select(item => item.Player)
            .FirstOrDefault();
    }

    private static bool IsPersonallyReachableTarget(PlayerControl bot, PlayerControl? target)
    {
        return target &&
               target!.Data is not null &&
               !target.Data.IsDead &&
               !target.Data.Disconnected &&
               BotPerceptionPolicy.CanBeOrdinarilyObserved(target) &&
               Vector2.Distance(bot.GetTruePosition(), target.GetTruePosition()) <= 6f &&
               !PhysicsHelpers.AnythingBetween(
                   bot.GetTruePosition(),
                   target.GetTruePosition(),
                   Constants.ShipOnlyMask,
                   false);
    }

    private static bool TrySelectTorAbilityRole(PlayerControl bot, AbilityState state, out TorRoleInfo role)
    {
        var roles = TorRoleAdapter.GetAbilityRoles(bot);
        if (roles.Count == 0)
        {
            role = default;
            return false;
        }

        for (var offset = 0; offset < roles.Count; offset++)
        {
            var candidate = roles[(state.AbilityRoleCursor + offset) % roles.Count];
            if (BotBehaviorPolicy.ShouldSelectStrategicAbilityRole(
                    TorRoleAdapter.IsAbilityReady(bot, candidate),
                    TorRoleAdapter.CanUseVents(bot, candidate),
                    TorRoleAdapter.IsAbilitySequencePending(bot, candidate)))
            {
                role = candidate;
                return true;
            }
        }

        role = default;
        return false;
    }

    private static bool SupportsStrategicAbility(PlayerControl bot)
    {
        return TorRoleAdapter.GetAbilityRoles(bot)
                   .Any(torRole => torRole.ActiveAbility || TorRoleAdapter.CanUseVents(bot, torRole)) ||
               bot.Data?.RoleType is
                   RoleTypes.Scientist or
                   RoleTypes.Engineer or
                   RoleTypes.Tracker or
                   RoleTypes.GuardianAngel or
                   RoleTypes.Phantom or
                   RoleTypes.Shapeshifter or
                   RoleTypes.Detective ||
               (bot.Data?.Role?.CanVent == true && TorRoleAdapter.IsImpostorTeam(bot));
    }

    private void TryUseOrRouteVent(PlayerControl bot, AbilityState state, string role, byte? ambushTargetId = null)
    {
        if (bot.Data?.RoleType == RoleTypes.Engineer)
        {
            if (FindClosestVent(bot, 1.45f) is null) TryRouteToVent(bot, state, role);
            else TryUseEngineerVent(bot, state);
            return;
        }

        var hasTorRole = TorRoleAdapter.TryGetRole(bot, out var torRole);
        var vent = FindClosestVent(bot, 1.35f, hasTorRole ? torRole.Name : role);
        if (vent is null)
        {
            TryRouteToVent(bot, state, role);
            return;
        }

        try
        {
            if (hasTorRole && string.Equals(torRole.Name, "Engineer", StringComparison.Ordinal))
            {
                if (!TorRoleAdapter.TryEnterTorEngineerVent(bot, vent.Id, out var nativeOutcome))
                {
                    state.NextAbilityAt = Time.time + 2f;
                    _log.LogInfo(
                        $"DeepBot TOR Engineer vent held by native room rules: bot={bot.Data?.PlayerName}, " +
                        $"vent={vent.Id}, reason={nativeOutcome}.");
                    return;
                }

                _log.LogInfo(
                    $"DeepBot TOR Engineer vent accepted by native rules: bot={bot.Data?.PlayerName}, " +
                    $"vent={vent.Id}, outcome={nativeOutcome}.");
            }
            else
            {
                bot.MyPhysics.RpcEnterVent(vent.Id);
            }
            _actions.CompleteRoleAbilityRoute(bot, $"{role}-entered-vent-{vent.Id}");
            BeginVentEntryTracking(bot, state, vent.Id, role);
            state.VentAmbushTargetId = ambushTargetId;
            state.MinimumVentHoldUntil = Time.time + (ambushTargetId.HasValue ? 1.8f : 1.0f);
            var plannedVentSeconds = ambushTargetId.HasValue
                ? UnityEngine.Random.Range(
                    string.Equals(role, "Trickster", StringComparison.Ordinal) ? 8f : 5f,
                    string.Equals(role, "Trickster", StringComparison.Ordinal) ? 13f : 9f)
                : UnityEngine.Random.Range(2.2f, 4.8f);
            state.ExitVentAt = Time.time + plannedVentSeconds;
            state.NextAbilityAt = Time.time + UnityEngine.Random.Range(18f, 34f);
            _memory.RecordAction(bot, "ability", $"{role} entered vent {vent.Id} after LLM approval");
            _log.LogInfo($"DeepBot role vent used after LLM approval: bot={bot.Data?.PlayerName}, role={(hasTorRole ? torRole.Name : role)}, vent={vent.Id}.");
        }
        catch (Exception ex)
        {
            state.NextAbilityAt = Time.time + 5f;
            _log.LogWarning($"DeepBot role vent failed: bot={bot.Data?.PlayerName}, role={role}, vent={vent.Id}, error={ex.Message}");
        }
    }

    private void BeginVentEntryTracking(PlayerControl bot, AbilityState state, int ventId, string role)
    {
        state.PendingVentId = ventId;
        state.PendingVentRole = role;
        state.VentEntryRequestedAt = Time.time;
        state.ActiveVentId = bot.inVent ? ventId : null;
        if (bot.inVent)
        {
            ConfirmVentEntry(bot, state);
        }
    }

    private void ConfirmVentEntry(PlayerControl bot, AbilityState state)
    {
        if (!bot.inVent || !state.PendingVentId.HasValue)
        {
            return;
        }

        state.ActiveVentId = state.PendingVentId;
        state.PendingVentId = null;
        state.PendingVentRole = null;
        state.VentEntryRequestedAt = 0f;
    }

    private bool HandlePendingVentEntry(PlayerControl bot, AbilityState state)
    {
        if (!state.PendingVentId.HasValue || state.VentEntryRequestedAt <= 0f)
        {
            return false;
        }

        if (bot.inVent)
        {
            ConfirmVentEntry(bot, state);
            return false;
        }

        var elapsed = Time.time - state.VentEntryRequestedAt;
        if (elapsed < 2.25f)
        {
            // Let TOR/Among Us finish the native enter animation.  Blocking
            // movement during this short window is expected.
            return bot.walkingToVent;
        }

        var failedVentId = state.PendingVentId.Value;
        var failedRole = state.PendingVentRole ?? "vent-capable role";
        bot.walkingToVent = false;
        if (!TorRoleAdapter.IsRuleImmobilized(bot) && !MeetingHud.Instance && !ExileController.Instance)
        {
            bot.moveable = true;
        }
        if (bot.NetTransform)
        {
            bot.NetTransform.Halt();
        }
        if (bot.MyPhysics)
        {
            bot.MyPhysics.SetNormalizedVelocity(Vector2.zero);
            if (bot.MyPhysics.body)
            {
                bot.MyPhysics.body.velocity = Vector2.zero;
            }
        }

        state.PendingVentId = null;
        state.PendingVentRole = null;
        state.VentEntryRequestedAt = 0f;
        state.ActiveVentId = null;
        state.VentAmbushTargetId = null;
        state.MinimumVentHoldUntil = 0f;
        state.ExitVentAt = 0f;
        state.NextAbilityAt = Time.time + 12f;
        _actions.CompleteRoleAbilityRoute(bot, $"{failedRole}-native-vent-entry-timeout-{failedVentId}");
        _memory.RecordAction(
            bot,
            "ability",
            $"{failedRole} native vent entry {failedVentId} timed out; movement released and vent plan abandoned");
        _log.LogWarning(
            $"DeepBot native vent entry timeout repaired: bot={bot.Data?.PlayerName}({bot.PlayerId}), " +
            $"role={failedRole}, vent={failedVentId}, elapsed={elapsed:0.00}s, " +
            "walkingToVent cleared, movement restored, retry delayed.");
        return false;
    }

    private static bool IsRoleCoolingDown(RoleBehaviour role)
    {
        if (role.TryCast<EngineerRole>() is { } engineer)
        {
            return engineer.IsCoolingDown || engineer.usesRemaining == 0;
        }
        if (role.TryCast<ScientistRole>() is { } scientist)
        {
            return scientist.IsCoolingDown || scientist.currentCharge <= 0.05f;
        }
        if (role.TryCast<TrackerRole>() is { } tracker)
        {
            return tracker.IsCoolingDown;
        }
        if (role.TryCast<GuardianAngelRole>() is { } guardian)
        {
            return guardian.IsCoolingDown;
        }
        if (role.TryCast<PhantomRole>() is { } phantom)
        {
            return phantom.IsCoolingDown || phantom.IsInvisible || phantom.IsFading;
        }
        if (role.TryCast<ShapeshifterRole>() is { } shapeshifter)
        {
            return shapeshifter.IsCoolingDown || shapeshifter.durationSecondsRemaining > 0.05f;
        }
        if (role.TryCast<DetectiveRole>() is { } detective)
        {
            return detective.IsCoolingDown;
        }
        return false;
    }

    private static bool HasActiveEmergency(PlayerControl bot)
    {
        if (bot.myTasks is null)
        {
            return false;
        }

        for (var index = 0; index < bot.myTasks.Count; index++)
        {
            var task = bot.myTasks[index];
            if (task &&
                !task.IsComplete &&
                task.TaskType is
                    TaskTypes.FixLights or
                    TaskTypes.FixComms or
                    TaskTypes.ResetReactor or
                    TaskTypes.RestoreOxy or
                    TaskTypes.ResetSeismic or
                    TaskTypes.StopCharles)
            {
                return true;
            }
        }

        return false;
    }

    private static Vent? FindClosestVent(PlayerControl bot, float maximumDistance, string? roleName = null)
    {
        var position = bot.GetTruePosition();
        return UnityEngine.Object.FindObjectsOfType<Vent>()
            .Where(vent => vent && IsVentEligibleForRole(vent, roleName))
            .Select(vent => new { Vent = vent, Distance = Vector2.Distance(position, vent.transform.position) })
            .Where(item => item.Distance <= maximumDistance)
            .OrderBy(item => item.Distance)
            .Select(item => item.Vent)
            .FirstOrDefault();
    }

    private static bool IsVentEligibleForRole(Vent vent, string? roleName)
    {
        if (!vent)
        {
            return false;
        }

        var isTricksterBox = (vent!.name ?? string.Empty)
            .StartsWith("JackInTheBoxVent_", StringComparison.OrdinalIgnoreCase);
        return string.Equals(roleName, "Trickster", StringComparison.Ordinal)
            ? isTricksterBox
            : !isTricksterBox;
    }

    private void TryRouteToVent(PlayerControl bot, AbilityState state, string role)
    {
        var position = bot.GetTruePosition();
        var vent = UnityEngine.Object.FindObjectsOfType<Vent>()
            .Where(candidate => candidate && IsVentEligibleForRole(candidate, role))
            .OrderBy(candidate => Vector2.Distance(position, candidate.transform.position))
            .FirstOrDefault();
        if (vent is null)
        {
            state.NextAbilityAt = Time.time + 3f;
            return;
        }

        var routed = _actions.TryRouteToRoleAbility(
            bot,
            vent.transform.position,
            $"ABILITY_VENT_{vent.Id}",
            1.15f);
        state.NextAbilityAt = Time.time + (routed ? 1.25f : 3f);
        if (routed && Time.time >= state.NextRouteLogAt)
        {
            state.NextRouteLogAt = Time.time + 8f;
            _log.LogInfo(
                $"DeepBot role ability route assigned: bot={bot.Data?.PlayerName}, role={role}, " +
                $"vent={vent.Id}, position={vent.transform.position}.");
        }
    }

    private static PlayerControl? FindAbilityTarget(PlayerControl bot, bool impostor)
    {
        var position = bot.GetTruePosition();
        return EnumerateLivingPlayers()
            .Where(player => player.PlayerId != bot.PlayerId)
            .Where(BotPerceptionPolicy.CanBeOrdinarilyObserved)
            .Where(player => !impostor || !TorRoleAdapter.IsImpostorTeam(player))
            .Select(player => new { Player = player, Distance = Vector2.Distance(position, player.GetTruePosition()) })
            .Where(item => item.Distance <= 3.5f)
            .OrderBy(item => item.Distance)
            .Select(item => item.Player)
            .FirstOrDefault();
    }

    private static PlayerControl? FindPlayer(byte playerId)
    {
        return PlayerControl.AllPlayerControls
            .ToArray()
            .FirstOrDefault(player => player && player.PlayerId == playerId);
    }

    private AbilityState GetState(PlayerControl bot)
    {
        if (!_states.TryGetValue(bot.PlayerId, out var state))
        {
            state = new AbilityState
            {
                NextAbilityAt = Time.time + UnityEngine.Random.Range(8f, 18f)
            };
            _states[bot.PlayerId] = state;
        }

        return state;
    }

    private static bool IsHostAuthority()
    {
        var client = AmongUsClient.Instance;
        return client &&
            client.NetworkMode == NetworkModes.LocalGame &&
            Plugin.AllowsWorldAuthority(client.AmHost) &&
            client.ClientId >= 0 &&
            client.ClientId == client.HostId;
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

    private static IEnumerable<PlayerControl> EnumerateLivingPlayers()
    {
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (player &&
                player.Data is not null &&
                !player.Data.IsDead &&
                !player.Data.Disconnected &&
                player.Data.Role is not null)
            {
                yield return player;
            }
        }
    }

    private sealed class AbilityState
    {
        public float NextAbilityAt { get; set; }
        public int? ActiveVentId { get; set; }
        public int? PendingVentId { get; set; }
        public string? PendingVentRole { get; set; }
        public float VentEntryRequestedAt { get; set; }
        public float ExitVentAt { get; set; }
        public float MinimumVentHoldUntil { get; set; }
        public byte? VentAmbushTargetId { get; set; }
        public float NextRouteLogAt { get; set; }
        public bool DecisionInFlight { get; set; }
        public bool DecisionCompleted { get; set; }
        public BotAbilityDecision? PendingDecision { get; set; }
        public RoleTypes? RequestedRole { get; set; }
        public string? RequestedTorRole { get; set; }
        public TorAbilitySequencePlan? PendingSequencePlan { get; set; }
        public string? SequenceCheckpointKey { get; set; }
        public bool SequenceCheckpointConsumed { get; set; }
        public int AbilityRoleCursor { get; set; }
        public byte? PendingShapeshiftTargetId { get; set; }
        public Vector2? PendingShapeshiftStagePosition { get; set; }
        public int ShapeshiftStageAttempts { get; set; }
        public string? TorStagingKey { get; set; }
        public Vector2? TorStagingPosition { get; set; }
        public string? TorStagingLabel { get; set; }
        public float TorStagingArrivalDistance { get; set; }

        public void ClearTorStagingTarget()
        {
            TorStagingKey = null;
            TorStagingPosition = null;
            TorStagingLabel = null;
            TorStagingArrivalDistance = 0f;
        }
    }
}
