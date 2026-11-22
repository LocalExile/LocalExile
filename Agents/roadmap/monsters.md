# Roadmap: Monsters

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/monsters.md](../status/monsters.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 2. The world

### Monsters: beyond `/spawn_mob`

- **Known:** spawning, levels, rarity, names, team, life, StateMachine / Transitionable types, walking, chasing and
  melee attacks all work ([10](../docs/protocol/10-chat-and-commands.md) §5, [11](../docs/protocol/11-skills.md) §6);
  areas are populated with their MonsterPacks and unique boss ([14](../docs/protocol/14-monster-packs.md));
  Twilight Strand has a provisional 45-monster calibration cap; complete official population and placement rules
  remain unresolved because the reference capture misses its opening.
  emerge monsters rise near the player ([10](../docs/protocol/10-chat-and-commands.md) §5).
  Act 2's Oversoul has its campaign attack set and altar/exit lifecycle; official decision weights/cooldowns still need
  a reference capture ([status](../status/monsters.md#partly-working)).
  Act 10 has three heart intervals and the allied finale; calibrate HP thresholds, AI weights and hazard geometry.
  Act 5 Kitava has the body/heart and rescue sequence; calibrate phase thresholds and AI weights against a reference.
  Arakaali has the Silk/stance transition and linked turret rails; corner travel and laser/HP calibration remain.
  Yugul has mirror intervals, pool chill, Fearful orbs and turret volleys; calibrate Fearful magnitudes and orb AI.
  Solaris/Lunaris have celestial fire/cold hazards; calibrate original handoff timing, target distribution and radii.
  Normal Sirus has its laboratory and four-phase controller; calibrate official decision weights and reward chances.
  Hits can make monsters flee ([11](../docs/protocol/11-skills.md) §6); a live check should settle the flee distance
  and whether the real server walks a fleeing monster with Monster.ot's `Flee` action instead of Move.
  Inherited Life corpse/sinking flags and the source eligibility predicate are implemented for ordinary creation
  ([status](../status/monsters.md#partly-working)); integrated native evidence is scoped in
  [04](../docs/protocol/04-player-entity.md#44-life-0x141e72890-55-bytes).
  Check template and variety life regeneration on the client, including the Sanguine Wave and Mammothcage.
  Check the life and physical damage totems' caster and recipient effects in the graphical client, including overlaps
  and the allies-only unique map totem. Verify the official aura sampling and attack timing during a range crossing.
  Native TakeHit recovery stops locomotion and invalidates ordinary pending attacks for the same recovery interval.
  AI decisions now use 50 ms game-time steps with 100 ms bounded repaths and full-duration attack busy periods.
  Historical LocalExile controls verify chase response, turning, pacing and native pause; this port awaits a rerun.
  Automated cadence and lifecycle checks retain interruption and stage-completion guards.
  Ordinary Melee follows resolved native clip clocks, including slow spans and published action-speed changes.
- **Start:** the systems the 74 unsupported monster mods need (auras on allies, retaliation and on-death skills, ailments and
  curses on the player; [14](../docs/protocol/14-monster-packs.md) §5), stages of other bosses (`BossFights.cs` has Hillock's
  and `KingInMists.cs` the two-form Ritual pinnacle), verify mixed-ground pack boundaries and ground priority for
  authored encounters, verify zero-range encounter points, variable choices when a pack has only fixed entries,
  and how explicit rooms combine with area weights. Check boss life and secondary hits in a desktop Strand fight
  with boss modifiers and after re-entry, including a party runner change. Remaining work includes varieties with BlightTower / Mercenary … components
  (each needs its deserializer, schema.md; NPC, InteractionAction and Timer are written), the bandit lords' help-or-kill dialogue, bosses' later
  forms (Abberath2, Shakari2, Malachai's second form) created by their fight, tile spawner bosses
  (Marceus).
  Emerge monsters: the official emerge distance and whether a pack rises together are unknown (the server's `.ais`
  files); types whose Transitionable lists more states (SpiderBoss: 3) are still created risen.
  Corpse consumers still need authoritative final-death registration and identity; one-use consumption exists only as
  Deathrattle's `SpawnedMonster.CorpseConsumed` flag, with no reservation shared between consumers.
  Preserve zero-Life, type, spectre and owned restrictions; dead snapshots also need Actor's conditional death bytes.
  Check every new component with `scripts/re/frida/check-components.js`.
  Extend contact timing to off-hand/special actions only after resolving their actual AO, animation category and
  selection rules; test fast/natural/slow/capped periods and mid-swing action-speed changes in both network modes.
  Ambiguous addon resources and unsupported animation scripts need explicit extraction before opting in
  ([11](../docs/protocol/11-skills.md) §6).
- **Done when:** monsters in areas match the official game's (mods, rarity bonuses, emerge, boss bar).

## 4. Server design

- **The client's monster controller:** monsters walk the client's own path (`PlayerWalk`, `ClientPathfinder`).
  The decision loop now uses 50 ms game time and repaths no faster than 100 ms after a three-cell target change.
  Native response, turning, attack pacing and pause controls pass; official monster controller rules remain separate.
  The estimate walks at the client's rolled speed (the status's monster walk speed entry).
  Still missing are the client's monster controller (its own re-paths, frozen far monsters) and the 5–50 ms until the
  client applies an order ([07](../docs/protocol/07-latency-visibility-movement.md) §5, "Open").
