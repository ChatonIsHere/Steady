<p align="center"><img src="assets/badge-256.png" width="128" alt=""></p>

# Steady

A MelonLoader mod that makes A Township Tale servers run smoother, and clients a little. It fixes things the
game does wastefully and leaves gameplay alone. Install it on a server, a client or both.

## Fixes

All on by default. Each has a setting under `[Steady]` in `UserData/MelonPreferences.cfg`, read at startup.
On clients only `FastAreaFill`, `ForestBudgetMs`, `SuppressFinalizers`, `ReceivePoolSize`, `LogLevel`,
`QuietPathfinding` and `CutOffAlta` apply.

### Freezes

| Setting | Fix |
| --- | --- |
| `ForcedGc` | The server forces a full GC every 30 s, a 0.2 s freeze on Windows and 2-3 s under Wine. Steady stops it and lets the game's incremental GC do the work. |
| `ScanGc` | The pathfinding library forces a full GC after each navmesh scan, such as a cave layer loading for the first time. Steady leaves it out. |
| `RebuiltTilesOnly` | Loading a forest chunk re-cuts all ~19,000 navmesh tiles instead of the few it rebuilt, a 2-3 s freeze about once a minute. Steady re-cuts only the rebuilt ones, which also stops removed trees and rocks leaving holes in the navmesh. |
| `ForestBudgetMs` | Each loading forest chunk places ten trees or rocks a frame, so twenty chunks loading together make 0.6 s frames. Steady shares a few milliseconds a frame between them. |
| `ChunkLoadBudgetMs` | Neighbouring forest and cave chunks load their saved entities in the same frame. Steady spreads them over the next frames. |
| `SpreadPlayerSaves`, `PlayerSaveBudgetMs` | Autosave saves every player in one frame. Steady spreads them out. `save now` and shutdown still save everyone at once. |

### Pathfinding

| Setting | Fix |
| --- | --- |
| `LocalAreaFill` | After each navmesh cut update (a tree falls, an item moves), the whole navmesh is relabelled to work out which parts connect: 70-80 ms, about twice a second with players around. When only re-cut tiles changed, Steady relabels just those once it has checked no area was joined or split, and falls back to the full fill otherwise. |
| `FastAreaFill` | When a full fill does run, Steady does it in tile order. Same labels, less time. |
| `FastNearestNode` | Every path looks for the nearest node in every graph. From a cave, the overworld search takes ~2 ms on the game's one pathfinding thread. Steady tries the nearest graph first and skips graphs that can't hold a nearer node. The same node is found. |
| `SinglePassCutChecks` | Every frame the server checks each navmesh cut for movement, and when one has moved it checks them all again. Steady checks each once. |
| `CaveScanFix` | Two cave layers loading together can start two navmesh scans. The second fails, so that layer's creatures can't find paths, or a static cave never finishes loading. Steady runs scans one at a time and retries a failed one. |
| `GraphMaskFix` | Graph masks have 32 bits, so cave graphs from 32 up matched any mask graph 0 did, and overworld-only updates reached them. Masks now stop at graph 31. |

### Memory and saves

| Setting | Fix |
| --- | --- |
| `DropDeadReferences` | The game holds on to destroyed creatures, attacks and items, and to players who left, so they're never freed. Steady drops those references. In a 2.5-hour test with 20 players the heap ended at 1.2 GB instead of 4 GB. |
| `SinglePassSaves` | Saving serializes every component twice, once only to measure it. Steady does it once. Save files are byte-for-byte the same. |
| `SkipUnchangedChunks` | Autosave rewrites every loaded chunk's file, though about half haven't changed. Steady skips those. |
| `FastSpawnCheck` | Each network send checks which players an entity is spawned for with a scan that allocates. Steady uses a set. |
| `SuppressFinalizers` | Disposed stream readers and writers keep their finalizer, so each survives an extra GC. |
| `ReceivePoolSize` | After a hitch, received messages overflow a pool of 100 64 KB buffers and the rest become garbage. Steady keeps up to 400. |

### Logging

| Setting | Fix |
| --- | --- |
| `LogLevel` | The game logs at Debug. Steady raises it to Info. Console replies are unaffected. Above Debug, Canary's `canary debug on` adds Trace lines only. |
| `QuietPathfinding` | Pathfinding logs every path found. Steady logs only errors. |

### Alta's web services

| Setting | Fix |
| --- | --- |
| `CutOffAlta` | Alta's web services are gone, but the game still calls them, and each call hangs until it times out (40 s or more). Steady cuts them off without standing in for Alta. The calls that ask Alta something get an empty answer: no public IP, no permission, no news or reports, no login refresh, and analytics are dropped. Any other request to Alta's hosts fails at once, the way a timeout does. A mod that answers one of these calls itself (TavernLib, TavernEssentials, AltaApiRemoval) still does, and Steady stands aside. |

Steady also removes the extra physics-rate updater each server settings reload leaves behind.

## Options

Off by default, because each trades something away.

| Setting | Default | What it does |
| --- | --- | --- |
| `MaxPhysicsHz` | 0 (the game's 45) | Caps server physics steps per second. Saves CPU; physics gets coarser. |
| `MaxCatchUpMs` | 0 (Unity's 333) | The longest frame Unity simulates in full after a hitch. Lower ends hitches sooner, but game time slows during them. |
| `JobWorkers` | 0 (one per core) | Unity job worker threads. In a container with a CPU limit, set it to the limit minus one. |
| `PathThreads` | 0 (the game's one) | Pathfinding threads. Helps when creatures in caves keep one thread busy; each extra uses another core. |

## With Cave Performance and Forest Performance

[calebv2's mods](https://github.com/calebv2/ATT-Optimizations) cover some of the same ground. When one is
installed, Steady leaves the overlapping jobs to it and says so at startup:

- Forest Performance: `LocalAreaFill`, `ForestBudgetMs`, forest chunk loads and `GraphMaskFix`.
- Cave Performance: cave chunk loads, `CaveScanFix` and `PathThreads`.

Turn the matching setting off in their mod and Steady takes the job back. `RebuiltTilesOnly`, `FastAreaFill`
and `FastNearestNode` run alongside theirs.

## Commands

`steady status` on the server console shows which fixes are on and what they've done.

With [Canary](https://github.com/ChatonIsHere/Canary) recording, Steady also sends it those counters once a second
(area fills and their time, chunk loads held and waiting, forest instructions moved, and the rest), so they show on
the viewer's timeline beside each spike. Turn it off with `CanaryMetrics`.

## Building

Set `GameDir` in `Local.props` (or pass `-p:GameDir=...`) to a game install with MelonLoader, or to a checkout
of [att-stubs](https://github.com/ChatonIsHere/att-stubs)' `full` branch, then run `dotnet build -c Release`.
Put `bin/Release/Steady.dll` in the game's `Mods` folder. Each push to main is released as `YY.M.N` (year,
month, release number that month); local builds are `0.0.0-dev`.

## Credits

`LocalAreaFill`, `FastNearestNode`, `ForestBudgetMs`, `ChunkLoadBudgetMs`, `ScanGc`, `CaveScanFix`, `GraphMaskFix` and
`PathThreads` grew out of ideas in [calebv2](https://github.com/calebv2)'s
[Cave Performance and Forest Performance](https://github.com/calebv2/ATT-Optimizations).

Icon: <a href="https://www.flaticon.com/free-icons/speedometer" title="speedometer icons">Speedometer icons created by Magnific - Flaticon</a>
