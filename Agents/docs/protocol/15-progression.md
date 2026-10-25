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

## 2. The Passive Skill Tree

- **S→C 0x33** (dispatcher case 0xf, class `0x1435dca60`, read `0x14219e3f0`, handler `0x141ef2710` on the tree at
  instance `+0xc2d8`):

  | Field | Goes to | Meaning |
  |---|---|---|
  | u16 | tree +0x270 | refund points (Orbs of Regret) |
  | u16 | +0x274 | `extra_skill_points` (quest points; dev extra points for now) |
  | u16 | +0x27c | ascendancy points |
  | u8 | +0x26c | **the level**: the client's `points_from_level` = (level − 1) × points per level |
  | u8 | +0x268 | the class byte (Characters.dat IntegerId, ascendancy in bits 5–7): the tree's start |
  | u8 | +0x269 | |
  | u8 flags | | bit0 → +0x91, bit1 → no lists (then `0x141ef73e0` sets only the numbers) |
  | vec16 u16 | +0x180 | **allocated nodes**: PassiveSkills.dat `PassiveSkillGraphId`, raw (little-endian), **sorted** (§2 Gold respec) |
  | vec16 u16, vec16 u16 | | (sent empty) |
  | vec16 u32 | | mastery effects: node \| effect << 16, raw little-endian; effect is the `PassiveSkillMasteryEffects.dat` HASH16 |
  | vec16 u32 | | (sent empty) |

  The client's check `0x141ef3e60` logs "character passive skills allocated but only allowed {}.
  points_from_level={}, skill_points_from_passives={}, extra_skill_points={}" and drops unconnected nodes itself.
  The Gateway sends it after the player on entry and after every level or passive change.
- **C→S 0x34** (7 bytes): `u32 node (big-endian), u8 expansion`. The high half of the node is not a mastery effect.
  Captured normal: `00340000DFB000` = 57264,
  Spell Damage and Mana, next to the Witch's start. The client waits for the tree back; without 0x33 the click is
  undone. The Gateway
  checks the node exists in the active graph and fits its ordinary or ascendancy budget. Ascendancy allocations
  additionally require the selected class/family, graph connectivity and exclusive-choice rules. It saves the change
  and answers with 0x33; a refused click gets the old tree back. Unsupported expansion/32-bit IDs are refused intact.
- **C→S 0x123** (3 bytes, u8 1 / 0): a panel opened or closed (sent when the tree opens and closes).
- **C->S 0x35** (6 bytes): u16 node (big-endian, PassiveSkillGraphId), u16 effect (big-endian,
  PassiveSkillMasteryEffects.dat HASH16). Captured `003598091194` = node 38921 (Block Mastery), effect 4500
  (+1% spell block per 5% attack block). The Gateway allocates the mastery node and records the effect, then answers
  with 0x33 and the updated stats.
- **S→C 0x3d is the Atlas tree**, not the character's: its three presets ([05](05-inventories-and-ui.md) §4; the check
  `0x141eed000` logs "atlas passive skills allocated but only allowed").
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
The actual league and account stash remain Standard. `/ascendancy` lists/chooses both families, reloading the area;
`/ascendancy_points` is a dev award shortcut. The native Labyrinth/Altar flow is still missing.
[Native addresses, wire layout and live traces](../re/ascendancy-client-3.29.3b.md).

**Book of Skill** (`UseSkillBook`). Right-clicking one (C→S 0x58, as an Orb of Regret) uses it up and adds one extra
passive skill point (`ExtraPassivePoints`, as `/add_passive_points 1`); the book and the point are saved in one
transaction (`ItemMoves.UseForPassivePoint`), then S→C 0xbf removes the book and 0x33 shows the new point. Every
BaseItemTypes row named "Book of Skill" counts (`Metadata/Items/QuestItems/SkillBooks/Book-a1q6` … one per quest); they
are `Usable` quest items, so the client sends 0x58 for them. A **Book of Regrets** (Acts 1, 6, 9, 10) goes the Orb of
Regret's way but gives two refund points (ClientStrings `ItemFunction2RespecPoints`, "Grants two Passive Respec Points").

## 3. Loot

`ItemDrops.Roll` (our own table): normal monsters drop 1 item at 35%, magic 1–2, rare 2–4, unique 4–6. Each item is
currency (45%: Scroll of Wisdom and Portal Scroll most, then the basic orbs, Chaos rarest), a life or mana flask (10%)
or equipment (45%: any weapon, armour or jewellery base with DropLevel ≤ the monster's level, minus event and league
bases), item level = the monster's level, normal, with the type's default sockets. `DropLoot` puts each as a WorldItem
([13](13-items.md) §5) 3–6 cells around the corpse, on a walkable cell when the walk map is known. `Game_DROPS=0` turns
it off.

## Open

- All-19 signature mechanics and native Labyrinth/Altar acquisition: [remaining dependencies](../game/phrecia-ascendancies.md#dependencies-of-all-19-classes).
  Choice/points/saves and initial server effects are implemented, but choosing a class does not implement its missing subsystems.
- The real rarity experience bonuses, the level 95+ penalty, party sharing.
- Quest passive-point rewards; ordinary tree connectivity and cluster/expansion nodes. Ascendancy connectivity is checked.
- Real drop pools (MonsterVarieties / area drop tables), item rarity and mods, quantity and rarity bonuses; ground
  items only exist on the connection that saw them drop.
