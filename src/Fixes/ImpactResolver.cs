using System;
using System.Collections;
using System.Collections.Generic;
using Alta.Impact;
using HarmonyLib;

namespace Steady.Fixes;

// Hits by held items are resolved once a frame by DelayedImpactResolver: on a server, players hitting players; on a
// client, the player's own hits on creatures and things. If it throws outside its own try (a hit whose tool has no
// entity, or a hit queued while it resolves), the game's update loop drops it, and it never registers again: from
// then on those hits do no damage until a restart, and they pile up unresolved.
// Steady drops the hits the game can't resolve before it runs, and if it throws anyway, logs why, drops that
// frame's hits and keeps it running.
internal static class ImpactResolver
{
    private static IDictionary _frameHits;
    private static bool _logged;
    private static readonly List<object> Bad = new();

    public static int Caught;

    public static void Apply()
    {
        if (!SteadyConfig.ImpactResolverFix) return;
        _frameHits = AccessTools.Field(typeof(DelayedImpactResolver), "frameHits")?.GetValue(null) as IDictionary;
        if (_frameHits != null && SteadyMod.Patch(AccessTools.Method(typeof(DelayedImpactResolver), "Update"), "ImpactResolverFix",
                typeof(ImpactResolver), nameof(Prefix), finalizer: nameof(Finalizer)))
            SteadyMod.Active.Add("impact resolver kept running");
    }

    // The game skips a hit whose tool's entity is being destroyed, but throws on one with no entity at all.
    private static void Prefix()
    {
        if (_frameHits.Count == 0) return;
        foreach (DictionaryEntry e in _frameHits)
            if (e.Key is ImpactTool tool && (object)tool.Entity == null) Bad.Add(tool);
        foreach (var tool in Bad) _frameHits.Remove(tool);
        Bad.Clear();
    }

    private static Exception Finalizer(Exception __exception)
    {
        if (__exception == null) return null;
        Caught++;
        _frameHits.Clear();
        if (!_logged)
        {
            _logged = true;
            SteadyMod.Log.Warning($"ImpactResolverFix: the game's impact resolver threw, so this frame's hits are dropped and it keeps running (logged once). {__exception}");
        }
        return null;
    }
}
