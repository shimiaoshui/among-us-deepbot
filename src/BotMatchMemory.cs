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
    private const float SampleIntervalSeconds = 0.25f;
    private const float EncounterRefreshSeconds = 8f;

    private readonly ManualLogSource _log;
    private readonly BotEvolutionSkillStore _evolutionSkills;
    private readonly Dictionary<byte, MemoryState> _states = [];
    private float _nextSampleAt;
    private bool _matchActive;
    private int _matchSerial;

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
            _nextSampleAt = 0f;
            return;
        }

        if (!_matchActive)
        {
            _matchActive = true;
            _matchSerial++;
            _states.Clear();
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

    public void RecordObservedMurder(PlayerControl killer, PlayerControl victim)
    {
        if (!_matchActive || !killer || !victim)
        {
            return;
        }

        foreach (var observer in EnumerateDeepBots())
        {
            if (!observer || observer.Data is null || observer.Data.IsDead || observer.Data.Disconnected ||
                observer.PlayerId == killer.PlayerId)
            {
                continue;
            }

            var observerPosition = observer.GetTruePosition();
            var killerPosition = killer.GetTruePosition();
            if (Vector2.Distance(observerPosition, killerPosition) > GetVisionDistance(observer) ||
                PhysicsHelpers.AnythingBetween(observerPosition, killerPosition, Constants.ShipAndObjectsMask, false))
            {
                continue;
            }

            var state = GetState(observer, Plugin.Settings.MaxMemoryEvents.Value);
            state.WitnessedKillers[killer.PlayerId] = Time.time;
            Append(
                state,
                "witness_kill",
                $"personally saw {killer.Data?.PlayerName}({killer.PlayerId}) kill {victim.Data?.PlayerName}({victim.PlayerId}); this proves a kill-capable role but not automatically the base Impostor role",
                SkeldPathGraph.Instance.NearestNode(killerPosition));
        }
    }

    public void RecordObservedSpecialAction(PlayerControl actor, string action, string inference)
    {
        if (!_matchActive || !actor || actor.Data is null)
        {
            return;
        }

        foreach (var observer in EnumerateDeepBots())
        {
            if (!CanPersonallyObserveActor(observer, actor))
            {
                continue;
            }

            var state = GetState(observer, Plugin.Settings.MaxMemoryEvents.Value);
            Append(
                state,
                "witness_action",
                $"personally saw {actor.Data.PlayerName}({actor.PlayerId}) {action}; inference={inference}",
                SkeldPathGraph.Instance.NearestNode(actor.GetTruePosition()));
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
            state.VisiblePlayerIds.Clear();
            return;
        }

        var nowVisible = new HashSet<byte>();
        var observerPosition = observer.GetTruePosition();
        var vision = GetVisionDistance(observer);
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
                PhysicsHelpers.AnythingBetween(observerPosition, targetPosition, Constants.ShipAndObjectsMask, false))
            {
                continue;
            }

            nowVisible.Add(player.PlayerId);
            var venting = player.inVent || player.walkingToVent;
            var previouslyVenting = state.ObservedVentStates.GetValueOrDefault(player.PlayerId);
            if (venting && !previouslyVenting)
            {
                Append(
                    state,
                    "witness_vent",
                    $"personally saw {player.Data.PlayerName}({player.PlayerId}) enter/use a vent; infer only a vent-capable current role because Engineer and some neutral/custom roles can also vent",
                    SkeldPathGraph.Instance.NearestNode(targetPosition));
            }
            state.ObservedVentStates[player.PlayerId] = venting;
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
                var node = SkeldPathGraph.Instance.NearestNode(observerPosition);
                Append(state, "witness_end", $"lost sight of playerId={previousId}", node);
            }
        }

        state.VisiblePlayerIds.Clear();
        state.VisiblePlayerIds.UnionWith(nowVisible);
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
            GameRuleSettings.IsSkeldMap();
    }

    private static bool CanPersonallyObserveActor(PlayerControl observer, PlayerControl actor)
    {
        if (!observer || observer.Data is null || observer.Data.IsDead || observer.Data.Disconnected ||
            !actor || actor.Data is null || actor.Data.Disconnected || observer.PlayerId == actor.PlayerId)
        {
            return false;
        }

        var observerPosition = observer.GetTruePosition();
        var actorPosition = actor.GetTruePosition();
        return Vector2.Distance(observerPosition, actorPosition) <= GetVisionDistance(observer) &&
               !PhysicsHelpers.AnythingBetween(observerPosition, actorPosition, Constants.ShipAndObjectsMask, false);
    }

    private static bool IsDeepBot(PlayerControl player)
    {
        return DeepBotIdentity.IsBot(player);
    }

    private static bool IsImpostor(PlayerControl player)
    {
        return player.Data is not null &&
            player.Data.Role is not null &&
            player.Data.Role.IsImpostor;
    }

    private static float GetVisionDistance(PlayerControl observer)
    {
        return IsImpostor(observer)
            ? GameRuleSettings.GetImpostorVision(1.5f) * 5f
            : GameRuleSettings.GetCrewVision(1f) * 5f;
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
        public Dictionary<byte, float> LastEncounterAt { get; } = [];
        public Dictionary<byte, bool> ObservedVentStates { get; } = [];
        public Dictionary<byte, float> WitnessedKillers { get; } = [];
        public HashSet<byte> SeenBodyIds { get; } = [];
        public PostMeetingSocialIntent? PostMeetingIntent { get; set; }
    }
}

internal readonly record struct PostMeetingSocialIntent(
    int MeetingSerial,
    byte? VotedPlayerId,
    byte? FollowPlayerId,
    string FollowIntent,
    float Confidence,
    string Reason);
