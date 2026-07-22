using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BepInEx.Logging;
using InnerNet;
using UnityEngine;

namespace AmongUsDeepSeekBots;

internal sealed class BotActionDirector
{
    private static readonly float[] AvoidanceAngles = [28f, 52f, 78f, -28f, -52f, -78f];
    private static readonly string[] PlausibleFakeTaskNodes =
    [
        "WEAP_DOWNLOAD",
        "NAV_STEER",
        "NAV_DOWNLOAD",
        "O2_FILTER",
        "UPPER_FUEL",
        "LOWER_FUEL",
        "ELEC_WIRES",
        "ADMIN_CARD",
        "COMMS_UPLOAD",
        "MED_SAMPLE"
    ];

    private const float NodeArrivalDistance = 0.25f;
    private const float SoftDeviationDistance = 0.85f;
    private const float HardDeviationDistance = 2.8f;
    private const float StuckCheckSeconds = 0.95f;
    private const float StuckProgressDistance = 0.08f;
    private const float LocalAvoidanceLookAhead = 0.78f;
    private const float EmergencyAvoidanceLookAhead = 0.54f;
    private const float LocalAvoidanceClearance = 0.22f;
    private const float PlayerSeparationDistance = 0.85f;
    private const float AvoidanceCommitSeconds = 0.85f;
    private const float EmergencyAvoidanceCommitSeconds = 1.35f;
    private const float AvoidanceSideChangeCooldown = 1.8f;
    private const float StuckEscapeCommitSeconds = 1.25f;
    private const float StuckEscapeProbeDistance = 0.90f;
    private const int RouteLookAheadNodes = 4;
    private const float EmergencyInterruptCooldown = 0.75f;
    private const float DecisionInterval = 4.5f;
    private const float UnreachableTargetCooldownSeconds = 45f;
    private const float VisitedTargetCooldownSeconds = 28f;
    private const float TaskDiagnosticIntervalSeconds = 8f;
    // Every normal task now takes exactly five seconds longer than the previous
    // 7-11 second range. Personality affects the break after a task, not this
    // guaranteed interaction time.
    private const float TaskDwellSecondsMin = 12f;
    private const float TaskDwellSecondsMax = 16f;
    private const float EmergencyDwellSecondsMin = 3.5f;
    private const float EmergencyDwellSecondsMax = 7f;
    private const float StrategicSabotageRetrySeconds = 7f;
    private const float RecentSightSeconds = 14f;
    private const float MurderPursuitSeconds = 18f;
    private const float MurderPursuitRefreshSeconds = 0.6f;
    private const float MurderPursuitRefreshDistance = 0.75f;
    private const float PostMurderDeceptionSeconds = 20f;
    private const float PostMurderAnimationGraceSeconds = 0.9f;
    private const float PostMurderMinimumBodyClearance = 4.5f;
    private const float PostMurderMandatoryEscapeSeconds = 8f;
    private const float ThreatApproachDistance = 3.2f;
    private const float ThreatEvadeSeconds = 6f;
    private const float ActionWindowStableSeconds = 1.5f;

    private readonly ManualLogSource _log;
    private readonly DeepSeekDecisionClient _deepSeek;
    private readonly BotMatchMemory _memory;
    private readonly Dictionary<byte, BotRuntimeState> _states = [];
    private bool _playClockStarted;
    private float _playClockStartedAt;
    private bool _meetingTransitionActive;
    private float _transitionReadySince;

    public BotActionDirector(ManualLogSource log, DeepSeekDecisionClient deepSeek, BotMatchMemory memory)
    {
        _log = log;
        _deepSeek = deepSeek;
        _memory = memory;
    }

    public void TickDecision(PluginConfig config)
    {
        if (!IsHostAuthority())
        {
            ResetActivePlayGate();
            return;
        }

        // moveable can briefly become true while the role card is still on
        // screen. HudManager.IsIntroDisplayed is the authoritative UI signal;
        // resetting here also prevents a previous match's play clock from
        // leaking into the next role reveal.
        if (IsIntroPresentationActive())
        {
            ResetActivePlayGate();
            return;
        }

        if (MeetingHud.Instance || ExileController.Instance)
        {
            FreezeKillCooldownSamples();
            _meetingTransitionActive = _playClockStarted;
            _transitionReadySince = 0f;
            return;
        }

        if (!_playClockStarted)
        {
            if (!IsTransitionReadyStable(out _))
            {
                return;
            }

            _playClockStarted = true;
            _playClockStartedAt = Time.time;
            ResetRoundRoutes("match-start");
            ResetImpostorKillCooldownsFromRoomRules("match-start");
            LogRoomRuleSnapshot();
            _transitionReadySince = 0f;
            _log.LogInfo(
                $"DeepBot active-play clock started after intro; HUD hidden-state cleared and every living player " +
                $"remained movable for {ActionWindowStableSeconds:0.0}s, " +
                "room kill cooldown and autonomous sabotage clocks begin now.");
        }

        if (_meetingTransitionActive)
        {
            if (!IsTransitionReadyStable(out _))
            {
                return;
            }

            _meetingTransitionActive = false;
            ResetRoundRoutes("post-meeting");
            ResetImpostorKillCooldownsFromRoomRules("post-meeting");
            foreach (var impostor in EnumerateDeepBots().Where(IsImpostor))
            {
                var impostorState = GetState(impostor);
                impostorState.NextSabotageAt = Mathf.Min(impostorState.NextSabotageAt, Time.time);
            }

            _transitionReadySince = 0f;
            _log.LogInfo(
                $"DeepBot meeting transition finished; HUD and movement remained ready for " +
                $"{ActionWindowStableSeconds:0.0}s and sabotage decisions are enabled again.");
        }

        foreach (var bot in EnumerateDeepBots())
        {
            if (bot.Data is null || bot.Data.Disconnected)
            {
                Stop(bot.MyPhysics);
                continue;
            }

            var state = GetState(bot);
            TickKillCooldown(bot, state);
            var isDeadCrewmate = IsDeadCrewmate(bot, state);
            if (bot.Data.IsDead && !isDeadCrewmate)
            {
                Stop(bot.MyPhysics);
                continue;
            }

            if (isDeadCrewmate)
            {
                EnterGhostTaskMode(bot, state);
            }
            else
            {
                state.GhostTaskModeLogged = false;
            }

            if (IsImpostor(bot) && state.NextSabotageAt <= 0f)
            {
                state.NextSabotageAt = _playClockStartedAt + UnityEngine.Random.Range(40f, 65f);
            }

            if (Time.time < state.NextDecisionAt)
            {
                continue;
            }

            state.NextDecisionAt = Time.time + DecisionInterval + UnityEngine.Random.Range(0f, 2f);
            if (!isDeadCrewmate &&
                TryAssignEmergencyRoute(bot, state, "decision"))
            {
                continue;
            }

            if (state.HasActiveRoute)
            {
                continue;
            }

            if (UsesFakeTaskCover(bot) && TryAssignImpostorOpeningCover(bot, state))
            {
                continue;
            }

            // Keep one high-level LLM plan warming while a real task or cover
            // action runs. Crew can then take a short personality-driven roam,
            // observation, follow or pause between unfinished tasks instead of
            // deterministically chaining every task. Hard rules still force an
            // eventual return to the real task list.
            if (state.PendingDecision is null &&
                !state.DecisionInFlight &&
                Time.time >= state.NextLlmIntentAt)
            {
                RequestLlmIntent(bot, state);
                state.NextLlmIntentAt = Time.time + UnityEngine.Random.Range(11f, 19f);
            }

            // Emergency routing and an already-running physical action always
            // outrank an asynchronous model reply. This also lets the opening
            // fake task finish instead of being replaced as soon as HTTP returns.
            if (state.PendingDecision is not null)
            {
                ApplyPendingDecision(bot, state);
                continue;
            }

            if (!isDeadCrewmate && TryHandlePostTaskPersonality(bot, state))
            {
                continue;
            }

            if (!isDeadCrewmate && TryApplyPostMeetingSocialIntent(bot, state))
            {
                continue;
            }

            if (IsImpostor(bot) &&
                bot.killTimer <= 0f &&
                Time.time >= state.NextMurderPlanAt &&
                TryAssignAutonomousMurderTarget(bot, state))
            {
                continue;
            }

            if (IsImpostor(bot) &&
                Time.time >= state.NextSabotageAt &&
                TrySelectStrategicSabotage(bot, state, out var sabotagePlan) &&
                TryTriggerSabotage(bot, state, sabotagePlan.Intent, sabotagePlan.Reason))
            {
                if (sabotagePlan.TargetPlayerId.HasValue)
                {
                    var target = FindPlayerControl(sabotagePlan.TargetPlayerId.Value);
                    if (target is not null)
                    {
                        target = PreferVisibleBotVictim(
                            bot,
                            target,
                            state,
                            "post-sabotage target arbitration");
                    }

                    if (target is not null &&
                        TryAssignMurderPursuitRoute(
                            bot,
                            state,
                            target,
                            $"post-sabotage-{sabotagePlan.Goal}:{sabotagePlan.Reason}"))
                    {
                        continue;
                    }
                }

                var coverNode = PickPostSabotageCoverNode(sabotagePlan.System);
                AssignRoute(
                    bot,
                    state,
                    coverNode,
                    $"post-sabotage-{sabotagePlan.Goal}:{sabotagePlan.Reason}",
                    5f,
                    10f,
                    BotActionKind.Llm,
                    null,
                    null);
                continue;
            }

            if (IsImpostor(bot) && Time.time >= state.NextSabotageAt)
            {
                state.NextSabotageAt = Time.time + StrategicSabotageRetrySeconds;
            }

            if (IsTaskCompletingRole(bot) && TryFindAssignedTaskTarget(bot, state, out var taskTarget))
            {
                AssignRoute(
                    bot,
                    state,
                    taskTarget.Position,
                    $"TASK_{taskTarget.Task.Id}",
                    $"task:{taskTarget.Task.TaskType}:{taskTarget.Source}",
                    TaskDwellSecondsMin,
                    TaskDwellSecondsMax,
                    BotActionKind.Task,
                    taskTarget.Task.Id,
                    null,
                    taskTarget.UseDistance);
                state.TaskSelectionEpoch++;
                continue;
            }

            if (isDeadCrewmate)
            {
                Stop(bot.MyPhysics);
                continue;
            }

            RequestLlmIntent(bot, state);
            if (IsImpostor(bot) || UsesFakeTaskCover(bot))
            {
                // The model is advisory and may be slow or unavailable. Give an
                // impostor a believable local action immediately so it never
                // waits motionless for a network response.
                TryAssignImpostorAmbientBehavior(bot, state);
            }
        }
    }

    public void UpdateMovement(PluginConfig config, float deltaTime)
    {
        if (!IsHostAuthority())
        {
            return;
        }

        if (!IsSharedActionWindowOpen())
        {
            foreach (var pausedBot in EnumerateDeepBots())
            {
                Stop(pausedBot.MyPhysics);
            }
            return;
        }

        foreach (var bot in EnumerateDeepBots())
        {
            if (bot.Data is null ||
                bot.Data.Disconnected)
            {
                Stop(bot.MyPhysics);
                continue;
            }

            var state = GetState(bot);
            if (!bot.moveable && !bot.inVent)
            {
                Stop(bot.MyPhysics);
                continue;
            }
            var isDeadCrewmate = IsDeadCrewmate(bot, state);
            if (bot.Data.IsDead && !isDeadCrewmate)
            {
                Stop(bot.MyPhysics);
                continue;
            }

            if (isDeadCrewmate)
            {
                EnterGhostTaskMode(bot, state);
            }

            if (bot.inVent || bot.walkingToVent)
            {
                Stop(bot.MyPhysics);
                state.DesiredMoveDirection = Vector2.zero;
                state.DesiredMoveUntil = 0f;
                continue;
            }

            if (!isDeadCrewmate)
            {
                if (IsImpostor(bot) && TryMaintainPostMurderEscape(bot, state))
                {
                    DriveAlongRoute(bot, state, config.BotSpeedMultiplier.Value, deltaTime);
                    continue;
                }

                var bodyOwnsPriority = TryInterruptForVisibleBody(bot, state, config);
                if (!bodyOwnsPriority)
                {
                    TryInterruptForEmergency(bot, state);
                }
                if (!IsImpostor(bot))
                {
                    TryInterruptForApproachingPlayer(bot, state);
                }
                else
                {
                    TryUpdatePostMurderDeception(bot, state);
                }
            }

            if (IsImpostor(bot) &&
                state.ActionKind == BotActionKind.Stalk &&
                state.TargetPlayerId.HasValue &&
                UpdateMurderPursuit(bot, state))
            {
                continue;
            }

            if (state.ActionKind == BotActionKind.Observe &&
                state.TargetPlayerId.HasValue &&
                UpdatePostMeetingObservation(bot, state))
            {
                continue;
            }

            DriveAlongRoute(bot, state, config.BotSpeedMultiplier.Value, deltaTime);
        }
    }

    private void EnterGhostTaskMode(PlayerControl bot, BotRuntimeState state)
    {
        state.PendingDecision = null;
        if (state.GhostTaskModeLogged)
        {
            return;
        }

        var continuingTask =
            state.ActionKind == BotActionKind.Task &&
            state.CurrentTargetPosition.HasValue;
        var taskPosition = state.CurrentTargetPosition;
        var taskLabel = state.CurrentTargetNode;
        var taskId = state.ActiveTaskId;
        var taskArrivalDistance = state.ArrivalDistance;
        var taskDwellSeconds = state.DwellSeconds;

        Stop(bot.MyPhysics);
        state.ClearRoute();
        state.ClearEmergency();
        state.PostTaskPauseUntil = 0f;
        state.PostTaskWanderPending = false;
        state.NextDecisionAt = Mathf.Min(state.NextDecisionAt, Time.time + 0.2f);
        if (continuingTask && taskPosition.HasValue)
        {
            var dwell = taskDwellSeconds > 0f
                ? taskDwellSeconds
                : UnityEngine.Random.Range(TaskDwellSecondsMin, TaskDwellSecondsMax);
            AssignRoute(
                bot,
                state,
                taskPosition.Value,
                taskLabel ?? $"TASK_{taskId?.ToString() ?? "unknown"}",
                "ghost-transition:continue-current-task",
                dwell,
                dwell,
                BotActionKind.Task,
                taskId,
                null,
                taskArrivalDistance);
        }

        state.GhostTaskModeLogged = true;
        _memory.RecordAction(
            bot,
            "ghost_task_mode",
            "died as crewmate; continuing only assigned normal tasks as a ghost");
        _log.LogInfo(
            $"DeepBot ghost task mode enabled: bot={bot.Data?.PlayerName}, " +
            $"incompleteTasks={CountIncompleteNormalTasks(bot)}, phaseThroughWalls=true, " +
            "normalTasks=true, emergencies=false, reports=false, abilities=false.");
    }

    internal void ApplyPhysicsMovement(PlayerPhysics physics)
    {
        if (!physics || !physics.myPlayer || !_states.TryGetValue(physics.myPlayer.PlayerId, out var state))
        {
            return;
        }

        if (physics.myPlayer.inVent || physics.myPlayer.walkingToVent)
        {
            return;
        }

        if (state.DesiredMoveUntil < Time.time || state.DesiredMoveDirection.sqrMagnitude <= 0.001f)
        {
            physics.SetNormalizedVelocity(Vector2.zero);
            if (physics.body)
            {
                physics.body.velocity = Vector2.zero;
            }
            return;
        }

        var direction = state.DesiredMoveDirection.normalized;
        var speedMultiplier = Mathf.Clamp(state.DesiredMoveSpeedMultiplier, 0.45f, 1f);
        physics.HandleAnimation(physics.myPlayer.Data is not null && physics.myPlayer.Data.IsDead);
        physics.SetNormalizedVelocity(direction * speedMultiplier);
        if (physics.body)
        {
            physics.body.velocity = direction * Mathf.Max(0.8f, physics.TrueSpeed) * speedMultiplier;
        }
    }

    internal bool TryRouteToRoleAbility(
        PlayerControl bot,
        Vector2 targetPosition,
        string targetLabel,
        float arrivalDistance)
    {
        if (!IsHostAuthority() || !bot || bot.Data is null || bot.Data.IsDead)
        {
            return false;
        }

        var state = GetState(bot);
        if (state.ActionKind is BotActionKind.Emergency or BotActionKind.Report or BotActionKind.Task)
        {
            return false;
        }

        if (state.ActionKind == BotActionKind.Ability &&
            state.CurrentTargetPosition.HasValue &&
            Vector2.Distance(state.CurrentTargetPosition.Value, targetPosition) <= 0.1f &&
            state.HasActiveRoute)
        {
            return true;
        }

        AssignRoute(
            bot,
            state,
            targetPosition,
            targetLabel,
            "role-ability-route",
            1.5f,
            2.5f,
            BotActionKind.Ability,
            null,
            null,
            arrivalDistance);
        return state.ActionKind == BotActionKind.Ability && state.HasActiveRoute;
    }

    internal void CompleteRoleAbilityRoute(PlayerControl bot, string reason)
    {
        if (!bot)
        {
            return;
        }

        var state = GetState(bot);
        if (state.ActionKind != BotActionKind.Ability)
        {
            return;
        }

        Stop(bot.MyPhysics);
        state.ClearRoute();
        state.NextDecisionAt = Mathf.Min(state.NextDecisionAt, Time.time + 0.5f);
        _memory.RecordAction(bot, "ability_route", $"completed ability route; reason={reason}");
    }

    private bool TryInterruptForVisibleBody(PlayerControl bot, BotRuntimeState state, PluginConfig config)
    {
        if (!config.SocialInteraction.Value ||
            !config.AutoReportBodies.Value ||
            IsImpostor(bot) ||
            Time.time < state.NextBodyCheckAt)
        {
            return state.ActionKind == BotActionKind.Report;
        }

        state.NextBodyCheckAt = Time.time + 0.45f;
        var vision = GetVisionDistance(bot);
        var bodies = UnityEngine.Object.FindObjectsOfType<DeadBody>();
        var visibleBodies = new List<(DeadBody Body, float Distance)>();
        for (var i = 0; i < bodies.Length; i++)
        {
            var body = bodies[i];
            if (!DeadBodyPerception.IsVisibleAndReportable(body, out var visibility))
            {
                LogBodyPerceptionRejection(bot, state, body, vision, visibility, false, config);
                continue;
            }

            if (!DeadBodyPerception.CanObserve(
                    bot,
                    body,
                    vision,
                    out var distance,
                    out var blockedByWall))
            {
                LogBodyPerceptionRejection(
                    bot,
                    state,
                    body,
                    vision,
                    visibility,
                    blockedByWall,
                    config);
                continue;
            }

            visibleBodies.Add((body, distance));
        }

        var nearest = visibleBodies.OrderBy(item => item.Distance).FirstOrDefault();
        if (nearest.Body && TorRoleAdapter.ShouldReserveBodyForAbility(bot))
        {
            var reportDistanceForAbility = DeadBodyPerception.GetReportDistance(bot);
            if (nearest.Distance > reportDistanceForAbility - 0.15f)
            {
                TryRouteToRoleAbility(
                    bot,
                    nearest.Body.TruePosition,
                    $"ABILITY_VULTURE_BODY_{nearest.Body.ParentId}",
                    Mathf.Clamp(reportDistanceForAbility - 0.2f, 0.75f, 1.35f));
            }
            _memory.RecordAction(
                bot,
                "body_ability_priority",
                $"reserved visible body playerId={nearest.Body.ParentId} for Vulture eat before report");
            return true;
        }
        var reportDistance = DeadBodyPerception.GetReportDistance(bot);
        var withinImmediateReportRange = nearest.Body && nearest.Distance <= reportDistance;
        if (withinImmediateReportRange)
        {
            var interruptedAction = state.ActionKind;
            if (TryReportBody(bot, nearest.Body.ParentId))
            {
                Stop(bot.MyPhysics);
                state.ClearRoute();
                state.ClearEmergency();
                state.NextBodyCheckAt = Time.time + 1.5f;
                _memory.RecordAction(
                    bot,
                    "body_report_priority",
                    $"reported visible body during {interruptedAction}; distance={nearest.Distance:0.0}");
                return true;
            }
        }

        var activeEmergency = FindActiveSabotageTask();
        var criticalEmergency = activeEmergency is not null && IsCriticalEmergency(activeEmergency.TaskType);
        // A failed/unsupported immediate report attempt must not make a bot
        // abandon a lethal countdown. At this point no report was sent, so the
        // policy receives false for an immediately actionable body.
        if (!BotBehaviorPolicy.ShouldPrioritizeVisibleBody(false, criticalEmergency))
        {
            // If the body is not already reportable, do not walk away from a
            // reactor/O2 panel. The corpse is not an emergency responder and is
            // never counted as occupying the panel.
            if (state.ActionKind == BotActionKind.Report)
            {
                Stop(bot.MyPhysics);
                state.ClearRoute();
            }
            return false;
        }

        if (state.ActionKind == BotActionKind.Report)
        {
            return true;
        }

        if (!nearest.Body)
        {
            return false;
        }

        var previousAction = state.ActionKind;
        if (previousAction == BotActionKind.Emergency)
        {
            state.ClearEmergency();
        }
        AssignRoute(
            bot,
            state,
            nearest.Body.TruePosition,
            $"BODY_{nearest.Body.ParentId}",
            $"visible-body:{nearest.Body.ParentId}:distance={nearest.Distance:0.00}",
            0.55f,
            1.35f,
            BotActionKind.Report,
            null,
            nearest.Body.ParentId,
            Mathf.Clamp(reportDistance - 0.25f, 0.75f, 1.5f));
        state.PostTaskPauseUntil = 0f;
        state.PostTaskWanderPending = false;
        _memory.RecordAction(
            bot,
            "body_seen",
            $"interrupted {previousAction} to approach visible body playerId={nearest.Body.ParentId}, distance={nearest.Distance:0.0}");
        _log.LogInfo(
            $"DeepBot visible body interrupted normal behavior: bot={bot.Data?.PlayerName}, " +
            $"victim={nearest.Body.ParentId}, distance={nearest.Distance:0.00}, reportDistance={reportDistance:0.00}.");
        return true;
    }

    private void LogBodyPerceptionRejection(
        PlayerControl bot,
        BotRuntimeState state,
        DeadBody body,
        float vision,
        string visibility,
        bool blockedByWall,
        PluginConfig config)
    {
        if (!config.VerboseDiagnostics.Value ||
            !body ||
            Time.time < state.NextBodyPerceptionDiagnosticAt)
        {
            return;
        }

        var distance = Vector2.Distance(bot.GetTruePosition(), body.TruePosition);
        if (distance > Mathf.Min(Mathf.Max(3f, vision), 6f))
        {
            return;
        }

        state.NextBodyPerceptionDiagnosticAt = Time.time + 2.5f;
        _log.LogInfo(
            $"DeepBot body perception rejected: bot={bot.Data?.PlayerName}({bot.PlayerId}), " +
            $"victim={body.ParentId}, distance={distance:0.00}, vision={vision:0.00}, " +
            $"visibility={visibility}, wallBlocked={blockedByWall}, reported={body.Reported}.");
    }

    private void TryInterruptForApproachingPlayer(PlayerControl bot, BotRuntimeState state)
    {
        if (state.ActionKind is BotActionKind.Emergency or BotActionKind.Report or BotActionKind.Evade ||
            Time.time < state.NextThreatScanAt ||
            Time.time < state.ThreatEvadeUntil)
        {
            return;
        }

        state.NextThreatScanAt = Time.time + 0.45f;
        var botPosition = bot.GetTruePosition();
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (!player ||
                player.PlayerId == bot.PlayerId ||
                player.Data is null ||
                player.Data.IsDead ||
                player.Data.Disconnected ||
                !CanObservePlayer(bot, player))
            {
                continue;
            }

            var distance = Vector2.Distance(botPosition, player.GetTruePosition());
            if (!state.ThreatTracks.TryGetValue(player.PlayerId, out var track))
            {
                state.ThreatTracks[player.PlayerId] = new ThreatTrack(distance, Time.time, 0);
                continue;
            }

            var sampleAge = Time.time - track.SampleAt;
            var closing = sampleAge is >= 0.25f and <= 1.25f &&
                          track.Distance - distance >= 0.18f;
            var streak = closing
                ? track.ApproachStreak + 1
                : Math.Max(0, track.ApproachStreak - 1);
            state.ThreatTracks[player.PlayerId] = new ThreatTrack(distance, Time.time, streak);
            if (!BotBehaviorPolicy.IsPersistentApproach(
                    track.Distance,
                    distance,
                    sampleAge,
                    streak,
                    ThreatApproachDistance))
            {
                continue;
            }

            var evadeNode = PickEvadeNode(botPosition, player.GetTruePosition());
            if (string.IsNullOrWhiteSpace(evadeNode))
            {
                return;
            }

            var interruptedAction = state.ActionKind;
            AssignRoute(
                bot,
                state,
                evadeNode,
                $"crew-evade:approaching={player.Data.PlayerName}({player.PlayerId}); interrupted={interruptedAction}",
                0.8f,
                2.2f,
                BotActionKind.Evade,
                null,
                player.PlayerId);
            state.ThreatEvadeUntil = Time.time + ThreatEvadeSeconds;
            state.PostTaskPauseUntil = 0f;
            state.PostTaskWanderPending = false;
            _memory.RecordAction(
                bot,
                "threat_evade",
                $"abandoned {interruptedAction}; {player.Data.PlayerName} repeatedly approached to {distance:0.0}m");
            _log.LogInfo(
                $"DeepBot crew evading suspicious approach: bot={bot.Data?.PlayerName}, " +
                $"approacher={player.Data.PlayerName}({player.PlayerId}), distance={distance:0.00}, " +
                $"streak={streak}, interrupted={interruptedAction}, target={evadeNode}.");
            return;
        }
    }

    private static string? PickEvadeNode(Vector2 botPosition, Vector2 threatPosition)
    {
        return SkeldPathGraph.Instance.Nodes
            .Where(node =>
                SkeldPathGraph.Instance.IsNodeAllowed(node.Id) &&
                node.Kind is NodeKind.Corner or NodeKind.Hall or NodeKind.Door or NodeKind.Landmark)
            .Select(node => new
            {
                Node = node,
                FromBot = Vector2.Distance(botPosition, node.Position),
                FromThreat = Vector2.Distance(threatPosition, node.Position)
            })
            .Where(item => item.FromBot is >= 3f and <= 11f && item.FromThreat >= 5.5f)
            .OrderByDescending(item => item.FromThreat - item.FromBot * 0.35f)
            .Select(item => item.Node.Id)
            .FirstOrDefault();
    }

    private void TryInterruptForEmergency(PlayerControl bot, BotRuntimeState state)
    {
        if (Time.time < state.NextEmergencyInterruptAt ||
            state.PostMurderEscapePending ||
            (state.ActionKind == BotActionKind.Escape &&
             Time.time < state.PostMurderMandatoryEscapeUntil))
        {
            return;
        }

        var emergencyTask = FindActiveSabotageTask();
        if (emergencyTask is null)
        {
            return;
        }

        if (state.ActionKind == BotActionKind.Emergency &&
            state.EmergencyTaskType == emergencyTask.TaskType)
        {
            return;
        }

        state.NextEmergencyInterruptAt = Time.time + EmergencyInterruptCooldown;
        TryAssignEmergencyRoute(bot, state, "movement-interrupt");
    }

    private void RequestLlmIntent(PlayerControl bot, BotRuntimeState state)
    {
        if (state.DecisionInFlight)
        {
            return;
        }

        state.DecisionInFlight = true;
        var prompt = BuildPrompt(bot, state);
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var decision = await _deepSeek.GetActionAsync(prompt, CancellationToken.None).ConfigureAwait(false);
                state.PendingDecision = decision;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"DeepBot LLM decision failed for {bot.Data?.PlayerName ?? bot.PlayerId.ToString()}: {ex.Message}");
            }
            finally
            {
                state.DecisionInFlight = false;
            }
        });
    }

    private void ApplyPendingDecision(PlayerControl bot, BotRuntimeState state)
    {
        var decision = state.PendingDecision;
        state.PendingDecision = null;
        var target = SkeldPathGraph.Instance.FindNode(decision?.TargetNode)?.Id;
        if (target is null || IsTargetCoolingDown(state, target))
        {
            target = PickReachableFallbackNode(bot, state, decision);
        }

        if (target is null)
        {
            state.NextDecisionAt = Time.time + 0.35f;
            _log.LogWarning($"DeepBot could not find any reachable fallback: bot={bot.Data?.PlayerName}, requested={decision?.TargetNode ?? "none"}.");
            return;
        }

        if (decision is not null && IsImpostor(bot) && TryApplyImpostorDecision(bot, state, decision))
        {
            return;
        }

        if (decision is not null && !IsImpostor(bot) && TryApplyCrewPostTaskDecision(bot, state, decision))
        {
            return;
        }

        var normalizedAction = NormalizeAction(decision?.Action);
        var dwellMin = normalizedAction is "task" or "fake_task" ? 3.5f : 1.2f;
        var dwellMax = normalizedAction is "task" or "fake_task" ? 6.5f : 2.8f;
        AssignRoute(
            bot,
            state,
            target,
            $"llm:{decision?.Action ?? "fallback"}:{decision?.Reason ?? "none"}",
            dwellMin,
            dwellMax,
            MapDecisionAction(decision),
            null,
            null);
    }

    private bool TryApplyImpostorDecision(PlayerControl bot, BotRuntimeState state, BotActionDecision decision)
    {
        var action = NormalizeAction(decision.Action);
        if (action.Length == 0)
        {
            return false;
        }

        if (action == "murder" && decision.TargetPlayerId.HasValue)
        {
            var target = FindPlayerControl((byte)decision.TargetPlayerId.Value);
            if (target is null)
            {
                return false;
            }

            target = PreferVisibleBotVictim(
                bot,
                target,
                state,
                "LLM murder target arbitration");
            if (!TryAssignMurderPursuitRoute(
                    bot,
                    state,
                    target,
                    $"murder-stage:{target.Data?.PlayerName ?? target.PlayerId.ToString()}:{decision.Reason ?? "none"}"))
            {
                _log.LogInfo($"DeepBot murder target not currently known: bot={bot.Data?.PlayerName}, target={target.Data?.PlayerName}, reason={decision.Reason ?? "none"}");
                return false;
            }

            return true;
        }

        if ((action == "shadow" || action == "follow") && decision.TargetPlayerId.HasValue)
        {
            var target = FindPlayerControl((byte)decision.TargetPlayerId.Value);
            if (target is null)
            {
                return false;
            }

            if (!TryPickKnownTargetNode(bot, state, target, out var stalkNode))
            {
                return false;
            }

            AssignRoute(bot, state, stalkNode, $"shadow-standoff:{target.Data?.PlayerName ?? target.PlayerId.ToString()}", 2.5f, 5.5f, BotActionKind.Llm, null, target.PlayerId);
            return true;
        }

        if (action == "sabotage")
        {
            if (TryTriggerSabotage(bot, state, decision.Sabotage, decision.Reason ?? "deepseek intent"))
            {
                var targetNode = MapSabotageIntentToNode(decision.Sabotage) ?? PickFakeTaskNode();
                AssignRoute(bot, state, targetNode, $"post-sabotage-cover:{decision.Sabotage ?? "unspecified"}", 4f, 8f, BotActionKind.Llm, null, null);
            }
            else
            {
                state.NextDecisionAt = Time.time + 1.5f;
            }

            return true;
        }

        if (action == "task" || action == "fake_task")
        {
            var targetNode = SkeldPathGraph.Instance.FindNode(decision.TargetNode)?.Id ?? PickFakeTaskNode();
            AssignRoute(bot, state, targetNode, $"fake_task:{decision.Reason ?? "none"}", 6f, 12f, BotActionKind.Llm, null, null);
            return true;
        }

        return false;
    }

    private bool TryApplyCrewPostTaskDecision(PlayerControl bot, BotRuntimeState state, BotActionDecision decision)
    {
        var action = NormalizeAction(decision.Action);
        if (action.Length == 0)
        {
            return false;
        }

        if ((action == "shadow" || action == "follow") && decision.TargetPlayerId.HasValue)
        {
            var target = FindPlayerControl((byte)decision.TargetPlayerId.Value);
            if (target is null || !TryPickKnownTargetNode(bot, state, target, out var nodeId))
            {
                return false;
            }

            AssignRoute(bot, state, nodeId, $"crew-transient-follow:{target.Data?.PlayerName ?? target.PlayerId.ToString()}:{decision.Reason ?? "none"}", 2.2f, 5.2f, BotActionKind.Llm, null, target.PlayerId);
            return true;
        }

        if (action == "task" && TryFindAssignedTaskTarget(bot, state, out var taskTarget))
        {
            AssignRoute(
                bot,
                state,
                taskTarget.Position,
                $"TASK_{taskTarget.Task.Id}",
                $"llm-task:{taskTarget.Task.TaskType}:{decision.Reason ?? "none"}",
                TaskDwellSecondsMin,
                TaskDwellSecondsMax,
                BotActionKind.Task,
                taskTarget.Task.Id,
                null,
                taskTarget.UseDistance);
            return true;
        }

        return false;
    }

    private static string NormalizeAction(string? action)
    {
        return string.IsNullOrWhiteSpace(action) ? string.Empty : action.Trim().ToLowerInvariant();
    }

    private void AssignRoute(PlayerControl bot, BotRuntimeState state, string targetNodeId, string reason, float dwellMin, float dwellMax, BotActionKind kind, uint? taskId, byte? targetPlayerId)
    {
        SkeldPathGraph.Instance.ValidateRuntimeEdges(_log);
        if (kind is BotActionKind.Llm or BotActionKind.Idle or BotActionKind.Hide &&
            IsTargetCoolingDown(state, targetNodeId))
        {
            _log.LogInfo($"DeepBot route target cooling down: bot={bot.Data?.PlayerName}, target={targetNodeId}, reason={reason}");
            TryAssignReachableFallback(bot, state, targetNodeId, $"target-cooldown:{reason}", kind);
            return;
        }

        if (!SkeldPathGraph.Instance.IsNodeAllowed(targetNodeId))
        {
            _log.LogWarning($"DeepBot route rejected: bot={bot.Data?.PlayerName}, target={targetNodeId}, reason={reason}, targetBlockedOrMissing=true");
            MarkTargetUnreachable(state, targetNodeId);
            TryAssignReachableFallback(bot, state, targetNodeId, reason, kind);
            return;
        }

        var targetNode = SkeldPathGraph.Instance.FindNode(targetNodeId);
        if (targetNode is null)
        {
            return;
        }

        AssignRoute(bot, state, targetNode.Value.Position, targetNodeId, reason, dwellMin, dwellMax, kind, taskId, targetPlayerId, 1.05f);
    }

    private void AssignRoute(
        PlayerControl bot,
        BotRuntimeState state,
        Vector2 targetPosition,
        string targetLabel,
        string reason,
        float dwellMin,
        float dwellMax,
        BotActionKind kind,
        uint? taskId,
        byte? targetPlayerId,
        float arrivalDistance)
    {
        SkeldPathGraph.Instance.ValidateRuntimeEdges(_log);
        var ghostTask = BotBehaviorPolicy.ShouldUseDirectGhostTaskRoute(
            bot.Data is not null && bot.Data.IsDead,
            kind == BotActionKind.Task);
        IReadOnlyList<IReadOnlyList<NavNode>> routes = ghostTask
            ?
            [
                [
                    new NavNode(
                        $"GHOST_DIRECT_{taskId?.ToString() ?? targetLabel}",
                        "Ghost direct task route",
                        targetPosition,
                        NodeKind.Waypoint)
                ]
            ]
            : SkeldPathGraph.Instance.FindTopRoutes(bot.GetTruePosition(), targetPosition, 5);
        if (routes.Count == 0)
        {
            _log.LogWarning($"DeepBot no route: bot={bot.Data?.PlayerName}, target={targetLabel}, targetPosition={targetPosition}, reason={reason}");
            MarkTargetUnreachable(state, targetLabel);
            TryAssignReachableFallback(bot, state, targetLabel, reason, kind);
            return;
        }

        var selected = routes[state.RouteVariant % routes.Count];
        state.RouteVariant++;
        state.Route = selected.ToList();
        state.RouteIndex = Math.Min(1, state.Route.Count - 1);
        state.CurrentTargetNode = targetLabel;
        state.CurrentTargetPosition = targetPosition;
        state.RouteEndpoint = state.Route[^1].Position;
        state.ArrivalDistance = Mathf.Clamp(arrivalDistance, 0.55f, 1.5f);
        state.DwellUntil = 0f;
        state.DwellSeconds = UnityEngine.Random.Range(dwellMin, dwellMax);
        state.ActionKind = kind;
        state.ActiveTaskId = taskId;
        state.TargetPlayerId = targetPlayerId;
        state.LastProgressPosition = bot.GetTruePosition();
        state.LastProgressAt = Time.time;
        state.LastProgressRouteIndex = state.RouteIndex;
        state.LastProgressTargetDistance = Vector2.Distance(state.LastProgressPosition, state.Route[state.RouteIndex].Position);
        state.LastSafeNodeId = state.Route.Count > 0 ? state.Route[0].Id : null;
        _log.LogInfo(
            $"DeepBot route assigned: bot={bot.Data?.PlayerName}, target={targetLabel}, targetPosition={targetPosition}, candidateRoutes={routes.Count}, " +
            $"nodes={state.Route.Count}, from={bot.GetTruePosition()}, first={state.Route[0].Position}, last={state.RouteEndpoint}, " +
            $"dwell={state.DwellSeconds:0.0}s, action={kind}, reason={reason}");
        _memory.RecordAction(bot, "intent", $"started {kind} toward {targetLabel}; reason={reason}");
    }

    private void TryAssignReachableFallback(PlayerControl bot, BotRuntimeState state, string failedTarget, string failedReason, BotActionKind failedKind)
    {
        if (failedReason.StartsWith("unreachable-fallback:", StringComparison.Ordinal) ||
            failedKind is BotActionKind.Task or BotActionKind.Emergency or BotActionKind.Report)
        {
            state.NextDecisionAt = Mathf.Min(state.NextDecisionAt, Time.time + 0.35f);
            return;
        }

        var fallback = PickReachableFallbackNode(bot, state, null, failedTarget);
        if (fallback is null)
        {
            state.NextDecisionAt = Mathf.Min(state.NextDecisionAt, Time.time + 0.35f);
            return;
        }

        _log.LogInfo($"DeepBot unreachable target cooldown: bot={bot.Data?.PlayerName}, failed={failedTarget}, fallback={fallback}, cooldown={UnreachableTargetCooldownSeconds:0}s.");
        AssignRoute(
            bot,
            state,
            fallback,
            $"unreachable-fallback:{failedTarget}:{failedReason}",
            3.5f,
            6.5f,
            BotActionKind.Llm,
            null,
            null);
    }

    private void DriveAlongRoute(PlayerControl bot, BotRuntimeState state, float speedMultiplier, float deltaTime)
    {
        if (!bot.MyPhysics || state.Route.Count == 0)
        {
            return;
        }

        if (state.DwellUntil > 0f)
        {
            Stop(bot.MyPhysics);
            if (state.ActionKind is not BotActionKind.Emergency)
            {
                if (TryAssignEmergencyRoute(bot, state, "dwell-interrupt"))
                {
                    return;
                }
            }

            if (Time.time >= state.DwellUntil)
            {
                CompleteDwell(bot, state);
            }

            return;
        }

        var position = bot.GetTruePosition();
        if (state.CurrentTargetPosition.HasValue &&
            Vector2.Distance(position, state.CurrentTargetPosition.Value) <= state.ArrivalDistance)
        {
            Stop(bot.MyPhysics);
            state.RouteIndex = state.Route.Count;
            BeginDwell(bot, state);
            state.LastProgressPosition = position;
            state.LastProgressAt = Time.time;
            return;
        }

        if (state.RouteIndex >= state.Route.Count)
        {
            Stop(bot.MyPhysics);
            BeginDwell(bot, state);
            return;
        }

        if (BotBehaviorPolicy.ShouldUseDirectGhostTaskRoute(
                bot.Data is not null && bot.Data.IsDead,
                state.ActionKind == BotActionKind.Task))
        {
            DriveDirectGhostTaskRoute(bot, state, position, speedMultiplier);
            return;
        }

        var routePosition = FindClosestPointOnRoute(position, state.Route, out var routeSegmentIndex, out var routeDistance);
        if (routeDistance > HardDeviationDistance)
        {
            ForceBackToLastSafeNode(bot, state, $"hard-deviation:{routeDistance:0.00}");
            return;
        }

        if (routeDistance > SoftDeviationDistance)
        {
            var returnOffset = routePosition - position;
            if (returnOffset.sqrMagnitude > 0.001f)
            {
                if (Time.time - state.LastProgressAt >= StuckCheckSeconds)
                {
                    var progress = Vector2.Distance(position, state.LastProgressPosition);
                    if (progress < StuckProgressDistance)
                    {
                        ForceBackToLastSafeNode(bot, state, $"return-stuck:{routeDistance:0.00}");
                        return;
                    }

                    state.LastProgressPosition = position;
                    state.LastProgressAt = Time.time;
                }

                DriveDirection(
                    bot,
                    ComputeSteeredDirection(bot, state, returnOffset.normalized, returnOffset.magnitude),
                    speedMultiplier);
                state.RouteIndex = Math.Max(1, Math.Min(routeSegmentIndex + 1, state.Route.Count - 1));
                if (Time.time >= state.NextReturnLogAt)
                {
                    state.NextReturnLogAt = Time.time + 1f;
                    _log.LogInfo($"DeepBot returning to route: bot={bot.Data?.PlayerName}, distance={routeDistance:0.00}, next={state.Route[state.RouteIndex].Id}");
                }
                return;
            }
        }

        AdvanceRouteLookAhead(position, state);
        var target = state.Route[state.RouteIndex];
        var offset = target.Position - position;
        if (offset.magnitude <= NodeArrivalDistance)
        {
            state.LastSafeNodeId = target.Id;
            state.LastProgressPosition = position;
            state.LastProgressAt = Time.time;
            state.RouteIndex++;
            state.LastProgressRouteIndex = state.RouteIndex;
            state.LastProgressTargetDistance = state.RouteIndex < state.Route.Count
                ? Vector2.Distance(position, state.Route[state.RouteIndex].Position)
                : 0f;
            if (state.RouteIndex >= state.Route.Count)
            {
                Stop(bot.MyPhysics);
                BeginDwell(bot, state);
            }
            return;
        }

        if (state.RouteIndex > 0)
        {
            var previous = state.Route[state.RouteIndex - 1];
            if (Time.time - state.LastProgressAt >= StuckCheckSeconds)
            {
                var progress = Vector2.Distance(position, state.LastProgressPosition);
                var distanceToTarget = offset.magnitude;
                var targetImprovement = state.LastProgressRouteIndex == state.RouteIndex
                    ? state.LastProgressTargetDistance - distanceToTarget
                    : float.MaxValue;
                if (progress < StuckProgressDistance || targetImprovement < StuckProgressDistance * 0.5f)
                {
                    state.StuckSamples++;
                    if (state.StuckSamples < 2)
                    {
                        if (state.AvoidanceSide == 0)
                        {
                            state.AvoidanceSide = bot.PlayerId % 2 == 0 ? 1 : -1;
                        }

                        if (Time.time >= state.NextAvoidanceSideChangeAt)
                        {
                            state.AvoidanceSide *= -1;
                            state.NextAvoidanceSideChangeAt =
                                Time.time + AvoidanceSideChangeCooldown;
                        }

                        state.AvoidanceDirection = Vector2.zero;
                        state.AvoidanceUntil = 0f;
                        state.LastProgressAt = Time.time;
                        DriveDirection(
                            bot,
                            ComputeSteeredDirection(bot, state, offset.normalized, offset.magnitude),
                            speedMultiplier);
                        _log.LogInfo(
                            $"DeepBot local avoidance retry: bot={bot.Data?.PlayerName}, sample={state.StuckSamples}, " +
                            $"edge={previous.Id}->{target.Id}, pos={position}, displacement={progress:0.00}, targetImprovement={targetImprovement:0.00}");
                        return;
                    }

                    SkeldPathGraph.Instance.BlockRuntimeEdge(
                        previous.Id,
                        target.Id,
                        _log,
                        $"stuck displacement={progress:0.00},targetImprovement={targetImprovement:0.00}");
                    _memory.RecordAction(
                        bot,
                        "navigation_replan",
                        $"stuck on {previous.Id}->{target.Id}; displacement={progress:0.00}; targetImprovement={targetImprovement:0.00}");
                    if (TryStartStuckEscape(bot, state, offset.normalized, speedMultiplier, previous.Id, target.Id))
                    {
                        return;
                    }
                    ReplanCurrentRoute(bot, state, "stuck");
                    return;
                }

                state.StuckSamples = 0;
                state.LastProgressPosition = position;
                state.LastProgressAt = Time.time;
                state.LastProgressRouteIndex = state.RouteIndex;
                state.LastProgressTargetDistance = distanceToTarget;
            }
        }

        DriveDirection(
            bot,
            ComputeSteeredDirection(bot, state, offset.normalized, offset.magnitude),
            speedMultiplier);
    }

    private void DriveDirectGhostTaskRoute(
        PlayerControl bot,
        BotRuntimeState state,
        Vector2 position,
        float speedMultiplier)
    {
        var target = state.CurrentTargetPosition ??
                     state.Route[Mathf.Clamp(state.RouteIndex, 0, state.Route.Count - 1)].Position;
        var offset = target - position;
        if (offset.sqrMagnitude <= 0.001f)
        {
            Stop(bot.MyPhysics);
            BeginDwell(bot, state);
            return;
        }

        state.LastProgressPosition = position;
        state.LastProgressAt = Time.time;
        state.LastProgressTargetDistance = offset.magnitude;
        DriveDirection(bot, offset.normalized, speedMultiplier);
        if (Time.time >= state.NextGhostProgressDiagnosticAt)
        {
            state.NextGhostProgressDiagnosticAt = Time.time + 2.5f;
            _log.LogInfo(
                $"DeepBot ghost task progress: bot={bot.Data?.PlayerName}({bot.PlayerId}), " +
                $"task={state.ActiveTaskId?.ToString() ?? "unknown"}, position={position}, " +
                $"target={target}, distance={offset.magnitude:0.00}, directThroughWalls=true.");
        }
    }

    private void BeginDwell(PlayerControl bot, BotRuntimeState state)
    {
        state.DwellUntil = Time.time + state.DwellSeconds;
        var category = state.ActionKind is BotActionKind.Task or BotActionKind.Emergency
            ? "task_started"
            : "arrived";
        _memory.RecordAction(
            bot,
            category,
            $"reached {state.CurrentTargetNode ?? "target"} for {state.ActionKind}; dwell={state.DwellSeconds:0.0}s");
    }

    private bool TryHandlePostTaskPersonality(PlayerControl bot, BotRuntimeState state)
    {
        if (state.PostTaskPauseUntil <= 0f)
        {
            return false;
        }

        if (Time.time >= state.PostTaskPauseUntil)
        {
            state.PostTaskPauseUntil = 0f;
            state.PostTaskWanderPending = false;
            return false;
        }

        Stop(bot.MyPhysics);
        if (!state.PostTaskWanderPending)
        {
            state.PostTaskWanderPending = true;
        }

        state.PostTaskWanderPending = false;
        var personality = BotPersonalityCatalog.ForPlayer(bot.PlayerId);
        var activitySeconds = Mathf.Max(1f, state.PostTaskPauseUntil - Time.time);
        state.PostTaskPauseUntil = 0f;
        AssignPostTaskActivity(bot, state, personality, activitySeconds);
        return true;
    }

    private void AssignPostTaskActivity(
        PlayerControl bot,
        BotRuntimeState state,
        BotPersonalityProfile personality,
        float activitySeconds)
    {
        var roll = (bot.PlayerId * 3 + state.TaskSelectionEpoch * 5) % 10;
        var knownPlayer = state.LastSeenPlayers
            .Where(pair => Time.time - pair.Value.SeenAt <= 30f)
            .Select(pair => FindPlayerControl(pair.Key))
            .FirstOrDefault(player =>
                player is not null &&
                player &&
                player.Data is not null &&
                !player.Data.IsDead &&
                !player.Data.Disconnected);
        var preferFollow =
            knownPlayer is not null &&
            (personality.Name == "社交派" || roll is 2 or 7);
        if (preferFollow &&
            TryPickKnownTargetNode(bot, state, knownPlayer!, out var followNode))
        {
            var motive = personality.Name == "谨慎派"
                ? "watch-suspicious-player"
                : "follow-familiar-player";
            AssignRoute(
                bot,
                state,
                followNode,
                $"post-task-{motive}:{knownPlayer!.Data?.PlayerName}({knownPlayer.PlayerId})",
                Mathf.Clamp(activitySeconds * 0.35f, 1.2f, 4f),
                Mathf.Clamp(activitySeconds * 0.6f, 2f, 6f),
                BotActionKind.Llm,
                null,
                knownPlayer.PlayerId);
            _log.LogInfo(
                $"DeepBot post-task autonomous activity: bot={bot.Data?.PlayerName}, " +
                $"personality={personality.Name}, activity={motive}, target={knownPlayer.Data?.PlayerName}.");
            return;
        }

        var hide =
            personality.Name is "谨慎派" or "懒散派" &&
            roll <= 4;
        var targetNode = hide ? PickHideNode() : PickRoamNode();
        AssignRoute(
            bot,
            state,
            targetNode,
            $"post-task-{(hide ? "hide" : "room-patrol")}:{personality.Name}",
            Mathf.Clamp(activitySeconds * 0.35f, 1f, 4.5f),
            Mathf.Clamp(activitySeconds * 0.7f, 2f, 8f),
            hide ? BotActionKind.Hide : BotActionKind.Idle,
            null,
            null);
        _log.LogInfo(
            $"DeepBot post-task autonomous activity: bot={bot.Data?.PlayerName}, " +
            $"personality={personality.Name}, activity={(hide ? "hide" : "room-patrol")}, target={targetNode}.");
    }

    private bool TryApplyPostMeetingSocialIntent(PlayerControl bot, BotRuntimeState state)
    {
        if (!_memory.TryGetPostMeetingIntent(bot.PlayerId, out var intent) ||
            intent.MeetingSerial <= state.LastConsumedMeetingSerial)
        {
            return false;
        }

        state.LastConsumedMeetingSerial = intent.MeetingSerial;
        if (!intent.FollowPlayerId.HasValue ||
            intent.FollowIntent is not ("trust" or "suspect"))
        {
            return false;
        }

        var target = FindPlayerControl(intent.FollowPlayerId.Value);
        if (target is null ||
            target.Data is null ||
            target.Data.IsDead ||
            target.Data.Disconnected ||
            !CanObservePlayer(bot, target))
        {
            _memory.RecordAction(
                bot,
                "post_meeting_social",
                $"did not follow playerId={intent.FollowPlayerId}; target was not personally visible after respawn");
            return false;
        }

        var personality = BotPersonalityCatalog.ForPlayer(bot.PlayerId);
        var minimumConfidence = intent.FollowIntent == "suspect"
            ? Mathf.Lerp(0.65f, 0.28f, personality.VoteBoldness)
            : Mathf.Lerp(0.62f, 0.25f, personality.SocialSuggestibility);
        if (intent.Confidence < minimumConfidence)
        {
            return false;
        }

        state.SocialFollowUntil = Time.time + UnityEngine.Random.Range(9f, 18f);
        state.NextSocialFollowRefreshAt = 0f;
        AssignRoute(
            bot,
            state,
            target.GetTruePosition(),
            $"POST_MEETING_{intent.FollowIntent.ToUpperInvariant()}_{target.PlayerId}",
            $"meeting-memory:{intent.FollowIntent}:{intent.Reason}",
            1.2f,
            2.8f,
            BotActionKind.Observe,
            null,
            target.PlayerId,
            1.8f);
        _memory.RecordAction(
            bot,
            "post_meeting_social",
            $"chose to observe {target.Data.PlayerName}({target.PlayerId}) as {intent.FollowIntent}; confidence={intent.Confidence:0.00}");
        _log.LogInfo(
            $"DeepBot post-meeting social intent started: bot={bot.Data?.PlayerName}, " +
            $"target={target.Data.PlayerName}({target.PlayerId}), intent={intent.FollowIntent}, " +
            $"confidence={intent.Confidence:0.00}, visibleAtStart=true.");
        return state.ActionKind == BotActionKind.Observe;
    }

    private bool UpdatePostMeetingObservation(PlayerControl bot, BotRuntimeState state)
    {
        if (Time.time >= state.SocialFollowUntil)
        {
            state.ClearRoute();
            return false;
        }

        var target = state.TargetPlayerId.HasValue
            ? FindPlayerControl(state.TargetPlayerId.Value)
            : null;
        if (target is null ||
            target.Data is null ||
            target.Data.IsDead ||
            target.Data.Disconnected)
        {
            state.ClearRoute();
            return false;
        }

        if (!CanObservePlayer(bot, target))
        {
            // Keep walking only toward the last personally observed point.
            return false;
        }

        var distance = Vector2.Distance(bot.GetTruePosition(), target.GetTruePosition());
        if (distance <= 1.7f)
        {
            Stop(bot.MyPhysics);
            return true;
        }

        if (Time.time >= state.NextSocialFollowRefreshAt &&
            (!state.CurrentTargetPosition.HasValue ||
             BotBehaviorPolicy.ShouldRefreshMovingTarget(
                 state.CurrentTargetPosition.Value,
                 target.GetTruePosition(),
                 MurderPursuitRefreshDistance)))
        {
            state.NextSocialFollowRefreshAt = Time.time + 0.9f;
            AssignRoute(
                bot,
                state,
                target.GetTruePosition(),
                $"POST_MEETING_OBSERVE_{target.PlayerId}",
                $"visible-social-follow-refresh:{target.Data.PlayerName}",
                1.2f,
                2.8f,
                BotActionKind.Observe,
                null,
                target.PlayerId,
                1.8f);
        }

        return false;
    }

    private void SchedulePostTaskPersonality(PlayerControl bot, BotRuntimeState state)
    {
        var personality = BotPersonalityCatalog.ForPlayer(bot.PlayerId);
        var pause = UnityEngine.Random.Range(
            personality.PostTaskPauseMin,
            personality.PostTaskPauseMax);
        state.PostTaskPauseUntil = Time.time + pause;
        state.PostTaskWanderPending = true;
        state.NextDecisionAt = Time.time + 0.15f;
        _memory.RecordAction(
            bot,
            "personality_break",
            $"personality={personality.Name}, pause={pause:0.0}s, wander={state.PostTaskWanderPending}");
        _log.LogInfo(
            $"DeepBot post-task break scheduled: bot={bot.Data?.PlayerName}, " +
            $"personality={personality.Name}, pause={pause:0.0}s, wander={state.PostTaskWanderPending}.");
    }

    private void DriveDirection(PlayerControl bot, Vector2 direction, float speedMultiplier)
    {
        var state = GetState(bot);
        state.DesiredMoveDirection = direction;
        state.DesiredMoveSpeedMultiplier = speedMultiplier;
        state.DesiredMoveUntil = Time.time + Mathf.Max(0.2f, Time.fixedDeltaTime * 8f);
        bot.MyPhysics.HandleAnimation(bot.Data is not null && bot.Data.IsDead);
        bot.MyPhysics.SetNormalizedVelocity(direction * Mathf.Clamp(speedMultiplier, 0.45f, 1f));
        if (bot.MyPhysics.body)
        {
            bot.MyPhysics.body.velocity = direction * Mathf.Max(0.8f, bot.MyPhysics.TrueSpeed) * Mathf.Clamp(speedMultiplier, 0.45f, 1f);
        }
    }

    private static void AdvanceRouteLookAhead(Vector2 position, BotRuntimeState state)
    {
        var furthest = Math.Min(state.Route.Count - 1, state.RouteIndex + RouteLookAheadNodes);
        for (var index = furthest; index > state.RouteIndex; index--)
        {
            if (!RuntimeSkeldGrid.IsNavigationSegmentClear(
                    position,
                    state.Route[index].Position,
                    LocalAvoidanceClearance))
            {
                continue;
            }

            state.RouteIndex = index;
            return;
        }
    }

    private static Vector2 ComputeSteeredDirection(
        PlayerControl bot,
        BotRuntimeState state,
        Vector2 desired,
        float distanceToTarget)
    {
        if (desired.sqrMagnitude <= 0.001f)
        {
            return Vector2.zero;
        }

        // Dead crewmates are real Among Us ghosts: they may cross walls and
        // should not get trapped by living-player doorway avoidance.
        if (bot.Data is not null && bot.Data.IsDead)
        {
            state.AvoidanceDirection = Vector2.zero;
            state.AvoidanceUntil = 0f;
            return desired.normalized;
        }

        var position = bot.GetTruePosition();
        var emergency = state.ActionKind == BotActionKind.Emergency;
        var lookAheadLimit = emergency
            ? EmergencyAvoidanceLookAhead
            : LocalAvoidanceLookAhead;
        var lookAhead = Mathf.Clamp(distanceToTarget, 0.38f, lookAheadLimit);
        var separation = emergency || (bot.Data is not null && bot.Data.IsDead)
            ? Vector2.zero
            : ComputePlayerSeparation(bot);
        var blended = (desired.normalized + separation * 0.22f).normalized;

        if (Time.time < state.AvoidanceUntil &&
            state.AvoidanceDirection.sqrMagnitude > 0.001f &&
            Vector2.Dot(state.AvoidanceDirection, desired.normalized) > -0.15f &&
            RuntimeSkeldGrid.IsNavigationSegmentClear(
                position,
                position + state.AvoidanceDirection * lookAhead,
                LocalAvoidanceClearance))
        {
            return state.AvoidanceDirection;
        }

        if (RuntimeSkeldGrid.IsNavigationSegmentClear(
                position,
                position + blended * lookAhead,
                LocalAvoidanceClearance))
        {
            state.AvoidanceDirection = Vector2.zero;
            state.AvoidanceUntil = 0f;
            return blended;
        }

        if (state.AvoidanceSide == 0)
        {
            state.AvoidanceSide = bot.PlayerId % 2 == 0 ? 1 : -1;
        }

        var side = state.AvoidanceSide;
        for (var i = 0; i < AvoidanceAngles.Length; i++)
        {
            var candidate = Rotate(blended, AvoidanceAngles[i] * side);
            if (RuntimeSkeldGrid.IsNavigationSegmentClear(
                    position,
                    position + candidate * lookAhead,
                    LocalAvoidanceClearance))
            {
                state.AvoidanceDirection = candidate;
                state.AvoidanceUntil = Time.time +
                    (emergency ? EmergencyAvoidanceCommitSeconds : AvoidanceCommitSeconds);
                return candidate;
            }
        }

        var fallback = Rotate(blended, 90f * side);
        state.AvoidanceDirection = fallback;
        state.AvoidanceUntil = Time.time +
            (emergency ? EmergencyAvoidanceCommitSeconds : AvoidanceCommitSeconds);
        return fallback;
    }

    private bool TryStartStuckEscape(
        PlayerControl bot,
        BotRuntimeState state,
        Vector2 desired,
        float speedMultiplier,
        string fromId,
        string toId)
    {
        if (desired.sqrMagnitude <= 0.001f)
        {
            return false;
        }

        var position = bot.GetTruePosition();
        var preferredSide = state.AvoidanceSide == 0
            ? (bot.PlayerId % 2 == 0 ? 1 : -1)
            : state.AvoidanceSide;
        float[] escapeAngles = [90f, -90f, 135f, -135f, 180f, 45f, -45f];
        foreach (var rawAngle in escapeAngles)
        {
            var candidate = Rotate(desired, rawAngle * preferredSide);
            if (!RuntimeSkeldGrid.IsNavigationSegmentClear(
                    position,
                    position + candidate * StuckEscapeProbeDistance,
                    LocalAvoidanceClearance))
            {
                continue;
            }

            state.AvoidanceSide = rawAngle < 0f ? -preferredSide : preferredSide;
            state.AvoidanceDirection = candidate;
            state.AvoidanceUntil = Time.time + StuckEscapeCommitSeconds;
            state.NextAvoidanceSideChangeAt = Time.time + AvoidanceSideChangeCooldown;
            state.StuckSamples = 0;
            state.LastProgressPosition = position;
            state.LastProgressAt = Time.time;
            DriveDirection(bot, candidate, speedMultiplier);
            _log.LogWarning(
                $"DeepBot committed physical stuck escape: bot={bot.Data?.PlayerName}, " +
                $"edge={fromId}->{toId}, angle={rawAngle * preferredSide:0}, duration={StuckEscapeCommitSeconds:0.00}s, teleport=false.");
            _memory.RecordAction(
                bot,
                "navigation_escape",
                $"physical side-step from blocked edge {fromId}->{toId}; angle={rawAngle * preferredSide:0}; teleport=false");
            return true;
        }

        return false;
    }

    private static Vector2 ComputePlayerSeparation(PlayerControl bot)
    {
        var result = Vector2.zero;
        var position = bot.GetTruePosition();
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (!player ||
                player.PlayerId == bot.PlayerId ||
                player.Data is null ||
                player.Data.IsDead ||
                player.Data.Disconnected)
            {
                continue;
            }

            var away = position - player.GetTruePosition();
            var distance = away.magnitude;
            if (distance <= 0.01f || distance >= PlayerSeparationDistance)
            {
                continue;
            }

            var otherIsDeepBot = player.Data.PlayerName.StartsWith("DeepBot ", StringComparison.Ordinal);
            if (otherIsDeepBot && player.PlayerId > bot.PlayerId)
            {
                // Deterministic right-of-way prevents two bots in a narrow doorway
                // from symmetrically steering away from each other forever.
                continue;
            }

            result += away.normalized * (1f - distance / PlayerSeparationDistance);
        }

        return Vector2.ClampMagnitude(result, 1f);
    }

    private static Vector2 Rotate(Vector2 value, float degrees)
    {
        var radians = degrees * Mathf.Deg2Rad;
        var cosine = Mathf.Cos(radians);
        var sine = Mathf.Sin(radians);
        return new Vector2(
            value.x * cosine - value.y * sine,
            value.x * sine + value.y * cosine).normalized;
    }

    private void ForceBackToLastSafeNode(PlayerControl bot, BotRuntimeState state, string reason)
    {
        Stop(bot.MyPhysics);
        _memory.RecordAction(bot, "navigation_replan", $"deviation recovery without teleport; reason={reason}");
        _log.LogWarning(
            $"DeepBot replanning from actual position: bot={bot.Data?.PlayerName}, pos={bot.GetTruePosition()}, reason={reason}, teleport=false");
        ReplanCurrentRoute(bot, state, reason);
    }

    private void ReplanCurrentRoute(PlayerControl bot, BotRuntimeState state, string reason)
    {
        var targetLabel = state.CurrentTargetNode;
        var targetPosition = state.CurrentTargetPosition;
        var arrivalDistance = state.ArrivalDistance;
        var dwell = state.DwellSeconds;
        var kind = state.ActionKind;
        var taskId = state.ActiveTaskId;
        var targetPlayerId = state.TargetPlayerId;
        state.ClearRoute();
        if (targetLabel is null || !targetPosition.HasValue)
        {
            return;
        }

        AssignRoute(bot, state, targetPosition.Value, targetLabel, $"replan:{reason}", dwell, dwell, kind, taskId, targetPlayerId, arrivalDistance);
    }

    private static Vector2 FindClosestPointOnRoute(Vector2 position, IReadOnlyList<NavNode> route, out int segmentIndex, out float distance)
    {
        segmentIndex = 0;
        var best = route[0].Position;
        distance = Vector2.Distance(position, best);

        for (var i = 0; i < route.Count - 1; i++)
        {
            var a = route[i].Position;
            var b = route[i + 1].Position;
            var ab = b - a;
            var t = ab.sqrMagnitude <= 0.001f ? 0f : Mathf.Clamp01(Vector2.Dot(position - a, ab) / ab.sqrMagnitude);
            var candidate = a + ab * t;
            var candidateDistance = Vector2.Distance(position, candidate);
            if (candidateDistance < distance)
            {
                distance = candidateDistance;
                best = candidate;
                segmentIndex = i;
            }
        }

        return best;
    }

    private static void Stop(PlayerPhysics physics)
    {
        if (!physics)
        {
            return;
        }

        physics.HandleAnimation(physics.myPlayer && physics.myPlayer.Data is not null && physics.myPlayer.Data.IsDead);
        physics.SetNormalizedVelocity(Vector2.zero);
        if (physics.body)
        {
            physics.body.velocity = Vector2.zero;
        }
    }

    private BotActionPrompt BuildPrompt(PlayerControl bot, BotRuntimeState state)
    {
        return new BotActionPrompt(
            bot.PlayerId,
            bot.Data?.PlayerName ?? $"DeepBot {bot.PlayerId}",
            TorRoleAdapter.TryGetRole(bot, out var torRole) ? torRole.Alignment : IsImpostor(bot) ? "impostor" : "crewmate",
            BotPersonalityCatalog.ForPlayer(bot.PlayerId).ActionPrompt,
            _memory.BuildIdentity(bot),
            _memory.BuildKnownRoleInformation(bot),
            BuildObservation(bot, state),
            string.Join(
                ",",
                SkeldPathGraph.Instance.Nodes
                    .Where(n =>
                        SkeldPathGraph.Instance.IsNodeAllowed(n.Id) &&
                        !IsTargetCoolingDown(state, n.Id) &&
                        (n.Kind is NodeKind.Interaction or NodeKind.Corner or NodeKind.Emergency))
                    .Select(n => n.Id)));
    }

    private BotObservation BuildObservation(PlayerControl bot, BotRuntimeState state)
    {
        return new BotObservation(
            $"pos={bot.GetTruePosition()}, alive={bot.Data is not null && !bot.Data.IsDead}, node={SkeldPathGraph.Instance.NearestNode(bot.GetTruePosition()).Id}",
            BuildVisiblePlayers(bot, state),
            BuildVisibleBodies(bot),
            BuildTaskSummary(bot),
            FindEmergencyTarget() ?? "none",
            BuildLastSeenMemory(state),
            _memory.BuildTimeline(bot.PlayerId, 24));
    }

    private string BuildVisiblePlayers(PlayerControl observer, BotRuntimeState state)
    {
        var visible = new List<string>();
        var vision = GetVisionDistance(observer);
        var observerPosition = observer.GetTruePosition();
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (!player || player.PlayerId == observer.PlayerId || player.Data is null || player.Data.IsDead)
            {
                continue;
            }

            var targetPosition = player.GetTruePosition();
            var distance = Vector2.Distance(observerPosition, targetPosition);
            if (distance <= vision && !PhysicsHelpers.AnythingBetween(observerPosition, targetPosition, Constants.ShipAndObjectsMask, false))
            {
                visible.Add($"{player.Data.PlayerName}({player.PlayerId}) dist={distance:0.0}");
                state.LastSeenPlayers[player.PlayerId] = new PlayerLastSeen(player.Data.PlayerName, targetPosition, Time.time);
            }
        }

        return visible.Count == 0 ? "none" : string.Join("; ", visible);
    }

    private static string BuildLastSeenMemory(BotRuntimeState state)
    {
        if (state.LastSeenPlayers.Count == 0)
        {
            return "none";
        }

        var parts = new List<string>();
        foreach (var (playerId, seen) in state.LastSeenPlayers)
        {
            var age = Time.time - seen.SeenAt;
            if (age <= 30f)
            {
                parts.Add($"{seen.Name}({playerId}) lastSeen={SkeldPathGraph.Instance.NearestNode(seen.Position).Id} age={age:0.0}s");
            }
        }

        return parts.Count == 0 ? "none" : string.Join("; ", parts);
    }

    private static string BuildVisibleBodies(PlayerControl observer)
    {
        var visible = new List<string>();
        var vision = GetVisionDistance(observer);
        var bodies = UnityEngine.Object.FindObjectsOfType<DeadBody>();
        for (var i = 0; i < bodies.Length; i++)
        {
            var body = bodies[i];
            if (!DeadBodyPerception.IsVisibleAndReportable(body))
            {
                continue;
            }

            if (DeadBodyPerception.CanObserve(observer, body, vision, out var distance, out _))
            {
                visible.Add($"victim={body.ParentId} dist={distance:0.0}");
            }
        }

        return visible.Count == 0 ? "none" : string.Join("; ", visible);
    }

    private static float GetVisionDistance(PlayerControl observer)
    {
        return IsImpostor(observer)
            ? GameRuleSettings.GetImpostorVision(1.5f) * 5f
            : GameRuleSettings.GetCrewVision(1f) * 5f;
    }

    private static string BuildTaskSummary(PlayerControl bot)
    {
        if (bot.myTasks is null || bot.myTasks.Count == 0)
        {
            return "none";
        }

        var parts = new List<string>();
        for (var i = 0; i < bot.myTasks.Count; i++)
        {
            var task = bot.myTasks[i];
            if (task && !task.IsComplete)
            {
                parts.Add($"{task.Id}:{task.TaskType}:step={SafeTaskStep(task)}");
            }
        }

        return parts.Count == 0 ? "all complete" : string.Join(",", parts);
    }

    private bool TryFindAssignedTaskTarget(PlayerControl bot, BotRuntimeState state, out TaskRouteTarget target)
    {
        target = default;
        if (!bot || bot.myTasks is null)
        {
            return false;
        }

        SkeldPathGraph.Instance.ValidateRuntimeEdges(_log);
        var from = bot.GetTruePosition();
        var allowGhostTraversal = bot.Data is not null && bot.Data.IsDead;
        var candidates = new List<TaskRouteTarget>();
        var diagnostics = new List<string>();
        var incompleteTasks = 0;
        for (var i = 0; i < bot.myTasks.Count; i++)
        {
            var task = bot.myTasks[i];
            if (!task || task.IsComplete || IsSabotage(task.TaskType))
            {
                continue;
            }

            incompleteTasks++;
            if (TryGetReachableTaskInteractionPoint(
                    task,
                    from,
                    allowGhostTraversal,
                    out var position,
                    out var source,
                    out var detail))
            {
                candidates.Add(new TaskRouteTarget(task, position, GetTaskUseDistance(task), source));
            }
            else
            {
                diagnostics.Add(detail);
            }
        }

        if (candidates.Count == 0)
        {
            if (Time.time >= state.NextTaskDiagnosticAt)
            {
                state.NextTaskDiagnosticAt = Time.time + TaskDiagnosticIntervalSeconds;
                _log.LogWarning(
                    $"DeepBot no reachable task candidate: bot={bot.Data?.PlayerName}, incomplete={incompleteTasks}, " +
                    $"details={(diagnostics.Count == 0 ? "none" : string.Join("; ", diagnostics))}");
            }
            return false;
        }

        candidates.Sort((left, right) => Vector2.Distance(from, left.Position).CompareTo(Vector2.Distance(from, right.Position)));
        var poolSize = Math.Min(3, candidates.Count);
        var selectedIndex = BotBehaviorPolicy.SelectDistributedIndex(bot.PlayerId, state.TaskSelectionEpoch, poolSize);
        target = candidates[selectedIndex];
        return true;
    }

    private static bool TryGetReachableTaskInteractionPoint(
        PlayerTask task,
        Vector2 from,
        bool allowGhostTraversal,
        out Vector2 point,
        out string source,
        out string detail)
    {
        point = default;
        source = "none";
        detail = "invalid task";
        if (!task)
        {
            return false;
        }

        var candidates = new List<TaskPointCandidate>();
        var validConsoleCount = 0;
        var locationCount = 0;
        try
        {
            var positions = task.FindValidConsolesPositions();
            validConsoleCount = positions?.Count ?? 0;
            AddReachableTaskPoints(
                positions,
                from,
                "valid_console",
                allowGhostTraversal,
                candidates);
        }
        catch
        {
        }

        try
        {
            var positions = task.Locations;
            locationCount = positions?.Count ?? 0;
            AddReachableTaskPoints(
                positions,
                from,
                "task_location",
                allowGhostTraversal,
                candidates);
        }
        catch
        {
        }

        detail =
            $"id={task.Id},type={task.TaskType},step={SafeTaskStep(task)},valid={validConsoleCount},locations={locationCount},reachable={candidates.Count}";
        if (candidates.Count == 0)
        {
            return false;
        }

        var selected = candidates
            .OrderBy(candidate => Vector2.Distance(from, candidate.Position))
            .ThenBy(candidate => candidate.ProjectionDistance)
            .First();
        point = selected.Position;
        source = selected.Source;
        return true;
    }

    private static void AddReachableTaskPoints(
        Il2CppSystem.Collections.Generic.List<Vector2>? points,
        Vector2 from,
        string source,
        bool allowGhostTraversal,
        List<TaskPointCandidate> candidates)
    {
        if (points is null || points.Count == 0)
        {
            return;
        }

        for (var i = 0; i < points.Count; i++)
        {
            var candidate = points[i];
            if (!IsPlausibleSkeldTaskPoint(candidate))
            {
                continue;
            }

            if (allowGhostTraversal)
            {
                candidates.Add(new TaskPointCandidate(candidate, $"ghost_{source}", 0f));
                continue;
            }

            if (SkeldPathGraph.Instance.FindTopRoutes(from, candidate, 1).Count > 0)
            {
                candidates.Add(new TaskPointCandidate(candidate, source, 0f));
                continue;
            }

            if (TryFindReachableTaskStagingPoint(from, candidate, out var stagingPoint, out var projectionDistance))
            {
                candidates.Add(new TaskPointCandidate(
                    stagingPoint,
                    $"{source}_staging:{projectionDistance:0.00}m",
                    projectionDistance));
            }
        }
    }

    private static bool TryFindReachableTaskStagingPoint(
        Vector2 from,
        Vector2 interactionPoint,
        out Vector2 stagingPoint,
        out float projectionDistance)
    {
        stagingPoint = default;
        projectionDistance = float.MaxValue;
        var nearby = SkeldPathGraph.Instance.Nodes
            .Where(node =>
                SkeldPathGraph.Instance.IsNodeAllowed(node.Id) &&
                node.Kind is not NodeKind.Waypoint)
            .Select(node => new
            {
                Node = node,
                Distance = Vector2.Distance(node.Position, interactionPoint)
            })
            .Where(candidate => candidate.Distance <= 2.6f)
            .OrderBy(candidate => candidate.Distance)
            .Take(16);

        foreach (var candidate in nearby)
        {
            if (SkeldPathGraph.Instance.FindTopRoutes(from, candidate.Node.Position, 1).Count == 0)
            {
                continue;
            }

            stagingPoint = candidate.Node.Position;
            projectionDistance = candidate.Distance;
            return true;
        }

        return false;
    }

    private static int SafeTaskStep(PlayerTask task)
    {
        try
        {
            return task.TaskStep;
        }
        catch
        {
            return -1;
        }
    }

    private static bool IsPlausibleSkeldTaskPoint(Vector2 point)
    {
        // Console locations come from the live task, which is more authoritative
        // than the hand-authored landmark graph. Current Skeld coordinates moved
        // far enough that valid Reactor, Navigation and Communications consoles
        // can be several metres from their legacy landmarks. Runtime routing
        // below still proves physical reachability.
        return float.IsFinite(point.x) &&
               float.IsFinite(point.y) &&
               RuntimeSkeldGrid.ContainsSupportedPoint(point);
    }

    private static float GetTaskUseDistance(PlayerTask task)
    {
        try
        {
            var consoles = task.FindConsoles();
            var distance = 0.9f;
            if (consoles is not null)
            {
                for (var i = 0; i < consoles.Count; i++)
                {
                    var console = consoles[i];
                    if (console)
                    {
                        distance = Mathf.Max(distance, console.UsableDistance);
                    }
                }
            }

            return Mathf.Clamp(distance, 0.75f, 1.5f);
        }
        catch
        {
            return 0.9f;
        }
    }

    private static string? FindEmergencyTarget()
    {
        var task = FindActiveSabotageTask();
        return task is null ? null : MapSabotageToNode(task.TaskType);
    }

    private bool TryAssignEmergencyRoute(PlayerControl bot, BotRuntimeState state, string reason)
    {
        var task = FindActiveSabotageTask();
        if (task is null)
        {
            ClearEmergencyRoute(bot, state, "sabotage-no-longer-active");
            return false;
        }

        EnsurePersonalEmergencyDecision(bot, state, task);
        if (state.EmergencyTaskType != task.TaskType || state.EmergencyTaskId != task.Id)
        {
            return false;
        }

        TrySwitchOccupiedEmergencyConsole(bot, state, task);

        if (state.EmergencyResponder && state.EmergencyConsolePosition.HasValue)
        {
            if (state.ActionKind == BotActionKind.Emergency &&
                state.EmergencyTaskType == task.TaskType &&
                state.EmergencyTaskId == task.Id &&
                state.HasActiveRoute)
            {
                // Do not rebuild a route that is already moving or dwelling.
                // Rebuilding reset stuck history and made the bot alternate
                // left/right forever near a narrow emergency panel.
                return true;
            }

            var consoleId = state.EmergencyConsoleId ?? -1;
            AssignRoute(
                bot,
                state,
                state.EmergencyConsolePosition.Value,
                $"EMERGENCY_{task.TaskType}_CONSOLE_{consoleId}",
                $"emergency:{reason}:{task.TaskType}:console={consoleId}",
                0.75f,
                1.15f,
                BotActionKind.Emergency,
                null,
                null,
                state.EmergencyUseDistance + 0.12f);
            return state.ActionKind == BotActionKind.Emergency;
        }

        // Crew who are not assigned to a physical panel keep doing their
        // existing work. Sending every bot to a standby point made the whole
        // team appear frozen whenever one responder failed to reach a panel.
        return false;
    }

    private void TrySwitchOccupiedEmergencyConsole(
        PlayerControl bot,
        BotRuntimeState state,
        PlayerTask task)
    {
        if (!state.EmergencyResponder ||
            !state.EmergencyConsoleId.HasValue ||
            !state.EmergencyConsolePosition.HasValue ||
            state.EmergencyInteractionActive ||
            Time.time - state.EmergencyLastPanelSwitchAt < 1.25f ||
            task.TaskType is not (TaskTypes.ResetReactor or TaskTypes.ResetSeismic or TaskTypes.RestoreOxy))
        {
            return;
        }

        // The player already operating a panel must keep holding it. Only a
        // newcomer reroutes after personally seeing that its chosen side is
        // occupied, or after the shared sabotage state marks that side done.
        if (Vector2.Distance(bot.GetTruePosition(), state.EmergencyConsolePosition.Value) <=
            state.EmergencyUseDistance + 0.2f)
        {
            return;
        }

        var selectedConsoleId = state.EmergencyConsoleId.Value;
        var selectedPosition = state.EmergencyConsolePosition.Value;
        var visibleOccupant = PlayerControl.AllPlayerControls
            .ToArray()
            .FirstOrDefault(player =>
                player &&
                player.PlayerId != bot.PlayerId &&
                player.Data is not null &&
                !player.Data.IsDead &&
                !player.Data.Disconnected &&
                CanObservePlayer(bot, player) &&
                Vector2.Distance(player.GetTruePosition(), selectedPosition) <= 1.35f);
        var selectedComplete = IsEmergencyConsoleComplete(task.TaskType, selectedConsoleId);
        if (!visibleOccupant && !selectedComplete)
        {
            return;
        }

        var alternative = FindEmergencyConsoles(task)
            .Where(console => console.ConsoleId != selectedConsoleId)
            .Where(console => !IsEmergencyConsoleComplete(task.TaskType, console.ConsoleId))
            .Where(console => !PlayerControl.AllPlayerControls
                .ToArray()
                .Any(player =>
                    player &&
                    player.PlayerId != bot.PlayerId &&
                    player.Data is not null &&
                    !player.Data.IsDead &&
                    !player.Data.Disconnected &&
                    CanObservePlayer(bot, player) &&
                    Vector2.Distance(player.GetTruePosition(), console.Position) <= 1.35f))
            .Where(console => SkeldPathGraph.Instance.FindTopRoutes(
                    bot.GetTruePosition(),
                    console.Position,
                    1).Count > 0)
            .OrderBy(console => Vector2.Distance(bot.GetTruePosition(), console.Position))
            .ThenBy(console => console.ConsoleId)
            .FirstOrDefault();
        if (alternative == default)
        {
            return;
        }

        Stop(bot.MyPhysics);
        state.ClearRoute();
        state.EmergencyResponder = true;
        state.EmergencyConsoleId = alternative.ConsoleId;
        state.EmergencyConsolePosition = alternative.Position;
        state.EmergencyUseDistance = alternative.UseDistance;
        state.EmergencyInteractionActive = false;
        state.EmergencyLastPanelSwitchAt = Time.time;
        state.EmergencyReconsiderAt = float.MaxValue;
        _memory.RecordAction(
            bot,
            "emergency_inference",
            $"rerouted {task.TaskType} from occupied/completed console={selectedConsoleId} to console={alternative.ConsoleId}");
        var occupantName = visibleOccupant is not null && visibleOccupant.Data is not null
            ? visibleOccupant.Data.PlayerName
            : "unknown";
        _log.LogInfo(
            $"DeepBot emergency panel reroute: bot={bot.Data?.PlayerName}, task={task.TaskType}, " +
            $"from={selectedConsoleId}, to={alternative.ConsoleId}, " +
            $"reason={(selectedComplete ? "panel-complete" : $"visible-occupant={occupantName}")}.");
    }

    private static bool IsEmergencyConsoleComplete(TaskTypes taskType, int consoleId)
    {
        if (!ShipStatus.Instance) return false;
        var systemType = MapSabotageToSystem(taskType);
        if (!systemType.HasValue ||
            !ShipStatus.Instance.Systems.TryGetValue(systemType.Value, out var rawSystem))
        {
            return false;
        }

        if (taskType is TaskTypes.ResetReactor or TaskTypes.ResetSeismic)
        {
            var reactor = rawSystem.TryCast<ReactorSystemType>();
            return reactor is not null && reactor.GetConsoleComplete(consoleId);
        }

        if (taskType == TaskTypes.RestoreOxy)
        {
            var oxygen = rawSystem.TryCast<LifeSuppSystemType>();
            return oxygen is not null && oxygen.GetConsoleComplete(consoleId);
        }

        return false;
    }

    private static void ClearEmergencyRoute(PlayerControl bot, BotRuntimeState state, string reason)
    {
        _ = reason;
        if (state.ActionKind == BotActionKind.Emergency)
        {
            Stop(bot.MyPhysics);
            state.ClearRoute();
        }

        state.ClearEmergency();
    }

    private void EnsurePersonalEmergencyDecision(
        PlayerControl bot,
        BotRuntimeState state,
        PlayerTask task)
    {
        var sameEmergency =
            state.EmergencyTaskType == task.TaskType &&
            state.EmergencyTaskId == task.Id;
        if (sameEmergency && Time.time < state.EmergencyReconsiderAt)
        {
            return;
        }

        if (!sameEmergency)
        {
            if (state.ActionKind == BotActionKind.Emergency)
            {
                Stop(bot.MyPhysics);
                state.ClearRoute();
            }

            state.ClearEmergency();
            state.EmergencyTaskType = task.TaskType;
            state.EmergencyTaskId = task.Id;
            state.EmergencySystem = MapSabotageToSystem(task.TaskType);
            state.EmergencyAssignedAt = Time.time;
            state.EmergencyObservedAt = Time.time;
        }

        var consoles = FindEmergencyConsoles(task);
        if (consoles.Count == 0)
        {
            state.EmergencyResponder = false;
            state.EmergencyReconsiderAt = Time.time + 2f;
            return;
        }

        var critical = task.TaskType is
            TaskTypes.ResetReactor or
            TaskTypes.ResetSeismic or
            TaskTypes.RestoreOxy;
        var personality = BotPersonalityCatalog.ForPlayer(bot.PlayerId);
        var visibleLikelyResponders = CountVisibleLikelyEmergencyResponders(bot, consoles);
        var unresolvedSeconds = Time.time - state.EmergencyObservedAt;
        var roll = DeterministicEmergencyRoll(
            bot.PlayerId,
            task.Id,
            state.EmergencyDecisionEpoch++);
        var respond = BotBehaviorPolicy.ShouldRespondToEmergency(
            critical,
            IsImpostor(bot),
            personality.EmergencyResponsiveness,
            visibleLikelyResponders,
            unresolvedSeconds,
            roll);
        state.EmergencyResponder = false;
        state.EmergencyConsoleId = null;
        state.EmergencyConsolePosition = null;
        state.EmergencyInteractionActive = false;

        if (!respond)
        {
            state.EmergencyReconsiderAt =
                Time.time + UnityEngine.Random.Range(critical ? 3.5f : 6f, critical ? 6.5f : 11f);
            _memory.RecordAction(
                bot,
                "emergency_inference",
                $"chose not to respond yet to {task.TaskType}; visibleLikelyResponders={visibleLikelyResponders}; roll={roll:0.00}");
            _log.LogInfo(
                $"DeepBot personal emergency choice: bot={bot.Data?.PlayerName}, task={task.TaskType}, " +
                $"respond=false, impostor={IsImpostor(bot)}, visibleLikelyResponders={visibleLikelyResponders}, " +
                $"unresolved={unresolvedSeconds:0.0}s, roll={roll:0.00}, reconsiderAt={state.EmergencyReconsiderAt:0.0}.");
            return;
        }

        var routableConsoles = consoles
            .Where(console => !IsEmergencyConsoleComplete(task.TaskType, console.ConsoleId))
            .Where(console => SkeldPathGraph.Instance.FindTopRoutes(
                    bot.GetTruePosition(),
                    console.Position,
                    1).Count > 0)
            .Select(console => new
            {
                Console = console,
                Score =
                    Vector2.Distance(bot.GetTruePosition(), console.Position) +
                    CountVisiblePlayersNearEmergencyConsole(bot, console.Position) * 2.6f +
                    ((bot.PlayerId + console.ConsoleId + state.EmergencyDecisionEpoch) % 3) * 0.28f
            })
            .OrderBy(item => item.Score)
            .ThenBy(item => item.Console.ConsoleId)
            .ToArray();
        if (routableConsoles.Length == 0)
        {
            state.EmergencyReconsiderAt = Time.time + 2.5f;
            _log.LogWarning(
                $"DeepBot personal emergency choice found no reachable panel: bot={bot.Data?.PlayerName}, " +
                $"task={task.TaskType}, panels={consoles.Count}.");
            return;
        }

        var selected = routableConsoles[0].Console;
        state.EmergencyResponder = true;
        state.EmergencyConsoleId = selected.ConsoleId;
        state.EmergencyConsolePosition = selected.Position;
        state.EmergencyUseDistance = selected.UseDistance;
        state.EmergencyReconsiderAt = float.MaxValue;
        _memory.RecordAction(
            bot,
            "emergency_inference",
            $"independently chose {task.TaskType} console={selected.ConsoleId}; visibleLikelyResponders={visibleLikelyResponders}");
        _log.LogInfo(
            $"DeepBot personal emergency choice: bot={bot.Data?.PlayerName}, task={task.TaskType}, " +
            $"respond=true, impostor={IsImpostor(bot)}, visibleLikelyResponders={visibleLikelyResponders}, " +
            $"console={selected.ConsoleId}, position={selected.Position}, useDistance={selected.UseDistance:0.00}, " +
            $"unresolved={unresolvedSeconds:0.0}s, roll={roll:0.00}.");
    }

    private static int CountVisibleLikelyEmergencyResponders(
        PlayerControl observer,
        IReadOnlyList<EmergencyConsoleTarget> consoles)
    {
        var observerDistance = consoles.Min(console =>
            Vector2.Distance(observer.GetTruePosition(), console.Position));
        return PlayerControl.AllPlayerControls
            .ToArray()
            .Count(player =>
                player &&
                player.PlayerId != observer.PlayerId &&
                player.Data is not null &&
                !player.Data.IsDead &&
                !player.Data.Disconnected &&
                CanObservePlayer(observer, player) &&
                consoles.Min(console =>
                    Vector2.Distance(player.GetTruePosition(), console.Position)) <
                observerDistance - 0.6f);
    }

    private static int CountVisiblePlayersNearEmergencyConsole(
        PlayerControl observer,
        Vector2 consolePosition)
    {
        return PlayerControl.AllPlayerControls
            .ToArray()
            .Count(player =>
                player &&
                player.PlayerId != observer.PlayerId &&
                player.Data is not null &&
                !player.Data.IsDead &&
                !player.Data.Disconnected &&
                CanObservePlayer(observer, player) &&
                Vector2.Distance(player.GetTruePosition(), consolePosition) <= 1.8f);
    }

    private static float DeterministicEmergencyRoll(byte playerId, uint taskId, int epoch)
    {
        var hash = unchecked(
            (int)playerId * 73856093 ^
            (int)taskId * 19349663 ^
            epoch * 83492791);
        return (hash & 1023) / 1023f;
    }

    private static List<EmergencyConsoleTarget> FindEmergencyConsoles(PlayerTask task)
    {
        var result = new List<EmergencyConsoleTarget>();
        var seenConsoleIds = new HashSet<int>();
        try
        {
            var consoles = task.FindConsoles();
            if (consoles is null)
            {
                return result;
            }

            for (var index = 0; index < consoles.Count; index++)
            {
                var console = consoles[index];
                if (!console || !task.ValidConsole(console) || !seenConsoleIds.Add(console.ConsoleId))
                {
                    continue;
                }

                result.Add(new EmergencyConsoleTarget(
                    console.ConsoleId,
                    console.transform.position,
                    Mathf.Clamp(console.UsableDistance, 0.7f, 1.35f)));
            }
        }
        catch
        {
            // The caller logs a physical-panel shortage and deliberately does not
            // fall back to remotely completing the sabotage.
        }

        return result
            .OrderBy(console => console.ConsoleId)
            .ThenBy(console => console.Position.x)
            .ThenBy(console => console.Position.y)
            .ToList();
    }

    private static int GetRequiredEmergencyResponders(TaskTypes type)
    {
        return type is TaskTypes.ResetReactor or TaskTypes.ResetSeismic or TaskTypes.RestoreOxy ? 2 : 1;
    }

    private static bool IsCriticalEmergency(TaskTypes type)
    {
        return type is TaskTypes.ResetReactor or TaskTypes.ResetSeismic or TaskTypes.RestoreOxy;
    }

    private static SystemTypes? MapSabotageToSystem(TaskTypes type)
    {
        if (type == TaskTypes.ResetReactor) return SystemTypes.Reactor;
        if (type == TaskTypes.ResetSeismic) return SystemTypes.Laboratory;
        if (type == TaskTypes.RestoreOxy) return SystemTypes.LifeSupp;
        if (type == TaskTypes.FixComms) return SystemTypes.Comms;
        if (type == TaskTypes.FixLights) return SystemTypes.Electrical;
        return null;
    }

    private static PlayerTask? FindActiveSabotageTask()
    {
        if (PlayerControl.LocalPlayer)
        {
            var localTask = FindActiveSabotageTask(PlayerControl.LocalPlayer);
            if (localTask is not null)
            {
                return localTask;
            }
        }

        foreach (var bot in EnumerateDeepBots())
        {
            var task = FindActiveSabotageTask(bot);
            if (task is not null)
            {
                return task;
            }
        }

        return null;
    }

    private static PlayerTask? FindActiveSabotageTask(PlayerControl player)
    {
        if (!player || player.myTasks is null)
        {
            return null;
        }

        for (var i = 0; i < player.myTasks.Count; i++)
        {
            var task = player.myTasks[i];
            if (task && !task.IsComplete && IsSabotage(task.TaskType))
            {
                return task;
            }
        }

        return null;
    }

    private static string? FindEmergencyTarget(PlayerControl player)
    {
        if (!player || player.myTasks is null)
        {
            return null;
        }

        for (var i = 0; i < player.myTasks.Count; i++)
        {
            var task = player.myTasks[i];
            if (!task || task.IsComplete || !IsSabotage(task.TaskType))
            {
                continue;
            }

            return MapSabotageToNode(task.TaskType);
        }

        return null;
    }

    private void CompleteDwell(PlayerControl bot, BotRuntimeState state)
    {
        var completedAction = state.ActionKind;
        var completedTarget = state.CurrentTargetNode;
        if (state.ActionKind is BotActionKind.Task or BotActionKind.Emergency)
        {
            if (!IsAtCurrentTargetNode(bot, state))
            {
                _log.LogWarning($"DeepBot dwell completion deferred: bot={bot.Data?.PlayerName}, action={state.ActionKind}, target={state.CurrentTargetNode}, pos={bot.GetTruePosition()}");
                ReplanCurrentRoute(bot, state, "dwell-not-at-target");
                return;
            }

            var task = state.ActiveTaskId.HasValue
                ? FindIncompleteTaskById(bot, state.ActiveTaskId.Value)
                : FindIncompleteTaskAtTarget(bot, state.ActionKind);
            if (task is null && state.ActionKind == BotActionKind.Emergency)
            {
                task = FindActiveSabotageTask();
            }
            if (task is not null)
            {
                var beforeStep = SafeTaskStep(task);
                var beforeComplete = task.IsComplete;
                if (state.ActionKind == BotActionKind.Emergency)
                {
                    if (!RepairEmergencySystem(bot, state, task))
                    {
                        state.DwellUntil = Time.time + 0.25f;
                        return;
                    }
                }
                else
                {
                    bot.RpcCompleteTask(task.Id);
                }
                if (GameData.Instance)
                {
                    GameData.Instance.RecomputeTaskCounts();
                }

                var afterStep = SafeTaskStep(task);
                var afterComplete = task.IsComplete;
                _log.LogInfo(
                    $"DeepBot completed {state.ActionKind.ToString().ToLowerInvariant()} dwell: bot={bot.Data?.PlayerName}, " +
                    $"task={task.Id}, type={task.TaskType}, step={beforeStep}->{afterStep}, complete={beforeComplete}->{afterComplete}, " +
                    $"seconds={state.DwellSeconds:0.0}");
                _memory.RecordAction(bot, "task_done", $"completed taskId={task.Id}, type={task.TaskType}, dwell={state.DwellSeconds:0.0}s");
            }
        }
        else if (state.ActionKind == BotActionKind.Report && state.TargetPlayerId.HasValue)
        {
            TryReportBody(bot, state.TargetPlayerId.Value);
        }
        else if (state.ActionKind == BotActionKind.Stalk && state.TargetPlayerId.HasValue)
        {
            var target = FindPlayerControl(state.TargetPlayerId.Value);
            if (target is not null)
            {
                if (!TryExecuteMurder(bot, target, "post-stalk opportunity check") &&
                    state.MurderPursuitUntil > Time.time &&
                    TryAssignMurderPursuitRoute(
                        bot,
                        state,
                        target,
                        "target moved before stalk arrival; continuing pursuit"))
                {
                    return;
                }
            }
        }

        if (completedAction is BotActionKind.Llm or BotActionKind.Idle or BotActionKind.Hide &&
            !string.IsNullOrWhiteSpace(completedTarget))
        {
            MarkTargetVisited(state, completedTarget);
        }

        if (state.ImpostorOpeningCoverPending && completedAction == BotActionKind.Llm)
        {
            state.ImpostorOpeningCoverPending = false;
            state.ImpostorOpeningCoverCompleted = true;
            _memory.RecordAction(
                bot,
                "impostor_cover",
                $"finished opening fake task at {completedTarget}; now reconsidering follow, roam, sabotage, or another fake task");
            _log.LogInfo(
                $"DeepBot impostor opening cover completed: bot={bot.Data?.PlayerName}, target={completedTarget}; " +
                "situational strategy is now enabled.");
        }

        state.ClearRoute();
        if (completedAction == BotActionKind.Emergency)
        {
            state.ClearEmergency();
        }
        else if (completedAction == BotActionKind.Task &&
                 bot.Data is not null &&
                 !bot.Data.IsDead &&
                 !IsImpostor(bot))
        {
            SchedulePostTaskPersonality(bot, state);
        }
    }

    private bool TryReportBody(PlayerControl bot, byte victimId)
    {
        if (!GameData.Instance || MeetingHud.Instance)
        {
            return false;
        }

        // This is the final report RPC gate, not merely a planning hint.  A
        // Vulture may have an old report intent queued before TOR assigned the
        // role (or before its ability decision completed).  Never let that
        // stale intent pre-empt the role's body-eat objective.
        if (TorRoleAdapter.ShouldReserveBodyForAbility(bot))
        {
            _log.LogInfo(
                $"DeepBot report rejected by role arbitration: bot={bot.Data?.PlayerName}, victim={victimId}, reservedFor=VultureEat.");
            return false;
        }

        var victim = GameData.Instance.GetPlayerById(victimId);
        if (victim is null || !victim.IsDead)
        {
            return false;
        }

        var bodies = UnityEngine.Object.FindObjectsOfType<DeadBody>();
        for (var i = 0; i < bodies.Length; i++)
        {
            var body = bodies[i];
            if (!DeadBodyPerception.IsVisibleAndReportable(body) || body.ParentId != victimId)
            {
                continue;
            }

            var distance = Vector2.Distance(bot.GetTruePosition(), body.TruePosition);
            var reportDistance = DeadBodyPerception.GetReportDistance(bot);
            if (distance > reportDistance)
            {
                _log.LogInfo(
                    $"DeepBot report held: bot={bot.Data?.PlayerName}, victim={victim.PlayerName}, " +
                    $"distance={distance:0.00}, allowed={reportDistance:0.00}.");
                return false;
            }

            try
            {
                bot.CmdReportDeadBody(victim);
                _log.LogInfo($"DeepBot report executed through native RPC: bot={bot.Data?.PlayerName}, victim={victim.PlayerName}, distance={distance:0.00}.");
                _memory.RecordAction(bot, "report", $"reported body of {victim.PlayerName}({victimId})");
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"DeepBot report RPC failed: bot={bot.Data?.PlayerName}, victim={victim.PlayerName}, error={ex.Message}");
                return false;
            }
        }

        return false;
    }

    private static bool IsAtCurrentTargetNode(PlayerControl bot, BotRuntimeState state)
    {
        return state.CurrentTargetPosition.HasValue &&
               state.RouteEndpoint.HasValue &&
               BotBehaviorPolicy.IsDestinationReached(
                   bot.GetTruePosition(),
                   state.CurrentTargetPosition.Value,
                   state.RouteEndpoint.Value,
                   state.ArrivalDistance);
    }

    private static PlayerTask? FindIncompleteTaskById(PlayerControl bot, uint taskId)
    {
        if (!bot || bot.myTasks is null)
        {
            return null;
        }

        for (var i = 0; i < bot.myTasks.Count; i++)
        {
            var task = bot.myTasks[i];
            if (task && !task.IsComplete && task.Id == taskId)
            {
                return task;
            }
        }

        return null;
    }

    private static PlayerTask? FindIncompleteTaskAtTarget(PlayerControl bot, BotActionKind kind)
    {
        if (!bot || bot.myTasks is null)
        {
            return null;
        }

        for (var i = 0; i < bot.myTasks.Count; i++)
        {
            var task = bot.myTasks[i];
            if (!task || task.IsComplete)
            {
                continue;
            }

            if (kind == BotActionKind.Emergency && IsSabotage(task.TaskType))
            {
                return task;
            }
        }

        return null;
    }

    private static string MapSabotageToNode(TaskTypes type)
    {
        if (type is TaskTypes.ResetReactor or TaskTypes.ResetSeismic) return "REACTOR_MID";
        if (type == TaskTypes.RestoreOxy) return "O2_CENTER";
        if (type == TaskTypes.FixComms) return "COMMS_CENTER";
        if (type == TaskTypes.FixLights) return "ELEC_SWITCH";
        return "CAF_TABLE";
    }

    private static bool IsSabotage(TaskTypes type)
    {
        return type is
            TaskTypes.ResetReactor or
            TaskTypes.FixLights or
            TaskTypes.FixComms or
            TaskTypes.RestoreOxy or
            TaskTypes.ResetSeismic or
            TaskTypes.MushroomMixupSabotage;
    }

    private bool RepairEmergencySystem(PlayerControl bot, BotRuntimeState state, PlayerTask task)
    {
        if (!ShipStatus.Instance)
        {
            return false;
        }

        if (!state.EmergencyResponder || !state.EmergencyConsoleId.HasValue)
        {
            return FindActiveSabotageTask() is null;
        }

        if (!state.EmergencyConsolePosition.HasValue)
        {
            return false;
        }

        var panelDistance = Vector2.Distance(bot.GetTruePosition(), state.EmergencyConsolePosition.Value);
        if (panelDistance > state.EmergencyUseDistance + 0.15f)
        {
            if (Time.time >= state.NextEmergencyProximityLogAt)
            {
                state.NextEmergencyProximityLogAt = Time.time + 1.5f;
                _log.LogWarning(
                    $"DeepBot emergency interaction blocked outside panel range: bot={bot.Data?.PlayerName}, " +
                    $"task={task.TaskType}, console={state.EmergencyConsoleId}, distance={panelDistance:0.00}, " +
                    $"allowed={state.EmergencyUseDistance + 0.15f:0.00}; remote completion disabled.");
            }

            return false;
        }

        try
        {
            switch (task.TaskType)
            {
                case TaskTypes.ResetReactor:
                case TaskTypes.ResetSeismic:
                    return HoldReactorPanel(bot, state);

                case TaskTypes.RestoreOxy:
                    return UseOxygenPanel(bot, state);

                case TaskTypes.FixLights:
                    return RepairLights(bot, state);

                case TaskTypes.FixComms:
                    return RepairComms(bot, state);

                default:
                    _log.LogWarning(
                        $"DeepBot emergency repair has no physical interaction protocol: bot={bot.Data?.PlayerName}, " +
                        $"task={task.TaskType}; remote completion disabled.");
                    return false;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"DeepBot emergency repair failed: bot={bot.Data?.PlayerName}, task={task.TaskType}, error={ex.Message}");
            return false;
        }
    }

    private bool HoldReactorPanel(PlayerControl bot, BotRuntimeState state)
    {
        var systemType = state.EmergencySystem;
        if (!systemType.HasValue ||
            !ShipStatus.Instance.Systems.TryGetValue(systemType.Value, out var rawSystem))
        {
            return false;
        }

        var reactor = rawSystem.TryCast<ReactorSystemType>();
        if (reactor is null || !reactor.IsActive)
        {
            return true;
        }

        var consoleId = state.EmergencyConsoleId!.Value;
        if (!state.EmergencyInteractionActive)
        {
            var amount = (byte)(ReactorSystemType.AddUserOp |
                                (consoleId & ReactorSystemType.ConsoleIdMask));
            ShipStatus.Instance.UpdateSystem(systemType.Value, bot, amount);
            state.EmergencyInteractionActive = true;
            state.EmergencyLastInteractionAt = Time.time;
            _memory.RecordAction(
                bot,
                "emergency_repair",
                $"started holding {systemType.Value} console={consoleId} at {bot.GetTruePosition()}");
            _log.LogInfo(
                $"DeepBot reactor panel hold started: bot={bot.Data?.PlayerName}, system={systemType.Value}, " +
                $"console={consoleId}, users={reactor.UserCount}/{ReactorSystemType.RequiredUserCount}.");
        }

        if (reactor.IsActive)
        {
            return false;
        }

        _log.LogInfo(
            $"DeepBot reactor repaired by physical panel holds: bot={bot.Data?.PlayerName}, " +
            $"console={consoleId}, position={bot.GetTruePosition()}.");
        return true;
    }

    private bool UseOxygenPanel(PlayerControl bot, BotRuntimeState state)
    {
        if (!ShipStatus.Instance.Systems.TryGetValue(SystemTypes.LifeSupp, out var rawSystem))
        {
            return false;
        }

        var oxygen = rawSystem.TryCast<LifeSuppSystemType>();
        if (oxygen is null || !oxygen.IsActive)
        {
            return true;
        }

        var consoleId = state.EmergencyConsoleId!.Value;
        if (!state.EmergencyInteractionActive)
        {
            var amount = (byte)(LifeSuppSystemType.AddUserOp |
                                (consoleId & LifeSuppSystemType.ConsoleIdMask));
            ShipStatus.Instance.UpdateSystem(SystemTypes.LifeSupp, bot, amount);
            state.EmergencyInteractionActive = true;
            state.EmergencyLastInteractionAt = Time.time;
            _memory.RecordAction(bot, "emergency_repair", $"entered O2 code at console={consoleId}, position={bot.GetTruePosition()}");
            _log.LogInfo($"DeepBot O2 panel used: bot={bot.Data?.PlayerName}, console={consoleId}, position={bot.GetTruePosition()}.");
        }

        return !oxygen.IsActive;
    }

    private bool RepairComms(PlayerControl bot, BotRuntimeState state)
    {
        if (!ShipStatus.Instance.Systems.TryGetValue(SystemTypes.Comms, out var rawSystem))
        {
            return false;
        }

        var comms = rawSystem.TryCast<HudOverrideSystemType>();
        if (comms is null || !comms.IsActive)
        {
            return true;
        }

        if (!state.EmergencyInteractionActive ||
            Time.time - state.EmergencyLastInteractionAt >= 1.5f)
        {
            var consoleId = state.EmergencyConsoleId!.Value;
            // HudOverrideSystemType uses DamageBit to turn the outage on.
            // Repairing the one Skeld tuning panel sends a task value with that
            // bit cleared; the former hard-coded 16|0 / 16|1 values never
            // cleared IsActive on the current game build.
            var repairAmount = (byte)(consoleId & HudOverrideSystemType.TaskMask);
            ShipStatus.Instance.UpdateSystem(SystemTypes.Comms, bot, repairAmount);
            state.EmergencyInteractionActive = true;
            state.EmergencyLastInteractionAt = Time.time;
            _memory.RecordAction(bot, "emergency_repair", $"tuned comms at console={consoleId}, position={bot.GetTruePosition()}");
            _log.LogInfo(
                $"DeepBot comms panel used: bot={bot.Data?.PlayerName}, console={consoleId}, " +
                $"repairAmount={repairAmount}, damageBit={HudOverrideSystemType.DamageBit}, " +
                $"activeAfter={comms.IsActive}, position={bot.GetTruePosition()}.");
        }

        return !comms.IsActive;
    }

    private bool RepairLights(PlayerControl bot, BotRuntimeState state)
    {
        if (!ShipStatus.Instance ||
            !ShipStatus.Instance.Systems.TryGetValue(SystemTypes.Electrical, out var system))
        {
            return false;
        }

        var switches = system.TryCast<SwitchSystem>();
        if (switches is null || !switches.IsActive)
        {
            return true;
        }

        if (!state.EmergencyInteractionActive ||
            Time.time - state.EmergencyLastInteractionAt >= 0.45f)
        {
            var mismatch = switches.ActualSwitches ^ switches.ExpectedSwitches;
            var flippedIndex = -1;
            var beforeActual = switches.ActualSwitches;
            for (var index = 0; index < SwitchSystem.NumSwitches; index++)
            {
                if ((mismatch & (1 << index)) != 0)
                {
                    ShipStatus.Instance.UpdateSystem(SystemTypes.Electrical, bot, (byte)index);
                    flippedIndex = index;
                    break;
                }
            }

            state.EmergencyInteractionActive = true;
            state.EmergencyLastInteractionAt = Time.time;
            _memory.RecordAction(
                bot,
                "emergency_repair",
                $"flipped lights switch={flippedIndex} at position={bot.GetTruePosition()}");
            _log.LogInfo(
                $"DeepBot lights panel used: bot={bot.Data?.PlayerName}, switch={flippedIndex}, " +
                $"actual={beforeActual}->{switches.ActualSwitches}, expected={switches.ExpectedSwitches}, " +
                $"activeAfter={switches.IsActive}, position={bot.GetTruePosition()}.");
        }

        return !switches.IsActive;
    }

    private bool TrySelectStrategicSabotage(
        PlayerControl bot,
        BotRuntimeState state,
        out SabotagePlan plan)
    {
        plan = default;
        var crew = PlayerControl.AllPlayerControls
            .ToArray()
            .Where(player =>
                player &&
                player.Data is not null &&
                !player.Data.IsDead &&
                !player.Data.Disconnected &&
                !IsImpostor(player))
            .ToArray();
        if (crew.Length == 0)
        {
            return false;
        }

        var knownCrew = new List<KnownCrewObservation>();
        foreach (var player in crew)
        {
            if (CanObservePlayer(bot, player))
            {
                var position = player.GetTruePosition();
                state.LastSeenPlayers[player.PlayerId] =
                    new PlayerLastSeen(player.Data!.PlayerName, position, Time.time);
                knownCrew.Add(new KnownCrewObservation(player, position, 0f));
                continue;
            }

            if (state.LastSeenPlayers.TryGetValue(player.PlayerId, out var seen))
            {
                var age = Time.time - seen.SeenAt;
                if (age <= RecentSightSeconds)
                {
                    knownCrew.Add(new KnownCrewObservation(player, seen.Position, age));
                }
            }
        }

        var taskProgress = GameData.Instance && GameData.Instance.TotalTasks > 0
            ? Mathf.Clamp01(GameData.Instance.CompletedTasks / (float)GameData.Instance.TotalTasks)
            : 0f;
        var clusteredPairs = 0;
        for (var left = 0; left < knownCrew.Count; left++)
        {
            for (var right = left + 1; right < knownCrew.Count; right++)
            {
                if (Vector2.Distance(knownCrew[left].Position, knownCrew[right].Position) <= 2.8f)
                {
                    clusteredPairs++;
                }
            }
        }

        KnownCrewObservation? isolatedTarget = null;
        var bestIsolation = 0f;
        foreach (var candidate in knownCrew)
        {
            var nearestOther = knownCrew
                .Where(other => other.Player.PlayerId != candidate.Player.PlayerId)
                .Select(other => Vector2.Distance(candidate.Position, other.Position))
                .DefaultIfEmpty(5.5f)
                .Min();
            var usefulIsolation = nearestOther -
                                  candidate.Age * 0.12f -
                                  Vector2.Distance(bot.GetTruePosition(), candidate.Position) * 0.04f;
            if (usefulIsolation > bestIsolation)
            {
                bestIsolation = usefulIsolation;
                isolatedTarget = candidate;
            }
        }

        var killCooldown = Mathf.Max(0f, bot.killTimer);
        var killReadySoon = killCooldown <= 5f;
        var hasIsolatedTarget = isolatedTarget.HasValue && bestIsolation >= 2.8f;
        var epoch = state.SabotageDecisionEpoch++;
        var candidates = new List<SabotagePlanCandidate>
        {
            new(
                SystemTypes.Electrical,
                "lights",
                "isolate_for_kill",
                4.5f +
                (killReadySoon ? 4f : 0f) +
                (hasIsolatedTarget ? 5f : 0f) +
                Mathf.Min(2f, knownCrew.Count * 0.35f),
                hasIsolatedTarget ? isolatedTarget!.Value.Player.PlayerId : null),
            new(
                SystemTypes.Comms,
                "comms",
                "deny_information_and_stall_tasks",
                4.2f +
                taskProgress * 6f +
                Mathf.Min(2.5f, knownCrew.Count * 0.4f),
                null),
            new(
                SystemTypes.Reactor,
                "reactor",
                "split_group_and_run_kill_cooldown",
                4f +
                Mathf.Min(5f, clusteredPairs * 1.4f) +
                (killCooldown > 5f ? 3f : 0f) +
                taskProgress * 1.5f,
                null),
            new(
                SystemTypes.LifeSupp,
                "o2",
                "pull_crew_apart_and_create_rotation",
                4.1f +
                Mathf.Min(4f, clusteredPairs * 1.2f) +
                (knownCrew.Count >= 2 ? 1.5f : 0f) +
                taskProgress * 1.8f,
                null)
        };

        var selected = candidates
            .OrderByDescending(candidate =>
                candidate.Score +
                ((bot.PlayerId + epoch + (int)candidate.System) % 5) * 0.03f)
            .First();
        if (selected.Score < 5.5f)
        {
            _log.LogInfo(
                $"DeepBot sabotage held for a better opportunity: bot={bot.Data?.PlayerName}, " +
                $"best={selected.System}, score={selected.Score:0.0}, knownCrew={knownCrew.Count}, " +
                $"taskProgress={taskProgress:P0}, killCooldown={killCooldown:0.0}s.");
            return false;
        }

        var targetDetail = selected.TargetPlayerId.HasValue
            ? $", target={FindPlayerControl(selected.TargetPlayerId.Value)?.Data?.PlayerName ?? selected.TargetPlayerId.Value.ToString()}, isolation={bestIsolation:0.0}m"
            : string.Empty;
        var reason =
            $"goal={selected.Goal}; score={selected.Score:0.0}; taskProgress={taskProgress:P0}; " +
            $"knownCrew={knownCrew.Count}; clusteredPairs={clusteredPairs}; killCooldown={killCooldown:0.0}s{targetDetail}";
        plan = new SabotagePlan(
            selected.System,
            selected.Intent,
            selected.Goal,
            reason,
            selected.TargetPlayerId);
        _memory.RecordAction(bot, "sabotage_plan", reason);
        _log.LogInfo(
            $"DeepBot sabotage plan selected: bot={bot.Data?.PlayerName}, system={selected.System}, {reason}.");
        return true;
    }

    private bool TryTriggerSabotage(PlayerControl bot, BotRuntimeState state, string? intent, string reason)
    {
        if (!IsBotActionWindowOpen(bot))
        {
            if (Time.time >= state.NextSabotageAt && Time.time >= state.NextSabotageDiagnosticAt)
            {
                state.NextSabotageDiagnosticAt = Time.time + 1.25f;
                _log.LogInfo(
                    $"DeepBot sabotage waiting: bot={bot.Data?.PlayerName}, " +
                    $"block={ExplainSharedActionWindowBlock(bot)}, reason={reason}");
            }
            return false;
        }

        if (Time.time < state.NextSabotageAt || FindEmergencyTarget() is not null)
        {
            return false;
        }

        if (ShipStatus.Instance.Systems.TryGetValue(SystemTypes.Sabotage, out var rawSystem))
        {
            var sabotageSystem = rawSystem.TryCast<SabotageSystemType>();
            if (sabotageSystem is not null && (sabotageSystem.AnyActive || sabotageSystem.Timer > 0f))
            {
                return false;
            }
        }

        var targetSystem = MapSabotageIntentToSystem(intent);
        try
        {
            ShipStatus.Instance.RpcUpdateSystem(SystemTypes.Sabotage, (byte)targetSystem);
            state.NextSabotageAt = Time.time + UnityEngine.Random.Range(32f, 52f);
            _memory.RecordAction(bot, "sabotage", $"triggered {targetSystem}; reason={reason}");
            _log.LogInfo($"DeepBot sabotage executed through native RPC: bot={bot.Data?.PlayerName}, system={targetSystem}, reason={reason}.");
            return true;
        }
        catch (Exception ex)
        {
            state.NextSabotageAt = Time.time + 8f;
            _log.LogWarning($"DeepBot sabotage RPC failed: bot={bot.Data?.PlayerName}, system={targetSystem}, error={ex.Message}");
            return false;
        }
    }

    private static SystemTypes MapSabotageIntentToSystem(string? sabotage)
    {
        if (!string.IsNullOrWhiteSpace(sabotage))
        {
            if (sabotage.Contains("reactor", StringComparison.OrdinalIgnoreCase)) return SystemTypes.Reactor;
            if (sabotage.Contains("o2", StringComparison.OrdinalIgnoreCase) || sabotage.Contains("oxygen", StringComparison.OrdinalIgnoreCase)) return SystemTypes.LifeSupp;
            if (sabotage.Contains("light", StringComparison.OrdinalIgnoreCase) || sabotage.Contains("electrical", StringComparison.OrdinalIgnoreCase)) return SystemTypes.Electrical;
            if (sabotage.Contains("comm", StringComparison.OrdinalIgnoreCase)) return SystemTypes.Comms;
        }

        var choices = new[] { SystemTypes.Reactor, SystemTypes.LifeSupp, SystemTypes.Electrical, SystemTypes.Comms };
        return choices[UnityEngine.Random.Range(0, choices.Length)];
    }

    private static string PickPostSabotageCoverNode(SystemTypes system)
    {
        return system switch
        {
            SystemTypes.Electrical => PickHideNode(),
            SystemTypes.Reactor or SystemTypes.LifeSupp => PickHideNode(),
            _ => PickFakeTaskNode()
        };
    }

    private static bool AreAllLivingPlayersMovable()
    {
        var foundLivingPlayer = false;
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (!player ||
                player.Data is null ||
                player.Data.IsDead ||
                player.Data.Disconnected)
            {
                continue;
            }

            foundLivingPlayer = true;
            if (!player.moveable || player.inVent || player.walkingToVent)
            {
                return false;
            }
        }

        return foundLivingPlayer;
    }

    internal bool IsSharedActionWindowOpen()
    {
        return _playClockStarted &&
               !_meetingTransitionActive &&
               IsHostAuthority() &&
               !IsIntroPresentationActive() &&
               !MeetingHud.Instance &&
               !ExileController.Instance;
    }

    internal bool IsBotActionWindowOpen(PlayerControl bot)
    {
        return IsSharedActionWindowOpen() &&
               bot &&
               bot.moveable &&
               !bot.walkingToVent;
    }

    private bool IsTransitionReadyStable(out string reason)
    {
        if (!TryGetRawTransitionReadiness(out reason))
        {
            _transitionReadySince = 0f;
            return false;
        }

        if (_transitionReadySince <= 0f)
        {
            _transitionReadySince = Time.time;
            reason = "action-window-stabilizing";
            return false;
        }

        if (!BotBehaviorPolicy.HasStableActionWindow(
                true,
                _transitionReadySince,
                Time.time,
                ActionWindowStableSeconds))
        {
            reason = $"action-window-stabilizing:{Time.time - _transitionReadySince:0.00}/{ActionWindowStableSeconds:0.00}s";
            return false;
        }

        reason = "ready";
        return true;
    }

    private static bool TryGetRawTransitionReadiness(out string reason)
    {
        if (!ShipStatus.Instance || !AmongUsClient.Instance ||
            AmongUsClient.Instance.GameState != InnerNetClient.GameStates.Started)
        {
            reason = "match-not-started";
            return false;
        }

        if (IsIntroPresentationActive())
        {
            reason = "role-intro-visible";
            return false;
        }

        if (MeetingHud.Instance || ExileController.Instance)
        {
            reason = "meeting-or-exile-transition";
            return false;
        }

        if (!HudManager.Instance)
        {
            reason = "hud-not-ready";
            return false;
        }

        if (!PlayerControl.LocalPlayer || !PlayerControl.LocalPlayer.moveable)
        {
            reason = "host-not-moveable";
            return false;
        }

        if (!AreAllLivingPlayersMovable())
        {
            reason = "living-player-not-moveable";
            return false;
        }

        reason = "raw-ready";
        return true;
    }

    private static bool IsIntroPresentationActive()
    {
        return IntroCutscene.Instance ||
               (HudManager.Instance && HudManager.Instance.IsIntroDisplayed);
    }

    private void ResetActivePlayGate()
    {
        _playClockStarted = false;
        _playClockStartedAt = 0f;
        _meetingTransitionActive = false;
        _transitionReadySince = 0f;
    }

    private string ExplainSharedActionWindowBlock(PlayerControl bot)
    {
        if (!_playClockStarted) return "active-play-not-started";
        if (_meetingTransitionActive) return "transition-stability-gate";
        if (IsIntroPresentationActive()) return "role-intro-visible";
        if (MeetingHud.Instance || ExileController.Instance) return "meeting-or-exile-transition";
        if (!IsHostAuthority()) return "host-authority-or-match-state-missing";
        if (!bot || !bot.moveable) return "bot-not-moveable";
        if (bot.walkingToVent) return "bot-in-vent-transition";
        return "action-window-closed";
    }

    private static string? MapTaskTypeToNode(TaskTypes type)
    {
        var text = type.ToString();
        if (text.Contains("Reactor", StringComparison.OrdinalIgnoreCase) || text.Contains("Manifold", StringComparison.OrdinalIgnoreCase)) return "REACTOR_MID";
        if (text.Contains("O2", StringComparison.OrdinalIgnoreCase) || text.Contains("Chute", StringComparison.OrdinalIgnoreCase)) return "O2_CENTER";
        if (text.Contains("Navigation", StringComparison.OrdinalIgnoreCase) || text.Contains("Chart", StringComparison.OrdinalIgnoreCase)) return "NAV_CENTER";
        if (text.Contains("Weapon", StringComparison.OrdinalIgnoreCase) || text.Contains("Asteroid", StringComparison.OrdinalIgnoreCase)) return "WEAP_CENTER";
        if (text.Contains("Admin", StringComparison.OrdinalIgnoreCase) || text.Contains("Card", StringComparison.OrdinalIgnoreCase)) return "ADMIN_CARD";
        if (text.Contains("Electrical", StringComparison.OrdinalIgnoreCase) || text.Contains("Wiring", StringComparison.OrdinalIgnoreCase)) return "ELEC_CENTER";
        if (text.Contains("Med", StringComparison.OrdinalIgnoreCase) || text.Contains("Scan", StringComparison.OrdinalIgnoreCase)) return "MED_SCAN";
        if (text.Contains("Security", StringComparison.OrdinalIgnoreCase)) return "SEC_CENTER";
        if (text.Contains("Shield", StringComparison.OrdinalIgnoreCase)) return "SHIELD_CENTER";
        if (text.Contains("Comms", StringComparison.OrdinalIgnoreCase) || text.Contains("Upload", StringComparison.OrdinalIgnoreCase)) return "COMMS_CENTER";
        if (text.Contains("Engine", StringComparison.OrdinalIgnoreCase) || text.Contains("Fuel", StringComparison.OrdinalIgnoreCase)) return "LOWER_ENGINE_M";
        return "STOR_CENTER";
    }

    private string? PickReachableFallbackNode(PlayerControl bot, BotRuntimeState state, BotActionDecision? decision, string? excludedTarget = null)
    {
        var action = NormalizeAction(decision?.Action);
        var candidates = SkeldPathGraph.Instance.Nodes
            .Where(node =>
                SkeldPathGraph.Instance.IsNodeAllowed(node.Id) &&
                !string.Equals(node.Id, excludedTarget, StringComparison.Ordinal) &&
                !IsTargetCoolingDown(state, node.Id) &&
                (action switch
                {
                    "idle" => node.Kind is NodeKind.Landmark or NodeKind.Hall,
                    "hide" => node.Kind is NodeKind.Corner,
                    "task" or "fake_task" => node.Kind is NodeKind.Interaction,
                    _ => node.Kind is NodeKind.Corner or NodeKind.Interaction or NodeKind.Landmark
                }))
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var start = BotBehaviorPolicy.SelectDistributedIndex(bot.PlayerId, state.RouteVariant++, candidates.Length);
        var from = bot.GetTruePosition();
        for (var offset = 0; offset < candidates.Length; offset++)
        {
            var node = candidates[(start + offset) % candidates.Length];
            if (SkeldPathGraph.Instance.FindTopRoutes(from, node.Position, 1).Count > 0)
            {
                return node.Id;
            }

            MarkTargetUnreachable(state, node.Id);
        }

        return null;
    }

    private static bool IsTargetCoolingDown(BotRuntimeState state, string targetNodeId)
    {
        var unreachable = state.UnreachableTargetUntil.TryGetValue(targetNodeId, out var unreachableUntil);
        if (unreachable && unreachableUntil <= Time.time)
        {
            state.UnreachableTargetUntil.Remove(targetNodeId);
            unreachable = false;
        }

        var visited = state.VisitedTargetUntil.TryGetValue(targetNodeId, out var visitedUntil);
        if (visited && visitedUntil <= Time.time)
        {
            state.VisitedTargetUntil.Remove(targetNodeId);
            visited = false;
        }

        return unreachable || visited;
    }

    private static void MarkTargetUnreachable(BotRuntimeState state, string targetLabel)
    {
        if (!string.IsNullOrWhiteSpace(targetLabel))
        {
            state.UnreachableTargetUntil[targetLabel] = Time.time + UnreachableTargetCooldownSeconds;
        }
    }

    private static void MarkTargetVisited(BotRuntimeState state, string targetLabel)
    {
        state.VisitedTargetUntil[targetLabel] = Time.time + VisitedTargetCooldownSeconds;
    }

    private static string PickFallbackPostTaskNode(BotActionDecision? decision)
    {
        var action = NormalizeAction(decision?.Action);
        if (action == "idle")
        {
            return PickIdleNode();
        }

        if (action == "hide")
        {
            return PickHideNode();
        }

        return PickRoamNode();
    }

    private static string PickRoamNode()
    {
        var candidates = SkeldPathGraph.Instance.Nodes
            .Where(node => SkeldPathGraph.Instance.IsNodeAllowed(node.Id) && (node.Kind is NodeKind.Corner or NodeKind.Interaction or NodeKind.Landmark))
            .ToArray();
        return candidates[UnityEngine.Random.Range(0, candidates.Length)].Id;
    }

    private static string PickIdleNode()
    {
        var candidates = SkeldPathGraph.Instance.Nodes
            .Where(node => SkeldPathGraph.Instance.IsNodeAllowed(node.Id) && (node.Kind is NodeKind.Landmark or NodeKind.Hall))
            .ToArray();
        return candidates[UnityEngine.Random.Range(0, candidates.Length)].Id;
    }

    private static string PickHideNode()
    {
        var candidates = SkeldPathGraph.Instance.Nodes
            .Where(node => SkeldPathGraph.Instance.IsNodeAllowed(node.Id) && node.Kind is NodeKind.Corner)
            .ToArray();
        return candidates[UnityEngine.Random.Range(0, candidates.Length)].Id;
    }

    private static string PickFakeTaskNode()
    {
        var candidates = PlausibleFakeTaskNodes
            .Where(id => SkeldPathGraph.Instance.IsNodeAllowed(id))
            .ToArray();
        return candidates.Length > 0
            ? candidates[UnityEngine.Random.Range(0, candidates.Length)]
            : "ADMIN_CARD";
    }

    private bool TryAssignImpostorOpeningCover(PlayerControl bot, BotRuntimeState state)
    {
        if (!BotBehaviorPolicy.ShouldUseOpeningFakeTask(
                UsesFakeTaskCover(bot),
                state.ImpostorOpeningCoverCompleted))
        {
            return false;
        }

        var target = PickReachableFakeTaskNode(bot, state, bot.PlayerId);
        if (target is null)
        {
            state.NextDecisionAt = Time.time + 0.35f;
            return false;
        }

        AssignRoute(
            bot,
            state,
            target,
            "impostor-opening-cover:fake-task-before-strategy",
            8f,
            13f,
            BotActionKind.Llm,
            null,
            null);
        if (!state.HasActiveRoute)
        {
            return false;
        }

        state.ImpostorOpeningCoverPending = true;
        _memory.RecordAction(
            bot,
            "impostor_cover",
            $"opened the round by pretending to work at {target}; later actions remain situational");
        _log.LogInfo(
            $"DeepBot impostor opening cover assigned: bot={bot.Data?.PlayerName}, target={target}, " +
            "strategy=fake-task-first-then-reconsider.");
        return true;
    }

    private bool TryAssignImpostorAmbientBehavior(PlayerControl bot, BotRuntimeState state)
    {
        var epoch = state.ImpostorAmbientEpoch++;
        var mode = (bot.PlayerId + epoch) % 5;
        if (mode <= 2)
        {
            var fakeTask = PickReachableFakeTaskNode(bot, state, epoch);
            if (fakeTask is not null)
            {
                AssignRoute(
                    bot,
                    state,
                    fakeTask,
                    $"impostor-ambient:fake-task:epoch={epoch}",
                    6f,
                    12f,
                    BotActionKind.Llm,
                    null,
                    null);
                return state.HasActiveRoute;
            }
        }

        if (mode == 3)
        {
            var visibleCrew = PlayerControl.AllPlayerControls
                .ToArray()
                .Where(player =>
                    player &&
                    player.PlayerId != bot.PlayerId &&
                    player.Data is not null &&
                    !player.Data.IsDead &&
                    !player.Data.Disconnected &&
                    !IsImpostor(player) &&
                    CanObservePlayer(bot, player))
                .OrderBy(player => Vector2.Distance(bot.GetTruePosition(), player.GetTruePosition()))
                .FirstOrDefault();
            if (visibleCrew is not null &&
                TryPickKnownTargetNode(bot, state, visibleCrew, out var followNode))
            {
                AssignRoute(
                    bot,
                    state,
                    followNode,
                    $"impostor-ambient:blend-follow:{visibleCrew.Data?.PlayerName}:epoch={epoch}",
                    2.5f,
                    5.5f,
                    BotActionKind.Llm,
                    null,
                    visibleCrew.PlayerId);
                return state.HasActiveRoute;
            }
        }

        var roamTarget = mode == 4 ? PickHideNode() : PickRoamNode();
        AssignRoute(
            bot,
            state,
            roamTarget,
            $"impostor-ambient:{(mode == 4 ? "hide" : "roam")}:epoch={epoch}",
            2.5f,
            6f,
            mode == 4 ? BotActionKind.Hide : BotActionKind.Llm,
            null,
            null);
        return state.HasActiveRoute;
    }

    private static string? PickReachableFakeTaskNode(PlayerControl bot, BotRuntimeState state, int epoch)
    {
        var candidates = PlausibleFakeTaskNodes
            .Where(id =>
                SkeldPathGraph.Instance.IsNodeAllowed(id) &&
                !IsTargetCoolingDown(state, id))
            .Select(id => new
            {
                Id = id,
                Node = SkeldPathGraph.Instance.FindNode(id)
            })
            .Where(item => item.Node.HasValue)
            .Where(item => SkeldPathGraph.Instance.FindTopRoutes(
                bot.GetTruePosition(),
                item.Node!.Value.Position,
                1).Count > 0)
            .OrderBy(item =>
                Vector2.Distance(bot.GetTruePosition(), item.Node!.Value.Position) +
                ((Array.IndexOf(PlausibleFakeTaskNodes, item.Id) + bot.PlayerId + epoch) % 4) * 0.7f)
            .ToArray();
        return candidates.FirstOrDefault()?.Id;
    }

    private bool TryAssignAutonomousMurderTarget(PlayerControl killer, BotRuntimeState state)
    {
        var killerPosition = killer.GetTruePosition();
        var candidates = PlayerControl.AllPlayerControls
            .ToArray()
            .Where(player =>
                player &&
                player.PlayerId != killer.PlayerId &&
                player.Data is not null &&
                !player.Data.IsDead &&
                !player.Data.Disconnected &&
                !IsImpostor(player) &&
                CanObservePlayer(killer, player))
            .Select(player =>
            {
                var position = player.GetTruePosition();
                var distance = Vector2.Distance(killerPosition, position);
                var nearbyCrew = PlayerControl.AllPlayerControls
                    .ToArray()
                    .Count(other =>
                        other &&
                        other.PlayerId != player.PlayerId &&
                        other.PlayerId != killer.PlayerId &&
                        other.Data is not null &&
                        !other.Data.IsDead &&
                        !other.Data.Disconnected &&
                        !IsImpostor(other) &&
                        CanObservePlayer(killer, other) &&
                        Vector2.Distance(position, other.GetTruePosition()) <= 3.8f);
                var isBotVictim = player.Data.PlayerName.StartsWith("DeepBot ", StringComparison.Ordinal);
                var repeatPenalty = state.LastMurderTargetId == player.PlayerId ? 4.5f : 0f;
                var score = BotBehaviorPolicy.ScoreMurderCandidate(
                    distance,
                    nearbyCrew,
                    isBotVictim,
                    repeatPenalty > 0f,
                    ((killer.PlayerId + player.PlayerId + state.MurderTargetEpoch) % 5) * 0.08f);
                return new { Player = player, Distance = distance, NearbyCrew = nearbyCrew, IsBotVictim = isBotVictim, Score = score };
            })
            .Where(item => item.Distance <= 11f)
            .OrderByDescending(item => item.Score)
            .ToArray();
        state.MurderTargetEpoch++;
        if (candidates.Length == 0 || candidates[0].Score < 3.2f)
        {
            state.NextMurderPlanAt = Time.time + UnityEngine.Random.Range(2.5f, 5f);
            return false;
        }

        var preferredBotVictim = candidates
            .Where(item => item.IsBotVictim && item.Score >= 3.2f)
            .OrderByDescending(item => item.Score)
            .FirstOrDefault();
        var selected = preferredBotVictim ?? candidates[0];
        state.LastMurderTargetId = selected.Player.PlayerId;
        state.NextMurderPlanAt = Time.time + UnityEngine.Random.Range(6f, 10f);
        var assigned = TryAssignMurderPursuitRoute(
            killer,
            state,
            selected.Player,
            $"autonomous-kill-plan:score={selected.Score:0.0}; distance={selected.Distance:0.0}; " +
            $"nearbyCrew={selected.NearbyCrew}; botVictim={selected.IsBotVictim}");
        if (assigned)
        {
            _memory.RecordAction(
                killer,
                "murder_plan",
                $"selected {selected.Player.Data?.PlayerName}({selected.Player.PlayerId}); score={selected.Score:0.0}; botVictim={selected.IsBotVictim}");
            _log.LogInfo(
                $"DeepBot autonomous murder target selected: killer={killer.Data?.PlayerName}, " +
                $"target={selected.Player.Data?.PlayerName}({selected.Player.PlayerId}), score={selected.Score:0.0}, " +
                $"distance={selected.Distance:0.0}, nearbyCrew={selected.NearbyCrew}, botVictim={selected.IsBotVictim}.");
        }

        return assigned;
    }

    private PlayerControl PreferVisibleBotVictim(
        PlayerControl killer,
        PlayerControl requested,
        BotRuntimeState state,
        string source)
    {
        if (IsDeepBotPlayer(requested))
        {
            return requested;
        }

        var preferred = PlayerControl.AllPlayerControls
            .ToArray()
            .Where(player =>
                player &&
                player.PlayerId != killer.PlayerId &&
                player.Data is not null &&
                !player.Data.IsDead &&
                !player.Data.Disconnected &&
                !IsImpostor(player) &&
                IsDeepBotPlayer(player) &&
                CanObservePlayer(killer, player))
            .OrderBy(player => Vector2.Distance(killer.GetTruePosition(), player.GetTruePosition()))
            .FirstOrDefault();
        if (preferred is null)
        {
            return requested;
        }

        state.LastMurderTargetId = preferred.PlayerId;
        _log.LogInfo(
            $"DeepBot murder target redirected to visible AI victim: killer={killer.Data?.PlayerName}, " +
            $"requested={requested.Data?.PlayerName}({requested.PlayerId}), selected={preferred.Data?.PlayerName}({preferred.PlayerId}), " +
            $"source={source}.");
        return preferred;
    }

    private bool UpdateMurderPursuit(PlayerControl killer, BotRuntimeState state)
    {
        var target = state.TargetPlayerId.HasValue
            ? FindPlayerControl(state.TargetPlayerId.Value)
            : null;
        if (target is null ||
            target.Data is null ||
            target.Data.IsDead ||
            target.Data.Disconnected ||
            IsImpostor(target))
        {
            _log.LogInfo(
                $"DeepBot murder pursuit ended: killer={killer.Data?.PlayerName}, " +
                $"target={target?.Data?.PlayerName ?? state.TargetPlayerId?.ToString() ?? "none"}, reason=target-invalid.");
            state.ClearRoute();
            return true;
        }

        if (TryExecuteMurder(killer, target, "entered legal kill range during pursuit"))
        {
            state.ClearRoute();
            return true;
        }

        if (state.MurderPursuitUntil <= Time.time)
        {
            _log.LogInfo(
                $"DeepBot murder pursuit expired: killer={killer.Data?.PlayerName}, target={target.Data.PlayerName}, " +
                $"distance={Vector2.Distance(killer.GetTruePosition(), target.GetTruePosition()):0.00}, timeout={MurderPursuitSeconds:0}s.");
            state.ClearRoute();
            return true;
        }

        if (Time.time < state.NextMurderPursuitRefreshAt ||
            !CanObservePlayer(killer, target))
        {
            return false;
        }

        var livePosition = target.GetTruePosition();
        state.LastSeenPlayers[target.PlayerId] =
            new PlayerLastSeen(target.Data.PlayerName, livePosition, Time.time);
        state.NextMurderPursuitRefreshAt = Time.time + MurderPursuitRefreshSeconds;
        if (state.CurrentTargetPosition.HasValue &&
            !BotBehaviorPolicy.ShouldRefreshMovingTarget(
                state.CurrentTargetPosition.Value,
                livePosition,
                MurderPursuitRefreshDistance))
        {
            return false;
        }

        return TryAssignMurderPursuitRoute(
            killer,
            state,
            target,
            $"live-target-refresh:distance={Vector2.Distance(killer.GetTruePosition(), livePosition):0.00}");
    }

    private bool TryAssignMurderPursuitRoute(
        PlayerControl killer,
        BotRuntimeState state,
        PlayerControl target,
        string reason)
    {
        if (!killer ||
            !target ||
            killer.Data is null ||
            target.Data is null ||
            killer.Data.IsDead ||
            target.Data.IsDead ||
            target.Data.Disconnected ||
            !IsImpostor(killer) ||
            IsImpostor(target))
        {
            return false;
        }

        Vector2 targetPosition;
        if (CanObservePlayer(killer, target))
        {
            targetPosition = target.GetTruePosition();
            state.LastSeenPlayers[target.PlayerId] =
                new PlayerLastSeen(target.Data.PlayerName, targetPosition, Time.time);
        }
        else if (state.LastSeenPlayers.TryGetValue(target.PlayerId, out var seen) &&
                 Time.time - seen.SeenAt <= RecentSightSeconds)
        {
            targetPosition = seen.Position;
        }
        else
        {
            return false;
        }

        var continuing =
            state.ActionKind == BotActionKind.Stalk &&
            state.TargetPlayerId == target.PlayerId &&
            state.MurderPursuitUntil > Time.time;
        if (!continuing)
        {
            state.MurderPursuitUntil = Time.time + MurderPursuitSeconds;
            state.MurderPursuitStartedAt = Time.time;
        }

        var killDistance = GameRuleSettings.GetKillDistance(1.35f);
        AssignRoute(
            killer,
            state,
            targetPosition,
            $"MURDER_TARGET_{target.PlayerId}",
            $"{reason}; pursuitTarget={target.Data.PlayerName}({target.PlayerId}); directFollow=true",
            0.3f,
            0.65f,
            BotActionKind.Stalk,
            null,
            target.PlayerId,
            Mathf.Clamp(killDistance * 0.72f, 0.55f, 1f));
        state.NextMurderPursuitRefreshAt = Time.time + MurderPursuitRefreshSeconds;
        return state.ActionKind == BotActionKind.Stalk &&
               state.TargetPlayerId == target.PlayerId;
    }

    private static bool TryPickKnownTargetNode(PlayerControl observer, BotRuntimeState state, PlayerControl target, out string nodeId)
    {
        if (CanObservePlayer(observer, target))
        {
            nodeId = PickStalkNodeNear(target.GetTruePosition());
            return true;
        }

        if (state.LastSeenPlayers.TryGetValue(target.PlayerId, out var seen) && Time.time - seen.SeenAt <= 30f)
        {
            nodeId = PickStalkNodeNear(seen.Position);
            return true;
        }

        nodeId = string.Empty;
        return false;
    }

    private static bool CanObservePlayer(PlayerControl observer, PlayerControl target)
    {
        if (!observer || !target || observer.Data is null || target.Data is null || target.Data.IsDead)
        {
            return false;
        }

        var observerPosition = observer.GetTruePosition();
        var targetPosition = target.GetTruePosition();
        return Vector2.Distance(observerPosition, targetPosition) <= GetVisionDistance(observer) &&
            !PhysicsHelpers.AnythingBetween(observerPosition, targetPosition, Constants.ShipAndObjectsMask, false);
    }

    private static bool IsDeepBotPlayer(PlayerControl player)
    {
        return player &&
               player.Data is not null &&
               player.Data.PlayerName.StartsWith("DeepBot ", StringComparison.Ordinal);
    }

    private static string PickStalkNodeNear(Vector2 targetPosition)
    {
        var candidates = SkeldPathGraph.Instance.Nodes
            .Where(node => SkeldPathGraph.Instance.IsNodeAllowed(node.Id) && (node.Kind is NodeKind.Interaction or NodeKind.Hall or NodeKind.Corner))
            .Select(node => new { Node = node, Distance = Vector2.Distance(node.Position, targetPosition) })
            .Where(item => item.Distance is >= 2.8f and <= 5.6f)
            .OrderBy(item => Mathf.Abs(item.Distance - 4.1f))
            .Take(10)
            .ToArray();

        return candidates.Length > 0
            ? candidates[UnityEngine.Random.Range(0, candidates.Length)].Node.Id
            : SkeldPathGraph.Instance.NearestNode(targetPosition).Id;
    }

    private static string? MapSabotageIntentToNode(string? sabotage)
    {
        if (string.IsNullOrWhiteSpace(sabotage))
        {
            return null;
        }

        if (sabotage.Contains("reactor", StringComparison.OrdinalIgnoreCase)) return "REACTOR_MID";
        if (sabotage.Contains("o2", StringComparison.OrdinalIgnoreCase) || sabotage.Contains("oxygen", StringComparison.OrdinalIgnoreCase)) return "O2_CENTER";
        if (sabotage.Contains("light", StringComparison.OrdinalIgnoreCase) || sabotage.Contains("electrical", StringComparison.OrdinalIgnoreCase)) return "ELEC_SWITCH";
        if (sabotage.Contains("comm", StringComparison.OrdinalIgnoreCase)) return "COMMS_CENTER";
        return null;
    }

    private bool TryExecuteMurder(PlayerControl killer, PlayerControl target, string reason)
    {
        if (!IsMurderPlayWindowOpen(killer))
        {
            var state = GetState(killer);
            if (Time.time >= state.NextMurderDiagnosticAt)
            {
                state.NextMurderDiagnosticAt = Time.time + 1.25f;
                _log.LogInfo(
                    $"DeepBot murder waiting: killer={killer.Data?.PlayerName}, target={target.Data?.PlayerName}, " +
                    $"block={ExplainMurderPlayWindowBlock(killer)}, reason={reason}");
            }
            return false;
        }

        if (!CanMurderTarget(killer, target))
        {
            var state = GetState(killer);
            if (Time.time >= state.NextMurderDiagnosticAt)
            {
                state.NextMurderDiagnosticAt = Time.time + 1.25f;
                _log.LogInfo(
                    $"DeepBot murder waiting: killer={killer.Data?.PlayerName}, target={target.Data?.PlayerName}, " +
                    $"block={ExplainMurderBlock(killer, target)}, reason={reason}");
            }
            return false;
        }

        var exposure = ClassifyMurderExposure(killer, target);
        var hasEscapeRoute = HasEscapeRoute(killer, target);
        if (!BotBehaviorPolicy.ShouldExecuteMurder(exposure, hasEscapeRoute))
        {
            var state = GetState(killer);
            if (Time.time >= state.NextMurderDiagnosticAt)
            {
                state.NextMurderDiagnosticAt = Time.time + 1.25f;
                _log.LogInfo(
                    $"DeepBot murder held: killer={killer.Data?.PlayerName}, target={target.Data?.PlayerName}, " +
                    $"exposure={exposure}, escapeRoute={hasEscapeRoute}, reason={reason}");
            }
            return false;
        }

        _log.LogInfo(
            $"DeepBot murder attempting: killer={killer.Data?.PlayerName}, target={target.Data?.PlayerName}, " +
            $"exposure={exposure}, escapeRoute={hasEscapeRoute}, reason={reason}");
        try
        {
            var murderPosition = target.GetTruePosition();
            var torHandled = TorRoleAdapter.TryExecuteRuleAwareMurder(
                killer,
                target,
                out var targetKilled,
                out var torOutcome);
            if (!torHandled)
            {
                killer.CmdCheckMurder(target);
                targetKilled = target.Data?.IsDead == true;
                torOutcome = "vanilla CmdCheckMurder fallback";
            }

            if (!targetKilled)
            {
                var blockedState = GetState(killer);
                blockedState.NextMurderPlanAt = Time.time + UnityEngine.Random.Range(2.5f, 4.5f);
                _memory.RecordAction(
                    killer,
                    "murder_blocked",
                    $"kill on {target.Data?.PlayerName}({target.PlayerId}) was blocked; {torOutcome}");
                _log.LogInfo(
                    $"DeepBot murder blocked by game rules: killer={killer.Data?.PlayerName}, " +
                    $"target={target.Data?.PlayerName}, outcome={torOutcome}.");
                return true;
            }

            killer.killTimer = Mathf.Max(killer.killTimer, GameRuleSettings.GetKillCooldown(10f));
            var murderState = GetState(killer);
            murderState.LastObservedKillTimer = killer.killTimer;
            murderState.LastKillTimerSampleAt = Time.time;
            _memory.RecordAction(killer, "murder", $"killed {target.Data?.PlayerName}({target.PlayerId}); exposure={exposure}; rule={torOutcome}; reason={reason}");
            _log.LogInfo(
                $"DeepBot murder executed: killer={killer.Data?.PlayerName}, target={target.Data?.PlayerName}, " +
                $"outcome={torOutcome}.");
            BeginPostMurderEscape(killer, murderState, target.PlayerId, murderPosition);
            return true;
        }
        catch (Exception ex)
        {
            if (target.Data is not null && target.Data.IsDead)
            {
                _log.LogWarning($"DeepBot murder command threw after target death: {ex.Message}");
                return true;
            }

            _log.LogWarning($"DeepBot murder command failed: {ex.Message}");
            return false;
        }
    }

    private void TickKillCooldown(PlayerControl player, BotRuntimeState state)
    {
        if (!player ||
            player.Data is null ||
            player.Data.IsDead ||
            !IsImpostor(player) ||
            player.killTimer <= 0f)
        {
            state.LastObservedKillTimer = player ? player.killTimer : 0f;
            state.LastKillTimerSampleAt = Time.time;
            return;
        }

        var elapsed = state.LastKillTimerSampleAt > 0f
            ? Time.time - state.LastKillTimerSampleAt
            : 0f;
        var before = player.killTimer;
        player.killTimer = BotBehaviorPolicy.AdvanceVirtualKillCooldown(
            before,
            state.LastObservedKillTimer,
            elapsed);
        state.LastObservedKillTimer = player.killTimer;
        state.LastKillTimerSampleAt = Time.time;
        if (before > 0f && player.killTimer <= 0f)
        {
            _log.LogInfo(
                $"DeepBot virtual kill cooldown ready: bot={player.Data.PlayerName}({player.PlayerId}), " +
                $"previous={before:0.00}s, elapsed={elapsed:0.00}s.");
        }
    }

    private void FreezeKillCooldownSamples()
    {
        foreach (var impostor in EnumerateDeepBots().Where(IsImpostor))
        {
            var state = GetState(impostor);
            state.LastObservedKillTimer = impostor.killTimer;
            state.LastKillTimerSampleAt = Time.time;
        }
    }

    private void ResetRoundRoutes(string reason)
    {
        foreach (var bot in EnumerateDeepBots())
        {
            var state = GetState(bot);
            Stop(bot.MyPhysics);
            state.ClearRoute();
            state.ClearEmergency();
            state.PendingDecision = null;
            state.PostTaskPauseUntil = 0f;
            state.PostTaskWanderPending = false;
            if (string.Equals(reason, "match-start", StringComparison.Ordinal))
            {
                state.NextSabotageAt = 0f;
                state.ImpostorOpeningCoverCompleted = false;
                state.ImpostorOpeningCoverPending = false;
                state.ImpostorAmbientEpoch = 0;
            }
            state.NextDecisionAt = Time.time + UnityEngine.Random.Range(0.25f, 1.1f);
            _memory.RecordAction(bot, "round_resume", $"cleared stale route at {reason}");
        }
    }

    private void ResetImpostorKillCooldownsFromRoomRules(string reason)
    {
        var configuredCooldown = GameRuleSettings.GetKillCooldown(10f);
        foreach (var impostor in EnumerateDeepBots().Where(IsImpostor))
        {
            var state = GetState(impostor);
            impostor.killTimer = configuredCooldown;
            state.LastObservedKillTimer = configuredCooldown;
            state.LastKillTimerSampleAt = Time.time;
            state.NextMurderPlanAt = Time.time + configuredCooldown;
            _log.LogInfo(
                $"DeepBot kill cooldown synchronized from room rules: bot={impostor.Data?.PlayerName}({impostor.PlayerId}), " +
                $"reason={reason}, configured={configuredCooldown:0.00}s, startsAfterAllMovable=true.");
        }
    }

    private void LogRoomRuleSnapshot()
    {
        var rules = GameRuleSettings.CaptureSnapshot();
        var actualImpostors = PlayerControl.AllPlayerControls
            .ToArray()
            .Count(player =>
                player &&
                player.Data is not null &&
                !player.Data.Disconnected &&
                IsImpostor(player));
        var taskEligibleCrew = PlayerControl.AllPlayerControls
            .ToArray()
            .Count(player =>
                player &&
                player.Data is not null &&
                !player.Data.Disconnected &&
                IsTaskCompletingRole(player));
        var expectedTaskTotal = taskEligibleCrew * rules.CrewmateTaskCount;
        var nativeTaskTotal = GameData.Instance is null ? -1 : GameData.Instance.TotalTasks;
        var level = actualImpostors == rules.NumImpostors ? "ok" : "mismatch";
        _log.LogInfo(
            $"DeepBot room-rule snapshot: level={level}, {rules.Describe()}, " +
            $"actualAssignedImpostors={actualImpostors}, taskEligibleCrew={taskEligibleCrew}, " +
            $"expectedCrewTaskTotal={expectedTaskTotal}, nativeTaskTotal={nativeTaskTotal}, " +
            "taskScope=crew-only, source=host-current-game-options.");
        if (actualImpostors != rules.NumImpostors)
        {
            _log.LogWarning(
                $"DeepBot native role assignment differs from host setting: " +
                $"configuredImpostors={rules.NumImpostors}, actualAssignedImpostors={actualImpostors}. " +
                "AI will not invent hidden roles; native game assignment remains authoritative.");
        }
    }

    private bool IsMurderPlayWindowOpen(PlayerControl killer)
    {
        return IsBotActionWindowOpen(killer) && !killer.inVent;
    }

    private string ExplainMurderPlayWindowBlock(PlayerControl killer)
    {
        if (!_playClockStarted)
        {
            return "active-play-not-started";
        }

        if (_meetingTransitionActive || MeetingHud.Instance || ExileController.Instance)
        {
            return "meeting-or-exile-transition";
        }

        if (IsIntroPresentationActive())
        {
            return "intro-cutscene-active";
        }

        if (!killer || !killer.moveable)
        {
            return "killer-not-moveable";
        }

        if (killer.inVent || killer.walkingToVent)
        {
            return "killer-in-vent-transition";
        }

        return "play-window-closed";
    }

    private void BeginPostMurderEscape(
        PlayerControl killer,
        BotRuntimeState state,
        byte victimId,
        Vector2 murderPosition)
    {
        state.LastMurderVictimId = victimId;
        state.LastMurderPosition = murderPosition;
        state.PostMurderDeceptionUntil = Time.time + PostMurderDeceptionSeconds;
        state.PostMurderReturnArmedAt = Time.time + UnityEngine.Random.Range(4.5f, 6.5f);
        state.PostMurderReturnTriggered = false;
        state.PostMurderEscapePending = true;
        state.PostMurderMovementUnlockAt = Time.time + PostMurderAnimationGraceSeconds;
        state.PostMurderMandatoryEscapeUntil = Time.time + PostMurderMandatoryEscapeSeconds;
        state.PostMurderNextReplanAt = state.PostMurderMovementUnlockAt;
        Stop(killer.MyPhysics);
        state.ClearRoute();
        _memory.RecordAction(
            killer,
            "murder_escape",
            $"queued escape after native kill animation for body playerId={victimId}");
        _log.LogInfo(
            $"DeepBot post-murder escape queued: killer={killer.Data?.PlayerName}, victim={victimId}, " +
            $"bodyPosition={murderPosition}, animationGrace={PostMurderAnimationGraceSeconds:0.0}s, " +
            $"minimumClearance={PostMurderMinimumBodyClearance:0.0}, returnWindow={PostMurderDeceptionSeconds:0}s.");
    }

    private bool TryMaintainPostMurderEscape(PlayerControl killer, BotRuntimeState state)
    {
        if (!state.LastMurderVictimId.HasValue ||
            Time.time > state.PostMurderDeceptionUntil)
        {
            state.PostMurderEscapePending = false;
            return false;
        }

        var bodyDistance = Vector2.Distance(killer.GetTruePosition(), state.LastMurderPosition);
        var stillNeedsMandatoryEscape =
            state.PostMurderEscapePending ||
            bodyDistance < PostMurderMinimumBodyClearance ||
            state.ActionKind == BotActionKind.Escape;
        if (!stillNeedsMandatoryEscape)
        {
            return false;
        }

        var meetingOrExileActive = MeetingHud.Instance || ExileController.Instance;
        if (!BotBehaviorPolicy.CanReleasePostMurderMovement(
                Time.time,
                state.PostMurderMovementUnlockAt,
                meetingOrExileActive))
        {
            Stop(killer.MyPhysics);
            return true;
        }

        // Virtual killers can remain move-locked because the native kill
        // animation coroutine is owned by another client. Release only after
        // the normal animation grace period and never during meetings/exile.
        if (!killer.moveable)
        {
            killer.moveable = true;
            _log.LogInfo(
                $"DeepBot post-murder movement lock released: killer={killer.Data?.PlayerName}, " +
                $"bodyDistance={bodyDistance:0.00}.");
        }

        var needsFreshRoute =
            state.PostMurderEscapePending ||
            !state.HasActiveRoute ||
            state.ActionKind != BotActionKind.Escape;
        if (needsFreshRoute && Time.time >= state.PostMurderNextReplanAt)
        {
            var escapeNode = PickPostMurderEscapeNode(killer.GetTruePosition(), state.LastMurderPosition);
            state.PostMurderEscapePending = false;
            state.PostMurderNextReplanAt = Time.time + 2.5f;
            AssignRoute(
                killer,
                state,
                escapeNode,
                $"post-murder-escape:victim={state.LastMurderVictimId}; replanned-after-animation=true",
                5.5f,
                10.5f,
                BotActionKind.Escape,
                null,
                null);
            _memory.RecordAction(
                killer,
                "murder_escape",
                $"began physical escape toward {escapeNode} from actual post-animation position");
            _log.LogInfo(
                $"DeepBot post-murder physical escape started: killer={killer.Data?.PlayerName}, " +
                $"victim={state.LastMurderVictimId}, from={killer.GetTruePosition()}, target={escapeNode}, " +
                $"bodyDistance={bodyDistance:0.00}.");
        }

        if (bodyDistance >= PostMurderMinimumBodyClearance &&
            Time.time >= state.PostMurderMandatoryEscapeUntil &&
            state.ActionKind != BotActionKind.Escape)
        {
            return false;
        }

        return state.PostMurderEscapePending ||
               state.ActionKind == BotActionKind.Escape ||
               bodyDistance < PostMurderMinimumBodyClearance;
    }

    private void TryUpdatePostMurderDeception(PlayerControl killer, BotRuntimeState state)
    {
        if (!state.LastMurderVictimId.HasValue ||
            state.PostMurderReturnTriggered ||
            state.PostMurderEscapePending ||
            Vector2.Distance(killer.GetTruePosition(), state.LastMurderPosition) <
                PostMurderMinimumBodyClearance ||
            Time.time < state.PostMurderReturnArmedAt ||
            Time.time > state.PostMurderDeceptionUntil ||
            state.ActionKind == BotActionKind.ReturnToBody)
        {
            return;
        }

        var body = UnityEngine.Object.FindObjectsOfType<DeadBody>()
            .FirstOrDefault(candidate =>
                DeadBodyPerception.IsVisibleAndReportable(candidate) &&
                candidate.ParentId == state.LastMurderVictimId.Value);
        if (body is null)
        {
            return;
        }

        var bodyPosition = body.TruePosition;
        var discoverer = PlayerControl.AllPlayerControls
            .ToArray()
            .Where(player =>
                player &&
                player.PlayerId != killer.PlayerId &&
                player.Data is not null &&
                !player.Data.IsDead &&
                !player.Data.Disconnected &&
                !IsImpostor(player) &&
                CanObservePlayer(killer, player))
            .Select(player => new
            {
                Player = player,
                BodyDistance = Vector2.Distance(player.GetTruePosition(), bodyPosition),
                KillerDistance = Vector2.Distance(killer.GetTruePosition(), player.GetTruePosition())
            })
            .Where(item => item.BodyDistance <= 6f && item.KillerDistance <= 15f)
            .OrderBy(item => item.BodyDistance)
            .FirstOrDefault();
        if (discoverer is null || Vector2.Distance(killer.GetTruePosition(), bodyPosition) < 4f)
        {
            return;
        }

        state.PostMurderReturnTriggered = true;
        AssignRoute(
            killer,
            state,
            bodyPosition,
            $"RETURN_BODY_{body.ParentId}",
            $"post-murder-blend-in:discoverer={discoverer.Player.Data?.PlayerName}({discoverer.Player.PlayerId})",
            1.2f,
            2.6f,
            BotActionKind.ReturnToBody,
            null,
            discoverer.Player.PlayerId,
            1.45f);
        _memory.RecordAction(
            killer,
            "murder_return",
            $"returned toward body as {discoverer.Player.Data?.PlayerName} approached");
        _log.LogInfo(
            $"DeepBot post-murder deception return: killer={killer.Data?.PlayerName}, victim={body.ParentId}, " +
            $"discoverer={discoverer.Player.Data?.PlayerName}({discoverer.Player.PlayerId}), " +
            $"discovererBodyDistance={discoverer.BodyDistance:0.0}.");
    }

    private static string PickPostMurderEscapeNode(Vector2 killerPosition, Vector2 murderPosition)
    {
        var candidates = SkeldPathGraph.Instance.Nodes
            .Where(node =>
                SkeldPathGraph.Instance.IsNodeAllowed(node.Id) &&
                node.Kind is NodeKind.Interaction or NodeKind.Corner or NodeKind.Landmark)
            .Select(node => new
            {
                Node = node,
                FromKiller = Vector2.Distance(killerPosition, node.Position),
                FromBody = Vector2.Distance(murderPosition, node.Position)
            })
            .Where(item => item.FromKiller is >= 4.5f and <= 14f && item.FromBody >= 6f)
            .OrderBy(item => Mathf.Abs(item.FromBody - 9f) + item.FromKiller * 0.12f)
            .Select(item => item.Node.Id)
            .ToArray();
        return candidates.Length > 0
            ? candidates[UnityEngine.Random.Range(0, Math.Min(4, candidates.Length))]
            : PickEscapeNodeAwayFrom(murderPosition);
    }

    private static string ExplainMurderBlock(PlayerControl killer, PlayerControl target)
    {
        if (!killer || killer.Data is null)
        {
            return "killer-invalid";
        }

        if (!target || target.Data is null)
        {
            return "target-invalid";
        }

        if (killer.Data.IsDead)
        {
            return "killer-dead";
        }

        if (target.Data.IsDead || target.Data.Disconnected)
        {
            return "target-dead-or-disconnected";
        }

        if (!IsImpostor(killer))
        {
            return "killer-not-impostor";
        }

        if (IsImpostor(target))
        {
            return "target-is-impostor-teammate";
        }

        if (killer.killTimer > 0f)
        {
            return $"kill-cooldown={killer.killTimer:0.00}s";
        }

        var distance = Vector2.Distance(killer.GetTruePosition(), target.GetTruePosition());
        var allowed = GameRuleSettings.GetKillDistance(1.35f);
        if (distance > allowed)
        {
            return $"distance={distance:0.00}>allowed={allowed:0.00}";
        }

        if (PhysicsHelpers.AnythingBetween(
                killer.GetTruePosition(),
                target.GetTruePosition(),
                Constants.ShipAndObjectsMask,
                false))
        {
            return "line-of-sight-blocked";
        }

        return "native-rule-rejected";
    }

    private static bool CanMurderTarget(PlayerControl killer, PlayerControl target)
    {
        if (!killer || !target || killer.Data is null || target.Data is null || killer.Data.IsDead || target.Data.IsDead)
        {
            return false;
        }

        if (!IsImpostor(killer) || IsImpostor(target) || killer.killTimer > 0f)
        {
            return false;
        }

        if (Vector2.Distance(killer.GetTruePosition(), target.GetTruePosition()) > GameRuleSettings.GetKillDistance(1.35f))
        {
            return false;
        }

        return !PhysicsHelpers.AnythingBetween(killer.GetTruePosition(), target.GetTruePosition(), Constants.ShipAndObjectsMask, false);
    }

    private static string ClassifyMurderExposure(PlayerControl killer, PlayerControl target)
    {
        var witnesses = 0;
        foreach (var observer in PlayerControl.AllPlayerControls)
        {
            if (!observer || observer.PlayerId == killer.PlayerId || observer.PlayerId == target.PlayerId || observer.Data is null || observer.Data.IsDead)
            {
                continue;
            }

            var vision = GetVisionDistance(observer);
            var seesKiller = Vector2.Distance(observer.GetTruePosition(), killer.GetTruePosition()) <= vision &&
                !PhysicsHelpers.AnythingBetween(observer.GetTruePosition(), killer.GetTruePosition(), Constants.ShipAndObjectsMask, false);
            var seesTarget = Vector2.Distance(observer.GetTruePosition(), target.GetTruePosition()) <= vision &&
                !PhysicsHelpers.AnythingBetween(observer.GetTruePosition(), target.GetTruePosition(), Constants.ShipAndObjectsMask, false);
            if (seesKiller && seesTarget)
            {
                witnesses++;
            }
        }

        return witnesses == 0 ? "low" : IsTightCrowd(killer, target, witnesses) ? "crowded" : "witnessed";
    }

    private static bool IsTightCrowd(PlayerControl killer, PlayerControl target, int witnessCount)
    {
        if (witnessCount < 3)
        {
            return false;
        }

        var center = target.GetTruePosition();
        var clustered = 0;
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (!player || player.Data is null || player.Data.IsDead)
            {
                continue;
            }

            if (Vector2.Distance(center, player.GetTruePosition()) <= 1.1f)
            {
                clustered++;
            }
        }

        return clustered >= 4 && Vector2.Distance(killer.GetTruePosition(), center) <= 1.25f;
    }

    private static bool HasEscapeRoute(PlayerControl killer, PlayerControl target)
    {
        var targetNode = SkeldPathGraph.Instance.NearestNode(target.GetTruePosition());
        var routes = SkeldPathGraph.Instance.FindTopRoutes(killer.GetTruePosition(), PickEscapeNodeAwayFrom(targetNode.Position), 1);
        return routes.Count > 0 && routes[0].Count >= 3;
    }

    private static string PickEscapeNodeAwayFrom(Vector2 dangerPosition)
    {
        return SkeldPathGraph.Instance.Nodes
            .Where(node => SkeldPathGraph.Instance.IsNodeAllowed(node.Id) && (node.Kind is NodeKind.Corner or NodeKind.Hall or NodeKind.Door))
            .OrderByDescending(node => Vector2.Distance(node.Position, dangerPosition))
            .First().Id;
    }

    private static BotActionKind MapDecisionAction(BotActionDecision? decision)
    {
        return NormalizeAction(decision?.Action) switch
        {
            "idle" => BotActionKind.Idle,
            "hide" => BotActionKind.Hide,
            _ => BotActionKind.Llm
        };
    }

    private static PlayerControl? FindPlayerControl(byte playerId)
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

    private static bool IsHostAuthority()
    {
        var client = AmongUsClient.Instance;
        return client &&
            client.NetworkMode == NetworkModes.LocalGame &&
            client.AmHost &&
            client.ClientId >= 0 &&
            client.ClientId == client.HostId &&
            client.GameState == InnerNetClient.GameStates.Started &&
            ShipStatus.Instance &&
            GameRuleSettings.IsSkeldMap();
    }

    private static IEnumerable<PlayerControl> EnumerateDeepBots()
    {
        var list = PlayerControl.AllPlayerControls;
        if (list is null)
        {
            yield break;
        }

        for (var i = 0; i < list.Count; i++)
        {
            var player = list[i];
            if (player && player.Data is not null && player.Data.PlayerName.StartsWith("DeepBot ", StringComparison.Ordinal))
            {
                yield return player;
            }
        }
    }

    private BotRuntimeState GetState(PlayerControl bot)
    {
        if (!_states.TryGetValue(bot.PlayerId, out var state))
        {
            state = new BotRuntimeState();
            _states[bot.PlayerId] = state;
            var personality = BotPersonalityCatalog.ForPlayer(bot.PlayerId);
            _log.LogInfo(
                $"DeepBot personality assigned: bot={bot.Data?.PlayerName ?? bot.PlayerId.ToString()}, " +
                $"personality={personality.Name}, postTaskPause={personality.PostTaskPauseMin:0.0}-" +
                $"{personality.PostTaskPauseMax:0.0}s, wanderChance={personality.WanderChance:P0}.");
        }

        return state;
    }

    private static bool IsDeadCrewmate(PlayerControl player, BotRuntimeState state)
    {
        if (player.Data is null)
        {
            return false;
        }

        if (!player.Data.IsDead)
        {
            state.TeamKnown = true;
            state.WasImpostorWhenAlive = IsImpostor(player);
            return false;
        }

        // Some role implementations replace Role after death. Remembering the
        // living team prevents a former crewmate ghost from being misclassified
        // and frozen, while still keeping dead impostors out of task logic.
        return state.TeamKnown
            ? !state.WasImpostorWhenAlive
            : !IsImpostor(player);
    }

    private static int CountIncompleteNormalTasks(PlayerControl bot)
    {
        if (!bot || bot.myTasks is null)
        {
            return 0;
        }

        var count = 0;
        for (var index = 0; index < bot.myTasks.Count; index++)
        {
            var task = bot.myTasks[index];
            if (task && !task.IsComplete && !IsSabotage(task.TaskType))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsImpostor(PlayerControl player)
    {
        return player.Data is not null && player.Data.Role is not null && player.Data.Role.IsImpostor;
    }

    private static bool IsTaskCompletingRole(PlayerControl player)
    {
        return !IsImpostor(player) &&
               (!TorRoleAdapter.TryGetRole(player, out var role) || !role.IsNeutral);
    }

    private static bool UsesFakeTaskCover(PlayerControl player)
    {
        return IsImpostor(player) ||
               (TorRoleAdapter.TryGetRole(player, out var role) && role.IsNeutral);
    }

    private sealed class BotRuntimeState
    {
        public List<NavNode> Route { get; set; } = [];
        public int RouteIndex { get; set; }
        public string? CurrentTargetNode { get; set; }
        public Vector2? CurrentTargetPosition { get; set; }
        public Vector2? RouteEndpoint { get; set; }
        public float ArrivalDistance { get; set; } = 1.05f;
        public float DwellSeconds { get; set; }
        public float DwellUntil { get; set; }
        public float NextDecisionAt { get; set; }
        public bool DecisionInFlight { get; set; }
        public BotActionDecision? PendingDecision { get; set; }
        public float NextLlmIntentAt { get; set; }
        public BotActionKind ActionKind { get; set; }
        public uint? ActiveTaskId { get; set; }
        public byte? TargetPlayerId { get; set; }
        public Dictionary<byte, PlayerLastSeen> LastSeenPlayers { get; } = [];
        public Dictionary<string, float> UnreachableTargetUntil { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, float> VisitedTargetUntil { get; } = new(StringComparer.Ordinal);
        public string? LastSafeNodeId { get; set; }
        public Vector2 LastProgressPosition { get; set; }
        public float LastProgressAt { get; set; }
        public float LastProgressTargetDistance { get; set; } = float.MaxValue;
        public int LastProgressRouteIndex { get; set; } = -1;
        public float NextReturnLogAt { get; set; }
        public float NextEmergencyInterruptAt { get; set; }
        public uint? EmergencyTaskId { get; set; }
        public TaskTypes? EmergencyTaskType { get; set; }
        public SystemTypes? EmergencySystem { get; set; }
        public int? EmergencyConsoleId { get; set; }
        public Vector2? EmergencyConsolePosition { get; set; }
        public float EmergencyUseDistance { get; set; } = 0.75f;
        public bool EmergencyResponder { get; set; }
        public bool EmergencyInteractionActive { get; set; }
        public float EmergencyAssignedAt { get; set; }
        public float EmergencyObservedAt { get; set; }
        public float EmergencyReconsiderAt { get; set; }
        public int EmergencyDecisionEpoch { get; set; }
        public float EmergencyLastInteractionAt { get; set; }
        public float EmergencyLastPanelSwitchAt { get; set; }
        public float NextEmergencyProximityLogAt { get; set; }
        public float NextSabotageAt { get; set; }
        public float NextSabotageDiagnosticAt { get; set; }
        public bool ImpostorOpeningCoverCompleted { get; set; }
        public bool ImpostorOpeningCoverPending { get; set; }
        public int ImpostorAmbientEpoch { get; set; }
        public float NextBodyCheckAt { get; set; }
        public float NextBodyPerceptionDiagnosticAt { get; set; }
        public float NextTaskDiagnosticAt { get; set; }
        public Vector2 DesiredMoveDirection { get; set; }
        public float DesiredMoveSpeedMultiplier { get; set; }
        public float DesiredMoveUntil { get; set; }
        public int TaskSelectionEpoch { get; set; }
        public int RouteVariant { get; set; }
        public int StuckSamples { get; set; }
        public int AvoidanceSide { get; set; }
        public Vector2 AvoidanceDirection { get; set; }
        public float AvoidanceUntil { get; set; }
        public float NextAvoidanceSideChangeAt { get; set; }
        public bool GhostTaskModeLogged { get; set; }
        public float NextGhostProgressDiagnosticAt { get; set; }
        public bool TeamKnown { get; set; }
        public bool WasImpostorWhenAlive { get; set; }
        public int SabotageDecisionEpoch { get; set; }
        public int MurderTargetEpoch { get; set; }
        public float NextMurderPlanAt { get; set; }
        public byte? LastMurderTargetId { get; set; }
        public float MurderPursuitStartedAt { get; set; }
        public float MurderPursuitUntil { get; set; }
        public float NextMurderPursuitRefreshAt { get; set; }
        public float NextMurderDiagnosticAt { get; set; }
        public float LastObservedKillTimer { get; set; }
        public float LastKillTimerSampleAt { get; set; }
        public float PostTaskPauseUntil { get; set; }
        public bool PostTaskWanderPending { get; set; }
        public Dictionary<byte, ThreatTrack> ThreatTracks { get; } = [];
        public float NextThreatScanAt { get; set; }
        public float ThreatEvadeUntil { get; set; }
        public byte? LastMurderVictimId { get; set; }
        public Vector2 LastMurderPosition { get; set; }
        public float PostMurderDeceptionUntil { get; set; }
        public float PostMurderReturnArmedAt { get; set; }
        public bool PostMurderReturnTriggered { get; set; }
        public bool PostMurderEscapePending { get; set; }
        public float PostMurderMovementUnlockAt { get; set; }
        public float PostMurderMandatoryEscapeUntil { get; set; }
        public float PostMurderNextReplanAt { get; set; }
        public int LastConsumedMeetingSerial { get; set; }
        public float SocialFollowUntil { get; set; }
        public float NextSocialFollowRefreshAt { get; set; }
        public bool HasActiveRoute => Route.Count > 0 || DwellUntil > 0f;

        public void ClearRoute()
        {
            Route.Clear();
            RouteIndex = 0;
            CurrentTargetNode = null;
            CurrentTargetPosition = null;
            RouteEndpoint = null;
            ArrivalDistance = 1.05f;
            DwellSeconds = 0f;
            DwellUntil = 0f;
            ActionKind = BotActionKind.None;
            ActiveTaskId = null;
            TargetPlayerId = null;
            LastSafeNodeId = null;
            LastProgressPosition = default;
            LastProgressAt = 0f;
            LastProgressTargetDistance = float.MaxValue;
            LastProgressRouteIndex = -1;
            NextReturnLogAt = 0f;
            DesiredMoveDirection = Vector2.zero;
            DesiredMoveSpeedMultiplier = 0f;
            DesiredMoveUntil = 0f;
            StuckSamples = 0;
            AvoidanceDirection = Vector2.zero;
            AvoidanceUntil = 0f;
            NextAvoidanceSideChangeAt = 0f;
            MurderPursuitStartedAt = 0f;
            MurderPursuitUntil = 0f;
            NextMurderPursuitRefreshAt = 0f;
            NextMurderDiagnosticAt = 0f;
        }

        public void ClearEmergency()
        {
            EmergencyTaskId = null;
            EmergencyTaskType = null;
            EmergencySystem = null;
            EmergencyConsoleId = null;
            EmergencyConsolePosition = null;
            EmergencyUseDistance = 0.75f;
            EmergencyResponder = false;
            EmergencyInteractionActive = false;
            EmergencyAssignedAt = 0f;
            EmergencyObservedAt = 0f;
            EmergencyReconsiderAt = 0f;
            EmergencyDecisionEpoch = 0;
            EmergencyLastInteractionAt = 0f;
            EmergencyLastPanelSwitchAt = 0f;
            NextEmergencyProximityLogAt = 0f;
        }
    }

    private readonly record struct TaskRouteTarget(PlayerTask Task, Vector2 Position, float UseDistance, string Source);

    private readonly record struct TaskPointCandidate(Vector2 Position, string Source, float ProjectionDistance);

    private readonly record struct PlayerLastSeen(string Name, Vector2 Position, float SeenAt);

    private readonly record struct ThreatTrack(float Distance, float SampleAt, int ApproachStreak);

    private readonly record struct EmergencyConsoleTarget(int ConsoleId, Vector2 Position, float UseDistance);

    private readonly record struct KnownCrewObservation(PlayerControl Player, Vector2 Position, float Age);

    private readonly record struct SabotagePlanCandidate(
        SystemTypes System,
        string Intent,
        string Goal,
        float Score,
        byte? TargetPlayerId);

    private readonly record struct SabotagePlan(
        SystemTypes System,
        string Intent,
        string Goal,
        string Reason,
        byte? TargetPlayerId);

    private enum BotActionKind
    {
        None,
        Task,
        Emergency,
        Report,
        Stalk,
        Idle,
        Hide,
        Escape,
        ReturnToBody,
        Evade,
        Observe,
        Ability,
        Llm
    }
}
