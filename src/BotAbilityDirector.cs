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
    private readonly ManualLogSource _log;
    private readonly BotMatchMemory _memory;
    private readonly BotActionDirector _actions;
    private readonly DeepSeekDecisionClient _deepSeek;
    private readonly Dictionary<byte, AbilityState> _states = [];
    private float _nextTickAt;

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
            if (bot.inVent)
            {
                if (state.ActiveVentId.HasValue && Time.time >= state.ExitVentAt)
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

            if (state.DecisionCompleted)
            {
                ApplyAbilityDecision(bot, state);
                continue;
            }

            if (!state.DecisionInFlight)
            {
                RequestAbilityDecision(bot, state);
            }
        }
    }

    private void RequestAbilityDecision(PlayerControl bot, AbilityState state)
    {
        var availableTorRoles = TorRoleAdapter.GetAbilityRoles(bot);
        var hasTorRole = TrySelectTorAbilityRole(bot, state, out var torRole);
        if (availableTorRoles.Count > 0 && !hasTorRole)
        {
            state.NextAbilityAt = Time.time + 1f;
            return;
        }
        var torVentReady = hasTorRole && TorRoleAdapter.CanUseVents(bot, torRole);
        if ((hasTorRole && !TorRoleAdapter.IsAbilityReady(bot, torRole) && !torVentReady) ||
            (!hasTorRole && IsRoleCoolingDown(bot.Data.Role)))
        {
            state.NextAbilityAt = Time.time + 2f;
            return;
        }

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

    private void ApplyAbilityDecision(PlayerControl bot, AbilityState state)
    {
        state.DecisionCompleted = false;
        var torRole = default(TorRoleInfo);
        var hasTorRole = !string.IsNullOrWhiteSpace(state.RequestedTorRole) &&
                         TorRoleAdapter.TryGetAbilityRole(bot, state.RequestedTorRole!, out torRole);
        var currentTorRole = hasTorRole ? torRole.Name : null;
        if (!string.Equals(state.RequestedTorRole, currentTorRole, StringComparison.Ordinal))
        {
            state.PendingDecision = null;
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
            state.RequestedTorRole = null;
            state.RequestedRole = null;
            state.NextAbilityAt = Time.time + 4f;
            _log.LogInfo(
                $"DeepBot discarded stale ability plan after role change: bot={bot.Data?.PlayerName}, " +
                $"currentRole={bot.Data?.RoleType}.");
            return;
        }

        state.RequestedTorRole = null;
        state.RequestedRole = null;
        var decision = state.PendingDecision ?? BuildStrategicFallback(bot, hasTorRole ? torRole : null);
        state.PendingDecision = null;
        var torVentReady = hasTorRole && TorRoleAdapter.CanUseVents(bot, torRole);
        if ((hasTorRole && !TorRoleAdapter.IsAbilityReady(bot, torRole) && !torVentReady) ||
            (!hasTorRole && IsRoleCoolingDown(bot.Data.Role)))
        {
            state.NextAbilityAt = Time.time + 3f;
            _log.LogInfo(
                $"DeepBot ability plan expired before execution: bot={bot.Data?.PlayerName}, " +
                $"role={(hasTorRole ? torRole.Name : bot.Data?.RoleType.ToString())}, reason=ability entered cooldown.");
            return;
        }

        if (!decision.Use || decision.Confidence < 0.52f)
        {
            var urgentBodyReview = hasTorRole &&
                                   torRole.Name == "Vulture" &&
                                   TorRoleAdapter.FindVisibleUsableBody(bot);
            state.NextAbilityAt = Time.time + (urgentBodyReview
                ? UnityEngine.Random.Range(2.5f, 4f)
                : UnityEngine.Random.Range(7f, 12f));
            _log.LogInfo(
                $"DeepBot ability held for strategy: bot={bot.Data?.PlayerName}, role={(hasTorRole ? torRole.Name : bot.Data?.RoleType.ToString())}, " +
                $"confidence={decision.Confidence:0.00}, urgentBodyReview={urgentBodyReview}, reason={decision.Reason ?? "no useful purpose"}.");
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
            $"role={(hasTorRole ? torRole.Name : bot.Data?.RoleType.ToString())}, target={targetId?.ToString() ?? "none"}, reason={decision.Reason}, confidence={decision.Confidence:0.00}");
        _log.LogInfo(
            $"DeepBot ability brain approved: bot={bot.Data?.PlayerName}, role={(hasTorRole ? torRole.Name : bot.Data?.RoleType.ToString())}, " +
            $"target={targetId?.ToString() ?? "none"}, confidence={decision.Confidence:0.00}, reason={decision.Reason}.");

        var abilityAction = string.IsNullOrWhiteSpace(decision.AbilityAction)
            ? "role"
            : decision.AbilityAction.Trim().ToLowerInvariant();
        if (abilityAction == "vent")
        {
            var canVent = hasTorRole
                ? TorRoleAdapter.CanUseVents(bot, torRole)
                : bot.Data?.Role?.CanVent == true;
            if (canVent)
            {
                TryUseOrRouteVent(bot, state, hasTorRole ? torRole.Name : bot.Data?.RoleType.ToString() ?? "unknown");
            }
            else
            {
                state.NextAbilityAt = Time.time + 6f;
                _log.LogInfo($"DeepBot LLM vent plan rejected by role rules: bot={bot.Data?.PlayerName}, role={(hasTorRole ? torRole.Name : bot.Data?.RoleType.ToString() ?? "unknown")}.");
            }
            return;
        }

        if (hasTorRole)
        {
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

            if (torRole.Name == "Vulture" &&
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
                    $"Vulture prioritized visible body playerId={visibleBody.ParentId} before report; llmReason={decision.Reason}");
                _log.LogInfo(
                    $"DeepBot Vulture body route approved by LLM: bot={bot.Data?.PlayerName}, victim={visibleBody.ParentId}, routed={routed}.");
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

            if (targetId.HasValue && FindPlayer(targetId.Value) is { } roleTarget && roleTarget && roleTarget.Data is not null &&
                !roleTarget.Data.IsDead && !roleTarget.Data.Disconnected)
            {
                var useRange = TorRoleAdapter.GetAbilityUseRange(torRole.Name);
                var distance = Vector2.Distance(bot.GetTruePosition(), roleTarget.GetTruePosition());
                var blocked = PhysicsHelpers.AnythingBetween(
                    bot.GetTruePosition(),
                    roleTarget.GetTruePosition(),
                    Constants.ShipAndObjectsMask,
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
                TorRoleAdapter.RegisterConfiguredCooldown(bot, torRole);
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
                TryUseTargetedAbility(bot, state, "shapeshift", targetId);
                break;
            case RoleTypes.Detective:
                TryUseTargetedAbility(bot, state, "detective-interrogate", targetId);
                break;
            default:
                if (bot.Data.Role.CanVent && bot.Data.Role.IsImpostor)
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
            state.ActiveVentId = vent.Id;
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
            state.ActiveVentId = vent.Id;
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
            state.ExitVentAt = 0f;
        }
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
            : FindAbilityTarget(bot, role.IsImpostor);
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
            target.Data is null ||
            target.Data.IsDead ||
            target.Data.Disconnected ||
            (role.IsImpostor && target.Data.Role is not null && target.Data.Role.IsImpostor))
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
                !player.Data.Role.IsImpostor &&
                Vector2.Distance(bot.GetTruePosition(), player.GetTruePosition()) <= 4.5f);
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

    private BotAbilityPrompt BuildAbilityPrompt(PlayerControl bot, TorRoleInfo? selectedTorRole = null)
    {
        var hasTorRole = selectedTorRole.HasValue;
        var torRole = selectedTorRole.GetValueOrDefault();
        var position = bot.GetTruePosition();
        var visible = EnumerateLivingPlayers()
            .Where(player => player.PlayerId != bot.PlayerId)
            .Select(player => new
            {
                Player = player,
                Distance = Vector2.Distance(position, player.GetTruePosition()),
                Blocked = PhysicsHelpers.AnythingBetween(
                    position,
                    player.GetTruePosition(),
                    Constants.ShipAndObjectsMask,
                    false)
            })
            .Where(item => item.Distance <= 6f && !item.Blocked)
            .Select(item => $"{item.Player.Data?.PlayerName}({item.Player.PlayerId}) distance={item.Distance:0.0}")
            .ToArray();
        var nearestVent = UnityEngine.Object.FindObjectsOfType<Vent>()
            .Where(vent => vent)
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
            hasTorRole ? torRole.Alignment : bot.Data?.Role?.IsImpostor == true ? "impostor" : "crewmate",
            hasTorRole ? torRole.Name : bot.Data?.RoleType.ToString() ?? "unknown",
            hasTorRole ? TorRoleAdapter.BuildStrategicRoleBrief(bot, torRole) : DescribeAbilityPurpose(bot),
            $"position={position}; node={SkeldPathGraph.Instance.NearestNode(position).Id}; killCooldown={bot.killTimer:0.0}; ventAccess={ventAccess}; nearestVent={nearestVent:0.0}; emergency={HasActiveEmergency(bot)}",
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
            _ when bot.Data?.Role?.CanVent == true && bot.Data.Role.IsImpostor =>
                "Impostor vent: covertly escape a dangerous scene or reposition for a planned kill; never vent with witnesses.",
            _ => "No strategically useful active ability."
        };
    }

    private BotAbilityDecision BuildStrategicFallback(PlayerControl bot, TorRoleInfo? selectedTorRole = null)
    {
        var hasTorRole = selectedTorRole.HasValue;
        var torRole = selectedTorRole.GetValueOrDefault();
        var target = FindAbilityTarget(bot, hasTorRole ? torRole.IsImpostorTeam : bot.Data.Role.IsImpostor);
        var visibleCrew = EnumerateLivingPlayers()
            .Where(player =>
                player.PlayerId != bot.PlayerId &&
                (player.Data?.Role?.IsImpostor != true || !(hasTorRole ? torRole.IsImpostorTeam : bot.Data.Role.IsImpostor)) &&
                Vector2.Distance(bot.GetTruePosition(), player.GetTruePosition()) <= 5f)
            .ToArray();
        if (hasTorRole)
        {
            var torTarget = TorRoleAdapter.FindPreferredAbilityTarget(bot, torRole) ?? target;
            var sheriffTarget = FindEvidenceBackedSuspect(bot, 0.78f);
            var deputyTarget = FindEvidenceBackedSuspect(bot, 0.62f);
            var mayorTarget = FindEvidenceBackedSuspect(bot, 0.86f);
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
                "Thief" =>
                    new BotAbilityDecision(false, null, "do not risk a blind theft without role evidence because an illegal target causes suicide", 0.74f),
                "Eraser" when torTarget is not null =>
                    new BotAbilityDecision(true, torTarget.PlayerId, "erase a nearby opposing role after the meeting", 0.59f),
                "Witch" when torTarget is not null && visibleCrew.Length <= 2 =>
                    new BotAbilityDecision(true, torTarget.PlayerId, "spell an isolated high-value opponent without exposing the caster", 0.62f),
                "Shifter" when torTarget is not null =>
                    new BotAbilityDecision(true, torTarget.PlayerId, "shift with a nearby player whose role may improve the objective", 0.58f),
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
                   bot.Data.Role.IsImpostor &&
                   FindClosestVent(bot, 1.35f) is not null &&
                   bot.killTimer > 0f &&
                   visibleCrew.Length <= 1 =>
                new BotAbilityDecision(true, null, "vent covertly while kill cooldown runs", 0.62f),
            _ => new BotAbilityDecision(false, null, "no meaningful ability purpose in the current situation", 0.58f)
        };
    }

    private PlayerControl? FindEvidenceBackedSuspect(PlayerControl bot, float minimumConfidence)
    {
        if (!_memory.TryGetPostMeetingIntent(bot.PlayerId, out var intent) ||
            intent.FollowIntent != "suspect" ||
            !intent.FollowPlayerId.HasValue ||
            intent.Confidence < minimumConfidence)
        {
            return null;
        }

        var target = FindPlayer(intent.FollowPlayerId.Value);
        if (!target || target!.Data is null || target.Data.IsDead || target.Data.Disconnected || target.PlayerId == bot.PlayerId)
        {
            return null;
        }

        var distance = Vector2.Distance(bot.GetTruePosition(), target.GetTruePosition());
        if (distance > 6f || PhysicsHelpers.AnythingBetween(
                bot.GetTruePosition(),
                target.GetTruePosition(),
                Constants.ShipAndObjectsMask,
                false))
        {
            return null;
        }

        return target;
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
            if (TorRoleAdapter.IsAbilityReady(bot, candidate) || TorRoleAdapter.CanUseVents(bot, candidate))
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
               (bot.Data?.Role?.CanVent == true && bot.Data.Role.IsImpostor);
    }

    private void TryUseOrRouteVent(PlayerControl bot, AbilityState state, string role)
    {
        if (bot.Data?.RoleType == RoleTypes.Engineer)
        {
            if (FindClosestVent(bot, 1.45f) is null) TryRouteToVent(bot, state, role);
            else TryUseEngineerVent(bot, state);
            return;
        }

        var hasTorRole = TorRoleAdapter.TryGetRole(bot, out var torRole);
        var vent = FindClosestVent(bot, 1.35f);
        if (vent is null)
        {
            TryRouteToVent(bot, state, role);
            return;
        }

        try
        {
            bot.MyPhysics.RpcEnterVent(vent.Id);
            _actions.CompleteRoleAbilityRoute(bot, $"{role}-entered-vent-{vent.Id}");
            state.ActiveVentId = vent.Id;
            state.ExitVentAt = Time.time + UnityEngine.Random.Range(2.2f, 4.8f);
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
                    TaskTypes.RestoreOxy)
            {
                return true;
            }
        }

        return false;
    }

    private static Vent? FindClosestVent(PlayerControl bot, float maximumDistance)
    {
        var position = bot.GetTruePosition();
        return UnityEngine.Object.FindObjectsOfType<Vent>()
            .Where(vent => vent)
            .Select(vent => new { Vent = vent, Distance = Vector2.Distance(position, vent.transform.position) })
            .Where(item => item.Distance <= maximumDistance)
            .OrderBy(item => item.Distance)
            .Select(item => item.Vent)
            .FirstOrDefault();
    }

    private void TryRouteToVent(PlayerControl bot, AbilityState state, string role)
    {
        var position = bot.GetTruePosition();
        var vent = UnityEngine.Object.FindObjectsOfType<Vent>()
            .Where(candidate => candidate)
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
            .Where(player => !impostor || !player.Data.Role.IsImpostor)
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
            client.AmHost &&
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
        public float ExitVentAt { get; set; }
        public float NextRouteLogAt { get; set; }
        public bool DecisionInFlight { get; set; }
        public bool DecisionCompleted { get; set; }
        public BotAbilityDecision? PendingDecision { get; set; }
        public RoleTypes? RequestedRole { get; set; }
        public string? RequestedTorRole { get; set; }
        public int AbilityRoleCursor { get; set; }
    }
}
