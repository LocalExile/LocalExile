# Roadmap: unique jewels

Part of the [Roadmap](../Roadmap.md); the passives area is in
[roadmap/progression.md](progression.md), and the full unique-jewel catalogue is in
[docs/game/unique-jewels.md](../docs/game/unique-jewels.md).

This page covers **unique jewels socketed in the passive tree**. Ordinary, magic and rare jewels already work through
the `PassiveJewels1` inventory (see [status/progression.md](../status/progression.md)); cluster jewels are a separate
change.

## What is already done

- The 21 ordinary large jewel sockets are mapped to the `PassiveJewels1` pane (Inventories.dat row 23, inventory id
  24, 60x1). The pane cell is the `PassiveJewelSlots.dat` row; that row's `Slot` column is the `PassiveSkills.dat` row
  of the socket node.
- A normal, magic or rare jewel can be placed in an allocated socket (C->S 0x2a or 0x4a), picked up (0x28), saved with
  the character's inventories, and its global mods are applied through `ItemModStats` like a worn item's.
- Allocation is checked on the server: the socket node must be allocated, the item must be a non-cluster `Jewel`, and
  its level requirement must be met. Refunding a socket that still holds a jewel is refused until the jewel is taken
  out; a level trim returns the jewel to the main inventory if it has to give the socket up.
- Unique jewels use the same socket and stat path, so an existing unique item with plain unconditional global mods
  already applies. **There is no unique-item catalogue, generator, drop or vendor entry yet**, so unique jewels cannot
  be obtained in normal play.
- **Pilot done: The Adorned.** `/adorned [0-1000]` creates the corrupted unique Crimson Jewel; its
  `0-100% increased Effect of Jewel Socket Passive Skills containing Corrupted Magic Jewels` scales corrupted magic
  jewels in the ordinary passive sockets. Live check: a corrupted magic jewel gave +15% fire resistance, The Adorned at
  100% made it +30% (225% -> 240% -> 255%). Cluster-socket jewels and the 50-150% pre-3.25 variant are not covered.

## What is not done

- No unique item definitions (name, base type, visual identity, mod list, variants) and no `/item`/drop/vendor path.
- No conditional-stat engine: today every non-local mod is applied unconditionally, so "if you have", "recently",
  "on kill", per-charge and threshold mods would be wrong.
- No passive-tree radius model, so "in Radius" jewels, threshold jewels and node-modifying jewels are not modelled.
- No timeless-jewel (Legion) seed, alternate tree or keystone substitution.
- Abyss jewels belong in abyss sockets on items and are out of scope here.
- Cluster jewels (expansion sockets, proxy passives and notables) are a separate PR.

## Plan

### 1. Unique item catalogue and source

- Build `data/unique-jewels.json` from the client's `UniqueStashLayout`, `Words`, `BaseItemTypes`, `ItemVisualIdentity`
  and `Mods`, cross-checked with Path of Building's `src/Data/Uniques/jewel.lua` (the same data used for the catalogue).
  Each entry needs: stable unique name, base item type, unique visual identity, implicit/explicit mod HASH16s and
  values, variants, level requirement and the `UniqueJewelLimits` entry.
- Add `/item <unique jewel name>` (and a unique drop/vendor source later) so the item can be tested. The item must be
  sent with `ItemRarity.Unique`, the unique visual identity and the correct mod list; the client derives the name from
  its own tables.
- Tests: every catalogue row creates a serialisable item with the expected base, visual and mod count; unknown names
  are refused; a reload keeps the item.
- Done when `/item Might of the Meek` creates the item and the next phases can use it.

### 2. Global unique jewels (42 in the catalogue)

- These have only unconditional character stats. The existing `ItemModStats.Global` path already applies them; this
  phase is about making the items obtainable and proving the stat mapping.
- Check attribute/level requirements, limited-to counts and variants (e.g. pre-3.28 Bloodnotch variants).
- Add a test per stat family: attributes, life/mana, resistances, damage, speed, regeneration, minion stats, corruption
  flags and "cannot" stats.
- Done when every Global row can be created, socketed in an allocated socket, and produces the same final stat keys the
  item tooltip describes; unsupported stat keys are listed rather than silently ignored.

### 3. Conditional unique jewels (40 remaining; The Adorned done)

- The Adorned is implemented as the pilot (ordinary passive sockets only; see above).
- Model the condition where it is cheap: per-item counts (Primordial items), per-charge, low/full life, recent kill,
  reserved mana, and per-allocated-node counts.
- Until a condition is modelled, the server must **not** apply the conditional part: either refuse the jewel in a
  passive socket with a clear message or mark the affected stat as inactive. The current unconditional path is not
  acceptable.
- Add a condition interface that `CharacterStatSources` / the hit and panel consumers can query at the point of use.
- Done when each row is either fully modelled or explicitly refused, with the reason in the catalogue status.

### 4. Radius unique jewels (78 rows, 76 distinct names; attribute + damage/defence pilot done)

Full classification, mechanics, radius/ring model and reuse notes:
[radius-jewels](../docs/game/radius-jewels.md); the generated per-jewel table is
[radius-jewel-list](../docs/game/radius-jewel-list.md).

- **Radius model:** `PassiveJewelRadii.dat` gives Small 800, Medium 1200, Large 1500, VeryLarge 2000 and Massive 2400,
  with the matching ring inner/outer values. `PassiveTree.NodesInRadius` already handles the timeless disc; add
  `NodesInRing` for Thread of Hope. Reuse the timeless exclusions and skip conquered nodes.
- **Mechanism groups:** 6 attribute conversions, 6 damage/defence/weapon conversions, 3 notable replacements, 10 passive
  additions, 33 threshold/skill jewels, 11 per-node scaling jewels, 3 allocation/tree rules, 1 passive-effect/notable-
  suppression jewel and 5 special/global-only jewels.
- **Reuse from timeless:** the `nodeStats` override pattern in `EquipmentStatSources`, the TinyMT32-independent
  per-node stat replacement, the `PassiveTree` geometry (`NodesInRadius`/`NodesInRing`), the passive-jewel
  socket/refusal path and the item-art table.
- **Missing layers:** skill-scoped threshold consumers; Lioneye's Fall's server flag model; the remaining two item
  sources (Inspired Learning, Warrior's Tale); special mechanics (rare-mod steal, tattoos, corpse destruction).
- **Wiki gap check (2026-10-29):** the poewiki list (189 distinct names) against this catalogue (181) leaves Bound By
  Destiny, Forbidden Flame, Forbidden Flesh, Megalomaniac, Sublime Vision, The Balance of Terror and Watcher's Eye
  without item sources; they are recorded in `unique-jewels.md` and are not implemented here.
- **Generated specials:** Impossible Escape (`/impossible_escape`, 48 keystone variants) is implemented with the
  keystone-radius allocation bypass; Rational Doctrine has a `/unique_jewel` item source, but its ground mechanics
  remain inert.
- **Pilot done (42 rows with server effects; 33 threshold and Lioneye's Fall item sources generated):**
  `RadiusJewelCatalogue`,
  `ThresholdJewelCatalogue`, `/radius_jewel`, the 1500/1200/800 disc and Thread of Hope ring
  queries, the six attribute conversions, the five damage/defence conversions (Cold Steel, Fireborn, Energy From
  Within, Healthy Mind, Energised Armour), the three notable replacements (Fevered Mind, Fortress Covenant, Quickening
  Covenant), the ten passive additions (Coated Shrapnel, Martial Artistry, the Dreams/Nightmares, The Light of Meaning,
  The Perandus Pact), the eleven per-node scaling jewels (Anatomical/Eldritch Knowledge, Pugilist, Spire of Stone,
  Static Electricity and the Tempered/Transcendent trios, with allocated/unallocated counts and PoB `getPerStat`
  floors), Might of the Meek (50% effect on non-keystone passives, notables suppressed), Intuitive Leap/Thread of Hope
  (allocation bypass, ring variants, removal refusal) and Unnatural Instinct (allocated smalls suppressed, unallocated
  smalls granted). The cluster-socket refusal, the item limits and the live checks pass (Strength -10 / Intelligence
  +34; life 5857 -> 5311 and ES 243 -> 273 with Energy From Within; life 5311 -> 5355 with The Light of Meaning
  (Life); in the full layout Anatomical is worth +165 at socket 11 (5520 with it, 5355 without), with socket 11's 20
  Int contributing `floor(20 / 3) = 6` flat life on top of its global life%; Might of the Meek at cell 3 changed life
  5520 -> 5529 and mana 2345 -> 2375, and uncapped attack/spell block 168%/27% -> 162%/20% as the Dark Arts notable
  was suppressed; Intuitive Leap accepted disconnected node 55247 (life 5520 -> 5575) and the allocation survived a
  relog, and in cell 1 it refused the outside neighbour 53802 (no bridging); Thread of Hope uses the PoB 3_16 rings
  960-1320 / 1320-1680 / 1680-2040 / 2040-2400 / 2400-2880, does not enable jewel sockets, and its -10% all-res roll
  changed `/resistances` 268/221/221 -> 258/211/211; its ring value is implicit and the base radius 0 so the client
  draws the annulus. Massive accepted 23507 (2628) and 48477 (523 allocated), and after refunding them Large accepted
  38129 (1808); swapping the item while those bypass nodes remained was refused. Might in All Forms in cell 1 changed
  the `passive jewel stats` melee value 129 -> 137 (+8 = 40 Dex+Int in radius / 5) while Brute Force was in the
  inventory; restoring Brute Force returned it to 127. Pitch Darkness and Sudden Ignition are created as global item
  sources; their Fire Damage stats apply, and Sudden Ignition's corpse-destruction stat is currently unread. The 33
  threshold items are generated from PoB by `scripts/re/threshold_jewels.py`; `ThresholdJewels` sums every ordinary
  passive in radius and adds the mapped stats when the attribute total is met. Live: `Combat Focus Cobalt` in socket
  36634 logged `elemental_hit_cannot_roll_fire_damage 1` (60 Int); `Collateral Damage` in socket 60735 logged
  `galvanic_arrow_area_damage_+% 50` and `shrapnel_shot_radius_+% 25` (60 Dex); `Spirited Response` in a spare socket
  changed mana 2355 -> 2476 from its 10% maximum mana global. Skill-specific consumers remain partial. Unnatural
  Instinct changed life 5520 -> 5685, mana 2355 -> 2326 and ES 277 -> 285). Lioneye's Fall's item source and client
  conversion are present (`/radius_jewel Lioneye's Fall`); its server flag model is still missing.
- **Order:** skill-specific threshold consumers -> Lioneye's Fall's flag layer -> the remaining special mechanics.
- Done when each group has tests on a small synthetic tree, a dev-created jewel changes only the intended nodes, the
  global mods still apply, and removing the jewel restores the baseline.

### 5. Timeless jewels (6 in the catalogue; the node transformation is done)

Reference, measurements and live evidence: [timeless-jewels](../docs/game/timeless-jewels.md).

- Done: the item data, `/timeless`, the alternate-tree generator, the fixed radius, replacement/addition stats, the
  one-Historic limit, save/load, the live client check, Militant Faith's two random devotion mods on the item, and the
  `DevotionStats` scaling of those mods by `floor(Devotion / 10)`.
- Remaining: the conqueror keystones' special gameplay mechanics (the stat rows are applied, but Divine Flesh,
  Chainbreaker and the rest have no consumers yet), the per-Devotion target stats whose consumers are still missing
  (curse duration, minion/totem/brand/channelled, mana cost and shield defences), legacy conquerors, and any future
  per-node stat overrides shared with the radius unique jewels.
- Next done point: at least one conqueror keystone's mechanic is modelled and checked in combat, without changing the
  already verified small/notable replacement.

### 6. Abyss jewels and cluster jewels

The catalogue also has 1 Special row, 8 cluster rows and 9 abyss rows, for 185 entries in total.

- **Abyss jewels (9):** separate feature for abyss sockets on items; not a passive-tree socket. Keep the catalogue rows
  marked out of scope.
- **Cluster jewels (8):** the separate cluster-jewel change (in progress, see
  [status/progression.md](../status/progression.md)): the item, nesting by size, allocation of the client's own node ids
  and the theme/notable stats are in; the id-to-node mapping (partly allocated clusters), the keystones, the radius rules
  and the vendor recipe are not. Reference and measurements: [cluster-jewels](../docs/game/cluster-jewels.md).

## Suggested order and acceptance

1. Catalogue + `/item` (unblocks every later phase).
2. Global rows (highest value, low risk).
3. Conditional rows (conditions evaluated at use).
4. Radius rows, split into in-radius stats, thresholds, node modification and granted nodes.
5. Timeless rows.
6. Abyss item sockets and cluster jewels in their own changes.

Every phase must keep the ordinary/magic/rare jewel path passing: socket placement, swap, pickup, save/load, refund
guard, level requirement, and the `PassiveJewels1` pane update. Use the real client through `scripts/dev/live.ps1`
and check `logs/live-gateway.log`; add focused tests for each new item/mod path.

## Open questions

- Whether unique jewel names should be resolved from `UniqueStashLayout` by name or by unique visual identity (some
  items share a name family or have alternate-art rows).
- How to represent a per-node override for radius jewels so the character panel and the client agree, especially when a
  node's own stats are replaced.
- Whether a jewel whose conditional/radius part is unsupported should be refused at the socket or allowed with only its
  global part active. The plan above refuses it until modelled to avoid silently wrong characters.
