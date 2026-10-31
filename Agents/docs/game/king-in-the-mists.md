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

The encounter has three states. **This branch** uses a 30% life gate; the official capture shows the third fetish
Ritual immediately before the maze, and the exact official trigger is still unresolved:

1. **Phase 1 — humanoid.** The King fights in the circular arena, uses his phase-1 skills, and periodically teleports to
   one of the three Mystic Fetishes on the raised platforms. Each activation adds a Portent Vapors stack and starts that
   fetish's arena hazard. In this branch the three fetishes can all be activated without ending the phase; the official
   trace shows the third `Ritual` and `The darkness, exultant!` immediately before the maze.
2. **The maze.** In this branch it starts at 30% life (`TakeDamageLocked` floors the humanoid there): he raises and slams
   his staff, opens the maze and teleports the player to the hedge maze (phase 2). The old "after the third fetish"
   transition went straight to the treant at ~80% life, which was wrong; the official third Ritual leads to the maze,
   not directly to the treant. Whether the official maze trigger is the third Ritual, 30% life, or both needs a
   `0x1b1` LifeUpdate capture (see the caveat below and "Official 3.29.3b capture").
3. **Phase 2 — the maze.** The player follows the Sacred Wisps through the hedge maze to the purple portal back to the
   arena. Dying or logging out during the maze is the wiki's bypass; the next entry is phase 3.
4. **Phase 3 — the treant.** Back in the arena, the humanoid body disintegrates at the central shrine and the treant
   emerges. The treant has a 7 s emergence grace (no action, no damage), matching the official capture's first action
   about 7 s after the spawn (the first chat line, `You are seen!`, is at ~11 s).

The `arena_01.dgr` graph places the arena and the `_maze.dgr` hedge maze in the **same area** (`AzmeriLeagueBoss`,
seed 1). The maze entrances are `maze_entrance.arm` rooms (a `telepad` point at room-local `(34, 34)`), the exit is
`maze_exit.arm` (room-local portal at `(80, 80)`), and the exit room is at world `(828, 460)` with orientation code 4 in
seed 1. The Gateway uses `ServerLevels` and `AreaPopulation.LocalToWorld` to find them.

> **Official-capture caveat.** The 2026-10-31 official-client trace (see "Official 3.29.3b capture" below) shows the
> third fetish Ritual and the "The darkness, exultant!" / "Embrace nothingness!" lines immediately before the maze, not
> a logged HP event. The exact boss life at that moment was not captured, so whether the official trigger is the third
> Ritual, 30% life, or both is unresolved. The implementation currently follows Qblolz's requested **30% life** gate
> and floors the humanoid there; do not change it to all-altars without a `0x1b1` LifeUpdate capture.

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

`KingInMists.cs`:

- Recognises both forms and gives the humanoid/treant their whole GrantedEffect skill list in the actor record, so each
  `0x1ab` plays the client's own skill animation. Bugrend uses the official `VoodooKingBugRend`/`BugRendSingle` rows and
  `Metadata/Projectiles/BugSoulrend` (six projectiles), matching the official capture; the earlier
  `PlayAnimation failed [SpellProjectileChaos]` was a stale Debug build, not the skill.
- Skips both forms, the Mystic Fetishes and the Guiding Lights in the ordinary `PlanMonsterAi` loop; the controller
  serializes one action at a time.
- Phase 1: creates the three `VoodooKingBoss2RitualPillar` Mystic Fetishes at the `arena.arm` object points
  (mapboss-relative `(94, 122)`, `(230, 123)` and `(160, 238)`; world `(232, 490)`, `(368, 491)` and `(298, 606)` at
  seed 1). On the 10-second cadence it teleports next to one, sets the fetish's StateMachine `active` state, sends
  `GSVoodooKingRitual` **targeted at the fetish**, waits for the ritual, then spawns that fetish's hazard actors (rats
  or toads). It does not send a raw `0x1ab` hazard action to the fetish: `RitualPillar.act` does not carry the
  `SpellAreaOfEffect` animation in its default stance, and the state-machine activation is the visible ritual. The
  fetishes are hostile and damage-immune (`KingGrace`), so they neither show the friendly outline nor die. It adds
  Portent Vapors stacks, casts Ritual of Meditation, uses Vine Slam and Vine Cascade, and fires its spell projectile
  every 4 s at range. The humanoid is immobile, so it does not walk. The central `HugeRitualShrine01_01.ao` is the
  room's own static model.
- At 30% life (`TakeDamageLocked` floors the humanoid there): plays the staff slam and the maze-opening animation,
  teleports the player to the nearest `maze_entrance.arm` telepad, applies Branching Affliction, builds a **room-graph
  route** from the entrance room to `maze_exit.arm` (BFS over the placed maze rooms, each leg checked with the actual
  walk map), spawns the exit portal, a moving `GuidingLightDaemonMaze_` trail along that route, one `WerewolfMaze` and
  one `MeleeSpiderMaze`, and auto-returns the player when they reach the exit (see Phase 2 above).
- On the maze return: teleports the humanoid back to the mapboss, plays the staff slam and waits for the full
  `EASVoodooKingBecomeATree` morph (5.5 s), removes the humanoid, spawns `VoodooKingBoss3` with `KingGrace` (7 s,
  matching the official first-action timing) and starts phase 3.
- Phase 3: Treant Slam, Bug Bomb, Bugrend (`BugSoulrend`), Summon Effigy, Ritual of Meditation/Dance (icon/duration
  only), VoodooKingPunishment and the default attack. The treant walks after a player that kites it (`Move` 0x2909)
  instead of standing at the altar between casts. Before a cast, the controller aligns the client model to the server's
  walk cell (`0x1b7`), so the projectile origin is where the model is shown.
- `ItemDrops.KingInMists` rolls the guaranteed unique, Hextoad, Hexpass and The Endless Darkness.

Open: the phase-trigger confirmation (official third-Ritual line vs the branch's 30% gate), blood/caustic ground
objects, the Ritual imprisonment/maim movement rule, the portal's click interaction, exact maze route/wisp curve,
Branching Affliction/Shadow Surge/Mystic Miasma damage over time, a half-finished maze after death/logout, monster
energy shield, corpse items, the Unlight Altar and Bloodline, exact AI/cooldown timings, and native animation capture for
the remaining actions. The arena's own `EndQuestFlags`/`StartQuestFlags` and the War for the Wildwood quest are not
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
| Fetish activation | `GSVoodooKingRitual` **targeted at the fetish**, then `EASVoodooKingReturnFromEdge` self | Implemented: teleport next to the fetish, `RitualSkill` with `targetObjectId = fetishId`, `ReturnFromEdge`, return to the arena centre | The exact TeleportFlee/dash movement sequence |
| Fetish hazards | Fetish actors cast `RatSwarm`, `CorruptedBloodDegen`, `SummonFrogSwamps`; toads/swamp/blood objects | Implemented: state-machine activation + rats/toads; no raw hazard `0x1ab` (the ACT lacks the default-stance `SpellAreaOfEffect` clip) | `VoodooKingSwampGround` and blood/caustic ground objects; hazard DoT |
| Phase 1 hexes | Meditation `0x4006` on the player | Implemented: `CastHex` plays Meditation, applies BuffDefinitions 2358 and removes the icon after 8 s | Imprisonment/maim movement rule |
| Phase 2 | Same area; `GuidingLightDaemonMaze_`, `WerewolfMaze`, `MeleeSpiderMaze`; Guiding Lights move along the route | Implemented: room-graph BFS route with walkable legs; real `GuidingLightDaemonMaze_` actors move along it; one werewolf and one spider spawn at the middle | Exact route/wisp curve and the full maze population |
| Phase 3 skills | Slam, Meditation, Dance, Effigy, BugBomb, Punishment, BugRend/BugRendSingle, BugBombImpact, SwarmPulse | Implemented: Slam, BugBomb, BugRend, Effigy, Meditation/Dance, Punishment; BugBombImpact/SwarmPulse are in the actor list | Shadow Surge, BugBombImpact/SwarmPulse action tuning |
| BugRend | `VoodooKingBugRend`/`BugRendSingle` + `BugSoulrend` projectiles (six) | Implemented: official effect + `BugSoulrend`, six projectiles | — |
| Emergence grace | First action ~7 s after spawn; first chat line ~11 s | 7 s | — |
| Phase trigger | Third fetish `Ritual`/`Embrace nothingness!` immediately before the maze; HP not captured | 30% life floor and trigger (Qblolz's requested behavior) | Hook `0x1b1` LifeUpdate on the next official run; decide whether 30% is the actual trigger or only a coincidence |
| Teleport model | TeleportFlee/dash + Ritual target + ReturnFromEdge | Implemented: ritual/return sequence and `Align` 0x1b7 before casts | Exact TeleportFlee/dash sequence |

### Next capture (to settle the trigger)

The trace did not log the King's life. For the next official run, add a hook on the client's **LifeUpdate** (`0x1b1`)
handler (or the actor Life component) and record the King's current/max life whenever it receives a `Ritual`,
`MazePhase` or `BecomeATree` action. That will show whether the official maze starts at 30% life, after the third
fetish, or both. Until then, this branch keeps the 30% behavior requested by Qblolz and must not claim it is the official
trigger.

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
