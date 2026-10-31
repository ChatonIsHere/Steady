using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;

namespace Steady.Fixes;

// An autosave spreads chunks over frames, then saves every player (body, inventory, state) in one frame.
// This queues those player saves and works through them over the next frames instead. Instant saves
// (`save now`, shutdown) still save everyone at once, and a player who leaves first is saved by the game
// on the way out.
internal static class PlayerSaves
{
    private static readonly List<Player> Queue = new();
    private static int _instant;

    internal static int Spread;

    public static void Apply()
    {
        if (!SteadyConfig.SpreadPlayerSaves) return;
        var world = AccessTools.Method(typeof(StreamerManager), nameof(StreamerManager.SaveWorld), [typeof(bool)]);
        if (SteadyMod.Patch(world, "SpreadPlayerSaves", typeof(PlayerSaves), nameof(SaveWorldPrefix), finalizer: nameof(SaveWorldFinalizer))
            && SteadyMod.Patch(AccessTools.Method(typeof(Player), nameof(Player.SaveAll)), "SpreadPlayerSaves", typeof(PlayerSaves), nameof(SaveAllPrefix)))
            SteadyMod.Active.Add("player saves spread");
    }

    // An instant save runs start to finish inside this call, Player.SaveAll included.
    private static void SaveWorldPrefix(bool isInstant)
    {
        if (isInstant) _instant++;
    }

    private static void SaveWorldFinalizer(bool isInstant)
    {
        if (isInstant) _instant--;
    }

    private static bool SaveAllPrefix()
    {
        if (_instant > 0 || ApplicationManager.IsQuitting)
        {
            Queue.Clear();
            return true;
        }
        foreach (var player in Player.AllPlayers)
            if (player is Player p && !Queue.Contains(p))
                Queue.Add(p);
        return false;
    }

    public static void Tick()
    {
        if (Queue.Count == 0) return;
        var clock = Stopwatch.StartNew();
        do
        {
            var player = Queue[0];
            Queue.RemoveAt(0);
            // Gone, or left and already saved by the game.
            if (player == null || !Player.AllPlayers.Contains(player)) continue;
            try
            {
                player.SaveControllerAndState();
                player.WriteSave(isUnloading: false);
                Spread++;
            }
            catch (Exception e)
            {
                SteadyMod.Log.Error($"Failed saving player {player.UserInfo}: {e}");
            }
        } while (Queue.Count > 0 && clock.Elapsed.TotalMilliseconds < SteadyConfig.PlayerSaveBudgetMs);
    }
}
