using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace Steady.Fixes;

// Headless servers run GarbageCollectTimer, which calls GC.Collect every 30 s. Unity's Boehm GC ignores the
// "optimized, non-blocking" flags, so each call stops the world for a full collection (0.2 s on an idle
// server, 2-3 s on a busy one under Wine). Incremental GC, already on, keeps up without it.
//
// TavernLib sets the timer's interval from garbage_collection_timer in server_settings.json. Every launcher
// writes the default, 30, so only another value counts as the host's choice, and then the timer stays.
internal static class GcFix
{
    private const float DefaultInterval = 30f;

    public static void Apply()
    {
        if (SteadyConfig.ForcedGc) return;
        var chosen = HostInterval();
        if (chosen != null)
        {
            SteadyMod.Log.Msg($"Keeping the forced GC: server_settings.json sets garbage_collection_timer to {chosen.Value.ToString(CultureInfo.InvariantCulture)}");
            return;
        }
        if (SteadyMod.Patch(AccessTools.Method(typeof(GarbageCollectTimer), "Start"), "ForcedGc", typeof(GcFix), nameof(SkipStart)))
            SteadyMod.Active.Add("no forced GC");
    }

    // The host's interval, or null when it's missing, the default or unreadable.
    private static float? HostInterval()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TheModdingTavern", "server_settings.json");
            if (!File.Exists(path)) return null;
            var match = Regex.Match(File.ReadAllText(path), @"""garbage_collection_timer""\s*:\s*(-?[0-9.]+(?:[eE][-+]?[0-9]+)?)");
            if (!match.Success || !float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
            return Math.Abs(value - DefaultInterval) < 0.001f ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool SkipStart() => false;
}
