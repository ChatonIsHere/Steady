using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Pathfinding;
using Pathfinding.Util;

namespace Steady.Fixes;

// The area fill (see AreaFill) runs after every navmesh cut update: a tree falls, a rock or a placed item moves,
// and a few tiles of the map are re-cut and swapped in. Then every node of every graph is labelled again, ~70-80
// ms on the main thread, about twice a second with players about. Most of the labels can't have changed. When the
// only changes since the last fill are those tile swaps, Steady labels just the swapped tiles' nodes, from the
// nodes round them, once it has shown no area was joined to another or cut in two. The forest's graph updates (when
// a chunk loads) count too: they swap in rebuilt tiles after their own fill, off the main thread. Steady checks:
//   - each connected group of new nodes touches at most one area outside, and takes that label (or a new one);
//   - each area still hangs together: the groups that touch it, and its outside nodes that touched the old tiles,
//     all reach each other through the new nodes or the area's own nodes round about (a bounded walk), so every
//     path the area had through the old tiles still has a way round.
// Labels elsewhere stay as they are. Nodes are in the same area exactly when they would be after the full fill,
// though the numbers can differ (every user only compares them). Anything else (another kind of navmesh change,
// one-way links, an unwalkable node, too big a change) gets the full fill.
internal static class LocalAreaFill
{
    private const int MaxNodes = 20000;

    private static readonly Func<AstarPath, bool> InsideWorkItem =
        AccessTools.MethodDelegate<Func<AstarPath, bool>>(AccessTools.PropertyGetter(typeof(AstarPath), "IsInsideWorkItem"));
    private static readonly FieldInfo Modifiers = AccessTools.Field(typeof(GraphModifier), "root");
    private static readonly FieldInfo WorkItems = AccessTools.Field(typeof(AstarPath), "workItems");
    private static readonly FieldInfo Queue = AccessTools.Field(AccessTools.TypeByName("Pathfinding.WorkItemProcessor"), "workItems");
    private static readonly AccessTools.FieldRef<NavmeshBase, NavmeshTile[]> Tiles = AccessTools.FieldRefAccess<NavmeshBase, NavmeshTile[]>("tiles");

    private struct Slot
    {
        public NavmeshBase Graph;
        public int X, Z, W, D;
    }

    private static readonly object Gate = new();
    // Labels match the navmesh except for the swaps recorded below.
    private static bool _trusted;
    // A graph update's own full fill has run (off the main thread, before its rebuilt tiles are swapped in).
    private static bool _updateFilled;
    private static readonly List<Slot> Slots = new();
    private static readonly HashSet<GraphNode> OldEdge = new();   // outside nodes that touched swapped-out tiles
    [ThreadStatic] private static bool _repaired;

    // Scratch, main thread only.
    private static readonly HashSet<NavmeshTile> Region = new();
    private static readonly Dictionary<GraphNode, int> Group = new();
    private static readonly HashSet<GraphNode> OldNodes = new();
    private static readonly HashSet<GraphNode> Touching = new();
    private static readonly List<uint> Label = new();
    private static readonly Stack<GraphNode> Pending = new();
    private static readonly List<List<GraphNode>> GroupTouch = new();
    private static readonly HashSet<GraphNode> Loose = new();
    private static readonly Dictionary<uint, int> AreaCount = new();
    private static readonly HashSet<GraphNode> Seen = new();
    private static readonly Queue<GraphNode> Walk = new();
    private const int MaxWalk = 20000;

    public static long Repaired, Full;
    public static bool On { get; private set; }

    public static void Apply()
    {
        if (!SteadyConfig.LocalAreaFill) return;
        if (Others.Forest("connectivity_updates"))
        {
            Others.LeaveToForest("local area fill");
            return;
        }
        if (InsideWorkItem == null || Modifiers == null || WorkItems == null || Queue == null)
        {
            SteadyMod.Log.Warning("LocalAreaFill: the pathfinding library isn't the one expected, skipped.");
            return;
        }
        var replace = AccessTools.Method(typeof(NavmeshBase), nameof(NavmeshBase.ReplaceTile),
            [typeof(int), typeof(int), typeof(int), typeof(int), typeof(Int3[]), typeof(int[])]);
        var add = AccessTools.Method(AccessTools.TypeByName("Pathfinding.WorkItemProcessor"), "AddWorkItem");
        var scan = AccessTools.Method(typeof(AstarPath), nameof(AstarPath.ScanAsync), [typeof(NavGraph[])]);
        var post = AccessTools.Method(typeof(RecastGraph), "Pathfinding.IUpdatableGraph.UpdateAreaPost");
        var postUpdates = AccessTools.Method(AccessTools.TypeByName("Pathfinding.GraphUpdateProcessor"), "ProcessPostUpdates");
        On = SteadyMod.Patch(replace, "LocalAreaFill", typeof(LocalAreaFill), nameof(BeforeSwap), nameof(AfterSwap), nameof(SwapFailed))
             && SteadyMod.Patch(add, "LocalAreaFill", typeof(LocalAreaFill), nameof(WorkAdded))
             && SteadyMod.Patch(scan, "LocalAreaFill", typeof(LocalAreaFill), nameof(Distrust))
             && SteadyMod.Patch(post, "LocalAreaFill", typeof(LocalAreaFill), nameof(BeforeRebuilt), nameof(AfterRebuilt), nameof(SwapFailed))
             && SteadyMod.Patch(postUpdates, "LocalAreaFill", typeof(LocalAreaFill), nameof(PostUpdates));
        if (On) SteadyMod.Active.Add("local area fill after navmesh cuts");
    }

    private static bool Main => System.Threading.Thread.CurrentThread.ManagedThreadId == SteadyMod.MainThread;

    private static void Distrust()
    {
        lock (Gate)
        {
            _trusted = false;
            _updateFilled = false;
            Slots.Clear();
            OldEdge.Clear();
        }
    }

    // Navmesh cuts queue their tile swaps (and the batching round them) as work items of their own. Anything else
    // might change the navmesh some other way, so when other work starts, the labels can't be trusted until the
    // next full fill. (It's when work runs that matters, not when it's queued: graph updates queue up while the
    // forest loads.)
    private static void WorkAdded(ref AstarWorkItem itm)
    {
        if (Cuts(itm)) return;
        var what = Describe(itm);
        if (itm.init != null)
        {
            var init = itm.init;
            itm.init = () => { Started(what); init(); };
        }
        else if (itm.initWithContext != null)
        {
            var init = itm.initWithContext;
            itm.initWithContext = context => { Started(what); init(context); };
        }
        else
        {
            var started = false;
            if (itm.update != null)
            {
                var update = itm.update;
                itm.update = force => { if (!started) { started = true; Started(what); } return update(force); };
            }
            else if (itm.updateWithContext != null)
            {
                var update = itm.updateWithContext;
                itm.updateWithContext = (context, force) => { if (!started) { started = true; Started(what); } return update(context, force); };
            }
        }
    }

    private static void Started(string what)
    {
        Distrust();
        lock (Gate) LastOther = what;
    }

    // What last made the next fill a full one, for `steady status`.
    public static string LastOther { get; private set; } = "none";

    private static string Describe(AstarWorkItem item)
    {
        var d = (Delegate)item.update ?? (Delegate)item.updateWithContext ?? (Delegate)item.init ?? item.initWithContext;
        return d == null ? "empty work item" : $"{d.Method.DeclaringType?.FullName}.{d.Method.Name}";
    }

    private static bool Cuts(AstarWorkItem item) =>
        Cuts(item.init) && Cuts(item.initWithContext) && Cuts(item.update) && Cuts(item.updateWithContext);

    private static bool Cuts(Delegate d)
    {
        if (d == null) return true;
        if (d.GetInvocationList().Length != 1) return false;
        var type = d.Method.DeclaringType;
        while (type != null && type != typeof(TileHandler)) type = type.DeclaringType;
        return type == typeof(TileHandler);
    }

    // Before a swap: note the outside nodes the old tiles touch, and every slot the old tiles covered.
    private static void BeforeSwap(NavmeshBase __instance, int x, int z, int w, int d)
    {
        lock (Gate)
        {
            if (!_trusted) return;
            if (!Main || !InsideWorkItem(AstarPath.active))
            {
                Distrust();
                return;
            }
            Capture(__instance, x, z, w, d);
        }
    }

    private static void Capture(NavmeshBase graph, int x, int z, int w, int d)
    {
        var tiles = Tiles(graph);
        OldNodes.Clear();
        Region.Clear();
        for (var j = z; j < z + d; j++)
        for (var i = x; i < x + w; i++)
        {
            var tile = tiles?[i + j * graph.tileXCount];
            if (tile == null || !Region.Add(tile)) continue;
            Slots.Add(new Slot { Graph = graph, X = tile.x, Z = tile.z, W = tile.w, D = tile.d });
            if (tile.nodes != null)
                foreach (var node in tile.nodes) OldNodes.Add(node);
        }
        foreach (var node in OldNodes)
        {
            var connections = (node as MeshNode)?.connections;
            if (connections == null) continue;
            foreach (var c in connections)
                if (c.node != null && !OldNodes.Contains(c.node)) OldEdge.Add(c.node);
        }
        Region.Clear();
        OldNodes.Clear();
        if (OldEdge.Count > MaxNodes || Slots.Count > MaxNodes) Distrust();
    }

    private static void AfterSwap(NavmeshBase __instance, int x, int z, int w, int d)
    {
        lock (Gate)
            if (_trusted) Slots.Add(new Slot { Graph = __instance, X = x, Z = z, W = w, D = d });
    }

    // A graph update (the forest's, when a chunk loads) rebuilds tiles off the main thread, fills the areas there
    // before the rebuilt tiles go in, then swaps them in on the main thread. Its fill vouches for the navmesh as it
    // is up to the swap, as long as nothing but cut swaps is queued after it; the swap itself is then recorded like
    // a cut's.
    private static void PostUpdates()
    {
        lock (Gate)
        {
            if (!_updateFilled || !Main) return;
            _updateFilled = false;
            Slots.Clear();
            OldEdge.Clear();
            _trusted = true;
        }
    }

    private static readonly AccessTools.FieldRef<RecastGraph, List<NavmeshTile>> Staging =
        AccessTools.FieldRefAccess<RecastGraph, List<NavmeshTile>>("stagingTiles");

    private static void BeforeRebuilt(RecastGraph __instance, out List<Slot> __state)
    {
        __state = null;
        lock (Gate)
        {
            if (!_trusted) return;
            if (!Main || !InsideWorkItem(AstarPath.active))
            {
                Distrust();
                return;
            }
            __state = new List<Slot>();
            foreach (var tile in Staging(__instance))
            {
                if (tile.w != 1 || tile.d != 1)
                {
                    Distrust();   // the game swaps in single-slot tiles only
                    __state = null;
                    return;
                }
                Capture(__instance, tile.x, tile.z, 1, 1);
                __state.Add(new Slot { Graph = __instance, X = tile.x, Z = tile.z, W = 1, D = 1 });
            }
        }
    }

    private static void AfterRebuilt(List<Slot> __state)
    {
        if (__state == null) return;
        lock (Gate)
            if (_trusted) Slots.AddRange(__state);
    }

    private static Exception SwapFailed(Exception __exception)
    {
        if (__exception != null) Distrust();
        return __exception;
    }

    // From AreaFill's prefix on the fill. True when the swapped tiles were labelled here, so no full fill is needed.
    public static bool TryRepair(AstarPath astar, ref uint last)
    {
        _repaired = false;
        if (!On || !Main) return false;
        lock (Gate)
        {
            var why = !_trusted ? "other navmesh work" : Slots.Count == 0 ? "no swaps recorded" : astar == null || astar.isScanning ? "scanning"
                : !InsideWorkItem(astar) ? "outside work items" : Modifiers.GetValue(null) != null ? "graph modifiers" : null;
            if (why != null) return Fallback(why);
            try
            {
                if (!Repair(ref last)) return Fallback(_failed);
            }
            finally
            {
                Region.Clear();
                Group.Clear();
                Touching.Clear();
                Label.Clear();
                Pending.Clear();
                GroupTouch.Clear();
                Loose.Clear();
                AreaCount.Clear();
                Seen.Clear();
                Walk.Clear();
            }
            Slots.Clear();
            OldEdge.Clear();
            Repaired++;
            _repaired = true;
            return true;
        }
    }

    // After every fill: a full one leaves every label right.
    public static void FillDone(bool ok)
    {
        if (!On || _repaired)
        {
            _repaired = false;
            return;
        }
        // Only a fill on the main thread, which also queues and runs the work items, can vouch for what comes after
        // it: work already queued that isn't a cut swap still has to run.
        if (!ok)
        {
            Distrust();
            return;
        }
        if (!Main)
        {
            lock (Gate) _updateFilled = true;
            return;
        }
        Full++;
        lock (Gate)
        {
            Slots.Clear();
            OldEdge.Clear();
            _trusted = RunningWorkIsKnown();
        }
    }

    // A full fill vouches for the navmesh from here on, unless it ran in the middle of some other work item that may
    // carry on changing it. A graph update's fill (with no other threads) comes after its last change.
    private static bool RunningWorkIsKnown()
    {
        var astar = AstarPath.active;
        if (astar == null || !InsideWorkItem(astar)) return true;
        var queue = Queue.GetValue(WorkItems.GetValue(astar));
        if (Traverse.Create(queue).Property<int>("Count").Value == 0) return true;   // all done: the end-of-work fill
        var running = (AstarWorkItem)AccessTools.Method(queue.GetType(), "get_Item").Invoke(queue, [0]);
        return Cuts(running) || running.update?.Method == GraphUpdates;
    }

    private static readonly MethodInfo GraphUpdates = AccessTools.Method(AccessTools.TypeByName("Pathfinding.GraphUpdateProcessor"), "ProcessGraphUpdates");

    // Why fills were full, for `steady status`.
    private static readonly Dictionary<string, int> Fallbacks = new();
    private static string _failed;

    public static string FallbackSummary()
    {
        lock (Gate) return Fallbacks.Count == 0 ? "none" : string.Join(", ", Fallbacks.OrderByDescending(f => f.Value).Select(f => $"{f.Key} {f.Value}"));
    }

    private static bool Fallback(string why)
    {
        Fallbacks[why] = Fallbacks.TryGetValue(why, out var n) ? n + 1 : 1;
        return false;
    }

    private static bool Fail(string why)
    {
        _failed = why;
        return false;
    }

    // Whether every group labelled area, and every loose old neighbour in it, can reach each other through the new
    // groups and the area's nodes outside them. Gives up (false) past MaxWalk nodes in all.
    private static bool HangsTogether(uint area, int groups, ref int walked)
    {
        Seen.Clear();
        Walk.Clear();
        var reached = new bool[groups];
        int toReach = 0, first = -1;
        for (var g = 0; g < groups; g++)
            if (Label[g] == area)
            {
                toReach++;
                if (first < 0) first = g;
            }
        var loose = 0;
        foreach (var node in Loose)
            if (node.Area == area) loose++;
        void Enter(int g)
        {
            if (reached[g]) return;
            reached[g] = true;
            toReach--;
            foreach (var t in GroupTouch[g])
                if (Seen.Add(t)) Walk.Enqueue(t);
        }
        if (first >= 0) Enter(first);
        else
            foreach (var node in Loose)
                if (node.Area == area && Seen.Add(node))
                {
                    Walk.Enqueue(node);
                    break;
                }
        while (Walk.Count > 0)
        {
            if (toReach == 0 && loose == 0) return true;
            if (++walked > MaxWalk) return false;
            var node = (MeshNode)Walk.Dequeue();
            if (Loose.Contains(node)) loose--;
            if (node.connections == null) continue;
            foreach (var c in node.connections)
            {
                var other = c.node;
                if (other == null || other.Destroyed) continue;
                if (Group.TryGetValue(other, out var g))
                {
                    if (Label[g] == area) Enter(g);
                }
                else if (other.Area == area && Seen.Add(other)) Walk.Enqueue(other);
            }
        }
        return toReach == 0 && loose == 0;
    }

    private static bool Repair(ref uint last)
    {
        // The swapped tiles as they are now, and their nodes in groups joined within them.
        foreach (var slot in Slots)
        {
            var tiles = Tiles(slot.Graph);
            if (tiles == null || slot.X + slot.W > slot.Graph.tileXCount || slot.Z + slot.D > slot.Graph.tileZCount) return Fail("slot outside graph");
            for (var j = slot.Z; j < slot.Z + slot.D; j++)
            for (var i = slot.X; i < slot.X + slot.W; i++)
            {
                var tile = tiles[i + j * slot.Graph.tileXCount];
                if (tile == null || !Region.Add(tile) || tile.nodes == null) continue;
                foreach (var node in tile.nodes)
                {
                    if (node == null || node.Destroyed || !node.Walkable || Group.Count >= MaxNodes) return Fail(Group.Count >= MaxNodes ? "too many nodes" : "unwalkable or destroyed node");
                    Group[node] = -1;
                }
            }
        }

        var groups = 0;
        foreach (var seed in new List<GraphNode>(Group.Keys))
        {
            if (Group[seed] >= 0) continue;
            var g = groups++;
            Label.Add(0);
            GroupTouch.Add(new List<GraphNode>());
            Group[seed] = g;
            Pending.Push(seed);
            while (Pending.Count > 0)
            {
                var node = (MeshNode)Pending.Pop();
                if (node.connections == null) continue;
                foreach (var c in node.connections)
                {
                    var other = c.node;
                    if (other == null || other.Destroyed || !other.ContainsConnection(node)) return Fail("one-way or dead link");
                    if (Group.TryGetValue(other, out var og))
                    {
                        if (og < 0)
                        {
                            Group[other] = g;
                            Pending.Push(other);
                        }
                        continue;
                    }
                    // An outside node: its label still holds. This group takes it, and no other label.
                    var area = other.Area;
                    if (!other.Walkable || area == 0 || area > last) return Fail("unlabelled neighbour");
                    if (Label[g] != 0 && Label[g] != area) return Fail("two areas joined");
                    Label[g] = area;
                    if (Touching.Add(other)) GroupTouch[g].Add(other);
                }
            }
        }

        // Each area the new tiles touch must still hang together: every group of new nodes that touches it, and every
        // outside node of it that touched the old tiles, joined through the new groups or the area's own nodes outside.
        // Usually one group touches it and its old neighbours touch that group; otherwise (a cut through the middle of
        // a tile leaves the tile in two groups round the hole) a short walk through the area round about shows it.
        foreach (var node in OldEdge)
        {
            if (node.Destroyed || Group.ContainsKey(node) || Touching.Contains(node)) continue;
            if (!node.Walkable || node.Area == 0 || node.Area > last) return Fail("unlabelled old neighbour");
            Loose.Add(node);
        }
        AreaCount.Clear();
        for (var g = 0; g < groups; g++)
            if (Label[g] != 0) AreaCount[Label[g]] = AreaCount.TryGetValue(Label[g], out var n) ? n + 1 : 1;
        foreach (var node in Loose) AreaCount[node.Area] = 2;   // needs the walk
        var walked = 0;
        foreach (var pair in AreaCount)
            if (pair.Value > 1 && !HangsTogether(pair.Key, groups, ref walked)) return Fail("area may be cut in two");

        var next = last;
        for (var g = 0; g < groups; g++)
            if (Label[g] == 0)
            {
                if (next >= GraphNode.MaxAreaIndex) return Fail("out of area numbers");
                Label[g] = ++next;
            }
        foreach (var pair in Group)
        {
            var area = Label[pair.Value];
            if (pair.Key.Area != area) pair.Key.Area = area;
        }
        last = next;
        return true;
    }
}
