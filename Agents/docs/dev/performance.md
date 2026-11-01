# Server performance: how to measure, and what was measured

Several server stacks (Auth, Patch, Gateway) often run side by side on one machine, one per worktree or agent, so a
stack's startup time, its resident memory and its CPU per busy instance all count. This note gives the repeatable
measurements and the results of the 2026-10-30 pass (branch `contrib/server-perf`).

## Measuring

**Startup and idle memory**: `scripts/perf/startup.ps1` (after `dotnet build`). It starts the built Auth, then the built
Gateway, on a private port block (`-PortBase`, default 23000: login +1, Auth control +2, Gateway +3, dev +4) with a copy
of `data\validation-saves.db`, times each to its "listening" line, waits `-SettleSeconds`, records private bytes,
working set and the GC heap and committed size (through `dotnet-counters`, when `artifacts\tools` has it:
`dotnet tool install dotnet-counters --tool-path artifacts/tools`), and stops only the processes it started. Run 1 is a
stack's first start (the Gateway seeds the game-data reference tables into the database); the medians use the
restarts. `-Env @{ NAME = 'value' }` tries runtime settings (`DOTNET_GCConserveMemory`, â€¦). It never binds the default
ports and never starts a client.

**A whole idle stack**: `scripts/perf/stack.ps1 [-Configuration Release]` starts the built Auth, Patch and Gateway on
PortBase+1 â€¦ +6, and after they are ready records each one's private bytes (its commit), working set and CPU time over
an idle minute (`-IdleSeconds`). `scripts/perf/watch.ps1` does the same for a Gateway under `dotnet watch` (as
`live.ps1` runs it by default) and lists every process of that tree. Neither binds the default ports or starts a client.
Each perf script takes `-PortBase`, a block of 10 ports of the worktree's own (defaults: stack.ps1 23000, watch.ps1
23010, startup.ps1 23020, which do not overlap), and leaves the shell's location and environment as they were.

**In-process scenarios**: `scripts/perf/PerfBench`, a console tool (not a test: it waits on real time) that reaches the
Gateway's and the game module's internals:

```
$env:Game_PERF_LABEL = 'my-change'
dotnet run -c Release --project scripts/perf/PerfBench -- area
```

One scenario per process (catalogues load once per process). Results go to stdout and, as JSON lines, to
`artifacts/perf/bench.jsonl`.

| Scenario | What it measures |
|---|---|
| `catalogue` | the first-player loads in a fixed order (Program's statics, stats, items, skills, the skill-type sets, monsters, packs, area transitions, waypoints, the passive tree): time, allocation and what each retains |
| `area` | six supported areas at seeds nobody asked for before: the layout (server level generation, `AreaLayouts.For`) and the monster population over a loopback connection; the GGPK caches and the heap afterwards. `Game_PERF_MORE_AREAS=N` adds N more layouts, as a long session visits them |
| `busy` | The Coast with 60 monsters of its pack kinds (and a Sand Spitter, for projectiles) around a walking player and 140 idle ones farther away; the real `RunMonsterAi` loop with swings, contacts and projectiles, every packet encrypted and sent. Process CPU per game second, measured after a 5 s warm-up (a fresh process first spends seconds in tier-1 JIT) |
| `planner` | `PlanMonsterAi` alone over the same crowd, 2000 steps: time, process CPU and allocation per step |
| `single` | one player's Gateway: the startup loads (`GameData.Open`), then the town, a combat area (The Coast, its packs) and a boss area (The Twilight Strand, Hillock), each entered and held as a connection does; after each, the heap, its large-object part and private bytes. The runtime files come from the extracted game pack |
| `idle` | one player standing in town with the per-connection loops a connection starts (recovery, player tick, charges, rage, server frames, monster AI â€¦): process CPU, allocation and bytes sent per second |

The busy instance is the same in every run (fixed placement, no buried monsters, which rise on a wall-clock watch);
`PopulateArea` is not, because it seeds placement with the process's randomized `HashCode`. Timings on a shared
machine vary by Â±20 %: compare several runs, and prefer allocation, which is exact.

**Profiles**: set `DOTNET_EnableEventPipe=1`, `DOTNET_EventPipeOutputPath=â€¦\trace-{pid}.nettrace` and
`DOTNET_EventPipeConfig=Microsoft-DotNETCore-SampleProfiler:0:5,Microsoft-Windows-DotNETRuntime:0x4c14fccbd:5` for a
run: CPU samples and allocation ticks with stacks (read them with TraceEvent or `dotnet-trace report`).
`Game_PERF_HOLD=1` keeps the area and single-player scenarios' state alive for `dotnet-gcdump collect`; a heap dump's
largest objects and what holds them show where the memory is (the pass below found most of it that way).

## Results (2026-10-30, this branch against origin/main 89b7aa90, same machine)

| Measure | Before | After |
|---|---|---|
| Gateway private bytes after startup (Debug, as `live.ps1` runs it) | 818 MB | 413-433 MB |
| Gateway GC committed after startup | 702 MB | 387-408 MB |
| Gateway ready (to "listening") | 2.4 s | 2.4-2.5 s |
| Auth ready / private bytes | 0.3 s / 36 MB | unchanged |
| First area layout (opens the GGPK) | 3.3 s, 13.2 GB allocated | 1.2 s, 0.49 GB |
| Area layout 1_1_3 / 1_1_4_1 / 1_1_5 / 1_1_7_1 / 1_2_1 | 0.80 / 1.83 / 1.27 / 1.39 / 1.73 s | 0.41 / 0.57 / 0.54 / 0.35 / 0.22 s |
| â€¦ allocated per layout | 4.4-8.0 GB | 84-166 MB |
| First area population (the monster catalogues load) | 0.95-0.97 s, 414-477 MB | 0.83-0.87 s, 357-431 MB |
| Catalogue loads, Program statics | 1.37 s, 816 MB allocated | 1.17-1.27 s, 572 MB |
| Skill-type sets of a skill catalogue | 0.30 s, 105 MB allocated | 0.11 s, 13 MB |
| `IsRangedAttack` (three type sets) | 200 ns, 133 B per call | 33 ns, 0 B |
| GGPK bundle cache ceiling (per Gateway) | 1 GiB | 128 MB (+ 64 MB decompressed chunks) |
| Generated levels kept (46-area session) | 24, 265 MB of grids | 8 most recently used (103-116 MB), plus any level a connection still plays |
| Levelgen gate, 122 checks, 2 workers | 30 s | 9 s (same results) |
| `PlanMonsterAi`, 200 monsters, 60 chasing (Release) | 1.34-1.37 ms per step | 1.04 ms (`MonsterSurround.Assign` 0.96 -> 0.48 ms) |
| Busy instance CPU (Release; Debug ~1.6Ã—) | 78-160 ms per game second | within that noise |

What changed: `BundleFile.DecompressSlice` copied every chunk of a bundle for each file read (88 % of a level
generation's CPU); `GameFileSystem` keeps the last 64 MB of decompressed chunks and serves a file whose chunks are all
cached without its bundle, so neighbouring terrain files do not go through the Node Oodle worker again; the Gateway asks
for a 128 MB bundle cache and keeps 8 generated levels for re-entries (a layout in use holds its level, so the
projectile and height lookups never regenerate it, and they take no lock); one aggressive full collection after the startup loads returns
the memory their parsing left committed; `AreaLayouts.DeepestCell` no longer allocates an array per cell;
`SkillCatalog` parses the skill-type tables once and resolves each set once; `GameTables` shares the parse of
Stats.json, Mods.json, MonsterVarieties.json and GrantedEffects.json among the catalogues that load them in one burst;
`MonsterSurround.Assign` checks a refused cell once per assignment.

The GGPK rows of these numbers predate `data/game-files.zip`: the Gateway now generates levels from that pack, with no
bundle cache or Node worker (`GamePack`), and its layout timings have not been measured again. The chunk and bundle
caches still serve the tools that read the GGPK.

Tried and dropped (no measurable effect): `DOTNET_GCConserveMemory` (5, 7), `DOTNET_GCgen0size`,
`DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=0`, shift-based bucket math and span loops in `BlockerSet.Query`, skipping
the pathfinder for a swing's stay-in-place order (they are a few per step and the search ends at once), skipping
duplicate ring cells and caching ground checks in the surround search, the managed Oodle decoder (it cannot decode
Selkie, which the terrain bundles use). Moving the startup collection before the Auth notification (and so before the
mods load) left the Gateway at 494 MB instead of 413 MB.

Measuring memory in-process: `ArrayPool<byte>.Shared` keeps the buffers `JsonDocument` rents, so the live heap after a
full collection varies by a few hundred MB between identical runs; compare sizes the benchmarks read directly (caches,
kept levels) or the server's own counters.

## Current integration

The parse-buffer isolation, lighter extraction-tool GGPK index and `live.ps1 -Release` remain applicable.
The Gateway now reads `data/game-files.zip` and `data/client-constants.json`, without opening the client archive or
starting an Oodle worker. The old runtime decoder timer, bundle release and HNC executable read have been removed.
The measurements below predate that migration; they are historical comparisons, not measurements of current main.
The `single` and `area` scenarios use the extracted pack and no longer measure runtime decoder caches.

## One player per stack (2026-10-31, before the extracted-pack migration)

The target is one server stack and one client per worktree, eight of them on one PC, so a stack's idle and
single-player cost counts eight times. Measured against `contrib/server-perf` (cb9a6de6) on the same machine:

| Measure | Before | After |
|---|---|---|
| Idle stack, private (stack.ps1: Auth / Patch / Gateway) | 483 MB (33 / 19 / 431) | 113-115 MB (11 / 18 / 84-86) |
| Idle stack, working set | 507 MB | 203-205 MB |
| Idle stack, CPU | 47-78 ms per minute | 47-78 ms per minute (timer noise) |
| `dotnet watch` beside the Gateway (watch.ps1, before any edit) | +223 MB private, +444 MB working set, +235 ms CPU per minute | none with `live.ps1 -Release` or `-NoHotReload` |
| Oodle worker (Node, ooz-wasm) after a generation | 78 MB private, for the Gateway's life | stops a minute after the last generation (+55 ms on the next one) |
| One player (test host): startup / town / combat area / boss area, GC heap | 527 / 718 / 809 / 830 MB | 187 / 273 / 361 / 379 MB |
| â€¦ the same, private bytes | 568 / 761 / 855 / 877 MB | 223 / 311 / 401 / 412 MB |
| â€¦ in the boss area once generation has gone idle | (as above) | 347 MB heap, 372 MB private |
| One player, after merging main (the `single` scenario in its own process): GC heap | | 46 / 148 / 242 / 264 MB; 231 MB once generation is idle |
| â€¦ the same, private bytes | | 75 / 180 / 276 / 291 MB; 255 MB once generation is idle |
| One idle player in town: CPU, allocation (`idle` scenario, Release) | not measured before | 52-59 ms per second, 12-14 MB per second |

The first single-player rows were measured in the Gateway test host, which added ~115 MB of its own; after main moved
the benchmarks into `scripts/perf/PerfBench` they run in a process of their own, close to a Gateway's figures. A
one-player stack therefore takes roughly 115 MB idle and ~300 MB with its player in a combat or boss area (~380 MB for
the minute the Oodle worker runs after a generation), against ~0.7 GB idle and ~1.1 GB before (Debug with
`dotnet watch`, the worker kept): eight stacks ~2.4 GB instead of ~8.8 GB.

What changed:
- **Parse buffers.** `JsonDocument` rents its buffers from `ArrayPool<byte>.Shared`, which keeps a returned buffer in the
  returning thread's slot and per-core stacks and trims only during a later full collection, at least 30-60 s after the
  buffer's last use; a lightly loaded server rarely runs one. The startup loads left ~240 MB in the main thread
  (Mods.json's alone is 128 MB), a player's first loads another ~250 MB in connection threads. `GameTables.Isolated`
  runs a load on a thread of its own, whose pool slots go with it; the Gateway's startup (`GameData.Open`), the
  2 MB-and-larger tables (57 sites, through `GameTables.Open`), area-transitions.json, the area mods and Auth's
  WorldAreas loads use it. A periodic background collection to let the pool trim was tried first: it freed the buffers,
  but the memory stayed committed as free space between the catalogues' large objects.
- **Debug temporaries.** A Debug build keeps a temporary alive until its method returns, and `Main` runs for the
  Gateway's life: the 76 MB client executable read for the HNC mask stayed. It is now read on its own thread.
- **The GGPK index** is searched in its records sorted by hash instead of a 39 MB dictionary beside them.
- **Level generation's caches.** The compressed bundles are released after each generation (the decompressed chunks
  serve the repeated reads; ~80 MB less after a first area); the Node worker and the decompressed chunks go a minute
  after the last generation (`ServerLevels.DecoderIdle`). Everything stays per stack: no decoder, cache or game data is
  shared between stacks, whose code and tables may differ.
- **`live.ps1 -Release`** runs the Release builds without `dotnet watch`; the monster AI is 1.6-2.3x faster there, the
  memory the same (the `single` scenario within 1 MB).

Tried and dropped: `DOTNET_gcConcurrent=0`, `DOTNET_TieredPGO=0`, `DOTNET_GCConserveMemory=9`, `DOTNET_GCHighMemPercent`,
the `DOTNET_SYSTEM_BUFFERS_SHAREDARRAYPOOL_*` limits and `DOTNET_GCName=clrgc.dll` (worse) on the idle stack (all within a
few MB); releasing the decompressed chunks after every generation (+8 % layout time); the managed Oodle decoder (no
Selkie). Release builds do not lower an idle player's CPU (Debug 51, Release 58 ms per second: the loops' timers and
allocation, not JIT code quality).

## Recommendations (tooling decisions, not made here)

- **Agent stacks without hot reload.** `live.ps1` runs Debug builds under `dotnet watch` by default (+223 MB and
  ~0.25 s CPU per minute per stack before the first edit, more after one). An agent's stack that does not need hot
  reload can start with `-Release` (or `-NoHotReload`); making that the default for agent sessions is the launcher's
  decision.
- **One Oodle decoder per machine** was not done: worktrees may run different code and tables, so a shared decoder would
  have to be stateless and version-agnostic. The per-stack worker now runs only around generations instead.

## Where the remaining cost is

- **Table parsing.** Most catalogues still parse whole JSON tables with `JsonDocument` (~190 sites); WorldAreas.json is
  read by nine loaders (Auth alone four times at startup, ~0.1 s). Reading the biggest ones with `Utf8JsonReader` into
  compact rows would lower the peak memory of the first loads. Several `JsonDocument`s open at once keep their pooled
  buffers: parse one at a time.
- **Monster AI.** `PlanMonsterAi` is still ~1 ms per step for a crowd of 60: half in `MonsterSurround.Assign` (the
  free cells' reservation queries), the rest mostly `WalkEstimator.Order`'s client-pathfinder searches.
- **An idle player's loops** (58 ms CPU and 13 MB allocated per second in town): the player stat snapshot
  (`CharacterPlayerStatsSnapshot`, the whole character panel) is ~44 % of it. The energy-shield publication builds it on
  every 16 ms recovery step (~25 % of the CPU and half the allocation, measured by leaving it out); the recovery loop
  builds it three times every 250 ms (life rate, mana rate, recovery stats). The snapshot configures leech and
  fortification as it goes, so reusing one needs an invalidation scheme for every stat source, not a cache.
- **The Gateway's idle heap** (~71 MB) is mostly the mod and item catalogues `GameData.Open` loads at startup (a player
  needs them anyway); Patch is a bare ASP.NET host (18 MB).
