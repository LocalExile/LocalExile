# Ascendancy identity and graph selection in 3.29.3b

The evidence below comes from the original `Game.exe` with SHA-256
`78A0E227189C263658A7F65A02F086428B859BFC114AE8E7CE8AAB721EBD48D5` and the two graphs extracted from its
verified standalone GGPK. Addresses use image base `0x140000000`; the login-key patch does not move them.

## Class byte

`0x142156f24..0x142156f37` and `0x141ef27a9..0x141ef27be` decode the main ascendancy as:

```text
baseClass = byte & 0x1f                 // Characters.IntegerId, not its table row
classNo   = (byte >> 6) | ((byte >> 3) & 4)
```

Zero means unascended: the first consumer branches to the base-class display when the decoded value is zero.
The earlier comparison with `7` at `0x142156e99` compares the entire byte, not the ascendancy number. It is not
an ascendancy sentinel. The inverse is `base | ((classNo & 3) << 6) | ((classNo & 4) << 3)`.
`CharacterClassByte` implements it, with exhaustive coverage of all seven base classes and all eight representable
class numbers. Only the existing data's 1..3 numbers are used by the current ordinary/Phrecia variants.

`0x142425e40` builds per-character vectors of ascendancy names. Their size follows the highest ClassNo plus one;
this particular loader does not impose a three-class maximum. That alone does not prove a six-option native chooser.

## The family is a world stat

The exact `Stats.dat` row 21145 is `league_enable_alt_ascendancies`, so the client's stat key is **21146** (`0x529a`).
`0x141ec5750` selects the character graph:

1. World `+0xd0 == 0x27` selects `PassiveSkillTrees` row 1, Royale.
2. Otherwise it reads key `0x529a` from the world's stats interface at `+0x120`.
3. Nonzero selects row 4, `DefaultAltAscendancies`, path
   `Metadata/AlternateSkillGraphs/CustomAscendancyPassiveSkillGraph`.
4. Zero selects row 0, `Default`, path `Metadata/PassiveSkillGraph`.

The same key is read by the ascendancy presentation consumers at `0x1409824e0`, `0x14099e770` and `0x140b724b0`.
It controls the existing normal/LeagueName presentation, not an additional ClassNo. The spare byte in `0x33` is
not required to select Phrecia.

The stat is supplied in **S→C `0x1d`**: flag byte 0 bit 6 enables three tail collections. They follow the existing
mandatory vectors. `0x142141a4d..0x142141b88` writes:

1. Compact unsigned count, then unsigned key / signed value pairs: packet `+0x100`.
2. Compact unsigned count, then unsigned key / signed value pairs: packet `+0x120`.
3. Byte count of further records: packet `+0x138`.

The first collection becomes the world's stats interface at `+0x120`. The full static path is the copied area
message in `0x142155140`, then `0x142157760`, world vtable slot `+0x1b8` (`0x142183130`), which assigns argument 4
to world `+0x120` at `0x142183168..0x142183172`.

`AreaInstance.LeagueStats` serializes that first collection. For Phrecia the exact tail is
`01 C0 52 9A 01 00 00` (first list has key 21146 value 1, second/final lists empty), and the first flag
byte becomes `40`. An empty dictionary preserves the old packet byte for byte. This changes the graph presented
to one connection; it does not change the character's stored league or add a native six-option chooser. A family
change must reload the area before the new graph is used.

## Point costs and multiple-choice options

Class starts are implicit. `0x141ef6040` first tests the received allocation list; if a node is absent,
`0x141ef64d5..0x141ef6535` still considers an ascendancy start allocated when its owner's ClassNo and Character
match the packed class byte. `0x141ef70d0` handles the ordinary class start. Neither start belongs in the paid
`0x33` node vector: `0x141ef69e0` increments the ascendancy counter for every explicit node with an ascendancy
owner, including an explicitly sent start. Keeping starts out of the save and wire list agrees with this native
behaviour; a collapsed/off-screen ascendancy view is not evidence that its start must be transmitted.

Runtime `PassiveSkills` has a packed layout with 16-byte foreign-row handles. `AscendancyKey` is at `+0x66`,
`SkillPointsGranted` at `+0x87`, `IsMultipleChoice` at `+0x8b`, and `IsMultipleChoiceOption` at `+0x8c`.
The remove-node function `0x141ef6bf0` decrements the ascendancy allocation count (`tree+0x280`) for a node with an
ascendancy owner; an option also decrements the free-option count (`tree+0x28c`).
`0x141ef7010` computes remaining ascendancy points from earned points (`+0x27c`) minus allocated owner nodes plus
the free-option count. Thus the parent costs one point and its chosen option costs zero. Both remain allocated.
The secondary-ascendancy equivalents are `+0x284` and `+0x290`.

An audit of all 40 extracted choices confirms unique `(family, packed class byte)` identities, one start per choice,
three normal plus three Phrecia choices per non-Scion base class, and three normal plus one Phrecia choice for Scion.
All 19 family pairs share their base class, ClassNo and start ID. This is a data/codec audit, not a live mechanics
test of every class. `AscendancyWireIdentityTests` repeats it when the exact game data is extracted.

## Exact PSG format and extraction

PoE1 version 3 is packed little-endian:

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

PoE2 variants with eight-byte root or edge records must not be used here. There is no group alignment padding.
Both target files decode to the last byte, have unique nodes, valid roots and no dangling edges, and re-encode
byte for byte identically:

| Family | Bytes | Groups | Nodes | Directed drawing edges | Roots | SHA-256 |
|---|---:|---:|---:|---:|---:|---|
| Ordinary | 78075 | 797 | 2987 | 3359 | 23 | `82D82CD3CD2DAD81606AF0DA9D18FCD53B2E92CF006550947D65A4F7E102DC32` |
| Phrecia | 76109 | 763 | 2928 | 3284 | 21 | `9A6D43A997BE44B53FC13FB6CED2E9F935AA11F8E4C4940CE8ECF9300A2FC1BC` |

`GameServer.Data.PassiveSkillGraph` reads and writes the full structure. `scripts/re/passive-graphs` verifies
lossless round trips and produces `data/passive-skill-graphs.json` with sorted, undirected adjacency, source paths,
hashes and revision. The ordinary and Phrecia starts share IDs (for example Slayer/Aristocrat 33795), but their
outgoing edges differ. Family is therefore required when interpreting those IDs.

## Preserve the current ordinary tree when Phrecia is selected

The shipped alternate PSG has an older ordinary/Bloodline portion: it omits 29 non-main-ascendancy nodes from the
current normal graph and differs in ordinary connections. Simply switching to it would change the base game too.
The version-2 `passive-graphs` extraction entry therefore writes a deterministic merged graph: every ordinary and
Bloodline node, connection and layout comes from the current normal PSG; only the main ascendancy portion comes
from the Phrecia PSG. It also writes `data/phrecia-passive-skillgraph.psg` for the client. No game files are tracked.

The merged graph has **2,957 nodes, 778 groups, 3,312 directed drawing edges**, and SHA-256
`F2766B5A4618CDBE7E2993973B4D653F155F71BDFE0E5D063A425196CC93D638`. An audit of the actual written GGPK confirms
all **2,576** ordinary/Bloodline nodes and all **381** Phrecia main-ascendancy nodes, with zero ordinary node,
ordinary edge, ordinary layout, or Phrecia node differences against their chosen sources. The normal PSG remains
byte-identical to the pinned original. The independently read-back audit is in ignored
`logs/ascendancy-merged-audit.json`.

`live.ps1` installs the merged virtual file after closing the client and before starting the patch server. A server-only
`-NoClient` or `-Only Gateway` restart instead checks the actual installed hash read-only and refuses a mismatch.
It does not rely on a potentially stale receipt. A full restart is required to install/reload a new graph.

The patch appends one small bundle and redirects only the existing alternate-graph file record in the bundle index.
Every unrelated file record and the entire path/directory table are preserved. The old game bundle is untouched.
The index backup, original virtual file, and hash receipt are retained under
`Client/LocalExileBackups/phrecia-passive-graph-v1/`. These are ignored client files. The writer uses valid uncompressed
Oodle Kraken blocks; it needs no additional native compressor. The format is independently decoded by the existing
OozSharp/ooz-wasm decoders ([block header reader](https://github.com/powzix/ooz/blob/master/kraken.cpp)).

The new bundle is verified and flushed before the index switches. Exclusive access prevents writes while the game
is open. Restore refuses an index or patch bundle that has unrelated intervening changes. Synthetic tests cover
apply/restore/reapply, unchanged unrelated files, source/hash refusal, lock refusal, and multiple chunk boundaries.
On the actual client GGPK, apply → restore → reapply passed; restore recovered the exact original alternate PSG and
raw index hashes, and the final installed graph matched the generated server graph. Native loading of the stored
bundle subsequently succeeded in the actual client: the merged alternate tree loaded without an executable change.

All GGPK writes now run through a durable byte-range undo stream, including PDIR relocation, free-list changes,
file payloads, truncation and the library's final Dispose/Flush writes. Complete checksummed before-images and
previous file lengths are flushed before each target mutation. A Windows volume/file ID, creation time, canonical
path and reconstructable original-prefix fingerprint bind the journal to its file. Recovery validates the journal
before any writes, replays before-images in reverse, and restores lengths without opening a GGPK or parsing an index.
A durable commit marker follows the fully disposed GGPK and fsynced stream; a leftover committed journal is cleaned
without undo. Torn final frames and unpublished preparation headers are handled separately from corrupt middle data.

Full `live.ps1` startup closes the client, builds/runs raw `--recover`, and only then extracts data. Server-only startup
and standalone extraction refuse a pending journal read-only. Apply/check validate the actual ordinary PSG hash and
manifest revision/patch version every run. Restore removes a custom bundle only when its receipt says this patch
created that GGPK record; a pre-existing reused record is retained. Equivalent bundle descriptors are compared with
case-insensitive `Bundles2/` and `.bundle.bin` normalization.

From the repository root, while the client is closed, the maintenance commands are:

```powershell
dotnet run --project scripts/re/passive-graphs -- --check Client/Content.ggpk data/passive-skill-graphs.json
dotnet run --project scripts/re/passive-graphs -- --apply Client/Content.ggpk data/passive-skill-graphs.json
dotnet run --project scripts/re/passive-graphs -- --restore Client/Content.ggpk
dotnet run --project scripts/re/passive-graphs -- --recover Client/Content.ggpk
```

The read-only `--check` also works with an already running client. After a restore, a full `live.ps1` run reapplies the
merged graph; a server-only restart refuses to serve the merged data against the restored legacy graph. Regeneration
uses the verified original backup when the GGPK already contains the merged graph, so repeated extraction is stable.

The latest local run (log timestamps 23:35–23:39) also exercised both families on the same account in Standard.
Slayer loaded `Default`; a native tree click and Apply allocated node 50845, spending one of eight ascendancy points.
Returning through character selection to Aristocrat loaded `DefaultAltAscendancies` with stat 21146=1 and retained
six allocated ascendancy nodes. A native allocation of 34482 changed ordinary available points 8→9 and ascendancy
available points 2→1; a native refund changed them back and spent five refund points (5→0, C→S `0x36`).
An actual Predictive Fireball cast used effective gem level 2 / quality 20, damage 10–16 and radius 13, spent mana
47→42, and produced projectile 3041 creation, movement and removal messages. These are concrete lifecycle/effect
observations, not verification of every mechanic in all 19 classes.

After the journal-enabled `--recover → --restore → --apply → --check` cycle, a full `live.ps1 -Walkmaps` restart
verified the merged SHA and launched one client (PID 22880). The fresh read-only trace
`logs/ascendancy-final/native-after-restart.jsonl` recorded normal Slayer's node 50845 in `Default`, then Aristocrat's
six nodes `[24607,27473,30851,46836,48846,54114]` and eight ordinary-point grants in `DefaultAltAscendancies`.
The client displayed eight ordinary points, two free ascendancy points and zero refund points after the paid refund.
This checks both saved families after restarting the client and all three servers, rather than only changing areas.

## Repeating the live observation

Start the single client through `scripts/dev/live.ps1`. Attach the read-only trace:

```powershell
.venv/Scripts/python.exe -I -X utf8 scripts/re/frida/run.py <pid> --log logs/ascendancies-native.jsonl scripts/re/frida/trace-ascendancies.js
```

It records received world stats, the actual graph selected by the client, and the class/points/nodes applied by
the native `0x33` handler. It does not write memory, replace functions or call into the game. Opening P on the
initial ordinary connection on 2026-10-25 recorded empty world stats and `Default` / `Metadata/PassiveSkillGraph`.
After choosing Aristocrat and re-entering with the corrected first-list encoding, the same client recorded
`{"21146":1}`, `DefaultAltAscendancies`, and
`Metadata/AlternateSkillGraphs/CustomAscendancyPassiveSkillGraph`. Its applied class byte was 68 (Duelist 4,
ClassNo 1), secondary byte 0. The Gateway retained the character's ordinary `Standard` league. This verifies the
native per-connection family selector without a game-data or executable patch; it does not by itself verify all
ascendancy mechanics or an expanded native chooser. The trace is in ignored `logs/ascendancies-native.jsonl`.
