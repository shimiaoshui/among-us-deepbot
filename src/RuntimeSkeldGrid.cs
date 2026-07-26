using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace AmongUsDeepSeekBots;

internal sealed class RuntimeSkeldGrid
{
    // Runtime steering probes this method several times per moving bot and per
    // rendered frame. LinecastAll allocates a new managed array for every
    // probe, which creates periodic GC stalls once a lobby contains several
    // bots. Navigation runs on Unity's main thread, so one reusable buffer is
    // sufficient and preserves the same collider filtering semantics.
    private static readonly RaycastHit2D[] SegmentHitBuffer = new RaycastHit2D[64];
    // A finer grid provides more center-line choices in narrow doors
    // and around table corners.  The previous 0.42 spacing repeatedly chose
    // the same wall-adjacent cell when a route was recalculated.
    private const float Step = 0.35f;
    private const float NamedNodeProjectionDistance = 1.65f;
    // A player-sized clearance that still fits through Skeld doorways. Dynamic
    // player/dead-body colliders are filtered below so the grid does not change
    // depending on where the lobby spawned everyone.
    // Match the real crewmate collider more closely.  A cell accepted with the
    // old 0.19 probe could still be physically unreachable by a 0.223 player.
    private const float ProbeRadius = 0.225f;
    // MIRA's room-area triggers stop short of several visual door/corridor
    // seams. A player-sized 0.24 expansion left Launchpad, Reactor/Lab and the
    // Greenhouse/O2 branch in separate flood-fill components. Static wall
    // overlap and segment tests still remain authoritative, so this tolerance
    // only fills clear trigger seams; it does not make walls traversable.
    private const float MiraRoomBoundaryTolerance = 0.70f;
    private const float MiraMinimumReachableCellCoverage = 0.80f;
    private const float MiraMinimumLiveLandmarkCoverage = 0.80f;
    private const float MaximumStartProjectionDistance = 0.9f;
    private const float MaximumTargetProjectionDistance = 1.5f;

    private static readonly (int X, int Y)[] Directions =
    [
        (1, 0), (-1, 0), (0, 1), (0, -1),
        (1, 1), (1, -1), (-1, 1), (-1, -1)
    ];

    private readonly Dictionary<int, Vector2> _positions;
    private readonly Dictionary<int, List<int>> _neighbors;
    private readonly Dictionary<int, float> _temporarilyBlockedCells = [];
    private readonly int _width;
    private readonly string _mapName;
    private int _routeVariant;

    // Build is deliberately conservative on live colliders. A null result can
    // mean either "the map objects are still being created" or "the completed
    // map failed the safety coverage gate"; callers use this flag to retry only
    // the former and avoid rebuilding thousands of physics probes every second.
    public static bool LastBuildWasDeferred { get; private set; }

    private RuntimeSkeldGrid(
        Dictionary<int, Vector2> positions,
        Dictionary<int, List<int>> neighbors,
        int width,
        string mapName)
    {
        _positions = positions;
        _neighbors = neighbors;
        _width = width;
        _mapName = mapName;
    }

    public string Summary => $"runtimeGrid={_mapName}:{_positions.Count}cells,step={Step:0.00},dynamicBlocks={_temporarilyBlockedCells.Count(pair => pair.Value > Time.time)}";

    public bool TryBlockRouteTarget(string fromId, string toId, ManualLogSource log, string reason)
    {
        if (!TryParseGridId(fromId, out var from) ||
            !TryParseGridId(toId, out var to) ||
            !_positions.ContainsKey(from) ||
            !_positions.ContainsKey(to))
        {
            return false;
        }

        // Routes are compressed, so from/to may span several collinear grid
        // cells.  Blocking the attempted destination cell is enough to force
        // A* to choose a different side of the obstacle without invalidating
        // the bot's current start cell.
        var wasBlocked = IsCellTemporarilyBlocked(to);
        _temporarilyBlockedCells[to] = Time.time + 120f;
        if (!wasBlocked)
        {
            log.LogWarning(
                $"DeepBot runtime grid cell temporarily blocked: {fromId}->{toId}, seconds=120, reason={reason}");
        }
        return true;
    }

    internal static bool ContainsSupportedPoint(Vector2 point)
    {
        return SkeldPathGraph.Instance.ContainsSupportedPoint(point);
    }

    public static RuntimeSkeldGrid? Build(ManualLogSource log)
    {
        LastBuildWasDeferred = false;
        if (!ShipStatus.Instance)
        {
            LastBuildWasDeferred = true;
            return null;
        }

        var graph = SkeldPathGraph.Instance;
        var bounds = graph.NavigationBounds;
        var width = Mathf.FloorToInt((bounds.MaxX - bounds.MinX) / Step) + 1;
        var height = Mathf.FloorToInt((bounds.MaxY - bounds.MinY) / Step) + 1;
        var miraRoomAreas = GameRuleSettings.IsMiraHqMap()
            ? CollectLiveRoomAreas()
            : Array.Empty<Collider2D>();
        if (GameRuleSettings.IsMiraHqMap() && miraRoomAreas.Length == 0)
        {
            LastBuildWasDeferred = true;
            log.LogWarning("DeepBot runtime MIRA HQ grid rejected: no live room/ corridor areas were available; refusing rectangular off-map navigation.");
            return null;
        }

        var allPositions = new Dictionary<int, Vector2>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var point = new Vector2(bounds.MinX + x * Step, bounds.MinY + y * Step);
                if ((miraRoomAreas.Length == 0 || IsInsideLiveRoomCoverage(point, miraRoomAreas)) &&
                    !IsBlockedByStaticObstacle(point))
                {
                    allPositions[ToIndex(x, y, width)] = point;
                }
            }
        }

        if (allPositions.Count == 0)
        {
            log.LogWarning($"DeepBot runtime {graph.CurrentMapName} grid build found no candidate cells.");
            return null;
        }

        var allNeighbors = BuildNeighbors(allPositions, width);
        var configuredSeed = graph.FindNode(graph.PrimarySpawnNodeId)?.Position ?? Vector2.zero;
        var seedPosition = ShipStatus.Instance.InitialSpawnCenter;
        if (!bounds.Contains(seedPosition))
        {
            seedPosition = configuredSeed;
        }
        var seed = FindNearest(allPositions, seedPosition);
        var reachable = FloodFill(seed, allNeighbors);
        var positions = allPositions
            .Where(pair => reachable.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var neighbors = reachable.ToDictionary(
            index => index,
            index => allNeighbors.GetValueOrDefault(index)?.Where(reachable.Contains).ToList() ?? []);

        var reachableCoverage = positions.Count / (float)allPositions.Count;
        var namedNodes = graph.Nodes
            .Where(node => node.Kind != NodeKind.Waypoint)
            .ToArray();
        var reachableNamedNodes = namedNodes.Count(
            node => DistanceToNearest(positions, node.Position) <= NamedNodeProjectionDistance);
        var namedNodeCoverage = namedNodes.Length == 0
            ? 0f
            : reachableNamedNodes / (float)namedNodes.Length;
        var liveLandmarkPositions = GameRuleSettings.IsMiraHqMap()
            ? CollectLiveLandmarkPositions()
            : Array.Empty<Vector2>();
        var reachableLiveLandmarks = liveLandmarkPositions.Count(
            position => DistanceToNearest(positions, position) <= NamedNodeProjectionDistance);
        var liveLandmarkCoverage = liveLandmarkPositions.Length == 0
            ? 0f
            : reachableLiveLandmarks / (float)liveLandmarkPositions.Length;
        var authoritativeCoverage = liveLandmarkPositions.Length > 0
            ? liveLandmarkCoverage
            : namedNodeCoverage;
        var miraCoverageInsufficient = GameRuleSettings.IsMiraHqMap() &&
            (reachableCoverage < MiraMinimumReachableCellCoverage ||
             liveLandmarkCoverage < MiraMinimumLiveLandmarkCoverage);
        if (positions.Count < 250 ||
            authoritativeCoverage < graph.MinimumNamedNodeCoverage ||
            miraCoverageInsufficient)
        {
            log.LogWarning(
                $"DeepBot runtime {graph.CurrentMapName} grid rejected: reachableCells={positions.Count}, candidates={allPositions.Count}, " +
                $"cellCoverage={reachableCoverage:P1}, namedCoverage={reachableNamedNodes}/{namedNodes.Length}={namedNodeCoverage:P1}, " +
                $"liveCoverage={reachableLiveLandmarks}/{liveLandmarkPositions.Length}={liveLandmarkCoverage:P1}, " +
                $"roomAreas={miraRoomAreas.Length}, requiredCoverage={graph.MinimumNamedNodeCoverage:P0}, " +
                $"miraCellRequired={MiraMinimumReachableCellCoverage:P0}, miraLiveRequired={MiraMinimumLiveLandmarkCoverage:P0}; " +
                "using collision-filtered static graph.");
            return null;
        }

        var grid = new RuntimeSkeldGrid(positions, neighbors, width, graph.CurrentMapName);
        log.LogInfo(
            $"DeepBot runtime {graph.CurrentMapName} grid ready: {grid.Summary}, candidates={allPositions.Count}, cellCoverage={reachableCoverage:P1}, " +
            $"namedCoverage={reachableNamedNodes}/{namedNodes.Length}={namedNodeCoverage:P1}, " +
            $"liveCoverage={reachableLiveLandmarks}/{liveLandmarkPositions.Length}={liveLandmarkCoverage:P1}, roomAreas={miraRoomAreas.Length}, " +
            $"bounds=({bounds.MinX:0.0},{bounds.MinY:0.0})..({bounds.MaxX:0.0},{bounds.MaxY:0.0}), seed={seedPosition}.");
        return grid;
    }

    private static Collider2D[] CollectLiveRoomAreas()
    {
        if (!ShipStatus.Instance || ShipStatus.Instance.AllRooms is null)
        {
            return Array.Empty<Collider2D>();
        }

        var areas = new List<Collider2D>();
        var rooms = ShipStatus.Instance.AllRooms;
        for (var index = 0; index < rooms.Length; index++)
        {
            var room = rooms[index];
            if (room && room.roomArea)
            {
                areas.Add(room.roomArea);
            }
        }

        return areas.ToArray();
    }

    private static Vector2[] CollectLiveLandmarkPositions()
    {
        if (!ShipStatus.Instance)
        {
            return Array.Empty<Vector2>();
        }

        var positions = new List<Vector2>();
        var rooms = ShipStatus.Instance.AllRooms;
        if (rooms is not null)
        {
            for (var index = 0; index < rooms.Length; index++)
            {
                var room = rooms[index];
                if (room && room.roomArea)
                {
                    positions.Add(room.roomArea.bounds.center);
                }
            }
        }

        var consoles = ShipStatus.Instance.AllConsoles;
        if (consoles is not null)
        {
            for (var index = 0; index < consoles.Length; index++)
            {
                var console = consoles[index];
                if (console)
                {
                    positions.Add(console.transform.position);
                }
            }
        }

        var vents = ShipStatus.Instance.AllVents;
        if (vents is not null)
        {
            for (var index = 0; index < vents.Length; index++)
            {
                var vent = vents[index];
                if (vent)
                {
                    positions.Add(vent.transform.position);
                }
            }
        }

        return positions.ToArray();
    }

    private static bool IsInsideLiveRoomCoverage(Vector2 point, IReadOnlyList<Collider2D> roomAreas)
    {
        for (var index = 0; index < roomAreas.Count; index++)
        {
            var area = roomAreas[index];
            if (!area)
            {
                continue;
            }

            if (area.OverlapPoint(point) ||
                Vector2.Distance(point, area.ClosestPoint(point)) <= MiraRoomBoundaryTolerance)
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<IReadOnlyList<NavNode>> FindTopRoutes(Vector2 from, Vector2 target, int count)
    {
        if (_positions.Count == 0)
        {
            return Array.Empty<IReadOnlyList<NavNode>>();
        }

        var start = FindNearest(_positions, from);
        var goal = FindNearest(_positions, target);
        if (Vector2.Distance(from, _positions[start]) > MaximumStartProjectionDistance ||
            Vector2.Distance(target, _positions[goal]) > MaximumTargetProjectionDistance)
        {
            return Array.Empty<IReadOnlyList<NavNode>>();
        }

        var requested = Math.Max(1, count);
        var routes = new List<IReadOnlyList<NavNode>>();
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        var acceptedCellUseCounts = new Dictionary<int, int>();
        for (var i = 0; i < requested * 8 && routes.Count < requested; i++)
        {
            var variant = _routeVariant++;
            var path = FindPath(start, goal, variant, acceptedCellUseCounts);
            if (path.Count == 0)
            {
                continue;
            }

            var compressed = Compress(path);
            var signature = string.Join(">", compressed);
            if (!signatures.Add(signature))
            {
                continue;
            }

            // Penalize only the interior cells of accepted candidates. Start
            // and goal are necessarily shared, while interior reuse is what
            // makes several nominal "top routes" collapse into one visible
            // corridor. Collision and room coverage remain hard constraints.
            for (var index = 1; index < path.Count - 1; index++)
            {
                acceptedCellUseCounts[path[index]] =
                    acceptedCellUseCounts.GetValueOrDefault(path[index]) + 1;
            }

            routes.Add(compressed
                .Select(index => new NavNode(
                    $"GRID_{index}",
                    "Runtime walkable cell",
                    _positions[index],
                    NodeKind.Waypoint))
                .ToArray());
        }

        return routes;
    }

    public static bool IsNavigationSegmentClear(Vector2 from, Vector2 to, float clearanceRadius)
    {
        var offset = to - from;
        if (offset.sqrMagnitude <= 0.001f)
        {
            return true;
        }

        var normal = new Vector2(-offset.y, offset.x).normalized * Mathf.Max(0f, clearanceRadius);
        return !IsSegmentBlockedByStaticObstacle(from, to) &&
            !IsSegmentBlockedByStaticObstacle(from + normal, to + normal) &&
            !IsSegmentBlockedByStaticObstacle(from - normal, to - normal);
    }

    internal static float RouteDiversityPenaltyForUseCount(int useCount)
    {
        return Mathf.Clamp(Math.Max(0, useCount) * 0.16f, 0f, 0.48f);
    }

    private List<int> FindPath(
        int start,
        int goal,
        int variant,
        IReadOnlyDictionary<int, int>? acceptedCellUseCounts = null)
    {
        var open = new PriorityQueue<int, float>();
        var cameFrom = new Dictionary<int, int>();
        var gScore = new Dictionary<int, float> { [start] = 0f };
        open.Enqueue(start, Vector2.Distance(_positions[start], _positions[goal]));

        while (open.TryDequeue(out var current, out _))
        {
            if (current == goal)
            {
                return Reconstruct(cameFrom, current);
            }

            foreach (var next in _neighbors[current])
            {
                if (next != goal && IsCellTemporarilyBlocked(next))
                {
                    continue;
                }

                var distance = Vector2.Distance(_positions[current], _positions[next]);
                var variation = 1f + 0.18f * Hash01(current, next, variant);
                var reusePenalty = acceptedCellUseCounts is null
                    ? 0f
                    : RouteDiversityPenaltyForUseCount(
                        acceptedCellUseCounts.TryGetValue(next, out var useCount) ? useCount : 0);
                var tentative = gScore[current] + distance * (variation + reusePenalty);
                if (gScore.TryGetValue(next, out var existing) && tentative >= existing)
                {
                    continue;
                }

                cameFrom[next] = current;
                gScore[next] = tentative;
                var priority = tentative + Vector2.Distance(_positions[next], _positions[goal]);
                open.Enqueue(next, priority);
            }
        }

        return [];
    }

    private static Dictionary<int, List<int>> BuildNeighbors(Dictionary<int, Vector2> positions, int width)
    {
        var neighbors = positions.Keys.ToDictionary(index => index, _ => new List<int>());
        foreach (var pair in positions)
        {
            var x = pair.Key % width;
            var y = pair.Key / width;
            foreach (var direction in Directions)
            {
                var otherX = x + direction.X;
                var otherY = y + direction.Y;
                if (otherX < 0 || otherX >= width || otherY < 0)
                {
                    continue;
                }

                var other = ToIndex(otherX, otherY, width);
                if (!positions.TryGetValue(other, out var otherPosition))
                {
                    continue;
                }

                if (direction.X != 0 && direction.Y != 0)
                {
                    var horizontal = ToIndex(x + direction.X, y, width);
                    var vertical = ToIndex(x, y + direction.Y, width);
                    if (!positions.ContainsKey(horizontal) || !positions.ContainsKey(vertical))
                    {
                        continue;
                    }
                }

                if (!IsSegmentBlockedByStaticObstacle(pair.Value, otherPosition))
                {
                    neighbors[pair.Key].Add(other);
                }
            }
        }

        return neighbors;
    }

    private static bool IsBlockedByStaticObstacle(Vector2 point)
    {
        var colliders = Physics2D.OverlapCircleAll(point, ProbeRadius);
        for (var i = 0; i < colliders.Length; i++)
        {
            if (IsStaticNavigationCollider(colliders[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSegmentBlockedByStaticObstacle(Vector2 from, Vector2 to)
    {
        var hitCount = Physics2D.LinecastNonAlloc(from, to, SegmentHitBuffer);
        // A saturated buffer means there may be an uninspected wall behind the
        // returned hits. Fail closed instead of allowing a possible shortcut
        // through geometry.
        if (hitCount >= SegmentHitBuffer.Length)
        {
            return true;
        }

        for (var i = 0; i < hitCount; i++)
        {
            if (IsStaticNavigationCollider(SegmentHitBuffer[i].collider))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStaticNavigationCollider(Collider2D? collider)
    {
        if (collider is null || !collider || collider.isTrigger)
        {
            return false;
        }

        if (collider.GetComponentInParent<PlayerControl>() ||
            collider.GetComponentInParent<DeadBody>())
        {
            return false;
        }

        // Only the two moving door leaves are transient. The old broad
        // DeconSystem/name filter also discarded the chamber's structural side
        // walls, so A* selected wall-adjacent cells and drove every bot into the
        // same frame. Keep real walls authoritative while allowing the native
        // door state machine to open the actual doorway colliders at runtime.
        if (GameRuleSettings.IsMiraHqMap() && IsMiraDeconDoorCollider(collider))
        {
            return false;
        }

        var layer = collider.gameObject.layer;
        var knownNavigationLayer = layer is 9 or 11 or 12;
        if (!knownNavigationLayer && !GameRuleSettings.IsMiraHqMap())
        {
            return false;
        }

        if (!knownNavigationLayer && GameRuleSettings.IsMiraHqMap())
        {
            var playerCollider = PlayerControl.LocalPlayer?.Collider;
            if (playerCollider is null || !playerCollider ||
                Physics2D.GetIgnoreLayerCollision(playerCollider.gameObject.layer, layer))
            {
                return false;
            }
        }

        var name = collider.name.ToLowerInvariant();
        if (name.Contains("shadow", StringComparison.Ordinal) ||
            name.Contains("sensor", StringComparison.Ordinal) ||
            name.Contains("spawn", StringComparison.Ordinal) ||
            name.Contains("vent", StringComparison.Ordinal) ||
            name.Contains("ladder", StringComparison.Ordinal) ||
            name.Contains("button", StringComparison.Ordinal))
        {
            return false;
        }

        if (name.Contains("task", StringComparison.Ordinal) &&
            collider.bounds.size.magnitude < 0.8f)
        {
            return false;
        }

        return true;
    }

    private static bool IsMiraDeconDoorCollider(Collider2D collider)
    {
        var decon = collider.GetComponentInParent<DeconSystem>();
        var door = collider.GetComponentInParent<SomeKindaDoor>();
        if (decon && door &&
            ((decon.UpperDoor && door == decon.UpperDoor) ||
             (decon.LowerDoor && door == decon.LowerDoor)))
        {
            return true;
        }

        var name = collider.name;
        return name.Contains("decon", StringComparison.OrdinalIgnoreCase) &&
               name.Contains("door", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<int> FloodFill(int seed, Dictionary<int, List<int>> neighbors)
    {
        var reachable = new HashSet<int> { seed };
        var queue = new Queue<int>();
        queue.Enqueue(seed);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in neighbors[current])
            {
                if (reachable.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return reachable;
    }

    private static int FindNearest(Dictionary<int, Vector2> positions, Vector2 target)
    {
        var best = positions.Keys.First();
        var bestDistance = float.MaxValue;
        foreach (var pair in positions)
        {
            var distance = (pair.Value - target).sqrMagnitude;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = pair.Key;
            }
        }

        return best;
    }

    private static float DistanceToNearest(Dictionary<int, Vector2> positions, Vector2 target)
    {
        if (positions.Count == 0)
        {
            return float.MaxValue;
        }

        var bestDistanceSquared = float.MaxValue;
        foreach (var position in positions.Values)
        {
            bestDistanceSquared = Mathf.Min(bestDistanceSquared, (position - target).sqrMagnitude);
        }

        return Mathf.Sqrt(bestDistanceSquared);
    }

    private static List<int> Reconstruct(Dictionary<int, int> cameFrom, int current)
    {
        var path = new List<int> { current };
        while (cameFrom.TryGetValue(current, out var previous))
        {
            current = previous;
            path.Add(current);
        }

        path.Reverse();
        return path;
    }

    private List<int> Compress(List<int> path)
    {
        if (path.Count < 3)
        {
            return path;
        }

        var result = new List<int> { path[0] };
        var previousDirection = QuantizedDirection(path[0], path[1]);
        for (var i = 1; i < path.Count - 1; i++)
        {
            var direction = QuantizedDirection(path[i], path[i + 1]);
            if (direction != previousDirection)
            {
                result.Add(path[i]);
                previousDirection = direction;
            }
        }

        result.Add(path[^1]);
        return result;
    }

    private (int X, int Y) QuantizedDirection(int from, int to)
    {
        var fromX = from % _width;
        var fromY = from / _width;
        var toX = to % _width;
        var toY = to / _width;
        return (Math.Sign(toX - fromX), Math.Sign(toY - fromY));
    }

    private static float Hash01(int a, int b, int variant)
    {
        unchecked
        {
            var hash = (uint)(a * 73856093 ^ b * 19349663 ^ variant * 83492791);
            hash ^= hash >> 13;
            hash *= 1274126177;
            return (hash & 1023) / 1023f;
        }
    }

    private static int ToIndex(int x, int y, int width)
    {
        return y * width + x;
    }

    private bool IsCellTemporarilyBlocked(int index)
    {
        if (!_temporarilyBlockedCells.TryGetValue(index, out var until))
        {
            return false;
        }

        if (until > Time.time)
        {
            return true;
        }

        _temporarilyBlockedCells.Remove(index);
        return false;
    }

    private static bool TryParseGridId(string id, out int index)
    {
        const string prefix = "GRID_";
        index = -1;
        return id.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(id.AsSpan(prefix.Length), out index);
    }
}
