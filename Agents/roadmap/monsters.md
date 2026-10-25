# Roadmap: Monsters

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/monsters.md](../status/monsters.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 2. The world

### Monsters: beyond `/spawn_mob`

- **Known:** spawning, levels, rarity, names, team, life, StateMachine / Transitionable types, walking, chasing and
  melee attacks all work ([10](../docs/protocol/10-chat-and-commands.md) §5, [11](../docs/protocol/11-skills.md) §6);
  areas are populated with their MonsterPacks and unique boss ([14](../docs/protocol/14-monster-packs.md)).
- **Start:** the "emerge" action for emerge monsters (then add the skipped emerge packs), mods for magic/rare monsters
  (their life bonus is done), stages of other bosses (`BossFights.cs` has Hillock's), pack `Grounds`, the 346
  varieties with NPC / ControlZone / BlightTower … components (each needs its deserializer, schema.md).
  Check every new component with `scripts/re/frida/check-components.js`.
- **Done when:** monsters in areas match the official game's (mods, rarity bonuses, emerge, boss bar).

## 4. Server design

- **Monster movement on the client's pathfinder:** monsters still use the fitted `WalkEstimator`.
  `ClientPathfinder` already paths them exactly as the client does;
  still missing are the client's monster controller (its own re-paths, frozen far monsters), the speed variance and
  the 5–50 ms until the client applies an order ([07](../docs/protocol/07-latency-visibility-movement.md) §5, "Open").
