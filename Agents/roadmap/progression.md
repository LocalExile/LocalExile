# Roadmap: Experience and the passive tree

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/progression.md](../status/progression.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 3. Gameplay

### Character progression: the rest of the Passive Skill Tree

- **Known:** experience, levels, S→C 0x33 and C→S 0x34 (allocate) work ([15](../docs/protocol/15-progression.md)).
- **Known:** gold respec at Bestel (C→S 0x36 / 0x37) and masteries (0x35) work.
- **Known:** refunds with refund points (0x36 flag 0; 0x33's first u16) work;
  Orbs of Regret (right-click, 0x58) add them.
- **Accuracy Mastery:** 3 of its 6 effects are modelled ([Status](../status/skills-and-combat.md),
  [11](../docs/protocol/11-skills.md) §4–5): Accuracy = Intelligence, +3 per Dexterity instead of +2, and +500 / -2
  per level.
  Not modelled: 40% increased Accuracy against Unique Enemies, 50% increased against Enemies in Close Range, and
  Minions' Accuracy equals yours (needs target rarity/distance and minions).
- **Start:** quest refund points, ascendancy points, quest points (0x33 +0x8a);
  the tree's connections (PassiveSkillGraph.psg) so the server checks them too.
- **Done when:** passives can be refunded with points from quests, and ascendancies chosen.
