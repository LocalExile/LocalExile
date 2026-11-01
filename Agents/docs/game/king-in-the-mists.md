# The King in the Mists

The Ritual pinnacle boss of the Crux of Nothingness (`WorldAreas AzmeriLeagueBoss`, area level 83). This page records
the fight's data and mechanics, what the Gateway models, and what is still open. It is the reference for
`Modules/Core/Game/Monster/KingInMists.cs`.

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

The encounter has three states. The humanoid is **phase-locked at 50% life**: it cannot be killed in phase 1, and the
maze starts as soon as that floor is reached. The fetish rituals are life-gated at **92%, 73% and 55%** (the requested
cadence; the official trace has no `0x1b1` life stream to confirm the exact percentages):

1. **Phase 1 — humanoid.** The King fights in the circular arena, uses his phase-1 skills, and jumps to one of the three
   Mystic Fetishes when a life gate is crossed. Each activation adds a Portent Vapors stack and starts that fetish's
   arena hazard. A single hit may cross several gates; only the first eligible ritual starts, and the later gates it
   crossed are consumed, so the King never chain-teleports through all three fetishes from one burst. The King is
   invulnerable for the whole ritual channel (the official `0x400e`→`0x4014` window is about 4–6.5 s), using the
   visible `god_mode` row 7 (`invulnerability.dds`) plus the server's `RitualGrace`.
2. **The maze.** At 50% life (`TakeDamageLocked` floors the humanoid there) he opens the maze and teleports the player
   to the hedge maze (phase 2). The official trace shows the final fetish Ritual and `The darkness, exultant!` /
   `Embrace nothingness!` immediately before the maze; the life trace does not exist, so the 50% trigger is the
   requested behavior rather than a directly measured one.
3. **Phase 2 — the maze.** The player follows the Sacred Wisps through the hedge maze to the purple portal back to the
   arena. The maze duration depends on the route the player finds. Dying or logging out during the maze is the wiki's
   bypass; the next entry is phase 3.
4. **Phase 3 — the treant.** Back in the arena, the humanoid is re-staged at **25% life** (the official create blob
   after the maze has 10,804,262 / 43,217,044), plays `0x4013` and is replaced by the treant. The treant has a **full**
   25,209,864 life bar (it does not inherit the husk's 25%) and a 7 s emergence grace (no action, no damage), matching
   the official capture's first action about 7 s after the spawn.

The `arena_01.dgr` graph places the arena and the `_maze.dgr` hedge maze in the **same area** (`AzmeriLeagueBoss`,
seed 1). The maze entrances are `maze_entrance.arm` rooms (a `telepad` point at room-local `(34, 34)`), the exit is
`maze_exit.arm` (room-local portal at `(80, 80)`), and the exit room is at world `(828, 460)` with orientation code 4 in
seed 1. The Gateway uses `ServerLevels` and `AreaPopulation.LocalToWorld` to find them.

> **Official-capture caveat.** The 2026-10-31 official-client trace shows the final fetish Ritual and the
> "The darkness, exultant!" / "Embrace nothingness!" lines immediately before the maze, but no phase-1 HP event. The
> create blob after the maze is the only captured HP near the transition: the humanoid husk is at exactly 25% and the
> treant at 100%. The 50% maze gate and the 92/73/55 ritual gates are therefore the requested behavior; hook `0x1b1`
> on the next official run to confirm them.

## Phase 1 — the humanoid

The King stands at the central arena. The fight begins when the player comes within his 135-cell aggro range. A kept
instance restores the monsters where the player left them, so `Fight.Run` moves a phase-1 King back to the mapboss
point before the intro when its saved cell is more than four cells away; every start is at the arena centre, like a
fresh create (live 2026-11-01: a re-entered fight began at (301,587) instead of (300,542)). His
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
  line; the maze itself starts at 50% life (see "Phase flow").
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
plays the staff slam, its body disintegrates, and the treant form emerges. The treant has a **7 s emergence grace**:
`KingGrace` refuses all damage and the controller performs no action for those seven seconds, matching the official
capture's first action about 7 s after the spawn (see the official-capture section). After the grace it fights normally,
and the three platforms are covered in purple fire. Its GrantedEffects list (`VoodooKingBoss3`) is:

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

`Modules/Core/Game/Monster/KingInMists.cs`:

- Recognises both forms and gives the humanoid/treant their whole GrantedEffect skill list in the actor record, using
  the capture's **official 0x4000+ actor ids** (the two forms have different tables) and the official
  `MeleeAtAnimationSpeedUnique` default attack (`0x4001` humanoid, `0x4000` treant), so each `0x1ab` plays the client's
  own skill animation. The player/generic `0x0102` has no matching clip on these rigs and made the humanoid T-pose and
  punch with an empty hand. Invented ids in the player's gem-skill range
  (`0x0103`-`0x0142`) crashed the client at the action (`0xc0000005`, live 2026-10-31); do not reuse that range for
  monster actions. Bugrend uses the official `VoodooKingBugRend`/`BugRendSingle` rows and
  `Metadata/Projectiles/BugSoulrend` (six projectiles), matching the official capture; the earlier
  `PlayAnimation failed [SpellProjectileChaos]` was a stale Debug build, not the skill.
- The hex, teleport, effigy, ritual and maze actions are **spells** and send their own cast-time stats
  (`MonsterAttack.Spell`); with only the melee attack duration the client crashed in its cast-duration path
  (`0xc0000005` at `0x19e0daf`, live 2026-10-31).
- Skips both forms, the Mystic Fetishes and the Guiding Lights in the ordinary `PlanMonsterAi` loop; the controller
  serializes one action at a time. The current AI stage (`Game_KING_BASIC`, default on) makes only the humanoid's default
  attack decision after the intro: `0x1ab` with skill `0x4001` (`MeleeAtAnimationSpeedUnique`), the actor's attack
  clip, and the ordinary half-action contact. `Game_KING_BASIC=0` opens the rest of the skill table, and
  `Game_KING_AI=0` disables AI decisions entirely. The humanoid's phase-1 movement follows the official clips:
  `0x4009` is `teleport_01` (clip 35, 1.333 s) and blinks to a point ~50 cells from the player when it closes to 20;
  `0x400a`-`0x400d` are `move_fwd`/`move_bwd`/`move_left`/`move_right` (clips 24/22/26/28, 0.5 s), with left/right
  strafes at medium range. Beyond 42 cells the immobile humanoid closes with an approach blink to ~22 cells before the
  player (the forward-dash action moved the client model onto the player and desynced); a single approach jump is capped
  at 24 cells, because a 50+ cell gap used to teleport the model 34 cells at once. Inside 42 it strafes, and
  after two dashes it blinks away (the blink used to be the last resort behind a ready strafe, so it never fired and the
  King strafed forever without casting; live 2026-11-01). The spell projectile is checked before movement and fires
  every 4 s at >15 cells, as the official 22 casts do. Blink uses the simple 52-cell away point snapped through
  `Layout.Near`; if the point is unusable next to a platform, it falls back to a point 35 cells from the arena centre
  on the side away from the player, then triggers a strafe if the player is still within 38. `0x400b` DashBackward
  (clip 22, 0.5 s) is used inside 22 cells when the blink is cooling down; `0x2909` Move (header 0xa000) is a
  low-priority step that holds a ~20-cell distance. Official phase 1 has 3 backward dashes and exactly one 0x2909
  order (10.49 s, immediately followed by 0x4002, so its move clip was interrupted). The official dashes are often
  paired left/right, so two dashes are chained before the normal 2.2 s cooldown. The native clip carries its own `MoveEventType` (`teleport_01` flee_target,
  `move_left`/`move_right` move); strafes use that native movement and only sync the server's walk (the previous
  no-packet strafes looked right). The blink plays only the actor's `teleport_01` clip (0x1b2 kind 0, index 35) instead
  of the 0x4009 skill action; the action's ActiveSkill also runs a teleport effect, the extra fire column. The clip is
  two-stage: burrow at the origin, then the `flee_target`/`contact` event at 0.70 s, then dig-out. The server sends a
  single 0x1b7 at 0.70 s so the second stage plays at the destination; moving at the clip end made both stages play at
  the old cell. A dash's `move_left`/`move_right`/`move_bwd` event is ~260 world units (24 cells; live 2026-11-01
  measured 23.8-24.4 cells on every clip, `logs/frida-king-position-2026-11-01c.jsonl`). The client's `move_left` goes
  toward `(dy, -dx)` and `move_right` toward `(-dy, dx)`, where `(dx, dy)` is the player-to-King vector; the opposite
  convention made every strafe play to one side while the settle dragged the model ~40 cells back to the other (live
  2026-11-01 20:12:49-58). The controller also sends a `0x1b9` facing the player right before the clip: without it the
  facing left over from the previous cast made the clip veer ~30 degrees off the server's perpendicular. The facing
  and the landing cell are both computed from the player cell that the action itself carries at the send gate: the
  120 ms action-end settle can hide a 10+ cell player dash, and a cell from the older loop position made the clip and
  the settle point in different directions (live 2026-11-01: a 40-cell backward-dash correction). The spell projectile
  cast also steps the model ~12 cells toward the action target (it stops ~18 cells from the aim), so the server follows
  the same landing after the cast; without it the next dash started from a cell 12+ cells behind and its settle pulled
  the model across the arena (live 2026-11-01: 55 cells). The native move clip is given 0.65 s from the action send
  (the client starts it ~0.13 s late) and the previous action ends 220 ms before the next one; a 0.5 s window cut the
  clip at ~10 of its 24 cells and the settle snapped the rest (live 2026-11-01: a 30-cell strafe correction). The last
  live build still had 20-41-cell corrections on backward dashes: the client sometimes cuts or redirects a clip at 2-10
  cells even with `CannotBeStunned` and the 0.65 s window. The next diagnostic is to read the client's current
  action/clip/facing at the settle; the Frida trace currently reads only the cell. The `0x2909`
  step is a special case: the client does not path this immobile humanoid with the move order, so its server-side
  teleport left the model ~20 cells behind; it now uses a `0x1ba` glide over the
  same window. The native clip's landing also drifts from the server's computed target; after the clip the controller
  ends the action and sends a `0x1ba` to the server cell (`0.1` s, follow-terrain height marker). The generic
  `CorrectMonsterCollisions` estimate is skipped for both King forms: it changes only the server cell (no packet), so a
  large boss near a melee player was swept back to its previous cell and the next settle dragged the model there (live
  2026-11-01: a strafe onto a fetish platform snapped back to the ramp start within a second). The duration
  argument is **seconds for the
  whole path**, so the older `5000` meant 5000 s: the client never applied the correction, the drift accumulated over
  chained dashes, and the next `0x1b7` snapped the model several cells (live 2026-11-01: the client sat 27.3 cells
  from the server's settle cell at 20:00:28.965, `logs/frida-king-position-2026-11-01b.jsonl`). The 0.1 s settle keeps
  the model and the server cell together, which is also what makes the following blink read as one movement. The
  0x4001 attack is
  **not used while in melee range**: the official humanoid blinked/dashed instead, and the bare default attack has no
  matching unarmed clip on this rig.
  The humanoid opens with `CTS1BossFightStart` (`0x4000`, GrantedEffects row 6227) and is
  invulnerable for its 2.5 s clip, matching the official first action at 46.286 s and first attack at 48.694 s. The
  intro's immunity is the invisible BuffDefinitions row 350 (`base_cannot_be_damaged`) plus the controller's `KingGrace`;
  both end with the clip, and only then the boss bar opens. The overhead unit bar is hidden for the clip with
  `hide_mini_life_bar` (0x2a8b) and shown again at its end. Creating the King as team 0 was tried live on 2026-11-01 and
  reverted: the overhead unit bar turned green and the model vanished when the update switched it back to team 1.
- The humanoid's `.act` gives the monster's `stance2` an idle of `call_loop_01` (the channel into the shrine) and
  `ChangeToStance1 = "call_end_01"` (the end of that channel): the official fight opens with the King channeling, then
  plays the clip and the first attack follows. Live 2026-10-31 the local client creates the humanoid in the **default**
  stance (`idle_01`, clip 17), so the `0x4000` action starts (`ChangeToStance1`, action type `0x16c7`) with no clip to
  play. The official deep capture (2026-11-01, `logs/official-king-deep-2026-11-01.jsonl`) settles the create blob:
  object flags `0x02` and four base-data links to the arena (shrine `4`; blockers `12/11/7/10/8/9`; fetishes `16/17/18`
  and `18/16/17`), **Animated flags 0** (no bit-3 start: a bit-3 experiment played `call_loop_01` but left the stance
  default), and the granted skills in the `.ot` starting-effect order, `CTS1BossFightStart` (`0x4000`) first and the
  default attack second. The official `0x4000` action carries flags `0x0800`, header u16 `0xD000` and no target object;
  it is the only action in the capture with those fields. The controller now sends the skill list in that order and the
  action with those fields. The base-data links and the staff are still open: the humanoid's Inventories component is
  empty, so `VoodooKingBossWeapon.ao` is never created.
- Every King action carries a **fresh sequence** (`0x8000+`), as the official server does (`0x8011`, `0x8012`, …). Reusing
  `0xffff` for every action left the client's channel object without a current action (`0xc0000005` at `0x19e0daf`, live
  2026-10-31). The ritual's `0x400e` channel start and the fetish/shrine StateMachine `active` update are currently
  withheld; the ritual plays the official rig clips and runs server-side (see below).
- Phase 1: creates the three `VoodooKingBoss2RitualPillar` Mystic Fetishes at the official create positions
  (mapboss-relative `(-68, -52)`, `(68, -51)` and `(-2, 64)`; world `(232, 490)`, `(368, 491)` and `(298, 606)` at
  seed 1) with the official Positioned rotations `5.23599`, `1.0472` and `3.14159` rad. The cells are blocked on the
  walk map, so they are placed exactly and not snapped through `Layout.Near` (that is what shifted them off the
  platforms). Each fetish spawns immune (`FetishGrace`, the invisible BuffDefinitions row 350 and a hidden overhead
  bar); while it is immune it also refuses ailments, impale, leech and hit reactions, not only life damage. The fetish the
  King channels into stays immune through the whole ritual animation and becomes hostile, attackable and bar-visible
  when `call_end_01` has finished (the StateMachine activation that would normally do this is still withheld); a fetish
  destroyed after its ritual is skipped by the next `NearestFetish` pick. Rituals are
  life-gated at 92%, 73% and 55% (a single hit starts only the first crossed gate and consumes the others it crossed);
  the King is invulnerable from the start of the teleport through the whole channel (`RitualGrace` plus visible row 7),
  about 4–6.5 s in the captures. The
  client `GSVoodooKingRitual` (`0x400e`) channel start is not sent: as an ordinary `0x1ab` action it makes the client
  enter `GeometryChannelBase::StartChannelling` without a current action and dereference a null pointer (`0xc0000005` at
  `0x19e0daf`, live 2026-10-31 and 2026-11-01). The fetish/shrine StateMachine `active` update enters the same path, so
  it is withheld too. The ritual instead plays the official `call_start_01` (clip 4), `call_loop_01` (3) and
  `call_end_01` (2) rig clips and runs server-side. The King is immune to damage for the whole sequence, so no ordinary
  damage can interrupt the cast; the controller still checks the fetish, but it cannot be destroyed before the
  vulnerability window. A completed ritual spawns that fetish's hazard actors (rats or toads); its `active` state and the central
  shrine's `active_pillars` state are not advanced until the channel action contract is known. It does not send a raw
  `0x1ab` hazard action to the fetish: `RitualPillar.act` does not carry the `SpellAreaOfEffect` animation in its
  default stance. The fetishes are hostile, stationary and destructible; breaking one gives no experience or loot and
  removes it from the King's rotation. The humanoid adds Portent Vapors stacks, uses Vine Slam and Vine Cascade, and
  fires its spell projectile every 4 s at range. Its Meditation/Dance hexes (`0x4006`/`0x4007`) are not sent for the
  same channel reason. It is immobile, so it does not walk. The central `HugeRitualShrine01_01.ao` is a server object
  (`RitualPillar2`) created at the `arena.arm` point mapboss-relative `(0, -12)`; its `active_pillars` state
  (0 = `idle_inactive`, 1–3 = `idle_active_01`–`_03`) is the shrine's start/quarter/half/full animation, currently not
  advanced.
- At 50% life (`TakeDamageLocked` floors the humanoid there): plays the staff slam and the maze-opening animation,
  teleports the player to the nearest `maze_entrance.arm` telepad, applies Branching Affliction, builds a **room-graph
  route** from the entrance room to `maze_exit.arm` (BFS over the placed maze rooms, each leg checked with the actual
  walk map), spawns the exit portal, a moving `GuidingLightDaemonMaze_` trail along that route, one `WerewolfMaze` and
  one `MeleeSpiderMaze`, and auto-returns the player when they reach the exit (see Phase 2 above).
- On the maze return: teleports the humanoid back to the mapboss, sets its life to the official 25% husk value (and
  sends `0x1b1`), plays the staff slam and waits for the full `EASVoodooKingBecomeATree` morph (5.5 s), removes the
  humanoid, spawns `VoodooKingBoss3` with full 25,209,864 life and `KingGrace` (7 s, matching the official first-action
  timing) and starts phase 3.
- Phase 3: Treant Slam, Bug Bomb, Bugrend (`BugSoulrend`, six projectiles), Summon Effigy, Ritual of Meditation/Dance
  (icon/duration only), VoodooKingPunishment and the default attack. The treant walks after a player that kites it
  (`Move` 0x2909) instead of standing at the altar between casts. There is no pre-cast model alignment: the action's
  own start coordinates are the server's walk cell, so the projectile origin and the model agree without an extra
  `0x1b7`. The server-created projectile objects always receive an explicit flight `0x1b2` (even when the `.ao` declares
  the same default animation) and enter the wake zone, so the default-animation King projectiles are drawn.
- `ItemDrops.KingInMists` rolls the guaranteed unique, Hextoad, Hexpass and The Endless Darkness.

Open: the phase-trigger confirmation (`0x1b1` life trace vs the branch's 50% gate and 92/73/55 ritual gates), whether a destroyed fetish
returns for a later ritual or stays broken, exact central-shrine state timing, blood/caustic ground objects, the Ritual
imprisonment/maim movement rule, the portal's click interaction, exact maze route/wisp curve, Branching
Affliction/Shadow Surge/Mystic Miasma damage over time, a half-finished maze after death/logout, monster energy shield,
corpse items, the Unlight Altar and Bloodline, exact AI/cooldown timings, and native animation capture for the remaining
actions. The arena's own `EndQuestFlags`/`StartQuestFlags` and the War for the Wildwood quest are not
wired to the fragment yet.

## Official 3.29.3b capture (2026-10-31)

This section records a read-only Frida trace of a complete official-server kill. It is the reference for the differences
between the real fight and this branch; it is deliberately separate from the wiki/data sections above because the
official trigger is not fully settled.

### Evidence and method

- Official Steam client `PathOfExileSteam.exe`, **3.29.3b / build 321328** (the same build as `Client/Game.exe`).
- Frida hooks loaded through `scripts/re/frida/run.py`: `trace-actions.js` (every `0x1ab` action: actor, skill id,
  sequence, target), `trace-skills.js` (ActiveSkillManager additions and skill names), `trace-enter-area.js` (area
  milestones/CreateObject types) and `trace-projectile-create.js` (client/server CreateObject types and projectile
  creation). The scripts were combined into one Frida script because `run.py` loads each file as a separate script, so
  `common.js` globals such as `at` are not shared; `common.js` was pointed at `PathOfExileSteam.exe`.
- Raw trace: `logs/frida-official-king.jsonl` (local, gitignored; 11,744 messages, first fight 15:45:19–15:49:01).
  The runner detached with `process-terminated` when the client closed, so the first fight is complete.
- Official dialogue timestamps: `F:\SteamLibrary\steamapps\common\Path of Exile\logs\Client.txt`, lines 1164–1192.
- First-fight object ids: humanoid King **15 (`0xf`)**, treant **373 (`0x175`)**, Mystic Fetishes **16/17/18**
  (`0x10/0x11/0x12`), central `RitualPillar2` **4**. Manager pointers are session-specific and not stable across runs.

### Official timeline

| Time | Event |
|---|---|
| 15:45:24 | `Protect the ritual!` — phase 1 starts |
| 15:45:28–15:46:29 | Vine Cascade / Vine Slam / teleports / effigies / Meditation / hazards; three fetishes activated |
| 15:46:29 | `The darkness, exultant!` (third fetish) |
| 15:46:32–33 | `Nothingness approaches!` / `Embrace nothingness!` |
| 15:46:39 | `A sliver of what my home feels like.` — maze; `EASVoodooKingMazePhase` action |
| 15:46:39–15:46:54 | phase 2, hedge maze (~15 s) |
| 15:46:54 | `The Wildwood's power... is mine!` — treant; `EASVoodooKingBecomeATree` |
| 15:47:05–15:47:40 | Meditation (`You are seen!`), Treant Slam (`An dire ed!`), Bug Bomb (`The end of meaning is nigh!`), Dance (`Rith!`), death (`So long as you know me, I will always exist...`) |

### Official skill ids

From the trace's `skillAdded` records. These are **client-side skill ids**; the GrantedEffects rows remain the ones in the
tables above.

**Humanoid `VoodooKingBoss2` (manager of the first fight):**

| id | Grantedeffect / action |
|---|---|
| `0x4000` | `CTS1BossFightStart` |
| `0x4001` | `MeleeAtAnimationSpeedUnique` |
| `0x4002` | `MPSVoodooKing2SpellProjectile` |
| `0x4003` | `GTVoodooKingVineCascade` |
| `0x4004` | `GAVoodooKingVineSlam` |
| `0x4005` | `EASVoodooKingSummonEffigy` |
| `0x4006` | `EASVoodooKingHexNoMovementAllowed` (Ritual of Meditation) |
| `0x4007` | `EASVoodooKingHexNoStandingAllowed` (Ritual of Dance) |
| `0x4008` | `VoodooKingTeleport` |
| `0x4009` | `VoodooKingTeleportFlee` |
| `0x400a`–`0x400d` | `VoodooKingDashForward/Backward/Left/Right` |
| `0x400e` | `GSVoodooKingRitual` |
| `0x400f` | `EASVoodooKingMazePhase` |
| `0x4010`/`0x4011` | `GAVoodooKingVineCascadeSmall/Big` |
| `0x4012` | `VoodooKingPunishment` |
| `0x4013` | `EASVoodooKingBecomeATree` |
| `0x4014` | `EASVoodooKingReturnFromEdge` |

**Treant `VoodooKingBoss3`:**

| id | GrantedEffect / action |
|---|---|
| `0x4000` | `MeleeAtAnimationSpeedUnique` |
| `0x4001` | `MPSVoodooKing3SpellProjectile` |
| `0x4002` | `GAVoodooKingVineSlam` (Treant Slam) |
| `0x4003` | `EASVoodooKingHexNoMovementAllowed` |
| `0x4004` | `EASVoodooKingHexNoStandingAllowed` |
| `0x4005` | `GPSVoodooKingBugBomb` |
| `0x4006` | `VoodooKingPunishment` |
| `0x4007` | `EASVoodooKingSummonDeathAuraEffigy` |
| `0x4008` | `GSVoodooKingBugBombImpact` |
| `0x4009` | `VoodooKingBugRend` |
| `0x400a` | `VoodooKingBugRendSingle` |
| `0x400b` | `GTVoodooKingSwarmPulse` |

**Mystic Fetishes** have their own managers (the three fetish actors) with:

| id | GrantedEffect |
|---|---|
| `0x4000` | `GTVoodooKing2RatSwarm` |
| `0x4001` | `GTVoodooKing2CorruptedBloodDegen` |
| `0x4002` | `GTVoodooKing2SummonFrogSwamps` |
| `0x4003` | `CGEVoodooKing2CorrptedBloodDegen` |
| `0x4004` | `CGEVoodooKing2SummonFrogSwamp` |

The official server sends the fetish actions to the **fetish actor**, not to the King. The trace shows
`VoodooKingSwampGround`, `ExplosiveToad` and `PoisonRat` objects during phase 1.

### Official phase 1 sequence (first fight, actor `0xf`)

```
CTS1BossFightStart -> DashBackward -> TeleportFlee -> VineCascade -> VineSlam
-> SpellProjectile ×5 -> VineCascade -> SpellProjectile ×3
-> Ritual (player) -> Ritual (fetish 0x12) -> ReturnFromEdge (self)
-> VineCascade -> SummonEffigy -> DashLeft -> SpellProjectile ×2 -> VineSlam
-> Meditation (player) -> SpellProjectile
-> Ritual (fetish 0x10) -> ReturnFromEdge -> ... VineCascade ×...
-> DashBackward -> SpellProjectile -> DashBackward -> SpellProjectile
-> TeleportFlee -> DashLeft -> SummonEffigy
-> Ritual (player) -> Ritual (fetish 0x11) -> ReturnFromEdge
-> Move -> MazePhase -> BecomeATree ×2
```

Key points:

- **Fetish activation is `GSVoodooKingRitual` targeted at the fetish**, followed by `EASVoodooKingReturnFromEdge`
  targeted at the King. The King also uses `VoodooKingTeleportFlee` and the dash skills to move; the plain teleport is
  not the only movement.
- The third Ritual (`0x11`) is immediately before `MazePhase`; the official chat line for it is
  `The darkness, exultant!`.
- The official phase 1 also uses **Ritual of Meditation** (`0x4006`, target player) and effigies. It does not show a
  Dance cast in this first fight, but the skill is in the actor list.
- The official fetish hazards are cast by the fetish actors. The King's own rats (`Summon Plagued Rats`) appear too.

### Official dialogue (2026-11-01 aligned capture)

`Client.txt` (2026-11-01 11:44–11:47) and `logs/official-king-full-2026-11-01.jsonl` (11:44:20–11:47:37) are the same
fight, so every line has an action id. The client randomises a bark per skill, so the controller rotates through the
captured lines for each pool.

| Trigger | Captured lines |
|---|---|
| spawn / intro | `Mharu an koriak!`, `Protect the ritual!` |
| `0x4003` Vine Cascade | `Thorngrip!` (every cast in both captures) |
| `0x4004` Vine Slam | `Sprout!`, `Invasive growth!`, `Grow!` |
| `0x4005` Summon Effigy | `Agrik armach!`, `Dark effigy!` |
| `0x4006` Ritual of Meditation | `You are seen!` |
| `0x4007` Ritual of Dance | `Keep running!` |
| `0x400e` Ritual | `Bid the darkness!`, `Nothingness approaches!` — at the start of `call_start_01` (11:45:27 and 11:45:50), not at the teleport |
| `0x400f` Maze Phase | `The border of my realm grows closer...`, `Embrace nothingness!` |
| maze entry | `A sliver of what my home feels like.` |
| inside the maze | `Ata... ore Wisps maah!` (~8 s after entry), `The Draiocht cannot help you here!` (later) |
| `0x4013` Become a Tree | `The Wildwood's power... is mine!` |
| treant fight | `Traveler's bane!`, `Oblivion awaits!`, `The end of meaning is nigh!`, `Rith!` |
| treant death | `So long as you know me, I will always exist...` (2026-10-31), `This is not the end...` (2026-11-01) |

The two hex lines are icon-only debuffs in the gateway (the imprisonment/maim rule is not modelled). The treant lines
after `0x4013` are recorded here but not wired yet; the phase-3 transform is still being verified.

### Full 2026-11-01 capture (11:44–11:47)

The same fight as the dialogue table has 30,966 messages. The humanoid (actor 15) makes 62 actions: 22 projectiles,
7 cascades, 2 slams, 5 `TeleportFlee`, 6 `DashLeft`, 3 `DashBackward`/`DashRight`/`ReturnFromEdge`, 2 rituals, 1 each
of effigy, Meditation, Dance, `MazePhase`, `BecomeATree` and Move, plus 3 unidentified `0x21a9` actions; `0x4008` never
appears. The treant (actor 609) makes 42 actions. The client calls `StartChannelling` exactly twice, at the two
`0x400e` actions (11:45:24.420 and 11:45:50.495): the raw channel needs a real action, which is the contract the
gateway does not have yet. The trace also contains 1,293 `exception` records, all access violations inside
`frida-agent.dll` from the hooks themselves; they do not interrupt the captured messages.

### Official phase 2 (maze)

- It is the **same `AzmeriLeagueBoss` area**, not a separate map. The trace's `CreateObject` records contain the maze
  monsters at area load:
  - `Metadata/Monsters/LeagueAzmeri/GuidingLight/GuidingLightDaemonMaze_@83` (Guiding Light; object size 1, no granted
    effects; ids 112–126, recreated/moved along the route),
  - `Metadata/Monsters/LeagueAzmeri/Werewolves/WerewolfMaze@83` (Wildwood Werewolf),
  - `Metadata/Monsters/LeagueAzmeri/Spiders/MeleeSpiderMaze@83` (Bloodtainter Arachnid).
- The player was in the maze from **15:46:39 to 15:46:54** (about 15 s) — the route can be short when following the
  Guiding Lights.
- The official Guiding Light is a **monster/daemon**, not a generic effect object; the maze is populated with hostile
  werewolves/spiders as well.
- The official maze is bypassed by death/logout per the wiki; the next entry is phase 3.

### Official phase 3 sequence (first fight, actor `0x175`)

The first treant actions in the trace are:

```
VineSlam -> Meditation -> Melee -> VineSlam -> Melee -> SummonDeathAuraEffigy
-> BugBomb -> Melee... -> SpellProjectile -> Melee... -> VineSlam -> ...
-> SummonDeathAuraEffigy -> Dance -> VineSlam -> ... -> BugBomb -> ...
```

- The **first treant action is ~7 s after the treant spawn** (spawn 15:46:54.333, first action between 15:47:00.9 and
  15:47:02.1). The first dialogue line, `You are seen!`, is at 15:47:05 (~11 s). The official emergence grace is
  therefore roughly **6–8 s**; the branch now uses **7 s**.
- The treant uses **Ritual of Meditation** (`0x4003`) and **Ritual of Dance** (`0x4004`); both are in its actor list and
  both appear in the chat lines (`You are seen!`, `Rith!`).
- The treant also has **Punishment** (`0x4006`), **BugBombImpact** (`0x4008`) and **SwarmPulse** (`0x400b`) in its actor
  list.
- **BugRend works on the official client.** The trace shows `VoodooKingBugRend` (`0x4009`) / `VoodooKingBugRendSingle`
  (`0x400a`) and six `Metadata/Projectiles/BugSoulrend` projectiles in one cast. The official `Client.txt` contains no
  `PlayAnimation failed` line. The branch originally bound Bugrend to `MPSVoodooKing3SpellProjectile` (3894) and
  `AzmeriVoodooKing3SpellProj` as a workaround; that was wrong, and the binding is now restored to
  `VoodooKingBugRend`/`BugRendSingle` + `BugSoulrend`. The earlier live `PlayAnimation failed [SpellProjectileChaos]` was
  from the stale Debug Gateway, not from the official skill.
- The official treant's `BugBomb` uses the real `GPSVoodooKingBugBomb` and `GSVoodooKingBugBombImpact` pair; the branch
  sends `GPSVoodooKingBugBomb` and its projectile impact, but does not apply **Shadow Surge**.
- `Mystic Miasma` is the death-aura effigy (`voodoo_king_death_aura_effigy`, BuffDefinitions row 2401); the branch
  spawns the effigy but does not apply the aura/DoT.

### Discrepancies against this branch

| Area | Official 3.29.3b | This branch (`KingInMists.cs`) | Remaining |
|---|---|---|---|
| Fetish activation | `GSVoodooKingRitual` **targeted at the player first, then the fetish**, then `EASVoodooKingReturnFromEdge` self | Implemented: `VoodooKingTeleportFlee` approach, official `call_start_01`/`call_loop_01`/`call_end_01` rig clips and a server-side ritual; the fetish stays immune through the animation and becomes attackable at `call_end_01`, and the King is immune throughout; `0x1b7` return to the centre. The raw `0x400e` channel and the fetish/shrine `active` state are withheld because they crash `StartChannelling` (`0xc0000005` at `0x19e0daf`) | The action contract that lets the client channel without a null action; the exact dash movement sequence |
| Fetish hazards | Fetish actors cast `RatSwarm`, `CorruptedBloodDegen`, `SummonFrogSwamps`; toads/swamp/blood objects | Implemented: state-machine activation + rats/toads; no raw hazard `0x1ab` (the ACT lacks the default-stance `SpellAreaOfEffect` clip) | `VoodooKingSwampGround` and blood/caustic ground objects; hazard DoT |
| Central shrine | `RitualPillar2` actor and `HugeRitualShrine01_01.ao`; `active_pillars` 0–3 | Implemented: server object at the `arena.arm` point, state update per completed ritual (`idle_inactive` → `idle_active_01`–`_03`) | Exact official state timing (capture logged actor creation, not 0x2ba) |
| Phase 1 hexes | Meditation `0x4006` on the player | Implemented as `hex_01` (clip 11) + BuffDefinitions 2358/2359 for 8 s, alternating every 25 s | Imprisonment/maim rule and the actual action/channel contract |
| Phase 2 | Same area; `GuidingLightDaemonMaze_`, `WerewolfMaze`, `MeleeSpiderMaze`; Guiding Lights move along the route | Implemented: room-graph BFS route with walkable legs; real `GuidingLightDaemonMaze_` actors move along it; one werewolf and one spider spawn at the middle | Exact route/wisp curve and the full maze population |
| Phase 3 skills | Slam, Meditation, Dance, Effigy, BugBomb, Punishment, BugRend/BugRendSingle, BugBombImpact, SwarmPulse | Implemented: Slam, BugBomb, BugRend, Effigy, Meditation/Dance, Punishment; BugBombImpact/SwarmPulse are in the actor list | Shadow Surge, BugBombImpact/SwarmPulse action tuning |
| BugRend | `VoodooKingBugRend`/`BugRendSingle` + `BugSoulrend` projectiles (six) | Implemented: official effect + `BugSoulrend`, six projectiles | — |
| Emergence grace | First action ~7 s after spawn; first chat line ~11 s | 7 s | — |
| Phase trigger | Final fetish `Ritual`/`Embrace nothingness!` immediately before the maze; phase-1 HP not captured. The post-maze husk create is exactly 25% and the treant is 100% | 50% life floor and trigger; rituals at 92/73/55%, one ritual per hit, immunity for the channel; husk set to 25% on the return | Hook `0x1b1` LifeUpdate on the next official run to confirm 50% and the 92/73/55 gates |
| Teleport model | TeleportFlee/dash + Ritual target + ReturnFromEdge | Implemented: the approach uses the official `VoodooKingTeleportFlee` (12420) and the return `EASVoodooKingReturnFromEdge`; both clips run for their one second, then `0x1ac` ends the action and only then does `0x1b7` move the object. The client ignores a `0x1b7` that arrives while the actor is still in the teleport action, which left its model at the previous spot while the server attacked from the new one (live 2026-10-31) | Exact dash sequence |

### Next capture (to settle the trigger)

The trace did not log phase-1 life, but its create blobs do: the humanoid's maximum is 43,217,044, the post-maze husk
is 10,804,262 (25.0%) and the treant is 25,209,864 (100%). For the next official run, add a hook on the client's
**LifeUpdate** (`0x1b1`) handler (or the actor Life component) and record the King's current/max life whenever it
receives a `Ritual`, `MazePhase` or `BecomeATree` action. That will confirm whether the maze starts at 50% and whether
the 92/73/55 ritual gates match the official values. Until then, this branch implements the requested 50/92/73/55
model and must not claim it is measured from the official client.

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
- Official 3.29.3b client capture (2026-10-31): `logs/frida-official-king.jsonl` (local, gitignored) with Frida
  `trace-actions`/`trace-skills`/`trace-enter-area`/`trace-projectile-create`; the official Steam client's
  `logs/Client.txt` lines 1164–1192. The trace is summarized in "Official 3.29.3b capture" above; the raw log is not
  committed.
