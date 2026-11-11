# Status: Experience and the passive tree

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/progression.md](../roadmap/progression.md).

## Works (verified with the real client)

- Experience and levels from kills, with the level penalty and party share.
  Docs: [15](../docs/protocol/15-progression.md) §1.
- Passive Skill Tree: allocation, refunds, Orbs of Regret, Books of Skill and Regrets.
  Docs: [15](../docs/protocol/15-progression.md) §2.
- Passive masteries: S→C 0x33 carries selected effects (`node | effect << 16`) and C→S 0x35 selects the effect HASH16;
  a mastery node can be allocated normally and its effect selected/changed without spending another point.
  Block Mastery's four effects are implemented: max attack block +2, max spell block +2, 20 life + 20 mana on block,
  and +1% spell block per 5% attack block; the effects are saved with the character.
  Fire Mastery's 1 Life regenerated per second per 1% uncapped fire resistance counts in the life regeneration
  (positive resistance only; live 2026-11-03: 161% added 161 per second).
  Wand Mastery's wand-only points are modelled: the extra attack projectile and accuracy equal to intelligence, each
  only while a wand is held; the mastery's 0.5% attack life and mana leech is a generic effect that applies with any
  weapon. Power charge on wand crit and additive spell damage to wand attacks were already applied. Spellslinger's
  reservation efficiency has no consumer while Spellslinger itself is unimplemented.
  Code: `PassiveTree.cs`, `CharacterSaves.cs`, Gateway (`ChangePassives`, `PassivesMessage`).
  Docs: [15](../docs/protocol/15-progression.md) §2.
- Passive tree jewel sockets (server and live stat check 2026-10-27; tree art check pending): the 21 ordinary large
  sockets are the `PassiveJewels1` pane (Inventories.dat row 23, inventory id 24, 60x1). Pane cell = the
  `PassiveJewelSlots.dat` row and its `Slot` = the `PassiveSkills.dat` row. Ordinary/magic/rare jewels can be placed,
  swapped and picked up (0x2a/0x4a/0x28), are saved with the inventories, and their global mods apply like worn items;
  the socket must be allocated, the item a non-cluster jewel and its level requirement met. Refunding a socket that
  holds a jewel is refused until it is taken out; a level trim returns the jewel to the inventory.
  Unique jewels use the same path but have no unique item catalogue/source yet.
  The Adorned is the pilot unique jewel: `/adorned [0-1000]` creates the corrupted unique Crimson Jewel, and its effect
  scales corrupted magic jewels in the ordinary passive sockets (live: 225% -> 240% -> 255% fire resistance with a
  +15% jewel and The Adorned at 100%). Cluster sockets and the pre-3.25 50-150% variant are not covered.
  Percentage attribute mods (`strength_+%`, `dexterity_+%`, `intelligence_+%`, `all_attributes_+%`) now scale the final
  class and item attributes, so Brawn and the Emperor's jewels reach the pools and the character panel.
  Readiness, counted per stat: 46 of the 97 stats of the 43 Global-set jewels (42 Global + The Adorned) are in game
  and 11 more are panel only, so the set is 47% done by stats (59% counting the panel rows); the whole 185-jewel
  catalogue is 10%. The per-jewel table is in [unique-jewels](../docs/game/unique-jewels.md).
  Code: `PassiveTree.JewelNodeForSlot`, Gateway (`PlaceItem`, `HeldOntoItem`, `PickUpItem`, `CharacterStatSources`).
  Docs: [15](../docs/protocol/15-progression.md) §2a, [unique-jewels](../docs/game/unique-jewels.md).
- The seven single-keystone small cluster jewels grant their native keystone stat on allocation. Their individual
  combat mechanics remain partial. For ordinary clusters, `/cluster_jewel <small|medium|large> [passives] [sockets]
  [theme] [notables…]` and `/voices [1|3|5|7]` make real items, and nesting follows the size rule (a tree socket takes any
  size, a large jewel's sockets medium/small, a medium one's socket small). Their passives are the client's own
  expansion ids (block base fixed per pane cell, e.g. 195..206 for cell 20); the server keeps them in the third list of
  0x33, counts a point each, saves them with the character and restores them on login up to the socketed jewels' node
  budget (the `Adds N Passive Skills` count per jewel, which counts the sockets; Voices' empty passives are plus its
  sockets). `ClusterJewelLayout` reproduces the client's measured layout: the block base is `39 * ClusterIndex` for a tree socket,
  `9 * the child's ClusterIndex + 12` for a socket of one and `6` deeper, the position is `(StartIndices[size] +
  templateIndex) % TotalIndices`, a tree-socket large jewel keeps the full ring (a small passive gets the theme, a notable
  its own row, a socket nothing) and a jewel in an expansion cell is compressed onto half the ring (a measured 4-passive
  medium: smalls 208, 209, 211 and socket 212 in cell 51, 225, 228, 229 and 226 in cell 53). 20 of the 42 theme stats are
  translated; expansion-cell shapes without a measured layout (notables) still use the count-based estimate. A small
  cluster jewel also fits medium and large sockets now (the client pads the cell); a blue/yellow small (with a notable)
  gets its measured graph (base 213 in cell 54: notable 213, smalls 214/215; in the large tree socket 17 the small's
  157/161 are theme smalls and 159 its first notable, the other notable mods inert) and gives the theme per small plus
  the first notable's row, while a Normal theme-only small drew no client graph in our test. Live full-tree check: a
  large in tree socket 11 (base 0), a medium in its cell 21 (base 12) and a blue small in cell 24 (base 18); the small's
  18/19 added the theme and 20 was Fettle. The chat's per-node debug line is gone; the Gateway still logs the ids and the
  `passive jewel stats` line. The jewel's rarity follows its notables (none Normal, one or
  two Magic, three Rare): a Magic cluster jewel with no affix makes the client draw the whole cluster as empty
  "Nothingness" nodes.
  Still missing: keystones, radius rules and the vendor recipe. A cluster jewel fits only a socket of its own size: the client builds the layout from the cell, so a
  smaller jewel is padded with empty "Nothingness" nodes that grant nothing.
  An end-to-end client check (`scripts/dev/jewel_selftest.py`) covers insert/remove with no node taken, the small and
  notable stats of a large cluster, a medium cluster in a large one's socket, and a normal jewel with +80 life in a
  cluster socket.
  Code: `Program.Items.cs` (`ClusterJewelSocketed`, `ClusterNodeBudget`, `CreateClusterJewel`, `CreateVoices`),
  `ClusterJewelStats.cs`, Gateway (`ChangeClusterPassive`). Docs: [cluster-jewels](../docs/game/cluster-jewels.md).

- Timeless jewels roll a faction seed and current conqueror in ordinary unique generation.
  `/timeless <name> [displayed seed] [conqueror]` creates the six unique jewels with the client's `UniqueJewelAlternateTreeInRadius*` mod. The client itself draws the
  conquered radius from those stats; the server reproduces the same alternate tree with the TinyMT32 generator, the
  `AlternateTreeVersions` flags, `AlternatePassiveSkills` and `AlternatePassiveAdditions`, and applies replacement or
  added stats to allocated ordinary nodes in the fixed 0..1500 radius. The RNG and generated values match Path of
  Building's LUTs (Vaal small 62429/seed 2000: Aura Effect 3; notable 37326: Cult of Fire, 30 fire resistance and 1
  maximum) and the live client log confirms the alternate tree version, seed and conqueror. `Elegant Hubris` stores its
  displayed seed divided by 20; only current conquerors are offered; only one Historic timeless jewel may be socketed;
  cluster sockets are refused. Militant Faith also carries two distinct current per-10-Devotion mods (the pre-3.29.0
  reduced Mana Cost row is legacy and not rolled); `DevotionStats` scales them by `floor(Devotion / 10)` into the normal
  stats, so resistance/damage/aura/regen/ailment targets are applied while curse duration, minion/totem/brand/channelled,
  mana cost and shield defences remain unread. The generated list covers the 96 timeless additions, including the four
  rows missing from Path of Building's reference data.
  Keystone stat rows are applied, but the conqueror keystones' special gameplay mechanics (Divine Flesh, Chainbreaker and
  the rest) are still missing.
  Code: `TimelessJewelCatalog.cs`, `TimelessJewelEngine.cs`, `PassiveTree` geometry/radius, `Program.Progression.cs`
  (`EquipmentStatSources`), `Program.Items.cs` (`PassiveJewelRefusal`). Docs:
  [timeless-jewels](../docs/game/timeless-jewels.md).

- Radius unique jewels (pilot 2026-10-29): all **78 rows / 76 distinct names** with a `Radius:` line are classified in
  [radius-jewels](../docs/game/radius-jewels.md) and the generated
  [radius-jewel-list](../docs/game/radius-jewel-list.md): 6 attribute conversions, 6 damage/defence/weapon
  conversions, 3 notable replacements, 10 passive additions, 33 threshold/skill jewels, 11 per-node scaling, 3
  allocation/tree rules, 1 passive-effect/notable-suppression, 5 special/global-only. **The 42 implemented jewels
  (39 pilot rows plus Might in All Forms, Pitch Darkness and Sudden Ignition):** `/radius_jewel <name> [variant]`
  creates the six attribute conversions, five damage/defence conversions, three notable replacements, ten passive
  additions, eleven per-node scaling jewels
  (Anatomical Knowledge, Eldritch Knowledge, Pugilist, Spire of Stone, Static Electricity, the Tempered/Transcendent
  trios), Might of the Meek and the three allocation/tree-rule jewels (Intuitive Leap, Thread of Hope's five ring
  variants, Unnatural Instinct).
  `EquipmentStatSources` moves/transforms the matching stats, replaces allocated notables, appends the passive
  additions (including the resistance-scaled Breach jewels), selects the Light/Perandus variant, computes the per-node
  scaling from allocated and unallocated attributes, applies Might of the Meek's 50% non-keystone effect with notables
  suppressed, and swaps Unnatural Instinct's allocated and unallocated small passives. `OrdinaryPassiveRules` treats
  Intuitive Leap/Thread of Hope bypass nodes as extra roots; `PassiveTree.NodesInRing` selects the Thread of Hope ring,
  and taking either jewel out is refused while a bypass-allocated node remains. Live: Brute Force in socket 1 changed
  Strength 559 -> 549 and Intelligence 647 -> 681; Energy From Within in socket 16 changed life 5857 -> 5311 and ES
  243 -> 273; The Light of Meaning (Life) in socket 12 changed life 5311 -> 5355; in the full demo layout (Brute Force
  socket 1, Anatomical socket 11, Fevered Mind socket 13, Light socket 12, Energy From Within socket 16) Anatomical is
  worth +165 (life 5520 with it, 5355 without), and the same layout reads mana 2345 and ES 276 (mana 2355/ES 277 after
  the later relog). Socket 11 has 20 Int, so `floor(20 / 3) = 6` flat life is part of that total; socket 13 has no
  Intelligence and in the earlier isolated check only the global life% applied (5355 -> 5502). Might of the Meek in cell
  3 changed life 5520 -> 5529 and mana 2345 -> 2375 from the 50% effect on the Intelligence/Strength smalls; uncapped
  attack/spell block moved 168%/27% -> 162%/20% as the Dark Arts notable was suppressed (partly offset by the scaled
  dual-wield block smalls). Intuitive Leap in cell 3 accepted disconnected node 55247, life went 5520 -> 5575 and the
  allocation survived a relog; in cell 1 it accepted 42981 but refused its neighbour 53802 outside the disc (no
  bridging). Thread of Hope uses the PoB 3_16 Variable rings 960-1320, 1320-1680, 1680-2040, 2040-2400 and 2400-2880,
  does not enable jewel sockets in the ring, and its -10% all-res roll changed `/resistances` 268/221/221 ->
  258/211/211; its ring value is implicit and the allocate mod's base radius is 0 so the client draws the annulus
  instead of a filled disc. Massive in cell 3 accepted 23507 (2628) and 48477 (523 allocated); after refunding them the
  Large item accepted 38129 (1808); swapping the jewel while those bypass nodes remained was refused. Might in All
  Forms in cell 1 changed the `passive jewel stats` melee value 129 -> 137 (+8 = 40 Dex+Int in radius / 5) while Brute
  Force was in the inventory; restoring Brute Force returned it to 127 (the conversion changes the baseline). Cluster
  passives are never in a radius jewel's radius: Path of Building precomputes `nodesInRadius` before its cluster graphs
  are built, so the server's ordinary-PSG-only radius is intentional. The 33 threshold jewels have generated item
  sources (`scripts/re/threshold_jewels.py`, `ThresholdJewelCatalogue.cs`); the client applies their local threshold
  mods. Current Dead Reckoning gives skeleton hits flat chaos from the equipped shield's local energy shield without
  an attribute threshold. `ThresholdJewels.cs` parses the target/attribute mask, sums every ordinary passive in radius (allocated or
  not, PoB's `Threshold` pass) and adds the mapped stat when the total is met. Live: `Combat Focus Cobalt` in socket
  36634 logged `elemental_hit_cannot_roll_fire_damage 1` (60 Int); `Collateral Damage` in socket 60735 logged
  `galvanic_arrow_area_damage_+% 50` and `shrapnel_shot_radius_+% 25` (60 Dex); `Spirited Response` in socket 6
  (cell 8) changed mana 2355 -> 2476 from its 10% maximum mana global. First Snow and Frozen Trail add projectiles, Omen on the Winds grants Ice Shot pierce,
  and Hazardous Research and Ring of Blades enable circular volleys for their respective skills. Other skill-specific consumers remain partial and
  targets without a client stat row are skipped. Unnatural Instinct in cell 3 changed life 5520 -> 5685, mana
  2355 -> 2326 and ES 277 -> 285 from three allocated Int smalls and three unallocated Life/ES smalls. Lioneye's Fall's
  radius now converts melee and melee-weapon stats with native bow equivalents, including physical damage, attack
  speed and critical modifiers. Conditional modifiers without a mapped bow stat still need individual consumers.
  Warrior's Tale scales tattoo stats within its socket radius. Grand Spectrum rolls one of three current variants
  per base and scales by the total equipped across bases (limit three). Inspired Learning's rare-mod steal and the
  skill-specific threshold consumers remain partial. The socket/save path,
  `PassiveTree.NodesInRadius`/`NodesInRing`, the timeless per-node pattern and the art table are reused. The radius
  suite has 98 tests.

- Impossible Escape (generated special): `/impossible_escape <keystone>|list` creates the 48-variant Viridian Jewel
  (Small, limit 1, corrupted). The item mod stores the keystone's `PassiveSkillGraphId` and display base radius 800;
  the effect radius is Path of Building 3_16's Small **960**. Legacy items with the Maven table row are migrated on
  load. The allocated socket grants disconnected passives around the named keystone, excluding the keystone itself.
  A live Witch allocated Dire Torment (9015) without taking Supreme Ego (49639). Earlier client checks also placed
  Replenishing Remedies (50690) within the ring and refused pickup while a disconnected node depended on the jewel. Rational Doctrine (`/unique_jewel`) creates
  consecrated ground while stationary with Strength highest and profane ground on a critical strike with Intelligence
  highest; both effects linger for 4 seconds, and hits against enemies on profane ground have 100% increased critical
  chance. The profane-ground debuff and its critical-chance consumer have automated coverage. Live 2026-11-01 (the
  item's stats set through `/set_stat`): a critical strike created the profane ground art, applied buff 1569, and
  removed it after the linger.

- Watcher's Eye, Bound By Destiny, Sublime Vision, The Balance of Terror and Megalomaniac have named item generation.
  Their encounter drop rates and individual conditional-stat consumers are partial. Megalomaniac adds three distinct
  notables and an empty small passive. Forbidden Flame and Forbidden Flesh still lack a mainline source.

- Command-based ordinary ascendancy selection, separate point awards, connected allocations, paid refunds and saves.
  A native Duelist/Slayer smoke checked ordinary nodes, ascendancy allocation/refund and both point budgets.
  Choice and progress survived relog and a Gateway process restart; a Phrecia request left the saved JSON unchanged.
  The read-only client trace observed class bytes, the Default graph, allocated nodes and separate point counters.
  Automated domain, packet and production-handler tests cover ownership, separate budgets, graph connectivity,
  multiple-choice effects, atomic refunds, stale connections and old/unknown saves.
  Area admission validates saved progress before inventory changes, then validates items before gold migration.
  Level changes recheck concurrent passive mutations before applying; queued tree packets publish current state.
  Old XP events cannot undo later gains or explicit level resets; Auth follows current XP.
  Old-area gains cannot suppress the live connection's pending level publication.
  XP level reconciliation adopts completed saved trims/refunds atomically before updating runtime state.
  Extra-point grants commit before runtime publication, including concurrent XP updates and failed-write checks.
  Code: `AscendancyCatalog`, `AscendancyProgress`, `OrdinaryPassiveRules`, `CharacterSaves`, Gateway progression.
  Docs: [scope, evidence and beginner steps](../docs/game/ascendancies.md).

- Descendancy, the second ascendancy layer: `Descendancy.dat` rows (Affliction's three Wildwood specialisations and the
  thirteen 3.29 Bloodline classes) selected through the `0x33` spare byte and saved with the character.
  The client's class selector opens with `0x7b` kind `0x4c` (first u32 = row) and Accept sends `0x3a`; both and the
  selector window were checked against the local 3.29.3c client.
  The tree panel reads the row from the Player component's `+0x1b0`, so the player object now carries it too.
  The server records the selector it opened with `0x7b` kind `0x4c` and accepts `0x3a` only while it is open in the
  same area;
  a Wildwood row needs its Azmeri class flag, and a layer with allocated nodes must be refunded before another is chosen.
  The `gm-commands` mod's `/descendancy` selects or clears a row, sets the three Wildwood Azmeri flag groups and
  reloads the area; its selection is the unrestricted development path.
  Nodes allocate by the layer's own `DescendancyKey` tree, stay connected to its implicit start, save, and count
  against the shared ascendancy points; a live Primalist allocation was checked in the local client.
  The three Wildwood rows keep Affliction's separate eight points: `0x33` carries the earned points plus eight for
  those rows, the main ascendancy stays capped at its own earned points and the layer at eight.
  A refused allocation now replies with its reason in chat; `/descendancy refund <node|all>` reverts the layer.
  Refunds charge the same five refund points per node; their stats are part of the passive stat list.
- Elementalist Shaper of Flames: all three stats work. `always_ignite` adds 100% ignite chance;
  `all_damage_can_ignite` lets any hit ignite and puts every damage type into the ignite's base (90% per second,
  fire scaling plus the types the portion visited, as poison's physical part); the panel's ignite rows follow.
  `enemies_ignited_by_you_have_physical_damage_%_converted_to_fire` (40%) converts a burning monster's hits on the
  player through the Born under Fire hook (`EnemyAuraConversionLocked`). Verified live on 3.29.3c with Arc: 40 of 40
  surviving hits ignited, 56 converted monster hits, no client crash. Not covered: the golem and Abberath ignites
  still need fire damage; the conversion applies to hits only.

- Antiquarian: all nine notables and nine small nodes execute through three legal connected eight-point builds.
  Native checks cover Ward/Adrenaline, final attributes/requirements, stun recovery, Enduring Cry duration,
  always-hit attacks, Far Shot arrows and active-shield block/ranged mitigation.
  Paid full refunds, reallocation, pause, death, area carry, relog and Gateway restart were checked.
  Selection requires `Game_VERIFIED_ANTIQUARIAN=1` and read-only verification of the exact installed compatible graph.
  Ordinary Juggernaut selection after a full refund restores the Default graph.
  Docs: [whole-class evidence, setup and explicit model boundaries](../docs/game/antiquarian.md).
- Juggernaut: all 14 ordinary nodes apply and passed native acceptance, [node by node](../docs/game/juggernaut.md).
- Slayer: every ordinary node's stats apply except Impact's melee range, checked in the client except overkill leech,
  [node by node](../docs/game/slayer.md).

- Gambler: every node in its installed graph has a gameplay consumer, including both Block Luck choices and drawbacks.
  Legal eight-point builds, exclusive choices, paid refunds, failed-save rollback and reload have Gateway coverage.
  Incoming ordinary monster ailments give Risk Aversion an actual avoidance consumer, alongside bounded stun recovery.
  Native checks cover legal builds, both choices, outgoing/incoming resistance outcomes, damage, real ignite avoidance,
  lethal death-panel/checkpoint behavior, refunds, relog and restart.
  Current-base build/tests and exact related-head composition preserve the documented shared merge resolutions.
  Default Phrecia admission is gated; the explicit Gambler opt-in verifies installed graph bytes read-only.
  Docs: [Gambler checklist, scope and verification](../docs/game/gambler.md).
- Passive masteries, with Block, Fire and Wand Mastery effects. Docs: [15](../docs/protocol/15-progression.md) §2.
- Passive tree jewel sockets, percentage attribute mods and The Adorned (tree art check pending).
  Docs: [15](../docs/protocol/15-progression.md) §2a, [unique jewels](../docs/game/unique-jewels.md).
- Cluster jewels: the single-keystone smalls' keystone stats, nesting, the client's layout and theme stats; keystones,
  radius rules and the vendor recipe are missing. Docs: [cluster jewels](../docs/game/cluster-jewels.md).
- Timeless jewels: the alternate tree in radius and Militant Faith's Devotion mods; conqueror keystone mechanics are
  missing. Docs: [timeless jewels](../docs/game/timeless-jewels.md).
- Radius unique jewels: 42 implemented and the 33 threshold jewels; skill-specific consumers are partial.
  Docs: [radius jewels](../docs/game/radius-jewels.md), [list](../docs/game/radius-jewel-list.md).
- Impossible Escape and Rational Doctrine. Docs: [radius jewels](../docs/game/radius-jewels.md),
  [13](../docs/protocol/13-items.md), "What some flasks and Rational Doctrine make".
- Watcher's Eye, Bound By Destiny, Sublime Vision, The Balance of Terror and Megalomaniac items; their drops and
  conditional consumers are partial. Docs: [unique jewels](../docs/game/unique-jewels.md).
- Ordinary ascendancy selection, point awards, allocations, paid refunds and saves.
  Docs: [ascendancies](../docs/game/ascendancies.md).
- Elementalist Shaper of Flames. Docs: [20](../docs/protocol/20-damage.md) §3.
- Antiquarian. Docs: [Antiquarian](../docs/game/antiquarian.md).
- Juggernaut. Docs: [Juggernaut](../docs/game/juggernaut.md).
- Slayer, except Impact's melee range. Docs: [Slayer](../docs/game/slayer.md).
- Gambler. Docs: [Gambler](../docs/game/gambler.md).

## Partly working

- Hierophant, except the Brand nodes. Docs: [Hierophant](../docs/game/hierophant.md).
- Behemoth through the optional Phrecia module. Docs: [Behemoth](../docs/game/behemoth.md).
- Whisperer through the optional module. Docs: [Whisperer](../docs/game/whisperer.md).
- Aristocrat (not checked live). Docs: [Aristocrat](../docs/game/aristocrat.md).
- Ancestral Commander (not checked live). Docs: [Ancestral Commander](../docs/game/ancestral-commander.md).
- Paladin (not checked live). Docs: [Paladin](../docs/game/paladin.md).
- Polytheist (not checked live). Docs: [Polytheist](../docs/game/polytheist.md).
- Surfcaster (not checked live). Docs: [Surfcaster](../docs/game/surfcaster.md).
- Phrecia ascendancies: the optional module, Harbinger, Forest Tracking and family gem levels.
  Docs: [Phrecia modules](../docs/game/phrecia-modules.md), [Harbinger](../docs/game/harbinger.md),
  [ascendancies](../docs/game/ascendancies.md).
- Ruthless experience and passive alternatives. Docs: [league modes](../docs/game/league-modes.md).

## Not working yet

- Saving quest progress, quest passive/refund rewards and native Labyrinth/Altar acquisition (#151).
- Tattoos: 75 rows work in the client; 22 rows (new consumers, Loyalty summons, Forbidden tattoos) are missing.
  Docs: [tattoos](../docs/game/tattoos.md).
