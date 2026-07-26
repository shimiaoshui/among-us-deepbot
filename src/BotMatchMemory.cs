using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using InnerNet;
using UnityEngine;

namespace AmongUsDeepSeekBots;

internal sealed class BotMatchMemory
{
    private const float SampleIntervalSeconds = 0.12f;
    private const float EncounterRefreshSeconds = 6f;

    private readonly ManualLogSource _log;
    private readonly BotEvolutionSkillStore _evolutionSkills;
    private readonly Dictionary<byte, MemoryState> _states = [];
    private readonly Dictionary<(byte KillerId, byte VictimId), float> _recentObservedMurders = [];
    private readonly Dictionary<(byte ActorId, string Action), float> _recentObservedSpecialActions = [];
    private float _nextSampleAt;
    private bool _matchActive;
    private int _matchSerial;
    private int _activeShipInstanceId;

    public BotMatchMemory(ManualLogSource log, BotEvolutionSkillStore evolutionSkills)
    {
        _log = log;
        _evolutionSkills = evolutionSkills;
    }

    internal int MatchSerial => _matchSerial;

    public void Update(PluginConfig config)
    {
        if (!IsStartedHostMatch())
        {
            _matchActive = false;
            _activeShipInstanceId = 0;
            _nextSampleAt = 0f;
            return;
        }

        var shipInstanceId = ShipStatus.Instance ? ShipStatus.Instance.GetInstanceID() : 0;
        if (!_matchActive ||
            (_activeShipInstanceId != 0 && shipInstanceId != 0 && shipInstanceId != _activeShipInstanceId))
        {
            _matchActive = true;
            _activeShipInstanceId = shipInstanceId;
            _matchSerial++;
            _states.Clear();
            _recentObservedMurders.Clear();
            _recentObservedSpecialActions.Clear();
            _log.LogInfo($"DeepBot match memory started: match={_matchSerial}, maxEventsPerBot={config.MaxMemoryEvents.Value}.");
        }

        if (Time.time < _nextSampleAt)
        {
            return;
        }

        _nextSampleAt = Time.time + SampleIntervalSeconds;
        var activeIds = new HashSet<byte>();
        foreach (var bot in EnumerateDeepBots())
        {
            if (bot.Data is null || bot.Data.Disconnected)
            {
                continue;
            }

            activeIds.Add(bot.PlayerId);
            var state = GetState(bot, config.MaxMemoryEvents.Value);
            SampleLocation(bot, state);
            SampleEncounters(bot, state);
            SampleBodies(bot, state);
            SampleLifeState(bot, state);
        }

        foreach (var staleId in _states.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            _states.Remove(staleId);
        }
    }

    public void RecordAction(PlayerControl bot, string category, string detail)
    {
        if (!bot || bot.Data is null || !IsDeepBot(bot))
        {
            return;
        }

        var state = GetState(bot, Plugin.Settings.MaxMemoryEvents.Value);
        Append(state, category, detail, SkeldPathGraph.Instance.NearestNode(bot.GetTruePosition()));
    }

    public void RecordPublicChat(PlayerControl source, string text)
    {
        if (!_matchActive || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var name = source && source.Data is not null ? source.Data.PlayerName : "Unknown";
        var clean = text.Trim();
        if (clean.Length > 180)
        {
            clean = clean[..180];
        }

        foreach (var state in _states.Values)
        {
            Append(state, "chat_claim", $"{name}: {clean}", null);
        }
    }

    public IReadOnlyList<byte> CapturePotentialMurderWitnesses(PlayerControl killer, PlayerControl victim)
    {
        if (!_matchActive || !killer || !victim)
        {
            return Array.Empty<byte>();
        }

        var witnessIds = new List<byte>();
        foreach (var observer in EnumerateDeepBots())
        {
            if (!observer || observer.Data is null || observer.Data.IsDead || observer.Data.Disconnected ||
                observer.PlayerId == killer.PlayerId || observer.PlayerId == victim.PlayerId)
            {
                continue;
            }

            var observerPosition = observer.GetTruePosition();
            var killerPosition = killer.GetTruePosition();
            var victimPosition = victim.GetTruePosition();
            if (BotPerceptionPolicy.CanWitnessMurderGeometry(
                    Vector2.Distance(observerPosition, killerPosition),
                    Vector2.Distance(observerPosition, victimPosition),
                    GetVisionDistance(observer),
                    PhysicsHelpers.AnythingBetween(observerPosition, killerPosition, Constants.ShipOnlyMask, false),
                    PhysicsHelpers.AnythingBetween(observerPosition, victimPosition, Constants.ShipOnlyMask, false)))
            {
                witnessIds.Add(observer.PlayerId);
            }
        }

        return witnessIds;
    }

    public IReadOnlyList<byte> RecordObservedMurder(
        PlayerControl killer,
        PlayerControl victim,
        IReadOnlyCollection<byte>? preEventWitnessIds = null)
    {
        if (!_matchActive || !killer || !victim)
        {
            return Array.Empty<byte>();
        }

        var murderKey = (killer.PlayerId, victim.PlayerId);
        if (_recentObservedMurders.TryGetValue(murderKey, out var lastRecordedAt) &&
            Time.time - lastRecordedAt < 0.75f)
        {
            return Array.Empty<byte>();
        }
        _recentObservedMurders[murderKey] = Time.time;

        var witnessIds = new List<byte>();
        var scannedObservers = 0;
        var rejectedByGeometry = 0;

        foreach (var observer in EnumerateDeepBots())
        {
            if (!observer || observer.Data is null || observer.Data.IsDead || observer.Data.Disconnected ||
                observer.PlayerId == killer.PlayerId || observer.PlayerId == victim.PlayerId)
            {
                continue;
            }

            scannedObservers++;

            var observerPosition = observer.GetTruePosition();
            var killerPosition = killer.GetTruePosition();
            var victimPosition = victim.GetTruePosition();
            var killerDistance = Vector2.Distance(observerPosition, killerPosition);
            var victimDistance = Vector2.Distance(observerPosition, victimPosition);
            var visionDistance = GetVisionDistance(observer);
            var killerBlocked = PhysicsHelpers.AnythingBetween(
                observerPosition,
                killerPosition,
                Constants.ShipOnlyMask,
                false);
            var victimBlocked = PhysicsHelpers.AnythingBetween(
                observerPosition,
                victimPosition,
                Constants.ShipOnlyMask,
                false);
            var state = GetState(observer, Plugin.Settings.MaxMemoryEvents.Value);
            var cachedVisibilityAge = Time.time - state.LastVisibilitySampleAt;
            var recentlySawPair =
                state.VisiblePlayerIds.Contains(killer.PlayerId) &&
                state.VisiblePlayerIds.Contains(victim.PlayerId);
            var capturedBeforeAnimation = preEventWitnessIds?.Contains(observer.PlayerId) == true;
            if (!capturedBeforeAnimation &&
                !BotPerceptionPolicy.CanWitnessMurder(
                    recentlySawPair,
                    cachedVisibilityAge,
                    killerDistance,
                    victimDistance,
                    visionDistance,
                    killerBlocked,
                    victimBlocked))
            {
                rejectedByGeometry++;
                continue;
            }

            state.WitnessedKillers[killer.PlayerId] = Time.time;
            witnessIds.Add(observer.PlayerId);
            Append(
                state,
                "witness_kill",
                $"personally saw {killer.Data?.PlayerName}({killer.PlayerId}) kill {victim.Data?.PlayerName}({victim.PlayerId}); this proves a kill-capable role but not automatically the base Impostor role",
                SkeldPathGraph.Instance.NearestNode(killerPosition));
            _log.LogInfo(
                $"DeepBot witnessed murder recorded: observer={observer.Data.PlayerName}({observer.PlayerId}), " +
                $"killer={killer.Data?.PlayerName}({killer.PlayerId}), victim={victim.Data?.PlayerName}({victim.PlayerId}), " +
                $"killerDistance={killerDistance:0.00}, victimDistance={victimDistance:0.00}, " +
                $"vision={visionDistance:0.00}, preAnimation={capturedBeforeAnimation}, recentPair={recentlySawPair}, cacheAge={cachedVisibilityAge:0.00}, " +
                $"killerBlocked={killerBlocked}, victimBlocked={victimBlocked}.");
        }

        _log.LogInfo(
            $"DeepBot murder perception scan: killer={killer.Data?.PlayerName}({killer.PlayerId}), " +
            $"victim={victim.Data?.PlayerName}({victim.PlayerId}), observers={scannedObservers}, " +
            $"witnesses={witnessIds.Count}, geometryRejected={rejectedByGeometry}.");

        return witnessIds;
    }

    public void RecordObservedSpecialAction(PlayerControl actor, string action, string inference)
    {
        if (!_matchActive || !actor || actor.Data is null)
        {
            return;
        }

        var key = (actor.PlayerId, action);
        if (_recentObservedSpecialActions.TryGetValue(key, out var lastObservedAt) &&
            Time.time - lastObservedAt < 0.30f)
        {
            return;
        }
        _recentObservedSpecialActions[key] = Time.time;

        foreach (var observer in EnumerateDeepBots())
        {
            if (!CanPersonallyObserveActor(observer, actor))
            {
                continue;
            }

            var state = GetState(observer, Plugin.Settings.MaxMemoryEvents.Value);
            RecordPersonalRoleEvidence(state, actor.PlayerId, action, inference);
            Append(
                state,
                "witness_action",
                $"personally saw {actor.Data.PlayerName}({actor.PlayerId}) {action}; inference={inference}",
                SkeldPathGraph.Instance.NearestNode(actor.GetTruePosition()));
            _log.LogInfo(
                $"DeepBot witnessed special action recorded: observer={observer.Data?.PlayerName}({observer.PlayerId}), " +
                $"actor={actor.Data.PlayerName}({actor.PlayerId}), action={action}.");
        }
    }

    public string BuildTimeline(byte botId, int maxEvents)
    {
        var skills = _evolutionSkills.BuildPrompt();
        var timeline = BuildRawTimeline(botId, maxEvents);
        return $"CORE EVOLUTION SKILLS (generalized lessons from earlier matches):\n{skills}\n" +
               $"CURRENT PRIVATE MATCH MEMORY:\n{timeline}";
    }

    public bool TryGetLatestWitnessedKiller(byte botId, out byte killerId)
    {
        killerId = byte.MaxValue;
        if (!_states.TryGetValue(botId, out var state) || state.WitnessedKillers.Count == 0)
        {
            return false;
        }

        var latest = state.WitnessedKillers
            .OrderByDescending(pair => pair.Value)
            .First();
        killerId = latest.Key;
        return true;
    }

    public PersonalContactEvidence GetPersonalContactEvidence(byte observerId, byte targetId)
    {
        if (!_states.TryGetValue(observerId, out var state) ||
            !state.VisualContacts.TryGetValue(targetId, out var contact) ||
            contact.LastSeenAt <= 0f)
        {
            return default;
        }

        var continuousSeconds = contact.Visible
            ? Mathf.Max(contact.LastContinuousSeconds, Time.time - contact.VisibleSince)
            : contact.LastContinuousSeconds;
        return new PersonalContactEvidence(
            continuousSeconds,
            contact.TotalVisibleSeconds,
            Mathf.Max(0f, Time.time - contact.LastSeenAt),
            contact.Visible,
            contact.LastNodeId);
    }

    public PersonalRoleEvidence GetPersonalRoleEvidence(byte observerId, byte targetId)
    {
        if (!_states.TryGetValue(observerId, out var state) ||
            !state.ObservedRoleEvidence.TryGetValue(targetId, out var evidence))
        {
            return default;
        }

        var age = Mathf.Max(0f, Time.time - evidence.LastObservedAt);
        // TOR can transfer or replace roles during a match. Keep the factual
        // observation, but slowly reduce its current-role voting weight rather
        // than pretending an old ability proves the actor's role forever.
        var ageMultiplier = Mathf.Lerp(1f, 0.60f, Mathf.Clamp01(age / 180f));
        return new PersonalRoleEvidence(
            evidence.HostilityScore * ageMultiplier,
            evidence.InferenceConfidence * ageMultiplier,
            evidence.ObservationCount,
            age,
            evidence.Summary,
            evidence.InferredRoleName);
    }

    public bool TryGetRecentPersonallySeenLivingPlayer(
        byte observerId,
        float maximumAgeSeconds,
        out byte playerId)
    {
        playerId = byte.MaxValue;
        if (!_states.TryGetValue(observerId, out var state))
        {
            return false;
        }

        foreach (var candidate in state.VisualContacts
                     .Where(pair => Time.time - pair.Value.LastSeenAt <= maximumAgeSeconds)
                     .OrderByDescending(pair => pair.Value.LastSeenAt))
        {
            var player = PlayerControl.AllPlayerControls
                .ToArray()
                .FirstOrDefault(item => item && item.PlayerId == candidate.Key);
            if (!player || player!.Data is null || player.Data.IsDead || player.Data.Disconnected)
            {
                continue;
            }

            playerId = candidate.Key;
            return true;
        }

        return false;
    }

    internal string BuildRawTimeline(byte botId, int maxEvents)
    {
        if (!_states.TryGetValue(botId, out var state) || state.Events.Count == 0)
        {
            return "no verified personal memory";
        }

        var take = Math.Clamp(maxEvents, 8, 96);
        var start = Math.Max(0, state.Events.Count - take);
        var builder = new StringBuilder(Math.Min(6000, take * 96));
        for (var i = start; i < state.Events.Count; i++)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(state.Events[i]);
        }

        return builder.ToString();
    }

    public string BuildIdentity(PlayerControl bot)
    {
        var hasTorRole = TorRoleAdapter.TryGetRole(bot, out var torRole);
        var roleName = hasTorRole
            ? $"TOR:{torRole.Name} (native={bot.Data?.Role?.NiceName})"
            : IsImpostor(bot) ? "Impostor" : "Crewmate";
        var team = hasTorRole ? torRole.Alignment : IsImpostor(bot) ? "impostor" : "crewmate";
        var roomRules = GameRuleSettings.CaptureSnapshot();
        var objective = hasTorRole
            ? torRole.WinCondition
            : IsImpostor(bot)
                ? "Blend in, protect known impostor teammates, create plausible alibis, sabotage, and eliminate crewmates without exposing the team."
                : "Complete tasks, report personally witnessed evidence accurately, identify impostors, and avoid inventing facts.";
        return $"playerId={bot.PlayerId}; name={bot.Data?.PlayerName}; role={roleName}; team={team}; " +
               $"roomConfiguredImpostors={roomRules.NumImpostors}; objective={objective}" +
               (hasTorRole ? $"; roleStrategy={TorRoleAdapter.BuildStrategicRoleBrief(bot, torRole)}" : string.Empty);
    }

    public void RecordMeetingConclusion(
        PlayerControl bot,
        int meetingSerial,
        byte? votedPlayerId,
        byte? followPlayerId,
        string followIntent,
        float confidence,
        string reason)
    {
        if (!bot || bot.Data is null || !IsDeepBot(bot))
        {
            return;
        }

        var state = GetState(bot, Plugin.Settings.MaxMemoryEvents.Value);
        var normalizedIntent = followIntent is "trust" or "suspect" ? followIntent : "none";
        state.PostMeetingIntent = new PostMeetingSocialIntent(
            meetingSerial,
            votedPlayerId,
            followPlayerId,
            normalizedIntent,
            Mathf.Clamp01(confidence),
            string.IsNullOrWhiteSpace(reason) ? "no private rationale" : reason.Trim());
        Append(
            state,
            "meeting_conclusion",
            $"meeting={meetingSerial}; vote={(votedPlayerId.HasValue ? votedPlayerId.Value.ToString() : "skip")}; " +
            $"follow={(followPlayerId.HasValue ? followPlayerId.Value.ToString() : "none")}; " +
            $"intent={normalizedIntent}; confidence={confidence:0.00}; reason={reason}",
            null);
    }

    public bool TryGetPostMeetingIntent(byte botId, out PostMeetingSocialIntent intent)
    {
        if (_states.TryGetValue(botId, out var state) && state.PostMeetingIntent.HasValue)
        {
            intent = state.PostMeetingIntent.Value;
            return true;
        }

        intent = default;
        return false;
    }

    public string BuildKnownRoleInformation(PlayerControl bot)
    {
        var publicRulebook = TorRoleAdapter.BuildPublicDeductionRulebook();
        if (TorRoleAdapter.TryGetRole(bot, out var torRole))
        {
            return TorRoleAdapter.BuildKnownRoleInformation(bot, torRole) + "\n" + publicRulebook;
        }

        if (!IsImpostor(bot))
        {
            return "You do not know any hidden assignments.\n" + publicRulebook;
        }

        var allies = new List<string>();
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (!player ||
                player.PlayerId == bot.PlayerId ||
                player.Data is null ||
                player.Data.Disconnected ||
                !IsImpostor(player))
            {
                continue;
            }

            allies.Add($"{player.Data.PlayerName}({player.PlayerId})");
        }

        var privateAllies = allies.Count == 0
            ? "No living impostor teammate is known."
            : $"Known impostor teammates: {string.Join(", ", allies)}. Never accuse or vote for them unless unavoidable.";
        return privateAllies + "\n" + publicRulebook;
    }

    private MemoryState GetState(PlayerControl bot, int configuredLimit)
    {
        if (_states.TryGetValue(bot.PlayerId, out var state))
        {
            state.MaxEvents = Math.Clamp(configuredLimit, 24, 160);
            return state;
        }

        state = new MemoryState
        {
            MaxEvents = Math.Clamp(configuredLimit, 24, 160),
            WasAlive = bot.Data is not null && !bot.Data.IsDead
        };
        _states[bot.PlayerId] = state;
        Append(state, "identity", BuildIdentity(bot), SkeldPathGraph.Instance.NearestNode(bot.GetTruePosition()));
        _log.LogInfo($"DeepBot memory initialized: match={_matchSerial}, bot={bot.Data?.PlayerName}({bot.PlayerId}), team={(IsImpostor(bot) ? "impostor" : "crewmate")}.");
        return state;
    }

    private static void SampleLocation(PlayerControl bot, MemoryState state)
    {
        var node = SkeldPathGraph.Instance.NearestNode(bot.GetTruePosition());
        if (string.Equals(state.LastNodeId, node.Id, StringComparison.Ordinal))
        {
            return;
        }

        state.LastNodeId = node.Id;
        Append(state, "location", $"entered {node.Name} ({node.Id})", node);
    }

    private static void SampleEncounters(PlayerControl observer, MemoryState state)
    {
        if (observer.Data is null || observer.Data.IsDead)
        {
            FreezeVisibleContacts(state);
            state.VisiblePlayerIds.Clear();
            return;
        }

        if (MeetingHud.Instance || ExileController.Instance)
        {
            FreezeVisibleContacts(state);
            state.VisiblePlayerIds.Clear();
            return;
        }

        var nowVisible = new HashSet<byte>();
        var observerPosition = observer.GetTruePosition();
        var vision = GetVisionDistance(observer);
        state.LastVisibilitySampleAt = Time.time;
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (!player ||
                player.PlayerId == observer.PlayerId ||
                player.Data is null ||
                player.Data.IsDead ||
                player.Data.Disconnected)
            {
                continue;
            }

            var targetPosition = player.GetTruePosition();
            var distance = Vector2.Distance(observerPosition, targetPosition);
            if (distance > vision ||
                PhysicsHelpers.AnythingBetween(observerPosition, targetPosition, Constants.ShipOnlyMask, false))
            {
                continue;
            }

            var venting = BotPerceptionPolicy.IsConcealedByVent(player);
            var previouslyVenting = state.ObservedVentStates.GetValueOrDefault(player.PlayerId);
            if (venting &&
                !previouslyVenting &&
                state.VisiblePlayerIds.Contains(player.PlayerId))
            {
                const string ventAction = "enter/use a vent";
                const string ventInference = "the current role can vent; Engineer and some neutral/custom roles can also vent";
                RecordPersonalRoleEvidence(state, player.PlayerId, ventAction, ventInference);
                Append(
                    state,
                    "witness_vent",
                    $"personally saw {player.Data.PlayerName}({player.PlayerId}) enter/use a vent; infer only a vent-capable current role because Engineer and some neutral/custom roles can also vent",
                    SkeldPathGraph.Instance.NearestNode(targetPosition));
            }
            state.ObservedVentStates[player.PlayerId] = venting;
            if (venting)
            {
                // The entry animation can be witnessed at the vent mouth only
                // when this observer actually saw the actor immediately before
                // concealment. A player already inside the vent network is not
                // a visible actor.
                // In particular, do not turn vent-to-vent travel into ordinary
                // nearby encounters or route evidence.
                continue;
            }

            nowVisible.Add(player.PlayerId);
            UpdateVisibleContact(state, player.PlayerId, observerPosition);
            var firstSeen = !state.VisiblePlayerIds.Contains(player.PlayerId);
            var refreshDue = Time.time - state.LastEncounterAt.GetValueOrDefault(player.PlayerId) >= EncounterRefreshSeconds;
            if (firstSeen || refreshDue)
            {
                var node = SkeldPathGraph.Instance.NearestNode(observerPosition);
                Append(state, "witness", $"saw {player.Data.PlayerName}({player.PlayerId}) nearby, distance={distance:0.0}", node);
                state.LastEncounterAt[player.PlayerId] = Time.time;
            }
        }

        foreach (var previousId in state.VisiblePlayerIds)
        {
            if (!nowVisible.Contains(previousId))
            {
                EndVisibleContact(state, previousId);
                var node = SkeldPathGraph.Instance.NearestNode(observerPosition);
                Append(state, "witness_end", $"lost sight of playerId={previousId}", node);
            }
        }

        state.VisiblePlayerIds.Clear();
        state.VisiblePlayerIds.UnionWith(nowVisible);
    }

    private static void UpdateVisibleContact(MemoryState state, byte playerId, Vector2 observerPosition)
    {
        if (!state.VisualContacts.TryGetValue(playerId, out var contact))
        {
            contact = new VisualContactState();
            state.VisualContacts[playerId] = contact;
        }

        if (!contact.Visible)
        {
            contact.Visible = true;
            contact.VisibleSince = Time.time;
            contact.LastSampleAt = Time.time;
            contact.LastContinuousSeconds = 0f;
        }
        else if (contact.LastSampleAt > 0f)
        {
            contact.TotalVisibleSeconds += Mathf.Clamp(Time.time - contact.LastSampleAt, 0f, 0.35f);
            contact.LastSampleAt = Time.time;
        }

        contact.LastSeenAt = Time.time;
        contact.LastNodeId = SkeldPathGraph.Instance.NearestNode(observerPosition).Id;
        contact.LastContinuousSeconds = Mathf.Max(
            contact.LastContinuousSeconds,
            Time.time - contact.VisibleSince);
    }

    private static void EndVisibleContact(MemoryState state, byte playerId)
    {
        if (!state.VisualContacts.TryGetValue(playerId, out var contact) || !contact.Visible)
        {
            return;
        }

        contact.LastContinuousSeconds = Mathf.Max(
            contact.LastContinuousSeconds,
            Time.time - contact.VisibleSince);
        contact.Visible = false;
        contact.LastSampleAt = Time.time;
    }

    private static void FreezeVisibleContacts(MemoryState state)
    {
        foreach (var playerId in state.VisiblePlayerIds)
        {
            EndVisibleContact(state, playerId);
        }
    }

    private static void SampleBodies(PlayerControl observer, MemoryState state)
    {
        if (observer.Data is null || observer.Data.IsDead)
        {
            return;
        }

        var observerPosition = observer.GetTruePosition();
        var vision = GetVisionDistance(observer);
        var bodies = UnityEngine.Object.FindObjectsOfType<DeadBody>();
        for (var i = 0; i < bodies.Length; i++)
        {
            var body = bodies[i];
            if (!DeadBodyPerception.IsVisibleAndReportable(body) ||
                state.SeenBodyIds.Contains(body.ParentId))
            {
                continue;
            }

            if (!DeadBodyPerception.CanObserve(
                    observer,
                    body,
                    vision,
                    out var distance,
                    out _))
            {
                continue;
            }

            state.SeenBodyIds.Add(body.ParentId);
            var victim = GameData.Instance ? GameData.Instance.GetPlayerById(body.ParentId) : null;
            var victimName = victim?.PlayerName ?? $"playerId={body.ParentId}";
            Append(
                state,
                "body_seen",
                $"personally saw body of {victimName}, distance={distance:0.0}",
                SkeldPathGraph.Instance.NearestNode(body.TruePosition));
        }
    }

    private static void SampleLifeState(PlayerControl bot, MemoryState state)
    {
        var alive = bot.Data is not null && !bot.Data.IsDead;
        if (alive == state.WasAlive)
        {
            return;
        }

        state.WasAlive = alive;
        Append(
            state,
            alive ? "revived" : "death",
            alive ? "became alive" : "was killed",
            SkeldPathGraph.Instance.NearestNode(bot.GetTruePosition()));
    }

    private static void Append(MemoryState state, string category, string detail, NavNode? node)
    {
        var location = node.HasValue ? $" at {node.Value.Name}({node.Value.Id})" : string.Empty;
        var entry = $"[t={Time.time:0.0}][{category}] {detail}{location}";
        if (state.Events.Count > 0 && string.Equals(state.Events[^1], entry, StringComparison.Ordinal))
        {
            return;
        }

        state.Events.Add(entry);
        while (state.Events.Count > state.MaxEvents)
        {
            state.Events.RemoveAt(0);
        }
    }

    private static bool IsStartedHostMatch()
    {
        var client = AmongUsClient.Instance;
        return client &&
            client.NetworkMode == NetworkModes.LocalGame &&
            client.AmHost &&
            client.ClientId >= 0 &&
            client.ClientId == client.HostId &&
            client.GameState == InnerNetClient.GameStates.Started &&
            ShipStatus.Instance &&
            GameRuleSettings.IsDeepBotSupportedMap();
    }

    private static bool CanPersonallyObserveActor(PlayerControl observer, PlayerControl actor)
    {
        if (!observer || observer.Data is null || observer.Data.IsDead || observer.Data.Disconnected ||
            !actor || actor.Data is null || actor.Data.Disconnected || observer.PlayerId == actor.PlayerId ||
            BotPerceptionPolicy.IsConcealedByVent(actor))
        {
            return false;
        }

        var observerPosition = observer.GetTruePosition();
        var actorPosition = actor.GetTruePosition();
        return Vector2.Distance(observerPosition, actorPosition) <= GetVisionDistance(observer) &&
               !PhysicsHelpers.AnythingBetween(observerPosition, actorPosition, Constants.ShipOnlyMask, false);
    }

    private static void RecordPersonalRoleEvidence(
        MemoryState state,
        byte actorId,
        string action,
        string inference)
    {
        var hostility = ClassifyObservedActionHostility(action);
        var (inferredRoleName, inferenceConfidence) = InferObservedRole(action);
        if (!state.ObservedRoleEvidence.TryGetValue(actorId, out var evidence))
        {
            evidence = new ObservedRoleEvidenceState();
            state.ObservedRoleEvidence[actorId] = evidence;
        }

        evidence.HostilityScore = Mathf.Max(evidence.HostilityScore, hostility);
        if (inferenceConfidence >= evidence.InferenceConfidence)
        {
            evidence.InferenceConfidence = inferenceConfidence;
            evidence.InferredRoleName = inferredRoleName;
        }
        evidence.ObservationCount++;
        evidence.LastObservedAt = Time.time;
        evidence.Summary = $"personally saw action={action}; inference={inference}";
    }

    private static float ClassifyObservedActionHostility(string action)
    {
        var normalized = action?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Contains("bomb placement")) return 0.98f;
        if (normalized.Contains("morph into")) return 0.95f;
        if (normalized.Contains("remove a nearby body")) return 0.88f;
        if (normalized.Contains("invisible")) return 0.68f;
        if (normalized.Contains("yoyo")) return 0.64f;
        if (normalized.Contains("vent")) return 0.28f;
        if (normalized.Contains("portal")) return 0.10f;
        return 0f;
    }

    private static (string? RoleName, float Confidence) InferObservedRole(string action)
    {
        var normalized = action?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Contains("bomb placement")) return ("Bomber", 0.98f);
        if (normalized.Contains("morph into")) return ("Morphling", 0.98f);
        if (normalized.Contains("jack-in-the-box")) return ("Trickster", 0.97f);
        if (normalized.Contains("security camera") || normalized.Contains("seal a vent")) return ("SecurityGuard", 0.97f);
        if (normalized.Contains("place a portal")) return ("Portalmaker", 0.97f);
        if (normalized.Contains("invisible")) return ("Ninja", 0.90f);
        if (normalized.Contains("yoyo")) return ("Yoyo", 0.96f);
        return (null, 0f);
    }

    internal static void LogSelfTest(ManualLogSource log)
    {
        var entryConcealed = BotPerceptionPolicy.IsConcealedByVent(false, true);
        var transitConcealed = BotPerceptionPolicy.IsConcealedByVent(true, false);
        var ordinaryMovementVisible = !BotPerceptionPolicy.IsConcealedByVent(false, false);
        var murderInsideVision = BotPerceptionPolicy.CanWitnessMurderGeometry(4.9f, 5.1f, 5f, false, true);
        var murderOutsideVision = !BotPerceptionPolicy.CanWitnessMurderGeometry(5.5f, 5.5f, 5f, false, false);
        var murderFullyOccluded = !BotPerceptionPolicy.CanWitnessMurderGeometry(2f, 2f, 5f, true, true);
        var murderRecentPairSurvivesAnimation = BotPerceptionPolicy.CanWitnessMurder(
            true, 0.25f, 7f, 7f, 5f, true, true);
        var stalePairRejected = !BotPerceptionPolicy.CanWitnessMurder(
            true, 1.2f, 7f, 7f, 5f, true, true);
        var observedActionSemanticsValid =
            ClassifyObservedActionHostility("perform a bomb placement action") >= 0.95f &&
            ClassifyObservedActionHostility("morph into another appearance") >= 0.90f &&
            ClassifyObservedActionHostility("enter/use a vent") is > 0f and < 0.40f &&
            ClassifyObservedActionHostility("defuse a bomb") == 0f &&
            InferObservedRole("place a Jack-in-the-box") == ("Trickster", 0.97f) &&
            InferObservedRole("enter/use a vent").RoleName is null;
        var level = entryConcealed && transitConcealed && ordinaryMovementVisible &&
                    murderInsideVision && murderOutsideVision && murderFullyOccluded &&
                    murderRecentPairSurvivesAnimation && stalePairRejected && observedActionSemanticsValid
            ? "ok"
            : "error";
        log.LogInfo(
            $"DeepBot vent perception self-test: level={level}, " +
            $"entryConcealed={entryConcealed}, transitConcealed={transitConcealed}, " +
            $"ordinaryMovementVisible={ordinaryMovementVisible}, murderInsideVision={murderInsideVision}, " +
            $"murderOutsideVision={murderOutsideVision}, murderFullyOccluded={murderFullyOccluded}, " +
            $"murderRecentPairSurvivesAnimation={murderRecentPairSurvivesAnimation}, stalePairRejected={stalePairRejected}, " +
            $"observedActionSemantics={observedActionSemanticsValid}.");
    }

    private static bool IsDeepBot(PlayerControl player)
    {
        return DeepBotIdentity.IsBot(player);
    }

    private static bool IsImpostor(PlayerControl player)
    {
        return TorRoleAdapter.IsImpostorTeam(player);
    }

    private static float GetVisionDistance(PlayerControl observer)
    {
        return BotPerceptionPolicy.GetCurrentVisionDistance(observer);
    }

    private static IEnumerable<PlayerControl> EnumerateDeepBots()
    {
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (player && IsDeepBot(player))
            {
                yield return player;
            }
        }
    }

    private sealed class MemoryState
    {
        public int MaxEvents { get; set; }
        public List<string> Events { get; } = [];
        public string? LastNodeId { get; set; }
        public bool WasAlive { get; set; }
        public HashSet<byte> VisiblePlayerIds { get; } = [];
        public Dictionary<byte, VisualContactState> VisualContacts { get; } = [];
        public float LastVisibilitySampleAt { get; set; }
        public Dictionary<byte, float> LastEncounterAt { get; } = [];
        public Dictionary<byte, bool> ObservedVentStates { get; } = [];
        public Dictionary<byte, float> WitnessedKillers { get; } = [];
        public Dictionary<byte, ObservedRoleEvidenceState> ObservedRoleEvidence { get; } = [];
        public HashSet<byte> SeenBodyIds { get; } = [];
        public PostMeetingSocialIntent? PostMeetingIntent { get; set; }
    }

    private sealed class VisualContactState
    {
        public bool Visible { get; set; }
        public float VisibleSince { get; set; }
        public float LastSeenAt { get; set; }
        public float LastSampleAt { get; set; }
        public float LastContinuousSeconds { get; set; }
        public float TotalVisibleSeconds { get; set; }
        public string? LastNodeId { get; set; }
    }

    private sealed class ObservedRoleEvidenceState
    {
        public float HostilityScore { get; set; }
        public float InferenceConfidence { get; set; }
        public int ObservationCount { get; set; }
        public float LastObservedAt { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string? InferredRoleName { get; set; }
    }
}

internal readonly record struct PersonalContactEvidence(
    float ContinuousSeconds,
    float TotalVisibleSeconds,
    float LastSeenSecondsAgo,
    bool CurrentlyVisible,
    string? LastNodeId)
{
    internal bool IsRecent => LastSeenSecondsAgo <= 6f;
}

internal readonly record struct PersonalRoleEvidence(
    float HostilityScore,
    float InferenceConfidence,
    int ObservationCount,
    float LastObservedSecondsAgo,
    string? Summary,
    string? InferredRoleName)
{
    internal bool HasObservation => ObservationCount > 0;
}

internal readonly record struct PostMeetingSocialIntent(
    int MeetingSerial,
    byte? VotedPlayerId,
    byte? FollowPlayerId,
    string FollowIntent,
    float Confidence,
    string Reason);
