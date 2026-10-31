# The King in the Mists

The Ritual pinnacle boss of the Crux of Nothingness (`WorldAreas AzmeriLeagueBoss`, area level 83). This page records
the fight's data and mechanics, what the Gateway models, and what is still open. It is the reference for
`src/GameServer.Gateway/KingInMists.cs`.

## Entry

`An Audience With The King` (`Metadata/Items/MapFragments/RitualBossFragment`, drop level 81) is a map fragment. Used
alone in the map device it opens the Crux of Nothingness; it is consumed like a map. The fragment is a guaranteed reward
of Nameless Rituals and a rare reward of other Rituals (poewiki, "An Audience With The King"). The server has no Ritual
encounter yet, so `/item An Audience With The King` is the development way to obtain one.

The area is a unique map area (`IsUniqueMapArea`), has no connections, and its `WorldAreas.Bosses` row is
`MonsterVarieties 10150` (the humanoid King). The arena's `.arm` room (`Metadata/Terrain/Leagues/Azmeri/TreantArena/Rooms/arena.arm`)
has a `mapboss` point at (162, 174) and a central ritual pillar at (162, 162); the three Mystic Fetish platforms are the
raised areas around the central bowl.

## Forms and statistics

The fight uses two MonsterVarieties rows. Both have the `BossHealthBar` flag and the map-boss mods
`MonsterMapBoss` (4134), `CannotBeSlowedBelowValueBosses` (4686), `StunRepeatImmunityMapBossSuper` (23585) and
`ImmuneToKnockback` (7475); the humanoid also has `MonsterNoDropsOrExperience` (6883), because only the treant's death
pays out.

| | Humanoid `VoodooKingBoss2` | Treant `VoodooKingBoss3` |
|---|---|---|
| MonsterVarieties row | 10150 | not in the area's Bosses row; spawned by the fight |
| Life multiplier | 1800% | 1050% |
| Energy Shield from Life | 100% | 60% |
| Damage multiplier | 210% | 255% |
| Attack time | 1.5 s (data row); poedb level 83 display 1.005 s | 1.005 s (data row); poedb display 0.67335 s |
| Reach | 4–15 | 4–16 |
| Accuracy | 100% | 100% |
| Crit | +5%, +130% multiplier | +5%, +130% multiplier |
| Resistances | 50% fire / 50% cold / 50% lightning / 30% chaos | same |
| Tags | human, humanoid, immobile, red_blood | demon, humanoid, large_model, very_slow_movement, Ritual_Boss_Divination_Card |

### Life at level 83

`MonsterMapBossDifficulty` level 83 adds 1864% life and `MonsterMapDifficulty` adds 109%; the combined multiplier is
`1 + 18.64 + 1.09 = 20.73`. With `DefaultMonsterStats` level 83 `MonsterLife` 15,319 and the unique rarity's
`monster_life_+%_final_from_rarity` 698% (×7.98):

- Humanoid: `15,319 × 18 × 7.98 × 20.73 = 45,614,731` (poedb's displayed value).
- Treant: `15,319 × 10.5 × 7.98 × 20.73 = 26,608,593`.

The map-boss damage multiplier at level 83 is `1 + 0 + 0.15 = 1.15`. The poedb level-83 display gives the humanoid
2,154 damage and 3,214 spell damage; these include map/rarity scaling and are a useful cross-check, not a replacement
for the server's `MonsterCatalog` pipeline.

The energy shield pools are the pre-rarity base-life shares shown by poedb: 275,742 for the humanoid (100% of
15,319 × 18) and 96,510 for the treant (60% of 15,319 × 10.5). The Gateway does not model monster energy shield yet, so
these pools are documented but not applied.

## Phase flow

The encounter has three states, and the phase change is **life-based**, not altar-based:

1. **Phase 1 — humanoid, 100% → 30% life.** The King fights in the circular arena, uses his phase-1 skills, and
   periodically teleports to one of the three Mystic Fetishes on the raised platforms. Each activation adds a Portent
   Vapors stack and starts that fetish's arena hazard. The three fetishes can all be activated, but they do not end the
   phase by themselves.
2. **At 30% life — the maze.** The humanoid stops taking damage at 30% (`TakeDamageLocked` floors it there). He raises
   and slams his staff, opens the maze, and teleports the player to the hedge maze (phase 2). This is the only phase
   trigger: the old "after the third fetish" transition was wrong and showed phase 3 at ~80% life.
3. **Phase 2 — the maze.** The player follows the Sacred Wisps through the hedge maze to the purple portal back to the
   arena. Dying or logging out during the maze is the wiki's bypass; the next entry is phase 3.
4. **Phase 3 — the treant.** Back in the arena, the humanoid body disintegrates at the central shrine and the treant
   emerges. The treant has a short emergence grace (3 s: no action, no damage) and then fights with its own skill list.

The `arena_01.dgr` graph places the arena and the `_maze.dgr` hedge maze in the **same area** (`AzmeriLeagueBoss`,
seed 1). The maze entrances are `maze_entrance.arm` rooms (a `telepad` point at room-local `(34, 34)`), the exit is
`maze_exit.arm` (room-local portal at `(80, 80)`), and the exit room is at world `(828, 460)` with orientation code 4 in
seed 1. The Gateway uses `ServerLevels` and `AreaPopulation.LocalToWorld` to find them.

## Phase 1 — the humanoid

The King stands at the central arena. The fight begins when the player comes within his 135-cell aggro range. His
GrantedEffects list (`MonsterVarieties 10150`) is:

| Skill | GrantedEffects | Data |
|---|---|---|
| Default attack | 420 `MeleeAtAnimationSpeedUnique` | 3420–5130 base at level 83, 1.5 s, reach 15 |
| `MPSVoodooKing2SpellProjectile` | 3893 | 1082–1623 physical + 360.7–541 chaos, 1 s cast, projectile variation 245 (`AzmeriVoodooKingSpellProj`, 800 units/s, −40% maximum action distance) |
| `GTVoodooKingVineCascade` | 10346 | trigger, 10 s cooldown |
| `GAVoodooKingVineCascadeSmall` | 7595 | 110% attack, +10% more, 25% physical→chaos, area |
| `GAVoodooKingVineCascadeBig` | 7596 | 140% attack, +40% more, 25% physical→chaos, area |
| `GAVoodooKingVineSlam` | 7597 | 190% attack, +90% more, 30% physical→chaos, area, 15 s cooldown |
| `EASVoodooKingSummonEffigy` | 8336 | 25 s cooldown |
| `EASVoodooKingHexNoMovementAllowed` | 8337 | 25 s cooldown; Ritual of Meditation |
| `EASVoodooKingHexNoStandingAllowed` | 8338 | 25 s cooldown; Ritual of Dance |
| `EASVoodooKingBecomeATree` | 8340 | phase 3 transformation |
| `EASVoodooKingMazePhase` | 8341 | maze teleport; 277.8 physical/s + 69.5 chaos/s while in the maze |
| `EASVoodooKingReturnFromEdge` | 8343 | return from the maze |
| `VoodooKingTeleport` | 12419 | 8 s cooldown, +15 cells additional teleport distance |
| `VoodooKingTeleportFlee` | 12420 | 3 s cooldown, faces away |
| `VoodooKingDashForward/Backward/Left/Right` | 12422–12425 | short repositioning |
| `VoodooKingPunishment` | 12437 | 1333–2000 physical, cannot be blocked, dodged or suppressed |
| `GSVoodooKingRitual` | 7568 | channelled, cannot be stunned or knocked back |

Wiki mechanics:

- **Vine Cascade** — physical/chaos tendrils.
- **Vine Nova / Vine Slam** — raises the staff, slams, creates an Ice Nova that chills; the data row is the area attack
  above.
- **Teleport / Mystic Fetish** — teleports to one of the three platforms and activates its fetish. Each activation gives
  the player a stack of **Portent Vapors** (increased damage taken) and starts an arena hazard:
  - Exploding Toads: a Viscous Swamp spawns purple toads that hop at the player and explode for chaos damage.
  - Blood Ritual: red boiling-blood patches deal physical damage over time.
  - Caustic Ground: small green patches.
  - Bugrend: the central altar fires slow purple Soulrend-like projectiles for physical damage.
- **Summon Plagued Rats** — rats join the fight.
- **All three fetishes** — the King says "Embrace nothingness!" / "The darkness, exultant!". This is the last fetish's
  line; the maze itself starts at 30% life (see "Phase flow").
- **Summon Effigy** — a dark effigy that applies **Mystic Miasma** (cold/chaos damage over time) near the staff.
- **Ritual of Dance** — 8 s debuff: standing still imprisons the player, then applies Maim.
- **Ritual of Meditation** — 8 s debuff: moving imprisons the player, then applies Maim.

## Phase 2 — the maze

The player is teleported to a large hedge maze and gains **Branching Affliction**, a stacking physical/chaos damage over
time. The Sacred Wisps show the route to the large purple portal back to the arena. Dying or logging out skips the maze;
the next entry starts at phase 3.

The arena graph contains the maze as the `_maze.dgr` subgraph, so it is the **same `AzmeriLeagueBoss` area**, not a
separate map. Its rooms are placed east of the arena: in seed 1 the four `maze_entrance.arm` rooms are at
`(644, 736)`, `(644, 276)`, `(1104, 276)` and `(1104, 736)`, and `maze_exit.arm` is at `(828, 460)` (161 × 161 cells).
Each entrance has a `telepad` point, and the exit has the `PortalToggleableReverseVoodooKing` object with the
`AzmeriVoodooPortal.ao` model at room-local `(80, 80)`.

Gateway implementation:

- `RunMaze` plays the staff slam and the maze-opening animation, teleports the player to the nearest entrance's
  `telepad`, applies Branching Affliction for the maze duration, spawns the exit portal effect and a trail of wisp
  effects along a `ClientPathfinder` route through the maze, and moves the wisps toward the exit every 0.4 s.
- When the player is within five cells of the exit portal (or after 120 s), the server teleports them back to the arena's
  `default` point, removes the wisps/portal, puts the humanoid on the mapboss point and starts the transformation.
- The portal is a visual effect with a proximity return; the portal's click interaction and the full `PortalTransition`
  object lifecycle are not modelled yet. Branching Affliction is an icon/timer here, not damage over time.
- Dying or logging out currently stops the maze sequence without advancing to phase 3; the next entry behavior for a
  half-finished maze is still open.

## Phase 3 — the treant

When the player returns through the maze portal, the King is back at the central shrine (mapboss point). The humanoid
plays the staff slam, its body disintegrates, and the treant form emerges. The treant has a **3 s emergence grace**:
`KingGrace` refuses all damage and the controller performs no action for those three seconds. After that it fights
normally, and the three platforms are covered in purple fire. Its GrantedEffects list (`VoodooKingBoss3`) is:

| Skill | GrantedEffects | Data |
|---|---|---|
| Default attack | 420 | 1723–2584 base at level 68; scale with the level-83 monster table |
| `GPSVoodooKingBugBomb` | 1898 | 802.9–1204 physical + 267.6–401.5 chaos, 1 projectile, 20 s cooldown |
| `MPSVoodooKing3SpellProjectile` | 3894 | 231.1–346.6 physical + 231.1–346.6 chaos, projectile variation 249 (`AzmeriVoodooKing3SpellProj`, 800 units/s, −40% maximum action distance) |
| `GAVoodooKingVineSlam` | 7597 | 190% attack, 30% physical→chaos, area, 15 s cooldown |
| `GSVoodooKingBugBombImpact` | 7608 | 1620–2430 physical, area |
| `EASVoodooKingHexNoMovementAllowed` | 8337 | 25 s cooldown |
| `EASVoodooKingHexNoStandingAllowed` | 8338 | 25 s cooldown |
| `EASVoodooKingSummonDeathAuraEffigy` | 8342 | 15 s cooldown; effigy applies Mystic Miasma |
| `GTVoodooKingSwarmPulse` | 10354 | trigger pulse |
| `VoodooKingBugRend` | 10510 | 551.8 physical/s, 1 s, +6 projectiles in a circle, −30% area |
| `VoodooKingBugRendSingle` | 10511 | 525.5 physical/s, 1 s, 1 projectile |
| `VoodooKingPunishment` | 12437 | 620.7–931 physical, cannot be blocked, dodged or suppressed |

Wiki mechanics:

- **Treant Slam** — the treant's area slam.
- **Bugrend** — slow purple projectiles from the central altar.
- **Bug Bomb** — a large purple orb that deals physical/chaos damage and applies **Shadow Surge**, an accelerating
  physical damage over time.
- **Summon Effigy**, **Ritual of Dance**, **Ritual of Meditation**, **Exploding Toads**, **Blood Ritual** (physical/fire
  now), **Caustic Ground**.
- **Mystic Miasma** — standing near the King while his staff is raised deals cold/chaos damage over time.

## Loot

The King always drops one of four uniques (poewiki crowdsourced 3.26 shares, n=576):

| Unique | Share |
|---|---|
| The Untouched Soul | ~40% |
| Pragmatism | ~35% |
| The Light of Meaning | 20% |
| The Burden of Shadows | ~5% |

`That Which Was Taken` (`Metadata/Items/Jewels/JewelStr`, Crimson Jewel, limited to 1) is not a separate weight in
that table. It is the Affliction predecessor of The Light of Meaning; in this server it falls together with The Light of
Meaning whenever that unique is rolled. It rolls four random charm modifiers from `Mods.dat` Domain 35 (one per family),
ignoring attribute alignment, as the wiki describes; the item remains modifiable/divinable.

He can also drop `Hextoad Support` (~6%), `Hexpass Support` (~10%) and `The Endless Darkness` divination card. Defeating
him has a 50% chance to drop a high-value corpse item (Perfect/Regular/Imperfect at 20%/30%/50%), restricted to corpse
items worth 1000+ Tribute; corpse-item support is not implemented. The Unlight Altar spawns after the kill and grants or
changes the character's Bloodline Ascendancy to the Nameless Bloodline; the server has no Bloodline system yet.

The item system already has all four unique definitions and both support gems, so the server rolls the four uniques and
the two gems/card directly (`ItemDrops.KingInMists`). Corpse items and the altar are documented as open.

## Gateway implementation

`KingInMists.cs`:

- Recognises both forms and gives the humanoid/treant their whole GrantedEffect skill list in the actor record, so each
  `0x1ab` plays the client's own skill animation. Bugrend uses the treant's `MPSVoodooKing3SpellProjectile` (3894)
  rather than `VoodooKingBugRend` (10510): the client's `monster_soulrend` action selects the `SpellProjectileChaos`
  animation, which `VoodooKingBoss3.act` does not carry (live `PlayAnimation failed`); the server keeps Bugrend's damage
  and projectile profile.
- Skips both forms in the ordinary `PlanMonsterAi` loop; the controller serializes one action at a time.
- Phase 1: creates the three `VoodooKingBoss2RitualPillar` Mystic Fetishes at the `arena.arm` object points
  (mapboss-relative `(94, 122)`, `(230, 123)` and `(160, 238)`; world `(232, 490)`, `(368, 491)` and `(298, 606)` at
  seed 1), teleports next to one on a 10-second cadence, ends the teleport action, and sets the fetish's StateMachine
  `active` state to 1. It adds Portent Vapors stacks (10% increased damage taken per stack, applied server-side to the
  King's hits), summons Plagued Rats and one effigy, uses Vine Slam and Vine Cascade, and fires its spell projectile
  every 4 s at range. The humanoid is immobile (`MonsterVarieties.MovementSpeed` 0), so it does not walk. The central
  `HugeRitualShrine01_01.ao` is the room's own static model; Bugrend still launches from the King rather than from the
  central altar.
- At 30% life (`TakeDamageLocked` floors the humanoid there): plays the staff slam and the maze-opening animation,
  teleports the player to the nearest `maze_entrance.arm` telepad, applies Branching Affliction, spawns the exit portal
  effect and the wisp trail, and auto-returns the player when they reach the exit (see Phase 2 above).
- On the maze return: teleports the humanoid back to the mapboss, plays the staff slam and the treant transformation,
  removes the humanoid, spawns `VoodooKingBoss3` with `KingGrace` (3 s, no damage/actions) and starts phase 3.
- Phase 3: Treant Slam, Bug Bomb (lobbed projectile with a 9-cell landing area), Bugrend (projectile from the central
  altar, at any distance), Summon Effigy (with its cast animation) and the default attack. The treant walks after a
  player that kites it (`Move` 0x2909) instead of standing at the altar between casts. Before a cast, the controller
  aligns the client model to the server's walk cell (`0x1b7`), so the projectile origin is where the model is shown.
- `ItemDrops.KingInMists` rolls the guaranteed unique, Hextoad, Hexpass and The Endless Darkness.

Open: monster energy shield, ground hazards (toads, blood, caustic, swamp), the Ritual of Dance/Meditation
imprisonment, the portal's click interaction, the exact maze route and wisp curve, Branching Affliction/Shadow
Surge/Mystic Miasma damage over time, a half-finished maze after death/logout, corpse items, the Unlight Altar and
Bloodline, exact AI/cooldown timings, and the native client capture for each action's animation. The arena's own
`EndQuestFlags`/`StartQuestFlags` and the War for the Wildwood quest are not wired to the fragment yet.

## Sources

- poewiki: [The King in the Mists](https://www.poewiki.net/wiki/The_King_in_the_Mists),
  [An Audience With The King](https://www.poewiki.net/wiki/An_Audience_With_The_King),
  [Crux of Nothingness](https://www.poewiki.net/wiki/Crux_of_Nothingness).
- poedb: [The King in the Mists](https://poedb.tw/us/The_King_in_the_Mists) (MonsterVarieties, life tooltip, skill
  lists and per-level stats, and its embedded Path of Building `Spectres.lua` / `spectre.lua` rows).
- Extracted 3.29.3b tables: `MonsterVarieties.json` rows 10150 and the `VoodooKingBoss3` row, `MonsterTypes.json`,
  `MonsterMapBossDifficulty.json`, `MonsterMapDifficulty.json`, `DefaultMonsterStats.json`, `GrantedEffects.json`,
  `GrantedEffectStatSets.json`, `GrantedEffectStatSetsPerLevel.json`, `Projectiles.json`, `MonsterProjectileSpell.json`,
  `BuffDefinitions.json`.
- The client's `VoodooKingBoss2.act` / `VoodooKingBoss3.act` and `.ot` files (animation keys: `vine_nova_01`,
  `vine_cascade_01`, `teleport_01`, `spellcast_01/02`, `call_start/loop/end`, `morph_01`, `locust_ball_01`).
