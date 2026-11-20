# Sirus in the client's code (Orion)

The data side is [Sirus, Awakener of Worlds](sirus.md): the arena, the forms, the kit, the clips, the storms. This page
is the code side: how the client turns one of his skills into an action class, the classes behind the five `Orion*`
action types, the rows that pick his art, and what each skill's class does on the client.

Addresses are the 3.29.3c release's (build 327984); code addresses hold for every allowed build, data addresses
(vtables, strings, constants) do not ([tooling](../re/tooling.md) §11). Client stat ids are the `Stats` row + 1
(client 6433 = row 6432 `skill_behaviour_variation`). Offsets are into the action object.

## The action-type registry

`0x141947300` is the only writer, `register(name, constructor, collector, slots)`. It hashes the name and stores the
`(constructor, collector)` pair under that hash.

- **The hash** starts at 0, multiplies by `0x01000193`, xors each byte in, and folds the high and low 16 bits
  together. It reproduces `ActionTypes.HASH16`: `OrionBeamSweep` 19042 (`0x4a62`), `OrionMeteor` 28504 (`0x6f58`),
  `OrionCorridorBlast` 40973 (`0xa00d`), `OrionCircleMazeBlast` 42185 (`0xa4c9`), `OrionBeamBomb` 13449 (`0x3489`).
  The same hash names animation events (`create_corridor` `0xa37a`, `beam_left` `0x41a4`, `beam_right` `0xdbed`,
  `finish` `0xd9cf`, `stop_beam` `0xa32e`, `blast` `0x8a6a`).
- **The map** is the sorted vector at `0x1447603e8` (the function-local static of `0x1419475a0`), 24-byte records
  `{u16 hash, u8 slot, u16 pad, u64 constructor, u64 collector}`; `0x14197bd40` is its find-or-insert.
- **The two slots.** The fourth argument is 0 = both slots, 1 = slot 0 only, 2 = slot 1 only. `0x1419473e0`
  (`create(hash, actor, skill, flag)`) asks the actor for its controller (vtable `+0x148`) and reads its state at
  `+0x14c`: state `0xe` or `9`, or `0x25` with `+0x150 == 9`, selects slot 1; anything else, or no controller, slot 0.
  It finds the `(hash, slot)` record (`0x14197bee0`) and calls its constructor with the actor, the skill and the flag.
- **The collector pass.** `0x1419474d0` calls slot 0's collector, then slot 1's unless it holds the same pair.

**Two registration passes.** `0x1400fed40` (the `[STARTUP] Registration` step) calls `0x14194c880`, `0x14124b4e0` and
`0x141e0a9d0` in that order. The first two are flat lists of calls to tiny registration stubs (1,131 and 1,025 calls,
stub blocks `0x14196e930`–`0x14197bd10` and `0x141264d30`–`0x141270d30`), all with `slots = 0`, so the second pass
overwrites the first for the 834 names they share. The first pass registers four `Orion*` names (not `OrionBeamBomb`)
with constructors that stop at the base class and collectors that are the shared `0x1419e6d50` alone; the second
registers all five with the classes below, so those are the ones a lookup returns.

## How a skill becomes an action

`0x141ef0050` (the action factory the S→C `0x1ab` path goes through) finds the skill instance by the 16-bit skill id
the server sends, checks that the skill is usable, and passes the u16 at skill instance `+0x42` (the action type's
`HASH16`, from `ActiveSkills` → `ActionTypes`) to `0x1419473e0` with flag 0. No effect name, clip or object path is
on the wire.

The preload goes the other way: `0x141ef0f00` (a skill instance added to an `ActiveSkillManager`) calls `0x1419474d0`,
which runs the collectors for every action type the added skills use.

## The five classes

| action type | `ActionTypes` | class ctor | class vtable | size | base ctor | base vtable | collector |
|---|---|---|---|---|---|---|---|
| `OrionBeamSweep` | 1034 | `0x14127ee70` | `0x14337c8e0` | 0x368 | `0x141d1ac90` | `0x143587090` | `0x141416f90` |
| `OrionMeteor` | 1035 | `0x14127ed30` | `0x14337c358` | 0x338 | `0x141d1f120` | `0x143588ae8` | `0x14141a240` |
| `OrionCorridorBlast` | 1036 | `0x14127ec00` | `0x14337add8` | 0x338 | `0x141d1de90` | `0x143588048` | `0x141419ce0` |
| `OrionCircleMazeBlast` | 1037 | `0x14127eab0` | `0x14337b8d8` | 0x3a0 | `0x141d1be60` | `0x1435875a0` | `0x141d1c130` |
| `OrionBeamBomb` | 1039 | `0x1412d0520` | `0x1433801b8` | 0x3e0 | — | `0x1435885c8` | `0x1414151c0` |

Each base ctor passes the `HASH16` to the shared action ctor `0x1419e9b10` (→ `0x1419e5460`, vtable `0x14323e928`),
installs its base vtable and `0x14323e408` at `+0xa8`; the class ctor runs the base and then installs the class
vtable (and `0x14323e408` again). Second tables: maze `+0x318` `0x14337c858`, `+0x320` `0x14337bdf8`; meteor
`+0x318` `0x14337b358`; beam bomb `+0x318` `0x14337dae0`, `+0x320` `0x143587af0` then `0x14337da80`. `OrionBeamBomb`
has no base ctor: its class ctor calls `0x1419e9b10` with `0x3489` itself and swaps `0x1435885c8` for `0x1433801b8`.

Every class ctor (and the first pass's `0x141985ce0`) begins the same way: when its flag is 0 it asks the actor for
properties `0x36c`, `0x14c` and `0x35a` in turn and, on the first non-zero answer, builds a shared class instead
(`0x1419e5860`, `0x1419e5900`, `0x1419e59b0`; vtables `0x1434c2db8`, `0x1434b9af0`, `0x1434c2350`). Only when all three
are 0, or the flag is set, does it allocate its own size and build the `Orion*` class. The `0x1ab` path passes flag 0.

### The collectors' art rows

The collectors fill a resource list: `MiscAnimated` by `HASH32` (`0x141e52b30`, key lookup `0x14228bed0`),
`MiscObjects` by `ObjectHASH32` (`0x141e53330`), metadata paths (`0x141e53420`) and `GroundEffectTypes` rows
(`0x141e544a0`). All but BeamSweep's start with the shared `0x1419e6d50` (the actor's own effect components).

| collector | rows (`HASH32`) |
|---|---|
| BeamSweep | `MiscAnimated` 3619 `AtlasExileOrionNukeBeamImpact` `nuke_beam_blast.ao` (`0xb2637bb2`), 3679 `AtlasExileOrionPermanentGroundDamage` `damage_NukeBeam.ao` (`0xce94536b`), 3699 `OrionGlobalHitEffects` `Hit_FX.ao` (`0x1fbc41d`) |
| Meteor | `GroundEffectTypes` 17 `OrionMeteor`; `MiscAnimated` 3622 `AtlasExileOrionMeteorRain` `Meteor_Rain.ao` (`0x7f254406`), 3623 `AtlasExileOrionMeteorImpact` `impact_Large.ao` (`0xa56ea289`), 3714/3715 `AtlasExileOrionMeteorPermaDamage1/2` `damage_MeteorRain_01/02.ao`, 3699 |
| CorridorBlast | `MiscObjects` 352/353 `AtlasExileOrionBlocking1/2` (`OrionBlockingWall1/2`, `0x6ff62d8a` / `0x837d318`); `Metadata/Projectiles/AtlasOrionCorridorBlast`; `MiscAnimated` 3814 `AtlasExileOrionCorridorProjectileExplode` `maze_meteor_impact.ao` (`0xa2ce450a`), 3699 |
| CircleMazeBlast | `MiscAnimated` 3779/3780/3781 `AtlasExileOrionMazeMeteor1Ring/2Rings/3Rings` `meteor_100/150/200.ao`, 3690 `AtlasExileOrionMazeMeteor` `maze_meteor.ao` (`0x29d35ddc`), 3691 `...MazeMeteorImpact` `maze_meteor_impact.ao`, 3734 `...CircleMazeMeteorMarker` `maze_meteor_incoming.ao` (`0xa0b5ffea`), 3777 `...TeleportFrom` `warp_down.ao`, 3778 `...TeleportTo` `maze_warp.ao`, 3733 `...CircleMazeGateway` `ExitHere.ao`; `MiscObjects` 354/355 `AtlasExileOrionBlockingExplode1/2` |
| BeamBomb | `GroundEffectTypes` 17; object types `Metadata/Monsters/AtlasExiles/OrionArenaObjects/OrionBeamBombClone` and `.../OrionBeamBomb` by path; `MiscAnimated` 3774 `AtlasExileOrionBeamBombExplosion` `maze_meteor_impact.ao` (`0xd12f4149`), 3699 |

`GroundEffectTypes` 17 `OrionMeteor` names `BuffDefinitions` 1449 `atlas_orion_meteor_ground` and 232; `GroundEffects`
159 (type 17) is `destruction_assets/meteors/meteor_ground.ao`, `loop`, ending `fadeout`. The BeamSweep and BeamBomb
collectors also push two row indices each (`0xe3`/`0xe4` and `0xef`/`0xf0`) into a table of 0x30-byte rows
(`0x14230aa10`, pushed with `0x141e52fb0`).

## The geometry art chain

A geometry action does not name its art; it looks it up:

`GrantedEffects.SkillID` → client stat 6433 `skill_behaviour_variation` (wire id `0x1921`) → the `GeometryAttack` row
with that `Id` → its `Animation` and `HitEffect` (`MiscAnimated` rows) → `MiscAnimated.AOFile`.

| effects | `SkillID` = `GeometryAttack` | `Animation` → art | `HitEffect` |
|---|---|---|---|
| 7008 | 332 | 3667 `AtlasExileOrionSpammableBeam` `spam_beam.ao` | 3701 `Hit_FX_beam.ao` |
| 7012 | 333 | 3668 `...SpammableBeamFinal` `spam_beam_final.ao` | 3701 |
| 7019 / 7020 / 7021 | 342 / 343 / 344 | 3702-3704 `TripleBeam/triple_beam_01/02/03.ao` | |
| 7022-7024, 7025-7027, 7028-7030 | 345-347, 348-350, 351-353 | 3705-3713, the same with `_ice`, `_fire`, `_lightning` | |
| 7013 / 7014 | 344 | 3704 `triple_beam_03.ao` | |
| 7031 | 359 | 3729 `AtlasExileOrionCloneBeam` `clone_trap_beam.ao` | none |
| 7074-7081 (storm shockwaves) | 395 | 3820 `AtlasExileDeathZoneShockwave` `deathZoneShock.ao` | |
| 10941, 10967-10972 (sweeps), 10942 | 1 | none | none |

`MiscAnimated` 7622-7633 `DieBeamRing{Physical,Cold,Fire,Lightning}{1,2,3}`
(`Microtransactions/Rings/die_beam_ring/triple_beam_0N*.ao`) are a microtransaction ring's copy of the triple beams
(only the sound differs), not Sirus's art.

`Data/GeometryAttack.dat` (`data/geometryattack.datc64`) holds 1360 rows of 305 bytes plus a variable section and no
strings; `Animation`/`HitEffect` are `MiscAnimated` row numbers. `spam_beam.ao`, `triple_beam_0N.ao` and
`clone_trap_beam.ao` all carry the bone group `beam_length false jntBnd_beam aux_beam` (the first two also
`beam_flash false jntBnd_beam aux_circle2`), so the client stretches them; `spam_beam.ao` and `triple_beam_0N.ao` share
the rig `impact_beams/rig.amd` and mesh `impact_beams/spambeam.sm` and differ in material, default animation and
particles. None of them has a `MiscBeams` row; the Orion `MiscBeams` rows are 227 `AtlasExileOrionFusionBeam` and 239
`...BeamBombChargeBeam` (`fusion_beam.ao`), 228 `AtlasExileOrionNukeBeam` (`fusioned_nuke_beam.ao`) and 240
`AtlasExileOrionBeamBombBeam` (`orb_beam.ao`, `__col2` 700).

## The ordinary beam (7008 / 7012)

**Class `GeometrySpell`** (`ActionTypes` 807, `HASH16` 52470 `0xccf6`; `ActiveSkills` 1242 `geometry_spell`).
Factory `0x14128cd10` (the same property check), size 0x4a8. Ctor `0x141514b10`: shared ctor with `0xccf6`, reads
stat 6433 from the stat block (`0x141940aa0`) and hands it to `0x141967050` for the sub-object at `+0x318`; installs
`0x14353bc00`, then the main vtable `0x143444e98`, `+0xa8` `0x14323e408`, `+0x318` `0x143446c70`.

- `0x141967050` loads `Data/GeometryAttack.dat` and finds the row whose `Id` is the stat's value; with no row the
  sub-object has no geometry.
- The class builds a 0x248-byte Effect from the fixed resource `Metadata/Effects/Effect` on the caster and keeps the
  handle at `+0x378`. Its start slots are `0x141512d30` (no packet) and `0x141513080` (reads a flag byte: when set, two
  resource strings to `+0x358`/`+0x360` and two u32 to `+0x368`/`+0x36c`; when 0, the skill data through
  `0x141dc0bc0`). Both first fill the Effect with `MiscAnimated` `0x7a39632a` (row 2169 `IncursionNovasoulMovingCone`,
  `moving_cone.ao`) as the default.
- The sub-object's vtable slot 14 (`0x141514ea0`, string `GetHitEffectOrientationReference`) picks a random
  `HitEffect` entry and its orientation (modes 3-8: ContactPoint, random, target, InFront, RNG, SelfOnce).

The clips draw no beam: `beam_01`/`beam_02` have no particle or trail entries in `AtlasExile5.ao` (only voice and swing
sounds, on `R_wrist_jntBnd` / `L_wrist_jntBnd`), and the `.act` has no effect clause for them. With no endpoints the
live client draws only `spam_beam.ao`'s ring at the hand (`beam_flash`), no beam body.

## The barrage (8098 / 8099)

**Class `EmptyActionSpell`** (`ActionTypes` 732, `HASH16` 18594 `0x48a2`; `ActiveSkills` 1130). Factory
`0x1419bf0b0`, size 0x348, base ctor `0x1419cf930`, vtable `0x1434bbfc8`. `EmptyActionSpell2/3/5` (`0x1419c6500`,
`0x1419c66a0`, `0x1419c6840`) build the name `"EmptyActionSpell"` + a number at run time, hash it, and use the same
base ctor and vtable: one class, several type numbers.

- Start (`0x1419cfab0`) appends `"_start"` to the name at `+0x330` and plays it by name (`0x1419cfcf0` on `+0x318`).
- Serialise / deserialise (`0x1419cf9b0` / `0x1419cfa20`): one byte at `+0x1d8`.
- No slot of its own creates an Effect or an object.

The picture comes from the `.act`'s effect clause, `on_act/beam_03/on_act_beam_03.ao`: an empty `SkinMesh`, bone groups
`curve_01..04`, `orbEmit`, `grd_Emit`, `blast`; `orbEmit.pet` at 0.617 s, `blast.pet` at 0.831 / 1.094 / 1.363 s
(next to `beam_03`'s three `*_beam_blast` events); `on_act.trl` on `curve_01..04` from 0.093 to 1.967 s, two segments
(`ground_bits.mat`, max 800 units; `chargeup_trail.mat`, max 1000 units, scale 8.32). The three blast positions are
all about 1.8 cells in front of the body.

## The apparition's sweep (10941, 10967-10972)

**Class `OrionBeamSweep`** (card above; `ActiveSkills` 1569). The class adds three handles at `+0x340`/`+0x348`/`+0x350`
(released by slot 4 `0x141419830`, kept by the copy ctor `0x141416e20`, which copies `+0x358`/`+0x35c`/`+0x360`).
Its per-frame slot 36 (`0x141417120`), while the state at `+0x318` is 1, adds `dt` to `+0x360` and builds an effect
(`0x141e643b0`) from the id at `+0x358` on the actor's owner, attached as two child effects (`0x141e671e0`).

The base class's slots:

| slot | address | does |
|---|---|---|
| 31 | `0x141d1ad80` | start: reads stat 349 (`skill_art_variation`) to pick the length L: 0 → 70, 1 → 23, 2 → 46, 3 → 69 cells (`0x1436ae80c`, `0x1436ae608`, `0x1436ae730`, `0x1436ae808`); re-anchors at caster + direction × L |
| 35 | `0x141d1aff0` | per frame, fixed 0.1 s step |
| 40 | `0x141d1af20` | animation events: `stop_beam` (`0xa32e`) → state 2/4, `blast` (`0x8a6a`) → state 3 |
| 86 | `0x141d1b100` | writes 24 bytes: state, t1, t2, posX, posY, rate |
| 153 | `0x141d1af10` | sets the state at `+0x318` to 1 |
| 157 | `0x141d1b230` | every 0.1 s solves the two arm tips (`0x141d1b6e0`): the action's position turned about the caster by ±75° × (1 − elapsed / rate) (`0x1436ae828` = 75.0, `0x1436ada7c` = π/180) |
| 161 | `0x141d1bc40` | the sweep after `blast`: radius = stat / 100 × 35 |

Slot 153 is the only writer of state 1 and has no direct caller.

**The daemons.** `double_beam_hands_01`'s `daemon1` / `daemon2` events (model `(∓559.0, −160.5, 0)`, 11.2 cells apart)
and `blast` (`(0, −753.9, 0)`, 7.5 cells ahead) carry no data. `GrantedEffects` 8614/8615
`AtlasExileBoss5BeamTargetDaemon1/2` (`SkillID` 53/54, `ActiveSkills` 1174 `move_daemon`, no animation) drive
`MonsterVarieties` 8387/8388 `AtlasExiles5BeamTargetDaemon(2)` (`InvisibleFire.ao`, `NoAi.ais`); `MoveDaemon` rows 52/53
(Id 53/54) name those varieties with `__col5` +90 / −90. **Class `MoveDaemon`** (`ActionTypes` 758, `HASH16` 35726
`0x8b8e`): factory `0x1412d6a80`, size 0x328, vtable `0x14356dc78`; it reads `Data/MoveDaemon.dat` by stat 6433 into
`+0x318`, serialises one byte, and its slot `0x141cbdff0` only moves and turns the daemon by the offset at `+0x1f0`
(X negated when the entity's mirror flag is set). The daemons' `.ot` files have no client block and their
`Animated` model is `Metadata/Monsters/Daemon/Daemon.ao`. The apparition's sweeps have `SkillID` 1, whose
`GeometryAttack` row names no art.

## The clones (10147, 8100 / 8101, 7031)

8100/8101 use **`EmptyActionSpell2`** (`ActionTypes` 811, `HASH16` 7582 `0x1d9e`), the barrage's class: it plays the
clip and draws nothing. The client's own signal is `identify` at 3742 ms (`Trap_Beam_Signal.epk` and `flash_beam.ao`,
[sirus.md](sirus.md#the-object-types)). The six `clone_beam_blast` events have no hook in the `.ot` and no `.act`
effect clause.

`GrantedEffects` 10147 `AtlasExilesOrionCreateClones` is a `GeometryTrigger` with `SkillID` 101; `GeometryTrigger`
Id 101 names `GrantedEffects` 9851 and has no `GroundMarker`.

The clone model is `AtlasExile5_BeamClone.ao` (13 clips, `default_animation` `trap_beam_01`, the body's rig, mesh
`TheOrionExile.sm`, no beam bone group); `OrionBeamBombClone.ot` animates it, plays
`teleporting/orion_teleport_movement.ao` and three packs on spawn, and `Delete()`s after `fade_out`.
`atlasexile5influence.ao` carries the same six `clone_beam_blast` times in its `clone_beam_start_01`.

The clones' beam is 7031 → `GeometryAttack` 359 → `MiscAnimated` 3729 `clone_trap_beam.ao`: rig
`clone_trap_beam/rig.amd`, `default_animation` `clonetrap_beam`, mesh `clonebeam.sm`, bone group `beam_length`,
particle `clone_trap_beam_length.pet` on it, sound `TripleBeam3`, screen shake 13 / 0.4 s. The apparition's model is
`AtlasExile5Apparition.ao`.

## The corridor (10948)

**Class `OrionCorridorBlast`** (card above; `ActiveSkills` 1571). Base slots:

| slot | address | does |
|---|---|---|
| 31 | `0x141d1df90` | start: when the target cell at `+0x318`/`+0x31c` is non-zero it is kept; otherwise it steps 0 to 2π by 10° and takes the first cell the room check (`0x141d888f0` ≥ 3) allows within 60 cells of the target (squared distance < 3600); sets `+0x334` = 1 |
| 40 | `0x141d1e3d0` | animation events: `0xa37a` `create_corridor` → slot 158, `0xc1a4` `beam_left` → slot 161 side 0, `0xdbed` `beam_right` → slot 161 side 1, `0xd9cf` `finish` → slot 160; others to the base `0x1419e9d30` |
| 158 | `0x141d1e460` | direction `+0x324`/`+0x328` = origin − boss position; length `+0x320` = \|direction\| + 75 cells; timer `+0x330` = 0.3 s |
| 157 | `0x141d1e280` | per frame: when the timer runs out, a segment query (`0x141d22fc0`, half width 1.0) from the boss to the corridor's end; a hit calls slot 159 with 32 |
| 161 | `0x141d1ea00` | a segment query (`0x141d235b0`); a hit calls slot 159 with 33 (left) / 34 (right) and derives a child action at the corridor origin |
| 159 | `0x141d1e8f0` | sends the damage record (`0x141ea0c60`) once, guarded by `+0x32e` |
| 160 | `0x141d1e9f0` | sets `+0x32e` only |

No slot creates an Effect or an object, and `beam_barrage_01` has no `.act` effect clause. The corridor's projectile is
`Projectiles` 857 `Metadata/Projectiles/AtlasOrionCorridorBlast` (`death_projectile/death_projectile.ao`, loop
`proj`, impact `impact`, speed 800). `MiscAnimated` 3692 `AtlasExileOrionCorridorNuke` is `corridor_slash_01.ao`.
`corridor_nuke.ao` has no `MiscAnimated` row.

## The maze (10949-10953)

**Class `OrionCircleMazeBlast`** (card above; `ActiveSkills` 1572).

- **Preparation `0x141d1c420`** (the duration code is at `0x141d1c4a0`): reads client stat 10845
  `orion_circle_maze_number_of_circles` into `+0x378` (N), and sets `+0x348` = (client stat 360
  `skill_effect_duration` + N × client stat 3250 `secondary_skill_effect_duration`) / 1000 s.
  The centre `+0x370`/`+0x374` is the position of the entity named `apparition` (the global
  `AtlasExile5ApparitionDetached.ot` sets), or the action's `+0x130` when there is none.
- **One ring `0x141d1d060`:** picks the art by N: 1 → 3779, 2 → 3780, 3 → 3781, any other N → 3690
  `maze_meteor.ao`; builds it at the centre through `0x141f6a910` and keeps the handle at `+0x380` (only the last).
- **States** `+0x328`, advanced 0 → 5 by `0x141d1d4e0`, run per frame by `0x141d1cb40` (timer `0x141d1cfa0`):

| state | does |
|---|---|
| 0 | waits 0.5 s (`+0x34c` accumulates) |
| 1 | builds a ring every 0.2 s (`+0x330`) until N are built (`+0x338`) |
| 2 | builds the warning object (`MiscAnimated` 3734, `0xa0b5ffea`) at the centre with radius 20 × N and state `meteor_incoming`; queries a square of half width 20 × N + 5 − 1 (5 is the int at `0x143a6ae58`); leaves when 1.0 s or less is left |
| 3 | clones itself with `+0x351` = 1 (its own centre) and builds a ring for the clone |
| 4 | interval 0.15 s; releases the warning object (`+0x388`) |
| 5 | sets bit `0x10` of `+0x122`; releases the last ring |

- `0x141d1d430` (can start) refuses while a nearby action is an `OrionBeamBomb` (`0x3489`).
- Serialisation (`0x141d1d940` / `0x141d1dab0`): u32 state, u8 flag (`+0x351`), u32 duration, u32 elapsed, then
  u32 x, y of the centre when the flag is set (the reader then builds a ring at once), then the shared entity block
  (`0x141dc09a0` / `0x141dc0bc0`), and a second one in states 2 and 3.
- Clone `0x141d1be20`; cleanup `0x141d1c640` releases `+0x380`/`+0x388`.

## The meteor (10942-10946)

**Class `OrionMeteor`** (card above; `ActiveSkills` 1570, used by all of 10942-10946). Members: `+0x130`/`+0x134` the
landing cell, `+0x320` "position given" byte, `+0x328` the meteor's handle, `+0x330` a callback.

| slot | address | does |
|---|---|---|
| 87 | `0x14141a3f0` | reads u8 → `+0x320`; when non-zero, i32 x, y (cells) and builds the meteor at once, then the shared block |
| 86 | `0x141d1f720` | the writer (the same layout) |
| 153 | `0x141d1f6c0` | when no position was given, clones itself onto the target and lets the copy build |
| 157 | `0x14141aa00` → `0x141d1f330` | builds `MiscAnimated` `0x7f254406` (3622 `Meteor_Rain.ao`) at the landing cell through `0x141f6a910`, one per action; orientation 0 when stat 349 is non-zero, else π/2 − jitter (±0.25 rad) + atan(300 / distance) |
| 158 | `0x14141ab20` | hit effect `0x1fbc41d` (3699 `Hit_FX.ao`) |
| 159 | `0x14141a660` | radius r = 20 × (client stat 5521 `total_skill_area_of_effect_radius_+%_including_final` + 100) / 100 cells, truncated; collects entities in landing cell ± (r + 5 − 1) (`0x1400f0cf0`, kind 23; `0x141d23ba0`), pairs each with the action's damage entry (`0x14193f690`), settles (`0x14193f8c0`), then builds the crater `0xa56ea289` (3623 `impact_Large.ao`) scaled to r |
| 38 | `0x141d1f220` | compares entity distances to the landing cell |

The listener table at `+0x318` (`0x14337b358`) calls slot 159 from its slot 9 (`0x141d1f630`) when animation event
`0x9b8d` arrives. `Meteor_Rain.ao` (rig `destruction/meteor_rain/rig.amd`, clip `meteor_01`) holds the whole meteor:
`MeteorWarning` at 0, `meteor_incoming.pet` at 0, `meteor_wind.pet` / `meteor_head.pet` at 0.35 s, `MeteorWhoosh`
at 1.54 s, contact 1.92 s, `meteor_grd.pet` at 1.92 s, trail `meteor_trl.trl` 0.35-1.95 s, screen shake at 1.90 s.

**The triggers.** 10141, 10145, 10146, 10956 and 10957 use `ActiveSkills` 1449 `geometry_trigger` (**class
`GeometryTrigger`**, `ActionTypes` 959, `HASH16` 10221 `0x27ed`; factory `0x1412d1e00`, size 0x3c8, vtable
`0x1434480e8` after `0x14353a728`). The factory reads `Data/GeometryTrigger.dat` by stat 6433; start `0x141516100`
builds an Effect from a random `GroundMarker` of that row with a random yaw, end `0x1415159e0` plays `end` and fires
event 56827. When its deserialiser gets a position it spawns `MiscAnimated` `0xe89e6877` (2827
`CascadingTriangleSlamEffect`, `triangleSlam.ao`), scaled 32 × stat / 100 cells.

| effect | `SkillID` = `GeometryTrigger` Id | that row's `GrantedEffect` | `GroundMarker` | `__col3` / `__col4` |
|---|---|---|---|---|
| 10141 `AtlasExilesOrionMeteorArena` | 91 | 10945 | empty | 60 / 10000 |
| 10145 `AtlasExilesOrionMeteorRectangle` | 100 | 10942 | empty | 20 / 2000 |
| 10146 `...MeteorRectangleInfluence` | 103 | 10943 | empty | 20 / 2000 |
| 10956 `AtlasExilesOrionCreateMeteorsSmall` | 134 | 10946 | empty | 12 / 800 |
| 10957 `...CreateMeteorsSmallDaemon` | 139 | 10946 | empty | 8 / 800 |

Only 38 of the 490 rows have a `GroundMarker`; none of these does.

## The beam bomb (10973 / 10974)

**Class `OrionBeamBomb`** (card above; `ActiveSkills` 1574). Members: `+0x328` state (0/2/3/4), `+0x32c` a count of
nearby entities, `+0x360` an animation clock, `+0x368` the main countdown, `+0x370` an object handle, `+0x380` a
second countdown, `+0x384` the rotation angle.

| slot | address | does |
|---|---|---|
| 31 | `0x141d18c90` | stores the entity's animation clock; counts nearby entities whose 16-bit tag is `0x9b8d` into `+0x32c` |
| 35 | `0x141d19020` | the per-frame state machine |
| 41 | `0x141d18da0` | 2 → 3: computes the beam's target and orientation (`0x141d19910`) and sends event 29 to the entity at `+0x128` (`0x141ea0c60`) |
| 74 | `0x141d191b0` | releases `+0x370` |
| 86 / 87 | `0x141d1a520` / `0x141d1a780` | serialise / deserialise |
| 138 | `0x141d18af0` | clone (0x390 bytes) |
| 153 | `0x141d18ea0` | start |
| 159 / 162 | `0x141d19710` / `0x141d1a130` | the two cycle actions |
| 160 / 161 | `0x141d19bc0` / `0x141d19f10` | wrap-up / intermediate phase |

- State 2 (charge) counts `+0x368` down, then calls slot 159 every frame; with no tagged entity nearby it ends at once.
- State 4 (rotate) adds 0.17453292 rad (10°) a second to `+0x384`; when `+0x380` runs out it runs slot 162 and
  reloads `+0x380` from `+0x368`; at or below 0 it sets bit `0x10` of `+0x122` and ends.
- No slot creates an Effect or an object; `+0x370` is only released.

`OrionBeamBomb.ot` animates `Orion/beam_core.ao` (bone groups `root`/`aux_line`, `beam_core/idle.pet` +
`start_once.pet`, `Core.loop.ogg`, all at 0, no contact), plays `idle` after `start` and deletes itself after `end`;
`no_serialise`, `object_size 0`, not blocking. The debuff is `BuffDefinitions` 1525 `orion_beam_bomb_slow`
(`BuffLimit` 8, visual 1268). `clone_beam_start_01` carries four `on_act/beam_03/cloneBeam.trl` trails (arms 0.144 to
0.52 s, hands 0.658 to 1.38 s); the Uber clip has none.

## The storms (6833-6837, 7074-7081)

The auras use `ActiveSkills` 1116 → **`AddBuffToTarget`** (`ActionTypes` 720, `HASH16` 28130 `0x6de2`): factory
`0x14198e350`, size 0x318, vtable `0x1434cb088`, no slot of its own. It reads stat 12731 (play speed) and plays one
animation on the caster through the base start `0x1419e9840`; it creates no object and selects no targets. The
shockwaves 7074-7081 are `geometry_spell` (`GrantedEffects` `SkillID` 395 → `deathZoneShock.ao`, above). The storm
itself is the monster's `OrionDeathZone.ao` ([sirus.md](sirus.md#the-storms)).

## Still open

- Which branch a Sirus actor takes in the class ctors: whether it answers 0 to properties `0x36c`, `0x14c` and `0x35a`
  (if not, one of the three shared classes runs and the `Orion*` class only preloads). Logging the vtable of the
  action `0x141ef0050` creates settles it (`0x14337c8e0` for the sweep).
- What the actor's animation is during an `Orion*` action, given that no `.act` names those types.
- Event `0x9b8d`, which the meteor's listener and the beam bomb's count wait for: it matches no event name in the
  monster or character `.ao` files and no stat.
- The beams' endpoints: what drives a geometry Effect's `beam_length` (the animated object maps `beam_length` to
  `+0x22c` and `radius` to `+0x228` in `0x141dcc410`, and copies `+0x290` into `+0x22c` each frame in `0x141e37db0`;
  the writer of `+0x290` is not found).
- What draws the apparition's two beams and the beam bomb's four (the daemons' `Daemon.ao` is not extracted; no
  `Orion*` slot creates an object).
- The maze: who writes the byte at `+0x350` that state 2's exit also reads, and whether its rings block movement.
- The beam bomb's 3 → 4 transition and its serialised layout; the meaning of the 0x30-byte table rows the collectors
  push.

## Sources

- the code: the 3.29.3c release executable, read with Ghidra (read-only project) and `pefile` + `capstone`
  ([tooling](../re/tooling.md));
- tables (`client.db`): `ActionTypes` (`HASH16`), `ActiveSkills`, `GrantedEffects` (`SkillID`), `GeometryAttack`,
  `GeometryTrigger`, `MiscAnimated` (`HASH32`), `MiscObjects` (`ObjectHASH32`), `MiscBeams`, `GroundEffectTypes`,
  `GroundEffects`, `Projectiles`, `BuffDefinitions`, `MoveDaemon`, `Stats`.
