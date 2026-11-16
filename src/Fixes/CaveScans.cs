using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Alta.Caves;
using Alta.Chunks;
using HarmonyLib;
using Pathfinding;
using UnityEngine;

namespace Steady.Fixes;

// Each cave layer scans its navmesh the first time it loads. The game checks that no scan is running and then
// starts its own, but a scan only counts as running once it has taken its first step, so two layers loading
// together can both pass the check. The second scan then throws "Another async scan is already running", and:
// a procedural layer keeps the empty graph it made, so its creatures never find a path, and a static cave chunk
// never finishes loading. The game's own check can also give up after a minute and leave the layer unscanned
// for good. Steady runs cave scans one at a time and, if one fails, removes its graph and tries again.
internal static class CaveScans
{
    private const int Attempts = 3;
    private const float WaitSeconds = 60f, RetrySeconds = 5f;

    private static readonly AccessTools.FieldRef<CaveLayerStreamer, RecastGraph> LayerGraph =
        AccessTools.FieldRefAccess<CaveLayerStreamer, RecastGraph>("navGraph");
    private static readonly AccessTools.FieldRef<StaticCaveChunk, LocationChunk> StaticChunk =
        AccessTools.FieldRefAccess<StaticCaveChunk, LocationChunk>("chunk");
    private static readonly AccessTools.FieldRef<StaticCaveChunk, TaskCompletionSource<bool>> StaticDone =
        AccessTools.FieldRefAccess<StaticCaveChunk, TaskCompletionSource<bool>>("pathfindingGenerated");

    // Set while one of ours is between creating its graph and finishing its scan.
    private static bool _scanning;
    private static readonly HashSet<CaveLayerStreamer> Pending = new();
    public static int Retried { get; private set; }

    public static void Apply()
    {
        if (!SteadyConfig.CaveScanFix) return;
        if (Others.Cave("navigation_recovery"))
        {
            Others.LeaveToCave("cave scan fix");
            return;
        }
        var layer = SteadyMod.Patch(AccessTools.Method(typeof(CaveLayerStreamer), nameof(CaveLayerStreamer.GeneratePathfinding)),
            "CaveScanFix", typeof(CaveScans), nameof(LayerPrefix));
        var chunk = SteadyMod.Patch(AccessTools.Method(typeof(StaticCaveChunk), "GeneratePathfinding"),
            "CaveScanFix", typeof(CaveScans), nameof(StaticPrefix));
        if (layer || chunk) SteadyMod.Active.Add("cave scans one at a time, retried");
    }

    private static bool LayerPrefix(CaveLayerStreamer __instance, ref IEnumerator __result)
    {
        __result = Layer(__instance);
        return false;
    }

    private static bool StaticPrefix(StaticCaveChunk __instance, ref IEnumerator __result)
    {
        __result = Static(__instance);
        return false;
    }

    private static IEnumerator Layer(CaveLayerStreamer layer)
    {
        // Each chunk of a layer asks for the scan when it finishes rendering; the first one does it.
        if (LayerGraph(layer) != null || !Pending.Add(layer)) yield break;
        try
        {
            var scan = Scan(layer.Bounds, g => LayerGraph(layer) = g, () => LayerGraph(layer) = null, layer.name);
            while (scan.MoveNext()) yield return scan.Current;
        }
        finally
        {
            Pending.Remove(layer);
        }
    }

    private static IEnumerator Static(StaticCaveChunk cave)
    {
        var chunk = StaticChunk(cave);
        var scan = Scan(chunk.Bounds, _ => { }, () => { }, cave.name);
        while (scan.MoveNext()) yield return scan.Current;
        // The chunk's load waits for this. If every attempt failed it loads without a navmesh rather than never.
        StaticDone(cave).TrySetResult(true);
    }

    private static IEnumerator Scan(Bounds bounds, Action<RecastGraph> made, Action failed, string name)
    {
        for (var attempt = 1; ; attempt++)
        {
            var giveUp = Time.realtimeSinceStartup + WaitSeconds;
            while (_scanning || AstarPath.active.isScanning)
            {
                if (Time.realtimeSinceStartup > giveUp)
                {
                    SteadyMod.Log.Warning($"CaveScanFix: waited {WaitSeconds:F0} s for another navmesh scan; {name} is left unscanned, as the game would.");
                    yield break;
                }
                yield return null;
            }

            RecastGraph graph;
            IEnumerator<Progress> steps;
            try
            {
                graph = CaveSettings<StreamingSettings>.Instance.GetCavePathfindingGraph();
                graph.forcedBoundsCenter = bounds.center;
                graph.forcedBoundsSize = bounds.size;
                made(graph);
                steps = AstarPath.active.ScanAsync(graph).GetEnumerator();
            }
            catch (Exception e)
            {
                SteadyMod.Log.Warning($"CaveScanFix: couldn't start the navmesh scan for {name}: {e.Message}");
                failed();
                yield break;
            }

            Exception error = null;
            _scanning = true;
            try
            {
                while (Step(steps, ref error)) yield return null;
            }
            finally
            {
                _scanning = false;
            }
            if (error == null) yield break;

            failed();
            while (AstarPath.active.isScanning) yield return null;
            try
            {
                AstarPath.active.data.RemoveGraph(graph);
            }
            catch (Exception e)
            {
                SteadyMod.Log.Warning($"CaveScanFix: couldn't remove the failed graph for {name}: {e.Message}");
            }
            if (attempt == Attempts)
            {
                SteadyMod.Log.Error($"CaveScanFix: the navmesh scan for {name} failed {Attempts} times; last error: {error}");
                yield break;
            }
            Retried++;
            SteadyMod.Log.Warning($"CaveScanFix: the navmesh scan for {name} failed ({error.Message}); trying again in {RetrySeconds:F0} s.");
            var retry = Time.realtimeSinceStartup + RetrySeconds;
            while (Time.realtimeSinceStartup < retry) yield return null;
        }
    }

    // One step of the scan, catching what it throws (an iterator can't yield inside a catch).
    private static bool Step(IEnumerator<Progress> steps, ref Exception error)
    {
        try
        {
            return steps.MoveNext();
        }
        catch (Exception e)
        {
            error = e;
            return false;
        }
    }
}
