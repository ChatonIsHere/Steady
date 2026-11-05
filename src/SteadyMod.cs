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
    public const string Version = "0.1.0";

    internal static MelonLogger.Instance Log { get; private set; }

    public static bool IsServer { get; } =
        Environment.GetCommandLineArgs().Any(a => string.Equals(a, "/start_server", StringComparison.OrdinalIgnoreCase));

    // What's on, for the startup line and `steady status`.
    internal static readonly List<string> Active = new();

    private static HarmonyLib.Harmony _harmony;

    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;
        _harmony = HarmonyInstance;
        SteadyConfig.Init();

        StreamFinalizers.Apply();
        ReceivePool.Apply();
        AreaFill.Apply();
        if (IsServer)
        {
            GcFix.Apply();
            SpawnCheck.Apply();
            PlayerSaves.Apply();
            PhysicsRate.Apply();
            NavmeshReload.Apply();
            SaveOnce.Apply();
            UnchangedChunks.Apply();
            DeadReferences.Apply();
            Alta.Console.CommandService.CommandCollection.Collect(Assembly.GetExecutingAssembly());
        }
        // NLog and the scene aren't set up until the game starts.
        MelonEvents.OnApplicationLateStart.Subscribe(() =>
        {
            Logging.Apply();
            Runtime.Apply();
            Log.Msg("On: " + (Active.Count == 0 ? "nothing" : string.Join(", ", Active)));
        });
    }

    public override void OnUpdate()
    {
        PlayerSaves.Tick();
        Runtime.Tick();
        DeadReferences.Tick();
    }

    // Patches one method, or warns and carries on if the game doesn't have it.
    internal static bool Patch(MethodBase target, string name, Type patches, string prefix = null, string postfix = null, string finalizer = null)
    {
        if (target == null)
        {
            Log.Warning($"{name}: target not found, skipped.");
            return false;
        }
        try
        {
            _harmony.Patch(target,
                prefix == null ? null : new HarmonyMethod(patches, prefix),
                postfix == null ? null : new HarmonyMethod(patches, postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(patches, finalizer));
            return true;
        }
        catch (Exception e)
        {
            Log.Warning($"{name}: couldn't patch {target.DeclaringType?.Name}.{target.Name}, skipped. {e.Message}");
            return false;
        }
    }
}
