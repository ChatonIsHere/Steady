using HarmonyLib;
using Pathfinding;

namespace Steady.Fixes;

// A graph mask has 32 bits, but graph numbers go past 31: every cave layer that has ever loaded adds a graph and
// keeps it. A* tests bit (number & 31), so graph 32 passes any mask graph 0 passes, and the forest's "overworld
// only" updates and lookups also go to cave graphs 32, 64 and so on. A mask other than "every graph" can only
// name graphs 0-31, so it doesn't take the ones above.
internal static class GraphMask
{
    public static void Apply()
    {
        if (!SteadyConfig.GraphMaskFix) return;
        if (Others.Forest("navigation_fixes"))
        {
            Others.LeaveToForest("graph mask fix");
            return;
        }
        if (SteadyMod.Patch(AccessTools.Method(typeof(NNConstraint), nameof(NNConstraint.SuitableGraph)), "GraphMaskFix", typeof(GraphMask), nameof(SuitableGraph)))
            SteadyMod.Active.Add("graph masks stop at graph 31");
    }

    private static bool SuitableGraph(NNConstraint __instance, int graphIndex, ref bool __result)
    {
        if (graphIndex < 32 || __instance.graphMask == -1) return true;
        __result = false;
        return false;
    }
}
