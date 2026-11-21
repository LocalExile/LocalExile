# Crucible

Crucible (3.21) adds Weapon Passive Skill Trees to weapons and shields, in-zone Crucible Forges that reveal and level
those trees through scaled monster waves, Magmatic Ore, Igneous and Crystalline Geodes (opening into Primeval and
Primordial Remnants), and the Forge of the Titans endgame encounter.

The module is `Modules/Legacy/Crucible`; the `Crucible` league (`Leagues/Crucible`) lists it alongside the Core modules.

## 3.29.3c Client Support and Boundary

The 3.29.3c client (`Client/Game.exe`, build 327984) retains almost the entire Crucible client runtime, with one
notable removal made by GGG when Crucible left core in 3.22:

| Subsystem | 3.29.3c Status | Notes |
|---|---|---|
| `Mods` bit 7 (`WeaponPassiveTree`) | Live | `0x1424bd880` / `0x1424bdb90`; full tree rendering, hover tooltips, XP bar, allocation display |
| C→S `0x0116` Orb of Scouring unallocate | Live | `0x1435ca4e0` / `0x141fa09e0`; unallocates deepest allocated node (`[node+0x2ec] == 6`) |
| Forge of the Titans Combining (`0x7b` `0x4a`, `0x299` / `0x29a`) | Live | `CrucibleCombiningWindow` (`0x1403e16f0`), normal (`arg1=0`) & uber (`arg1=1`), mutation visual flags |
| Antique / Scouring Furnace (`0x7b` `0x4b`, `0x29b` / `0x29c`) | Live | `CrucibleAddWeaponTreeToItemCraftingWindow` (`0x1403d6fd0`), modes `0`/`1`/`2` + entity id (`arg2`) |
| `CrucibleRules` (`0x89`) & `CrucibleMapRules` (`0x8c`) | Live | Registered via S→C `0x001f`, driven via S→C `0x02c9` (sub-messages `0x00`–`0x06`: popup, threat shader, FMOD audio) |
| `CrucibleDevice` / `Fissure` / `MoltenPool` / `CrucibleEndgameMonstersSpawnerBase` | Live | Animations, barrier, breakout/end waves, pools via `StateMachine` & `Transitionable` |
| In-zone `CrucibleForgePopup` `SelectPage` clicks | **Stripped** | `CrucibleWeaponSelect` (`0x1403e91d9`) and `CrucibleBack` (`0x1403e9292`) bind vtable `0x14309fb80` whose invoke slot `[+0x18]` is `0x1400f1180` (`ret 0`). `CrucibleRules` never invokes listener slots 1 (`SwitchPage`), 4 (XP preview), or 6 (inventory highlight). |

Because clicking a weapon inside `SelectPage` sends nothing on 3.29.3c (and `CrucibleDevice.ot` sets
`Targetable { targetable = false; no_highlight = true; }` on KB/M), the in-zone Crucible Forge uses native visuals,
audio, and `CrucibleRules` `0x02c9` state together with server-driven weapon/branch selection and channeling (via the
native `crucible_channel` skill `ActiveSkills` 1815 / action `0x5f29` masked to `0x1f29`, put on the skill bar inside the Forge's zone).
Seen live on 3.29.3d: a held channel sends its start and, on release, C→S `0x0199`, with nothing in between while the
cursor rests. Creating `Metadata/NPC/Info/Crucible_GlyphRegular` stops the client with "Unknown object type serialized
by server"; `CrucibleDevice`, `Fissure` and `MoltenPool` are created fine.

## Wire Formats (3.29.3c Addresses)

### 1. Item `Mods` Component Bit 7 (`0x0080`) — Weapon Passive Tree

Written immediately after `ObjectMagicProperties` and before `MemoryStrands` / `Intangibility` (`0x1421e5fc3`,
`0x1424bd880` writer, `0x1424bdb90` reader). All fields are **little-endian**:

1. `u32 LE` `BalanceItemLevel` — row lookup in `WeaponPassiveTreeBalancePerItemLevel.dat` (`0x1424bc620`, fallback level 1).
   The client reads `Bar1..Bar5` thresholds from that table, never from the wire.
2. `u32 LE` `Experience` — compared as signed `i32` (`>= 0`) against `[Bar1..Bar5]` to compute `UnlockedDepth` (`0..5`).
3. `u8` `HasTree` — `0` stops here (9 bytes total, used for unrevealed partial XP); `1` precedes the tree body:
4. `u16 LE` `Layout` — 15-slot triangular bitset (`col = 0..4`, `row = 0..col`, `bit = col * (col + 1) / 2 + row`).
   **Bit 15 (`0x8000`) MUST be `0`** when `HasTree == 1` (`0x1424bcc9b` sets `isRevealed = (Layout & 0x8000) == 0`;
   `0x1424bdb90` popcounts all 16 bits for mod count).
5. `u16 LE` `Allocation` — allocated nodes (`0x1424bcea0`: `col < UnlockedDepth`, node present, connected parent allocated, ≤ 1 per column).
6. `u16 LE` `LeftParentConnection` — bit `k` connects `(col, row)` to `(col - 1, row - 1)` (always set for `row == col, col > 0`).
7. `u16 LE` `RightParentConnection` — bit `k` connects `(col, row)` to `(col - 1, row)` (always set for `row == 0, col > 0`).
8. `sbyte[5]` `PendingAllocateRow[5]` (`+0x161..+0x165`) — pending auto-allocation target row per column `0..4`;
   **`-1` (`0xFF`) = none** (`0x1424bcd20` auto-allocates `(col, row)` once `Experience >= Bar[col]`).
9. `popcount(Layout)` standard mods (`0x142436e50`: `u16 LE Mods.dat HASH16` + `StatCount` signed compact ints) in
   **strictly ascending bit-index order (`0..14`)**.

### 2. Orb of Scouring Unallocation (C→S `0x0116`)

Fixed 26 bytes, **big-endian** (`0x1435ca4e0` / `0x141fa09e0`):

`u16 0x0116` | `u32 currencyInvId` | `u32 currencyItemId` | `u32 targetInvId` | `u32 targetItemId` | `u32 column (0..4)` | `u32 row (0..col)`

Sent exclusively for Orb of Scouring (`CurrencyConvertToNormal`) on the deepest allocated node (`[node+0x2ec] == 6`).
Server responds with `0x00bf` updates (node unallocated, XP reset to previous column's bar threshold, scouring stack decremented).

### 3. Forge of the Titans UI (`0x7b`, `0x0299`–`0x029c`)

All packet fields are **big-endian**:

- **S→C `0x007b`** (`u16 0x007b`, `u8 kind`, `u32 arg1..arg4`):
  - `kind = 0x4a` (`CrucibleCombiningWindow`, `0x1403e16f0`): `arg1 = 0` normal (non-uniques), `arg1 = 1` uber (allows uniques).
  - `kind = 0x4b` (`CrucibleAddWeaponTreeToItemCraftingWindow`, `0x1403d6fd0`):
    `arg1 = 0` Antique Furnace (non-corrupted unique), `arg1 = 1` Antique Furnace (corrupted allowed),
    `arg1 = 2` Scouring Furnace (remove tree from non-unique); `arg2 = forgeEntityId`.
- **C→S `0x0299`** (18 bytes, `0x1403e2490` → `0x141fa43e0`):
  `u16 0x0299` | `u32 topInvId` | `u32 topItemId` | `u32 bottomInvId` | `u32 bottomItemId` (top destroyed, bottom survives).
- **S→C `0x029a`** (`0x1421b7790` → `0x14250a640`):
  `u16 0x029a` | `u32 resultInvId` | `u32 resultItemId` | `CompactUInt count` | `count × (u32 col, u32 row, u8 flags)`.
  `flags` (`0x1403f0fc0`): `0x01` UpgradedTier, `0x02` DowngradedTier, `0x04` Mutated. Sent alongside `0x00bf` updates.
- **C→S `0x029b`** (10 bytes, `0x1403d7d70`) / **S→C `0x029c`** (10 bytes, `0x141fa51e0`):
  `u16 opcode` | `u32 invId` | `u32 itemId`. Sent alongside `0x00bf` update.

### 4. Rulesets & In-Zone Forge Messages (S→C `0x001f` & `0x02c9`)

- Register `CrucibleRules` (row `137` / `0x89`) in zones with a Crucible Forge, or `CrucibleMapRules` (row `140` / `0x8c`)
  in Forge of the Titans, via `S→C 0x001f` (`AddRuleset`).
- Custom ruleset payloads travel on **S→C `0x02c9`** (`0x1421bc5b0` / `0x141fa7ca0`):
  `u16 BE 0x02c9` | `u8 rulesetRow` | `CompactUInt payloadLen` | `byte[payloadLen]` (little-endian body).
- `CrucibleRules` (`0x89`, `0x141682b90`) sub-messages:
  - `0x00` (1B): `inZone = 1` (shows world-space `CrucibleForgePopup`)
  - `0x01` (1B): `inZone = 0` (fades popup)
  - `0x02` (1B): `weaponLocked = 1`
  - `0x03` + `f32 LE charge` (`0.0..1.0`, 5B): updates `CrucibleThreat` shader & difficulty bar (`barPct = (int)(powf(charge * 100f, 0.65f) * 5.01187f)`)
  - `0x04` + `u8 channelling` (2B): FMOD `CrucibleChannelStart`/`Loop` (`1`) or `CrucibleChannelEnd` (`0`)
  - `0x05` + `u8 state` (2B): FMOD encounter state (`>= 3` decays threat shader; `2` closes gamepad popup)
  - `0x06` + `u32 LE forgeEntityId` (5B): binds gamepad popup to forge entity

*(Note: opcodes `0x291`–`0x295`, C→S `0x294`, and C→S `0x296` belong to Recombinator, Map Device, Sentinel, and Deepwater—not Crucible.)*

## Client Tables & World Objects

- **Area generation:** Setting 1-based area stat `17912` (`map_crucible_league`, `Stats._row 17911`) causes
  `ExtraTerrainFeatures.Roll` to add `"Crucible"` (row 45, `crucible_encounter.arm`), placing `CrucibleDevice` in `RoomObjects`.
- **Object types missing from `client.db` `object_types`:** `CrucibleDevice` (`Positioned`, `BaseEvents`, `InteractionAction`,
  `Animated`, `TriggerableBlockage`, `MinimapIcon`, `StateMachine` [`activated`, `channelling`, `in_zone`], `ControlZone`)
  and `CrucibleCombiningBench` (`Positioned`, `BaseEvents`, `Animated`, `InteractionAction`, `Transitionable`, `MinimapIcon`,
  placed in `arena_01.tmd` → `TileQuestObjects`).
- **Other Crucible `.ot` states:**
  - `Fissure`: `crucible_fissure` (`0` paused, `1` start, `2` idle/channelling, `3` end), `channelling` (`0`/`1`).
  - `MoltenPool`: `crucible_pool` (`0` hidden, `1` grow, `2` shrink).
  - `CrucibleUniqueForge`: `removetreeforge` (`0` Antique Furnace, `1` Scouring Furnace); `Transitionable` `2` disables after use.
  - `CrucibleEndgameMonstersSpawnerBase`: `pool` (`1` active); `Transitionable` `2` ends.
- **Tables used by `Modules/Legacy/Crucible`:** `WeaponPassiveSkills` (2632 rows; `GenerationType 31`/`1` normal, `32` combine-only
  mutations; `Type 0` Regular / `1` Notable / `2` SellBonus), `WeaponPassiveTreeBalancePerItemLevel` (100 rows, ratio `1:3:9:18:36`),
  `WeaponPassiveTreeUniqueBaseTypes` (172 rows), `CrucibleDifficulty` (5 tiers: `30/55/75/90/100`), `CrucibleTags` (6 themes -> 65
  `Metadata/Monsters/LeagueCrucible/*` varieties), `CrucibleEndgameMonsterPacks` (8 tiers), `CrucibleLifeScalingPerLevel` (100 rows).
- **Geodes & Remnants:** Right-clicking `Igneous Geode` (`4809`) rolls a rare corrupted `Primeval Remnant` (`2970`, ilvl 80, Domain 33
  mods, `MapDeviceRecipes` 52 → `CrucibleLeagueArea` `1909`). Right-clicking `Crystalline Geode` (`4810`) rolls a rare corrupted
  `Primordial Remnant` (`2971`, ilvl 84, +1 max prefix, `MapDeviceRecipes` 53 → `CrucibleLeagueAreaUber` `1910`).
