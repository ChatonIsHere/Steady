using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace Steady.Fixes;

// When a navmesh scan finishes (the world's at boot, and each cave layer's the first time it loads), the
// pathfinding library calls GC.Collect: a full stop-the-world collection, like the 30 s timer's (2-3 s under Wine
// on a busy server). The incremental GC frees the scan's garbage without it.
internal static class ScanGc
{
    private static int _removed;

    public static void Apply()
    {
        if (SteadyConfig.ScanGc) return;
        var scan = AccessTools.FirstInner(typeof(AstarPath), t => t.Name.StartsWith("<ScanAsync>d__"));
        if (SteadyMod.Patch(scan == null ? null : AccessTools.Method(scan, "MoveNext"), "ScanGc", typeof(ScanGc), transpiler: nameof(DropCollect)))
        {
            if (_removed > 0) SteadyMod.Active.Add("no GC after navmesh scans");
            else SteadyMod.Log.Msg("ScanGc: the navmesh scan has no GC.Collect to remove (another mod may have removed it).");
        }
    }

    private static IEnumerable<CodeInstruction> DropCollect(IEnumerable<CodeInstruction> code)
    {
        var collect = AccessTools.Method(typeof(GC), nameof(GC.Collect), Type.EmptyTypes);
        foreach (var i in code)
        {
            if (!i.Calls(collect))
            {
                yield return i;
                continue;
            }
            _removed++;
            // GC.Collect() takes and leaves nothing on the stack, so a nop keeps the IL valid (and any jump to it).
            yield return new CodeInstruction(OpCodes.Nop).MoveLabelsFrom(i).MoveBlocksFrom(i);
        }
    }
}
