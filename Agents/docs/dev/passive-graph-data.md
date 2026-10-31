# Read-only passive graph analysis (3.29.3b)

This layer parses and exports passive topology from the pinned client archive.
Generated files are analysis artifacts under `data/`; they provide no installed gameplay support.
The exporter never writes `Client/Content.ggpk` and has no apply, restore, recovery or installed-graph check modes.
Standard startup must preserve the installed 3.29.3b archive.
Any client archive mutation requires separate maintainer agreement.
The [selection layer](../game/ascendancies.md) consumes JSON topology for server validation and command-based lifecycle.
Exporting topology does not enable gameplay admission.
Antiquarian's separately opted-in read-only archive check and native acceptance are in [its audit](../game/antiquarian.md).

## Inputs and generated data

Run commands from the repository root with the standalone 3.29.3b client and the dependencies in
[GettingStarted](../../GettingStarted.md#what-you-need).
The exporter uses the existing Node/ooz-wasm worker and `data/tables/PassiveSkills.json` from table extraction.
Production paths remain relative to the repository root.

```powershell
.\scripts\dev\extract-data.ps1 -Only passive-graphs
# Equivalent tool invocation, after the tables have been extracted:
dotnet run --project scripts/re/passive-graphs -- Client/Content.ggpk data/passive-skill-graphs.json
```

The `passive-graphs` manifest entry is version 5 and depends on `tables`.
It writes two ignored analysis artifacts:

- `data/passive-skill-graphs.json`: revision, `analysisOnly: true`, source descriptions, hashes, roots, orbit sizes and
  sorted undirected adjacency for the ordinary and derived Phrecia graphs. Version 5 also exports each group's `x`/`y`
  and each node's `orbit`/`orbitIndex`, which the Gateway uses only to compute jewel radii (no graph is installed).
- `data/phrecia-passive-skillgraph.psg`: the matching merged graph, referenced by `mergedGraphFile` in the JSON.
  This file is not installed or consumed by gameplay.

Output is restricted to these two files under this checkout's `data/`, using ordinary files and directories.
Outputs cannot overwrite the input archive.
The artifact name is retained from the source implementation; it does not imply native installation.
`GameDataFiles.cs` lists both outputs to keep the extraction contract in step with the manifest.
The selection layer consumes the JSON for server connectivity; the PSG remains an uninstalled analysis artifact.

The exporter pins the ordinary source `metadata/passiveskillgraph.psg` to SHA-256
`82D82CD3CD2DAD81606AF0DA9D18FCD53B2E92CF006550947D65A4F7E102DC32` and the original alternate source
`metadata/alternateskillgraphs/customascendancypassiveskillgraph.psg` to
`9A6D43A997BE44B53FC13FB6CED2E9F935AA11F8E4C4940CE8ECF9300A2FC1BC`.
Version 4 also accepts the exact maintained combined alternate hash
`F2766B5A4618CDBE7E2993973B4D653F155F71BDFE0E5D063A425196CC93D638` as an input.
Hashes are checked against bytes actually read from the archive before exporting anything.
Any other source is refused; the exporter does not restore it or substitute a backup and claim it was original.
The original ordinary graph has 2,987 nodes and the original alternate graph has 2,928.
Family is needed when interpreting shared start IDs because their outgoing connections differ.

The shipped alternate graph predates part of the ordinary/Bloodline tree.
`AdditionalAscendancyGraph.Merge` retains the current ordinary/Bloodline nodes and layout, replacing only main
ascendancy nodes identified through `PassiveSkills.AscendancyKey` and `PassiveSkillGraphId`.
It rejects incompatible kind/orbits and nonzero group associations without an explicit merge rule.
The source audit records a merged result with 2,957 nodes, 778 groups and 3,312 directed drawing edges, SHA-256
`F2766B5A4618CDBE7E2993973B4D653F155F71BDFE0E5D063A425196CC93D638`.
The exported `phrecia.source` explicitly identifies derived data; its hash describes the derived artifact.
`originalPhreciaSha256` retains the pinned original hash even when the separately verified maintained graph is input.
Export verifies lossless decode/encode round trips before publishing output.

## Packed PSG format

`PassiveSkillGraph` reads and writes the packed PoE1 version-3 layout, little-endian throughout:

```text
u8 version=3, u8 kind, u8 orbitCount, orbitCount*u8 orbitSizes
u32 rootCount, rootCount*u32 rootIds
u32 groupCount
per group:
  f32 x, f32 y, u32 association, u32 backgroundOverride, u8 jewelPositionReference
  u32 nodeCount
  per node:
    u32 id, u32 orbit, u32 orbitIndex, u32 connectionCount, connectionCount*u32 targetIds
```

File connections are directed drawing edges; `Adjacency()` includes both directions and deduplicates neighbours.
Ownership and other node metadata come from `PassiveSkills.dat`, joined by the graph node ID.
The reader rejects unsupported versions, impossible counts, truncation, trailing bytes, duplicate nodes, missing
roots and missing edge targets.
This binary format is separate from the existing passive packets in
[15-progression.md](../protocol/15-progression.md#2-the-passive-skill-tree).
Read-only `GameFileSystem` disposal also releases the underlying LibGGPK3 stream.

## Verification boundary

The parser, merger and their tests are preserved from the completed PR206 source snapshot
`8e03f49e87fd223b87ca9181ffc0e2d89c122b3a`.
The CLI is reduced to read-only export with checked original sources and output restricted to `data/`.
Archive-writing classes, CLI maintenance modes and their dependencies are excluded.
Version 5 was run read-only against the actual maintained archive: ordinary 2,987 nodes/797 groups/3,359 drawing edges,
and combined alternate 2,957 nodes/778 groups/3,312 drawing edges, with exact round trips and group geometry.
The archive was unchanged; this does not install a graph or relax gameplay admission.

`tests/GameServer.Data.Tests` covers graph round trips and malformed input, incoming-edge adjacency, and merge
preservation/idempotence with incompatible-orbit refusal.
These tests do not need a copied client.
