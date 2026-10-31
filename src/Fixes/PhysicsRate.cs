using Alta.Utilities;
using HarmonyLib;
using UnityEngine;

namespace Steady.Fixes;

// DynamicPhysicsTimeSet sets the server's physics rate from its frame rate every 5 s (15-45 Hz).
// Each server settings reload registers its update again without removing the old one; this removes it
// first. MaxPhysicsHz, when set, caps the rate it picks.
internal static class PhysicsRate
{
    private static readonly AccessTools.FieldRef<DynamicPhysicsTimeSet, IUnityUpdateMethod> Update =
        AccessTools.FieldRefAccess<DynamicPhysicsTimeSet, IUnityUpdateMethod>("update");

    public static void Apply()
    {
        var type = typeof(DynamicPhysicsTimeSet);
        SteadyMod.Patch(AccessTools.Method(type, "SettingsChanged"), "PhysicsRate", typeof(PhysicsRate), nameof(Deregister));
        if (SteadyConfig.MaxPhysicsHz == 0) return;
        SteadyMod.Patch(AccessTools.Method(type, "Update"), "MaxPhysicsHz", typeof(PhysicsRate), postfix: nameof(Cap));
        SteadyMod.Patch(AccessTools.Method(type, "Disable"), "MaxPhysicsHz", typeof(PhysicsRate), postfix: nameof(Cap));
        SteadyMod.Active.Add($"physics capped at {SteadyConfig.MaxPhysicsHz} Hz");
    }

    private static void Deregister(DynamicPhysicsTimeSet __instance) => UpdateModule.DeregisterMethod(ref Update(__instance));

    private static void Cap()
    {
        var min = 1f / SteadyConfig.MaxPhysicsHz;
        if (Time.fixedDeltaTime < min) Time.fixedDeltaTime = min;
    }
}
