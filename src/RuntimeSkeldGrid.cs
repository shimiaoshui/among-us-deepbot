using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace AmongUsDeepSeekBots;

internal sealed class RuntimeSkeldGrid
{
    // Current Skeld reactor consoles are at approximately x=-21.3. The old
    // -17.2 bound cut the whole reactor room out of the runtime graph, so every
    // physical reactor route was rejected even though the console was valid.
    private const float MinX = -23.8f;
    // Current Skeld's Navigation/Shields side reaches roughly x=17, while
    // Communications and its physical sabotage panel reach below y=-17.
    // The previous x=13.2/y=-12.2 limits silently clipped those rooms: normal
    // routes could stop at the old boundary and FixComms had no target cell at
    // all. Keep a modest margin around every walkable room so physical task and
    // sabotage console positions can project onto the runtime grid.
    private const float MaxX = 18.8f;
    private const float MinY = -18.8f;
    private const float MaxY = 7.2f;
    // A finer grid provides more center-line choices in Skeld's narrow doors
    // and around table corners.  The previous 0.42 spacing repeatedly chose
    // the same wall-adjacent cell when a route was recalculated.
    private const float Step = 0.35f;
    private const float MinimumNamedNodeCoverage = 0.82f;
    private const float NamedNodeProjectionDistance = 1.65f;
    // A player-sized clearance that still fits through Skeld doorways. Dynamic
    // player/dead-body colliders are filtered below so the grid does not change
    // depending on where the lobby spawned everyone.
    // Match the real crewmate collider more closely.  A cell accepted with the
    // old 0.19 probe could still be physically unreachable by a 0.223 player.
    private const float ProbeRadius = 0.225f;
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
    private int _routeVariant;

    private RuntimeSkeldGrid(Dictionary<int, Vector2> positions, Dictionary<int, List<int>> neighbors, int width)
    {
        _positions = positions;
        _neighbors = neighbors;
        _width = width;
    }

    public string Summary => $"runtimeGrid={_positions.Count}cells,step={Step:0.00},dynamicBlocks={_temporarilyBlockedCells.Count(pair => pair.Value > Time.time)}";

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
        _temporarilyBlockedCells[to] = Time.time + 18f;
        if (!wasBlocked)
        {
            log.LogWarning(
                $"DeepBot runtime grid cell temporarily blocked: {fromId}->{toId}, seconds=18, reason={reason}");
        }
        return true;
    }

    internal static bool ContainsSupportedPoint(Vector2 point)
    {
        return point.x >= MinX &&
               point.x <= MaxX &&
               point.y >= MinY &&
               point.y <= MaxY;
    }

    public static RuntimeSkeldGrid? Build(ManualLogSource log)
    {
        if (!ShipStatus.Instance)
        {
            return null;
        }

        var width = Mathf.FloorToInt((MaxX - MinX) / Step) + 1;
        var height = Mathf.FloorToInt((MaxY - MinY) / Step) + 1;
        var allPositions = new Dictionary<int, Vector2>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var point = new Vector2(MinX + x * Step, MinY + y * Step);
                if (!IsBlockedByStaticObstacle(point))
                {
                    allPositions[ToIndex(x, y, width)] = point;
                }
            }
        }

        if (allPositions.Count == 0)
        {
            log.LogWarning("DeepBot runtime Skeld grid build found no candidate cells.");
            return null;
        }

        var allNeighbors = BuildNeighbors(allPositions, width);
        var seed = FindNearest(allPositions, SkeldPathGraph.Instance.FindNode("CAF_SPAWN")?.Position ?? new Vector2(-0.8f, 3.2f));
        var reachable = FloodFill(seed, allNeighbors);
        var positions = allPositions
            .Where(pair => reachable.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var neighbors = reachable.ToDictionary(
            index => index,
            index => allNeighbors.GetValueOrDefault(index)?.Where(reachable.Contains).ToList() ?? []);

        var reachableCoverage = positions.Count / (float)allPositions.Count;
        var namedNodes = SkeldPathGraph.Instance.Nodes
            .Where(node => node.Kind != NodeKind.Waypoint)
            .ToArray();
        var reachableNamedNodes = namedNodes.Count(
            node => DistanceToNearest(positions, node.Position) <= NamedNodeProjectionDistance);
        var namedNodeCoverage = namedNodes.Length == 0
            ? 0f
            : reachableNamedNodes / (float)namedNodes.Length;
        if (positions.Count < 250 || namedNodeCoverage < MinimumNamedNodeCoverage)
        {
            log.LogWarning(
                $"DeepBot runtime Skeld grid rejected: reachableCells={positions.Count}, candidates={allPositions.Count}, " +
                $"cellCoverage={reachableCoverage:P1}, namedCoverage={reachableNamedNodes}/{namedNodes.Length}={namedNodeCoverage:P1}, " +
                $"requiredNamed={MinimumNamedNodeCoverage:P0}; using collision-filtered static graph.");
            return null;
        }

        var grid = new RuntimeSkeldGrid(positions, neighbors, width);
        log.LogInfo(
            $"DeepBot runtime Skeld grid ready: {grid.Summary}, candidates={allPositions.Count}, cellCoverage={reachableCoverage:P1}, " +
            $"namedCoverage={reachableNamedNodes}/{namedNodes.Length}={namedNodeCoverage:P1}, " +
            $"bounds=({MinX:0.0},{MinY:0.0})..({MaxX:0.0},{MaxY:0.0}).");
        return grid;
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
        for (var i = 0; i < requested * 3 && routes.Count < requested; i++)
        {
            var variant = _routeVariant++;
            var path = FindPath(start, goal, variant);
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

    private List<int> FindPath(int start, int goal, int variant)
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
                var variation = 1f + 0.12f * Hash01(current, next, variant);
                var tentative = gScore[current] + distance * variation;
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
        var hits = Physics2D.LinecastAll(from, to);
        for (var i = 0; i < hits.Length; i++)
        {
            if (IsStaticNavigationCollider(hits[i].collider))
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

        var layer = collider.gameObject.layer;
        if (layer is not (9 or 11 or 12))
        {
            return false;
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
