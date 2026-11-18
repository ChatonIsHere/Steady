using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Steady.Fixes;

[assembly: MelonInfo(typeof(Steady.SteadyMod), "Steady", Steady.SteadyMod.Version, "ChatonIsHere", "https://github.com/ChatonIsHere/Steady")]

namespace Steady;

public class SteadyMod : MelonMod
{
    public const string Version = Build.Version;

    internal static MelonLogger.Instance Log { get; private set; }
    internal static int MainThread { get; private set; }

    public static bool IsServer { get; } =
        Environment.GetCommandLineArgs().Any(a => string.Equals(a, "/start_server", StringComparison.OrdinalIgnoreCase));

    // What's on, for the startup line and `steady status`.
    internal static readonly List<string> Active = new();

    private static HarmonyLib.Harmony _harmony;
    internal static string HarmonyId => _harmony.Id;

    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;
        MainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        _harmony = HarmonyInstance;
        SteadyConfig.Init();
        Others.Detect();

        AltaCutoff.Apply();
        ImpactResolver.Apply();
        StreamFinalizers.Apply();
        ReceivePool.Apply();
        if (IsServer) LocalAreaFill.Apply();
        AreaFill.Apply();
        ForestPacing.Apply();
        if (IsServer)
        {
            GcFix.Apply();
            ScanGc.Apply();
            SpawnCheck.Apply();
            PlayerSaves.Apply();
            PhysicsRate.Apply();
            NavmeshReload.Apply();
            CutChecks.Apply();
            SaveOnce.Apply();
            UnchangedChunks.Apply();
            DeadReferences.Apply();
            NearestNode.Apply();
            GraphMask.Apply();
            PathThreads.Apply();
            CaveScans.Apply();
            ChunkLoads.Apply();
            Alta.Console.CommandService.CommandCollection.Collect(Assembly.GetExecutingAssembly());
        }
        // NLog and the scene aren't set up until the game starts.
        MelonEvents.OnApplicationLateStart.Subscribe(() =>
        {
            Logging.Apply();
            Runtime.Apply();
            CanaryMetrics.Apply();
            AltaCutoff.Report();
            Log.Msg("On: " + (Active.Count == 0 ? "nothing" : string.Join(", ", Active)));
        });
    }

    public override void OnUpdate()
    {
        PlayerSaves.Tick();
        Runtime.Tick();
        DeadReferences.Tick();
        ChunkLoads.Tick();
        CanaryMetrics.Tick();
    }

    // Patches one method, or warns and carries on if the game doesn't have it. A lower priority runs our prefix after
    // other mods' prefixes on the same method.
    internal static bool Patch(MethodBase target, string name, Type patches, string prefix = null, string postfix = null,
        string finalizer = null, string transpiler = null, int priority = HarmonyLib.Priority.Normal)
    {
        if (target == null)
        {
            Log.Warning($"{name}: target not found, skipped.");
            return false;
        }
        HarmonyMethod Method(string m) => m == null ? null : new HarmonyMethod(patches, m) { priority = priority };
        try
        {
            _harmony.Patch(target, Method(prefix), Method(postfix), Method(transpiler), Method(finalizer), ilmanipulator: null);
            return true;
        }
        catch (Exception e)
        {
            Log.Warning($"{name}: couldn't patch {target.DeclaringType?.Name}.{target.Name}, skipped. {e.Message}");
            return false;
        }
    }
}
