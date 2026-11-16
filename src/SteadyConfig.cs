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
    private static MelonPreferences_Entry<bool> _fastAreaFill;
    private static MelonPreferences_Entry<bool> _dropDeadReferences;
    private static MelonPreferences_Entry<bool> _singlePassCutChecks;
    private static MelonPreferences_Entry<bool> _scanGc;
    private static MelonPreferences_Entry<bool> _localAreaFill;
    private static MelonPreferences_Entry<bool> _fastNearestNode;
    private static MelonPreferences_Entry<float> _forestBudgetMs;
    private static MelonPreferences_Entry<float> _chunkLoadBudgetMs;
    private static MelonPreferences_Entry<bool> _caveScanFix;
    private static MelonPreferences_Entry<bool> _graphMaskFix;
    private static MelonPreferences_Entry<int> _pathThreads;
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
    public static bool FastAreaFill => _fastAreaFill.Value;
    public static bool DropDeadReferences => _dropDeadReferences.Value;
    public static bool SinglePassCutChecks => _singlePassCutChecks.Value;
    public static bool ScanGc => _scanGc.Value;
    public static bool LocalAreaFill => _localAreaFill.Value;
    public static bool FastNearestNode => _fastNearestNode.Value;
    public static double ForestBudgetMs => Math.Max(0f, _forestBudgetMs.Value);
    public static double ChunkLoadBudgetMs => Math.Max(0f, _chunkLoadBudgetMs.Value);
    public static bool CaveScanFix => _caveScanFix.Value;
    public static bool GraphMaskFix => _graphMaskFix.Value;
    public static int PathThreads => Math.Max(0, Math.Min(8, _pathThreads.Value));
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
        _fastAreaFill = category.CreateEntry("FastAreaFill", true, description: "Work out which parts of the navmesh connect (after every navmesh change) about a quarter faster. The result is the same, node for node; anything unusual uses the game's own.");
        _dropDeadReferences = category.CreateEntry("DropDeadReferences", true, description: "Servers: drop the references the game keeps to destroyed creatures, attacks and items, and to players who have left, so their memory can be freed.");
        _singlePassCutChecks = category.CreateEntry("SinglePassCutChecks", true, description: "Servers: check each navmesh cut for movement once a frame instead of twice. The same cuts are updated, in the same order.");
        _scanGc = category.CreateEntry("ScanGc", false, description: "Servers: keep the full garbage collection the pathfinding library runs after every navmesh scan (each cave layer the first time it loads). It freezes the server like the 30 s one.");
        _localAreaFill = category.CreateEntry("LocalAreaFill", true, description: "Servers: after a navmesh cut update, label only the re-cut tiles' areas instead of the whole navmesh, once it's shown no area was joined or cut in two. Nodes share an area exactly as after a full fill.");
        _fastNearestNode = category.CreateEntry("FastNearestNode", true, description: "Servers: when finding the navmesh node nearest a point, skip the graphs (the overworld, each cave layer) that can't hold a nearer one than already found. The same node is found.");
        _forestBudgetMs = category.CreateEntry("ForestBudgetMs", 4f, description: "Servers: ms a frame all loading forest chunks share for placing their trees and rocks (0 keeps the game's ten instructions a frame per chunk). The first each frame always runs.");
        _chunkLoadBudgetMs = category.CreateEntry("ChunkLoadBudgetMs", 8f, description: "Servers: ms a frame for loading the saved entities of forest and cave chunks that are ready (0 loads each as soon as it's ready, many in one frame). At least one chunk a frame.");
        _caveScanFix = category.CreateEntry("CaveScanFix", true, description: "Servers: scan cave layers' navmeshes one at a time, and retry a failed scan, instead of leaving the layer without paths (or a static cave unloaded) when two load together.");
        _graphMaskFix = category.CreateEntry("GraphMaskFix", true, description: "Servers: stop masks meant for graphs 0-31 (like the forest's 'overworld only') also taking cave graphs 32, 64 and so on.");
        _pathThreads = category.CreateEntry("PathThreads", 0, description: "Servers: pathfinding threads (0 keeps the game's one). With creatures busy in caves one thread can fall behind; each extra thread uses another core.");
        _maxPhysicsHz = category.CreateEntry("MaxPhysicsHz", 0, description: "Servers: cap physics steps per second (0 keeps the game's 45). Lower saves CPU on weak machines but makes physics coarser.");
        _maxCatchUpMs = category.CreateEntry("MaxCatchUpMs", 0, description: "Longest frame Unity simulates in full after a hitch (0 keeps Unity's 333). Lower ends hitches sooner, but game time slows during them.");
        _jobWorkers = category.CreateEntry("JobWorkers", 0, description: "Unity job worker threads (0 keeps Unity's choice, one per core). Under a container CPU limit, set it to the limit minus one.");
        // MelonLoader only saves on a clean exit, which a killed server never gets.
        category.SaveToFile(false);
    }
}
