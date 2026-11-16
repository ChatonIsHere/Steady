using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Alta.Chunks;
using HarmonyLib;

namespace Steady.Fixes;

// A forest or cave chunk loads its saved entities (trees, rocks, creatures, placed items) in one go, as soon as its
// generated content is ready, and chunks loading near each other tend to get ready in the same frame: several
// chunks' entities then spawn in one frame. Steady holds each chunk at that point and lets them through from its
// own update, as many a frame as fit in ChunkLoadBudgetMs (at least one). Nothing about a load changes, only
// which frame it lands in; a chunk that is still loading isn't saved or unloaded, by the game's own rules.
internal static class ChunkLoads
{
    private static readonly Queue<TaskCompletionSource<bool>> Waiting = new();
    private static readonly Stopwatch Clock = new();
    private static double _spentMs;   // entity loading in the current release, timed where it runs
    private static int _loads;
    public static long Spread { get; private set; }
    public static int Pending => Waiting.Count;   // main thread only

    public static void Apply()
    {
        if (SteadyConfig.ChunkLoadBudgetMs <= 0) return;
        var any = false;
        // Forest Performance and Cave Performance hold the same point for forests and cave layers when theirs is on.
        if (Others.Forest("separate_entity_loads")) Others.LeaveToForest("spreading forest chunk loads");
        else any |= Hold(typeof(ForestChunk));
        if (Others.Cave("separate_entity_loads")) Others.LeaveToCave("spreading cave layer loads");
        else any |= Hold(typeof(CaveLayerStreamer));
        any |= Hold(typeof(StaticCaveChunk));
        if (!any) return;
        SteadyMod.Patch(AccessTools.Method(typeof(DynamicChunkContentManager), "LoadEntities"), "ChunkLoadBudgetMs", typeof(ChunkLoads),
            nameof(LoadBegin), finalizer: nameof(LoadEnd));
        SteadyMod.Active.Add("chunk loads spread over frames");
    }

    private static bool Hold(System.Type streamer) =>
        SteadyMod.Patch(AccessTools.Method(streamer, "Alta.Chunks.IProceduralGenerationStreamer.LoadDynamic"), "ChunkLoadBudgetMs",
            typeof(ChunkLoads), postfix: nameof(HoldResult));

    private static void HoldResult(ref Task __result)
    {
        if (__result != null) __result = AfterTurn(__result);
    }

    private static async Task AfterTurn(Task generated)
    {
        await generated;   // a failure goes straight back to the game, as before
        if (System.Threading.Thread.CurrentThread.ManagedThreadId != SteadyMod.MainThread) return;
        var turn = new TaskCompletionSource<bool>();
        Waiting.Enqueue(turn);
        Spread++;
        await turn.Task;
    }

    private static void LoadBegin() => Clock.Restart();

    private static void LoadEnd()
    {
        _spentMs += Clock.Elapsed.TotalMilliseconds;
        _loads++;
    }

    public static void Tick()
    {
        _spentMs = 0;
        while (Waiting.Count > 0)
        {
            var loads = _loads;
            // The chunk's own load carries on inside this call (its awaits resume on this thread), so its time
            // counts here. If it didn't run here, it runs later this frame: let one through a frame.
            Waiting.Dequeue().TrySetResult(true);
            if (_loads == loads || _spentMs >= SteadyConfig.ChunkLoadBudgetMs) break;
        }
    }
}
