using System;
using Alta.Serialization;
using HarmonyLib;

namespace Steady.Fixes;

// The game's StreamWriter and StreamReader have finalizers that Dispose doesn't suppress, so every one
// (one per saved entity, plus network messages) waits for the finalizer thread and survives an extra GC.
// After Dispose the finalizer has nothing left to do.
internal static class StreamFinalizers
{
    public static void Apply()
    {
        if (!SteadyConfig.SuppressFinalizers) return;
        var writer = SteadyMod.Patch(AccessTools.Method(typeof(StreamWriter), nameof(StreamWriter.Dispose)), "SuppressFinalizers", typeof(StreamFinalizers), postfix: nameof(Suppress));
        var reader = SteadyMod.Patch(AccessTools.Method(typeof(StreamReader), nameof(StreamReader.Dispose)), "SuppressFinalizers", typeof(StreamFinalizers), postfix: nameof(Suppress));
        if (writer || reader) SteadyMod.Active.Add("stream finalizers suppressed");
    }

    private static void Suppress(object __instance) => GC.SuppressFinalize(__instance);
}
