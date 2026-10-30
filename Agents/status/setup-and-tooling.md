# Status: Setup, tooling and tests

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/setup-and-tooling.md](../roadmap/setup-and-tooling.md).

## Works (verified with the real client)

- Game data extracted from the GGPK in one step: `scripts/dev/extract-data.ps1` (run by `live.ps1` on every start,
  `-NoExtract` to skip) regenerates each data file that is missing or whose manifest version changed (stamps in
  `data/extract-stamps.json`), and the Gateway warns once at startup about any missing file.
  Code: `scripts/dev/extract-data.ps1`, `GameDataFiles.cs`.
  Docs: [GettingStarted](../GettingStarted.md) step 7.
  Launcher, extraction and native client control also accept an external installation through ignored
  `client.local.json` or `Game_CLIENT_DIR`; the default remains repository `Client/`.
  Relative paths use the repository root, while generated data stays under `data/`.
  Native validation reached the login screen from the external folder with the dedicated profile selected.
  Read-only graph and item-type extraction succeeded against the relocated archive.
  Automated controls cover default/external paths, spaces, invalid configuration, override precedence,
  launch arguments/working directory and archive propagation to .NET/Python extraction.
  Optional `buildDirectory` or `Game_BUILD_DIR` also separates Debug binaries and intermediate files using
  .NET's artifacts layout; servers, gateway selection and all extraction helpers execute that same build.
  Optional `runtimeDirectory` or `Game_RUNTIME_DIR` moves the working directory, data, logs and active extraction
  caches outside source; omitted settings retain the original layout.
  Startup creates required runtime directories and extraction dependencies.
  Explicit migration verifies every copy before preserving the originals in a separate backup, refuses collisions
  and running processes, and leaves the tracked schema in source.
  Automated controls exercise full startup, `-Only Gateway`, `-NoBuild`, `-ClientOnly` and missing-DLL preflight.
  Gateway test data helpers use the shared test environment's runtime root, allowing sibling build output
  without moving data or changing production catalog paths.
  Tracked files the servers read (the level generator's gate baseline, `config/ruthless.json`) resolve against the
  source checkout; server-side level generation and capture tours use the configured client.
  Native validation reached the login screen with external server DLLs and verified the actual server/client
  working directories; read-only atlas and Python-to-GGPK extraction also ran from external DLLs.
  See [external client folder](../GettingStarted.md#optional-external-client-folder) for configuration and limits.
  See [separate build folder](../GettingStarted.md#optional-separate-build-folder) for build and test commands.
  See [separate runtime folder](../GettingStarted.md#optional-separate-runtime-folder) for setup and migration.
- Local patching server: the client never contacts the official patch servers.
  Code: `GameServer.Patch`.
  Docs: [01](../docs/protocol/01-login.md) §1, [GettingStarted](../GettingStarted.md) step 6.
- Dev account service: register, log in, create characters (JSON-lines over TCP).
  Code: `GameServer.Host`, `GameServer.DevClient`.
  Docs: [dev-server.md](../docs/dev-server.md).

## Partly working

- Read-only passive graph tooling exports pinned 3.29.3b topology and a merged PSG as `data/` analysis artifacts.
  Parsing and merging have automated round-trip, malformed-input and ordinary-tree preservation coverage.
  The exporter refuses modified source graphs and never installs its output into the client archive.
  This is data tooling only; Gateway consumers and native integration remain later work.
  This standalone layer has no new native launch proof; client archive mutation requires separate agreement.
  Code: `PassiveSkillGraph.cs`, `AdditionalAscendancyGraph.cs`, `GameFileSystem.cs`, `scripts/re/passive-graphs`.
  Docs: [passive graph data guide](../docs/dev/passive-graph-data.md).

- Debugging aids: timestamped server logs, whole exceptions with stack traces (a failing packet handler names its
  messages), the reason on every `DISCONNECT` line, archived logs of the last 10 full runs, each connection's last 256
  packets (printed after an error or an unexplained disconnect, dumped to `logs\packets`, `/lastpackets`), `Game_TRACE`
  categories, `scripts/dev/bugreport.ps1` and `scripts/re/frida/trace-crash.js`.
  Unit-tested (`ServerLogTests`, `PacketHistoryTests`), the archive pruning and the bug report (logs held open, names
  in every form) checked with scripts; a live run that catches a client crash with the dumps and `trace-crash.js` is
  still to do, and so is checking that `trace-crash.js` reads the client's receive pump right.
  Code: `ServerLog.cs`, `PacketHistory.cs`, `Program.Connection.cs`, `scripts/dev/live.ps1`.
  Docs: [debugging](../docs/dev/debugging.md).

## Not working yet

Nothing yet.

## Tests

`dotnet test tests/GameServer.Core.Tests` (378 tests), `tests/GameServer.Data.Tests` (15) and
`tests/GameServer.Gateway.Tests` (376: currency on items (from the inventory or a stash tab), the currency tab's
crafting slot, gem levels, gem requirements (level and attributes) and the level stall, the level-up stopped between
two levels, the gem level-up and lock messages and the saved lock flag, quality and variants, stable gem skill ids,
the save database and its transactions, stash tab rules, Ctrl + click moves, item moves with failing saves, inventory
moves, stacks, the start weapon on the ground, flasks, arrival points, instance ground items and monsters, quests,
passives, spell suppression, attack speed, critical strikes, accuracy, armour/evasion, item mods, auras, damage
conversion and the account stash) pass.
Tests that need the real client pass quietly without `Client/`.

Teleport-name controls use an unmapped synthetic destination, so real WorldAreas data cannot change their labels.
Bow hit-recovery controls explicitly start their connection clock before checking input admission.
These fixture corrections leave production catalogs and the shared module initializer unchanged.

## Tooling

Everything used to work out the protocol is in the repository: the Ghidra, Frida and Python scripts in `scripts/re/`,
our own tools in `Tools/`, and how to install the third-party ones in [GettingStarted](../GettingStarted.md)
("Third-party tools").
The full reverse-engineering setup has not yet been rebuilt from a fresh clone;
if something is missing, fix the guide.

Every network message of the client is listed, in both directions, generated from the binary.
S→C: [schema.md](../docs/protocol/schema.md), the four factories (instance 272, world 63, login 27, patch 6 classes),
each equal to its factory switch's explicit cases.
C→S: [c2s.md](../docs/protocol/c2s.md), instance 352, login 28 and patch 4 ids, from every caller of the stream flush.
Its exact lengths match all 61 comparable ids of the Gateway's `KnownClientLength`.
Code: `scripts/re/ghidra/ReadSchema.java`, `SendSites.java`, `scripts/re/schema_md.py`, `c2s_md.py`.

Optional [native client control](../docs/dev/client-control.md) provides CLI/JSON RPC for development without repeated
UI input.
It targets the copied standalone 3.29.3b client and local services only.
Login keeps waiting within its existing timeout when success 0 precedes authenticated state 3.
Mocked execute/RPC regressions cover this wait, rejection 6 and timeouts; this fix has no new native verification.

## Doc caveats

`docs/re/003`–`004` are early notes with known errors (for example "login is HTTPS"; it is custom TCP, see `re/007`).
Treat them as history, not reference.
