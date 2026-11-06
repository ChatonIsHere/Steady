using System.Collections.Generic;
using HarmonyLib;
using Pathfinding;
using Pathfinding.Util;
using UnityEngine;

namespace Steady.Fixes;

// Every frame, A* walks all navmesh cuts (each tree, rock and placed object that cuts the navmesh, several thousand
// with players about) asking each whether it has moved, stops at the first that has, and then walks them all again,
// asking each again, to handle the moved ones. With a cut moving somewhere most frames, that's two full walks a
// frame (~5 ms under Wine; each question reads a transform). Steady walks them once, noting the moved ones, then
// handles those in the same order. Asking has no side effects, so the result is the same.
internal static class CutChecks
{
    private static readonly System.Type SettingsType = AccessTools.Inner(typeof(NavmeshUpdates), "NavmeshUpdateSettings");
    private static readonly System.Reflection.FieldInfo UpdateData = AccessTools.Field(typeof(NavmeshBase), "navmeshUpdateData");
    private static readonly System.Reflection.FieldInfo SettingsHandler = AccessTools.Field(SettingsType, "handler");
    private static readonly System.Reflection.FieldInfo SettingsForced = AccessTools.Field(SettingsType, "forcedReloadRects");
    private static readonly System.Reflection.MethodInfo SettingsRefresh = AccessTools.Method(SettingsType, "Refresh");
    private static readonly AccessTools.FieldRef<NavmeshUpdates, float> LastUpdateTime = AccessTools.FieldRefAccess<NavmeshUpdates, float>("lastUpdateTime");
    private static readonly AccessTools.FieldRef<GridLookup<NavmeshClipper>.Root, IntRect> PreviousBounds =
        AccessTools.FieldRefAccess<GridLookup<NavmeshClipper>.Root, IntRect>("previousBounds");
    private static readonly System.Func<NavmeshClipper, GraphTransform, Rect> GetBounds =
        AccessTools.MethodDelegate<System.Func<NavmeshClipper, GraphTransform, Rect>>(AccessTools.Method(typeof(NavmeshClipper), "GetBounds"));
    private static readonly System.Action<NavmeshClipper> NotifyUpdated =
        AccessTools.MethodDelegate<System.Action<NavmeshClipper>>(AccessTools.Method(typeof(NavmeshClipper), "NotifyUpdated"));
    private static readonly object[] NoForce = { false };
    private static readonly List<GridLookup<NavmeshClipper>.Root> Moved = new();

    public static void Apply()
    {
        if (!SteadyConfig.SinglePassCutChecks) return;
        if (SettingsType == null || UpdateData == null || SettingsHandler == null || SettingsForced == null || SettingsRefresh == null) return;
        if (SteadyMod.Patch(AccessTools.Method(typeof(NavmeshUpdates), nameof(NavmeshUpdates.ForceUpdate)), "SinglePassCutChecks", typeof(CutChecks), nameof(ForceUpdate)))
            SteadyMod.Active.Add("navmesh cuts checked once a frame");
    }

    // The game's ForceUpdate, with its two walks over the cuts made one.
    private static bool ForceUpdate(NavmeshUpdates __instance)
    {
        LastUpdateTime(__instance) = Time.realtimeSinceStartup;
        List<NavmeshClipper> list = null;
        var graphs = AstarPath.active.graphs;
        for (var i = 0; i < graphs.Length; i++)
        {
            if (graphs[i] is not NavmeshBase navmeshBase) continue;
            var settings = UpdateData.GetValue(navmeshBase);
            SettingsRefresh.Invoke(settings, NoForce);
            if (SettingsHandler.GetValue(settings) is not TileHandler handler) continue;
            var forced = (List<IntRect>)SettingsForced.GetValue(settings);
            Moved.Clear();
            for (var root = handler.cuts.AllItems; root != null; root = root.next)
                if (root.obj.RequiresUpdate()) Moved.Add(root);
            if (forced.Count == 0 && Moved.Count == 0) continue;

            handler.StartBatchLoad();
            for (var j = 0; j < forced.Count; j++) handler.ReloadInBounds(forced[j]);
            forced.ClearFast();
            list ??= ListPool<NavmeshClipper>.Claim();
            foreach (var root in Moved)
            {
                handler.ReloadInBounds(PreviousBounds(root));
                var bounds = GetBounds(root.obj, handler.graph.transform);
                var tiles = handler.graph.GetTouchingTilesInGraphSpace(bounds);
                handler.cuts.Move(root.obj, tiles);
                handler.ReloadInBounds(tiles);
                list.Add(root.obj);
            }
            Moved.Clear();
            handler.EndBatchLoad();
        }
        if (list != null)
        {
            for (var k = 0; k < list.Count; k++) NotifyUpdated(list[k]);
            ListPool<NavmeshClipper>.Release(ref list);
        }
        return false;
    }
}
