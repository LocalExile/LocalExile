# Ascendancy identity and graph selection in 3.29.3b

The evidence below comes from the original `Game.exe` with SHA-256
`78A0E227189C263658A7F65A02F086428B859BFC114AE8E7CE8AAB721EBD48D5` and the two graphs extracted from its
verified standalone GGPK. Addresses use image base `0x140000000`; the login-key patch does not move them.

## Class byte

`0x14215d894..0x14215d8a7` and `0x141ef9009..0x141ef901e` decode the main ascendancy as:

```text
baseClass = byte & 0x1f                 // Characters.IntegerId, not its table row
classNo   = (byte >> 6) | ((byte >> 3) & 4)
```

Zero means unascended: the first consumer branches to the base-class display when the decoded value is zero.
The earlier comparison with `7` at `0x14215d7e9` compares the entire byte, not the ascendancy number. It is not
an ascendancy sentinel. The inverse is `base | ((classNo & 3) << 6) | ((classNo & 4) << 3)`.
`CharacterClassByte` implements it, with exhaustive coverage of all seven base classes and all eight representable
class numbers. Only the existing data's 1..3 numbers are used by the current ordinary/Phrecia variants.

`0x14242c900` builds per-character vectors of ascendancy names. Their size follows the highest ClassNo plus one;
this particular loader does not impose a three-class maximum. That alone does not prove a six-option native chooser.

## The family is a world stat

The exact `Stats.dat` row 21145 is `league_enable_alt_ascendancies`, so the client's stat key is **21146** (`0x529a`).
`0x141ecc000` selects the character graph:

1. World `+0xd0 == 0x27` selects `PassiveSkillTrees` row 1, Royale.
2. Otherwise it reads key `0x529a` from the world's stats interface at `+0x120`.
3. Nonzero selects row 4, `DefaultAltAscendancies`, path
   `Metadata/AlternateSkillGraphs/CustomAscendancyPassiveSkillGraph`.
4. Zero selects row 0, `Default`, path `Metadata/PassiveSkillGraph`.

The same key is read by the ascendancy presentation consumers at `0x140984040`, `0x1409a0360` and `0x140b6da40`.
It controls the existing normal/LeagueName presentation, not an additional ClassNo. The spare byte in `0x33` is
not required to select Phrecia.

The stat is supplied in **S→C `0x1d`**: flag byte 0 bit 6 enables three tail collections. They follow the existing
mandatory vectors. `0x14214839d..0x1421484d8` writes:

1. Compact unsigned count, then unsigned key / signed value pairs: packet `+0x100`.
2. Compact unsigned count, then unsigned key / signed value pairs: packet `+0x120`.
3. Byte count of further records: packet `+0x138`.

The first collection becomes the world's stats interface at `+0x120`. The full static path is the copied area
message in `0x14215ba90`, then `0x14215e0d0`, world vtable slot `+0x1b8` (`0x142189aa0`), which assigns argument 4
to world `+0x120` at `0x142189ad8..0x142189ae2`.

`AreaInstance.LeagueStats` serializes that first collection. For Phrecia the exact tail is
`01 C0 52 9A 01 00 00` (first list has key 21146 value 1, second/final lists empty), and the first flag
byte becomes `40`. An empty dictionary preserves the old packet byte for byte. This changes the graph presented
to one connection; it does not change the character's stored league or add a native six-option chooser. A family
change must reload the area before the new graph is used.

## Point costs and multiple-choice options

Class starts are implicit. `0x141efc8d0` first tests the received allocation list; if a node is absent,
`0x141efcd62..0x141efcde0` still considers an ascendancy start allocated when its owner's ClassNo and Character
match the packed class byte. `0x141efd970` handles the ordinary class start. Neither start belongs in the paid
`0x33` node vector: `0x141efd280` increments the ascendancy counter for every explicit node with an ascendancy
owner, including an explicitly sent start. Keeping starts out of the save and wire list agrees with this native
behaviour; a collapsed/off-screen ascendancy view is not evidence that its start must be transmitted.

Runtime `PassiveSkills` has a packed layout with 16-byte foreign-row handles. `AscendancyKey` is at `+0x66`,
`SkillPointsGranted` at `+0x87`, `IsMultipleChoice` at `+0x8b`, and `IsMultipleChoiceOption` at `+0x8c`.
The remove-node function `0x141efd490` decrements the ascendancy allocation count (`tree+0x280`) for a node with an
ascendancy owner; an option also decrements the free-option count (`tree+0x28c`).
`0x141efd8b0` computes remaining ascendancy points from earned points (`+0x27c`) minus allocated owner nodes plus
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

## Client compatibility remains separate

The shipped alternate PSG has older ordinary/Bloodline topology: it omits 29 current non-main-ascendancy nodes.
Switching to that graph changes the ordinary tree too.
Earlier PR206 experiments installed a combined graph and observed ordinary Slayer and Phrecia Aristocrat in one
Standard account; those observations establish the protocol, not permission to replace the installed archive.
This extracted selection slice performs no archive apply, recovery or automatic installation.
Its production command/connection preflight rejects Phrecia until a separate client-compatible graph
solution is agreed.
The preflight runs only for a requested/saved Phrecia identity.
Ordinary/unascended characters require no alternate graph.
No executable patch or memory-writing graph selector is introduced.
The native selector remains world stat 21146, with an area reload required when the family changes.

The parent must repeat ordinary command selection, allocation, paid refund, relog and restart with the real client.
A Phrecia native smoke test is blocked by this explicit compatibility prerequisite.
The native Labyrinth/Altar selection flow remains #151, outside command-based selection.
