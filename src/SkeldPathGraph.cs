using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace AmongUsDeepSeekBots;

internal sealed class SkeldPathGraph
{
    // The legacy type name is kept to avoid a risky all-at-once call-site
    // rewrite. The implementation is now a map-selected graph for The Skeld
    // and MIRA HQ.
    public const float MaxEdgeLength = 1.65f;
    public static readonly SkeldPathGraph Instance = new();

    private const float AgentRadius = 0.18f;
    private const float NodeProbeRadius = 0.035f;

    private Dictionary<string, NavNode> _nodes = new(StringComparer.Ordinal);
    private Dictionary<string, List<NavEdge>> _edges = new(StringComparer.Ordinal);
    private readonly Dictionary<(string From, string To), float> _runtimeBlockedEdges = [];
    private readonly HashSet<string> _runtimeBlockedNodes = [];
    private int _generatedWaypointCount;
    private int _runtimeLandmarkCount;
    private int _activeMapId = -1;
    private MapNavigationProfile _profile = MapNavigationProfile.ForMap(0);
    private bool _runtimeValidated;
    private RuntimeSkeldGrid? _runtimeGrid;
    private float _nextRuntimeValidationRetryAt;

    private SkeldPathGraph()
    {
        ActivateMap(0);
    }

    public IReadOnlyCollection<NavNode> Nodes
    {
        get
        {
            EnsureCurrentMap();
            return _nodes.Values;
        }
    }

    public IReadOnlyList<string> SpawnNodeIds
    {
        get
        {
            EnsureCurrentMap();
            return _profile.SpawnNodeIds;
        }
    }

    public IReadOnlyList<string> FakeTaskNodeIds
    {
        get
        {
            EnsureCurrentMap();
            return _profile.FakeTaskNodeIds;
        }
    }

    public NavigationBounds NavigationBounds
    {
        get
        {
            EnsureCurrentMap();
            return _profile.Bounds;
        }
    }

    public string CurrentMapName
    {
        get
        {
            EnsureCurrentMap();
            return _profile.Name;
        }
    }

    public string PrimarySpawnNodeId
    {
        get
        {
            EnsureCurrentMap();
            return _profile.PrimarySpawnNodeId;
        }
    }

    public float MinimumNamedNodeCoverage
    {
        get
        {
            EnsureCurrentMap();
            return _profile.MinimumNamedNodeCoverage;
        }
    }

    public int EdgeCount
    {
        get
        {
            EnsureCurrentMap();
            return _edges.Values.Sum(list => list.Count) / 2;
        }
    }

    public int RuntimeBlockedEdgeCount => _runtimeBlockedEdges.Count(pair => pair.Value > Time.time) / 2;
    public int RuntimeBlockedNodeCount => _runtimeBlockedNodes.Count;
    public int GeneratedWaypointCount => _generatedWaypointCount;

    public string Summary
    {
        get
        {
            EnsureCurrentMap();
            return $"map={_profile.Name}({_activeMapId}), nodes={_nodes.Count}, runtimeLandmarks={_runtimeLandmarkCount}, " +
                   $"generatedWaypoints={_generatedWaypointCount}, edges={EdgeCount}, maxEdge={MaxObservedEdgeLength():0.00}/{MaxEdgeLength:0.00}, " +
                   $"blockedNodes={RuntimeBlockedNodeCount}, blockedEdges={RuntimeBlockedEdgeCount}, {(_runtimeGrid?.Summary ?? "runtimeGrid=pending")}";
        }
    }

    public bool ContainsSupportedPoint(Vector2 point)
    {
        EnsureCurrentMap();
        return _profile.Bounds.Contains(point);
    }

    public string GetEmergencyNode(TaskTypes type)
    {
        EnsureCurrentMap();
        return _profile.EmergencyNode(type);
    }

    public string GetTaskNode(TaskTypes type)
    {
        EnsureCurrentMap();
        return _profile.TaskNode(type);
    }

    private void EnsureCurrentMap()
    {
        var requestedMapId = GameRuleSettings.GetMapId(_activeMapId < 0 ? 0 : _activeMapId);
        if (!MapNavigationProfile.IsSupported(requestedMapId))
        {
            requestedMapId = 0;
        }

        if (requestedMapId != _activeMapId)
        {
            ActivateMap(requestedMapId);
        }
    }

    private void ActivateMap(int mapId)
    {
        _profile = MapNavigationProfile.ForMap(mapId);
        _activeMapId = _profile.MapId;
        _nodes = BuildNodes(_activeMapId).ToDictionary(node => node.Id, StringComparer.Ordinal);
        _edges = BuildEdges(_nodes, _activeMapId, out _generatedWaypointCount);
        _runtimeBlockedEdges.Clear();
        _runtimeBlockedNodes.Clear();
        _runtimeLandmarkCount = 0;
        _runtimeValidated = false;
        _runtimeGrid = null;
        _nextRuntimeValidationRetryAt = 0f;
    }

    public NavNode NearestNode(Vector2 point)
    {
        EnsureCurrentMap();
        var best = _nodes.Values.First();
        var bestDistance = float.MaxValue;
        foreach (var node in _nodes.Values)
        {
            if (_runtimeBlockedNodes.Contains(node.Id))
            {
                continue;
            }

            var distance = Vector2.Distance(point, node.Position);
            if (distance < bestDistance)
            {
                best = node;
                bestDistance = distance;
            }
        }

        return best;
    }

    public NavNode? FindNode(string? id)
    {
        EnsureCurrentMap();
        return id is not null && _nodes.TryGetValue(id, out var node) ? node : null;
    }

    public bool IsNodeAllowed(string id)
    {
        EnsureCurrentMap();
        return _nodes.ContainsKey(id) && !_runtimeBlockedNodes.Contains(id);
    }

    public void LogStaticSelfTest(ManualLogSource log)
    {
        EnsureCurrentMap();
        var start = _profile.PrimarySpawnNodeId;
        var reachable = CountReachableNodes(start, ignoreRuntimeBlocks: true);
        var namedNodes = _nodes.Values.Count(node => node.Kind != NodeKind.Waypoint);
        var disconnectedNamedNodes = _nodes.Values.Count(node => node.Kind != NodeKind.Waypoint && !CanReach(start, node.Id, ignoreRuntimeBlocks: true));
        var maxObservedEdge = MaxObservedEdgeLength();
        var level = disconnectedNamedNodes == 0 && maxObservedEdge <= MaxEdgeLength + 0.001f ? "ok" : "warning";
        log.LogInfo($"DeepBot path graph static self-test: level={level}, {Summary}, reachable={reachable}/{_nodes.Count}, namedNodes={namedNodes}, disconnectedNamedNodes={disconnectedNamedNodes}");
    }

    public void LogSupportedMapSelfTests(ManualLogSource log)
    {
        foreach (var mapId in new[] { 0, 1 })
        {
            var profile = MapNavigationProfile.ForMap(mapId);
            var nodes = BuildNodes(mapId).ToDictionary(node => node.Id, StringComparer.Ordinal);
            var edges = BuildEdges(nodes, mapId, out var generatedWaypoints);
            var reachable = CountStaticReachable(profile.PrimarySpawnNodeId, edges);
            var namedNodes = nodes.Values.Count(node => node.Kind != NodeKind.Waypoint);
            var maxObservedEdge = edges
                .SelectMany(pair => pair.Value.Select(edge => edge.Cost))
                .DefaultIfEmpty(0f)
                .Max();
            var level = reachable == nodes.Count && maxObservedEdge <= MaxEdgeLength + 0.001f
                ? "ok"
                : "warning";
            log.LogInfo(
                $"DeepBot supported map static self-test: level={level}, map={profile.Name}({mapId}), " +
                $"nodes={nodes.Count}, namedNodes={namedNodes}, generatedWaypoints={generatedWaypoints}, " +
                $"edges={edges.Values.Sum(list => list.Count) / 2}, reachable={reachable}/{nodes.Count}, " +
                $"maxEdge={maxObservedEdge:0.00}/{MaxEdgeLength:0.00}.");
        }
    }

    private static int CountStaticReachable(
        string start,
        IReadOnlyDictionary<string, List<NavEdge>> edges)
    {
        if (!edges.ContainsKey(start))
        {
            return 0;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { start };
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in edges[current])
            {
                if (visited.Add(edge.To))
                {
                    queue.Enqueue(edge.To);
                }
            }
        }

        return visited.Count;
    }

    public IReadOnlyList<IReadOnlyList<NavNode>> FindTopRoutes(Vector2 from, string targetNodeId, int count)
    {
        EnsureCurrentMap();
        if (!_nodes.ContainsKey(targetNodeId) || _runtimeBlockedNodes.Contains(targetNodeId))
        {
            return Array.Empty<IReadOnlyList<NavNode>>();
        }

        if (_runtimeGrid is not null)
        {
            return _runtimeGrid.FindTopRoutes(from, _nodes[targetNodeId].Position, count);
        }

        var start = NearestNode(from).Id;
        if (_runtimeBlockedNodes.Contains(start))
        {
            return Array.Empty<IReadOnlyList<NavNode>>();
        }

        var requested = Math.Max(1, count);
        var candidates = new List<List<NavNode>>();
        var signatures = new HashSet<string>(StringComparer.Ordinal);

        AddCandidate(FindShortestPath(start, targetNodeId, []));
        for (var candidateIndex = 0; candidateIndex < candidates.Count && candidates.Count < requested * 4; candidateIndex++)
        {
            var seed = candidates[candidateIndex];
            for (var edgeIndex = 0; edgeIndex < seed.Count - 1 && candidates.Count < requested * 4; edgeIndex++)
            {
                var banned = new HashSet<(string From, string To)>
                {
                    (seed[edgeIndex].Id, seed[edgeIndex + 1].Id),
                    (seed[edgeIndex + 1].Id, seed[edgeIndex].Id)
                };

                AddCandidate(FindShortestPath(start, targetNodeId, banned));
            }
        }

        return candidates
            .OrderBy(PathCost)
            .ThenBy(path => path.Count)
            .Take(requested)
            .Cast<IReadOnlyList<NavNode>>()
            .ToArray();

        void AddCandidate(List<NavNode> path)
        {
            if (path.Count == 0)
            {
                return;
            }

            var signature = string.Join(">", path.Select(node => node.Id));
            if (signatures.Add(signature))
            {
                candidates.Add(path);
            }
        }
    }

    public IReadOnlyList<IReadOnlyList<NavNode>> FindTopRoutes(Vector2 from, Vector2 target, int count)
    {
        EnsureCurrentMap();
        if (_runtimeGrid is not null)
        {
            return _runtimeGrid.FindTopRoutes(from, target, count);
        }

        return FindTopRoutes(from, NearestNode(target).Id, count);
    }

    public bool TryResolveNavigationDestination(Vector2 from, Vector2 desired, out Vector2 reachable)
    {
        reachable = default;
        var routes = FindTopRoutes(from, desired, 1);
        if (routes.Count == 0 || routes[0].Count == 0)
        {
            return false;
        }

        // Live room centers and some console transforms can be inside scenery
        // colliders (the Skeld Storage fuel/crate corner is one example).  The
        // runtime grid already projects that transform onto a player-sized,
        // reachable cell, so ability placement must use the projected endpoint
        // itself instead of steering back toward the obstructed transform.
        reachable = routes[0][^1].Position;
        return true;
    }

    private List<NavNode> FindShortestPath(string start, string goal, HashSet<(string From, string To)> bannedDirectedEdges)
    {
        var open = new PriorityQueue<string, float>();
        var cameFrom = new Dictionary<string, string>(StringComparer.Ordinal);
        var gScore = _nodes.Keys.ToDictionary(id => id, _ => float.MaxValue, StringComparer.Ordinal);

        gScore[start] = 0f;
        open.Enqueue(start, Heuristic(start, goal));

        while (open.TryDequeue(out var current, out _))
        {
            if (current == goal)
            {
                return Reconstruct(cameFrom, current);
            }

            if (_runtimeBlockedNodes.Contains(current))
            {
                continue;
            }

            foreach (var edge in _edges[current])
            {
                if (bannedDirectedEdges.Contains((current, edge.To)) ||
                    IsRuntimeEdgeBlocked(current, edge.To) ||
                    _runtimeBlockedNodes.Contains(edge.To))
                {
                    continue;
                }

                var tentative = gScore[current] + edge.Cost;
                if (tentative >= gScore[edge.To])
                {
                    continue;
                }

                cameFrom[edge.To] = current;
                gScore[edge.To] = tentative;
                open.Enqueue(edge.To, tentative + Heuristic(edge.To, goal));
            }
        }

        return [];
    }

    public void BlockRuntimeEdge(string from, string to, ManualLogSource log, string reason)
    {
        EnsureCurrentMap();
        if (_runtimeGrid is not null && _runtimeGrid.TryBlockRouteTarget(from, to, log, reason))
        {
            return;
        }

        if (!_nodes.ContainsKey(from) || !_nodes.ContainsKey(to))
        {
            return;
        }

        if (!_edges.TryGetValue(from, out var edges) || edges.All(edge => edge.To != to))
        {
            return;
        }

        var wasBlocked = IsRuntimeEdgeBlocked(from, to);
        var blockedUntil = Time.time + 7.5f;
        _runtimeBlockedEdges[(from, to)] = blockedUntil;
        _runtimeBlockedEdges[(to, from)] = blockedUntil;
        if (!wasBlocked)
        {
            log.LogWarning($"DeepBot path edge temporarily blocked: {from}<->{to}, seconds=7.5, reason={reason}");
        }
    }

    private bool IsRuntimeEdgeBlocked(string from, string to)
    {
        if (!_runtimeBlockedEdges.TryGetValue((from, to), out var blockedUntil))
        {
            return false;
        }

        if (blockedUntil > Time.time)
        {
            return true;
        }

        _runtimeBlockedEdges.Remove((from, to));
        _runtimeBlockedEdges.Remove((to, from));
        return false;
    }

    public bool IsPointBlocked(Vector2 point)
    {
        EnsureCurrentMap();
        return ShipStatus.Instance && Physics2D.OverlapCircle(point, NodeProbeRadius, Constants.ShipAndObjectsMask);
    }

    public bool IsSegmentClear(Vector2 from, Vector2 to)
    {
        EnsureCurrentMap();
        return !ShipStatus.Instance || IsEdgeClear(from, to);
    }

    public void ValidateRuntimeEdges(ManualLogSource log)
    {
        EnsureCurrentMap();
        if (_runtimeValidated || !ShipStatus.Instance || Time.time < _nextRuntimeValidationRetryAt)
        {
            return;
        }

        _runtimeGrid = RuntimeSkeldGrid.Build(log);
        if (_runtimeGrid is null)
        {
            if (!RuntimeSkeldGrid.LastBuildWasDeferred)
            {
                // The live map is present and the safety gate made a final
                // decision (for example MIRA's collider coverage is too
                // fragmented). Keep the static collision-filtered graph and
                // never repeat the expensive full-grid scan every tick.
                _runtimeValidated = true;
                log.LogInfo(
                    $"DeepBot runtime graph validation finished without live grid: map={_profile.Name}, " +
                    "using static collision-filtered graph; no further rebuilds scheduled.");
            }
            else
            {
            // ShipStatus can exist one or two frames before AllRooms, room
            // areas, consoles and vents are populated. Do not permanently
            // lock the graph into its pre-runtime state in that window: a
            // failed early build was the reason MIRA bots could all remain
            // still for an entire round.
            _nextRuntimeValidationRetryAt = Time.time + 1.0f;
            log.LogInfo(
                $"DeepBot runtime graph validation deferred: map={_profile.Name}, " +
                "live navigation objects are not ready; retrying in 1.0s.");
            }
            return;
        }

        _runtimeValidated = true;
        if (_runtimeGrid is not null)
        {
            AddLiveMapLandmarks(log);
        }
        var blockedNodes = 0;
        foreach (var node in _nodes.Values)
        {
            if (IsPointBlocked(node.Position))
            {
                blockedNodes++;
                log.LogWarning($"DeepBot path node overlaps ship/object collider, diagnosticOnly=true: {node.Id} ({node.Name}) at {node.Position}");
            }
        }

        var checkedEdges = new HashSet<(string From, string To)>();
        var blocked = 0;
        foreach (var (from, list) in _edges)
        {
            foreach (var edge in list)
            {
                var normalized = string.CompareOrdinal(from, edge.To) < 0 ? (from, edge.To) : (edge.To, from);
                if (!checkedEdges.Add(normalized))
                {
                    continue;
                }

                var a = _nodes[from].Position;
                var b = _nodes[edge.To].Position;
                if (IsEdgeClear(a, b))
                {
                    continue;
                }

                blocked++;
                _runtimeBlockedEdges[(from, edge.To)] = float.PositiveInfinity;
                _runtimeBlockedEdges[(edge.To, from)] = float.PositiveInfinity;
                log.LogWarning($"DeepBot path edge disabled after collision validation: {from}<->{edge.To}");
            }
        }

        log.LogInfo(
            $"DeepBot path graph runtime validation complete: map={_profile.Name}, nodes={_nodes.Count}, runtimeLandmarks={_runtimeLandmarkCount}, generatedWaypoints={_generatedWaypointCount}, " +
            $"edges={EdgeCount}, maxEdge={MaxEdgeLength:0.00}, blockedNodes=0, collisionBlockedEdges={blocked}, " +
            $"diagnosticNodeOverlaps={blockedNodes}, runtimeGrid={(_runtimeGrid is null ? "fallback" : "active")}");
    }

    private void AddLiveMapLandmarks(ManualLogSource log)
    {
        if (!ShipStatus.Instance || _runtimeGrid is null)
        {
            return;
        }

        var added = 0;
        var rooms = ShipStatus.Instance.AllRooms;
        if (rooms is not null)
        {
            for (var i = 0; i < rooms.Length; i++)
            {
                var room = rooms[i];
                if (!room)
                {
                    continue;
                }

                var position = room.roomArea
                    ? (Vector2)room.roomArea.bounds.center
                    : (Vector2)room.transform.position;
                added += AddRuntimeLandmark(
                    $"LIVE_ROOM_{SanitizeId(room.RoomId.ToString())}_{i}",
                    $"{_profile.Name} {room.RoomId} room center",
                    position,
                    NodeKind.Landmark);
            }
        }

        var consoles = ShipStatus.Instance.AllConsoles;
        if (consoles is not null)
        {
            for (var i = 0; i < consoles.Length; i++)
            {
                var console = consoles[i];
                if (!console)
                {
                    continue;
                }

                added += AddRuntimeLandmark(
                    $"LIVE_CONSOLE_{console.ConsoleId}_{i}",
                    $"{_profile.Name} {console.name} console {console.ConsoleId}",
                    console.transform.position,
                    NodeKind.Interaction);
            }
        }

        var vents = ShipStatus.Instance.AllVents;
        if (vents is not null)
        {
            for (var i = 0; i < vents.Length; i++)
            {
                var vent = vents[i];
                if (!vent)
                {
                    continue;
                }

                added += AddRuntimeLandmark(
                    $"LIVE_VENT_{vent.Id}_{i}",
                    $"{_profile.Name} vent {vent.Id}",
                    vent.transform.position,
                    NodeKind.Landmark);
            }
        }

        _runtimeLandmarkCount += added;
        log.LogInfo(
            $"DeepBot live map landmarks added: map={_profile.Name}, added={added}, totalNodes={_nodes.Count}, " +
            $"rooms={rooms?.Length ?? 0}, consoles={consoles?.Length ?? 0}, vents={vents?.Length ?? 0}.");
    }

    private int AddRuntimeLandmark(string id, string name, Vector2 position, NodeKind kind)
    {
        if (!_profile.Bounds.Contains(position) || _nodes.ContainsKey(id))
        {
            return 0;
        }

        _nodes[id] = new NavNode(id, name, position, kind);
        _edges[id] = [];
        return 1;
    }

    private static string SanitizeId(string value)
    {
        var chars = value
            .Select(character => char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_')
            .ToArray();
        return new string(chars);
    }

    private float Heuristic(string a, string b)
    {
        return Vector2.Distance(_nodes[a].Position, _nodes[b].Position);
    }

    private float PathCost(IReadOnlyList<NavNode> path)
    {
        var cost = 0f;
        for (var i = 0; i < path.Count - 1; i++)
        {
            cost += Vector2.Distance(path[i].Position, path[i + 1].Position);
        }

        return cost;
    }

    private float MaxObservedEdgeLength()
    {
        var max = 0f;
        var checkedEdges = new HashSet<(string From, string To)>();
        foreach (var (from, list) in _edges)
        {
            foreach (var edge in list)
            {
                var normalized = string.CompareOrdinal(from, edge.To) < 0 ? (from, edge.To) : (edge.To, from);
                if (!checkedEdges.Add(normalized))
                {
                    continue;
                }

                max = Mathf.Max(max, edge.Cost);
            }
        }

        return max;
    }

    private bool CanReach(string start, string goal, bool ignoreRuntimeBlocks)
    {
        if (!_nodes.ContainsKey(start) || !_nodes.ContainsKey(goal))
        {
            return false;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(start);
        visited.Add(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == goal)
            {
                return true;
            }

            foreach (var edge in _edges[current])
            {
                if (!ignoreRuntimeBlocks && (_runtimeBlockedNodes.Contains(edge.To) || IsRuntimeEdgeBlocked(current, edge.To)))
                {
                    continue;
                }

                if (visited.Add(edge.To))
                {
                    queue.Enqueue(edge.To);
                }
            }
        }

        return false;
    }

    private int CountReachableNodes(string start, bool ignoreRuntimeBlocks)
    {
        if (!_nodes.ContainsKey(start))
        {
            return 0;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(start);
        visited.Add(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in _edges[current])
            {
                if (!ignoreRuntimeBlocks && (_runtimeBlockedNodes.Contains(edge.To) || IsRuntimeEdgeBlocked(current, edge.To)))
                {
                    continue;
                }

                if (visited.Add(edge.To))
                {
                    queue.Enqueue(edge.To);
                }
            }
        }

        return visited.Count;
    }

    private List<NavNode> Reconstruct(Dictionary<string, string> cameFrom, string current)
    {
        var path = new List<NavNode> { _nodes[current] };
        while (cameFrom.TryGetValue(current, out var previous))
        {
            current = previous;
            path.Add(_nodes[current]);
        }

        path.Reverse();
        return path;
    }

    private static bool IsEdgeClear(Vector2 from, Vector2 to)
    {
        return RuntimeSkeldGrid.IsNavigationSegmentClear(from, to, AgentRadius);
    }

    private static Dictionary<string, List<NavEdge>> BuildEdges(
        Dictionary<string, NavNode> nodes,
        int mapId,
        out int generatedWaypointCount)
    {
        var edges = nodes.Keys.ToDictionary(id => id, _ => new List<NavEdge>(), StringComparer.Ordinal);
        var declaredEdges = new HashSet<(string A, string B)>();
        var waypointCount = 0;

        void Add(string a, string b)
        {
            var normalized = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
            if (!declaredEdges.Add(normalized))
            {
                return;
            }

            var distance = Vector2.Distance(nodes[a].Position, nodes[b].Position);
            var segments = Math.Max(1, Mathf.CeilToInt(distance / MaxEdgeLength));
            if (segments == 1)
            {
                AddStrict(a, b);
                return;
            }

            var previous = a;
            for (var i = 1; i < segments; i++)
            {
                var id = $"WP_{a}_{b}_{i}";
                if (nodes.ContainsKey(id))
                {
                    throw new InvalidOperationException($"Duplicate generated map waypoint id: {id}");
                }

                var position = Vector2.Lerp(nodes[a].Position, nodes[b].Position, i / (float)segments);
                nodes.Add(id, new NavNode(id, $"Waypoint {a}->{b} {i}/{segments}", position, NodeKind.Waypoint));
                edges[id] = [];
                AddStrict(previous, id);
                previous = id;
                waypointCount++;
            }

            AddStrict(previous, b);
        }

        void AddStrict(string a, string b)
        {
            var distance = Vector2.Distance(nodes[a].Position, nodes[b].Position);
            if (distance > MaxEdgeLength + 0.001f)
            {
                throw new InvalidOperationException($"Map edge {a}->{b} is too long after splitting: {distance:0.00}");
            }

            if (edges[a].Any(edge => edge.To == b))
            {
                return;
            }

            edges[a].Add(new NavEdge(b, distance));
            edges[b].Add(new NavEdge(a, distance));
        }

        if (mapId == 1)
        {
            AddChain("MIRA_LAUNCHPAD_N", "MIRA_LAUNCHPAD_SPAWN", "MIRA_LAUNCHPAD_S");
            AddChain("MIRA_LAUNCHPAD_SPAWN", "MIRA_LAUNCHPAD_FUEL", "MIRA_LAUNCHPAD_E", "MIRA_WEST_HALL_1", "MIRA_WEST_HALL_2");
            AddChain("MIRA_WEST_HALL_2", "MIRA_MED_ENTRY", "MIRA_MED_CENTER", "MIRA_MED_SCAN", "MIRA_MED_CORNER");
            AddChain("MIRA_WEST_HALL_2", "MIRA_LOCKER_ENTRY", "MIRA_LOCKER_CENTER", "MIRA_LOCKER_WIRES");
            AddChain("MIRA_LOCKER_CENTER", "MIRA_DECON_LOWER", "MIRA_DECON_CENTER", "MIRA_DECON_UPPER");
            AddChain("MIRA_DECON_UPPER", "MIRA_REACTOR_ENTRY", "MIRA_REACTOR_CORE", "MIRA_REACTOR_MANIFOLD", "MIRA_REACTOR_CORNER");
            AddChain("MIRA_DECON_UPPER", "MIRA_LAB_ENTRY", "MIRA_LAB_CENTER", "MIRA_LAB_SORT", "MIRA_LAB_CORNER");
            AddChain("MIRA_LOCKER_CENTER", "MIRA_MAIN_HALL_W", "MIRA_COMMS_ENTRY", "MIRA_COMMS_CENTER", "MIRA_COMMS_PANEL", "MIRA_COMMS_UPLOAD");
            AddChain("MIRA_COMMS_CENTER", "MIRA_STORAGE_ENTRY", "MIRA_STORAGE_CENTER", "MIRA_STORAGE_CORNER");
            AddChain("MIRA_STORAGE_CENTER", "MIRA_CAFE_ENTRY", "MIRA_CAFE_CENTER", "MIRA_CAFE_BUTTON", "MIRA_CAFE_VENDING");
            AddChain("MIRA_CAFE_CENTER", "MIRA_BALCONY_ENTRY", "MIRA_BALCONY_CENTER", "MIRA_BALCONY_ASTEROIDS", "MIRA_BALCONY_CORNER");
            AddChain("MIRA_COMMS_CENTER", "MIRA_Y_BOTTOM", "MIRA_Y_CENTER", "MIRA_Y_TOP");
            AddChain("MIRA_Y_TOP", "MIRA_OFFICE_ENTRY", "MIRA_OFFICE_CENTER", "MIRA_OFFICE_LIGHTS", "MIRA_OFFICE_CORNER");
            AddChain("MIRA_Y_TOP", "MIRA_ADMIN_ENTRY", "MIRA_ADMIN_CENTER", "MIRA_ADMIN_TABLE", "MIRA_ADMIN_CHART", "MIRA_ADMIN_CORNER");
            AddChain("MIRA_Y_TOP", "MIRA_GREENHOUSE_ENTRY", "MIRA_GREENHOUSE_CENTER", "MIRA_GREENHOUSE_O2", "MIRA_GREENHOUSE_CORNER");
            AddChain("MIRA_OFFICE_CENTER", "MIRA_ADMIN_CENTER");
            AddChain("MIRA_ADMIN_CENTER", "MIRA_GREENHOUSE_ENTRY");
        }
        else
        {
            AddChain("CAF_SPAWN", "CAF_TOP", "CAF_TABLE_N", "CAF_TABLE", "CAF_TABLE_S", "CAF_BOTTOM", "STOR_N");
            AddChain("CAF_TOP", "CAF_UL", "CAF_LEFT");
            AddChain("CAF_TOP", "CAF_UR", "CAF_RIGHT");
            AddChain("CAF_TABLE", "CAF_LEFT", "W_HALL_1", "W_HALL_2", "SEC_ENTRY", "SEC_CENTER");
            AddChain("SEC_CENTER", "REACTOR_ENTRY", "REACTOR_TOP", "REACTOR_MID", "REACTOR_BOTTOM", "LOWER_ENGINE_N");
            AddChain("REACTOR_MID", "REACTOR_HAND_L", "REACTOR_TOP");
            AddChain("REACTOR_MID", "REACTOR_HAND_R", "REACTOR_BOTTOM");
            AddChain("SEC_CENTER", "UPPER_ENGINE_S", "UPPER_ENGINE_M", "UPPER_ENGINE_N");
            AddChain("UPPER_ENGINE_M", "UPPER_FUEL");
            AddChain("W_HALL_2", "MED_ENTRY", "MED_SCAN", "MED_CORNER");
            AddChain("MED_SCAN", "MED_SAMPLE");
            AddChain("W_HALL_2", "W_DOWN_1", "W_DOWN_2", "ELEC_ENTRY", "ELEC_CENTER", "ELEC_BACK");
            AddChain("ELEC_CENTER", "ELEC_SWITCH", "ELEC_WIRES", "ELEC_BACK");
            AddChain("W_DOWN_2", "LOWER_ENGINE_E", "LOWER_ENGINE_N", "LOWER_ENGINE_M", "LOWER_ENGINE_S");
            AddChain("LOWER_ENGINE_M", "LOWER_FUEL");
            AddChain("CAF_TABLE", "CAF_RIGHT_IN", "CAF_RIGHT", "E_HALL_1", "E_HALL_2", "WEAP_ENTRY", "WEAP_CENTER", "WEAP_TOP");
            AddChain("WEAP_CENTER", "WEAP_DOWNLOAD");
            AddChain("E_HALL_2", "E_DOWN_1", "NAV_O2_HALL", "NAV_ENTRY", "NAV_CENTER", "NAV_TOP", "NAV_BOTTOM");
            AddChain("NAV_CENTER", "NAV_STEER", "NAV_DOWNLOAD");
            AddChain("NAV_O2_HALL", "O2_ENTRY", "O2_CENTER", "O2_CORNER");
            AddChain("O2_CENTER", "O2_FILTER");
            AddChain("O2_ENTRY", "O2_DOWN", "SHIELD_N", "SHIELD_CENTER", "SHIELD_S");
            AddChain("SHIELD_CENTER", "SHIELD_PRIME", "SHIELD_CORNER");
            AddChain("CAF_BOTTOM", "STOR_N", "STOR_CENTER", "STOR_S", "COMMS_ENTRY", "COMMS_CENTER");
            AddChain("STOR_CENTER", "STOR_GAS", "STOR_TRASH");
            AddChain("STOR_CENTER", "ADMIN_ENTRY", "ADMIN_TABLE", "ADMIN_CARD", "ADMIN_CORNER");
            AddChain("ADMIN_TABLE", "ADMIN_W", "ADMIN_CORNER", "ADMIN_E");
            AddChain("COMMS_CENTER", "COMMS_CORNER", "SHIELD_S", "SHIELD_CENTER");
            AddChain("COMMS_CENTER", "COMMS_UPLOAD");
            AddChain("STOR_CENTER", "STOR_W", "W_DOWN_2");
            AddChain("CAF_LEFT", "MED_ENTRY");
        }

        generatedWaypointCount = waypointCount;
        return edges;

        void AddChain(params string[] ids)
        {
            for (var i = 0; i < ids.Length - 1; i++)
            {
                Add(ids[i], ids[i + 1]);
            }
        }
    }

    private static IEnumerable<NavNode> BuildNodes(int mapId)
    {
        if (mapId == 1)
        {
            foreach (var node in BuildMiraNodes())
            {
                yield return node;
            }
            yield break;
        }

        yield return new("CAF_SPAWN", "Cafeteria spawn", new(-0.8f, 3.2f), NodeKind.Spawn);
        yield return new("CAF_TOP", "Cafeteria top", new(-0.8f, 4.7f), NodeKind.Corner);
        yield return new("CAF_UL", "Cafeteria upper-left corner", new(-3.1f, 3.7f), NodeKind.Corner);
        yield return new("CAF_UR", "Cafeteria upper-right corner", new(2.2f, 3.7f), NodeKind.Corner);
        yield return new("CAF_TABLE_N", "Cafeteria table north", new(-0.8f, 2.8f), NodeKind.Landmark);
        yield return new("CAF_TABLE", "Emergency button table", new(-0.8f, 1.4f), NodeKind.Interaction);
        yield return new("CAF_TABLE_S", "Cafeteria table south", new(-0.8f, 0.0f), NodeKind.Landmark);
        yield return new("CAF_BOTTOM", "Cafeteria lower door", new(-0.8f, -2.7f), NodeKind.Door);
        yield return new("CAF_LEFT", "Cafeteria west door", new(-3.4f, 0.9f), NodeKind.Door);
        yield return new("CAF_RIGHT_IN", "Cafeteria right inner", new(1.3f, 1.1f), NodeKind.Hall);
        yield return new("CAF_RIGHT", "Cafeteria east door", new(3.4f, 0.9f), NodeKind.Door);
        yield return new("W_HALL_1", "West upper hall", new(-5.0f, 0.7f), NodeKind.Hall);
        yield return new("W_HALL_2", "Med/Security junction", new(-6.7f, -0.9f), NodeKind.Hall);
        yield return new("SEC_ENTRY", "Security entry", new(-8.2f, -1.0f), NodeKind.Door);
        yield return new("SEC_CENTER", "Security cameras", new(-10.0f, -1.0f), NodeKind.Interaction);
        yield return new("REACTOR_ENTRY", "Reactor entry", new(-12.0f, -1.0f), NodeKind.Door);
        yield return new("REACTOR_TOP", "Reactor top", new(-13.3f, 0.9f), NodeKind.Interaction);
        yield return new("REACTOR_MID", "Reactor center", new(-14.5f, -1.0f), NodeKind.Emergency);
        yield return new("REACTOR_BOTTOM", "Reactor bottom", new(-13.3f, -3.0f), NodeKind.Interaction);
        yield return new("REACTOR_HAND_L", "Reactor hand left", new(-15.2f, 0.3f), NodeKind.Emergency);
        yield return new("REACTOR_HAND_R", "Reactor hand right", new(-15.2f, -2.3f), NodeKind.Emergency);
        yield return new("UPPER_ENGINE_S", "Upper engine entry", new(-8.8f, 2.0f), NodeKind.Door);
        yield return new("UPPER_ENGINE_M", "Upper engine", new(-11.2f, 2.4f), NodeKind.Interaction);
        yield return new("UPPER_ENGINE_N", "Upper engine corner", new(-13.0f, 3.8f), NodeKind.Corner);
        yield return new("UPPER_FUEL", "Upper engine fuel", new(-12.0f, 1.0f), NodeKind.Interaction);
        yield return new("MED_ENTRY", "MedBay entry", new(-6.2f, 0.5f), NodeKind.Door);
        yield return new("MED_SCAN", "MedBay scan", new(-7.8f, -0.4f), NodeKind.Interaction);
        yield return new("MED_CORNER", "MedBay corner", new(-9.2f, 0.9f), NodeKind.Corner);
        yield return new("MED_SAMPLE", "MedBay sample", new(-6.6f, 1.2f), NodeKind.Interaction);
        yield return new("W_DOWN_1", "West lower hall", new(-5.0f, -3.0f), NodeKind.Hall);
        yield return new("W_DOWN_2", "West deep hall", new(-6.6f, -5.2f), NodeKind.Hall);
        yield return new("ELEC_ENTRY", "Electrical entry", new(-7.9f, -6.4f), NodeKind.Door);
        yield return new("ELEC_CENTER", "Electrical center", new(-8.9f, -8.0f), NodeKind.Interaction);
        yield return new("ELEC_BACK", "Electrical back", new(-10.3f, -8.9f), NodeKind.Corner);
        yield return new("ELEC_SWITCH", "Electrical switch", new(-7.4f, -8.6f), NodeKind.Interaction);
        yield return new("ELEC_WIRES", "Electrical wires", new(-8.6f, -9.4f), NodeKind.Interaction);
        yield return new("LOWER_ENGINE_E", "Lower engine east", new(-8.8f, -5.7f), NodeKind.Door);
        yield return new("LOWER_ENGINE_N", "Lower engine north", new(-10.8f, -5.5f), NodeKind.Interaction);
        yield return new("LOWER_ENGINE_M", "Lower engine center", new(-12.5f, -6.6f), NodeKind.Interaction);
        yield return new("LOWER_ENGINE_S", "Lower engine south", new(-10.5f, -8.5f), NodeKind.Corner);
        yield return new("LOWER_FUEL", "Lower engine fuel", new(-13.2f, -8.0f), NodeKind.Interaction);
        yield return new("E_HALL_1", "East upper hall", new(4.8f, 0.8f), NodeKind.Hall);
        yield return new("E_HALL_2", "Weapons junction", new(6.5f, 0.8f), NodeKind.Hall);
        yield return new("WEAP_ENTRY", "Weapons entry", new(7.5f, 2.2f), NodeKind.Door);
        yield return new("WEAP_CENTER", "Weapons asteroids", new(9.5f, 2.0f), NodeKind.Interaction);
        yield return new("WEAP_TOP", "Weapons top", new(10.8f, 3.6f), NodeKind.Corner);
        yield return new("WEAP_DOWNLOAD", "Weapons download", new(8.5f, 0.7f), NodeKind.Interaction);
        yield return new("E_DOWN_1", "East down hall", new(6.5f, -1.0f), NodeKind.Hall);
        yield return new("NAV_O2_HALL", "O2/Nav hall", new(7.4f, -2.4f), NodeKind.Hall);
        yield return new("NAV_ENTRY", "Navigation entry", new(9.3f, -2.4f), NodeKind.Door);
        yield return new("NAV_CENTER", "Navigation center", new(11.3f, -2.4f), NodeKind.Interaction);
        yield return new("NAV_TOP", "Navigation top", new(12.5f, -1.0f), NodeKind.Corner);
        yield return new("NAV_BOTTOM", "Navigation bottom", new(12.5f, -4.0f), NodeKind.Interaction);
        yield return new("NAV_STEER", "Navigation steering", new(10.2f, -1.0f), NodeKind.Interaction);
        yield return new("NAV_DOWNLOAD", "Navigation download", new(10.2f, -4.0f), NodeKind.Interaction);
        yield return new("O2_ENTRY", "O2 entry", new(6.0f, -3.6f), NodeKind.Door);
        yield return new("O2_CENTER", "O2 panel", new(4.8f, -4.8f), NodeKind.Emergency);
        yield return new("O2_CORNER", "O2 corner", new(3.7f, -3.7f), NodeKind.Corner);
        yield return new("O2_FILTER", "O2 filter", new(3.8f, -5.5f), NodeKind.Interaction);
        yield return new("O2_DOWN", "O2 lower hall", new(6.0f, -5.2f), NodeKind.Hall);
        yield return new("SHIELD_N", "Shields north", new(7.0f, -6.5f), NodeKind.Door);
        yield return new("SHIELD_CENTER", "Shields center", new(8.4f, -7.6f), NodeKind.Interaction);
        yield return new("SHIELD_S", "Shields south", new(8.0f, -9.2f), NodeKind.Corner);
        yield return new("SHIELD_PRIME", "Shields prime", new(9.4f, -8.4f), NodeKind.Interaction);
        yield return new("SHIELD_CORNER", "Shields corner", new(9.8f, -10.0f), NodeKind.Corner);
        yield return new("STOR_N", "Storage north", new(-0.8f, -4.3f), NodeKind.Door);
        yield return new("STOR_CENTER", "Storage center", new(-0.8f, -6.4f), NodeKind.Interaction);
        yield return new("STOR_S", "Storage south", new(-0.2f, -8.7f), NodeKind.Hall);
        yield return new("STOR_W", "Storage west", new(-3.2f, -6.5f), NodeKind.Hall);
        yield return new("STOR_GAS", "Storage gas can", new(-2.2f, -7.7f), NodeKind.Interaction);
        yield return new("STOR_TRASH", "Storage trash", new(-0.8f, -8.8f), NodeKind.Interaction);
        yield return new("ADMIN_ENTRY", "Admin entry", new(2.5f, -6.3f), NodeKind.Door);
        yield return new("ADMIN_TABLE", "Admin table", new(4.0f, -7.6f), NodeKind.Interaction);
        yield return new("ADMIN_CARD", "Admin card swipe", new(5.7f, -8.8f), NodeKind.Interaction);
        yield return new("ADMIN_CORNER", "Admin corner", new(3.0f, -9.2f), NodeKind.Corner);
        yield return new("ADMIN_W", "Admin west", new(2.5f, -8.4f), NodeKind.Corner);
        yield return new("ADMIN_E", "Admin east", new(5.4f, -7.2f), NodeKind.Corner);
        yield return new("COMMS_ENTRY", "Communications entry", new(2.8f, -9.4f), NodeKind.Door);
        yield return new("COMMS_CENTER", "Communications panel", new(4.5f, -10.3f), NodeKind.Interaction);
        yield return new("COMMS_CORNER", "Communications corner", new(5.8f, -11.2f), NodeKind.Corner);
        yield return new("COMMS_UPLOAD", "Communications upload", new(3.5f, -11.2f), NodeKind.Interaction);
    }

    private static IEnumerable<NavNode> BuildMiraNodes()
    {
        // These semantic landmarks are deliberately coarse. Once MIRA HQ is
        // instantiated, RuntimeSkeldGrid projects routes onto live colliders
        // and AddLiveMapLandmarks adds authoritative room/console positions.
        yield return new("MIRA_LAUNCHPAD_SPAWN", "MIRA HQ Launchpad spawn", new(-4.4f, 2.0f), NodeKind.Spawn);
        yield return new("MIRA_LAUNCHPAD_N", "Launchpad north edge", new(-4.4f, 3.5f), NodeKind.Corner);
        yield return new("MIRA_LAUNCHPAD_S", "Launchpad south edge", new(-4.4f, 0.5f), NodeKind.Corner);
        yield return new("MIRA_LAUNCHPAD_FUEL", "Launchpad fuel task", new(-1.6f, 2.8f), NodeKind.Interaction);
        yield return new("MIRA_LAUNCHPAD_E", "Launchpad east exit", new(0.2f, 2.0f), NodeKind.Door);
        yield return new("MIRA_WEST_HALL_1", "Launchpad west corridor", new(3.2f, 2.0f), NodeKind.Hall);
        yield return new("MIRA_WEST_HALL_2", "MedBay and Locker junction", new(6.6f, 2.0f), NodeKind.Hall);

        yield return new("MIRA_MED_ENTRY", "MIRA MedBay entry", new(10.6f, 0.2f), NodeKind.Door);
        yield return new("MIRA_MED_CENTER", "MIRA MedBay center", new(14.2f, -0.8f), NodeKind.Landmark);
        yield return new("MIRA_MED_SCAN", "MIRA MedBay scan", new(15.4f, -1.8f), NodeKind.Interaction);
        yield return new("MIRA_MED_CORNER", "MIRA MedBay corner", new(13.0f, -2.7f), NodeKind.Corner);

        yield return new("MIRA_LOCKER_ENTRY", "Locker Room entry", new(8.0f, 1.6f), NodeKind.Door);
        yield return new("MIRA_LOCKER_CENTER", "Locker Room center", new(9.2f, 1.0f), NodeKind.Landmark);
        yield return new("MIRA_LOCKER_WIRES", "Locker Room wiring", new(10.2f, 0.2f), NodeKind.Interaction);
        yield return new("MIRA_DECON_LOWER", "MIRA decontamination lower door", new(6.1f, 3.8f), NodeKind.Door);
        yield return new("MIRA_DECON_CENTER", "MIRA decontamination chamber", new(6.1f, 6.2f), NodeKind.Hall);
        yield return new("MIRA_DECON_UPPER", "MIRA decontamination upper door", new(6.1f, 8.6f), NodeKind.Door);

        yield return new("MIRA_REACTOR_ENTRY", "MIRA Reactor entry", new(4.3f, 10.2f), NodeKind.Door);
        yield return new("MIRA_REACTOR_CORE", "MIRA Reactor core", new(2.5f, 12.0f), NodeKind.Emergency);
        yield return new("MIRA_REACTOR_MANIFOLD", "MIRA Reactor manifolds", new(0.5f, 11.0f), NodeKind.Interaction);
        yield return new("MIRA_REACTOR_CORNER", "MIRA Reactor corner", new(2.0f, 14.5f), NodeKind.Corner);
        yield return new("MIRA_LAB_ENTRY", "MIRA Laboratory entry", new(7.7f, 10.0f), NodeKind.Door);
        yield return new("MIRA_LAB_CENTER", "MIRA Laboratory center", new(9.5f, 12.0f), NodeKind.Landmark);
        yield return new("MIRA_LAB_SORT", "MIRA Laboratory sort samples", new(11.3f, 11.1f), NodeKind.Interaction);
        yield return new("MIRA_LAB_CORNER", "MIRA Laboratory corner", new(10.5f, 14.0f), NodeKind.Corner);

        yield return new("MIRA_MAIN_HALL_W", "MIRA lower main corridor", new(12.2f, 2.6f), NodeKind.Hall);
        yield return new("MIRA_COMMS_ENTRY", "MIRA Communications entry", new(14.0f, 3.2f), NodeKind.Door);
        yield return new("MIRA_COMMS_CENTER", "MIRA Communications center", new(15.2f, 3.8f), NodeKind.Landmark);
        yield return new("MIRA_COMMS_PANEL", "MIRA Communications panel", new(14.4f, 4.4f), NodeKind.Emergency);
        yield return new("MIRA_COMMS_UPLOAD", "MIRA Communications upload", new(16.2f, 4.4f), NodeKind.Interaction);
        yield return new("MIRA_STORAGE_ENTRY", "MIRA Storage entry", new(18.0f, 4.0f), NodeKind.Door);
        yield return new("MIRA_STORAGE_CENTER", "MIRA Storage center", new(19.4f, 4.0f), NodeKind.Landmark);
        yield return new("MIRA_STORAGE_CORNER", "MIRA Storage corner", new(20.2f, 5.4f), NodeKind.Corner);

        yield return new("MIRA_CAFE_ENTRY", "MIRA Cafeteria entry", new(22.2f, 3.3f), NodeKind.Door);
        yield return new("MIRA_CAFE_CENTER", "MIRA Cafeteria center", new(25.2f, 2.2f), NodeKind.Landmark);
        yield return new("MIRA_CAFE_BUTTON", "MIRA Cafeteria emergency button", new(24.0f, 1.8f), NodeKind.Emergency);
        yield return new("MIRA_CAFE_VENDING", "MIRA Cafeteria vending machine", new(27.0f, 2.3f), NodeKind.Interaction);
        yield return new("MIRA_BALCONY_ENTRY", "MIRA Balcony entry", new(23.0f, 0.2f), NodeKind.Door);
        yield return new("MIRA_BALCONY_CENTER", "MIRA Balcony center", new(23.8f, -1.8f), NodeKind.Landmark);
        yield return new("MIRA_BALCONY_ASTEROIDS", "MIRA Balcony asteroids", new(22.0f, -2.5f), NodeKind.Interaction);
        yield return new("MIRA_BALCONY_CORNER", "MIRA Balcony corner", new(25.8f, -2.5f), NodeKind.Corner);

        yield return new("MIRA_Y_BOTTOM", "MIRA Y corridor bottom", new(17.5f, 7.0f), NodeKind.Hall);
        yield return new("MIRA_Y_CENTER", "MIRA Y junction", new(17.8f, 11.0f), NodeKind.Hall);
        yield return new("MIRA_Y_TOP", "MIRA upper corridor junction", new(17.8f, 15.0f), NodeKind.Hall);
        yield return new("MIRA_OFFICE_ENTRY", "MIRA Office entry", new(15.8f, 17.0f), NodeKind.Door);
        yield return new("MIRA_OFFICE_CENTER", "MIRA Office center", new(14.8f, 19.0f), NodeKind.Landmark);
        yield return new("MIRA_OFFICE_LIGHTS", "MIRA Office lights panel", new(13.5f, 18.5f), NodeKind.Emergency);
        yield return new("MIRA_OFFICE_CORNER", "MIRA Office corner", new(13.0f, 20.3f), NodeKind.Corner);
        yield return new("MIRA_ADMIN_ENTRY", "MIRA Admin entry", new(19.7f, 17.0f), NodeKind.Door);
        yield return new("MIRA_ADMIN_CENTER", "MIRA Admin center", new(21.0f, 19.0f), NodeKind.Landmark);
        yield return new("MIRA_ADMIN_TABLE", "MIRA Admin table", new(21.2f, 19.8f), NodeKind.Interaction);
        yield return new("MIRA_ADMIN_CHART", "MIRA Admin chart course", new(22.6f, 18.2f), NodeKind.Interaction);
        yield return new("MIRA_ADMIN_CORNER", "MIRA Admin corner", new(23.0f, 20.4f), NodeKind.Corner);
        yield return new("MIRA_GREENHOUSE_ENTRY", "MIRA Greenhouse entry", new(17.8f, 20.8f), NodeKind.Door);
        yield return new("MIRA_GREENHOUSE_CENTER", "MIRA Greenhouse center", new(17.8f, 23.3f), NodeKind.Landmark);
        yield return new("MIRA_GREENHOUSE_O2", "MIRA Greenhouse oxygen panel", new(16.2f, 24.2f), NodeKind.Emergency);
        yield return new("MIRA_GREENHOUSE_CORNER", "MIRA Greenhouse corner", new(20.2f, 25.2f), NodeKind.Corner);
    }
}

internal readonly record struct NavNode(string Id, string Name, Vector2 Position, NodeKind Kind);
internal readonly record struct NavEdge(string To, float Cost);

internal enum NodeKind
{
    Spawn,
    Door,
    Hall,
    Landmark,
    Interaction,
    Emergency,
    Corner,
    Waypoint
}
