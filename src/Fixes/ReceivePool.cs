using System.Collections.Concurrent;
using System.Threading;
using Alta.NetworkingTransport;
using HarmonyLib;

namespace Steady.Fixes;

// Every received message is copied into a 64 KB buffer from a pool the game caps at 100. A frame holds its
// buffers until it's processed, so after a hitch a burst allocates fresh buffers and then throws them away,
// which feeds the next GC. Buffers hold no pointers, so keeping more costs memory but no GC time.
internal static class ReceivePool
{
    private static readonly ConcurrentBag<byte[]> Pool =
        (ConcurrentBag<byte[]>)AccessTools.Field(typeof(MessageReceivePool), "Pool")?.GetValue(null);

    internal static int Dropped;

    public static void Apply()
    {
        if (SteadyConfig.ReceivePoolSize <= MessageReceivePool.MaximumPoolSize || Pool == null) return;
        if (SteadyMod.Patch(AccessTools.Method(typeof(MessageReceivePool), nameof(MessageReceivePool.Put)), "ReceivePoolSize", typeof(ReceivePool), nameof(Put)))
            SteadyMod.Active.Add($"receive pool {SteadyConfig.ReceivePoolSize}");
    }

    private static bool Put(byte[] bytes)
    {
        if (bytes?.Length != MessageReceivePool.MaximumMessageSize) return false;
        if (Pool.Count < SteadyConfig.ReceivePoolSize) Pool.Add(bytes);
        else Interlocked.Increment(ref Dropped);
        return false;
    }
}
