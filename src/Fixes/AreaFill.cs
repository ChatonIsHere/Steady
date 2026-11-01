using System;
using HarmonyLib;
using Pathfinding;

namespace Steady.Fixes;

// After a navmesh change, A* labels every node with the number of its connected area, by a depth-first fill
// over the whole graph (~600,000 nodes on this world, ~75 ms on the main thread). Steady gets the same numbers
// by walking the tiles in order instead: each node is joined to the neighbours already passed (union-find,
// a group's root being its first node), then groups are numbered in order of their first walkable node, which
// is the order the game seeds its fills in. That only matches the game when every connection goes both ways,
// so each connection is noted in a checksum when first met and cancelled when met from the other end; anything
// left over (a one-way connection, a node not in the graph's list) or any other surprise, and the game's own
// fill runs instead. Nothing is written until the checks pass.
internal static class AreaFill
{
    private static readonly System.Reflection.FieldInfo Processor = AccessTools.Field(typeof(AstarPath), "pathProcessor");
    private static readonly System.Reflection.FieldInfo NextIndex = AccessTools.Field(AccessTools.TypeByName("Pathfinding.PathProcessor"), "nextNodeIndex");
    private static readonly object Gate = new();

    private static NavmeshTile[] _tiles = new NavmeshTile[0];
    private static long[] _seen = new long[0];   // by NodeIndex: (generation << 32) | position
    private static int[] _parent = new int[0];
    private static uint[] _area = new uint[0], _old = new uint[0];
    private static bool[] _walkable = new bool[0];
    private static int _generation;

    public static void Apply()
    {
        if (!SteadyConfig.FastAreaFill || Processor == null || NextIndex == null) return;
        var fill = AccessTools.Method(AccessTools.TypeByName("Pathfinding.GraphUpdateProcessor"), "FloodFill", Type.EmptyTypes);
        if (SteadyMod.Patch(fill, "FastAreaFill", typeof(AreaFill), nameof(FloodFill)))
            SteadyMod.Active.Add("fast area fill");
    }

    private static bool FloodFill(AstarPath ___astar, ref uint ___lastUniqueAreaIndex)
    {
        int shared;
        uint last;
        lock (Gate)
        {
            try
            {
                if (!Fill(___astar, out last, out shared)) return true;
            }
            catch (Exception e)
            {
                // Possibly part-written; the game's fill rewrites every node.
                SteadyMod.Log.Warning("FastAreaFill: " + e.Message + "; using the game's fill.");
                return true;
            }
            finally
            {
                Array.Clear(_tiles, 0, _tiles.Length);
            }
        }
        ___lastUniqueAreaIndex = last;
        if (shared > 0)
            UnityEngine.Debug.LogError(shared + " areas had to share IDs. This usually doesn't affect pathfinding in any significant way (you might get 'Searched whole area but could not find target' as a reason for path failure) however some path requests may take longer to calculate (specifically those that fail with the 'Searched whole area' error).The maximum number of areas is " + 131071u + ".");
        return false;
    }

    private static bool Fill(AstarPath astar, out uint last, out int shared)
    {
        last = 0;
        shared = 0;
        var graphs = astar?.graphs;
        if (graphs == null) return false;

        // The tiles GetNodes would visit, in its order.
        int tileCount = 0, total = 0;
        foreach (var g in graphs)
        {
            if (g == null) continue;
            if (g is not NavmeshBase graph) return false;
            var tiles = graph.GetTiles();
            if (tiles == null) continue;
            if (_tiles.Length < tileCount + tiles.Length) Array.Resize(ref _tiles, tileCount + tiles.Length);
            for (var i = 0; i < tiles.Length; i++)
            {
                var t = tiles[i];
                if (t == null || t.x + t.z * graph.tileXCount != i || t.nodes == null) continue;
                _tiles[tileCount++] = t;
                total += t.nodes.Length;
            }
        }
        var indices = (int)NextIndex.GetValue(Processor.GetValue(astar));
        if (_seen.Length < indices) _seen = new long[indices + indices / 4];
        if (_parent.Length < total)
        {
            var size = total + total / 4;
            _parent = new int[size]; _area = new uint[size]; _old = new uint[size]; _walkable = new bool[size];
        }
        var gen = (long)++_generation << 32;
        var list = _tiles; var seen = _seen; var parent = _parent; var old = _old; var walkable = _walkable;
        ulong opened = 0, closed = 0;
        var n = 0;
        for (var ti = 0; ti < tileCount; ti++)
        {
            var nodes = list[ti].nodes;
            for (var j = 0; j < nodes.Length; j++)
            {
                var node = nodes[j];
                if (node == null || node.GetType() != typeof(TriangleMeshNode) || n >= total) return false;
                var index = node.NodeIndex;
                if ((uint)index >= (uint)seen.Length || (seen[index] & ~0xFFFFFFFFL) == gen) return false;
                seen[index] = gen | (uint)n;
                walkable[n] = node.Walkable;
                old[n] = node.Area;
                parent[n] = n;
                var connections = node.connections;
                if (connections != null)
                {
                    for (var k = 0; k < connections.Length; k++)
                    {
                        var other = connections[k].node;
                        if (other == null) return false;
                        var o = other.NodeIndex;
                        if (o == index) continue;
                        long s;
                        if ((uint)o < (uint)seen.Length && ((s = seen[o]) & ~0xFFFFFFFFL) == gen)
                        {
                            closed += Mix(((ulong)(uint)o << 32) | (uint)index);
                            var x = n;
                            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                            var y = (int)(s & 0xFFFFFFFFL);
                            while (parent[y] != y) { parent[y] = parent[parent[y]]; y = parent[y]; }
                            if (x < y) parent[y] = x;
                            else if (y < x) parent[x] = y;
                        }
                        else opened += Mix(((ulong)(uint)index << 32) | (uint)o);
                    }
                }
                n++;
            }
        }
        if (n != total || opened != closed) return false;

        // Number the groups by their first walkable node, as the game's seeds do (sharing the last number past
        // the maximum, as it does).
        var area = _area;
        uint next = 0;
        for (var p = 0; p < n; p++)
        {
            var root = parent[p];
            if (root == p) area[p] = 0;
            else
            {
                while (parent[root] != root) root = parent[root];
                parent[p] = root;
            }
            if (walkable[p] && area[root] == 0)
            {
                next++;
                if (next > GraphNode.MaxAreaIndex)
                {
                    next--;
                    if (shared == 0) shared = 1;
                    shared++;
                }
                area[root] = next;
            }
        }
        for (int ti = 0, q = 0; ti < tileCount; ti++)
        {
            var nodes = list[ti].nodes;
            for (var j = 0; j < nodes.Length; j++, q++)
            {
                var value = area[parent[q]];
                if (value != old[q]) nodes[j].Area = value;
            }
        }
        last = next;
        return true;
    }

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
        z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
        return z ^ (z >> 31);
    }
}
