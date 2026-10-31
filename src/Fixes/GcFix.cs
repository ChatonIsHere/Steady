using HarmonyLib;

namespace Steady.Fixes;

// Headless servers run GarbageCollectTimer, which calls GC.Collect every 30 s. Unity's Boehm GC ignores the
// "optimized, non-blocking" flags, so each call stops the world for a full collection (0.2 s on an idle
// server, 2-3 s on a busy one under Wine). Incremental GC, already on, keeps up without it.
internal static class GcFix
{
    public static void Apply()
    {
        if (SteadyConfig.ForcedGc) return;
        if (SteadyMod.Patch(AccessTools.Method(typeof(GarbageCollectTimer), "Start"), "ForcedGc", typeof(GcFix), nameof(SkipStart)))
            SteadyMod.Active.Add("no forced GC");
    }

    private static bool SkipStart() => false;
}
