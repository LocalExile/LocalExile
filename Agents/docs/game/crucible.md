# Crucible

Crucible (3.21) gives weapons and shields a passive skill tree. A Crucible Forge in an area is channelled to form
molten monsters; killing them pays experience into the chosen item. Geodes open into Remnants, which open Forge of the
Titans, where two trees are combined. Module: `Modules/Legacy/Crucible`, listed by the `Crucible` league.

## 1. What the 3.29 client lacks

The 3.29 client still has the tree on items, the Forge popup, both rulesets, the combining window and every object
and monster. GGG removed the popup's conversation with the server. In 3.21.2b (handler `0x140de7760` of that build)
the ruleset `CrucibleRules` (row `0x89`) had these messages; 3.29 renumbered what it kept:

| Direction | 3.21 op | Payload (little-endian) | Meaning | 3.29 op |
|---|---|---|---|---|
| S→C | 0 / 1 | - | player in / out of the Forge's ring: popup fades in / out | `0x00` / `0x01` |
| S→C | 2 | u32 item id, u32 inventory id | echo of the chosen item: opens its tree page; zeros = weapon page | removed |
| S→C | 3 | - | weapon locked | `0x02` |
| S→C | 4 | i32 | experience preview on the bar | removed |
| S→C | 5 | f32 charge 0..1 | difficulty bar, channel action, threat shader | `0x03` |
| S→C | 6 | u8 | channel sound on / off | `0x04` |
| S→C | 7 | u32 entity id, u8 levelUp | completion effect on the player, item highlight | removed |
| S→C | 8 | u8 state | encounter state (2 closes the gamepad popup, 3 and up fades the threat) | `0x05` |
| S→C | 9 | u32 forge entity id | gamepad popup | `0x06` |
| C→S | 0 | u32 item id, u32 inventory id | item chosen; zeros = Back | removed |
| C→S | 1 | u8 column, u8 row | skill pressed, for the item sent last | removed |

- S→C bodies travel in `0x02c9` (`u16 0x02c9`, `u8 row`, compact length, body); `0x001f` adds the ruleset. The C→S
  ones were ordinary ruleset actions (3.29 id `0x02ca`). 3.21 had no "stop": the release was generic skill input.
- In 3.29 the two popup delegates are empty (`0x1400f1180`), and nothing calls the popup's page switch
  (`0x1403e93b0`), so its page index stays 2 and the weapon list is empty. `CrucibleWeaponSelect` and `CrucibleBack`
  are sound ids, not handlers.
- So the server picks: the first eligible of main hand, off hand and the swap set, and for each unlocked column the
  first connected skill that leads on. It also reads both 3.21 actions, should a changed client send them.
- With no popup to click and hold, the player channels with `crucible_channel`, which the server puts on the first
  free skill bar slot.
- The shown difficulty is `100 x charge^0.65` (seen live: "29% Easy" at charge 0.16, "48% Moderate" at 0.31);
  `CrucibleDifficulty` names the steps 30 / 55 / 75 / 90 / 100.
- A held channel sends its start and, on release, C→S `0x0199`; a predictive client also sends `0x0199` by itself
  when another action (a click to move) starts under the held key. The popup's click-and-hold repeats the skill use
  every frame and sends nothing on release.
- In lockstep the start is `0x018f` on the player and the release `0x0195`, or `0x0196` with the skill while a mouse
  button is down. Another action under the held key sends only its own `0x018e`, so the server ends the hold on it.

## 2. The tree on an item

`Mods` flag bit 7 (`0x0080`), after `ObjectMagicProperties` (writer `0x1424bd880`, reader `0x1424bdb90`), little-endian:

1. `u32` balance item level: the row of `WeaponPassiveTreeBalancePerItemLevel`. The bars are never sent.
2. `u32` experience. **`Bar1..Bar5` are cumulative totals**: depth = how many bars the experience has reached
   (`0x1424bcd20`). A full tree at item level 86+ needs 4,184,388.
3. `u8` has tree. 0 ends here (experience towards the first bar).
4. `u16` layout: 15 slots, `bit = column * (column + 1) / 2 + row`, columns 0..4, rows 0..column. Bit 15 must be 0.
5. `u16` allocation, at most one per column. Only the server allocates: the client applies these bits on load and
   has no message for allocating (`0x1424bcea0`).
6. `u16` left parents (to `(column - 1, row - 1)`), `u16` right parents (to `(column - 1, row)`).
7. `i8[5]` pending row per column, `-1` none: the skill the experience goes towards. The client keeps one only for
   a locked column and wipes it when the bar is reached (`0x1424bce1f`).
8. One mod per layout bit, in bit order: `u16` Mods hash, then its values.

- Which items: a weapon class, Shield or FishingRod, any rarity (`0x1424b1c30`); at a Forge not corrupted or
  mirrored, and a unique only with a tree already (ClientStrings 6786).
- `WeaponPassiveTreeUniqueBaseTypes` is not an eligibility list: it names the bases of the "sells for an additional
  Unique {0}" skill (`__col1` = the stat's value, 0 = the item's own base).
- `Mods.GenerationType` 32 is recombined-only (schema enum): those skills appear only through combining.
- C→S `0x0116` (26 bytes, big-endian, `0x141fa09e0`): `u32` currency inventory, `u32` currency item, `u32` target
  inventory, `u32` target item, `u32` column, `u32` row. Orb of Scouring on the deepest allocated skill; on 3.29 only
  the gamepad UI sends it. The client does not check "deepest" (ClientStrings 6787), the server does.

## 3. Objects and monsters, as seen live on 3.29.3d

- Area stat `map_crucible_league` makes `ExtraTerrainFeatures` add the `Crucible` room with `CrucibleDevice`
  (states `activated`, `channelling`, `in_zone`; `breakout_wave` plays 1.0 s after `activated = 1`).
- `Fissure`: `crucible_fissure` 1 start, 2 idle, 3 end. One is a straight piece about 4.5 cells long, lying across
  its facing. Pieces further than about 21 cells from the device did not draw.
- `MoltenPool`: `crucible_pool` 1 grows, 2 shrinks.
- Monsters (`Metadata/Monsters/LeagueCrucible/`, 10 per folder, the last two bosses): held at the Forge by buff
  `crucible_frozen` with visual `crucible_stasis`, `_magic`, `_rare` or `_unique`; `broken_out = 1` with
  `crystal_size` 0..3 adds the crystals. `broken_out = 0` looks like the plain monster. The held ones stay dark.
- `CrucibleCombiningBench`, `CrucibleEndgameMonstersSpawnerBase` (`pool = 1`) and `CrucibleDevice` are created
  fine, though the object register names only the spawner. `Metadata/NPC/Info/Crucible_GlyphRegular` stops the client
  ("Unknown object type serialized by server"); `NPCs` lists it under the Id `MoltenOneGlyph01` (not tried).

## 4. Forge of the Titans

- Right-click on an Igneous / Crystalline Geode makes a rare Primeval / Primordial Remnant (item level 80 / 84 from
  `DropLevel`, six / seven domain 33 mods, not corrupted). `MapDeviceRecipes` 52 / 53 open `CrucibleLeagueArea` /
  `CrucibleLeagueAreaUber` (area level 80 / 84).
- `CrucibleMapRules` (row `0x8c`) is only the area's music (`0x141682250`); it has no messages.
- The bench comes from `arena_01.tmd`. A click answers S→C `0x7b` kind `0x4a`, `arg1` 0 normal, 1 uber
  (`0x1403e16f0`). The bench is `Transitionable`: any transition makes it untargetable.
- C→S `0x0299` (18 bytes, big-endian): `u32` top inventory id, `u32` top item id, `u32` bottom inventory id, `u32`
  bottom item id. The top item is destroyed. Equipped items can be put in.
- S→C `0x029a`: `u32` inventory id, `u32` item id of the result, compact count, then per changed skill `u32` column,
  `u32` row, `u8` flags: `0x01` upgraded tier, `0x02` downgraded, `0x04` mutated. Sent after the `0x00bf` updates;
  the client plays the forge video and opens the result's tree with those skills marked.
- The window enables Forge only when (`0x1424bb530`, `0x1424bb770`, `0x1424bb850`): each item is identified, has a
  tree and no socketed gem, is not mirrored and has no skin transfer; the bottom item is modifiable; both classes share
  an `ItemClassCategory` (sword + thrusting sword, mace + sceptre, staff + warstaff; not sword + axe). The normal bench
  refuses unique and corrupted items; the uber bench takes both, uniques only as two of the same unique with the same
  explicit mods. Seen live: "Items are not of the same item class category.", "Item's Sockets are not empty.",
  "Target is corrupted.". The server checks the same.
- `CrucibleEndgameMonsterPacks`: read as `__col1[i]` = how many of a theme's monster number i + 1, `__col3` = weight.
- Not built: the Antique and Scouring Furnace (`0x7b` kind `0x4b`, C→S `0x029b`), the Remnant mods for drops and
  for sockets, links, rerolled values and corruption of the forged item, Remnant-scaled monsters, 3.21.0c's "no
  downside skill as the first skill", the bench for the map's owner only (any member combines).

## 5. Our own numbers

GGG published none of these and no client table holds them:

- Tree: depth chances by item level, skills per column, no skill or mod family twice, a third each for the links.
- Forge: ring radii, 10 s to full charge, 32 steps, fissure and pool places, three monsters merge into the next
  tier, rarity per tier, damage and damage taken by charge, the default target and skill, the last holder letting
  go starts the wave, a channeller who dies gets no experience.
- Experience: `Bar1(area level) x sum(ExperienceMultiplier of the monsters killed) / 1650`; what scouring leaves.
- Geode chance and area levels; the experience in a sell-node Magmatic Ore.
- Combining: 93% / 70% that a skill stays when both trees / one tree had it, 70% for the allocated side, 6.6%
  mutated, 2.1% tier up, 1.6% tier down (counted by a player over 30 to 40 combines); the result's experience and
  allocations; a kept skill has the links of both trees, the first skill always stays, new skills go by the higher
  item level; what "more likely to be retained" adds; 30% empty spawner spots and a random theme per spot.
- `CrucibleTags` to monster folders is our map by name (Demon = `CriticalSpeed`).
