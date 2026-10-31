# Timeless jewels (Legion)

**Status: implemented and checked live (2026-10-29) for the item, radius tree, node stats and Militant Faith's
per-Devotion item mods; the conqueror keystone mechanics are not modelled yet.** This page records what the target
3.29.3b client does with a timeless jewel, what the server reproduces, and what remains. It is the reference for the
[unique-jewels roadmap](../../roadmap/unique-jewels.md) phase 5 and
[status/progression.md](../../status/progression.md).

The full generated list of alternate keystones, notables, small passives, additions and Militant Faith devotion mods,
with a works/partial/inert note for each, is [timeless-jewel-list.md](timeless-jewel-list.md).

## The item

All six timeless jewels share the base type `Metadata/Items/Jewels/JewelTimeless` (`Timeless Jewel`). The unique name
comes from `Words` (wordlist 6), and the art from `UniqueStashLayout` / `ItemVisualIdentity`
(`UniqueJewelVisuals.cs` already has all six):

| Jewel | Visual | Faction (`AlternateTreeVersions`) |
|---|---|---|
| Glorious Vanity | `UniqueJewel128` | Vaal (1) |
| Lethal Pride | `UniqueJewel129` | Karui (2) |
| Brutal Restraint | `UniqueJewel130` | Maraketh (3) |
| Militant Faith | `UniqueJewel131` | Templar (4) |
| Elegant Hubris | `UniqueJewel132` | Eternal (5) |
| Heroic Tragedy | `UniqueJewel185` | Kalguur (6) |

A timeless jewel carries **one conqueror mod**, one of these `Mods.dat` rows. Militant Faith also carries **two distinct
per-10-Devotion mods** (see below); those are ordinary explicit mods on the item, not extra alternate-tree stats.

| Mod Id | HASH16 | Faction |
|---|---:|---:|
| `UniqueJewelAlternateTreeInRadiusVaal` | 9428 | 1 |
| `UniqueJewelAlternateTreeInRadiusKarui` | 4316 | 2 |
| `UniqueJewelAlternateTreeInRadiusMaraketh` | 49718 | 3 |
| `UniqueJewelAlternateTreeInRadiusTemplar` | 21706 | 4 |
| `UniqueJewelAlternateTreeInRadiusEternal` | 14635 | 5 |
| `UniqueJewelAlternateTreeInRadiusKalguur` | 8305 | 6 |

The mod has six stats, and the item's values are passed in this order:

| # | Stat Id (`Stats.dat`) | Value |
|---:|---|---|
| 1 | `local_unique_jewel_alternate_tree_version` | faction, 1..6 |
| 2 | `local_unique_jewel_alternate_tree_seed` | seed (see the ranges below) |
| 3 | `local_unique_jewel_alternate_tree_keystone` | conqueror index, 1..3 |
| 4 | `local_jewel_effect_base_radius` | 1500 |
| 5 | `local_is_alternate_tree_jewel` | 1 |
| 6 | `local_unique_jewel_alternate_tree_internal_revision` | 1 for current items |

All six stats are `IsLocal`, so the existing `ItemModStats.Global` path does not treat them as character stats. The
client reads them from the item's mod list and computes the conquered nodes itself. **No new protocol field and no
changed node ids in 0x33 are needed**; a correct item is enough for the client to draw the transformed tree. The client
address `0x141f035e0` reads the three tree stats from the item and `0x141f07ff0` builds the alternate passive; this was
confirmed by decompiling the 3.29.3b client.

The seed stored in the mod is the **real** seed. `Elegant Hubris` stores 100..8000 and the client shows it as
`×20` (2000..160000), so a command that accepts the displayed seed must divide it by 20 before creating the item.

### Militant Faith devotion mods

Militant Faith is the only timeless jewel with a second random-mod group. Every item carries **two distinct mods** from
the client's `...PerDevotion` pool. The target 3.29.3b `Mods.dat` has 16 such rows: 15 current and
`ReducedManaCostPerDevotion`, the pre-3.29.0 "1% reduced Mana Cost of Skills" row that is kept for legacy items
(Path of Building's `jewel.lua` labels it `Pre 3.29.0`). The
3.15 wiki note ("1% reduced Mana Cost of Skills per 10 Devotion") is therefore two changes behind: 3.29.0 replaced that
row with `ManaCostEfficiencyPerDevotion` ("3% increased Mana Cost Efficiency per 10 Devotion"). The server creates two of
the 15 current rows, so the item tooltip and the client's item data are complete.

`DevotionStats` then scales each per-10-Devotion stat by `floor(Devotion / 10)` into the normal stat it names. At 145
Devotion the resistance row adds `2 × 14 = 28%` to all elemental resistances. The working targets are elemental and area
damage, all-elemental resistance, aura effect, mana regeneration, and (panel-only) non-damaging ailment effect and
self elemental ailment duration. The targets with no server consumer remain inert: curse duration, totem/brand/channelled
damage, minion speed/accuracy, mana cost (legacy and efficiency) and shield defences. The full 16-row list is in
[timeless-jewel-list.md](timeless-jewel-list.md) (section "Militant Faith devotion mods").

## Seeds and conquerors

| Jewel | Seed in the mod | Current conquerors (index/version → keystone) | Legacy conqueror (index/version → keystone) |
|---|---|---|---|
| Glorious Vanity | 100..8000 | Xibaqua 1/v0 → Divine Flesh; Ahuana 2/v1 → Immortal Ambition; Doryani 3/v0 → Corrupted Soul | Zerphi 2/v0 → Eternal Youth |
| Lethal Pride | 10000..18000 | Kaom 1/v0 → Strength of Blood; Rakiata 2/v0 → Tempered by War; Akoya 3/v1 → Chainbreaker | Kiloava 3/v0 → Glancing Blows |
| Brutal Restraint | 500..8000 | Balbala 1/v1 → The Traitor; Asenath 2/v0 → Dance with Death; Nasima 3/v0 → Second Sight | Deshret 1/v0 → Wind Dancer |
| Militant Faith | 2000..10000 | Maxarius 1/v1 → Transcendence; Dominus 2/v0 → Inner Conviction; Avarius 3/v0 → Power of Purpose | Venarius 1/v0 → The Agnostic |
| Elegant Hubris | 100..8000 (shown ×20) | Cadiro 1/v0 → Supreme Decadence; Victario 2/v0 → Supreme Grandstanding; Caspiro 3/v1 → Supreme Ostentation | Chitus 3/v0 → Supreme Ego |
| Heroic Tragedy | 100..8000 | Vorana 1/v0 → Black Scythe Training; Uhtred 2/v0 → Celestial Mathematics; Medved 3/v0 → The Unbreaking Circle | none (new jewel) |

The version (`v0`/`v1`) selects between the old and current alternate keystone of the same conqueror index in
`AlternatePassiveSkills` (`ConquerorIndex` is `__col18`, `ConquerorVersion` is `__col24`). Current items have internal
revision 1 and use the current row where one exists; a legacy item (revision 0) uses the v0 row. The pre-3.11
conquerors (Zerphi, Kiloava, Deshret, Venarius, Chitus) are legacy items and are not offered by `/timeless`.

## How the client replaces nodes

The game tables are `AlternateTreeVersions.json`, `AlternatePassiveSkills.json` and
`AlternatePassiveAdditions.json`. Vilsol's Go implementation (`https://github.com/Vilsol/timeless-jewels`) reproduces
the client's generation; the same algorithm is used by Path of Building's precomputed LUTs
(`src/Data/TimelessJewelData/`).

1. **RNG.** TinyMT32, seeded from the pair `[PassiveSkillGraphId, seed]` (for Elegant Hubris the stored seed is used
   directly; the ×20 is display only).
2. **Node classification.** Keystone, notable, small attribute and small normal. A small node is an attribute node when
   it has exactly one stat and the client stat key is one of Strength, Dexterity or Intelligence. The client checks
   `(key - 576)` against mask `0x49` (keys 576, 579 and 582), which is the authoritative rule.
3. **Replacement chance.** Keystones always replace. Notables replace with the weight in
   `AlternateTreeVersions.__col9` (0, 20 or 100). Small attribute/normal nodes replace according to `__col1`/`__col2`.
   A node that is not replaced still gets **additions**.
4. **Candidate choice.** `AlternatePassiveSkills` rows are grouped by `AlternateTreeVersionsKey` and `PassiveType`
   (1 small attribute, 2 small normal, 3 notable, 4 keystone, 5 jewel socket). The first matching row with the
   conqueror index/version is the keystone. Other candidates are chosen by `SpawnWeight` in table order, using the same
   weighted loop as Vilsol (`GenerateSingle(currentWeight) < skill.SpawnWeight`).
5. **Values.** Each candidate has one or two stats (`StatsKeys` are 0-based `Stats.dat` row indices; the client key is
   row + 1). `Stat1Min`/`Stat1Max` and `Stat2Min`/`Stat2Max` are the roll range. `RandomMin`/`RandomMax` add extra
   additions to the base addition range from `AlternateTreeVersions`.
6. **Additions.** `AlternatePassiveAdditions` rows are likewise filtered by tree version and passive type. The count is
   the version's `__col3`/`__col4` (small attribute), `__col5`/`__col6` (small normal) or `__col7`/`__col8` (notable),
   plus the candidate's `RandomMin`/`RandomMax`. Each addition is chosen by `SpawnWeight`; its up to two stats are
   rolled.

Special rows:

- `vaal_notable_random_offense` / `vaal_notable_random_defence` have no base stats and 3-4 random additions.
- `eternal_small_blank` replaces small nodes of Elegant Hubris with nothing ("Price of Glory").
- Karui and Maraketh do not replace smalls/notables; they only add their stat (`AlternatePassiveAdditions`).
- Kalguur replaces notables, and adds Ward to smalls.

### Notable additions worth calling out

`AlternatePassiveAdditions` has 337 rows in the client, but only **96** belong to the six timeless factions; the other
241 are abyss-jewel rows. Four of the 96 are absent from Path of Building's `LegionPassives.lua` and were missing from the
first generated list:

| Row | Actual stat | Server |
|---|---|---|
| `karui_notable_add_faster_burn` | 10% faster Ignite damage (`faster_burn_%`) | inert (no consumer) |
| `karui_notable_add_physical_taken_as_fire` | Gain 1 Rage on Melee Hit (`gain_x_rage_on_melee_hit`; the id is a legacy name) | works (`RageStats`) |
| `maraketh_notable_add_ailment_avoid` | 20% chance to avoid being Stunned (`base_avoid_stun_%`; the id is a legacy name) | works |
| `maraketh_notable_add_flask_effect` | 25% chance to gain Alchemist's Genius when you use a Flask (`gain_alchemists_genius_on_flask_use_%`) | inert (no consumer) |

`karui_notable_add_fortify_effect` is **+1 to maximum Fortification** (`base_max_fortification`) in the 3.29.3b data,
not the "5% increased Fortify Effect" in the 3.15 patch note; `FortificationState` reads the stat, so the maximum works.

The server must apply the **replacement** stats instead of the node's original stats, and add the **addition** stats on
top. Only allocated ordinary nodes matter for the character's stats; the client draws all nodes in the radius itself.

## Radius and geometry

Timeless jewels use a fixed Large radius: `0..1500`. The client's radius check is
`innerSquared <= distanceSquared <= outerSquared`; for a fixed large radius the inner radius is 0.

Node positions are already in the packed `PassiveSkillGraph` (`PassiveSkillGraph.cs`): each group has `X`/`Y` and each
node has `Orbit`/`OrbitIndex`. The extractor currently drops them from `data/passive-skill-graphs.json`; it must emit
them (planned `passive-graphs` v5). The client computes a node's position as:

```text
angle = 2π * OrbitIndex / skillsPerOrbit[Orbit]
x = group.X + sin(angle) * orbitRadii[Orbit]
y = group.Y - cos(angle) * orbitRadii[Orbit]
```

The 3.29 constants (matching Path of Building's `src/TreeData/3_29/tree.lua` and the PSG orbit list) are:

```text
skillsPerOrbit = [1, 6, 16, 16, 40, 72, 72]
orbitRadii     = [0, 82, 162, 335, 493, 662, 846]
```

Nodes excluded from the radius: class starts, jewel sockets, ascendancy nodes, masteries, proxy/expansion nodes, and
cluster-jewel passives (they are a separate subgraph and have no position in the ordinary PSG). The socket itself is
also excluded.

## Server implementation

1. **Geometry and item.** `scripts/re/passive-graphs` v5 emits `normal.groups` (group X/Y and each node's orbit/orbit
   index); `PassiveTree` computes positions with the client's orbit radii and answers `NodesInRadius`. `TimelessJewelCatalog`
   creates the unique item, writes the six mod values and parses them back. The dev command is
   `/timeless <name> [displayed seed] [conqueror] | list`.
2. **Generator.** `TimelessJewelEngine` ports TinyMT32 and the replacement/addition rules. The RNG matches the reference
   sequence for graph 26725 / seed 2000; the generated results match Path of Building's LUTs for a Vaal small (node
   62429, seed 2000: Aura Effect 3) and a Vaal notable (node 37326: Cult of Fire, 30 fire resistance and 1 maximum).
3. **Stats.** `EquipmentStatSources` builds an effective per-node stat list: for each allocated node in a timeless
   radius, the original stats are removed and the alternate stats are used, or the additions are appended. The same list
   feeds the attribute pass. `StatSourcesKey` includes the tree jewel's pane cell (`X`), because the socket position is
   part of the result.
4. **Limits.** A timeless jewel is accepted only in an ordinary tree socket (not a cluster-expansion cell), and only one
   Historic timeless jewel may be socketed at a time.
5. **Verification.** Live: `/timeless Glorious Vanity 2000 Xibaqua`, socketed in pane cell 1 (socket node 36634); the
   client log shows `ALTERNATE TREE JEWEL: Applying modifications for jewel with alternate tree version = 1, seed =
   2000, and keystone selection = 1`, the tree draws the radius and the transformed nodes, and removing/adding the jewel
   changes life 5969 -> 5861 and mana 2293 -> 2234. The same state survives relog. Live Devotion check: the Militant
   Faith above (seed 3304, Maxarius) has 145 Devotion in pane cell 0; `/resistances` changed from 235/193/193 to
   263/221/221 (+28 each, `floor(145 / 10) × 2`) while the capped values stayed 84/84/83. Unit tests cover the RNG,
   item values, the two distinct Militant Faith devotion mods, the Devotion scaling (145 -> +28% resistance, 9 -> 0),
   the resistance pool integration, the PoB LUT vectors, the radius set, the pool change, the one-Historic limit,
   Karui/Maraketh/Templar/Eternal/Kalguur shapes and the Elegant Hubris blank small.

The client must be launched only through `scripts/dev/live.ps1`; the verification uses the real client and `logs/`.

## Scope and remaining work

- **Keystones.** Their stat rows are applied (the client shows e.g. Divine Flesh and the server removes the original
  keystone's stats), but the special gameplay mechanics of the conqueror keystones are not modelled. Those need separate
  consumers (damage conversion, recovery, charge rules, etc.).
- **Legacy conquerors** (Zerphi, Kiloava, Deshret, Venarius, Chitus) and their keystones are listed in the table above
  for reference, but the command creates the current variants only.
- **Militant Faith devotion mods.** The item carries two distinct current per-10-Devotion mods, and `DevotionStats`
  scales them by `floor(Devotion / 10)` into the normal stats. Elemental/area damage, all-elemental resistance, aura
  effect, mana regeneration and two panel-only ailment stats are consumed; curse duration, totem/brand/channelled damage,
  minion speed/accuracy, mana cost and shield defences still have no consumer. The legacy
  `ReducedManaCostPerDevotion` row is not rolled on current items.
- **Elegant Hubris seed input.** The command accepts the displayed seed (2000..160000 in steps of 20) and stores
  `seed / 20` in the item, matching the tooltip. The other jewels accept their stored seed.
- **Cluster sockets.** Timeless jewels are accepted only in ordinary tree sockets; a cluster-expansion socket has no
  ordinary PSG position for the radius.
- **Multiple timeless jewels.** The server enforces one Historic timeless jewel in the passive tree, matching the item
  limit. If the abyss Historic jewels become socketable, the same one-jewel rule must cover them too.
- **Conquered nodes.** The client does not let another jewel modify a passive conquered by a timeless jewel; the server
  must enforce that when a general radius-jewel model lands.

## References

- Client data: `data/tables/Mods.json`, `AlternateTreeVersions.json`, `AlternatePassiveSkills.json`,
  `AlternatePassiveAdditions.json`, `PassiveSkills.json`, `PassiveJewelSlots.json`, `UniqueJewelLimits.json`.
- Client code: `0x141f035e0` (reads the item's tree stats and iterates the tree), `0x141f07ff0` (alternate passive
  generation), `0x1421df6e0` (Mods component reader).
- Path of Building: `src/Modules/DataLegionLookUpTableHelper.lua`, `src/Classes/PassiveSpec.lua`,
  `src/Data/Uniques/jewel.lua`, `src/Data/TimelessJewelData/`.
- Reference generator: `https://github.com/Vilsol/timeless-jewels` (`calculator/tree_manager.go`,
  `random/main.go`, `data/jewels.go`).
- Wiki articles: [Glorious Vanity](https://www.poewiki.net/wiki/Glorious_Vanity),
  [Elegant Hubris](https://www.poewiki.net/wiki/Elegant_Hubris), [Lethal Pride](https://www.poewiki.net/wiki/Lethal_Pride),
  [Brutal Restraint](https://www.poewiki.net/wiki/Brutal_Restraint),
  [Militant Faith](https://www.poewiki.net/wiki/Militant_Faith),
  [Heroic Tragedy](https://www.poewiki.net/wiki/Heroic_Tragedy). The current wiki is bot-protected; the older Fandom
  mirror and PoEDB's Heroic Tragedy page supplied the historical 3.7-3.15 and Kalguur details.
