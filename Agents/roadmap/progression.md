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
- **Known:** ordinary and Phrecia main choices, separate budgets, ascendancy connectivity and paid refunds work.
  [Ascendancy usage and limits](../docs/game/ascendancies.md).
- **Start:** quest refund/passive awards, native Labyrinth/Altar acquisition and ordinary-tree connectivity.
- **Done when:** normal progression grants the points and opens the native ascendancy chooser without dev commands.

### Main ascendancy mechanics

- **Known:** all 19 Phrecia alternatives have the shared choice, point and save lifecycle.
  Only an initial subset of their effects executes on the server.
  The [current class table](../docs/game/ascendancies.md#what-this-does-not-complete) distinguishes those effects.
- **Start:** complete the missing signature subsystems in the
  [dependency order](../docs/game/phrecia-ascendancies.md#dependencies-of-all-19-classes), including support gems,
  minion ownership/AI, Rage, Ward, totems, shrines and passive-granted skills.
- **Done when:** each class's defining effect has a reproducible gameplay check and survives refunds and reloads.
