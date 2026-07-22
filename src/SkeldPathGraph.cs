using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace AmongUsDeepSeekBots;

internal sealed class SkeldPathGraph
{
    public const float MaxEdgeLength = 1.65f;
    public static readonly SkeldPathGraph Instance = new();

    private const float AgentRadius = 0.18f;
    private const float NodeProbeRadius = 0.035f;

    private readonly Dictionary<string, NavNode> _nodes;
    private readonly Dictionary<string, List<NavEdge>> _edges;
    private readonly Dictionary<(string From, string To), float> _runtimeBlockedEdges = [];
    private readonly HashSet<string> _runtimeBlockedNodes = [];
    private readonly int _generatedWaypointCount;
    private bool _runtimeValidated;
    private RuntimeSkeldGrid? _runtimeGrid;

    private SkeldPathGraph()
    {
        _nodes = BuildNodes().ToDictionary(node => node.Id, StringComparer.Ordinal);
        _edges = BuildEdges(_nodes, out _generatedWaypointCount);
    }

    public IReadOnlyCollection<NavNode> Nodes => _nodes.Values;
    public int EdgeCount => _edges.Values.Sum(list => list.Count) / 2;
    public int RuntimeBlockedEdgeCount => _runtimeBlockedEdges.Count(pair => pair.Value > Time.time) / 2;
    public int RuntimeBlockedNodeCount => _runtimeBlockedNodes.Count;
    public int GeneratedWaypointCount => _generatedWaypointCount;

    public string Summary => $"nodes={_nodes.Count}, generatedWaypoints={_generatedWaypointCount}, edges={EdgeCount}, maxEdge={MaxObservedEdgeLength():0.00}/{MaxEdgeLength:0.00}, blockedNodes={RuntimeBlockedNodeCount}, blockedEdges={RuntimeBlockedEdgeCount}, {(_runtimeGrid?.Summary ?? "runtimeGrid=pending")}";

    public NavNode NearestNode(Vector2 point)
    {
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
        return id is not null && _nodes.TryGetValue(id, out var node) ? node : null;
    }

    public bool IsNodeAllowed(string id)
    {
        return _nodes.ContainsKey(id) && !_runtimeBlockedNodes.Contains(id);
    }

    public void LogStaticSelfTest(ManualLogSource log)
    {
        var reachable = CountReachableNodes("CAF_SPAWN", ignoreRuntimeBlocks: true);
        var namedNodes = _nodes.Values.Count(node => node.Kind != NodeKind.Waypoint);
        var disconnectedNamedNodes = _nodes.Values.Count(node => node.Kind != NodeKind.Waypoint && !CanReach("CAF_SPAWN", node.Id, ignoreRuntimeBlocks: true));
        var maxObservedEdge = MaxObservedEdgeLength();
        var level = disconnectedNamedNodes == 0 && maxObservedEdge <= MaxEdgeLength + 0.001f ? "ok" : "warning";
        log.LogInfo($"DeepBot path graph static self-test: level={level}, {Summary}, reachable={reachable}/{_nodes.Count}, namedNodes={namedNodes}, disconnectedNamedNodes={disconnectedNamedNodes}");
    }

    public IReadOnlyList<IReadOnlyList<NavNode>> FindTopRoutes(Vector2 from, string targetNodeId, int count)
    {
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
        if (_runtimeGrid is not null)
        {
            return _runtimeGrid.FindTopRoutes(from, target, count);
        }

        return FindTopRoutes(from, NearestNode(target).Id, count);
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
        return ShipStatus.Instance && Physics2D.OverlapCircle(point, NodeProbeRadius, Constants.ShipAndObjectsMask);
    }

    public bool IsSegmentClear(Vector2 from, Vector2 to)
    {
        return !ShipStatus.Instance || IsEdgeClear(from, to);
    }

    public void ValidateRuntimeEdges(ManualLogSource log)
    {
        if (_runtimeValidated || !ShipStatus.Instance)
        {
            return;
        }

        _runtimeValidated = true;
        _runtimeGrid = RuntimeSkeldGrid.Build(log);
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
            $"DeepBot path graph runtime validation complete: nodes={_nodes.Count}, generatedWaypoints={_generatedWaypointCount}, " +
            $"edges={EdgeCount}, maxEdge={MaxEdgeLength:0.00}, blockedNodes=0, collisionBlockedEdges={blocked}, " +
            $"diagnosticNodeOverlaps={blockedNodes}, runtimeGrid={(_runtimeGrid is null ? "fallback" : "active")}");
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

    private static Dictionary<string, List<NavEdge>> BuildEdges(Dictionary<string, NavNode> nodes, out int generatedWaypointCount)
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
                    throw new InvalidOperationException($"Duplicate generated Skeld waypoint id: {id}");
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
                throw new InvalidOperationException($"Skeld edge {a}->{b} is too long after splitting: {distance:0.00}");
            }

            if (edges[a].Any(edge => edge.To == b))
            {
                return;
            }

            edges[a].Add(new NavEdge(b, distance));
            edges[b].Add(new NavEdge(a, distance));
        }

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

    private static IEnumerable<NavNode> BuildNodes()
    {
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
