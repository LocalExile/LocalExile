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
- Passive masteries: S→C 0x33 carries selected effects (`node | effect << 16`) and C→S 0x35 selects the effect HASH16;
  a mastery node can be allocated normally and its effect selected/changed without spending another point.
  Block Mastery's four effects are implemented: max attack block +2, max spell block +2, 20 life + 20 mana on block,
  and +1% spell block per 5% attack block; the effects are saved with the character.
  Code: `PassiveTree.cs`, `CharacterSaves.cs`, Gateway (`ChangePassives`, `PassivesMessage`).
  Docs: [15](../docs/protocol/15-progression.md) §2.

## Partly working

Nothing yet.

## Not working yet

- Saving quest progress.
  Quest passive points, quest refund points and ascendancy ([15](../docs/protocol/15-progression.md) "Open").
