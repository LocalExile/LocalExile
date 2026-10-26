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
- **Known:** ordinary and Phrecia main choices, separate budgets, ascendancy connectivity and paid refunds work.
  [Ascendancy usage and limits](../docs/game/ascendancies.md).
- **Known:** ordinary allocations, masteries and refunds are checked against the character's available starts.
  Level trimming preserves connected branches; unsupported expansion/proxy nodes are refused.
- **Start:** quest refund/passive awards and native Labyrinth/Altar acquisition.
  Ascendancy-system work is claimed in [#151](https://github.com/Wrathen/LocalExile/issues/151).
  Check that issue and open pull requests before taking its native chooser work.
- **Done when:** normal progression grants the points and opens the native ascendancy chooser without dev commands.

### Main ascendancy mechanics

- **Known:** all 19 Phrecia alternatives have the shared choice, point and save lifecycle.
  Only an initial subset of their effects executes on the server.
  The proven combined-graph client experiment is separate from standard startup, which does not install it.
  Agree a client delivery mechanism before promoting the full Phrecia selection integration.
  Passive-granted ordinary auras, owner buffs, equipment conditions, life costs/reserve and nearby effects now execute.
  Ward absorption/restoration and timed Adrenaline also execute, including cross-area lifetime.
  Current Rage, mana-spending grants, supported hit grants and the attack multiplier now execute and are native-checked.
  Incorruptible's full chaos-armour flag also executes through the common incoming-hit stage and survives paid refunds.
  Pack Mentality now has owned wolves, actual attacks and owner damage buffs, with native limit/refund/area checks.
  Visiting Harbingers have actual companions, buff casts and owner effects, with explicit timing/immunity limits.
  Toughened Skin's body-only armour doubling and independent action/movement floors also execute.
  Whaletooth Bangle's final duration stat is native-checked; verify elapsed lifetimes when integrating summon consumers.
  The [current class table](../docs/game/ascendancies.md#what-this-does-not-complete) distinguishes those effects.
- **Start:** complete the missing signature subsystems in the
  [dependency order](../docs/game/phrecia-ascendancies.md#dependencies-of-all-19-classes), including special support execution,
  other minion families and inheritance, remaining Rage bonuses/costs, totems, shrines and passive-granted triggers.
  Juggernaut, endurance charges and stun are claimed in [#152](https://github.com/Wrathen/LocalExile/issues/152).
  Linked support foundations from [#207](https://github.com/Wrathen/LocalExile/pull/207) are merged.
  Support enhancement remains owned in issue #64; coordinate with its owner before expanding that work.
  Coordinate those dependencies before starting overlapping work.
- **Done when:** each class's defining effect has a reproducible gameplay check and survives refunds and reloads.
