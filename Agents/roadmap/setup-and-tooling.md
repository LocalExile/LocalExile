# Roadmap: Setup, tooling and tests

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/setup-and-tooling.md](../status/setup-and-tooling.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 5. Project

- Define native graph integration separately from the [read-only analysis exporter](../docs/dev/passive-graph-data.md).
  Standard startup must preserve the installed 3.29.3b client archive.
  Any client archive mutation requires separate maintainer agreement; automatic graph installation is not approved.
  Analysis artifacts alone provide no installed gameplay support or native proof.
- Rebuild the reverse-engineering setup from a fresh clone (Ghidra import, REA, Frida) and fix whatever the guide
  misses.
  Normal launch, native client control and extraction now support a configured external client folder.
  Remaining manual RE recipes still require explicit file paths; see GettingStarted's external-client section.
- Unnamed fields: [04](../docs/protocol/04-player-entity.md), [05](../docs/protocol/05-inventories-and-ui.md) and
  [09](../docs/protocol/09-area-change.md) list fields whose meaning is unknown.
  Naming them is a good first task.
