# Status: Experience and the passive tree

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/progression.md](../roadmap/progression.md).

## Works (verified with the real client)

- Experience and levels from kills, with the level penalty and party share.
  Docs: [15](../docs/protocol/15-progression.md) §1.
- Passive Skill Tree: allocation, refunds, Orbs of Regret, Books of Skill and Regrets.
  Docs: [15](../docs/protocol/15-progression.md) §2.
- Passive masteries, with Block, Fire and Wand Mastery effects. Docs: [15](../docs/protocol/15-progression.md) §2.
- A passive node's conditional stats (`..._while_dual_wielding`, `..._if_..._recently`) count only while their
  condition holds, on the same conditions items use, and an attack's hit and its damaging ailments take the tree's
  "with &lt;weapon type&gt;" rows (mace, axe, sword, claw, dagger, staff, wand). Resolute Technique (hits cannot be
  evaded, never crit) and Precise Technique (40% more Attack Damage above Maximum Life Accuracy, never crit) also work,
  as does the "critical strikes do not deal extra Damage" part of Elemental Overload and Perfect Agony. Unwavering
  Stance (cannot evade, cannot be stunned) and Arrow Dancing (evasion doubled against projectile attacks, 25% less
  against melee attacks) change the rating an enemy attack rolls against. Magebane removes Dexterity's inherent evasion
  bonus and grants 1% spell suppression per 15 Dexterity; Pain Attunement grants 30% more Spell Damage on low life.
  Elemental Overload's window grants 40% more Elemental Damage with hits and ailments, its critical strikes deal no
  extra Damage and its ailments are never from critical strikes; the skill-icon diamond is sent and expires with the
  window. Perfect Agony makes the ailment DoT multiplier the critical strike multiplier and only critical strikes
  inflict ailments (damaging and non-damaging); with Elemental Overload no hit inflicts one. Lethe Shade doubles the
  duration of ailments on you (reflected ones excepted) and halves DoT taken for a second after a damaging ailment
  starts. Vaal Pact makes melee life leech instant and blocks life recovery other than leech.
  Docs: [15](../docs/protocol/15-progression.md) §2.
- Passive tree jewel sockets, percentage attribute mods, The Adorned and Split Personality's allocated paths,
  including nested clusters (tree art check pending).
  Docs: [15](../docs/protocol/15-progression.md) §2a, [unique jewels](../docs/game/unique-jewels.md).
- Cluster jewels: a new one has its theme, passive count and sockets, its small passives grant the theme and its
  notables roll for it; the single-keystone smalls' keystone stats, nesting (including smaller clusters in larger
  sockets), unidentified/empty-jewel refusal, block bases, refund-point sync and the client's layout; keystones,
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
- Descendancy (the Bloodline and Wildwood classes) and the Wildwood Primalist's nodes; the charm modifiers chain
  projectiles off terrain, add physical damage after a recent critical strike and explode bleeding kills (phasing on
  kill waits for the client to be told it phases); Primal Wisps found waits for the Wildwood Wisp system. Docs: [15](../docs/protocol/15-progression.md) §2,
  [16](../docs/protocol/16-waypoints.md) §3, [05](../docs/protocol/05-inventories-and-ui.md).
- Elementalist Shaper of Flames. Docs: [20](../docs/protocol/20-damage.md) §3.
- Antiquarian. Docs: [Antiquarian](../docs/game/antiquarian.md).
- Juggernaut. Docs: [Juggernaut](../docs/game/juggernaut.md).
- Slayer, except Impact's melee range. Docs: [Slayer](../docs/game/slayer.md).
- Gambler. Docs: [Gambler](../docs/game/gambler.md).

- Forbidden Flame, Forbidden Flesh and Bound By Destiny support explicit rates at their respective encounters.

## Partly working

- Dual Wielding Mastery's different-weapon-types damage. Docs: [20](../docs/protocol/20-damage.md) §2b.
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
- Tattoos: 91 rows work in the client and 7 are partial (the seven Forbidden ascendancy tattoos, whose client use
  flow is untraced); two are inert. Replace, tattoo persistence across refund/reallocate and the 50-tattoo cap are
  client-verified (the 51st tattoo is refused). The vendor recipe works (three same-tribe tattoos for one weighted
  random tattoo of that tribe; not live-checked).
  Docs: [tattoos](../docs/game/tattoos.md).
