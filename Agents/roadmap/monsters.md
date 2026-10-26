# Roadmap: Monsters

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/monsters.md](../status/monsters.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 2. The world

### Monsters: beyond `/spawn_mob`

- **Known:** spawning, levels, rarity, names, team, life, StateMachine / Transitionable types, walking, chasing and
  melee attacks all work ([10](../docs/protocol/10-chat-and-commands.md) §5, [11](../docs/protocol/11-skills.md) §6);
  areas are populated with their MonsterPacks and unique boss ([14](../docs/protocol/14-monster-packs.md));
  emerge monsters rise near the player ([10](../docs/protocol/10-chat-and-commands.md) §5).
- **Start:** the systems the 74 unsupported monster mods need (auras on allies, retaliation and on-death skills, ailments and
  curses on the player; [14](../docs/protocol/14-monster-packs.md) §5), stages of other bosses (`BossFights.cs` has Hillock's), pack
  `Grounds`, the 346 varieties with NPC / ControlZone / BlightTower … components (each needs its deserializer, schema.md).
  Emerge monsters: the official emerge distance and whether a pack rises together are unknown (the server's `.ais`
  files); types whose Transitionable lists more states (SpiderBoss: 3) are still created risen.
  Check every new component with `scripts/re/frida/check-components.js`.
- **Done when:** monsters in areas match the official game's (mods, rarity bonuses, emerge, boss bar).

### Owned combat summons

- **Known:** Pack Mentality's wolves have actual ownership, friendly monster objects, combat AI and owner buffs.
  They use the minion life/damage columns and are excluded from ordinary instance monster snapshots.
  The three Visiting Harbingers now have separate native summon/buff cycles and owner-effect consumers.
  Inherited Life corpse flags, the true native default and Player-component veto now have static proof and tests.
  Six native scenarios verify the creation flags and predicate; Zombie/Spider types retain explicit false eligibility.
  [Verified data and limits](../docs/game/owned-summons.md).
- **Start:** reuse the roster for the other Phrecia summon families, adding each source's exact behaviour and stats.
  `OwnedSummonCatalog` now resolves Zombie variants and Broodguard's Spider metadata; actor/trigger execution is open.
  Build authoritative final-death registration and one-use corpse reservation/consumption before real Raise Zombie.
  Preserve zero-Life, type, spectre and owned restrictions; dead snapshot restoration also needs Actor's death bytes.
  Complete Harbinger warp/ally application and shared action-speed/immunity consumers.
  This work is claimed in [#231](https://github.com/Wrathen/LocalExile/issues/231).
- **Done when:** the granted summon families and their modifiers execute with source removal and native checks.

## 4. Server design

- **Monster movement on the client's pathfinder:** monsters still use the fitted `WalkEstimator`.
  `ClientPathfinder` already paths them exactly as the client does;
  still missing are the client's monster controller (its own re-paths, frozen far monsters), the speed variance and
  the 5–50 ms until the client applies an order ([07](../docs/protocol/07-latency-visibility-movement.md) §5, "Open").
