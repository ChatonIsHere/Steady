# Steady

A MelonLoader mod that makes A Township Tale servers (and, a little, clients) run smoother. Nothing visual:
it fixes what the game does wastefully and leaves gameplay alone. Install it on a server, a client or both;
neither needs the other.

## What it fixes

| Fix | Where | Setting |
| --- | --- | --- |
| The game forces a full garbage collection every 30 s on servers. Each one freezes the server (0.2 s idle on Windows, 2-3 s under Wine). Steady stops it; the incremental GC the game already has does the work in small slices. | Server | `ForcedGc` |
| Each time the server loads a forest chunk, it rebuilds that chunk's navmesh tiles, then re-cuts all ~19,000 tiles in the map instead of only the rebuilt ones: a 2-3 s freeze about once a minute once the world is loaded. Steady re-cuts only the rebuilt tiles, as the pathfinding library intends. That also stops holes from removed trees and rocks staying in the navmesh. | Server | `RebuiltTilesOnly` |
| Saving serializes every component twice: once only to measure its length, then for real. Steady serializes it once into a scratch buffer at the same bit alignment and copies it in, so the save files are byte-for-byte the same. | Server | `SinglePassSaves` |
| Every autosave rewrites the file of every loaded chunk, though about half are unchanged apart from the save date (which the game never reads). Steady compares with the file on disk and leaves unchanged ones (and their backups) alone. | Server | `SkipUnchangedChunks` |
| An autosave saves every player in one frame. Steady saves them over the next frames. `save now` and shutdown still save everyone at once. | Server | `SpreadPlayerSaves`, `PlayerSaveBudgetMs` |
| Every network send checks which players each entity is spawned for with a scan that allocates. Steady uses a set lookup with the same answer. | Server | `FastSpawnCheck` |
| The game's stream readers and writers (one per saved entity, and some network messages) keep a finalizer after they're disposed, so each survives an extra GC. | Both | `SuppressFinalizers` |
| After a hitch, received messages overflow a pool of 100 64 KB buffers and the extras become garbage. Steady keeps up to 400. | Both | `ReceivePoolSize` |
| The game logs at Debug level to its log file and console. Steady raises it to Info. Console command replies are unaffected. | Both | `LogLevel` |
| Pathfinding logs every path found. Steady logs only errors. | Both | `QuietPathfinding` |
| Each server settings reload adds another copy of the physics-rate updater. Steady removes the old one. | Server | always |

All of it is on by default.

## Options for weaker machines

These are off by default because each trades something away.

| Setting | Default | What it does |
| --- | --- | --- |
| `MaxPhysicsHz` | 0 (the game's 45) | Caps server physics steps per second. Saves CPU, but physics gets coarser. |
| `MaxCatchUpMs` | 0 (Unity's 333) | The longest frame Unity simulates in full after a hitch. Lower ends hitches sooner, but game time slows during them. |
| `JobWorkers` | 0 (one per core) | Unity's job worker threads. In a container with a CPU limit, set it to the limit minus one. |

Settings are in `[Steady]` in `UserData/MelonPreferences.cfg` and apply on the next start.

## Commands

`steady status` (server console) says which fixes are on, how many players were saved over frames, how
many receive buffers were dropped, how many unchanged chunk files were skipped, and the GC count and heap size.

## Notes

- With `LogLevel` above Debug, Canary's `canary debug on` adds Trace lines only, not Debug.
- Outside the mod, changes to the server host are tracked in [HOST.md](HOST.md).

## Building

Set `GameDir` in `Local.props` (or pass `-p:GameDir=...`) to a game install with MelonLoader, then
`dotnet build -c Release`. The DLL is `bin/Release/Steady.dll`; put it in the game's `Mods` folder.
