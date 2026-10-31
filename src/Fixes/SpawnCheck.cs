using Alta.Networking;
using Alta.Networking.Scripts.Player;
using HarmonyLib;

namespace Steady.Fixes;

// SceneSerializer asks IsEntitySpawnedFor for every sending entity, for every connection, every send.
// The game answers with LINQ Contains over a HashSet<Player> typed as IEnumerable<IPlayer>, which can't use
// the set, so it allocates an enumerator and walks every player. Same answer from the set itself (its
// comparer is reference equality).
internal static class SpawnCheck
{
    private static readonly AccessTools.FieldRef<EntitySerializer, NetworkEntity> Entity =
        AccessTools.FieldRefAccess<EntitySerializer, NetworkEntity>("entity");

    public static void Apply()
    {
        if (!SteadyConfig.FastSpawnCheck) return;
        if (SteadyMod.Patch(AccessTools.Method(typeof(EntitySerializer), nameof(EntitySerializer.IsEntitySpawnedFor)), "FastSpawnCheck", typeof(SpawnCheck), nameof(IsSpawnedFor)))
            SteadyMod.Active.Add("fast spawn check");
    }

    private static bool IsSpawnedFor(EntitySerializer __instance, IPlayer player, ref bool __result)
    {
        var chunker = (Entity(__instance) as IChunkedEntity)?.Chunker;
        if (chunker == null || player is not Player p) return true;
        __result = chunker.SpawnedForPlayers.Contains(p);
        return false;
    }
}
