# Sirus, Awakener of Worlds

Sirus (`MonsterVarieties` 8376, `Metadata/Monsters/AtlasExiles/AtlasExile5`) and his arena, `WorldAreas` 1700
`AtlasExilesBoss5` "Eye of the Storm". This page records what the client's tables and metadata say about the fight:
the arena, the forms, the kit, the clips and their events, the storms and the mods. It is the reference for
`Modules/Core/Game/Monster/Sirus*.cs` and `SirusFight.cs`. The client's code side (the action classes behind his
skills and the rows that pick his art) is [sirus-client.md](sirus-client.md).

The client calls him **Orion**: the metadata is `metadata/monsters/atlasexiles/atlasexile5*`, the effects live under
`Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/`. Table rows are 0-based (`_row`).

## The arena

`WorldAreas` 1700: `Name` "Eye of the Storm", `Act` 11, `AreaLevel` 84, `IsMapArea` 1,
`Bosses_MonsterVarietiesKeys` **[8377, 8376]** (the throne first), `Monsters_MonsterVarietiesKeys` empty,
`TopologiesKeys` [2505], `ModsKeys` [12663, 22720, 7490], `ParentTown_WorldAreasKey` 167, `EndQuestFlags` [59],
`QuestState` 1911, `EnvironmentsKey` 77, `MaxLevel` 100. No `MonsterPacks` row names a Sirus variety.

The arena's objects are metadata of their own; several are `server_only` (no client object exists unless one is
spawned):

| path (archive, lower case) | extends | what it is |
|---|---|---|
| `metadata/monsters/atlasexiles/arenamechanics/atlasexile5portalbase.ot` | `nothing` | the portal: `InteractionAction`, `Animated` on `.../portal/unstable_RED/AtlasExile5Portal.ao`, shared state `animation` (0 opening, 1 opened, 2 closing) |
| `.../arenamechanics/atlasexile5portal1..6.ot` | `AtlasExile5PortalBase` | the six portals out, each with a `...portal1..6destination.ot` |
| `.../arenamechanics/atlasexile5portalforboss.ot` | `nothing` | the boss's own portal (same art and state) |
| `.../orionarenaobjects/orionarenamarker.ot` | `nothing` | the arena middle: `server_only`, `Animated` on `Orion/Orion_Arena.ao` |
| `.../orionarenaobjects/orionentrancemarker.ot` | `nothing` | `server_only` + `no_serialise`, `object_size` 0, `blocking` false |
| `metadata/miscellaneousobjects/arenamiddle.ot` | `nothing` | the generic marker: `server_only`, no components |
| `metadata/npc/epilogue/atlasexile5npctrigger.ot` | `nothing` | `server_only` + `no_serialise`: the epilogue NPC's trigger cell |

The rest of `orionarenaobjects/`: `orionpath1..4`, `orionblockingwall1/2` and `orionblockingwallexpode1/2` (the walls
that close and then explode), `orionbeambomb` / `orionbeambombclone`, `orioncloneobject`, `oriongeyserobject`,
`oriondoodad` / `oriondoodad_nodestroy`, `oriathciviliannoblood`, `orionapparitionhideout`.

`Orion_Arena.ao`'s art is the boundary band: `Orion/Arena/orion_arena.pet` is an infinite point emitter of the
`arenaedge.fmt` mesh at `scale 1214.29` on the `line` bone group, so it runs round the arena's edge.

`MonsterVarieties.AISFile` is empty for all five Sirus rows, and the archive holds no `.ais` file at all.

## The forms

| row | Id | effects | life × | damage × | attack spd | move | size | life bar | mods |
|---|---|---|---|---|---|---|---|---|---|
| 8376 | `AtlasExile5` | 36 | 2400 | 150 | 1500 | 40 | 4 | yes | 4134, 4686, 23585, 32441, 33065 (+7475 special) |
| 8377 | `AtlasExile5Throne` | **0** | 2400 | 150 | 1500 | 4 | 0 | no | 4134, 4686, 23585, 32441 (+7475) |
| 8378 | `AtlasExile5Apparition` | 20 | 2400 | 150 | 1995 | 40 | 4 | no | the body's + **4121** |
| 8379 | `AtlasExile5ApparitionDetached` | 18 | 2400 | 150 | 1995 | 40 | 4 | no | 4134, 4686, 23585, 32441 (+7475) |
| 8380 | `AtlasExile5Intruder` | 1 (11770) | 2400 | **70** | 1995 | 40 | 4 | no | none |

All five carry `AilmentThreshold` 504 and `MonsterCategory` 5. The body and the throne share `BaseMonsterTypeIndex`
`Metadata/Monsters/AtlasExiles/AtlasExile5`, while their `AOFiles`/`ACTFiles` name the throne's pair
(`AtlasExile5Throne.ao` / `.act`). The throne has no `GrantedEffectsKeys` (it cannot cast) and no `BossHealthBar`.

## The kit

`GrantedEffectsKeys` in the client's order. The numbers come from `GrantedEffects` → `ActiveSkills` → `ActionTypes`,
and from `StatSet1` → the `GrantedEffectStatSetsPerLevel` row with the greatest `PlayerLevelReq` ≤ 84, whose
`GemLevel` selects the stats. At 84 the ordinary skills are on `GemLevel` 4 and the Uber ones on `GemLevel` 1
(7013/7014 from the `PlayerLevelReq` 85 row; 10974 and the sweeps 10970-10972 from a row of their own at 84).

`cast` is `GrantedEffects.CastTime` in ms (the wind-up, not the whole action), `set` is `StatSet1`, `animation` the
clip keyword ([next section](#the-clips)). `phys` is `spell_minimum/maximum_base_physical_damage`; the percentages are
the `skill_physical_damage_%_to_convert_to_*` shares; `/min` is `base_*_damage_to_deal_per_minute` for each of
physical, fire, lightning and chaos.

#### Body (row 8376)

| # | effect | Id | action type | animation | cast | set | numbers at 84 |
|---|---|---|---|---|---|---|---|
| 0 | 7008 | `AtlasExileOrionSpammableBeamBlast` | GeometrySpell | `SpellProjectile` | 1500 | 7013 | phys 1446–2169, fire 30%/ligh 30%, area |
| 1 | 7012 | `AtlasExileOrionSpammableBeamBlastFinal` | GeometrySpell | `SpellProjectile` | 1350 | 7017 | as 7008 |
| 2 | 10948 | `AtlasExileOrionCorridorBlast` | OrionCorridorBlast | `SpellAreaOfEffect` | 7500 | 10909 | phys 1643–2464, fire/ligh/chao 25% each, area, projectile |
| 3 | 10949 | `AtlasExileOrionCircleMazeBlast` | OrionCircleMazeBlast | `SpellBuffFire` | 1500 | 10910 | phys 10215–15323, fire/ligh/chao 25%, duration 2000, secondary 1000, 1 circle |
| 4 | 6809 | `TeleportOrion` | Teleport | `Teleport` | 1000 | 6817 | — |
| 5 | 8098 | `EmptyActionSpellOrionMultiBeam` | EmptyActionSpell | `SpellProjectileFire` | 2000 | 8090 | — |
| 6-8 | 7019-7021 | `AtlasExileOrionSpammableBeamPhysical1..3` | GeometrySpell | — | 1000 | 7024-7026 | phys 1526–2289, corrupted blood 3 stacks 5000 ms |
| 9-11 | 7022-7024 | `...SpammableBeamCold1..3` | GeometrySpell | — | 1000 | 7027-7029 | phys 997–1495, cold 75%, always freeze, chill +200% |
| 12-14 | 7025-7027 | `...SpammableBeamFire1..3` | GeometrySpell | — | 1000 | 7030-7032 | phys 997–1495, fire 75%, always ignite, ignite damage +200% |
| 15-17 | 7028-7030 | `...SpammableBeamLightning1..3` | GeometrySpell | — | 1000 | 7033-7035 | phys 997–1495, ligh 75%, always shock, shock duration +200% |
| 18 | 7031 | `AtlasExileOrionCloneBeam` | GeometrySpell | — | 1000 | 7036 | as the lightning beams |
| 19 | 9851 | `AtlasExileOrionSpawnClones` | SpawnObject | — | 1000 | 9835 | — |
| 20 | 10147 | `AtlasExilesOrionCreateClones` | GeometryTrigger | `SpellAreaOfEffectChaos` | 2000 | 10132 | — |
| 21 | 8100 | `EmptyActionSpellOrionCloneBeam` | EmptyActionSpell2 | `SpellAreaOfEffectChaos` | 5330 | 8092 | — |
| 22 | 10950 | `AtlasExileOrionCircleMazeBlast2` | OrionCircleMazeBlast | `SpellBuffFire` | 1500 | 10911 | phys 11919–17878, duration 4000, secondary 1000, 2 circles |
| 23 | 10951 | `...CircleMazeBlast3` | OrionCircleMazeBlast | `SpellBuffFire` | 1500 | 10912 | phys 13620–20431, duration 6000, secondary 1000, 3 circles |
| 24 | 10952 | `...CircleMazeBlast4` | OrionCircleMazeBlast | `SpellBuffFire` | 1500 | 10913 | as 10951, 4 circles |
| 25 | 10953 | `...CircleMazeBlast5` | OrionCircleMazeBlast | `SpellBuffFire` | 1500 | 10914 | as 10951, 5 circles |
| 26 | 10973 | `AtlasExileOrionBeamBomb` | OrionBeamBomb | `SpellProjectileUpwardFire` | 3000 | 10934 | phys 228–342, 11673/min, fire/chao 25%, duration 13000, secondary 5000 |
| 27 | 9690 | `AtlasExilesOrionGeometrySpellOrionDoodadObliterations` | ExecuteGEAL | — | 1000 | 9675 | — |
| 28 | 10141 | `AtlasExilesOrionMeteorArena` | GeometryTrigger | `SpellAreaOfEffect` | 2000 | 10126 | — |
| 29 | 10945 | `AtlasOrionMeteorGroundEffect` | OrionMeteor | — | 1000 | 10906 | phys 1709–2563, 65850/min, fire/chao 25%, infinite duration |
| 30 | 8099 | `EmptyActionSpellOrionMultiBeam2` | EmptyActionSpell | `SpellProjectileCold` | 2500 | 8091 | — |
| 31 | 10991 | `AtlasOrionTeleportToCentre` | Teleport | `Teleport` | 1000 | 10950 | — |
| 32 | 8101 | `EmptyActionSpellOrionCloneBeamUber` | EmptyActionSpell2 | `SpellProjectileUpwardChaos` | 5330 | 8093 | — |
| 33 | 10974 | `AtlasExileOrionBeamBombUber` | OrionBeamBomb | `SpellBuffChaos` | 3000 | 10935 | phys 342–512, 17509/min, fire/chao 25%, duration 13000, secondary 5000 |
| 34 | 7013 | `AtlasExileOrionSpammableBeamBlastUber` | GeometrySpell | `SpellProjectileLightning` | 1500 | 7018 | phys 1520–2279, fire 30%/ligh 30%, corrupted blood 1 stack 5000 ms, area +50% |
| 35 | 7014 | `...SpammableBeamBlastUberFinal` | GeometrySpell | `SpellProjectileLightning` | 1350 | 7019 | as 7013 |

The maze rows' durations are `base_skill_effect_duration` and `base_secondary_skill_effect_duration`; the number of
circles is `orion_circle_maze_number_of_circles`.

#### Apparition (`AtlasExile5ApparitionDetached`, row 8379)

| # | effect | Id | action type | animation | cast | set | numbers at 84 |
|---|---|---|---|---|---|---|---|
| 0 | 5905 | `AtlasOrionFaceLastTarget` | FaceLastTarget | — | 10 | 5912 | — |
| 1 | 10945 | `AtlasOrionMeteorGroundEffect` | OrionMeteor | — | 1000 | 10906 | as the body's |
| 2-4 | 10967-10969 | `AtlasOrionBeamSweepNoFinalExplosion{Short,Medium,Long}` | OrionBeamSweep | `DoubleStrike` | 4200 | 10928-10930 | phys 241–362, fire/ligh/chao 25% |
| 5 | 10954 | `AtlasExilesOrionCreateGeysersSmall` | GeometryTrigger | `SpellAreaOfEffectFire` | 3500 | 10915 | — |
| 6 | 9849 | `AtlasExileOrionSpawnGeysers` | SpawnObject | — | 1000 | 9833 | — |
| 7 | 7032 | `AtlasExileOrionGeyserImpact` | GeometrySpell | — | 1000 | 7037 | phys 289–434, fire 30%/ligh 30% |
| 8 | 7033 | `AtlasExileOrionGeyserImpactInitial` | GeometrySpell | — | 1000 | 7038 | as 7032 |
| 9 | 9685 | `AtlasExilesOrionGeyserWaveSmall` | ExecuteGEAL | — | 1000 | 9670 | — |
| 10 | 9687 | `AtlasExilesOrionGeyserWaveDoodadsSmall` | ExecuteGEAL | — | 1000 | 9672 | — |
| 11 | 10956 | `AtlasExilesOrionCreateMeteorsSmall` | GeometryTrigger | `SpellAreaOfEffectLightning` | 2170 | 10917 | — |
| 12 | 10946 | `AtlasOrionMeteorGroundEffectTemp` | OrionMeteor | — | 1000 | 10907 | phys 8–11, 158/min, fire/chao 25%, duration 6000 |
| 13 | 6832 | `AtlasOrionApparitionShield` | AddBuffToTarget | `SpellBuffChaos` | 2000 | 6839 | duration 10000, `base_cannot_be_damaged` |
| 14-16 | 10970-10972 | `AtlasOrionBeamSweepNoFinalExplosion{...}Uber` | OrionBeamSweep | `HeavyStrike` | 4200 | 10931-10933 | phys 570–855, fire/ligh/chao 25% |
| 17 | 3629 | `SSMOrionApparitionSummonDeathZone` | SummonSpecificMonsters | `SpellAreaOfEffectLightning` | 1000 | 3638 | summons 1 |

The effects with no numbers deal no damage of their own. The meteor arena's damage is not on 10141 but on the ground
effect 10945: `infinite_skill_effect_duration` and 65,850 a minute of each element (1,097.5 a second each).

`AtlasOrionBeamSweep` (10941, `HeavyStrike`, cast 4200, set 10902) is only in row 8378's list, not in 8379's.
`GrantedEffects` 10942-10946 (`AtlasOrionMeteor`, `...Influence`, `...InfluenceUber`, `...GroundEffect`,
`...GroundEffectTemp`) all use `ActiveSkills` 1570 `orion_meteor`.

## The clips

`GrantedEffects.Animation` is a row of the `Animation` table, and `Animation.Id` is the keyword the `.act` binds a clip
to. The five `Orion*` action types appear in no Sirus `.act`; the animation keyword does:

| effect | animation | body `.act` → clip (stance 1) |
|---|---|---|
| 7008 / 7012 | `SpellProjectile` | `beam_01` then `beam_02` |
| 7013 / 7014 | `SpellProjectileLightning` | `beam_uber_01` then `beam_uber_02` |
| 8098 | `SpellProjectileFire` | `beam_03` + effect `on_act/beam_03/on_act_beam_03.ao` |
| 8099 | `SpellProjectileCold` | `beam_uber_03` + the same effect |
| 10948, 10141 | `SpellAreaOfEffect` | `beam_barrage_01` |
| 10949-10953 | `SpellBuffFire` | `meteor_maze_start_01` (`SpellBuffCold` = `meteor_maze_01_hold`, `SpellBuffLightning` = `meteor_maze_end_01`) |
| 10147 / 8100 | `SpellAreaOfEffectChaos` | `trap_beam_01` |
| 8101 | `SpellProjectileUpwardChaos` | `trap_beam_uber_01` |
| 10973 | `SpellProjectileUpwardFire` | `clone_beam_start_01` |
| 10974 | `SpellBuffChaos` | `clone_beam_start_uber_01` |
| 6809 / 10991 | `Teleport` | `teleport_01` |
| 10967-10969 (apparition) | `DoubleStrike` | `double_beam_hands_small_01` |
| 10970-10972 (apparition) | `HeavyStrike` | `double_beam_hands_small_uber_01` |
| 10954 / 10956, 3629 | `SpellAreaOfEffectFire` / `...Lightning` | `Geyser_small_01` / `meteor_shower_small_01` |
| 6832 | `SpellBuffChaos` | `shield_01` |

The effects with an empty `Animation` (7019-7031, 7032/7033, 10945/10946, 9851, 9690, 5905) play no cast clip.
`GrantedEffects` 7009 `AtlasExileOrionSpammableBeamBlastRepeated` (not in the kit; `geometry_spell`, cast 2000,
`SkillID` 332 like 7008) also uses `SpellProjectileFire`.

`AtlasExile5.act` binds 42 actions. Besides the rows above: Idle/Run `idle_01`, TakeHit `flinch_large_01`, Death
`death_01` + `Orion_onAct_death.ao`, Taunt `@Idle`, **Emerge** `throne_deactivate_01` + `onAct_throneDeactivate.ao`,
**Emerge2** `throne_activate_01`, **ChangeToStance2** `final_throne_deactivate_01` + `onAct_throneDeactivate.ao`,
`SpellAreaOfEffectFire/Cold/Lightning` `beam_barrage_abort_01/02/03`, `SpellProjectileChaos`
`standalone_beam_barrage_01`, `SpellProjectileUpwardCold` `clone_beam_01`, `SpellProjectileUpwardLightning`
`clone_beam_end_01`. Then one `stance stance2 { … }` block replaces each with its `final_*` twin (including
`SpellBuffLightning = final_meteor_maze_end_01` + `onAct_meteor_slam.ao`, and both `SpellProjectileFire` and
`SpellProjectileCold` = `final_beam_03`). Stance 1 is the default set, so `ChangeToStance2` is the last phase; the
`ChangeToStance1..5Immediate` action types (`ActionTypes` 575-584) are the unanimated swaps. `clone_beam_01` and
`clone_beam_end_01` exist in no `.ao`.

`AtlasExile5Throne.act` binds Idle/Run `idle_01`, TakeHit `emerge_01`, Death `submerge_01`, Taunt `@Idle`, Emerge
`throne_deactivate_01`, Emerge2 `throne_activate_01`. Both acts carry `movement_speed 435` and `action_set monster`.

`AtlasExile5ApparitionDetached.act` binds Idle `idle_01`, Run `idle_throne_01`, TakeHit `flinch_large_01`, Death
`death_01`, Taunt `@Idle`, Emerge `emerge_01`, Emerge2 `throne_deactivate_01`, SpellAreaOfEffect `idleonce_01`,
DoubleStrike `double_beam_hands_small_01`, HeavyStrike `double_beam_hands_small_uber_01`, SpellAreaOfEffectFire
`Geyser_small_01`, SpellAreaOfEffectLightning `meteor_shower_small_01`, SpellBuffChaos `shield_01`.
`AtlasExile5Apparition.act` (row 8378, the same `AtlasExile5Apparition.ao`) adds `Emerge3`, `ChangeToStance2/3`,
`stance2`/`stance3`, and binds HeavyStrike `double_beam_hands_01`, SpellAreaOfEffect `carpet_bomb_01`,
SpellAreaOfEffectFire `Geyser_01`.

The `.ao` also carries `view_of` remaps that let one clip serve two skills: `beam_01/02 → beam_uber_01/02`,
`beam_03 → beam_uber_03`, `trap_beam_01 → trap_beam_uber_01`, `clone_beam_start_01 → clone_beam_start_uber_01`,
`final_beam_barrage_01 → standalone_beam_barrage_01` (excluding its `StandardEvent`s).

### Clip lengths and events

The body's rig is `Art/Models/MONSTERS/AtlasExiles/TheOrion/Character/rig.amd` (version 5, 50 animations); the events
are the `contact` keys and the `StandardEvent`s of `Metadata/Monsters/AtlasExiles/AtlasExile5.ao` (its
`Atlasexile5Influence` twin carries the same numbers). Milliseconds.

| clip | length | events |
|---|---|---|
| `awaken_01` / `awaken_beam_01` / `awaken_exit_01` | 5000 / 1500 / 1500 | — / contact 703 / 1050 |
| `beam_01` / `final_beam_01` | 1500 | contact 672 in both (not scaled by 7012's 1350 ms cast) |
| `beam_02` | 1500 | contact 692 |
| `beam_03` | 2000 | `identify` 400, `small_beam_blast` 841, `medium_beam_blast` 1098, `large_beam_blast` 1370 |
| `final_beam_03` | | `start_face` 0, `identify` 400, small 841, 991, medium 1125, 1264, large 1389, 1516, `end_face` 1526 |
| `beam_barrage_01` / `final_beam_barrage_01` | 7500 | `teleportstart` 100, `teleportend` 400, `create_corridor` 461, `addhandglowepk` 945, `removehandglowepk` 1425, `beam_right` 1173, `beam_left` 1825, right 2485, left 3156, right 3818, left 4497, right 5167, left 5812, `finish` 6536 |
| `trap_beam_01` / `trap_beam_uber_01` | 5333 | `start_face` 0, `dialogue` 2000, `identify` 3742 (uber `end_face` 4694), `clone_beam_blast` 4141, 4282, 4388, 4500, 4597, 4722 |
| `clone_beam_start_01` | 1500 | contact 500, 567, 639, 728, 1450; `teleport` 500 |
| `clone_beam_start_uber_01` | | contact 830, 943 |
| `meteor_maze_start_01` / `final_...` | 1500 | `killminions` 801, 1145, 1452 / 754, 1110, 1500 |
| `meteor_maze_01` | 3500 | `create_maze` 461, contact 2006 |
| `meteor_maze_01_hold` / `meteor_maze_end_01` | 2000 / 1500 | — |
| `teleport_01` / `final_teleport_01` | 500 | contact 250 |
| `throne_deactivate_01` / `final_throne_deactivate_01` | 2000 / 4000 | `disintegrate_throne` 140 (`throne_deactivate_01`) |
| `throne_activate_01` | 8000 | teleport 502, finish 1003, `create_deathzone` 2344, flashbang 4540 |
| `death_01` | 6500 | — |
| `flinch_large_01` / `idle_01` | 1000 / 3000 | — |

`GrantedEffects` 10948's `CastTime` 7500 outlasts `beam_barrage_01`'s `finish` (6536). The `beam_right` hand sits at
model `(-60, 0, -104.29)`, `beam_left` at `(60, 0, -100)` (100 model units = 1 cell). The six `clone_beam_blast`
positions are x 0, y -2.96, z -211.6 to -223.0.

The apparition (`AtlasExile5Apparition.ao`): `meteor_shower_01` / `_small_01` contact 240, `shield_01` 580,
`double_beam_hands_01` 910 (`daemon1`, `daemon2` and contact at 910, `stop_beam` 2729, `blast` 3431),
`double_beam_hands_small_01` 1010, `Geyser_01` 1140 / `Geyser_small_01` 1146, and `carpet_bomb_01` with
twenty-eight contacts from 973 to 1867.

The clip index is the clip's position in the actor's `ClientAnimationController` list (0-based); S→C `0x1b2`
(`AnimationUpdate`) kind 0 plays a clip by this index and ignores an index past the actor's count. Apparition: 0
`Geyser_01`, 1 `Geyser_small_01`, 2 `carpet_bomb_01`, 3 `double_beam_hands_01`, 4 `double_beam_hands_small_01`, 5
`meteor_shower_01`, 6 `meteor_shower_small_01`, 7 `double_beam_hands_small_uber_01`, 8 `final_throne_deactivate_01`, 9
`shield_01`, 10 `throne_activate_01`. Body: 1 `beam_01`, 3 `beam_03`, 7 `clone_beam_start_01`, 10 `final_beam_01`, 11
`final_beam_02`, 12 `final_beam_03`, 13 `final_beam_barrage_01`, 20 `final_meteor_maze_start_01`, 23
`meteor_maze_01`, 28 `trap_beam_01`.

The effect objects carry almost no events of their own: `nuke_beam_blast.ao` has `blast_damage` / `attach_damage` at
0 ms, `doom_geyser.ao` `geyser_explode` contacts at 100/200/300/400/485 ms, and `corridor_nuke.ao`,
`death_carpeting.ao`, `death_carpeting_wave.ao`, `clone_trap_beam.ao`, `maze_meteor.ao`, `meteor/meteor_150.ao`,
`spam_beam.ao`, `blocking_01/02.ao`, `maze_warp.ao` and `orion_arena.ao` have none. `Meteor_Rain.ao` is the
exception (contact 1920, [sirus-client](sirus-client.md#the-meteor-10942-10946)).

## The object types

`AtlasExile5.ot` extends `Metadata/Monsters/Monster` and `Metadata/Monsters/BossLifeBar`:

- `Actor`: `basic_action` Emerge, Emerge2, ChangeToStance2; on Emerge, ChangeToStance2 and Emerge2's teleport event it
  runs `MoveToObject(the_throne, force, …)`.
- `StateMachine`: `define_shared_state = "throne = 1; rocks; cloak; eyes; x_pos; y_pos; z_pos; point_light;
  ready_for_clone_teleport; apparition_merged; norendering=1;"`: eleven shared states, no phase machine. He starts
  on the throne and invisible.
- `Positioned { force_placement = true }`, `Stats { set_monster_uses_map_boss_difficulty_scaling = 1 }`,
  `Life { on_death = PlayInterlude('Audio/Music/AtlasRework/CrusaderResolve', 0.5) }`.

The client block reacts to the states:

| state | the client does |
|---|---|
| `throne = 1` / 0 | the throne object plays `Throne_Appear.epk` / `Throne_Disappear.epk` |
| `norendering` | `DisableRendering` / `EnableRendering` |
| `rocks = 1` | attaches the six `AtlasExile5*Rocks.ao` and `AttachApply.epk`; at 0 hides, then detaches after 1 s |
| `cloak = 1` | `AtlasExile5Cape_NoChurch.ao` + `AtlasExile5Robe.ao`, `Cloak_Appear.epk` |
| `eyes` | `OrionEyes.epk` on / off |
| `point_light` | `Orion_Light.ao` on `aux_light_attach`, animated `on`/`idle_on` or `off`/`idle_off` |
| `apparition_merged = 1` | `Orion_Final.epk` |

`Animated` hangs `OrionDeath.ao` + `death.epk` on the death clip and `OrionDeathEnergy.ao` on its energy-orb event.
The teleport art is client `Functions`: `start_teleport` / `do_teleport` / `finish_teleport` (`Orion_MoveOut.epk`,
`Orion_MoveIn.epk`, `Orion_teleport_movement.ao`) and the `*_clone_teleport` twins (`Orion_MoveOutFull` /
`MoveInFull`), driven by `on_start_Teleport` / `on_event_contact_for_Teleport` / `on_end_Teleport` and by the
`clone_beam_start_01` and `beam_barrage_01` animations. Other hooks: `on_event_identify_for_EmptyActionSpell2` adds
`body_fx/Trap_Beam_Signal.epk` and plays `body_fx/flash_beam.ao` (`on_end_EmptyActionSpell2` removes the pack);
`on_event_addhandglowepk` / `removehandglowepk` add and remove `on_act/beam_barrage_01/handGlow.epk`. Nothing handles
`create_deathzone`.

`Preload` names the four `EnvironmentEmbers*.ao`, the ground effect `OrionMeteor`, the buffs `corrupted_blood`,
`atlas_orion_immunity`, `atlas_orion_death_zone`, `atlas_orion_death_zone_core`, `dz_debuff.ao`, `Orion_Arena.ao` and
the buff visual `orion_beam_bomb_slow`.

`AtlasExile5Throne.ot` extends `Monster` only: `basic_action` FollowPath / Emerge / Emerge2, shared states
`human = 1; visible = 1; point_light = 1`, `object_size 0`, `blocking = false`, `set_phase_through_objects`, and
`on_construction_complete = { globals.the_throne = this; }`, the global the body's `MoveToObject` uses. Its
`human = 1` attaches `AtlasExile5OnThrone.ao` (the seated Sirus) to `throne_jntBnd` with `Throne_FX.epk`; the body's
`throne` state only drives the throne's appear/disappear packs.

`AtlasExile5ApparitionDetached.ot` extends `Monster`: shared states `beaming; x_pos; y_pos; z_pos`,
`Positioned { force_placement = true; blocking = false }`, `Pathfinding { immobile = true }`, an empty `ControlZone`,
`on_construction_complete` sets `globals.apparition = this` and adds `orion_apparition_Power_01.epk`, and `Animated`
plays `apparitionSurge.ao` on the hip for `double_beam_hands_small_01`, `Geyser_small_01` and `meteor_shower_small_01`.
`apparitionSurge.ao` is a particle (`surgeEmit.pet`) with an empty `SkinMesh`.

## The storms

Four varieties on object types under `Metadata/Monsters/AtlasExiles/ArenaMechanics/`, all animating
`OrionArenaObjects/OrionDeathZone.ao`:

| row | Id | name | object type | effects | mods |
|---|---|---|---|---|---|
| 3282 | `InvisibleFireOrionDeathZone` | Creeping Fire | `AtlasExile5DeathZoneMobile` | 6833, 6834, 10981, 10982, 7074-7081 | — |
| 3283 | `InvisibleFireOrionDeathZoneStationary` | Creeping Fire | `AtlasExile5DeathZone` | 6833, 6834, 10981, 10982, 4858 | — |
| 3284 | `InvisibleFireOrionDeathZoneApparition` | Creeping Fire | `AtlasExile5DeathZoneApparition` | 6833, 6835, 7074-7081 | — |
| 3285 | `InvisibleFireOrionDeathZoneSupporter` | Sirus Storm | `AtlasExile5DeathZoneMobileSupporter` | 6836, 6837, 10983, 10984, 7074-7081 | 37405 |

Every storm object type has `base_cannot_be_damaged`, `base_cannot_be_stunned`, `cannot_be_knocked_back`,
`untargetable_by_monster_ai`, `immune_to_curses`, `immune_to_auras_from_other_entities`, `set_item_drop_slots = 0`,
`corpse_usable = false`, `Targetable { no_highlight = true; targetable = false }` and
`Positioned { collision_hull_type = "projectile"; blocking = false; locked_orientation = true }` (the Supporter's
hull is `"walk"`). Mobile, apparition and supporter add `base_actor_scale_+% = -80`; the stationary one does not. The
Supporter's `ModelSizeMultiplier` is 50. All four carry an empty `DiesAfterTime {}`. The stationary storm has no
shockwaves: its fifth effect is `MonsterWait` (4858, `base_skill_effect_duration` 5000). `OrionDeathZone.act`:
`Idle = "idle"`, `Taunt`/`Run` `@Idle`, `Death = "idle"`, `movement_speed 10.0`, `action_set minion`.

`OrionDeathZone.ao` (rig `death_zone/rig.amd`, mesh `deathzone.sm`) plays `cloud.pet`, `death_core.pet`,
`lightning_circle.pet`, `grd_rocks.pet` and `grd_shockwaves.pet` at 0 and the sounds `DeathCloud.loop`,
`EarthCrumble.loop` and `DeathCloudStart.once`. The effect twin `Orion/deathzone.ao` has the same particles and no
sound.

The damage is an aura on the player (`ActiveSkills` 1116 `add_buff_to_target_triggered`, `action_speed_-%` 25 on
every aura):

| effect | who | each element per minute | per second |
|---|---|---|---|
| 6833 `AtlasOrionDeathZoneAura` | storm rim (mobile, stationary) | 35,476 | 591 |
| 6834 `AtlasOrionDeathZoneAuraCore` | the mobile storm's core | 354,762 | 5,913 |
| 6835 `AtlasOrionDeathZoneAuraCoreApparition` | the apparition storm's core | 570,087 | 9,501 |
| 10981 / 10982 | the minion storm's rim / core | 4,967 / 53,214 | 83 / 887 |
| 6836 / 6837, 10983 / 10984 | the Supporter's rim / core | as 6833 / 6834 | |

7074-7081 (`AtlasOrionDeathZoneShockwave` … `Shockwave8`, `geometry_spell`) are the eight shockwaves:
`global_knockback`, `knockback_distance_+%` 100, `base_knockback_speed_+%` 100, and
`active_skill_area_of_effect_radius_+%_final` 0, 50, 100 … 350, one step each. The Supporter's mod 37405 is
`NecropolisImmuneToUnionOfSouls`.

`SSMOrionApparitionSummonDeathZone` (3629): `number_of_monsters_to_summon` 1, `alternate_minion` 611,
`summon_specific_monsters_radius_+%` -99, `summoned_monsters_are_minions`, `minion_dies_when_parent_dies`,
`minion_dies_when_parent_deleted`, `monster_no_drops_or_experience`. `MonsterVarieties` 611 is a Bonewarped Archer, so
611 does not name the storm variety.

The arena room `fountain.arm` places six `InvisibleFireOrionDeathZoneStationary` at (375,463) (513,223) (374,273)
(223,350) (181,538) (536,441).

## The mods

| mod | Id | its stats |
|---|---|---|
| 4134 | `MonsterMapBoss` | `monster_slain_experience_+%` 100, `monster_dropped_item_rarity_+%` 15000 |
| 4686 | `CannotBeSlowedBelowValueBosses` | `hellscape_boots_action_speed_+%_minimum_value` -50 |
| 23585 | `StunRepeatImmunityMapBossSuper` | `cannot_be_stunned_while_stunned` 1, `cannot_be_stunned_for_ms_after_stun_finished` 4000 |
| 32441 | `PinnacleAtlasBoss` | none, only the tag `pinnacle_boss` |
| 33065 | `MonsterDroppedItemsConqueror` | `dropped_items_have_random_conqueror_influence` 1, `monster_dropped_item_quantity_+%_final_for_influenced_monster` -50 |
| 7475 (special) | `ImmuneToKnockback` | `cannot_be_knocked_back` 1 |
| 4121 | `MonsterGauntletRuinEnabled` | `monster_gauntlet_ruin_enabled` 1 (row 8378 only) |

The area's `ModsKeys` (12663, 22720, 7490) are area mods, not monster mods.

## Art scale and walls

Each effect model's `*.smd.header` starts with its bounding box (`[0,0,0, minX, minY, minZ, maxX, maxY, maxZ, …]`,
padded 0.5):

| model | X | Y | Z |
|---|---|---|---|
| `arena/arena_ring` | ±850.5 | ±850.5 | −425…0 |
| `death_zone/rig` (the storm) | ±1500.5 | ±1500.5 | −458.8…11.9 |
| `blocking_slams/beam_cannon/rig` (the corridor beam) | ±107.5 | −950…−7.4 | ±107.2 |
| `blocking_slams/meteor/rig` | ±750.5 | ±750.5 | −1000…0 |
| `destruction/meteor_rain/meteor_ground/rig` | ±250.5 | ±250.5 | −120…0 |
| `environment/rig` | ±1250.5 | ±1250.5 | −500…0 |
| `corridor_slash_01/slash` | ±0.27 | −93.1…217.2 | −279.3…0 |

The `.ao` files carry no `radius`/`scale`/`size`/`length` key; the spawn scale belongs to whoever creates the object.
Where the art stretches, a bone group does it: `corridor_nuke.ao` binds `beam_length false jntBnd_beam aux_beam`,
`blocking_02.ao`'s `Hull` gives one wall's footprint (about 17.9 × 9.0 units), and `OrionBlockingWall1.ot` states
`object_size = 1`.

- **The maze's rings are moving boxes.** `meteor_{100,150,200}.ao` are meteors that fly and emit boxes as they go
  (`boxEmit.pet`, an `AxisAlignedBoxEmitter` with a stretch curve on the `aux_box_01..03` bones; nine boxes for 150 and
  200), not a drawn arc.
- **The wall object.** `OrionBlockingWall2`'s `TriggerableBlockage` closes the cells under the model's footprint in
  the client (`0x141d8ad70`), so the footprint shrinks with the scale. Its `blocking_02.ao` (a ring of broken rock
  with lava in the `crack` bone group's particle) defaults to `wait`, which has no particle events; clip 2, `loop`, is
  the one that draws. The wall carries the state `detonate`.
- **Decals without an animation draw nothing.** `destruction_assets/crater_small_01.ao`, `crater_big_01.ao` and
  `scorch_01..04.ao` are a `FixedMesh` (`destructionLow/Large.fmt`) with one `Append` pass and no
  `AnimationController`; created as effect objects they stay invisible. `meteors/impact_small|medium|large.ao` (a
  `SkinMesh` with `default_animation`) do draw.

## The fight rules from the PoE Wiki (not checked against the client)

The client states the numbers above, not the rules of the fight. The
[PoE Wiki's Sirus page](https://www.poewiki.net/wiki/Sirus) says the following; none of it has been checked against
the client:

- once he reaches a health threshold he **becomes invulnerable and places a Deatomization Storm** at his position, and he
  **spawns a storm every time he reaches 75 %, 50 % or 25 %** life;
- a **safe zone** — a large animated ring around him — pushes the storms out, and standing outside it for too long
  spawns a storm as well;
- the storm phase **ends when the player comes close to him after** the storm is created; he retreats to his throne, and
  the phase takes a number of seconds during which a knockback pushes the player away from the new storm;
- the arena holds several storms; touching one deals **2102-3154** damage (50 % physical, 25 % fire, 25 % chaos) plus
  **548** base physical and **1368** base fire and chaos damage per second;
- the **maze** gains one wall ring per phase, up to three, and every ring has an exit marked by animated flares;
- from the page's version history: he heals to **75 %** of his maximum life at the start of his final phase (3.10.0);
  he teleports away before **Corridor Blast** and takes **80 % less damage** while casting it (3.10.0); he does not cast
  Meteor or Corridor Blast near a storm (3.10.1d), nor while his apparition is using a skill (3.10.1c); he teleports to the
  centre of the **Meteor Maze** when he starts it (3.9.1); the storms do not pursue the player (3.9.2).

## Still open

- The storms' rim and core radii and the shockwaves' base radius: the auras and 7074-7081 state no radius, the
  varieties have `ObjectSize` 0, and the art's rings (`aux_circle1` at 35 units, `aux_circle3` at 23.29) do not
  convert to cells.
- How long a storm lives (`DiesAfterTime {}` is empty) and how `base_actor_scale_+% = -80` combines with the
  Supporter's `ModelSizeMultiplier` 50.
- Which variety 3629 summons (`alternate_minion` 611 resolves to no storm).
- How long the maze's walls stand: `meteor_maze_01` is 3500 ms and no row states a wall lifetime.

## Sources

- tables: `client.db` — `MonsterVarieties` (8376-8380, 3282-3285), `WorldAreas` 1700, `GrantedEffects`,
  `ActiveSkills`, `ActionTypes`, `Animation`, `GrantedEffectStatSets(PerLevel)`, `Stats`, `Mods`, `MiscAnimated`,
  `GeometryAttack`, `GeometryTrigger`;
- metadata: `Client/Content.ggpk`, mirrored in `data/game-files.zip` (`.ot`/`.ao`), plus the archive for the `.act`
  files; UTF-16LE. `scripts/re/ot_components.py` (`rig_clips`, `all_contacts`) reads clip lengths and `contact` keys
  (not `StandardEvent`s). Re-extraction:

  ```bash
  dotnet run --no-build --project scripts/re/ggdump -- Client "Tools/re/out/orion" extract \
    "^metadata/(monsters/atlasexiles/|npc/epilogue/atlasexile5|effects/spells/monsters_effects/atlasexiles/orion/)" \
    "\\.(dds|png|ogg|tga)$" 1
  ```
