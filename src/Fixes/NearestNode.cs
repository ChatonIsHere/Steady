using System;
using HarmonyLib;
using Pathfinding;
using UnityEngine;

namespace Steady.Fixes;

// Every path a creature asks for starts by finding the navmesh node nearest its start and end, in every graph:
// the overworld and each cave layer that has loaded. For a creature in a cave, the overworld's nearest node is
// tens of metres up through the rock, and the overworld search widens ring by ring of tiles until it has looked
// that far round, ~2 ms each time, on the one pathfinding thread (half of its time with rovers in the caves).
// Steady asks the graphs nearest the point first and skips a graph when none of its tiles within reach comes
// closer than the best node already found, from the tiles' own corners. The answer is the same node: a graph
// it skips couldn't have won, and ties still go to the lower graph number, as in the game.
internal static class NearestNode
{
    private const int MaxTilesChecked = 64;
    private const float Slack = 0.01f;   // world positions are rounded to the millimetre
    private const float Margin = 0.5f;   // in case a tile's triangles reach past its edge

    [ThreadStatic] private static int[] _order;
    [ThreadStatic] private static float[] _near;
    public static long Skipped;

    public static void Apply()
    {
        if (!SteadyConfig.FastNearestNode) return;
        // Low priority: Cave Performance's prefix (which picks a cave's own graph for its creatures) runs first.
        if (SteadyMod.Patch(AccessTools.Method(typeof(AstarPath), nameof(AstarPath.GetNearest), [typeof(Vector3), typeof(NNConstraint), typeof(GraphNode)]),
                "FastNearestNode", typeof(NearestNode), nameof(GetNearest), priority: Priority.Last))
            SteadyMod.Active.Add("nearest node skips graphs out of reach");
    }

    // The game's AstarPath.GetNearest, visiting graphs nearest first and skipping those out of reach.
    private static bool GetNearest(AstarPath __instance, Vector3 position, NNConstraint constraint, ref NNInfo __result)
    {
        var graphs = __instance.graphs;
        if (graphs == null || graphs.Length < 2 || __instance.prioritizeGraphs) return true;
        if (_order == null || _order.Length < graphs.Length)
        {
            _order = new int[graphs.Length + 8];
            _near = new float[graphs.Length + 8];
        }
        var order = _order;
        var near = _near;
        var count = 0;
        for (var i = 0; i < graphs.Length; i++)
        {
            var graph = graphs[i];
            if (graph == null || !constraint.SuitableGraph(i, graph)) continue;
            var d = RoughDistance(graph, position);
            var at = count++;
            while (at > 0 && near[at - 1] > d)
            {
                order[at] = order[at - 1];
                near[at] = near[at - 1];
                at--;
            }
            order[at] = i;
            near[at] = d;
        }

        var full = __instance.fullGetNearestSearch;
        var best = float.PositiveInfinity;
        var bestGraph = -1;
        NNInfoInternal info = default;
        for (var k = 0; k < count; k++)
        {
            var i = order[k];
            var graph = graphs[i];
            if (bestGraph >= 0 && !CouldBeat(graph, position, best, i < bestGraph))
            {
                System.Threading.Interlocked.Increment(ref Skipped);
                continue;
            }
            var found = full ? graph.GetNearestForce(position, constraint) : graph.GetNearest(position, constraint);
            if (found.node == null) continue;
            var magnitude = (found.clampedPosition - position).magnitude;
            if (magnitude < best || (magnitude == best && i < bestGraph))
            {
                best = magnitude;
                info = found;
                bestGraph = i;
            }
        }

        // From here on, the game's own steps.
        if (bestGraph == -1)
        {
            __result = default;
            return false;
        }
        if (info.constrainedNode != null)
        {
            info.node = info.constrainedNode;
            info.clampedPosition = info.constClampedPosition;
        }
        if (!full && info.node != null && !constraint.Suitable(info.node))
        {
            var forced = graphs[bestGraph].GetNearestForce(position, constraint);
            if (forced.node != null) info = forced;
        }
        if (!constraint.Suitable(info.node) || (constraint.constrainDistance && (info.clampedPosition - position).sqrMagnitude > __instance.maxNearestNodeDistanceSqr))
        {
            __result = default;
            return false;
        }
        __result = new NNInfo(info);
        return false;
    }

    // Only orders the search: distance to the graph's bounds, and among graphs the point is inside, the smaller
    // first (a cave layer before the overworld round it), so the first answer is usually the nearest.
    private static float RoughDistance(NavGraph graph, Vector3 position)
    {
        if (graph is not RecastGraph recast || recast.transform == null) return 0f;
        var p = recast.transform.InverseTransform(position);
        float w = recast.tileXCount * recast.TileWorldSizeX, h = recast.forcedBoundsSize.y, d = recast.tileZCount * recast.TileWorldSizeZ;
        var dx = Outside(p.x, 0f, w);
        var dy = Outside(p.y, 0f, h);
        var dz = Outside(p.z, 0f, d);
        var outside = dx * dx + dy * dy + dz * dz;
        return outside > 0f ? outside : -1f / (1f + w * h * d);
    }

    // Whether any node of the graph could be nearer than best (or as near, when the graph would win a tie). A node
    // further than best across the ground can't be, so only the tiles within best of the point can hold one; each
    // tile's own search tree is asked for a node within best, the query the game's search makes of every tile.
    private static bool CouldBeat(NavGraph graph, Vector3 position, float best, bool winsTie)
    {
        if (graph is not NavmeshBase navmesh || navmesh.transform == null) return true;
        var tiles = navmesh.GetTiles();
        if (tiles == null) return false;   // the game finds nothing in it either
        var size = navmesh.TileWorldSizeX;
        if (navmesh.TileWorldSizeZ != size || size <= 0f) return true;
        var p = navmesh.transform.InverseTransform(position);
        var reach = best + Slack + Margin;
        int x0 = Math.Max(0, (int)Math.Floor((p.x - reach) / size)), x1 = Math.Min(navmesh.tileXCount - 1, (int)Math.Floor((p.x + reach) / size));
        int z0 = Math.Max(0, (int)Math.Floor((p.z - reach) / size)), z1 = Math.Min(navmesh.tileZCount - 1, (int)Math.Floor((p.z + reach) / size));
        if (x0 > x1 || z0 > z1) return false;
        if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > MaxTilesChecked) return true;
        var limit = best + Slack;
        NavmeshTile last = null;
        for (var z = z0; z <= z1; z++)
        for (var x = x0; x <= x1; x++)
        {
            var tile = tiles[x + z * navmesh.tileXCount];
            if (tile == null || tile == last || tile.bbTree == null || tile.nodes == null || tile.nodes.Length == 0) continue;
            last = tile;
            var within = limit;
            if (tile.bbTree.QueryClosest(position, null, ref within, new NNInfoInternal(null)).constrainedNode != null) return true;
        }
        return false;
    }

    private static float Outside(float v, float min, float max) => v < min ? min - v : v > max ? v - max : 0f;
}
