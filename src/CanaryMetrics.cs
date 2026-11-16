using System;
using System.Diagnostics;
using System.Linq;
using MelonLoader;
using Steady.Fixes;

namespace Steady;

// While Canary (github.com/ChatonIsHere/Canary) is recording, sends it what Steady did each second, so a recording
// shows Steady's work beside each spike. Canary is found at runtime; Steady never needs it.
internal static class CanaryMetrics
{
    private static Action<string, double> _metric;
    private static Func<bool> _recording;
    private static long _lastAt;
    private static double[] _last;
    private static bool[] _seen;

    // A counter is sent as its rate per second; a gauge as it stands. Each starts once it first moves, so fixes that
    // are off or idle don't fill the recording with zeros.
    private static readonly (string Name, bool Rate, Func<double> Read)[] Counters =
    {
        ("Steady.LocalAreaFills/s", true, () => LocalAreaFill.Repaired),
        ("Steady.FullAreaFills/s", true, () => LocalAreaFill.Full),
        ("Steady.LocalAreaFill ms/s", true, () => AreaFill.LocalMs),
        ("Steady.FullAreaFill ms/s", true, () => AreaFill.FullMs),
        ("Steady.GraphsSkipped/s", true, () => NearestNode.Skipped),
        ("Steady.ChunkLoadsHeld/s", true, () => ChunkLoads.Spread),
        ("Steady.ChunkLoadsWaiting", false, () => ChunkLoads.Pending),
        ("Steady.ForestInstructionsDeferred/s", true, () => ForestPacing.Deferred),
        ("Steady.PlayersSavedOverFrames/s", true, () => PlayerSaves.Spread),
        ("Steady.UnchangedChunksSkipped/s", true, () => UnchangedChunks.Skipped),
        ("Steady.ReceiveBuffersDropped/s", true, () => ReceivePool.Dropped),
        ("Steady.CaveScansRetried/s", true, () => CaveScans.Retried),
    };

    public static void Apply()
    {
        if (!SteadyConfig.CanaryMetrics) return;
        var api = MelonBase.RegisteredMelons.FirstOrDefault(m => m.Info.Name == "Canary")?.MelonAssembly?.Assembly
            .GetType("Canary.CanaryApi");
        var metric = api?.GetMethod("Metric", new[] { typeof(string), typeof(double) });
        var recording = api?.GetProperty("IsRecording")?.GetGetMethod();
        if (metric == null || recording == null) return;
        _metric = (Action<string, double>)Delegate.CreateDelegate(typeof(Action<string, double>), metric);
        _recording = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), recording);
        _seen = new bool[Counters.Length];
        SteadyMod.Active.Add("metrics sent to Canary");
    }

    // Main thread, every frame; does its work once a second.
    public static void Tick()
    {
        if (_metric == null) return;
        var now = Stopwatch.GetTimestamp();
        var seconds = (now - _lastAt) / (double)Stopwatch.Frequency;
        if (_last != null && seconds < 1) return;
        if (!_recording())
        {
            _last = null;
            return;
        }
        var values = Counters.Select(c => c.Read()).ToArray();
        if (_last != null)
            for (var i = 0; i < Counters.Length; i++)
            {
                var value = Counters[i].Rate ? (values[i] - _last[i]) / seconds : values[i];
                _seen[i] |= value != 0;
                if (_seen[i]) _metric(Counters[i].Name, Math.Round(value, 2));
            }
        _last = values;
        _lastAt = now;
    }
}
