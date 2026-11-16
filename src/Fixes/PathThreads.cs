using HarmonyLib;
using Pathfinding;

namespace Steady.Fixes;

// The game computes every creature's paths on one thread. With creatures busy in the caves that one thread can
// fall behind, and creatures stand waiting for their paths. More threads share the queue; each uses another core.
internal static class PathThreads
{
    public static void Apply()
    {
        if (SteadyConfig.PathThreads <= 0) return;
        if (Others.Cave("path_workers"))
        {
            Others.LeaveToCave("pathfinding threads");
            return;
        }
        // The thread count is read when the world's AstarPath starts, after Steady loads.
        if (SteadyMod.Patch(AccessTools.Method(typeof(AstarPath), "InitializePathProcessor"), "PathThreads", typeof(PathThreads), nameof(Initialize)))
            SteadyMod.Active.Add($"{SteadyConfig.PathThreads} pathfinding threads");
    }

    private static void Initialize(AstarPath __instance) => __instance.threadCount = (ThreadCount)SteadyConfig.PathThreads;
}
