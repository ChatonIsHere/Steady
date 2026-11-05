using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Alta.Impact;
using Alta.Intelligence;
using Alta.Liquid;
using Alta.Networking.Servers;
using Alta.PlayerStates.Transitions;
using Alta.Utilities;
using Features.Gameplay.ClimbingTower;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Steady.Fixes;

// The game keeps references to destroyed objects in a few places, and each one keeps the whole destroyed
// entity alive with it (its components, network sync, meshes, and what those reach), so the heap grows with
// everything that dies or unloads (20 players: live heap 380 MB to 1 GB in 40 minutes; with these dropped it
// levelled off at ~700 MB). Steady drops those references, which changes nothing the game does with them:
//   ImpactDetector.hitCooldowns never removes a tool; expired cooldowns act like missing ones. Dropped on each hit,
//   and every 5 minutes for detectors that aren't hit again.
//   ActionDecider.possibleTargetHashSet: GetBestAction drops gone targets from its list but not from this set.
//   Agent.providedTargets keeps a target destroyed without a RemoveTarget call.
//   LiquidContainerTrigger: a body destroyed inside the trigger never leaves its handled* maps.
//   BasicAction.activePool keeps finished actions with their agent; Start makes a new one when it's empty.
//   AI hooks are shared by every creature using an action, and keep a map entry per creature that stays when it is
//   destroyed mid-action (FixedStatChangeHook never removes one). ProgressStatChangeHook's also keeps updating the
//   destroyed creature's stat every frame, so those are disposed as they are dropped.
//   Every player who ever joined stays in ServerDiagnosticsManager.PlayersToSendTo (never pruned), in
//   Player.LocalPlayerSet (each player's handler is removed when it is raised, which only clients do), and in
//   PlayingToCustomizationTransition.trigger if they left while playing. ChunkSync keeps them too: its pooled syncs
//   keep the last player and chunk, and its per-player map can keep players who left. Each player's saved content
//   (gold bank account, postbox, climbing tower save) stays subscribed to ServerHandler.PlayerJoined with its player
//   after its manager drops it, and each ControllerDPad to GameConfig.SettingsChanged (it unsubscribes in a finalizer,
//   which the subscription keeps from running). PlayerList.LastList keeps the players the last console command named.
//   TradeVendor never leaves ServerHandler.PlayerJoined (a destroyed one throws when its owner joins, which stops the
//   handlers after it), and SpawnOnDestroyed never leaves ApplicationManager.ApplicationQuit.
//   A released pooled impact tool keeps the entity that last used it (SeedSpray sets it on each shot, and leaves its
//   hit handlers on if disabled mid-shot). A destroyed tool keeps both too, and SeedSpray's shot arrays go back to a
//   pool that never clears them, so they keep destroyed seeds. SpawnMessage and SceneEntityMessage keep the last
//   entity they were set for, and TargetCollectorManager targets that no longer exist until a collector is forced to
//   update. Each of these held a whole unloaded area's destroyed entities through the links between them.
// Target sets are pruned as the game uses them, the rest every 5 minutes, one kind per frame (each search of all
// objects is a few hundred milliseconds on a big world).
internal static class DeadReferences
{
    private const float SweepSeconds = 300f;
    private static readonly AccessTools.FieldRef<Agent, HashSet<ITarget>> Provided =
        AccessTools.FieldRefAccess<Agent, HashSet<ITarget>>("providedTargets");
    private static readonly AccessTools.FieldRef<ImpactDetector, Dictionary<ImpactTool, float>> HitCooldowns =
        AccessTools.FieldRefAccess<ImpactDetector, Dictionary<ImpactTool, float>>("hitCooldowns");
    private static readonly System.Reflection.FieldInfo LocalPlayerSet = AccessTools.Field(typeof(Player), "LocalPlayerSet");
    private static readonly System.Reflection.FieldInfo CustomizationTrigger = AccessTools.Field(typeof(PlayingToCustomizationTransition), "trigger");
    private static readonly System.Reflection.PropertyInfo TransitionPlayer = AccessTools.Property(typeof(PlayingToCustomizationTransition), "Player");
    private static readonly Type ChunkSync = AccessTools.TypeByName("Alta.Chunks.ChunkSync");
    private static readonly System.Reflection.FieldInfo SyncPool = AccessTools.Field(ChunkSync, "pool");
    private static readonly System.Reflection.FieldInfo SyncPlayers = AccessTools.Field(ChunkSync, "playerSyncs");
    private static readonly System.Reflection.FieldInfo SyncDisconnects = AccessTools.Field(ChunkSync, "disconnectedConnections");
    private static readonly System.Reflection.FieldInfo SyncPlayer = AccessTools.Field(ChunkSync, "player");
    private static readonly System.Reflection.FieldInfo SyncChunk = AccessTools.Field(ChunkSync, "chunk");
    private static readonly System.Reflection.FieldInfo PlayerJoined = AccessTools.Field(typeof(ServerHandler), "PlayerJoined");
    private static readonly System.Reflection.FieldInfo ApplicationQuit = AccessTools.Field(typeof(ApplicationManager), "ApplicationQuit");
    private static readonly Type[] ContentManagers = { typeof(CentralGoldCoinBank), typeof(PostboxManager), typeof(ClimbingTowerTeleporterPerPlayerSaveable) };
#pragma warning disable CS0618 // obsolete, but still what GameConfig holds
    private static readonly System.Reflection.FieldInfo SettingsChanged = AccessTools.Field(typeof(SettingsFileAccess<GameSettings>), "SettingsChanged");
#pragma warning restore CS0618
    private static readonly System.Reflection.FieldInfo DPadInput = AccessTools.Field(typeof(ControllerDPad), "playerInput");
    private static readonly System.Reflection.PropertyInfo LastNamed = AccessTools.Property(typeof(PlayerList), "LastList");
    private static readonly System.Reflection.FieldInfo[] LastEntities =
    {
        AccessTools.Field(typeof(Alta.Networking.SpawnMessage), "entity"),
        AccessTools.Field(typeof(Alta.Networking.SceneEntityMessage), "entity"),
    };
    private static readonly System.Reflection.FieldInfo CollectorTargets = AccessTools.Field(typeof(TargetCollectorManager), "targets");
    private static readonly System.Reflection.FieldInfo ComponentAction = AccessTools.Field(typeof(ActionComponent), "action");
    private static readonly System.Reflection.FieldInfo[] TriggerMaps =
    {
        AccessTools.Field(typeof(LiquidContainerTrigger), "handledRigidbodies"),
        AccessTools.Field(typeof(LiquidContainerTrigger), "handledPickups"),
        AccessTools.Field(typeof(LiquidContainerTrigger), "handledLiquidContainers"),
        AccessTools.Field(typeof(LiquidContainerTrigger), "handledPourableLiquidContainers"),
    };
    private static readonly System.Reflection.FieldInfo[] HookMaps =
    {
        AccessTools.Field(typeof(AnimatedBoolAiHook), "booleans"),
        AccessTools.Field(typeof(AnimatedFloatAiHook), "booleans"),
        AccessTools.Field(typeof(AnimatedFloatTransitionAiHook), "floats"),
        AccessTools.Field(typeof(AnimatedLayerAiHook), "weights"),
        AccessTools.Field(typeof(AttackDirectionReactAIHook), "actions"),
        AccessTools.Field(typeof(StatChangeHook), "affectedStats"),
        AccessTools.Field(typeof(FixedStatChangeHook), "addedExtras"),
        AccessTools.Field(typeof(ProgressStatChangeHook), "progresses"),
    };
    private static readonly System.Reflection.MethodInfo DisposeProgress =
        AccessTools.Method(AccessTools.Inner(typeof(ProgressStatChangeHook), "TopicProgress"), "Dispose");
    private static readonly object[] KeepStat = { false };
    private static readonly Dictionary<Type, System.Reflection.FieldInfo> PossibleSets = new();
    private static readonly Dictionary<Type, System.Reflection.FieldInfo> Pools = new();
    private static readonly List<ImpactTool> Expired = new();
    private static readonly List<DictionaryEntry> Gone = new();
    private static readonly Predicate<ITarget> NotThere = t => !t.Exists();
    private static readonly Predicate<ITarget> Destroyed = t => IsDestroyed(t);
    private static readonly HashSet<object> Loaded = new();
    private static readonly HashSet<ImpactDetector> Cooling = new();
    private static readonly List<ImpactTool> DestroyedTools = new();
    private static readonly Predicate<ImpactDetector> DoneCooling = d => IsDestroyed(d) || DropExpired(HitCooldowns(d)) == 0;
    private static readonly Action[] Steps = { DropTriggerBodies, DropHookEntries, DropCooldowns, DropPlayers, DropStaleHandlers, EmptyActionPools, DropLastUsers };
    private static bool _on;
    private static float _nextSweep = SweepSeconds;
    private static int _step = Steps.Length; // the sweep's next step; Steps.Length between sweeps

    public static void Apply()
    {
        if (!SteadyConfig.DropDeadReferences) return;
        var cooldowns = SteadyMod.Patch(AccessTools.Method(typeof(ImpactDetector), "OnToolHit"), "DropDeadReferences", typeof(DeadReferences), nameof(OnToolHit));
        var targets = SteadyMod.Patch(AccessTools.Method(typeof(Agent), "UpdateHurt"), "DropDeadReferences", typeof(DeadReferences), postfix: nameof(UpdateHurt));
        var tools = SteadyMod.Patch(AccessTools.Method(typeof(ImpactTool), "OnDestroy"), "DropDeadReferences", typeof(DeadReferences), postfix: nameof(ToolDestroyed));
        _on = cooldowns || targets || tools;
        if (_on) SteadyMod.Active.Add("dead references dropped");
    }

    public static void Tick()
    {
        if (!_on) return;
        if (DestroyedTools.Count > 0) ClearDestroyedTools();
        if (_step == Steps.Length)
        {
            if (Time.realtimeSinceStartup < _nextSweep) return;
            _nextSweep = Time.realtimeSinceStartup + SweepSeconds;
            _step = 0;
        }
        try
        {
            Steps[_step++]();
        }
        catch (Exception e)
        {
            SteadyMod.Log.Warning("DropDeadReferences: " + e.Message);
        }
    }

    // Detectors that were hit are remembered until their cooldowns run out, so the sweep needn't search for them.
    private static void OnToolHit(ImpactDetector __instance, Dictionary<ImpactTool, float> ___hitCooldowns)
    {
        DropExpired(___hitCooldowns);
        Cooling.Add(__instance);
    }

    private static int DropExpired(Dictionary<ImpactTool, float> cooldowns)
    {
        if (cooldowns == null) return 0;
        var now = Time.realtimeSinceStartup;
        foreach (var kv in cooldowns)
            if (kv.Value <= now) Expired.Add(kv.Key);
        foreach (var tool in Expired) cooldowns.Remove(tool);
        Expired.Clear();
        return cooldowns.Count;
    }

    // Cleared on the next frame, once nothing destroyed with it reads it any more.
    private static void ToolDestroyed(ImpactTool __instance) => DestroyedTools.Add(__instance);

    private static void ClearDestroyedTools()
    {
        foreach (var tool in DestroyedTools)
        {
            tool.Entity = null;
            tool.ClearHitEvents();
        }
        DestroyedTools.Clear();
    }

    // UpdateHurt runs every frame for every agent; its sets are pruned on one frame in 256.
    private static void UpdateHurt(Agent __instance)
    {
        if (((Time.frameCount + __instance.GetInstanceID()) & 255) != 0) return;
        Provided(__instance)?.RemoveWhere(Destroyed);
        var decider = __instance.GeneralActionDecider;
        if (decider == null) return;
        var type = decider.GetType();
        if (!PossibleSets.TryGetValue(type, out var field)) PossibleSets[type] = field = AccessTools.Field(type, "possibleTargetHashSet");
        (field?.GetValue(decider) as HashSet<ITarget>)?.RemoveWhere(NotThere);
    }

    private static void DropTriggerBodies()
    {
        foreach (var trigger in Resources.FindObjectsOfTypeAll<LiquidContainerTrigger>())
            foreach (var field in TriggerMaps)
                DropDestroyed(field?.GetValue(trigger) as IDictionary);
    }

    private static void DropHookEntries()
    {
        foreach (var hook in Resources.FindObjectsOfTypeAll<AiHook>())
            foreach (var field in HookMaps)
                if (field != null && field.DeclaringType.IsInstanceOfType(hook))
                    DropDestroyed(field.GetValue(hook) as IDictionary, field.DeclaringType == typeof(ProgressStatChangeHook) ? DisposeProgress : null);
    }

    private static void DropCooldowns() => Cooling.RemoveWhere(DoneCooling);

    private static void DropPlayers()
    {
#pragma warning disable CS0618 // obsolete, but the server still runs it
        DropDestroyed(ServerDiagnosticsManager.PlayersToSendTo);
#pragma warning restore CS0618
        DropHandlers(LocalPlayerSet, null, IsDestroyed);
        DropHandlers(CustomizationTrigger, null, transition => IsDestroyed(TransitionPlayer?.GetValue(transition)));
        DropHandlers(SettingsChanged, SettingsManager.GameConfig, target => target is ControllerDPad && IsDestroyed(DPadInput?.GetValue(target)));
        // Only read again while converting the same command's arguments.
        if (PlayerList.LastList?.Any(IsDestroyed) == true)
            LastNamed?.SetValue(null, PlayerList.LastList.Where(player => !IsDestroyed(player)).ToList());
        // ChunkSync.Start sets both again when it reuses a pooled sync.
        if (SyncPool?.GetValue(null) is IEnumerable pooled)
            foreach (var sync in pooled)
            {
                SyncPlayer?.SetValue(sync, null);
                SyncChunk?.SetValue(sync, null);
            }
        // The sync loop looks up the player of each pending disconnect, so players are only dropped while none is pending.
        if (SyncDisconnects?.GetValue(null) is HashSet<Alta.Networking.Connection> { Count: 0 })
            DropDestroyed(SyncPlayers?.GetValue(null) as IDictionary);
    }

    // Content being loaded is subscribed before its manager has it, so contents are only dropped while none is loading.
    private static void DropStaleHandlers()
    {
        DropHandlers(ApplicationQuit, null, IsDestroyed);
        Loaded.Clear();
        var loading = false;
        foreach (var type in ContentManagers)
        {
            var manager = AccessTools.Property(type, "Instance")?.GetValue(null);
            if (manager == null) continue;
            loading |= AccessTools.Field(type, "getContentTasks")?.GetValue(manager) is ICollection { Count: > 0 };
            if (AccessTools.Field(type, "contents")?.GetValue(manager) is IDictionary contents)
                foreach (var content in contents.Values) Loaded.Add(content);
        }
        DropHandlers(PlayerJoined, ServerHandler.Current, target => IsDestroyed(target) ||
            !loading && target is PlayerGoldBankAccount or PlayerPostboxStorage or ClimbingTowerTeleporterSave && !Loaded.Contains(target));
    }

    private static void EmptyActionPools()
    {
        if (ComponentAction == null) return;
        foreach (var component in Resources.FindObjectsOfTypeAll<ActionComponent>())
        {
            var action = ComponentAction.GetValue(component);
            if (action == null) continue;
            var type = action.GetType();
            if (!Pools.TryGetValue(type, out var pool)) Pools[type] = pool = AccessTools.Field(type, "activePool");
            if (pool?.GetValue(action) is ICollection { Count: > 0 } stack)
                AccessTools.Method(stack.GetType(), "Clear")?.Invoke(stack, null);
        }
    }

    // A pooled tool starts with no entity and gets one and its hit handlers on each use (they're cleared before release,
    // except when SeedSpray is disabled mid-shot); the messages are set before each write; the targets are what
    // ForceUpdateCollector drops as it meets them.
    private static void DropLastUsers()
    {
        foreach (var tool in Resources.FindObjectsOfTypeAll<ImpactTool>())
            if (IsDestroyed(tool.Entity) && tool.TryGetComponent(out PooledObject pooled) && pooled.IsReleased)
            {
                tool.Entity = null;
                tool.ClearHitEvents();
            }
        foreach (var field in LastEntities)
            if (IsDestroyed(field?.GetValue(null))) field.SetValue(null, null);
        if (CollectorTargets?.GetValue(null) is LinkedList<ITarget> targets)
            for (var node = targets.First; node != null;)
            {
                var next = node.Next;
                if (!node.Value.Exists()) targets.Remove(node);
                node = next;
            }
    }

    // Removes the entries whose key is a destroyed Unity object, disposing each value first if given a Dispose(bool).
    private static void DropDestroyed(IDictionary map, System.Reflection.MethodInfo dispose = null)
    {
        if (map == null) return;
        foreach (DictionaryEntry kv in map)
            if (IsDestroyed(kv.Key)) Gone.Add(kv);
        foreach (var kv in Gone)
        {
            dispose?.Invoke(kv.Value, KeepStat);
            map.Remove(kv.Key);
        }
        Gone.Clear();
    }

    // Removes the handlers of an event (static if no owner) whose target is gone.
    private static void DropHandlers(System.Reflection.FieldInfo field, object owner, Func<object, bool> gone)
    {
        if (field == null || owner == null && !field.IsStatic || field.GetValue(owner) is not Delegate handlers) return;
        var kept = handlers;
        foreach (var handler in handlers.GetInvocationList())
            if (handler.Target != null && gone(handler.Target)) kept = Delegate.Remove(kept, handler);
        if (kept != handlers) field.SetValue(owner, kept);
    }

    private static bool IsDestroyed(object o) => o is Object u && u == null;
}
