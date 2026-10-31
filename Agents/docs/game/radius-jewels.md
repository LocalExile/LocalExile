# Radius unique jewels (excluding timeless and cluster)

**Status: the six attribute-conversion jewels, five damage/defence-conversion jewels, three notable-replacement
jewels, ten passive-addition jewels, eleven per-node-scaling jewels, one passive-effect/notable-suppression jewel,
three allocation/tree-rule jewels and three special/global-only jewels (Might in All Forms, Pitch Darkness, Sudden
Ignition) are implemented (`/radius_jewel`). The 33 threshold rows now have generated item sources, the client applies
their local threshold mods and the server counts the radius attributes and adds the mapped target stats; the
skill-specific consumers remain partial. Lioneye's Fall has an item source and client conversion, but its server flag
model is missing; Inspired Learning and Warrior's Tale remain research/design. Impossible Escape is a generated special
outside the 78-row PoB list and is implemented with 48 keystone variants (`/impossible_escape`).** The socket, save and global-stat path
already works,
and the timeless jewels provide a reusable
radius/per-node pattern. This page records every radius jewel, the mechanics it needs, what can be reused from the
timeless work, and the implementation order. The full generated table is
[radius-jewel-list.md](radius-jewel-list.md); the complete mod text is also in [unique-jewels.md](unique-jewels.md).

## Scope and data

The target 3.29.3b Path of Building data has **78 unique-jewel rows with a `Radius:` line**, or **76 distinct names**
(Combat Focus has three colour variants). This page excludes:

- the **6 timeless jewels** (Glorious Vanity, Lethal Pride, Brutal Restraint, Militant Faith, Elegant Hubris, Heroic
  Tragedy), documented in [timeless-jewels.md](timeless-jewels.md);
- the **8 cluster jewels** (Voices and the seven keystone cluster jewels), documented in
  [cluster-jewels.md](cluster-jewels.md).

The rows come from Path of Building's `src/Data/Uniques/jewel.lua`, cross-checked with the client tables
`PassiveJewelRadii`, `PassiveJewelSlots`, `UniqueJewelLimits`, `BaseItemTypes` and `Mods`. The generated list has one row
per item row with its base, radius, limit, variant flag, mechanism, full effect and current server status.

## Radius geometry

`PassiveJewelRadii` gives each size's disc radius. Thread of Hope uses Path of Building 3_16's **Variable** rings, which
are the current tree's annulus values:

| Size | Disc radius | Thread ring inner | Thread ring outer | Used by |
|---|---:|---:|---:|---|
| Small | 800 | 960 | 1320 | Intuitive Leap, Unnatural Instinct, Coated Shrapnel, Martial Artistry, Fevered Mind, Inspired Learning, Thread "Small Ring" |
| Medium | 1200 | 1320 | 1680 | Most threshold/notable jewels, Thread "Medium Ring" |
| Large | 1500 | 1680 | 2040 | Attribute/defence conversions, Might of the Meek, Dreams/Nightmares, Light/Perandus, Thread "Large Ring" |
| Very Large | 2000 | 2040 | 2400 | Thread of Hope "Very Large Ring" |
| Massive | 2400 | 2400 | 2880 | Thread of Hope "Massive Ring" (Uber) |

A normal radius jewel affects nodes in the **disc** `0..Radius`. Thread of Hope selects one **ring**; only passives whose
distance is between the inner and outer radius are affected. The client's test is `distanceSquared <= radiusSquared`
for a disc; the ring variant uses `innerSquared <= distanceSquared <= outerSquared`, as Path of Building does. The
server has `PassiveTree.NodesInRadius(socket, radius)` and `NodesInRing(socket, inner, outer)`; the latter is used by
Thread of Hope's allocation rule. `PassiveJewelRadii`'s own `RingInnerRadius`/`RingOuterRadius` fields (800/1100,
1100/1400, ...) are the older 3_15-style values; the current client tree uses the 3_16 Variable values above, which is
why `RingRadii` returns 960-1320, 1320-1680, 1680-2040, 2040-2400 and 2400-2880. PoB selects the ring from its
`Variable` radius entry; the server uses the item's `local_jewel_variable_ring_radius_value` (1-5).

For stat effects, the same node exclusions as the timeless radius apply: class starts, jewel sockets, ascendancy nodes,
masteries, proxy/expansion nodes and cluster-jewel passives are not ordinary passive-tree nodes for this purpose. The
socket itself is excluded. The allocation bypass may target any ordinary passive node in the disc/ring, including jewel
sockets, notables and keystones, but not starts, ascendancy, masteries, proxy/expansion or cluster nodes. A passive
conquered by a timeless jewel cannot be modified by another jewel; the client enforces this and Path of Building's
radius functions skip `node.conqueredBy` nodes, so the radius layer must do the same.

**Allocated vs unallocated.** Most radius jewels only change the stats of **allocated** passives. Three mechanics also
read **unallocated** nodes:

- Unnatural Instinct: allocated small passives in radius grant nothing; unallocated small passives in radius grant their
  bonuses.
- Tempered/Transcendent Flesh/Mind/Spirit: part of the effect counts attributes on **unallocated** passives in radius.
- The threshold jewels count "X in Radius"; the implementation must use the client's allocated-only rule (Path of
  Building's `getThreshold` accumulates the node stats it iterates, so use it as the reference and verify the exact set
  against the client).

## What already works

- A unique jewel in an ordinary tree socket is placed, swapped, picked up, saved and restored by the existing passive
  jewel path. A normal jewel's global (non-local) mods are summed by `ItemModStats.Global`; the socket must be allocated
  and the level requirement met.
- `PassiveTree` has node positions from `passive-graphs` v5 and `NodesInRadius` for the timeless 0..1500 disc.
- `TimelessJewels.Alternate` and `EquipmentStatSources` show the per-node override pattern: build a `nodeStats` map for
  allocated nodes, replace or append stats, and let the pools/panels consume the result.
- `Program.PassiveJewelRefusal`, `PlayerInventories.PassiveJewels`, the item limits and `CharacterSaves` are shared.
- `UniqueJewelVisuals.cs` already contains the art for all 190 unique jewels, so a radius item can use the correct icon
  without new art work.
- The 42 "Global" unique jewels have a dev source (`/unique_jewel`); the six attribute-conversion, five
  damage/defence-conversion, three notable-replacement, ten passive-addition, eleven per-node-scaling, one
  passive-effect/notable-suppression, three allocation/tree-rule and three special/global-only jewels have
  `/radius_jewel` (`RadiusJewelCatalogue`), and their server-side effects run in `EquipmentStatSources`. The 33
  threshold items are generated into `ThresholdJewelCatalogue`; the client applies their local 40-attribute mods, while
  the server threshold layer is still partial. Lioneye's Fall, Inspired Learning and Warrior's Tale still have no item
  source.

A hand-made/saved item with plain global mods would already apply those mods; a threshold item's client mods are now
present, but the server's own threshold counting is not. This is why the generated list distinguishes the 42 rows with
server-side effects from the 33 threshold items.

## Pilot: attribute conversion (implemented)

`/radius_jewel <name> | list` creates the pilot items, max-rolled, with the unique name word, the correct art and the
client mods:

| Jewel | Attribute mod | Conversion mod | Conversion |
|---|---|---|---|
| Brute Force Solution | `IntelligenceUniqueJewel35` | `JewelStrToIntUniqueJewel35` | Strength -> Intelligence |
| Careful Planning | `DexterityUniqueJewel36` | `JewelIntToDexUniqueJewel36` | Intelligence -> Dexterity |
| Efficient Training | `StrengthUniqueJewel34` | `JewelIntToStrUniqueJewel34` | Intelligence -> Strength |
| Fertile Mind | `IntelligenceUniqueJewel11` | `JewelDexToIntUniqueJewel11` | Dexterity -> Intelligence |
| Fluid Motion | `DexterityUniqueJewel13` | `JewelStrToDexUniqueJewel13` | Strength -> Dexterity |
| Inertia | `StrengthUniqueJewel37` | `JewelDexToStrUniqueJewel37` | Dexterity -> Strength |

The conversion mod is local: it carries `local_jewel_nearby_passives_<pair>` and `local_jewel_effect_base_radius`
(1500, Large). `RadiusJewelCatalogue.ReadConversion` reads the pair and radius; `EquipmentStatSources` applies the same
stat move to every allocated passive in that radius, after the timeless layer and skipping a node conquered by a
timeless jewel. Several conversion jewels apply in socket order. The item's global `+(16-24)` attribute mod is a normal
global stat and works through `ItemModStats.Global`. A radius jewel is refused in a cluster-expansion socket.

Live check with Brute Force Solution in socket 1 (node 36634), with the Militant Faith still in socket 0:

- Strength **559 -> 549** (-10, the converted Strength smalls in radius);
- Intelligence **647 -> 681** (+34 = 10 converted + the item's max-rolled +24);
- Dexterity unchanged;
- life 5873 -> 5857 and mana 2293 -> 2326 (the same attribute move, not a separate effect).

The client panel showed the exact change. Unit tests cover the six mod pairs, the item name/word/visual, the conversion
at a known Strength node, the full pools, and the cluster-socket refusal.

## Pilot: damage and defence conversion (implemented)

The next five items convert damage or defence increase/reduction stats on allocated passives in their radius:

| Jewel | Base | Global mod | Conversion mod | Effect | Radius |
|---|---|---|---|---|---|
| Cold Steel | Viridian | - | `ColdAndPhysicalNodesInRadiusSwapPropertiesUniqueJewel48_` | Physical <-> Cold damage increases | 1500 |
| Fireborn | Crimson | - | `AllDamageInRadiusBecomesFireUniqueJewel49` | Physical/Cold/Lightning/Chaos/Elemental increases -> Fire | 1200 |
| Energy From Within | Cobalt | `IncreasedEnergyShieldPercentUniqueJewel51` (+3-6% ES) | `LifeInRadiusBecomesEnergyShieldAtHalfValueUniqueJewel51` | Life increases -> Energy Shield | 1500 |
| Healthy Mind | Cobalt | `MaximumManaUniqueJewel54` (+15-20% Mana) | `LifePassivesBecomeManaPassivesInRadiusUniqueJewel54` | Life increases -> Mana at 200% | 1500 |
| Energised Armour | Crimson | `IncreasedPhysicalDamageReductionRatingPercentUniqueJewel50` (+15-20% Armour) | `EnergyShieldInRadiusIncreasesArmourUniqueJewel50` | Energy Shield increases -> Armour at 200% | 1500 |

Cold Steel is a simultaneous swap: a passive with both physical and cold increases swaps their values; a passive with
only one moves it to the other. Fireborn sums every listed damage-type increase into `fire_damage_+%`. The three
defence jewels move the percentage stat and multiply by 1 or 2. The item's global mod is a normal global stat. Healthy
Mind's one-item limit is enforced by the socket refusal.

**Lioneye's Fall** has an item source and the client conversion now, but its
`local_unique_jewel_melee_applies_to_bow` effect rewrites melee/melee-weapon modifier flags, not a single stat key. The
server does not carry those flags on every passive modifier yet, so a stat-key-only mapping would be wrong. The item
source/client side is present; the **server flag layer** remains the missing part.

Live check with Energy From Within in socket 16 (node 54127), with Brute Force Solution still in socket 1:

- life **5857 -> 5311** (the life% passives in the radius moved to ES);
- energy shield **243 -> 273** (the converted ES% plus the item's +6% ES);
- mana unchanged 2326.

The client log showed the ES pool rising to 273 and `passive jewel stats: life 5311, mana 2326`. The other four
conversions are covered by direct stat-list tests; their pools are covered by the same `EquipmentStatSources` pass.

## Pilot: notable replacement (implemented)

The three notable-replacement jewels replace every allocated notable in their radius with a fixed effect:

| Jewel | Base | Global mods | Replacement mod | Replacement | Radius | Limit |
|---|---|---|---|---|---|---|
| Fevered Mind | Cobalt | +5-10 Intelligence | `NotablesGrantManaCostAndSpellDamageUnique1` | +10% Mana Cost, +20% Spell Damage | 800 | 1 |
| Fortress Covenant | Cobalt | +35-45% Minion Damage, +10-12% Minion Attack/Spell Block | `NotablesGrantMinionDamageTakenUnique__1_` | Minions take 20% increased Damage | 1200 | 1 |
| Quickening Covenant | Viridian | +12-16% Minion Attack/Cast Speed, +20-24% Minion Suppress | `NotablesGrantMinionMovementSpeedUnique__1_` | Minions have 25% reduced Movement Speed | 1200 | 1 |

The layer replaces the notable's own stats with the fixed list; it does not append. Two affected notables grant the
fixed effect twice. Overlapping replacement jewels apply in socket order (the last replacement for a notable wins in the
current stat model). The item's global mods apply normally. Fortress/Quickening require level 20 and the current items
are corrupted; the dev command sets both and enforces the limit.

The current consumers are uneven: Fevered Mind's +20% Spell Damage reaches the spell damage path, but its +10% Mana Cost
has no consumer; Fortress Covenant's minion damage taken and Quickening Covenant's minion movement speed have no
consumer yet (the item's global minion damage is panel-only, the other minion globals are unread). The generated list
marks these rows **partial**. Live: Fevered Mind is in socket 11, which has no allocated notables in its 800 radius, so
the visible part there is the +10 Intelligence (mana 2326 -> 2336); the replacement itself is covered by the socket-0 /
Quick Recovery unit test.

## Pilot: passive addition (implemented)

The ten passive-addition jewels append a stat to every allocated passive in their radius (the passive's own stats stay):

| Jewel | Base | Global mods | Addition | Radius | Limit |
|---|---|---|---|---|---|
| Coated Shrapnel | Crimson | 25% Trap/Mine Poison | Traps/Mines +2-3 to +4-6 Physical | 800 | - |
| Martial Artistry | Crimson | +0.3-0.4m Unarmed Strike Range | 1% Unarmed Melee Attack Speed | 800 | 1 |
| The Blue Dream | Cobalt | 6-10% Lightning as Extra Chaos | Lightning-resistance passives also grant an equal Power Charge on Kill chance | 1500 | - |
| The Blue Nightmare | Cobalt | 6-10% Lightning as Extra Chaos | Lightning-resistance passives also grant Spell Block at 50% of the resistance | 1500 | 1 |
| The Green Dream | Viridian | 6-10% Cold as Extra Chaos | Cold-resistance passives also grant an equal Frenzy Charge on Kill chance | 1500 | - |
| The Green Nightmare | Viridian | 6-10% Cold as Extra Chaos | Cold-resistance passives also grant Spell Suppression at 70% of the resistance | 1500 | 1 |
| The Red Dream | Crimson | 6-10% Fire as Extra Chaos | Fire-resistance passives also grant an equal Endurance Charge on Kill chance | 1500 | - |
| The Red Nightmare | Crimson | 6-10% Fire as Extra Chaos | Fire-resistance passives also grant Attack Block at 50% of the resistance | 1500 | 1 |
| The Light of Meaning | Prismatic | - | Selected variant on every passive | 1500 | 1 |
| The Perandus Pact | Prismatic | - | Selected variant on every passive | 1500 | 1 |

The Light of Meaning and The Perandus Pact have 13 selected variants: Life (+5), Energy Shield (3%), Mana (+5),
Armour (7%), Evasion (7%), all Attributes (+2), Global Crit (5%), Physical/Cold/Fire/Lightning/Chaos Damage (6%), and
Chaos Resistance (4%). The command takes the variant after the name, for example
`/radius_jewel The Light of Meaning Life` or `/radius_jewel The Perandus Pact Fire Damage`.

The Dreams/Nightmares are conditional: only passives granting the matching resistance or all elemental resistances are
affected. The added value is the resistance value for a charge chance, or 50/70% of it for block/suppression. The
current Nightmares are the block/suppression variants (the charge-on-kill Nightmare line is a pre-3.16/3.21 legacy
variant and is not created by the command).

The current consumers are uneven. The Light/Perandus additions and the Nightmare block/spell block/suppression use
stats the pools already read; the Dream charge chances use the charge-gain path. Coated Shrapnel's trap/mine physical
stats and the specific "X damage as extra chaos" globals have no consumer yet, and Martial Artistry's global unarmed
range is unread; its added unarmed attack speed is read. The generated list marks each row accordingly.

Live check with The Light of Meaning (Life) in socket 12 (node 46882): life **5311 -> 5355** (+44; five flat life per
affected passive, scaled by the character's life%), mana unchanged 2336. The item was created with
`/radius_jewel The Light of Meaning Life`, has the selected Life mod plus the `JewelEffectBaseRadiusUnique__1` radius mod,
and the client drew the radius.

## Pilot: per-node scaling (implemented)

These jewels count an attribute on allocated (or unallocated) passives in their radius and add a global stat:

| Jewel | Scaling | Radius | Limit |
|---|---|---|---:|
| Anatomical Knowledge | +1 maximum Life per 3 Intelligence allocated | 1500 | - |
| Eldritch Knowledge | 5% increased Chaos Damage per 10 Intelligence allocated | 1200 | - |
| Pugilist | 1% Evasion / Claw Physical / Unarmed Melee Physical per 3 Dexterity allocated | 1500 | - |
| Spire of Stone | 3% increased Totem Life per 10 Strength allocated; Totems cannot be Stunned | 1500 | 1 |
| Static Electricity | 1 maximum Lightning Damage to Attacks per 1 Dexterity allocated | 1500 | - |
| Tempered Flesh | -1 Strength per allocated Strength; 2% Life Recovery Rate per 10 Strength allocated | 1200 | - |
| Tempered Mind | -1 Intelligence per allocated Intelligence; 2% Mana Recovery Rate per 10 Intelligence allocated | 1200 | - |
| Tempered Spirit | -1 Dexterity per allocated Dexterity; 2% Movement Speed per 10 Dexterity allocated | 1200 | - |
| Transcendent Flesh | -Strength + crit multi/life recovery/physical reduction | 1200 | - |
| Transcendent Mind | -Intelligence + DoT multi/mana recovery/ES regeneration | 1200 | - |
| Transcendent Spirit | -Dexterity + movement/accuracy/movement penalty | 1200 | - |

The formula follows Path of Building's `getPerStat`: count the attribute on the passives in radius, then
`floor(count * value / per)` for the per-10 rules, or `floor(count / value)` for the `per X` rules. The six
Tempered/Transcendent jewels also subtract the allocated attribute total from the character and use the **unallocated**
attribute totals for the bonus lines. The server builds an allocated count map from the effective `nodeStats` and an
unallocated map from the original passive stats; timeless-conquered nodes are skipped. The scaling result is added to
the character's global stat sources, and the attribute subtraction is also fed into the attribute calculation.

The current consumers are mixed: Anatomical (life), Eldritch (chaos damage), Static (attack lightning), the Tempered/
Transcendent attribute subtraction, life recovery, crit multi, physical reduction, movement and accuracy use existing
stats. Pugilist's claw/unarmed lines and Spire's totem life are applied but have no consumer yet; Tempered Mind's mana
recovery and Transcendent Mind's DoT multi are likewise unread. The generated list marks those rows partial.

Live check with Anatomical Knowledge: in the restored demo layout, removing it from socket 11 (node 7960) drops life
**5520 -> 5355**; placing it back restores 5520, so the item is worth +165 there. Socket 11 has two Intelligence
passives (20 Int), so `floor(20 / 3) = 6` flat life is part of that total, on top of the item's (6-8)% increased
maximum Life global. Socket 13 (node 55190) has no Intelligence passives in its radius: in the earlier isolated check
there, life went **5355 -> 5502**, so only the global part (+147) could apply. The unit tests pin the scaling formula;
the panel number alone does not split the global and flat parts. The full layout reads mana 2345 and ES 276, and the
client drew the radius.

## Pilot: passive effect and notable suppression (implemented)

Might of the Meek (Crimson Jewel, Large, level 20) uses one mod row, `PassiveEffectivenessJewelUnique__1_`:
`local_unique_jewel_non_keystone_passive_in_radius_effect_+%` 50, `local_unique_jewel_notable_passive_in_radius_does_nothing`
1 and the 1500 radius. Path of Building adds `PassiveSkillEffect` 50 to every node in radius except Keystone and
ClassStart, flags notables with `PassiveSkillHasNoEffect`, and wipes the notable's mod list before applying the effect
scaling. The server collects the effect and suppression while applying the other first-pass jewels, zeroes the
notables after the pass (so a notable-replacement jewel cannot survive), and scales the remaining node stats with
Path of Building's `ScaleAddList` truncation (5 -> 7 at 50%). The per-node scaling pass then counts the scaled stats,
and unallocated passives use the same scaled view, so the Tempered/Transcendent unallocated totals see the effect too.

Live check: in cell 3 (socket 41263) the radius has seven Intelligence smalls (70 Int), one Strength small (10 Str) and
the Dark Arts notable. With Might of the Meek, life went **5520 -> 5529** and mana **2345 -> 2375**, consistent with
105 Int (+30 mana after mana%) and 15 Str (+9 life after life%). `/block` showed the suppression directly: uncapped
attack/spell block **168% / 27% -> 162% / 20%**, i.e. the Dark Arts notable's 8%/8% was removed, partly offset by the
3% dual-wield block smalls becoming 4%. The item was then moved back to the inventory so the demo layout is unchanged;
`/radius_jewel Might of the Meek` recreates it.

Cluster passives are never in a radius jewel's radius: Path of Building precomputes each socket's `nodesInRadius`
before `BuildClusterJewelGraphs` adds the synthetic cluster nodes, and the client draws cluster jewels in a separate
pane. The server's `NodesInRadius` therefore intentionally covers only the ordinary PSG, and a radius jewel in a cluster
socket has no effect.

Other limitations: mods that Path of Building marks `unscalable` are not distinguished in the integer stat model; a
notable in the radius is suppressed even if another radius jewel replaces it (matching Path of Building's first-pass
wipe).

## Pilot: allocation and tree rules (implemented)

**Intuitive Leap** (Viridian, Small, 800) and **Thread of Hope** (Crimson, variable) let allocated passives in the
jewel's disc/ring stand without a path to the class start. Thread of Hope selects a ring through
`local_jewel_variable_ring_radius_value`: 1 Small (960-1320), 2 Medium (1320-1680), 3 Large (1680-2040), 4 Very Large
(2040-2400), 5 Massive (2400-2880); its global `-(10-20)%` all-resistance mod is applied as a normal item mod. The
server's `OrdinaryPassiveRules` allows a bypass node that is not otherwise connected, but it does **not** make that
node a bridge: its neighbours are only reachable when the bypass node is itself adjacent to the normal connected
component (Path of Building's dependency model, and the PoEDB Intuitive Leap restriction). The bypass map records the
provider socket(s), and a bypass node is only honoured when one provider socket is itself connected to the tree. Thread
of Hope follows the wiki and does not enable jewel sockets in its ring; Intuitive Leap does. The bypass set is rebuilt
from the candidate allocation, and taking the jewel out is refused while a passive still depends on it.
`PassiveTree.NodesInRing` uses the client's inclusive-inner/inclusive-outer check.

**Unnatural Instinct** (Viridian, Small, limit 1) zeroes allocated small passives in radius and grants the stats of
unallocated small passives in radius. The first pass adds the suppression flags; the suppression phase zeroes the
allocated smalls before the effect scaling; the second pass copies the unallocated smalls' (possibly Might of the
Meek-scaled) stats into the character's global sources. Notables, keystones, jewel sockets and masteries are unaffected.

Live: Intuitive Leap in cell 3 (socket 41263) accepted disconnected node 55247 (Life and Energy Shield) and life went
**5520 -> 5575**; the server refused removing the jewel until 55247 was refunded, and a relog with the node allocated
restored both the jewel and the allocation. Intuitive Leap in cell 1 (socket 36634) accepted the isolated node 42981 but
refused its neighbour 53802 outside the 800 disc, confirming the no-bridge rule. Thread of Hope Massive in cell 3
accepted **23507** (Attack and Cast Speed, distance 2628) and **48477**, and Apply Points saved them (523 allocated);
Large accepted **38129** (Damage and Energy Shield, distance 1808) after the Massive nodes were refunded and the Large
item socketed. The server refused swapping the Massive item for the Large one while 23507/48477 remained (refund
first); after the refund the swap and 38129 allocation succeeded. Small ring node **5875** (distance 862) is inside the
960-1320 small ring's inner hole and is refused. The -10% all-resistance roll changed `/resistances`
**268/221/221 -> 258/211/211**. The explicit-ring item drew a filled disc in the client; base radius 0 plus the implicit
ring value draws the annulus with the inner hole (items 1162/1161 in cell 3 during the checks). Unnatural Instinct in
cell 3 changed life **5520 -> 5685**, mana **2355 -> 2326** and ES
**277 -> 285**: the three allocated Intelligence smalls were suppressed (-30 Int) while three unallocated Life/Energy
Shield smalls granted +9% maximum Life and +15% maximum Energy Shield. The items were returned to the inventory.

Limitations: the jewel socket itself must be connected, and the server records the provider socket(s) so a socket
allocated only through another bypass does not extend the bypass chain. Unnatural Instinct's Path of Building
`Condition:ConnectedTo` filter has no current passive stat to remove (no `PassiveSkills` row carries a connected stat;
the flat stat model has no condition flag). Cluster passives are never in a radius jewel's radius (PoB precomputes
`nodesInRadius` before the cluster graphs).

### Impossible Escape (generated special, implemented)

A generated Viridian Jewel (Small, limit 1, corrupted) whose variant is one of the client's 48 keystones
(`MavenJewelRadiusKeystones.json`). The item mod `JewelUniqueAllocateDisconnectedPassivesAroundKeystone` stores the
keystone's `PassiveSkillGraphId` in `local_unique_jewel_disconnected_passives_can_be_allocated_around_keystone_hash`
and the display base radius 800; the effect uses Path of Building 3_16's Small radius **960** (the client's
`PassiveJewelRadii` row is the 800 display/base value). The Maven table row is used only to choose/name the keystone.
`/impossible_escape <keystone>` creates it (the list shows the names; a numeric row or graph id is also accepted).
Items made before the graph-id fix store the row and are migrated when the save loads.

Rule: the jewel socket must be allocated and connected, and the chosen keystone must be allocated. Every ordinary
passive within **960** of that keystone is a bypass node with the jewel socket as provider; the keystone itself is
excluded. The server refuses taking the jewel out or swapping it while one of those nodes remains. Live: Versatile
Combatant (row 2444, graph 50679) showed the client's ring; node 29547 (Life Leech, 293 units from the keystone) was
accepted without a path, and the pickup was refused until 29547 was refunded. Supreme Ego (row 2312, graph 49639)
showed no ring while the keystone was unallocated; after allocating it the ring appeared and Dire Torment (9015) next
to it was accepted. Replenishing Remedies (50690, 953.7 units from Supreme Ego) is inside the client's ring and is now
accepted by the server too. The command warns when the chosen keystone is not allocated. The items and nodes were then
removed.

## Mechanism groups

| Group | Rows | Jewels |
|---|---:|---|
| Attribute conversion | 6 | Brute Force Solution, Careful Planning, Efficient Training, Fertile Mind, Fluid Motion, Inertia |
| Damage/defence/weapon conversion | 6 | Cold Steel, Fireborn, Lioneye's Fall, Energy From Within, Healthy Mind, Energised Armour |
| Notable replacement | 3 | Fevered Mind, Fortress Covenant, Quickening Covenant |
| Passive addition | 10 | Coated Shrapnel, Martial Artistry, The Blue/Green/Red Dream, The Blue/Green/Red Nightmare, The Light of Meaning, The Perandus Pact |
| Threshold / skill modifier | 33 | the "With at least 40 X in Radius" jewels |
| Per-node scaling | 11 | Anatomical Knowledge, Eldritch Knowledge, Pugilist, Spire of Stone, Static Electricity, Tempered/Transcendent Flesh/Mind/Spirit |
| Allocation / tree rules | 3 | Intuitive Leap, Thread of Hope, Unnatural Instinct |
| Passive effect / notable suppression | 1 | Might of the Meek |
| Special / global only | 5 | Inspired Learning, Might in All Forms, Warrior's Tale, Sudden Ignition, Pitch Darkness |

The counts are item rows, so Combat Focus's three colour variants count as three.

### Attribute conversion (6)

The six jewels each grant `+(16-24)` to one attribute and transform another attribute on passives in radius:

| Jewel | Conversion | Base | Radius |
|---|---|---|---|
| Brute Force Solution | Strength -> Intelligence | Cobalt | Large |
| Careful Planning | Intelligence -> Dexterity | Viridian | Large |
| Efficient Training | Intelligence -> Strength | Crimson | Large |
| Fertile Mind | Dexterity -> Intelligence | Cobalt | Large |
| Fluid Motion | Strength -> Dexterity | Viridian | Large |
| Inertia | Dexterity -> Strength | Crimson | Large |

For every **allocated** passive in radius, replace the source attribute's flat stat with the target attribute's stat
(`base_strength` -> `base_intelligence`, etc.). The jewel's own `+(16-24)` is a global mod and already works on a
hand-made item. The conversion does not change the jewel's own attribute; it changes the passives it affects. Path of
Building's `getSimpleConv` in `ModParser.lua` is the reference. This is the easiest group to implement: it reuses the
`nodeStats` map and a small stat-key map.

### Damage, defence and weapon conversion (6)

| Jewel | Transformation | Base | Radius |
|---|---|---|---|
| Cold Steel | Physical <-> Cold increases/reductions | Viridian | Large |
| Fireborn | other damage types -> Fire increases/reductions | Crimson | Medium |
| Lioneye's Fall | melee/melee-weapon modifiers -> bow modifiers | Viridian | Medium |
| Energy From Within | Life increases/reductions -> Energy Shield | Cobalt | Large |
| Healthy Mind | Life increases/reductions -> Mana at 200% | Cobalt | Large |
| Energised Armour | Energy Shield increases/reductions -> Armour at 200% | Crimson | Large |

Cold Steel is bidirectional: physical increases become cold and cold increases become physical. Fireborn maps physical,
cold, lightning, chaos and elemental damage increases to fire. Energy From Within, Healthy Mind and Energised Armour map
the increase/reduction stat keys and scale the value (100% or 200%). Lioneye's Fall is different: it rewrites melee and
melee-weapon-type mods to bow mods, including weapon-type conditions. The item source (`UniqueJewelMeleeToBow`) and the
client-side conversion are now present (`/radius_jewel Lioneye's Fall`), but the server's flat stat model does not carry
all of those modifier flags yet, so the server conversion needs a richer modifier/flag model than the other five.

The jewels' own global mods (`(6-8)% increased maximum Life`, `(15-20)% increased Armour`, etc.) work if the item exists.
The transformation applies only to allocated passives in radius.

### Notable replacement (3)

| Jewel | Replacement | Base | Radius | Limit |
|---|---|---|---|---|
| Fevered Mind | notables instead grant 10% increased Mana Cost and 20% increased Spell Damage | Cobalt | Small | 1 |
| Fortress Covenant | notables instead grant "Minions take 20% increased Damage" | Cobalt | Medium | 1 |
| Quickening Covenant | notables instead grant "Minions have 25% reduced Movement Speed" | Viridian | Medium | 1 |

Every **allocated notable** in radius loses its normal stats and grants the fixed effect instead. Multiple notables each
grant the fixed effect, so the effect stacks with the number of affected notables. The jewel's other global mods (spell
damage, minion damage/block/suppression) still work. The item needs the notable's name/icon to remain on the tree while
its stats are replaced; the client draws the item's modified node text from the item data, but the server's stat layer
must replace the notable row exactly once per notable.

### Passive addition (10)

| Jewel | Added to passives in radius | Base | Radius | Limit |
|---|---|---|---|---|
| Coated Shrapnel | traps/mines added physical damage | Crimson | Small | - |
| Martial Artistry | 1% increased Unarmed Attack Speed with Melee Skills | Crimson | Small | 1 |
| The Blue Dream / Nightmare | resistance passives also grant power-charge chance / spell block | Cobalt | Large | 1 for Nightmare |
| The Green Dream / Nightmare | resistance passives also grant frenzy-charge chance / spell suppression | Viridian | Large | 1 for Nightmare |
| The Red Dream / Nightmare | resistance passives also grant endurance-charge chance / attack block | Crimson | Large | 1 for Nightmare |
| The Light of Meaning | selected stat added to every passive | Prismatic | Large | 1 |
| The Perandus Pact | selected stat added to every passive | Prismatic | Large | 1 |

Coated Shrapnel and Martial Artistry add their stat to every allocated passive in radius except keystones/class starts
and sockets. The Light of Meaning and The Perandus Pact have 13 variants: +2 all Attributes, +4% Chaos Resistance, +5
maximum Life, +5 maximum Mana, 6% increased Chaos/Cold/Fire/Lightning/Physical Damage, 5% increased Global Critical
Strike Chance, 7% increased Evasion Rating, 3% increased Energy Shield, 7% increased Armour. The item selects one variant;
it is not all 13 at once.

The six Dream/Nightmare jewels are conditional additions: only passives granting the matching resistance or all elemental
resistances are affected. The added value is proportional to the passive's resistance value: an equal chance to gain the
charge on kill for the Dream, and 35/50/70% of the resistance value as spell block, spell suppression or attack block
for the Nightmare variants. The flat "Gain 5% of X Damage as Extra Chaos" line is a separate global mod. These jewels
need the resistance stat of each affected passive and the charge/block/suppression consumers; the block/suppression
values are percentages of the resistance, not flat percentages.

### Threshold / skill modifier (33)

These jewels have one or more `With at least 40 <attribute> in Radius` lines. If the total attribute of the passives in
radius reaches 40, the listed skill-specific modifiers apply. The threshold is global (one check per jewel), not a
per-node effect.

| Threshold | Jewels |
|---|---|
| 40 Strength | Divine Inferno, Overwhelming Odds, Rapid Expansion, Shattered Chains, The Vigil, Weight of the Empire, Wildfire, Winter Burial |
| 40 Dexterity | Collateral Damage, Fight for Survival, Growing Agony, Might and Influence, Omen on the Winds, Ring of Blades, Spirit Guards, Steel Spirit, Volley Fire |
| 40 Intelligence | Dead Reckoning, Endless Misery, First Snow, Frozen Trail, Hazardous Research, Inevitability, Rolling Flames, Spirited Response, Spreading Rot, The Long Winter, Unending Hunger, Violent Dead, Winter's Bounty |
| 40 Strength + Intelligence | Combat Focus (Crimson) |
| 40 Intelligence + Dexterity | Combat Focus (Cobalt) |
| 40 Dexterity + Strength | Combat Focus (Viridian) |

The affected skills and effects are listed in the generated table and in `unique-jewels.md`. They include projectile
count, area of effect, damage, conversion, cooldown, charge/ailment behavior and minion behavior.

All 33 items are now generated from Path of Building by `scripts/re/threshold_jewels.py` into
`ThresholdJewelCatalogue.cs` (`/radius_jewel <name>`; `Combat Focus Cobalt` selects the base). The item carries the
client's local `local_unique_jewel_..._with_40_<attribute>_in_radius` mods, so the **client** applies the threshold
effect when the attribute total in radius reaches 40; the unconditional global mods (for example
`(10-15)% increased Cold Damage`) are ordinary item stats. The Vigil's `(8-15)% increased Armour` line is text in PoB's
export; the generator uses the client's 10-15% unique armour row as its global part.

The server side is implemented in `ThresholdJewels.cs`: it parses each local stat's target and attribute mask, sums
Strength/Dexterity/Intelligence from every ordinary passive in radius (allocated or not, matching PoB's `Threshold`
pass), and adds the mapped target stat when the total reaches the requirement. The `threshold jewel socket ...` log
line records each application. Live: `Combat Focus Cobalt` in socket 36634 logged
`elemental_hit_cannot_roll_fire_damage 1` (60 Intelligence in radius); `Collateral Damage` in socket 60735 logged
`galvanic_arrow_area_damage_+% 50` and `shrapnel_shot_radius_+% 25` (60 Dexterity).

Path of Building's `jewelThresholdFuncs` in `ModParser.lua` is the exact reference for the stat, value and skill name.
The remaining work is the skill-scoped consumer layer: the target stats are present in the server's stat view, but the
damage/area calculations still read only some of them, so the skill-specific projectile, cooldown, minion and charge
behaviors remain partial. Targets with no matching client stat (for example Spreading Rot's two-second wither or the
`*_final` more-multipliers without a stat row) are skipped by `ThresholdJewels.Read`.

### Per-node scaling (11)

| Jewel | Counts | Applies |
|---|---|---|
| Anatomical Knowledge | Intelligence on allocated passives in radius | +1 maximum Life per 3 |
| Eldritch Knowledge | Intelligence on allocated passives in radius | 5% increased Chaos Damage per 10 |
| Pugilist | Dexterity on allocated passives in radius | evasion, claw physical, unarmed melee physical per 3 |
| Spire of Stone | Strength on allocated passives in radius | 3% increased Totem Life per 10 |
| Static Electricity | Dexterity on allocated passives in radius | +1 maximum Lightning Damage to Attacks per 1 |
| Tempered/Transcendent Flesh | Strength on allocated and unallocated passives in radius | -1 Strength per allocated Strength; crit multi per unallocated; recovery/phys reduction per allocated |
| Tempered/Transcendent Mind | Intelligence on allocated and unallocated passives in radius | -1 Intelligence per allocated Intelligence; accuracy/DoT multi per unallocated; mana recovery/ES regen per allocated |
| Tempered/Transcendent Spirit | Dexterity on allocated and unallocated passives in radius | -1 Dexterity per allocated Dexterity; mana/movement per unallocated; movement per allocated |

The formula is `floor(total / per) * value`, the same floor used by the Militant Faith devotion pass. The six
Tempered/Transcendent jewels are different: they subtract the allocated attribute total from the character (the `-1 per
1` line) and add bonuses from the unallocated total. The server's `nodeStats` map holds the allocated values and
`UnallocatedStats` supplies the unallocated (possibly Might-of-the-Meek-scaled) values; the other five only count
allocated nodes. The remaining work here is the unread target stats and the Tempered/Transcendent corner cases.

### Allocation and tree rules (3)

**Intuitive Leap** (Viridian, Small, radius 800): passive skills in radius can be allocated without being connected to
the tree. The jewel itself has no stat effect beyond the allocation rule (`Passage` is flavour text). Implemented: a
bypass node is allowed as an exception, but it only propagates to its neighbours when it is adjacent to the normally
connected component; an isolated bypass node cannot bridge to a node outside the radius (the PoEDB restriction). The
item cannot be taken out while one of them still depends on it.

**Thread of Hope** (Crimson, Variable): the same allocation bypass, but only for passives in the selected ring, plus
`-(10-20)% to all Elemental Resistances` (a global mod). The five variants are Small Ring, Medium Ring, Large Ring, Very
Large Ring and Massive Ring (Uber); `RingRadii` maps the selected `local_jewel_variable_ring_radius_value` to the
`PassiveJewelRadii` inner/outer rows, and `NodesInRing` uses the inclusive-inner/inclusive-outer check. The ring value
is created as an **implicit** mod: the client's passive-tree art reads it there to draw the annulus, while the explicit
allocate mod's `local_jewel_effect_base_radius` 800 would otherwise draw a filled disc. The tooltip order is ring,
resistance, allocate. Following the wiki, Thread of Hope does not enable jewel sockets in the ring (Intuitive Leap
does). Implemented with the same removal refusal as Intuitive Leap.

**Unnatural Instinct** (Viridian, Small, limit 1): allocated small passives in radius grant nothing; unallocated small
passives in radius grant all their bonuses. This is not an allocation bypass; it swaps which small passives contribute.
The allocated smalls stay allocated on the tree (the client still shows them), but their stats are zeroed; the
unallocated smalls' stats are added to the character. Notables and keystones are unaffected. Implemented in the first-pass
suppression and second-pass copy described in the pilot section above.

### Passive effect / notable suppression (1)

**Might of the Meek** (Crimson, Large, level 20): `50% increased Effect of non-Keystone Passive Skills in Radius`;
`Notable Passive Skills in Radius grant nothing`. Implemented in the pilot (see above): the server collects the effect
and suppression in the first pass, zeroes the notables after it, scales the remaining stats with Path of Building's
round-toward-zero rule and lets the per-node scaling pass count the scaled values. The effect stacks additively across
copies (50% + 50% = 100%), and a notable is suppressed even if another radius jewel replaces it.

### Special and global-only (5)

**Inspired Learning** (Crimson, Small): with 4 notables allocated in radius, killing a rare monster grants one of its
modifiers for 20 seconds. Needs a count of allocated notables in radius, the rare monster's mod list, a timed buff and a
way to apply the stolen mod to the player. The server has monster mods and buffs, but not this steal-and-apply flow.

**Warrior's Tale** (Crimson, Medium, limit 1): `100% increased effect of Tattoos in Radius`. Tattoos are a separate
mechanic; the 100-item catalogue, target/limit rules and implementation plan are in [tattoos.md](tattoos.md). The
server has no tattoo model yet, so keep this jewel out of scope until the tattoo apply/remove flow exists.

**Might in All Forms** (Crimson, Medium): implemented. The item carries
`DexterityAndIntelligenceGiveStrengthMeleeBonusInRadiusUniqueJewel55`; `ReadConversion` sets `StrengthMeleeBonus`, and
after the radius passes the server sums `base_dexterity` and `base_intelligence` on the allocated passives in the 1200
radius and adds `(Dex + Int) / 5` to `melee_physical_damage_+%`. Path of Building models the same input as
`DexIntToMeleeBonus`, at the Strength rate of 1% per 5 points. Live check: with Brute Force Solution in cell 1 the
`passive jewel stats` line read `melee 129`; socketing Might in All Forms in the same cell changed it to `melee 137`
(+8 = 40 Dex+Int in radius / 5), and restoring Brute Force returned it to `melee 127` (the Brute Force conversion moves
the same attributes to Intelligence, so the baseline differs). The jewel has no other radius effect.

**Sudden Ignition** (Viridian, Medium): implemented as a global item. It carries
`BurningArrowThresholdJewel_2` (`ignited_enemies_explode_on_kill`) and `FireDamagePercentUnique__10`; the increased Fire
Damage is an ordinary global stat, while the corpse destruction is not consumed by the server's kill/corpse path
(partial). The radius label is Medium but the mod text has no "in Radius".

**Pitch Darkness** (Viridian, Medium): implemented as a global item. It carries
`BurningArrowThresholdJewelUnique__1` (`fire_dot_multiplier_+`) and `FireDamagePercentUnique__9` (`fire_damage_+%`); both
are ordinary global stats and the radius label is unused.

## Implementation plan

1. **Item catalogue and dev source.** The 42 server-side definitions live in `RadiusJewelCatalogue` and the 33
   threshold items in the generated `ThresholdJewelCatalogue`; `/radius_jewel` creates all of them with the existing
   `UniqueJewelVisuals` art, the ordinary passive-jewel socket path and the unique item limits. Only Inspired Learning
   and Warrior's Tale have no item source yet; Lioneye's Fall's item source is present, but its server flag model is
   missing.
2. **Radius query.** The 1500 disc for the pilot uses `PassiveTree.NodesInRadius`; add
   `NodesInRing(socket, inner, outer)` for Thread of Hope and expose the `PassiveJewelRadii` values. Reuse the timeless
   exclusions and skip conquered nodes. Test a known socket at Small/Medium/Large and the five Thread of Hope rings.
3. **Per-node stat layer.** The pilot's attribute/damage/defence conversions, notable replacement, passive addition,
   per-node scaling, Might of the Meek's passive effect/notable suppression and Unnatural Instinct's small swap are
   implemented in `EquipmentStatSources` through `RadiusJewelCatalogue`; the remaining server item is:
   - Lioneye's Fall: melee/melee-weapon modifier flags -> bow flags (the item source and client conversion are present;
     the server needs the flag model, not a stat-key move).
   The `EquipmentStatSources` `nodeStats`/`passives` path is the place to hook this; the existing point/jewel caches must
   include the jewel's socket and variant in their key.
4. **Per-node scaling.** Implemented with allocated and unallocated attribute totals, `getPerStat`-style floors and the
   global stat source. The remaining work here is only the unread target stats and the unallocated-node tests for
   Tempered/Transcendent corner cases.
5. **Thresholds.** The 33 item sources, client local mods and server attribute counting (`ThresholdJewels`) are
   implemented; the mapped target stats reach the server stat view. Remaining: skill-specific consumers for the mapped
   projectile count, cooldown, minion, charge and damage/area stats, and aliases for targets with no client stat row.
6. **Allocation rules.** Implemented: Intuitive Leap/Thread of Hope add their allocated disc/ring nodes as roots
   (neighbours become reachable), the ring variants use `NodesInRing`, and taking the jewel out is refused while a
   bypass-allocated passive remains. Unnatural Instinct's small swap is separate and implemented in the stat layer.
7. **Special.** Might in All Forms (Strength melee bonus) and the two global-only items (Pitch Darkness, Sudden
   Ignition) are implemented. Inspired Learning and Warrior's Tale remain; each needs a different subsystem (rare mod
   steal, tattoos).

## Tests and acceptance

- **Pilot (covered):** 98 radius tests cover the six attribute pairs, the five damage/defence conversions (Cold Steel
  swap, Fireborn multi-source, Energy/Healthy/Energised factors), the three notable replacements, the ten passive
  additions (including the Light/Perandus variants and the resistance-scaled Breach jewels), the eleven per-node scaling
  jewels (primary rules, scaling counts and the Tempered attribute subtraction), Might of the Meek (item row, 50%/100%
  truncation, non-keystone scaling and notable suppression), Might in All Forms (item row, Dex/Int melee bonus at socket
  36634), the two global-only item sources (Pitch Darkness, Sudden Ignition), the 33 generated threshold item sources
  (item creation, the Combat Focus base selection and the server threshold parser/alias/application at sockets 36634,
  60735 and 41263), Lioneye's Fall's item source, Impossible Escape's 48 keystone variants, graph-id mapping and
  legacy-row migration, the three allocation/tree-rule jewels (bypass roots, ring variants and geometry, allocated/unallocated small
  swap), item creation, the item limits, the life pools, the cluster-socket refusal and the live panel values.
- Remaining geometry: every other radius size returns the expected node set; the socket and class starts are excluded;
  a conquered node is never modified by a second jewel.
- Remaining conversion: a known physical/cold or life/ES passive changes the expected stat; the 200% cases double.
- Notable replacement: a notable in radius loses its row and grants exactly one fixed effect; two notables stack twice.
- Passive addition: every allocated passive in radius gains the stat; a resistance passive gains the Dream/Nightmare
  proportional value; the selected Light/Perandus variant is the only one applied.
- Per-node scaling: 9/10/145-style floors and allocated vs unallocated counts; Tempered/Transcendent subtract the
  allocated attribute and add the unallocated bonuses.
- Threshold: 39 vs 40 attribute totals; the skill modifier applies only at 40; the non-threshold global mod always
  applies.
- Allocation: a node in the jewel's disc/ring can be allocated without a connection; its neighbours become reachable;
  removing the jewel is refused until the bypass-allocated nodes are refunded; Thread of Hope only allows its selected
  ring. Unnatural Instinct suppresses allocated smalls and grants unallocated smalls.
- Live check (pilot done): Brute Force Solution in socket 1 changed Strength 559 -> 549 and Intelligence 647 -> 681;
  Energy From Within in socket 16 changed life 5857 -> 5311 and energy shield 243 -> 273; The Light of Meaning (Life) in
  socket 12 changed life 5311 -> 5355 and left mana at 2336. Anatomical Knowledge in the full demo layout (timeless
  socket 0, Brute Force socket 1, Anatomical socket 11, Light of Meaning socket 12, Fevered Mind socket 13, Energy From
  Within socket 16 and the cluster jewels) is worth +165: removing it drops life 5520 -> 5355 and placing it back
  restores 5520. Socket 11 has 20 Int, so `floor(20 / 3) = 6` flat life is part of that total; socket 13 has no
  Intelligence in radius and in the earlier isolated check only its global life% applied (5355 -> 5502). The full
  layout reads mana 2345 and ES 276. Might of the Meek in cell 3 (socket 41263) changed life 5520 -> 5529 and mana
  2345 -> 2375 from the 50% effect on the Intelligence/Strength smalls, and uncapped attack/spell block 168%/27% ->
  162%/20% from the suppressed Dark Arts notable (partly offset by the scaled 3% dual-wield block smalls); the item was
  then returned to the inventory. Intuitive Leap in cell 3 accepted disconnected node 55247 (life 5520 -> 5575) and the
  allocation survived a relog; Thread of Hope Massive in the same cell accepted 23507 (2628) and 48477 (523 allocated),
  and after refunding them the Large item accepted 38129 (1808) in the 1680-2040 ring. Small ring node 5875 at 862 is
  inside the small ring's inner hole and is refused. Swapping the Massive item for the Large one while 23507/48477
  remained was refused until they were refunded; the jewel pickups have the same guard. Thread of Hope's -10% all-res
  roll changed `/resistances` 268/221/221 -> 258/211/211. Unnatural Instinct in cell 3 changed life 5520 -> 5685, mana
  2355 -> 2326 and ES 277 -> 285 from the three allocated Int smalls and three unallocated Life/ES smalls. The later
  allocation relog read the demo layout at mana 2355 and ES 277 (the earlier Might check was at 2345/276); the life
  values and relative deltas are unchanged. The client drew the radii; removing the items restores the baseline. Each
  remaining group needs the same check.

## References

- Path of Building: `src/Data/Uniques/jewel.lua` (the 78 rows), `src/Modules/ModParser.lua` (`jewelOtherFuncs`,
  `jewelSelfFuncs`, `jewelSelfUnallocFuncs`, `jewelThresholdFuncs`), `src/Classes/PassiveSpec.lua`
  (`NodesInIntuitiveLeapLikeRadius`, the radius-jewel rebuild).
- Client data: `data/tables/PassiveJewelRadii.json`, `PassiveJewelSlots.json`, `UniqueJewelLimits.json`,
  `BaseItemTypes.json`, `Mods.json`, `Stats.json`.
- Server: `PassiveTree.cs` (`NodesInRadius`), `TimelessJewels`/`Program.Progression.cs` (per-node override and pools),
  `Program.Items.cs` (passive-jewel socket/refusal), `UniqueJewelVisuals.cs`, `UniqueJewelCatalogue.cs`.
- Wiki list: [List of unique jewels](https://www.poewiki.net/wiki/List_of_unique_jewels) (the current wiki is
  bot-protected; the Path of Building data is the current reference).
