# Roadmap: Setup, tooling and tests

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/setup-and-tooling.md](../status/setup-and-tooling.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 5. Project

- Define native graph integration separately from the [read-only analysis exporter](../docs/dev/passive-graph-data.md).
  Standard startup must preserve the installed 3.29.3b client archive.
  Any client archive mutation requires separate maintainer agreement; automatic graph installation is not approved.
  Analysis artifacts alone provide no installed gameplay support or native proof.
- Generate the Gateway's C→S length table from [c2s.md](../docs/protocol/c2s.md) (285 ids with an exact length;
  `KnownClientLength` has 66).
  `SplitClientMessages` drops the rest of a read at an unknown id, so every message after it in that read is lost.
- Mods without rebuilding the Gateway, after [hot reload](../status/setup-and-tooling.md).
  A `GameServer.Modding` contracts assembly is the only API mods see; each mod is a folder `mods/<id>/` with a
  `mod.json` (id, version, kind `league`, `feature` or `script`, dependencies), loaded on demand into its own
  collectible `AssemblyLoadContext` and reloaded when its files change.
  Hooks (tick, packet, chat command, area generated, monster killed, drop and stat modifiers, catalog overrides) are
  registrations the host owns and drops on unload; a mod's in-memory state resets on reload, saved data stays.
  Loose `.cs` files in a mod folder compile with Roslyn on save (chat commands first).
  Every league becomes one league module that names the feature modules it enables (Harvest, Delve, ...); Ruthless
  moves out of `Program.LeagueModes.cs` first, then legacy and custom leagues.
  Standard stays built in and cannot be turned off: a character whose league module is missing plays in Standard.
  Existing systems move out of the static `Program` one at a time behind the contracts API.
- Rebuild the reverse-engineering setup from a fresh clone (Ghidra import, REA, Frida) and fix whatever the guide
  misses.
  Normal launch, native client control and extraction now support a configured external client folder.
  They also support a separate runtime folder for build output, data, logs and active extraction caches.
  Remaining manual RE recipes still require explicit file paths; see GettingStarted's external-client section.
- Unnamed fields: [04](../docs/protocol/04-player-entity.md), [05](../docs/protocol/05-inventories-and-ui.md) and
  [09](../docs/protocol/09-area-change.md) list fields whose meaning is unknown.
  Naming them is a good first task.
