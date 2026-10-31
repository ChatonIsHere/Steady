using System.Collections.Generic;
using HarmonyLib;
using Pathfinding;

namespace Steady.Fixes;

// After a graph update rebuilds a few navmesh tiles, the game's RecastGraph.UpdateAreaPost hands the navmesh
// cutter every tile in the graph instead of only the rebuilt ones (stock A* passes the rebuilt ones). The
// cutter then re-cuts and reloads all ~19,000 tiles on the main thread: a 2-3 s freeze each time the server
// loads a forest chunk. It also takes the already-cut tiles as their new uncut shape, so holes from cuts
// that later go away stay. Hand it only the rebuilt tiles.
internal static class NavmeshReload
{
    private static readonly AccessTools.FieldRef<RecastGraph, List<NavmeshTile>> Staging =
        AccessTools.FieldRefAccess<RecastGraph, List<NavmeshTile>>("stagingTiles");

    public static void Apply()
    {
        if (!SteadyConfig.RebuiltTilesOnly) return;
        var settings = AccessTools.Inner(typeof(NavmeshUpdates), "NavmeshUpdateSettings");
        if (SteadyMod.Patch(settings == null ? null : AccessTools.Method(settings, "OnRecalculatedTiles"), "RebuiltTilesOnly", typeof(NavmeshReload), nameof(OnlyRebuilt)))
            SteadyMod.Active.Add("navmesh reloads rebuilt tiles only");
    }

    private static void OnlyRebuilt(NavmeshBase ___graph, ref NavmeshTile[] tiles)
    {
        if (___graph is RecastGraph recast && ReferenceEquals(tiles, recast.GetTiles()))
            tiles = Staging(recast).ToArray();
    }
}
