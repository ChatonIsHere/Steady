using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Alta.Forests;
using Alta.Serialization;
using Alta.Static;
using HarmonyLib;
using UnityEngine;

namespace Steady.Fixes;

// A loading forest chunk runs its instructions (place this tree, that rock, and so on) ten a frame, each chunk on
// its own, on servers and clients alike. Chunks load together as players move, so a frame can run the batches of
// twenty or more chunks at once: 0.6 s frames were seen on a server with four rovers in the forest, and a client
// walking through the forest has about half the long frames with this on. Steady gives all forest chunks
// one shared allowance a frame (ForestBudgetMs): an instruction starts only while the frame still has some left,
// and the first one each frame always runs, so the forest keeps loading. Each chunk still runs at most ten a
// frame, in the game's order, with the same results; it just spreads over more frames when the forest is busy.
internal static class ForestPacing
{
    private static readonly AccessTools.FieldRef<InstructionSet, List<Instruction>> Instructions =
        AccessTools.FieldRefAccess<InstructionSet, List<Instruction>>("instructions");
    private static readonly AccessTools.FieldRef<InstructionSet, List<IInstructionResult>> Results =
        AccessTools.FieldRefAccess<InstructionSet, List<IInstructionResult>>("results");
    private static readonly AccessTools.FieldRef<InstructionSet, ConcurrentQueue<Instruction>> Adding =
        AccessTools.FieldRefAccess<InstructionSet, ConcurrentQueue<Instruction>>("adding");
    private static readonly AccessTools.FieldRef<InstructionSet, StreamReader> Reader =
        AccessTools.FieldRefAccess<InstructionSet, StreamReader>("reader");
    private static readonly AccessTools.FieldRef<InstructionSet, uint> ReaderOffset =
        AccessTools.FieldRefAccess<InstructionSet, uint>("readerOffset");

    private static readonly Stopwatch Clock = new();
    private static int _frame = -1;
    private static double _spentMs;
    public static long Deferred;

    public static void Apply()
    {
        if (SteadyConfig.ForestBudgetMs <= 0) return;
        if (Others.Forest("forest_instructions_per_frame"))
        {
            Others.LeaveToForest("forest loading pace");
            return;
        }
        if (SteadyMod.Patch(AccessTools.Method(typeof(InstructionSet), "ExecuteInternal"), "ForestBudgetMs", typeof(ForestPacing), nameof(ExecuteInternal)))
            SteadyMod.Active.Add($"forest loading within {SteadyConfig.ForestBudgetMs:0.#} ms a frame");
    }

    private static bool ExecuteInternal(InstructionSet __instance, object context, Task addingFinished, int instructionsPerFrame, ref IEnumerator __result)
    {
        if (context is not ForestGenerator) return true;
        __result = Run(__instance, context, addingFinished, instructionsPerFrame);
        return false;
    }

    // May an instruction start now? Counts this chunk's instructions in the current frame.
    private static bool Turn(ref int frame, ref int count, int perFrame)
    {
        var now = Time.frameCount;
        if (now != _frame)
        {
            _frame = now;
            _spentMs = 0;
        }
        if (now != frame)
        {
            frame = now;
            count = 0;
        }
        if (count >= perFrame || _spentMs >= SteadyConfig.ForestBudgetMs)
        {
            Deferred++;
            return false;
        }
        count++;
        Clock.Restart();
        return true;
    }

    private static void Spent() => _spentMs += Clock.Elapsed.TotalMilliseconds;

    // The game's InstructionSet.ExecuteInternal, waiting for a turn before each instruction.
    private static IEnumerator Run(InstructionSet set, object context, Task addingFinished, int perFrame)
    {
        int frame = -1, count = 0;
        var instructions = Instructions(set);
        var results = Results(set);
        for (var i = 0; i < instructions.Count; i++)
        {
            while (!Turn(ref frame, ref count, perFrame)) yield return null;
            var result = instructions[i].Execute(context);
            Spent();
            if (result != null) results.Add(result);
        }
        var adding = Adding(set);
        while (true)
        {
            if (adding.TryDequeue(out var instruction))
            {
                while (!Turn(ref frame, ref count, perFrame)) yield return null;
                instructions.Add(instruction);
                var result = instruction.Execute(context);
                Spent();
                if (result != null) results.Add(result);
            }
            else
            {
                yield return null;
                if (addingFinished.IsCompleted && !adding.TryPeek(out _)) break;
            }
        }
        if (Reader(set) != null)
        {
            var reader = Reader(set);
            reader.Reset();
            reader.SkipBits(ReaderOffset(set));
            uint saved = 0;
            reader.SerializeUnsignedInt(ref saved);
            for (var i = 0; i < saved; i++)
            {
                while (!Turn(ref frame, ref count, perFrame)) yield return null;
                var result = Instruction.Execute(Reader(set), context);
                Spent();
                if (result != null) results.Add(result);
            }
        }
        Reader(set) = null;
    }
}
