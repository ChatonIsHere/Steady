using System;
using MelonLoader;

namespace Steady;

// [Steady] in UserData/MelonPreferences.cfg. Changes apply on the next start.
internal static class SteadyConfig
{
    private static MelonPreferences_Entry<bool> _forcedGc;
    private static MelonPreferences_Entry<bool> _spreadPlayerSaves;
    private static MelonPreferences_Entry<float> _playerSaveBudgetMs;
    private static MelonPreferences_Entry<bool> _fastSpawnCheck;
    private static MelonPreferences_Entry<bool> _suppressFinalizers;
    private static MelonPreferences_Entry<int> _receivePoolSize;
    private static MelonPreferences_Entry<string> _logLevel;
    private static MelonPreferences_Entry<bool> _quietPathfinding;
    private static MelonPreferences_Entry<bool> _rebuiltTilesOnly;
    private static MelonPreferences_Entry<bool> _singlePassSaves;
    private static MelonPreferences_Entry<bool> _skipUnchangedChunks;
    private static MelonPreferences_Entry<int> _maxPhysicsHz;
    private static MelonPreferences_Entry<int> _maxCatchUpMs;
    private static MelonPreferences_Entry<int> _jobWorkers;

    public static bool ForcedGc => _forcedGc.Value;
    public static bool SpreadPlayerSaves => _spreadPlayerSaves.Value;
    public static double PlayerSaveBudgetMs => Math.Max(0f, _playerSaveBudgetMs.Value);
    public static bool FastSpawnCheck => _fastSpawnCheck.Value;
    public static bool SuppressFinalizers => _suppressFinalizers.Value;
    public static int ReceivePoolSize => Math.Max(0, _receivePoolSize.Value);
    public static string LogLevel => _logLevel.Value;
    public static bool QuietPathfinding => _quietPathfinding.Value;
    public static bool RebuiltTilesOnly => _rebuiltTilesOnly.Value;
    public static bool SinglePassSaves => _singlePassSaves.Value;
    public static bool SkipUnchangedChunks => _skipUnchangedChunks.Value;
    public static int MaxPhysicsHz => Math.Max(0, _maxPhysicsHz.Value);
    public static int MaxCatchUpMs => Math.Max(0, _maxCatchUpMs.Value);
    public static int JobWorkers => Math.Max(0, _jobWorkers.Value);

    public static void Init()
    {
        var category = MelonPreferences.CreateCategory("Steady", "Steady");
        _forcedGc = category.CreateEntry("ForcedGc", false, description: "Keep the game's full garbage collection every 30 s on servers. It freezes the server for 0.2-3 s each time; incremental GC does the job without it.");
        _spreadPlayerSaves = category.CreateEntry("SpreadPlayerSaves", true, description: "Save players over the frames after an autosave instead of all in one frame. Instant saves and shutdown still save everyone at once.");
        _playerSaveBudgetMs = category.CreateEntry("PlayerSaveBudgetMs", 2f, description: "With SpreadPlayerSaves, ms per frame to spend saving players (at least one player a frame).");
        _fastSpawnCheck = category.CreateEntry("FastSpawnCheck", true, description: "Check which players an entity is spawned for with a set lookup instead of a scan that allocates, on every network send.");
        _suppressFinalizers = category.CreateEntry("SuppressFinalizers", true, description: "Skip the finalizer of the game's stream readers and writers once they're disposed, so the GC frees them straight away.");
        _receivePoolSize = category.CreateEntry("ReceivePoolSize", 400, description: "64 KB network receive buffers kept for reuse (the game keeps 100 and throws the rest away after a burst).");
        _logLevel = category.CreateEntry("LogLevel", "Info", description: "Lowest level the game logs to its log file and console: Debug (the game's own), Info, Warn or Error. Console command replies are unaffected.");
        _quietPathfinding = category.CreateEntry("QuietPathfinding", true, description: "Only log pathfinding errors, not every path found.");
        _rebuiltTilesOnly = category.CreateEntry("RebuiltTilesOnly", true, description: "Servers: after a navmesh update, re-cut only the tiles it rebuilt instead of the whole map (a 2-3 s freeze each time a forest chunk loads).");
        _singlePassSaves = category.CreateEntry("SinglePassSaves", true, description: "Servers: serialize each saved component once instead of twice (once to measure, once to write). The save files are byte-for-byte the same.");
        _skipUnchangedChunks = category.CreateEntry("SkipUnchangedChunks", true, description: "Servers: don't rewrite a chunk file when it would come out the same as the file on disk apart from the save date.");
        _maxPhysicsHz = category.CreateEntry("MaxPhysicsHz", 0, description: "Servers: cap physics steps per second (0 keeps the game's 45). Lower saves CPU on weak machines but makes physics coarser.");
        _maxCatchUpMs = category.CreateEntry("MaxCatchUpMs", 0, description: "Longest frame Unity simulates in full after a hitch (0 keeps Unity's 333). Lower ends hitches sooner, but game time slows during them.");
        _jobWorkers = category.CreateEntry("JobWorkers", 0, description: "Unity job worker threads (0 keeps Unity's choice, one per core). Under a container CPU limit, set it to the limit minus one.");
        // MelonLoader only saves on a clean exit, which a killed server never gets.
        category.SaveToFile(false);
    }
}
