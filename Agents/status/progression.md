# Status: Experience and the passive tree

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/progression.md](../roadmap/progression.md).

## Works (verified with the real client)

- Experience and levels: kills within 150 cells give DefaultMonsterStats experience × the variety's multiplier ×
  rarity, with the level penalty (S→C 0x2ba Player experience; the client derives the level);
  a new level sends its stats, full life and mana, the passive points and the account's level;
  experience is saved per character; `/add_xp`.
  Code: `Progression.cs`, Gateway (`GainExperience`, `ApplyLevel`).
  Docs: [15](../docs/protocol/15-progression.md) §1.
- Passive Skill Tree: S→C 0x33 (level, class, allocated nodes, passive and refund points;
  the client counts level − 1 points), C→S 0x34 allocates a node, checked, applied to the stats (attributes, life,
  mana, resistances and more) and saved; C→S 0x36 with flag 0 refunds a node for a saved refund point (checked,
  mastery effects removed); using an Orb of Regret (right-click, C→S 0x58) takes one item and adds one point;
  a Book of Skill (right-click, C→S 0x58; every quest book of that name) is used up and gives one more passive skill
  point, both saved together; a Book of Regrets gives two refund points the same way;
  `/passives`, `/add_refund_points`, `/reset_passives`.
  Code: `PassiveTree.cs`, `CharacterSaves.cs`, Gateway (`ChangePassives`, `UseItem`).
  Docs: [15](../docs/protocol/15-progression.md) §2.
- Ordinary-tree connectivity is checked on allocation, mastery selection, refunds, family changes and loading.
  Each branch needs the character's start or an allocated Ascendant Path grant.
  Masteries cannot connect separate branches; unsupported proxy/expansion nodes are refused.
  Refused changes spend no gold or refund points, and invalid saves are retained.
  Lowering a level removes safely refundable leaves, including after reverse-order allocation batches.
  Client checks cover normal Elementalist, Phrecia Herald and Ascendant, including relog and full restart.
  Code: `OrdinaryPassiveRules.cs`, Gateway (`ChangePassives`, `LoadProgress`, `ApplyLevel`).
  Docs: [15](../docs/protocol/15-progression.md#ordinary-tree-connectivity),
  [reproducible checks](../docs/game/ascendancies.md#ordinary-connectivity-and-ascendant-roots).
- Passive masteries: S→C 0x33 carries selected effects (`node | effect << 16`) and C→S 0x35 selects the effect HASH16;
  a mastery node can be allocated normally and its effect selected/changed without spending another point.
  Block Mastery's four effects are implemented: max attack block +2, max spell block +2, 20 life + 20 mana on block,
  and +1% spell block per 5% attack block; the effects are saved with the character.
  Code: `PassiveTree.cs`, `CharacterSaves.cs`, Gateway (`ChangePassives`, `PassivesMessage`).
  Docs: [15](../docs/protocol/15-progression.md) §2.

## Partly working

- Main ascendancies: `/ascendancy` offers one ordinary or Phrecia specialisation for the character's base class.
  The catalog contains 21 ordinary and 19 Phrecia choices in the same ordinary league and account stash.
  Selection, separate point awards, class-owned connected nodes, exclusive choices and paid refunds are saved atomically.
  Old unascended saves retain their fields; stale passive snapshots cannot overwrite newer progress.
  The merged client graph preserves the ordinary tree.
  Real-client checks cover Slayer and Aristocrat, native mouse allocation, five-point refunds and restart persistence.
  Initial server effects include Aristocrat's points/gem properties and Gambler's hit rules.
  Native checks also cover Paladin/Arakaali granted auras, Ghost of the Deep, Harbinger ES and Architect life costs/reserve.
  Nearby Paladin/Bog Shaman/Ancestral enemy effects have native buff and production damage-path checks.
  Oath equipment conditions, corrupted equipment and Sanguine Power have real-data Gateway integration checks.
  Antiquarian Ward conversion/restoration and timed Adrenaline have native hit/refund/area-transfer checks.
  The unique mechanics of most classes and native Labyrinth/Altar acquisition remain incomplete.
  Code: `AscendancyCatalog.cs`, `AscendancyProgress.cs`, `CharacterSaves.cs`, `Program.Progression.cs`.
  Docs: [usage, implemented effects and limits](../docs/game/ascendancies.md),
  [native evidence](../docs/re/ascendancy-client-3.29.3b.md).

## Not working yet

- Quest passive/refund-point awards and native Labyrinth completion rewards.
  Ascendancy development commands supply idempotent awards; quest and Altar flows remain separate work.
  Docs: [15](../docs/protocol/15-progression.md) "Open".
