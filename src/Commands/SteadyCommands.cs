using System;
using Alta.Console;
using Steady.Fixes;
using UnityEngine;
using UnityEngine.Scripting;

namespace Steady.Commands;

[Module("steady", "Steady performance fixes")]
public static class SteadyCommands
{
    [ServerOnly]
    [Command("status", "Which fixes are on, and what they've done.")]
    private static string Status() =>
        $"Steady {SteadyMod.Version}. On: {string.Join(", ", SteadyMod.Active)}.\n"
        + $"Players saved over frames: {PlayerSaves.Spread}. Receive buffers dropped past the pool: {ReceivePool.Dropped}.\n"
        + $"GC: {GC.CollectionCount(0)} collections, heap {GC.GetTotalMemory(false) / 1048576} MB, incremental {GarbageCollector.isIncremental}. "
        + $"Physics {1f / Time.fixedDeltaTime:F0} Hz.";
}
