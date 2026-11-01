# 15. Progression: experience, levels, the Passive Skill Tree, loot

Code: `GameServer.Core/World/Progression.cs` (rules, tested in `ProgressionTests`), `InstanceEntities.CharacterPassives`
/ `ReadAllocatePassive` (messages), Gateway `GainExperience`, `ApplyLevel`, `ChangePassives`, `MonsterKilled`,
`DropLoot`, `PassiveTree.cs`, `ItemDrops.cs`, `CharacterSaves.cs`. Live 2026-10-18 with a Witch: kills on The Coast gave
experience (Gateway log), nodes allocated in the Passive Skill Tree, and dying monsters created their drops (log; not
yet confirmed on screen).

## 1. Experience and levels

- **The client derives the level from the experience** in the Player component ([04](04-player-entity.md) §4.6):
  S→C 0x2ba to component 6 with flag bit0 + u32 experience (`InstanceEntities.PlayerExperienceUpdate`). Crossing a
  level start (ExperienceLevels.dat) changes the level on the client.
- **A kill** (`MonsterKilled`): the player gains experience when alive and within `Game_XP_RANGE` cells (default 150)
  of the monster when it dies (melee, Fireball or Righteous Fire). Amount (`Progression.MonsterExperience`):
  DefaultMonsterStats `Experience` at the monster's level × MonsterVarieties `ExperienceMultiplier` / 100 × rarity
  (magic 2, rare and unique 5: our own numbers) × the level penalty, rounded.
- **Level penalty** (`Progression.LevelPenalty`): safe zone 3 + level / 16; beyond it, with d the levels past it,
  ((L + 5) / (L + 5 + d^2.5))^1.5, at least 0.01 (L = the player's level). The extra penalty above level 95 is not
  modelled.
- **A new level** (`ApplyLevel`): the level's stats (0x1b3) and skills (0x2ba Actor), full life and mana (0x1b1), the
  Passive Skill Tree with the new level (0x33, §2), and the account's level through Auth's `set_level` control op (the
  character list and the next tickets carry it). Experience stops at the start of level 100.
- **Saved** in the character's save (`data/saves.db`, `Experience`): added to the character's total in memory and saved within 5 s, at once at a level-up ([saves](../saves.md)); the saved
  experience decides the level, and a stale account level is corrected at login ([saves](../saves.md)).
- **Concurrent events:** each receiving connection reconciles its level from current character experience.
  An old-area gain cannot suppress a live connection's pending level gain or explicit reset.
  Level changes reconcile the latest saved tree and refund balance before publishing, including another area's trim.
  Runtime application derives the target from current experience before publishing stats, independently of Auth latency.
  Queued Player XP updates also sample current experience at the cipher gate.
  Gateway Auth requests serialize per character, recheck XP after replies and correct obsolete in-flight requests.
  A delayed Auth answer cannot apply its captured level or undo an intentional downward control.

## 2. The Passive Skill Tree

- **S→C 0x33** (dispatcher case 0xf, class `0x1435dca60`, read `0x14219e3f0`, handler `0x141ef2710` on the tree at
  instance `+0xc2d8`):

  | Field | Goes to | Meaning |
  |---|---|---|
  | u16 | tree +0x270 | refund points (Orbs of Regret) |
  | u16 | +0x274 | `extra_skill_points` (quest points; dev extra points for now) |
  | u16 | +0x27c | ascendancy points |
  | u8 | +0x26c | **the level**: the client's `points_from_level` = (level − 1) × points per level |
  | u8 | +0x268 | class byte: base IntegerId + packed ClassNo (`CharacterClassByte`), the tree's start |
  | u8 | +0x269 | |
  | u8 flags | | bit0 → +0x91, bit1 → no lists (then `0x141ef73e0` sets only the numbers) |
  | vec16 u16 | +0x180 | **allocated nodes**: PassiveSkills.dat `PassiveSkillGraphId`, raw (little-endian), **sorted** (§2 Gold respec) |
  | vec16 u16 | | (sent empty) |
  | vec16 u16 | +0x1b0 | **the cluster jewel nodes** of the tree's expansion cells: the client's own small layout ids (202, 206 live for a Voices), not PassiveSkillGraphIds; sorted |
  | vec16 u32 | | mastery effects: node \| effect << 16, raw little-endian; effect is the `PassiveSkillMasteryEffects.dat` HASH16 |
  | vec16 u32 | +0x1f8 | **applied tattoos**: node \| `PassiveSkillOverrides.dat` HASH16 << 16, raw little-endian; empty when no tattoo |

  The client's check `0x141ef3e60` logs "character passive skills allocated but only allowed {}.
  points_from_level={}, skill_points_from_passives={}, extra_skill_points={}" and drops unconnected nodes itself.
  The Gateway sends it after the player on entry and after every level or passive change.
- **C→S 0x34** (7 bytes): `u32 node (big-endian), u8 expansion`. The high half of the node is not a mastery effect.
  Captured normal: `00340000DFB000` = 57264,
  Spell Damage and Mana, next to the Witch's start. The client waits for the tree back; without 0x33 the click is
  undone. The Gateway
  checks the node exists in the active graph and fits its ordinary or ascendancy budget. Ascendancy allocations
  additionally require the selected class/family and exclusive-choice rules.
  Both ordinary and ascendancy allocations require graph connectivity.
  It saves the change
  and answers with 0x33; a refused click gets the old tree back. With `expansion` 1 the node is a cluster jewel node
  (its own small layout id, e.g. 202 and 206 for a Voices): the Gateway keeps it in the character's cluster list, saves
  it and sends it back in 0x33's third list, and 0x36 with the flag refunds it. 32-bit ids are still refused intact.
- **C->S 0x77** (12 bytes): an item used on a passive. u32 inventory id, u32 item id, u16 `PassiveSkillGraphId`,
  all big-endian. The item type decides: `apply_passive_tattoo` applies a tattoo, `CurrencyConvertToNormal`
  (Orb of Scouring) removes it. Captured `0077000000010000049C5335` = inventory 1, item 1180, node 21301.
  The Gateway answers with 0xbf, 0x1b3 and 0x33; the tattoo is carried in 0x33's fifth list as
  `node | PassiveSkillOverrides.dat HASH16 << 16`. It validates the target set, the 50-tattoo cap and the
  `PassiveSkillOverrides.Limit` group (one per character; same-node replacement is allowed). Journey Tattoo of
  Makanui sends the HASH16 of the keystone it saved in `TattooKeystones`, not the tattoo's own override row.
- **C→S 0x123** (3 bytes, u8 1 / 0): a panel opened or closed (sent when the tree opens and closes).
- **C->S 0x35** (6 bytes): u16 node (big-endian, PassiveSkillGraphId), u16 effect (big-endian,
  PassiveSkillMasteryEffects.dat HASH16). Captured `003598091194` = node 38921 (Block Mastery), effect 4500
  (+1% spell block per 5% attack block). The Gateway allocates the mastery node and records the effect, then answers
  with 0x33 and the updated stats.
- **S→C 0x3d is the Atlas tree**, not the character's: its three presets ([05](05-inventories-and-ui.md) §4; the check
  `0x141eed000` logs "atlas passive skills allocated but only allowed"). The server populates them from the saved presets,
  and handles Atlas allocation/refund as C→S 0x3e/0x3f. The point budget is saved but completion does not award it yet
  (completed maps themselves are tracked: [21](21-hideouts.md) §4).
- **Masteries**: `PassiveSkillMasteryGroups.dat` maps a mastery node's `MasteryGroup` to its effects and
  `PassiveSkillMasteryEffects.dat` holds their stats. The selected effect is stored per node and its stats are summed
  with the node's (`Passives.Stats`). Block Mastery's four effects are max attack block +2, max spell block +2, 20
  life and 20 mana gained on block, and +1% spell block per 5% attack block (the last is applied after the shield's
  base block).
- **Stats** (`PassiveBonus`, `BaseStats`): the nodes' stats summed; attributes (`base_*`, `additional_*`, all and
  pairs), flat and increased life and mana, increased evasion, energy shield and melee physical damage go into the
  pools ([11](11-skills.md) §4); the other stats are sent as they are (the character panel shows them; nothing uses
  them yet).
- **C→S 0x36** (8 bytes, class `0x1435bf610`, write `0x141f92b00`, built in `0x1403cc410` like 0x34): refund one
  node. u32 the node (big-endian: PassiveSkillGraphId, or a cluster jewel's expansion id), u8 1 for an expansion node,
  u8 1 when the tree was opened as a gold respec (the tree state's +0x17b8 == 2: Bestel's Respecialisation), else 0
  (refund points). "Apply refunds" sends one per picked node; captured `003600001FC70001` and `003600002B780001`
  (8135, 11128, gold). **C→S 0x37** (id only, `0x1409d03bb` / `0x1409da517`) is "Refund All", sent after its Yes.
  Apply refunds (lambda `0x140a164f0` → ApplyChanges `0x14098f030`, which sends the 0x36s and clears the picks) and
  Refund All (`0x1409da470`, drops the picks and sends 0x37) both keep the Respecialisation screen open. The screen
  leaves its mode (graph view +0x17b8: 2 → 0, `0x14098a360`) when the tree reports a newly allocated node, so 0x33's
  lists must be **sorted**: the handler diffs them against its own sorted list (set difference `0x141f145b0`), and an
  unsorted list made kept nodes look newly allocated (`0x141ef69e0` → event → `0x1409dd800` → `0x1409de680` with 0;
  traced with `scripts/re/frida/trace-tree-mode.js`).
  0x34 has the same u32 node + u8 expansion layout (its "u16 0" is the node's high half).
- **Gold respec**: the client's per-node cost `0x141efb140` is `VillageBalancePerLevelShared.dat` GoldRespecCost at
  the character's level (4 gold at level 1, 29119 at 100) times the node's multiplier: 1, 0 for an `IsMultipleChoice`
  node, an `IsExpansion` node's own value (unnamed i32 at +0xb2). The full cost (`GetFullGoldRespecCost`, lambda
  `0x141f1b500`) skips ascendancy nodes. The Gateway (`PassiveTree.GoldRespecCost`, `ChangePassives` with
  `goldRespec`) refunds only allocated nodes, refuses when the gold is short, removes the nodes' mastery effects,
  saves the passives, takes the gold from the account (`AccountStash.TrySpendGold`, nothing when refused), and answers with the gold (0x2a9), the stats and the tree (0x33). Refund All
  refunds every non-ascendancy node.
- **Refund points** (0x36 flag 0): the Gateway refunds one allocated, non-ascendancy node per message, spends one
  point from 0x33's first u16, removes the node's mastery effect, saves the tree and the points, and answers with the
  stats and the tree. An unallocated node or too few points is refused (the old tree is sent back). Refund points are
  saved per character. Using an Orb of Regret (right-click, C→S 0x58) takes one item and adds one point; the stack's
  0xbf and the tree's 0x33 are sent back. `/add_refund_points <n>` remains a dev shortcut. Expansion nodes are still
  refused.
- `/passives` lists the ordinary passive and refund points; `/reset_passives` refunds ordinary nodes (free).

### Passive tree jewel sockets

The ordinary tree's 21 large jewel sockets are the client's `PassiveJewels1` inventory (Inventories.dat row 23,
inventory id 24, 60x1). Pane cell `x` is the `PassiveJewelSlots.dat` row; that row's first column (`Slot`) is the
`PassiveSkills.dat` row of the socket node. Rows 21-56 are cluster-jewel expansion slots and rows 57-59 are
ascendancy charm sockets, so only slots 0-20 are ordinary jewel sockets. `PassiveTree.JewelNodeForSlot` and
`JewelSlotForNode` expose this mapping from `PassiveJewelSlots.json` and `PassiveSkills.json`.

A jewel is moved in like any inventory item: C->S 0x2a places the held item at (slot, 0), 0x4a swaps it onto an
occupied cell, and 0x28 picks it up. The server checks that the slot is an ordinary socket, its node is allocated in
`conn.Passives`, the item is ItemClasses `Jewel` and not a `Metadata/Items/Jewels/JewelPassiveTreeExpansion*` cluster
jewel, and its level requirement is met. The jewel stays in `PassiveJewels1` (saved with the other inventories), and
`CharacterStatSources` includes the jewels of allocated sockets in the worn-item stat pass, so their global mods reach
the pools and the panel like equipment. `SendPassiveJewelStats` recomputes the pools and sends 0x1b3 after a move.

Refunding a jewel socket that still holds a jewel is refused until the jewel is taken out; a level trim prefers
non-jewel leaves and returns a jewel to the main inventory if the socket must be removed. Not modelled: unique-item
generation, conditional mods, radius/area jewels other than timeless, cluster jewels and abyss sockets. Timeless jewels
are implemented for the item, radius tree and node stats; `/timeless` creates them, and the conqueror keystones'
special mechanics remain separate work. See [timeless-jewels.md](../game/timeless-jewels.md). The complete unique-jewel
catalogue and the phased plan are in [unique-jewels.md](../game/unique-jewels.md).

The Adorned is the pilot unique jewel: `/adorned [0-1000]` creates the corrupted unique Crimson Jewel with
`CorruptedMagicJewelModEffectUnique__1` (HASH16 11126). While it is in an allocated ordinary socket, every corrupted
magic jewel in the ordinary passive sockets has each mod value multiplied by `(100 + percent) / 100` before the global
stat sum. Cluster sockets and the pre-3.25 50-150% variant are not covered.

### Ordinary-tree connectivity

`OrdinaryPassiveRules` checks the complete candidate against the selected normal or generated Phrecia graph before
`ChangePassives` saves or charges anything.
The character's implicit root comes from `PassiveSkills.Characters`, translated through `Characters.IntegerId`.
Other class starts are not paid allocations or traversal bridges.
An allocated Ascendant Path node can supply another implicit root through `display_can_take_character_start_point`.
Its value is the target's `IntegerId` directly, not its table row or `IntegerId + 1`.
For example, Path of the Marauder `24755` grants class `1`; its root is resolved from the extracted tables.

Traversal follows undirected graph links through allocated ordinary nodes.
Masteries may be reached from an allocated notable but cannot connect other branches.
Proxy passives and expansion nodes are refused until actual cluster-jewel topology is implemented.
Ordinary large jewel sockets remain allocatable.
Allocation batches may arrive in any order; only their final connected set matters.
The same check covers mastery selection, regret/gold refunds, development commands and family changes.
It runs after the proposed ascendancy change, so refunding a Path grant cannot orphan ordinary nodes.

On loading, validation precedes normalization and legacy gold migration.
An invalid saved tree is refused with its original save retained.
If a valid tree exceeds a lower level's budget, trimming removes the latest safely removable leaf rather than
blindly deleting the last listed node.
Mastery effects belonging to removed nodes are removed with them.
Identity, ordinary/ascendancy allocations and refund payment still share the existing save transaction and stale-save
comparison.

### Main ascendancies

`AscendancyCatalog` joins the exact target's Ascendancy/Characters/PassiveSkills tables with the generated PSG
topology (`data/passive-skill-graphs.json`, extraction manifest entry `passive-graphs`). It retains 21 ordinary and
19 Phrecia choices, including all ordinary Scion alternatives. The durable identity is family + stable table Id +
revision, not a packed class byte or a table row. See [the feature and limitations](../game/ascendancies.md).

`MainAscendancy` stores that identity, its own allocated nodes and the four idempotent Labyrinth-tier awards (two
points each). Start nodes are implicit/free and are not sent as allocated nodes. 0x33 sends total earned ascendancy
points and the union of both allocated lists; the client derives spending. Nodes' `SkillPointsGranted` explicitly
add to the ordinary budget, independently of their ascendancy cost. Level trimming affects only ordinary nodes.

Refund currency has a different rule from allocation cost: a multiple-choice parent consumes one ascendancy point
and its effect consumes zero; refunding the parent costs zero regrets, the effect five. Other ascendancy nodes cost
five, ordinary nodes one. Gold respec remains ordinary-only. Removing point grants or prerequisites cannot leave
overspending or orphaned ascendancy nodes. Changing the selected class/family requires an empty ascendancy allocation.

Identity, allocations, choices and refund payment commit in one save before runtime/stat publication. A passive
snapshot comparison inside the transaction prevents an old connection from overwriting newer progress. Unknown
saved revisions, nodes and graph mismatches refuse loading and preserve the original save. Real refund-currency use
refreshes the snapshot before the next allocation.

The native class byte is `baseIntegerId | ((ClassNo & 3) << 6) | ((ClassNo & 4) << 3)`; zero ClassNo is unascended.
Phrecia uses world stat 21146 in **the first** optional compact pair list of 0x1d, not 0x33's secondary-ascendancy byte.
The character's actual league and account stash are unchanged.
`/ascendancy normal <name>` chooses an ordinary class and reloads the area.
`/ascendancy_points` is a dev award shortcut.
Phrecia selection and game connections are explicitly gated pending an agreed client-compatible graph.
The installed alternate graph has older ordinary topology; this slice never installs or recovers a graph in the client.
Ordinary/unascended connections do not run an alternate-graph compatibility check.
The native Labyrinth/Altar flow is still separate work (#151).
[Native addresses, wire layout and live traces](../re/ascendancy-client-3.29.3b.md).

**Book of Skill** (`UseSkillBook`). Right-clicking one (C→S 0x58, as an Orb of Regret) uses it up and adds one extra
passive skill point (`ExtraPassivePoints`, as `/add_passive_points 1`); the book and the point are saved in one
transaction (`ItemMoves.UseForPassivePoint`), then S→C 0xbf removes the book and 0x33 shows the new point. Every
BaseItemTypes row named "Book of Skill" counts (`Metadata/Items/QuestItems/SkillBooks/Book-a1q6` … one per quest); they
are `Usable` quest items, so the client sends 0x58 for them. A **Book of Regrets** (Acts 1, 6, 9, 10) goes the Orb of
Regret's way but gives two refund points (ClientStrings `ItemFunction2RespecPoints`, "Grants two Passive Respec Points").

## 3. Loot

`ItemDrops.Roll` (our own table): normal monsters drop 1 item at 35%, magic 1–2, rare 2–4, unique 4–6, times the item
quantity. Each item is currency (35%: Scroll of Wisdom most, down to the Mirror), a gem (4%: an obtainable gem
whose DropLevel is at most the monster's level, from `GemSources`, at gem level 1 like a vendor's), a life or mana flask (8%)
or equipment (53%: a weighted slot, then any of its bases with DropLevel ≤ the monster's level, minus unique-only, event
and league bases), item level = the monster's level, +1 magic, +2 rare, +3 unique (gems keep 1), with the type's default sockets. Equipment then rolls
unique, rare or magic, raised by item rarity. A unique is chosen among its base's by the inverse of its
`VillageUniqueDisenchantValues` value (the client's measure of a unique's rarity; `RuthlessValue` in Ruthless); where a
base's weights add up to less than 1 the rest gives a rare, and a unique outside that table does not drop.
`DropLoot` puts each as a WorldItem
([13](13-items.md) §5) 3–6 cells around the corpse, on a walkable cell when the walk map is known. `Game_DROPS=0` turns
it off.

## Open

- Phrecia client compatibility, special ascendancy gameplay and native Labyrinth/Altar acquisition (#151).
  Selection/points/saves have automated coverage; this slice makes no claim that all 19 classes' gameplay works.
  See [scope and checks](../game/ascendancies.md).
- The real rarity experience bonuses, the level 95+ penalty, party sharing.
- Quest passive-point rewards and cluster/expansion topology; ordinary and ascendancy connectivity are checked.
- Real drop pools (MonsterVarieties / area drop tables), item rarity and mods, quantity and rarity bonuses; ground
  items only exist on the connection that saw them drop.
