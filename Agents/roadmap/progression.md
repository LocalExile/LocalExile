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
- **Accuracy Mastery:** all five effects currently offered by the target client's Accuracy group are modelled
  for the existing default-attack accuracy roll ([Status](../status/skills-and-combat.md),
  [11](../docs/protocol/11-skills.md) §4–5): Accuracy = Intelligence, +3 per Dexterity instead of +2, +500 / -2 per
  level, 40% more accuracy against Unique Enemies and 50% more against Enemies in Close Range (both at the attack's
  accuracy roll; close range is the constant 20-unit radius).
  Minions' Accuracy equals yours is not offered by that target group and remains unsupported with minion accuracy.
  Gem attacks still need their own accuracy rolls before these target conditions can affect them.
- **Known:** pinned ordinary/Phrecia topology is available as
  [read-only analysis artifacts](../docs/dev/passive-graph-data.md), without installing a client graph.
- **Known:** command selection, saved identities, separate point awards, paid refunds and ordinary/ascendancy
  graph connectivity now have automated coverage; see [scope and limitations](../docs/game/ascendancies.md).
  Ordinary command selection, refunds, relog and a Gateway restart are verified with a native Duelist/Slayer smoke.
- **Known:** Antiquarian's complete supported node consumers and three legal builds have native acceptance (#98).
  Whaletooth scales actual Enduring Cry recovery; paid refunds, transient effects and durable class progress pass.
  See [Status](../status/progression.md) and the [model/evidence audit](../docs/game/antiquarian.md).
- **Start:** quest passive/refund rewards and the native Labyrinth/Altar flow (#151).
  Other Phrecia classes retain their default gate; Antiquarian requires its exact verified installed graph opt-in.
  Do not add automatic archive writes or require the alternate graph for ordinary characters.
- **Known:** Forest Tracking derives attack damage from complete groups of final evasion (#243).
  It composes with Steel Willed; allocation/refund/reload, Iron Reflexes and supported damage have automated coverage.
  Other Daughter of Oshabi mechanics and Phrecia client admission remain separate work.
- **Known:** family gem levels for Herald, Vaal, Link and Raise Zombie reach the common effective skill level.
  Connected allocation/refund/reload has automated coverage (#244).
  Family-specific execution engines, global gem bonuses and Phrecia client admission remain separate work.
- **Done when:** quest rewards persist and the native Altar can choose an ordinary ascendancy, with real-client checks.
  Implementing all 19 Phrecia classes' special gameplay is separate from this selection lifecycle.
