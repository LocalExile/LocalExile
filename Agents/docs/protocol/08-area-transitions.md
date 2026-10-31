# 08 — Area transitions (exits between areas)

State of 2026-10-16: Lioneye's Watch has its exit to **The Coast** at the town's east edge. The client creates it,
shows its icon on the map and puts it in the wake zone. Using it is in [09](09-area-change.md).
2026-10-17: the exit sits on the gate and highlights the gate's arch on mouse-over like the real game (§5).
2026-10-18: **every area gets its area transitions** with destinations (§7): verified live town ↔ The Coast ↔ The Tidal
Island, The Coast → The Mud Flats and The Mud Flats' three.

![The Coast exit on the town map](../re/evidence/2026-10-16-coast-exit-on-map.jpg)

Code: `AreaTransitionObject` (`src/GameServer.Core/Protocol/Instance/InstanceEntities.cs`), `AreaTransitionCatalog`
(`src/GameServer.Gateway/World/AreaTransitionCatalog.cs`), `AreaLayouts` (`src/GameServer.Gateway/World/AreaLayouts.cs`). Tests: `InstanceEntitiesTests.Area_transition_blob_layout_and_type`,
`Area_transition_with_a_tile_object_model_rotation_and_height`.

## 1. What an exit is

A server object of type **`Metadata/MiscellaneousObjects/AreaTransition`** (MurmurHash2 key, see
[04](04-player-entity.md#1-object-types-need-no-registration-message); the type is in the client's register, and live it
was created without "Unknown object type"). Its `.ot` file:

| Component | Notes |
|---|---|
| Positioned | `object_size = 6`, `blocking = false` |
| BaseEvents | `server_only = true` (no client data) |
| Animated | |
| AreaTransition | the destination |
| Stats | |
| ObjectMagicProperties | stat descriptions `map_stat_descriptions.txt` |
| MinimapIcon | `icon = 18` |
| InteractionAction | |
| client: Render, Targetable | `display_text = "ObjectDisplayTextAreaTransition"` (the label is the destination's name) |

Sent like the player: **0x2b9** (object id 0x2000 for the first exit) before 0x21, which queues it until the world
activates, then **0x1a9** (wake zone) after 0x21.

## 2. The component blob (traced with Frida, 41 bytes, consumed exactly)

The live object's components, in deserialize order: Positioned `0x1435a0f18`, BaseEvents `0x1435a55e8` (no data),
Animated `0x1435a5030`, AreaTransition `0x14346de00` (a client subclass, slot 17 `0x14166bb80` → base `0x141e5b870`),
Stats `0x1435dd4e8`, ObjectMagicProperties `0x1435a6ae0` (slot 17 → slot 20 `0x141e77990`), MinimapIcon
`0x1435a7018` (slot 20 `0x141e6f5d0`), InteractionAction `0x14359f7d0` (`0x141e1e240`), then two the loop does not reach
(`0x143468920`, `0x14346c238`).

| Part | Bytes | Fields |
|---|---|---|
| object flags, base data | `00 00` | as the player |
| Positioned | 16 | i32 x, i32 y (grid), f32 rotation, u16, 2 flag bytes; + 8 with the fixed height (§5) |
| Animated | `00` | flags; bit0 + u32-counted UTF-16 `.ao` path for a model (§5) |
| **AreaTransition** | 5 | u8 flags (low nibble = state `+0xb2`, bit4/5/6 → `+0xb5/+0xb6/+0xb7`, bit7 → extra record `0x141e62df0`); **destination variant** (`0x141e62490`): u8 index + value, index 0 → u16, 1 → u32, **2 → u16 WorldAreas.HASH16** (resolved by `0x141e5be60` through `0x1422d9920`, the same lookup as 0x14/0x1d); then a compact int `+0xa4`, read only when the state is 0 and **the current area is a town** (`0x141e5b98e`: entity +8 → world +0x78 → +0xb0, the current WorldAreas row, byte +0x14 = IsTown; the server-side writer `0x141e5b7a0` has the same test). Nothing but the debug dump `0x141e5bf20` reads it (for type 1 it prints it as the telepad index). The Gateway writes it only in towns (`AreaTransitionObject.InTown`): before, every exit outside a town had one byte left over at the blob's end, harmless because every byte after it was 0 |
| Stats | 10 | as the player's comp1: compact-int n pairs, u32, u32, u8 |
| ObjectMagicProperties | 4 | **base magic properties first** (`0x142201d70`, called with the reader still in `rdx`: u16 flags, rarity = low 3 bits, u8 `+0x7a`, sections behind flag bits 0x10/0x40/0x200/0x400/0x800 and, with bit3, 0x80/0x100/0x20), then the component's own u8 flags (bit1 → u16 flavour text key, bit2 → string) |
| MinimapIcon | `00` | u8 flags (bit1 hidden/shown, bit0 → u16 icon override, bit3 → variant) |
| InteractionAction | `00` | u8 flags (bit2 → u8 [+ u16 action]) |
| tasks | `00` | |

The Coast exit: `00 00 | 80010000 D9000000 00000000 0000 0000 | 00 | 00 02 D27A 00 | 00 00000000 00000000 00 | 0000 00 00 | 00 | 00 | 00`.

How the deserializers were found: name string (`strexact.py`) → static initializer (`0x14006xxxx`: AreaTransition
`0x140067c60`, MinimapIcon `0x140068870`, InteractionAction `0x1400667c0`, ObjectMagicProperties `0x140068d00`,
BaseEvents `0x140067cd0`) → pool (`0x1445bb800`, `0x1445bc1b0`, `0x1445ba6c0`, `0x1445bbea0`, `0x1445bb7b0`) → factory
(`0x141e5b1d0`, `0x141e6e5e0`, `0x141e1c9e0`, `0x141e775c0`, `0x141e5e220`) → vtable → slot 17 (+0x88). Then three Frida
rounds fixed the byte counts: AreaTransition needed the compact int, and ObjectMagicProperties needed the 3-byte base
part that the decompiler hid (a call that reuses `rdx`).

## 3. Where exits go: the area's layout files

The server has to place each exit where the client's own layout expects it. For Lioneye's Watch:

1. `WorldAreas` row `1_1_town` (HASH16 `0x235c`): `Connections` = `1_1_1` The Twilight Strand, `1_1_2` **The Coast**
   (HASH16 `0x7ad2`); `TopologiesKeys` → `Topologies` row `ShoreEncampTownNormal` → DGR file
   `Metadata/Terrain/Act1/Town/Graphs/beachtown.dgr`.
2. The DGR has a single node: `19 11 0 "beachtown" R270 3 "default" "entrance2" "entrance1disabled" …`, i.e. room
   "beachtown", rotated 270°, with three named points enabled or disabled.
3. `Metadata/Terrain/Act1/Town/master.tsi` → `generate.rs` → room `Rooms/beach_town_unique_normal.arm` (version 36,
   18 × 30 tiles of 23 cells = 414 × 690). Its named points, in room cells:
   ```
   105 123 3.14162 "entrance1"
   196 384 0.261828 "entrance2"
   178 346 12.5664 "default"
   ```
4. The R270 placement maps room (x, y) to the 690 × 414 area grid as **(y, 413 − x)**. That is the only rotation that
   puts all three points on walkable cells of the client's walkability dump:

| Point | Room | Grid | Role |
|---|---|---|---|
| entrance1 | (105, 123) | (123, 308) | disabled in the DGR: the one-way Twilight Strand side |
| **entrance2** | (196, 384) | **(384, 217)** | **exit to The Coast**, east edge (matches the map icon) |
| default | (178, 346) | (346, 235) | the default arrival point (where a player coming into town appears) |

`scripts/re/worldareas.py` reads WorldAreas rows (Id, Name, Act, IsTown, HasWaypoint, Connections, AreaLevel,
HASH16) straight from `Data/WorldAreas.datc64`. The JSON extraction of that table is still broken past the early columns.

## 4. All zones: what it takes

- **Fixed layouts (towns, hideouts, many boss arenas):** the method above works per area: WorldAreas →
  Topologies → DGR → room `.arm` named points → rotation, then which point leads where. Named entrance points line up
  with `Connections` by name and DGR tags (enabled or "…disabled"). Matching names to destinations still needs care for
  areas with several exits. Doing this for every town and fixed area is mechanical work.
- **Generated areas (most outdoor and dungeon zones):** the layout comes from the area seed through the client's level
  generator (the DGR is a graph of rooms joined by randomly picked tiles). The exits sit on the generated layout, so the
  server must either reproduce the generator or learn the positions from the client (for example a Frida dump after
  generation, as was done for walkability). This is the large part.
- **Using an exit (captured live):** clicking the exit sends **C→S 0x24** "use skill on object", 12 bytes:
  `0024 00002000 0266 1066 0408` = u16 id, **u32 target object id (0x2000, the exit)**, u16 skill id `0x0266` (the
  interaction skill), u16 sequence (`0x1066`, `0x1067`, … like 0x25), u16 flags `0x0408`. It is 0x25's sibling aimed at
  an object instead of a cell. A `0016 08 0000` (C→S 0x16, 5 bytes) was also seen. The client keeps re-sending 0x24 while
  nothing answers. Next: echo the action (as 0x1ab does for 0x25) and then move the player to the destination area,
  most likely with a new 0x14-style instance assignment or an area change on the same connection.

## 5. Tile objects: the exit's model, rotation and height

The white arch the real game highlights on mouse-over is not a separate model file. It belongs to the town **tile**.
`Metadata/Terrain/Beach/Town_v2/beachencampment_v01_01.tdt` names the tile
`Art/Models/Terrain/Beach/BeachTown_v2/beachencampment_v01_01.tgt`, and next to it
`beachencampment_v01_01.tmd` lists the tile's placed objects.

**`.tmd` (version 5):** u8 version, u8 count, then count × 36-byte records, then a u32 length in UTF-16 units and the
string table. Record: f32 x, f32 y, f32 z (tile-local world units; 250/23 per cell, z negative = up), u32 `0x80000000`,
u32 0 (the same in every record here), then four u32 string offsets (in UTF-16 units from table start + 2): name,
mesh `.fmt`, object type, `.ao` (`0xffffffff` = none). The town tile has 17: torches (`Doodad` + `TorchFire.ao`), the
waypoint, `transition_twilightstrand` (`AreaTransitionMapMarker`) and:

| Record | Mesh | Type | Tile cell | z |
|---|---|---|---|---|
| `beachencampment_v01_01_area_transition2` | `…_area_transition2.fmt` (material `transparentobjectsc.mat`) | `AreaTransition` | (113.5, 319.5) | −351.04 |

The `.fmt` is a flat vertical plane (every vertex at local x = −2.24, y −108…+116, z 0…−234): the pivot is the bottom
centre of the arch, and it is only visible when highlighted.

**How the server gives the exit that mesh** (found by scanning the live client's memory for the record name): for
each record the client registers a **virtual `.ao`** named `<.tdt folder>/<record name>.ao`, here
`Metadata/Terrain/Beach/Town_v2/beachencampment_v01_01_area_transition2.ao`. It is not in the bundle index. The server
sends that path in the exit's Animated component. Animated accepts only `.ao` (the `.fmt` path itself throws "Filename
must end with .ao", `0x141da7d80`); its bit1 form is a `Data/MiscAnimated.dat` row, not used here.

**Placement**, all from the files:
- position: tile cell (113.5, 319.5) + the tile's slot in the room (`k 15 18 …` at tile column 3, row 3 of
  `beach_town_unique_normal.arm` = +69 cells) = room (182.5, 388.5), then the R270 mapping of §3 = **grid (388, 230)**
  (world = (grid + 0.5) × 250/23, i.e. the record's exact position). Checked: the same mapping puts the waypoint record
  at (256.5, 168.5); live the player reads (256, 170) there.
- rotation: Positioned's float `+0x29c`. The plane must face across the gate, so 90° or 270° (the room's R270); live,
  **π/2** matches the real game. The sign convention is not derived yet.
- **height:** Positioned flags0 bit2 (`0x04`) adds i32 `+0x290` (steps of 7.8125) and f32 `+0x2b4` (world z); when
  `+0x290` ≠ `0x7fffffff` the client uses that z instead of the terrain height (`0x141e331e0`, `0x141e35800`). Sent as
  −45 and **−351.04**. Without it the arch's base sat on the terrain and the beam hid its top.

The server's blob: Positioned `84010000 E6000000 DB0FC93F 0000 04 00 D3FFFFFF 1F85AFC3`, Animated `01 49000000` + the
path. Dev command in the Gateway: `/transition [pos [here | x y | +dx +dy] | rot <degrees> | z <z | none> | model
<path | none> | index <n>]` re-creates the first area transition with that change (`index` is the AreaTransition compact int,
sent in towns only, §2). Every position the client has seen stays
on its map: the minimap cache (`%LOCALAPPDATA%\Path of Exile\Minimap\`) remembers icons per area across restarts,
so after moving an area transition around, clear that cache before judging the map
([../dev/live-testing.md](../dev/live-testing.md), Troubleshooting).

Other exits: an exit that is a `.tmd` record takes its model, rotation and height this way. The Twilight Strand gate is
`transition_twilightstrand` (type `AreaTransitionMapMarker`) at tile cell (37.5, 45.5), grid (114.5, 306.5).

## Open

- The rotation's sign convention: settled. The client computes 3π/2 for the town arch (record 0 + code 3's three quarter
  turns, §7), which looks the same as the π/2 picked live because the arch is a flat plane; the Gateway uses the
  client's value.
- Arrival points: coming back from The Coast arrives next to the gate (near entrance2), from the exit's position
  ([09](09-area-change.md) §4); The Twilight Strand arrives at entrance1. A fresh login should probably use "default"
  (346, 235) instead of today's (197, 261).

## 6. The area transition atlas (every WorldAreas row, offline)

State of 2026-10-17. `scripts/re/atlas` derives every area's transitions and where each leads from the game files
alone. It writes **`data/area-transitions.json`** (not committed, because it is derived from the client) and prints the
coverage report below. Parsers live in `src/GameServer.Data/Terrain` (`TileObjectFile` .tmd, `TilePlacement` codes,
`GraphFile`, `RoomFile`, `TileDefinition`). Tests are in `tests/GameServer.Data.Tests/TerrainTests.cs`: the .tmd
binary and text forms, room and graph parsing, the town placement (388, 231) / 3π/2 and the waypoint (256, 169), and
code composition.

```bash
dotnet run --project scripts/re/atlas -- Client/Content.ggpk
```

A run takes about 3 minutes. Other modes: `… survey` (tile key vs .tdt conventions), `… survey2 <graph regex>` (which
key index is which side) and `… debug <area id>` (per room slot: the tile set's tiles with that tag, the codes they
match, their .tmd).

### How it resolves an area

1. `WorldAreas.datc64` gives Id, Name, Connections and HASH16 (read directly). `TopologiesKeys` come from
   `data/tables/WorldAreas.json` (checked row by row by Id) and lead to `Topologies.DGRFile`.
2. Graph (.dgr / .tgr, versions 19 and 25): a node line is `x y edgeCount edges… "tag" rotation nameCount "names"… …`.
   A node tagged **`graph`** embeds a sub-graph. Its first name is either a graph path or a group of the master's
   `FileGroups` (.fgp: `"group"`, then graph paths relative to the .fgp, of which the generator picks one). Every
   candidate is walked.
3. Master (.tsi) → `RoomSet` (.rs → .arm rooms) and `TileSet` (.tst → .tdt tiles). A node's rooms are the rooms whose
   tag (the first quoted line after the dictionary) equals the node tag. With several, the generator picks one.
4. Room (.arm): dictionary (1-based), tag, room key `k W H …`, named points `x y angle "name"` (room cells), the slot
   grid (H lines of W slots: `k` + 24 numbers = a tile key at its top-left slot, `n`, `s`, `o`, `f N`), doodads after it.
5. Tile key ↔ .tdt. A key is `w h, E0..E3, 8 edge points, G0..G3, 4 heights, tag, ?`. The .tdt (version 10) header
   after its string table is `u32 "", u32 .tgt, u32 tag, 4 × u32 edge types, u8 w, u8 h, 4 × u32 corner grounds, u32,
   8 bytes edge points (a0..a3, b0..b3)`. **E0..E3 = top, right, bottom, left; G0..G3 = corners (0,0), (W,0), (W,H),
   (0,H)** (from the borders of 2850 rooms, `survey2`). A tile fits a slot under placement code c (§5's client codes)
   when all of these match:
   - size (swapped for odd codes) and tag;
   - edges permuted by c;
   - corners permuted by c's corner partner (the same rotation; a mirror m_j on edges is m_(j+1) on corners).

   When several codes fit, the edge point pairs (a_i, b_i), swapped by a mirror, pick among them. For the town tile,
   edges and corners alone also fit a vertical mirror; the points pick code 0.
6. Transition objects are the tile's .tmd records (the .tmd next to the .tgt, else next to the .tdt) and the room's
   doodads whose object type is an area transition. A type counts when it, or a type its `.ot` extends, contains
   `AreaTransition`, or when its .ot has an `AreaTransition` component. That includes quest exits like
   `Metadata/QuestObjects/SouthBeachTownEntrance` and `…/Act4/MineEntranceTransition`. The model is §5's virtual
   `<.tdt folder>/<record name>.ao`, with the folder spelled as the .tst writes it (e.g. `BeachToSwamp`).
7. **Point ↔ object:** the named point (any except `default` and the monster and object spawn points named by a
   `Metadata/…` path) inside the transition tile's slot rectangle, else the nearest one. The Climb's `prison.arm` has
   `Metadata/Monsters/CorpseWheel/CorpseWheelAggroCharge` beside its `entrance`: counted, one of the tile's two
   orientations put the exit to The Lower Prison nearest to it, and the exit was left unresolved. `matched` says which, with the distance; more than 80 cells adds a "guess" note.
8. **Point ↔ graph name:**
   - Matching: the same name, or the name + `disabled`. A generic room point (`entrance`) takes the one numbered node
     name no other room point has (Coast `town_entrance_exclude.arm`: `entrance` ↔ node `entrance1`). With several
     generic points and as many names, they pair in order (noted as assumed).
   - **`entranceN` → `Connections[N − 1]`.** `…disabled` means `enabled: false`, a one-way exit (the town's Twilight
     Strand marker).
   - `teleportN` / `telepadN` (also with `-ArenaName` suffixes) are in-area teleports (`status: internal`). `sidearea…`
     names are side-area links; `link` / `subarea=…` are sub-area links.
   - A room point the node does not name is not used in that layout (`status: unused`).
9. An entrance that the node names but no object belongs to becomes a `source: point` entry (`status: point-only`):
   destination and the point's cell, no model.
10. **Grid placement** is only computed for `fixed` layouts (a .dgr with one node, a fixed rotation, one room and one
    orientation):
    - room cell = slot × 23 + `TilePlacement.Cell(tileCode, …)`;
    - grid = `Cell(roomCode, …)`, where the node rotation `I`, `R90`… gives 0..3 and `F…` gives 4..7 (assumed to mirror
      first);
    - rotation = `Angle(room ∘ tile, record angle)`; the .tmd's 5th field is the record angle (f32, π/2 on Highgate's
      `AscentTransition`);
    - z = the record's z. Text .tmd (version 8) records are `cellX cellY hasPos [x y] z angle qx qy qz qw ? scale
      "ao" "name" "mesh" "type" [n]` (all 3 345 records in the game files; the `.ao` comes first, empty for none), so
      they give z and the angle too (before 2026-10-23 the parser took z as the angle).

### Results (run of 2026-10-17)

- **Lioneye's Watch** (fixed): `beachencampment_v01_01_area_transition2.ao` → 1_1_2 The Coast (0x7ad2) at grid
  **(388, 231)**, rotation **3π/2**, z **−351.04**, the same as live. `…_transition_twilightstrand.ao`
  (AreaTransitionMapMarker) → 1_1_1 via `entrance1disabled` at (114, 307).
- **The Coast** (18 topologies, all generated), in every topology:
  `Beach/Transitions/beachtown_north_entrance_v02_01_area_transition.ao` → 1_1_town,
  `BeachToSwamp/act1_area2_transition_v01_01_area_transition.ao` → 1_1_3 The Mud Flats,
  `TidalIsland/act1_karui_coast_to_island_transition_v01_01_area_transition.ao` → 1_1_2a The Tidal Island. These are
  the three models of the live capture, with the expected destinations.

Coverage:

| | Count |
|---|---|
| WorldAreas rows | 1932 (1915 with a topology, 340 with connections) |
| area × topology | 5736 generated, 2909 fixed-graph (.dgr with several nodes), 316 fixed (310 areas, all fixed) |
| connections (area → destination, 720) | **439 resolved** (an enabled transition object in every topology), 230 disabled (only `…disabled`: one-way back links), 40 point-only, **11 unresolved** |
| transition objects (area × topology × room, 23884) | 11737 resolved, 263 point-only, 6747 in-area teleports, 3584 unused by their node, 1553 unresolved |
| with grid placement | 72 objects in 24 fixed areas |

Unresolved objects, by reason:
- 1291 "the area has no connections" (maps and league areas, whose exits are not in `Connections`);
- 151 "the room has no named point" (mostly side-area and sub-area links);
- 25 generic points with a different number of node names;
- 14 tiles whose possible orientations put them nearest to different points.

877 objects also note that the tile key fits several orientations. That is harmless for generated areas, where only
the destination and model matter; a fixed layout gets no grid position then. 162 objects note a nearest-point pairing
more than 80 cells away.

The 11 unresolved connections:
- Named by a node, but the object there is unresolved or missing: 1_5_2 → 1_5_town (×2), 2_6_6 → 2_6_5,
  2_7_3 → SanctumFoyer_Fellshrine, 2_7_12_1 → 2_7_12_2, 2_8_6 → 2_8_13, 2_10_6 → 2_10_town,
  Descent2_6_boss → Descent2_6_1, Descent2_9_2 → Descent2_8_1, Descent2_9_boss2 → Descent2_10.
- No node names it: TGRMulti → 1_4_7.

Files: 12 .tdt that are not version 10 do not parse (wall chests, theatre walls, abyss holes, templar doors; none is a
transition tile seen so far), and 2 referenced files are not in the index.

### Not derived / next

- Generated areas: only room-local positions, because the generator places rooms by seed. The walk-map recorder
  captures the client's placement per (area, seed) (§7), or reproduce the generator.
- Fixed layouts with an outer buffer (`OuterBufferSize` ≠ 0) are flagged: the grid offset may need the buffer added.
- Mirrored room rotations (`F…`) and the edge-point rule under quarter turns are inferred, not verified live. The
  Twilight Strand's `zombie_bite` key fits no orientation by points; the edge/corner set is kept and flagged.
- Point-only entrances (e.g. 2_11_endgame_town → 2_10_town, Menagerie) need the object's model from elsewhere: the
  room's tile .tgt, or a live capture.

## 7. Every area's area transitions in the Gateway (live 2026-10-18)

This section is how the Gateway places and pairs the exits of an area it does not generate itself (a walk-map
capture, or the atlas's fixed placement). A layout the Gateway generates takes its exits from its graph nodes instead
(§8); nothing below about matching models applies to it.

**Where they are.** Generated layouts exist only in the client, so `scripts/walkmaps` (started by
`live.ps1 -Walkmaps`) also hooks the per-record callback `0x1420415e0` of the tile-object loop (`0x141d7f700` walks each
placed tile's `.tmd` records). For records of AreaTransition, Waypoint or quest town entrance types
(`…TownEntrance`, e.g. The Twilight Strand's `SouthBeachTownEntrance`) it computes the placement the way
`0x14202f1c0` does and writes `data/walkability/<area>_seed<seed>.transitions.json` next to the walk map:

| From the client | Meaning |
|---|---|
| `ctx[1]` → placed tile | `+0x30` i16 height offset (steps of 7.8125), `+0x36` u8 rotation/flip code 0..7 |
| `ctx[2]` | i32 x, i32 y: the tile's grid origin |
| `ctx[3]` → handle, `*handle + 0x28` | `+0x60` / `+0x61` u8 tile size (tiles of 23 cells) |
| record | i32 x, i32 y (tile-local cell), f32 z `+0x10` (+∞ = none), f32 angle `+0x14`, type handle `+0x30`, `.ao` handle `+0x38` (handle `+8` → UTF-16 path) |
| code table `0x142fa04b0` | 8 × 3 bytes T: a = [w·23 − x − 1, x, h·23 − y − 1, y]; gx = a[T1 + 2·T0] + ox, gy = a[(T0 == 0 ? 2 : 0) + T2] + oy |
| angle | 2π − angle for codes ≥ 4, then + (code & 3) · π/2 (`0x1436a3318`), wrapped to [0, 2π] |
| z | record z − height offset · 7.8125 |

The client spawns only its own doodads from these records; server types (AreaTransition, Waypoint …) fail its type
filter, which is why nothing in the client ever creates them. The Frida trace `scripts/re/frida/trace-tile-records.js`
is the same hook for one-off use.

**Where they lead.** A layout the Gateway generates itself (`ServerLevels`, [re/011](../re/011-level-generation.md) §7)
takes each exit's destination from the graph node of the room it stands in (§8); it wins over a capture of the same
(area, seed), which has no rooms. For any other area `AreaTransitionCatalog` loads the atlas (§6) once and, per area:
- with a capture: picks the graph variant (topology) whose transition models cover the captured models best, then
  gives each captured AreaTransition the destination of the same model in that topology (resolved and enabled). Per
  topology only 53 of 6 474 models lead to more than one place; those are logged and skipped. Disabled one-way links
  (e.g. town → The Twilight Strand, `entrance1disabled`) are not created;
- without a capture: the atlas's static placement for fixed layouts (26 areas, the town among them);
- the town keeps a built-in fallback when there is no atlas.
The objects get ids 0x2000, 0x2001, …, their model, rotation and fixed height, and their own object type. After 0x7d
an area without area transitions re-reads the capture (the recorder writes it while the client generates).

**Object types.** Every resolved transition type but four writes the AreaTransition blob. Types that add components
(`Metadata/QuestObjects/WaterCave/WaterCaveAreaTransition` adds Transitionable; `DreamPortal`, two effect objects) are
created as plain `AreaTransition`, with the same model, place and destination. Live 2026-10-18, The Mud Flats' cave
entrance: sent as its own type with the AreaTransition blob, the client read only the parent's components (Frida
`check-components.js`: 8 components, blob consumed exactly), kept Transitionable at state 0 and crashed in its
post-create event (`0x141ded7b9`); a Transitionable byte appended to the blob was read as the task count (Buffer
Underflow). `data/object-types.json` now also lists the atlas's tile-object types (`ot_components.py --all`). The quest
behind that cave is in [12](12-quests.md).

**Dev commands.** `/goto <area id or name> [seed]` changes area as if through an area transition (e.g. `/goto 1_1_3`; with a seed, into a new instance at that seed, e.g. `/goto 1_1_5 1384217569`);
`/transition …` (§5) changes the first one.

## 8. Exits of a generated layout: from the room's graph node

Matching a placed object to the atlas by its model (§7) cannot work for generated layouts: a master's Reskin places
records the atlas (built from the tile files) lists under another name (The Cavern of Wrath and Anger's
`corrupted_cave*_exit`), some objects come from no file the atlas reads (The Submerged Passage's
`watery_depth_entrance_v01_01`), one model serves two exits of an area (The Upper Prison's stairs), and a model says
nothing about which in-area gate leads to which arrival (the Warden's Quarters, every boss arena behind a gate).

**What the client does** (static reading, addresses in Game.exe 3.29.3b):
- It never maps a transition to a destination, a graph node or a room point. Node descriptions are read only while
  generating (`0x142100be0`, `0x1420f9d50` mark the "important" ones: default, telepad, teleport, entrance, accessible,
  sideentrance; `0x14204f590` "subarea="); `%areaname%` and the "-label" split do not occur in the binary.
- The generator's object-created hook (`0x142044b60`, slot +0x30 of the generator vtable `0x1435c6988`) disables every
  AreaTransition it is given and files it in a map at generator +0x220, keyed by the placed room's node descriptions
  (room record +0x20) for room lines and by a per-tile value (tile map +0xa0) for tile records. It runs only when slot
  +0x10 returns true, which in the client is `0x1400ed4a0` (false): this is the real server's path. So the server
  derives each exit from the generated layout's nodes.
- The client creates no server type itself (`0x14152d680`: BaseEvents `server_only`), and AreaTransition is
  `server_only`: the server creates every tile `.tmd` record and every `.arm` object line whose type has the
  AreaTransition component (also e.g. `DepthsReturnTransition`, a room line). A master's Reskin swaps the tile's
  `.tmd` for `alttiles/<reskin>/<reskin>_<name>.tmd` (`0x141d7f700`, path `0x141d88810`); the virtual `.ao` name rule is
  `0x1429fc0a0` (the record's own `.ao`, else `<folder>/<name>.ao` if it exists, else the name without the Reskin
  prefix if that exists, else the reskinned name).
- The AreaTransition component (debug dump `0x141e5bf20`): the flags' low nibble +0xb2 is the transition type (0
  Normal, 1 Teleport, 2 SideArea, 3 SideAreaReturn, 4 LastTownReturn, 5 Labyrinth, 6 Menagerie, 7 SynthesisVoid, 8
  Respawn; table `0x1435a5460`), bit 4 +0xb5 disabled (`0x141e5bd30`; enabled later by the delta `0x141e5bac0`), bit 6
  +0xb7 and bit 7 +0xb8 Necropolis data (`0x1424d3070`). The destination variant: 0 = ClientStrings row + 1, 1 =
  ClientStrings HASH32, 2 = WorldAreas HASH16; the label is the area's name, or the ClientStrings text
  (`0x14166c790`), "Area Transition" without one. For type 1 the dump calls +0xa4 the telepad index.

**What the server does** (`AreaExits` in `GameServer.Data/Terrain/Generation`, used by `ServerLevels` and
`AreaTransitionCatalog.FromLayout`, for every area the Gateway generates):
1. The transition objects: the tiles' records (`AreaObjects.Transitions`) and the rooms' object lines
   (`AreaObjects.RoomObjects`) whose `.ot` has the AreaTransition component, no waypoints or effects.
2. Each stands in a placed room whose footprint holds it: one whose node names an exit before one that names none (a
   background fill room around The Rogue Harbour's rooms), also one tile off such a room (The Cathedral Rooftop's
   Kitava ladder lies 3 cells outside bossentry.arm), the last placed of them. Its node descriptions are its exits:
   `entranceN` (→ WorldAreas Connections[N − 1]), `entranceNdisabled` or `entranceN-disabled` (an arrival point only:
   not created, e.g. Lioneye's Watch's Twilight Strand marker, The Submerged Passage's Depths return boat, The Sarn
   Ramparts' spider tomb from The Temple of Decay), `teleportN[disabled][-label]` (also `=label`: a gate within the
   area to `telepadN`), `telepadN[-label]` (an arrival); a space before the number occurs (`"telepad 1"` in every
   Peninsula topology). A room whose node names none places no exit (a sewer grate of a side area, an unused
   transition room's arch). `entranceNloot` (Descent's loot rooms) is not an exit.
3. Slots, first the arrivals: a numbered room point (`entrance2`, `telepad1`) holds its own number; otherwise the k-th
   generic `entrance` point holds the node's k-th enabled `entranceN` (enabled first: The Control Blocks'
   "entrance2disabled" "entrance1") and the k-th generic `telepad`/`teleport` point the k-th `telepadN`. Then the
   gates: a gate stands beside the arrival from the place it leads to, so `teleportM` takes the slot of the room's
   `telepadK` whose `teleportK` stands in the room of `telepadM` (K ≠ M). The Harvest's boss1 ("telepad7" "teleport8"
   "teleport9" "telepad10"): the gate back to the core stands at telepad7 (the arrival from the core), the gate to
   boss2 at telepad10. The Cathedral Rooftop's bossentry ("telepad1" "telepad2" "telepad3" "teleport4"
   "teleport3-ArenaNameKitava" "teleport1-%areaname%" "teleport5-MapStairAlternativeLadder"): the arena door
   (teleport3) at telepad1, the way out of the arena (teleport1) at telepad3, the ladder to the dock (teleport4) at
   telepad2, where the dock's teleport2 lands. A gate without such an arrival takes a numbered point, else the k-th
   generic one no gate took; an exit without a point of its kind takes a point left over (The Upper Prison's stairs
   room: one "entrance" point, the node's only exit "teleport1-BrutusAreaName"); a telepad without one arrives at its
   room's gate.
4. Each slot takes the room's nearest object (closest pairs first), an object no player can walk up to (no walkable
   cell within the walk-up range, 13 cells) only when no usable one is left (The Harbour Bridge arena's transition1
   door, 44 cells from walkable ground); of gates tied for an object, one leading to another room first. The room's
   other objects are not created (The Lower Prison's entrance room: the wall hole by the "entrance" point, not the
   ceiling marker 190 cells away).
5. A gate is an AreaTransition of type 1 (Teleport); it takes the player ([09](09-area-change.md): `TeleportTo`, then
   `AreaLayout.Near`) beside the gate back: in the room of `telepadN`, the gate (another number's) whose own telepad is
   in the gate's room, at its object (The Hall of Grandmasters' main.arm: eight arrivals at "telepad" points that
   repeat, each beside the door back to its arena); without one, the telepad's slot. Its destination is the label:
   the description's ClientStrings key as variant 1 (HASH32 from `data/tables/ClientStrings.json`, e.g.
   BrutusAreaName 0x169BA21A "The Warden's Quarters", MerveilArenaName "Merveil's Lair"), else (`%areaname%`, no key,
   no table) the area itself as variant 2; no compact int (type 1). `teleportNdisabled` gates (an arena's way in or a
   shortcut back, which the game unlocks; the Upper Prison's only door into Brutus's room is
   "teleport3disabled-BrutusArenaName") are created open: the encounters that unlock them are not modelled.
6. An enabled `entranceN` that got no object, to an area no other exit leads to, is an exit without a model at its
   point. In the game that is a portal or an NPC's boat that appears there (the way to Highgate after Malachai,
   Weylam's boat at The Beacon and the Brine King's Reef, the ways to Oriath from the Act 9 and Act 10 bosses), or a
   town's exit no tile marks (Karui Shores' camp.arm "entrance1" to Oriath, as the atlas's point-only exits). A quest
   gate on the destination still applies (`Quests.FlagToEnter`).
7. Delve's mine entrance and exit lead to areas no graph node names (`TryDelveEntrance`: Delve_Hub's
   `foothills_DelveEntrance01` into the mine, the mine's exit back to the hub).
8. Arrivals: next to the exit back; through a one-way entrance, at that disabled entrance's point
   (`AreaTransitionCatalog.EntrancePoint`, `AreaLayout.EntranceFrom`).

The model plays no part, so this covers the Reskin, the objects the atlas cannot see and shared models without a
special case each. A gate or exit the game enables later is sent enabled (no Transitionable step, no disabled bit).

A `telepadN` no node's `teleportN` leads to is where the game moves the player when a fight ends (The Harvest's and
The Rotting Core's organ rooms, telepad11–14). The Rotting Core's weylam.arm ("telepad16" "zone=7" "entrance2") is
reached only that way, after the Depraved Trinity dies; its exit to Oriath is created, the move there is not.

**The audit** (`scripts/re/exits`, from the repository root):

```bash
dotnet run --project scripts/re/exits -c Release -- [area regex] [--all] [--seeds N] [--seed S] [--json out.json] [--quiet] [--notes] [--objects]
```

For every campaign area (WorldAreas "1_…" / "2_…" of acts 1–10 with a topology; `--all`: every area with one) that
the Gateway generates, at seed 1 and N more seeds (default 3, fixed per area), it generates the layout, asks
`AreaTransitionCatalog.For` for the exits as the Gateway does on entry (captures off) and checks them:
- against the layout's descriptions and the WorldAreas connections: missing (an enabled `entranceN` whose room holds a
  transition object at which no exit was created, by position), misplaced, unexpected and duplicate destinations, a
  `teleportN` without a gate to its `telepadN`;
- against §7's model matching over the same objects: a destination or a gate it had that no exit has now (this also
  covers areas without connections, such as Delve_Hub);
- for reachability: walking from the exit back to Connections[0] within the Gateway's walk-up range, and through the
  area's gates.

Info lines: rooms without an object for their entrance, connections no node names, objects not created, destinations
the layout names only as a disabled entrance, gates no player can reach whose target is reached anyway, telepads
no gate leads to. Exit code 1 on a problem.
