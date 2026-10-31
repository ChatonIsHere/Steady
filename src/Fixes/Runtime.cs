using Pathfinding;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

namespace Steady.Fixes;

// Unity settings the game never sets, and pathfinding's per-path logging.
internal static class Runtime
{
    private static AstarPath _astar;

    public static void Apply()
    {
        if (SteadyConfig.MaxCatchUpMs > 0)
        {
            Time.maximumDeltaTime = SteadyConfig.MaxCatchUpMs / 1000f;
            SteadyMod.Active.Add($"catch-up capped at {SteadyConfig.MaxCatchUpMs} ms");
        }
        if (SteadyConfig.JobWorkers > 0)
        {
            JobsUtility.JobWorkerCount = Mathf.Clamp(SteadyConfig.JobWorkers, 1, JobsUtility.JobWorkerMaximumCount);
            SteadyMod.Active.Add($"{JobsUtility.JobWorkerCount} job workers");
        }
        if (SteadyConfig.QuietPathfinding) SteadyMod.Active.Add("quiet pathfinding");
    }

    // AstarPath lives in the world scene, so it can come and go.
    public static void Tick()
    {
        if (!SteadyConfig.QuietPathfinding || (object)AstarPath.active == _astar) return;
        _astar = AstarPath.active;
        if (_astar != null && _astar.logPathResults != PathLog.None && _astar.logPathResults != PathLog.OnlyErrors)
            _astar.logPathResults = PathLog.OnlyErrors;
    }
}
