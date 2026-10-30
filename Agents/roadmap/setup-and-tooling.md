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
- Mods: the rest after the [mod host](../status/setup-and-tooling.md) ([modding](../docs/dev/modding.md)).
  Catalog overrides (a mod replacing rows of the Gateway's catalogs) are the hook the host does not have yet.
  Legacy and custom leagues become league modules, as Ruthless is; the Auth server's list still comes from `Game_LEAGUES`.
  Existing systems keep moving out of the static `Program` one at a time. Harvest, Heist and Delve are built-in mods; making
  them separate assemblies needs contracts for items, stash and objects. Survivors does not exist in the Gateway yet and should
  start as a module.
- Rebuild the reverse-engineering setup from a fresh clone (Ghidra import, REA, Frida) and fix whatever the guide
  misses.
- Unnamed fields: [04](../docs/protocol/04-player-entity.md), [05](../docs/protocol/05-inventories-and-ui.md) and
  [09](../docs/protocol/09-area-change.md) list fields whose meaning is unknown.
  Naming them is a good first task.
