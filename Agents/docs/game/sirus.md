# Sirus, Awakener of Worlds

Sirus (`MonsterVarieties 8376` `Metadata/Monsters/AtlasExiles/AtlasExile5`, "Sirus, Awakener of Worlds") and the arena
around him, `WorldAreas 1700` `AtlasExilesBoss5` "Eye of the Storm", area level 84. The client calls him **Orion**: the
metadata is `metadata/monsters/atlasexiles/atlasexile5*`, the effects live under
`Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/`. This page records what the client data says about the
fight and is the reference for `Modules/Core/Game/Monster/Sirus*.cs`.

Everything below is read from the 3.29.3c client: the tables through `data/client.db`, the metadata through the archive.
The commands are under [Sources](#sources).

**Per-skill reference (2026-11-17).** The ten documents in [game/sirus/](sirus/README.md) are the authoritative
account of each ability: the client's action class, its slots, its serialized fields, what it draws and what the server
must send. The skill-level sections below predate that reverse engineering; where they disagree, the per-skill document
wins (the corrections are marked "revised 2026-11-17").

## The arena

`WorldAreas 1700` `AtlasExilesBoss5`: `Name` "Eye of the Storm", `Act` 11, `AreaLevel` 84, `IsMapArea` 1,
`Bosses_MonsterVarietiesKeys` **[8377, 8376]** (the throne first), `Monsters_MonsterVarietiesKeys` empty,
`TopologiesKeys` [2505], `ModsKeys` **[12663, 22720, 7490]**, `ParentTown_WorldAreasKey` 167, `EndQuestFlags` [59],
`QuestState` 1911, `EnvironmentsKey` 77, `MaxLevel` 100. The boss is placed by that row: no `MonsterPacks` row names a
Sirus variety.

The arena's objects are separate metadata, and most of them are `server_only` — real cells the server has to place and
drive, with no client object behind them:

| path (archive, lower case) | extends | what it is |
|---|---|---|
| `metadata/monsters/atlasexiles/arenamechanics/atlasexile5portalbase.ot` | `nothing` | the portal: `InteractionAction`, `Animated` on `Metadata/Effects/Spells/portal/unstable_RED/AtlasExile5Portal.ao`, and the shared state `animation` (0 opening, 1 opened, 2 closing) |
| `.../arenamechanics/atlasexile5portal1..6.ot` | `AtlasExile5PortalBase` | the six portals out of the arena, each with `...portal1..6destination.ot` beside it (the server's `0x2f00 + i` transitions) |
| `.../arenamechanics/atlasexile5portalforboss.ot` | `nothing` | the boss's own portal (same art, same `animation` state) |
| `.../arenamechanics/atlasexile5portalinfluence.ot` | — | the influence variant of the portal |
| `.../orionarenaobjects/orionarenamarker.ot` | `nothing` | **the arena middle**: `server_only`, `Animated` on `Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/Orion_Arena.ao`. This is Sirus's counterpart of `ArenaMiddle` |
| `.../orionarenaobjects/orionentrancemarker.ot` | `nothing` | `server_only` + `no_serialise`, `object_size` 0, `blocking` false — where the player comes in |
| `metadata/miscellaneousobjects/arenamiddle.ot` | `nothing` | the generic marker: `server_only` and no components at all |
| `metadata/npc/epilogue/atlasexile5npctrigger.ot` | `nothing` | `server_only` + `no_serialise`, nothing else: the cell that triggers the epilogue NPC |

The rest of `orionarenaobjects/` is the fight's furniture: `orionpath1..4` (the four paths), `orionblockingwall1/2` and
`orionblockingwallexpode1/2` (the walls that close a corridor and then explode), `orionbeambomb` / `orionbeambombclone`,
`orioncloneobject`, `oriongeyserobject`, `oriondoodad` / `oriondoodad_nodestroy` (the doodads the clone beams erase),
`oriathciviliannoblood`, `orionapparitionhideout`.

`MonsterVarieties.AISFile` is empty for all five Sirus rows, and the archive holds no `.ais` at all (0 of 1,245,702
paths, as the Shaper note records), so his behaviour is not in the game data.

## The forms

| row | Id | effects | life × | damage × | attack spd | move | size | bar | mods |
|---|---|---|---|---|---|---|---|---|---|
| 8376 | `AtlasExile5` | 36 | 2400 | 150 | 1500 | 40 | 4 | yes | 4134, 4686, 23585, 32441, 33065 (+7475 special) |
| 8377 | `AtlasExile5Throne` | **0** | 2400 | 150 | 1500 | 4 | 0 | no | 4134, 4686, 23585, 32441 (+7475) |
| 8378 | `AtlasExile5Apparition` | 20 | 2400 | 150 | 1995 | 40 | 4 | no | the body's + **4121** |
| 8379 | `AtlasExile5ApparitionDetached` | **18** | 2400 | 150 | 1995 | 40 | 4 | no | 4134, 4686, 23585, 32441 (+7475) |
| 8380 | `AtlasExile5Intruder` | 1 (11770) | 2400 | **70** | 1995 | 40 | 4 | no | none |

All five carry `AilmentThreshold` 504 and `MonsterCategory` 5. The body and the throne share `BaseMonsterTypeIndex`
`Metadata/Monsters/AtlasExiles/AtlasExile5` while their `AOFiles`/`ACTFiles` name the throne's own pair
(`AtlasExile5Throne.ao` / `.act`), and `AtlasExile5Throne.ot` is metadata of its own — it extends `Monster` only, not the
body's type. Its one interesting line is `on_construction_complete = { globals.the_throne = this; }`: the body's `Actor`
block reaches the throne through that global (`MoveToObject(the_throne, …)`), so **a server that forgets to spawn the
throne leaves the body's `Emerge` / `ChangeToStance2` / `Emerge2` with nowhere to move to**.

Row 8377 has no `GrantedEffectsKeys` at all: the throne cannot cast. It also has no life bar (`BossHealthBar` absent),
so a server that gives every Sirus actor the bar states hands one to an object that has none.

## The kit

`GrantedEffectsKeys` is the authoritative list **and its order is the skill id**: the server publishes effect `n` as skill
`0x4000 + n`, and `Sirus.Native.SkillBase` / `Skill` / `EffectOf` in `Sirus.Native.cs` are the two directions of that.
The numbers come from `GrantedEffects[e]` → `ActiveSkills` → `ActionTypes`, then `StatSet1` → the
`GrantedEffectStatSetsPerLevel` row of that set with the greatest `PlayerLevelReq` ≤ 84 (level 84 is the arena's), whose
`GemLevel` then selects the row `EffectStats` reads. At 84 the ordinary skills are on `GemLevel` 4 and the Uber ones on
`GemLevel` 1 — 7013/7014 from the `PlayerLevelReq` 85 row, 10974 and the apparition's sweeps 10970-10972 from a
`PlayerLevelReq` 84 row of their own.

`cast` is `GrantedEffects.CastTime` in ms — the wind-up, not the whole action. `set` is that effect's `StatSet1` (a
`GrantedEffectStatSets` row, named after the effect but numbered on its own: effect 7008's set is 7013). `animation` is
the clip keyword (see [next section](#the-clips-the-animation-keyword-not-the-action-type)). `numbers` is the level-84
stat set: `spell_minimum/maximum_base_physical_damage`, the `skill_physical_damage_%_to_convert_to_*` shares, the
`base_*_damage_to_deal_per_minute` degenerations, the ailment flags, durations and the corrupted-blood block.

#### Body: the client's GrantedEffectsKeys, in order

| # | skill id | effect | granted effect | skill | action type | animation | cast | set | numbers at 84 |
|---|---|---|---|---|---|---|---|---|---|
| 0 | `0x4000` | 7008 | `AtlasExileOrionSpammableBeamBlast` | `geometry_spell` | GeometrySpell | `SpellProjectile` | 1500 | 7013 | phys 1446–2169, convert fire 30%/ligh 30%, area |
| 1 | `0x4001` | 7012 | `AtlasExileOrionSpammableBeamBlastFinal` | `geometry_spell` | GeometrySpell | `SpellProjectile` | 1350 | 7017 | phys 1446–2169, convert fire 30%/ligh 30%, area |
| 2 | `0x4002` | 10948 | `AtlasExileOrionCorridorBlast` | `orion_corridor_blast` | OrionCorridorBlast | `SpellAreaOfEffect` | 7500 | 10909 | phys 1643–2464, convert fire 25%/ligh 25%/chao 25%, area, projectile |
| 3 | `0x4003` | 10949 | `AtlasExileOrionCircleMazeBlast` | `orion_circle_maze_blast` | OrionCircleMazeBlast | `SpellBuffFire` | 1500 | 10910 | phys 10215–15323, convert fire 25%/ligh 25%/chao 25%, duration 2000 ms, secondary 1000 ms, 1 circle, area |
| 4 | `0x4004` | 6809 | `TeleportOrion` | `teleport` | Teleport | `Teleport` | 1000 | 6817 | — |
| 5 | `0x4005` | 8098 | `EmptyActionSpellOrionMultiBeam` | `empty_action_spell` | EmptyActionSpell | `SpellProjectileFire` | 2000 | 8090 | — |
| 6 | `0x4006` | 7019 | `AtlasExileOrionSpammableBeamPhysical1` | `geometry_spell` | GeometrySpell | — | 1000 | 7024 | phys 1526–2289, corrupted blood 3 stacks for 5000 ms, area |
| 7 | `0x4007` | 7020 | `AtlasExileOrionSpammableBeamPhysical2` | `geometry_spell` | GeometrySpell | — | 1000 | 7025 | phys 1526–2289, corrupted blood 3 stacks for 5000 ms, area |
| 8 | `0x4008` | 7021 | `AtlasExileOrionSpammableBeamPhysical3` | `geometry_spell` | GeometrySpell | — | 1000 | 7026 | phys 1526–2289, corrupted blood 3 stacks for 5000 ms, area |
| 9 | `0x4009` | 7022 | `AtlasExileOrionSpammableBeamCold1` | `geometry_spell` | GeometrySpell | — | 1000 | 7027 | phys 997–1495, convert cold 75%, always freeze, chill +200%, area |
| 10 | `0x400a` | 7023 | `AtlasExileOrionSpammableBeamCold2` | `geometry_spell` | GeometrySpell | — | 1000 | 7028 | phys 997–1495, convert cold 75%, always freeze, chill +200%, area |
| 11 | `0x400b` | 7024 | `AtlasExileOrionSpammableBeamCold3` | `geometry_spell` | GeometrySpell | — | 1000 | 7029 | phys 997–1495, convert cold 75%, always freeze, chill +200%, area |
| 12 | `0x400c` | 7025 | `AtlasExileOrionSpammableBeamFire1` | `geometry_spell` | GeometrySpell | — | 1000 | 7030 | phys 997–1495, convert fire 75%, always ignite, ignite damage +200%, area |
| 13 | `0x400d` | 7026 | `AtlasExileOrionSpammableBeamFire2` | `geometry_spell` | GeometrySpell | — | 1000 | 7031 | phys 997–1495, convert fire 75%, always ignite, ignite damage +200%, area |
| 14 | `0x400e` | 7027 | `AtlasExileOrionSpammableBeamFire3` | `geometry_spell` | GeometrySpell | — | 1000 | 7032 | phys 997–1495, convert fire 75%, always ignite, ignite damage +200%, area |
| 15 | `0x400f` | 7028 | `AtlasExileOrionSpammableBeamLightning1` | `geometry_spell` | GeometrySpell | — | 1000 | 7033 | phys 997–1495, convert ligh 75%, always shock, shock duration +200%, area |
| 16 | `0x4010` | 7029 | `AtlasExileOrionSpammableBeamLightning2` | `geometry_spell` | GeometrySpell | — | 1000 | 7034 | phys 997–1495, convert ligh 75%, always shock, shock duration +200%, area |
| 17 | `0x4011` | 7030 | `AtlasExileOrionSpammableBeamLightning3` | `geometry_spell` | GeometrySpell | — | 1000 | 7035 | phys 997–1495, convert ligh 75%, always shock, shock duration +200%, area |
| 18 | `0x4012` | 7031 | `AtlasExileOrionCloneBeam` | `geometry_spell` | GeometrySpell | — | 1000 | 7036 | phys 997–1495, convert ligh 75%, always shock, shock duration +200%, area |
| 19 | `0x4013` | 9851 | `AtlasExileOrionSpawnClones` | `spawn_object` | SpawnObject | — | 1000 | 9835 | — |
| 20 | `0x4014` | 10147 | `AtlasExilesOrionCreateClones` | `geometry_trigger` | GeometryTrigger | `SpellAreaOfEffectChaos` | 2000 | 10132 | — |
| 21 | `0x4015` | 8100 | `EmptyActionSpellOrionCloneBeam` | `empty_action_spell_2` | EmptyActionSpell2 | `SpellAreaOfEffectChaos` | 5330 | 8092 | — |
| 22 | `0x4016` | 10950 | `AtlasExileOrionCircleMazeBlast2` | `orion_circle_maze_blast` | OrionCircleMazeBlast | `SpellBuffFire` | 1500 | 10911 | phys 11919–17878, convert fire 25%/ligh 25%/chao 25%, duration 4000 ms, secondary 1000 ms, 2 circles, area |
| 23 | `0x4017` | 10951 | `AtlasExileOrionCircleMazeBlast3` | `orion_circle_maze_blast` | OrionCircleMazeBlast | `SpellBuffFire` | 1500 | 10912 | phys 13620–20431, convert fire 25%/ligh 25%/chao 25%, duration 6000 ms, secondary 1000 ms, 3 circles, area |
| 24 | `0x4018` | 10952 | `AtlasExileOrionCircleMazeBlast4` | `orion_circle_maze_blast` | OrionCircleMazeBlast | `SpellBuffFire` | 1500 | 10913 | phys 13620–20431, convert fire 25%/ligh 25%/chao 25%, duration 6000 ms, secondary 1000 ms, 4 circles, area |
| 25 | `0x4019` | 10953 | `AtlasExileOrionCircleMazeBlast5` | `orion_circle_maze_blast` | OrionCircleMazeBlast | `SpellBuffFire` | 1500 | 10914 | phys 13620–20431, convert fire 25%/ligh 25%/chao 25%, duration 6000 ms, secondary 1000 ms, 5 circles, area |
| 26 | `0x401a` | 10973 | `AtlasExileOrionBeamBomb` | `orion_beam_bomb` | OrionBeamBomb | `SpellProjectileUpwardFire` | 3000 | 10934 | phys 228–342, 11673/min to all four elements, convert fire 25%/chao 25%, duration 13000 ms, secondary 5000 ms, area |
| 27 | `0x401b` | 9690 | `AtlasExilesOrionGeometrySpellOrionDoodadObliterations` | `execute_geal` | ExecuteGEAL | — | 1000 | 9675 | — |
| 28 | `0x401c` | 10141 | `AtlasExilesOrionMeteorArena` | `geometry_trigger` | GeometryTrigger | `SpellAreaOfEffect` | 2000 | 10126 | — |
| 29 | `0x401d` | 10945 | `AtlasOrionMeteorGroundEffect` | `orion_meteor` | OrionMeteor | — | 1000 | 10906 | phys 1709–2563, 65850/min to all four elements, convert fire 25%/chao 25%, infinite duration, area |
| 30 | `0x401e` | 8099 | `EmptyActionSpellOrionMultiBeam2` | `empty_action_spell` | EmptyActionSpell | `SpellProjectileCold` | 2500 | 8091 | — |
| 31 | `0x401f` | 10991 | `AtlasOrionTeleportToCentre` | `teleport` | Teleport | `Teleport` | 1000 | 10950 | — |
| 32 | `0x4020` | 8101 | `EmptyActionSpellOrionCloneBeamUber` | `empty_action_spell_2` | EmptyActionSpell2 | `SpellProjectileUpwardChaos` | 5330 | 8093 | — |
| 33 | `0x4021` | 10974 | `AtlasExileOrionBeamBombUber` | `orion_beam_bomb` | OrionBeamBomb | `SpellBuffChaos` | 3000 | 10935 | phys 342–512, 17509/min to all four elements, convert fire 25%/chao 25%, duration 13000 ms, secondary 5000 ms, area |
| 34 | `0x4022` | 7013 | `AtlasExileOrionSpammableBeamBlastUber` | `geometry_spell` | GeometrySpell | `SpellProjectileLightning` | 1500 | 7018 | phys 1520–2279, convert fire 30%/ligh 30%, corrupted blood 1 stacks for 5000 ms, area +50%, area |
| 35 | `0x4023` | 7014 | `AtlasExileOrionSpammableBeamBlastUberFinal` | `geometry_spell` | GeometrySpell | `SpellProjectileLightning` | 1350 | 7019 | phys 1520–2279, convert fire 30%/ligh 30%, corrupted blood 1 stacks for 5000 ms, area +50%, area |

#### Apparition (`AtlasExile5ApparitionDetached`, row 8379): in order

| # | skill id | effect | granted effect | skill | action type | animation | cast | set | numbers at 84 |
|---|---|---|---|---|---|---|---|---|---|
| 0 | `0x4000` | 5905 | `AtlasOrionFaceLastTarget` | `face_last_target` | FaceLastTarget | — | 10 | 5912 | — |
| 1 | `0x4001` | 10945 | `AtlasOrionMeteorGroundEffect` | `orion_meteor` | OrionMeteor | — | 1000 | 10906 | phys 1709–2563, 65850/min to all four elements, convert fire 25%/chao 25%, infinite duration, area |
| 2 | `0x4002` | 10967 | `AtlasOrionBeamSweepNoFinalExplosionShort` | `orion_beam_sweep` | OrionBeamSweep | `DoubleStrike` | 4200 | 10928 | phys 241–362, convert fire 25%/ligh 25%/chao 25%, area |
| 3 | `0x4003` | 10968 | `AtlasOrionBeamSweepNoFinalExplosionMedium` | `orion_beam_sweep` | OrionBeamSweep | `DoubleStrike` | 4200 | 10929 | phys 241–362, convert fire 25%/ligh 25%/chao 25%, area |
| 4 | `0x4004` | 10969 | `AtlasOrionBeamSweepNoFinalExplosionLong` | `orion_beam_sweep` | OrionBeamSweep | `DoubleStrike` | 4200 | 10930 | phys 241–362, convert fire 25%/ligh 25%/chao 25%, area |
| 5 | `0x4005` | 10954 | `AtlasExilesOrionCreateGeysersSmall` | `geometry_trigger` | GeometryTrigger | `SpellAreaOfEffectFire` | 3500 | 10915 | — |
| 6 | `0x4006` | 9849 | `AtlasExileOrionSpawnGeysers` | `spawn_object` | SpawnObject | — | 1000 | 9833 | — |
| 7 | `0x4007` | 7032 | `AtlasExileOrionGeyserImpact` | `geometry_spell` | GeometrySpell | — | 1000 | 7037 | phys 289–434, convert fire 30%/ligh 30%, area |
| 8 | `0x4008` | 7033 | `AtlasExileOrionGeyserImpactInitial` | `geometry_spell` | GeometrySpell | — | 1000 | 7038 | phys 289–434, convert fire 30%/ligh 30%, area |
| 9 | `0x4009` | 9685 | `AtlasExilesOrionGeyserWaveSmall` | `execute_geal` | ExecuteGEAL | — | 1000 | 9670 | — |
| 10 | `0x400a` | 9687 | `AtlasExilesOrionGeyserWaveDoodadsSmall` | `execute_geal` | ExecuteGEAL | — | 1000 | 9672 | — |
| 11 | `0x400b` | 10956 | `AtlasExilesOrionCreateMeteorsSmall` | `geometry_trigger` | GeometryTrigger | `SpellAreaOfEffectLightning` | 2170 | 10917 | — |
| 12 | `0x400c` | 10946 | `AtlasOrionMeteorGroundEffectTemp` | `orion_meteor` | OrionMeteor | — | 1000 | 10907 | phys 8–11, 158/min to all four elements, convert fire 25%/chao 25%, duration 6000 ms, area |
| 13 | `0x400d` | 6832 | `AtlasOrionApparitionShield` | `add_buff_to_target` | AddBuffToTarget | `SpellBuffChaos` | 2000 | 6839 | duration 10000 ms, cannot be damaged |
| 14 | `0x400e` | 10970 | `AtlasOrionBeamSweepNoFinalExplosionShortUber` | `orion_beam_sweep` | OrionBeamSweep | `HeavyStrike` | 4200 | 10931 | phys 570–855, convert fire 25%/ligh 25%/chao 25%, area |
| 15 | `0x400f` | 10971 | `AtlasOrionBeamSweepNoFinalExplosionMediumUber` | `orion_beam_sweep` | OrionBeamSweep | `HeavyStrike` | 4200 | 10932 | phys 570–855, convert fire 25%/ligh 25%/chao 25%, area |
| 16 | `0x4010` | 10972 | `AtlasOrionBeamSweepNoFinalExplosionLongUber` | `orion_beam_sweep` | OrionBeamSweep | `HeavyStrike` | 4200 | 10933 | phys 570–855, convert fire 25%/ligh 25%/chao 25%, area |
| 17 | `0x4011` | 3629 | `SSMOrionApparitionSummonDeathZone` | `summon_specific_monsters_on_self` | SummonSpecificMonsters | `SpellAreaOfEffectLightning` | 1000 | 3638 | summons 1 |

Ten of the body's effects and seven of the apparition's name no damage at all, and `Sirus.Attack` correctly returns an
empty packet for them: body 6809 (teleport), 8098 / 8099 / 8100 / 8101 (the four `empty_action_spell` channels that exist
only to play a clip), 9851 and 10147 (spawn / create), 9690 (the GEAL that erases doodads), 10141 (the meteor trigger)
and 10991 (teleport to the centre); apparition 5905 (`face_last_target`, a 10 ms turn), 10954 / 9849 / 10956 / 3629 (the
trigger / spawn / summon channels), 9685 and 9687 (GEAL wave). `6832` names damage only through
`base_cannot_be_damaged`: it is the apparition's 10 s shield, not a hit.

The damage the meteor arena really deals is **not** on 10141 (no numbers): it is 10945, the ground effect, which the
trigger leaves behind — `infinite_skill_effect_duration` and `base_{physical,fire,lightning,chaos}_damage_to_deal_per_minute`
65,850 each, i.e. **1,097.5 a second of every element while you stand in it**. 10973 / 10974 (the beam bomb) last 13 s
with `base_*_damage_to_deal_per_minute` 11,673 (194.6/s ×4) and a 5 s secondary.

## The clips: the Animation keyword, not the action type

`GrantedEffects.Animation` is a row of the **`Animation`** table, and `Animation.Id` is the keyword the `.act` binds by.
That is the whole effect → clip mapping, and it is the one thing about this fight the server cannot infer from the action
type: the five Orion action types (`OrionBeamSweep` 1034, `OrionMeteor` 1035, `OrionCorridorBlast` 1036,
`OrionCircleMazeBlast` 1037, `OrionBeamBomb` 1039) appear **nowhere** in any Sirus `.act`. The animation keyword does:

| effect | animation | `.act` binding → clip |
|---|---|---|
| 7008 / 7012 (beam blast) | `SpellProjectile` | `beam_01` then `beam_02` |
| 7013 / 7014 (Uber blast) | `SpellProjectileLightning` | `beam_uber_01` then `beam_uber_02` |
| 8098 (multibeam) | `SpellProjectileFire` | `beam_03` (+ the `on_act_beam_03` effect) |
| 8099 (multibeam 2) | `SpellProjectileCold` | `beam_uber_03` |
| 10948 (corridor blast) | `SpellAreaOfEffect` | `beam_barrage_01` |
| 10949-10953 (circle maze) | `SpellBuffFire` | `meteor_maze_start_01` (the rest of the sequence is `SpellBuffCold` = `meteor_maze_01_hold`, `SpellBuffLightning` = `meteor_maze_end_01`) |
| 10147 / 8100 (create clones / clone beam) | `SpellAreaOfEffectChaos` | `trap_beam_01` |
| 8101 (Uber clone beam) | `SpellProjectileUpwardChaos` | `trap_beam_uber_01` |
| 10973 (beam bomb) | `SpellProjectileUpwardFire` | `clone_beam_start_01` |
| 10974 (Uber beam bomb) | `SpellBuffChaos` | `clone_beam_start_uber_01` |
| 10141 (meteor arena) | `SpellAreaOfEffect` | `beam_barrage_01` |
| 6809 / 10991 (teleport) | `Teleport` | `teleport_01` |
| 10967-10969 (apparition swell) | `DoubleStrike` | `double_beam_hands_small_01` |
| 10970-10972 (Uber swell) | `HeavyStrike` | `double_beam_hands_small_uber_01` |
| 10954 / 10956 / 3629 | `SpellAreaOfEffectFire` / `...Lightning` | `Geyser_small_01` / `meteor_shower_small_01` |
| 6832 (apparition shield) | `SpellBuffChaos` | `shield_01` |

The effects whose `Animation` is empty (7019-7031, 7032/7033, 10945/10946, 9851, 9690, 5905) play no cast clip: they are
the triggered volleys and ground effects the client is told to place, and the server owns their timing.

`AtlasExile5.act` binds **42** actions, exactly as many as the fight needs:

| keyword | clip | | keyword | clip |
|---|---|---|---|---|
| Idle / Run | `idle_01` | | SpellAreaOfEffect | `beam_barrage_01` |
| TakeHit | `flinch_large_01` | | SpellAreaOfEffectFire / Cold / Lightning | `beam_barrage_abort_01/02/03` |
| Death | `death_01` + `Orion_onAct_death.ao` | | SpellProjectileChaos | `standalone_beam_barrage_01` |
| Taunt | `@Idle` | | SpellBuffFire / Cold / Lightning | `meteor_maze_start_01` / `meteor_maze_01_hold` / `meteor_maze_end_01` |
| **Emerge** | `throne_deactivate_01` + `onAct_throneDeactivate.ao` | | SpellAreaOfEffectChaos | `trap_beam_01` |
| **Emerge2** | `throne_activate_01` | | SpellProjectileUpwardChaos | `trap_beam_uber_01` |
| **ChangeToStance2** | `final_throne_deactivate_01` + `onAct_throneDeactivate.ao` | | SpellProjectileUpwardFire | `clone_beam_start_01` |
| SpellProjectile | `beam_01` then `beam_02` | | SpellBuffChaos | `clone_beam_start_uber_01` |
| SpellProjectileLightning | `beam_uber_01` then `beam_uber_02` | | SpellProjectileUpwardCold | `clone_beam_01` |
| SpellProjectileFire / Cold | `beam_03` / `beam_uber_03` (+ `on_act_beam_03`) | | SpellProjectileUpwardLightning | `clone_beam_end_01` |
| Teleport | `teleport_01` | | | |

and then `stance stance2 { … }`, which replaces every one of those with its `final_*` twin (including
`SpellBuffLightning = final_meteor_maze_end_01` + `onAct_meteor_slam.ao`). **There is one stance block, `stance2`**:
stance 1 is the default set, so `ChangeToStance2` is "last phase" and there is no `ChangeToStance1` clip to play.
`ChangeToStance1` and `ChangeToStance1..5Immediate` do exist as `ActionTypes` (rows 575-584), the `…Immediate` ones being
the unanimated swap — which is why the act binds only `ChangeToStance2`.

`AtlasExile5Throne.act` has seven: Idle/Run `idle_01`, TakeHit `emerge_01`, Death `submerge_01`, Taunt `@Idle`, Emerge
`throne_deactivate_01`, Emerge2 `throne_activate_01`. Both body and throne carry `movement_speed 435` and
`action_set monster` — the actor's movement in the action set, which is **not** the variety's `MovementSpeed` (40 / 4).

Contacts, from the object's own animation events (`.ao`); these are when a hit lands:

| clip | contact | | clip | contact |
|---|---|---|---|---|
| `beam_01` / `beam_02` | 0.672 / 0.692 | | `meteor_maze_01` | 2.006 |
| `beam_03` | `identify` 0.400 | | `teleport_01` / `final_teleport_01` | 0.250 |
| `clone_beam_start_01` | 0.500, 0.567, 0.639, 0.728, 1.450 | | `throne_activate_01` | teleport 0.502, finish 1.003, **flashbang 4.540** |
| `clone_beam_start_uber_01` | 0.830, 0.943 | | `trap_beam_01` / `_uber_01` | `identify` 3.742 |
| `beam_barrage_01` | teleportstart 0.100, teleportend 0.400 | | `awaken_beam_01` | 0.703 |

The apparition's contacts: `meteor_shower_01` / `_small_01` **0.240**, `shield_01` **0.580**, `double_beam_hands_01`
**0.910**, `double_beam_hands_small_01` **1.010**, `Geyser_01` **1.140** / `Geyser_small_01` **1.146**, and
**`carpet_bomb_01` fires twenty-eight contacts from 0.973 to 1.867** — that clip is the carpet of beams, one contact per
beam, not one hit.

## The object type

`AtlasExile5.ot` extends **two** bases, `Metadata/Monsters/Monster` and `Metadata/Monsters/BossLifeBar`:

```
Actor { basic_action = "Emerge"; basic_action = "Emerge2"; basic_action = "ChangeToStance2"; … }
StateMachine { define_shared_state = "throne = 1; rocks; cloak; eyes; x_pos; y_pos; z_pos;
                                    point_light; ready_for_clone_teleport; apparition_merged; norendering=1;" }
Positioned { force_placement = true }
Stats { set_monster_uses_map_boss_difficulty_scaling = 1 }
```

So this is **not** a phase machine like the Shaper's thirteen `Transitionable` states: Sirus has eleven **shared
booleans** the server sets and the client reacts to. `throne` starts at 1 and `norendering` at 1 — he begins invisible on
the throne. The client side (`client { StateMachine {…} }`) does the rest:

| state | the client does |
|---|---|
| `throne = 1` | the throne object gets `Throne_Appear.epk` (and `Throne_Disappear.epk` at 0) |
| `norendering` | `DisableRendering` / `EnableRendering` |
| `rocks = 1` | attaches the six `AtlasExile5*Rocks.ao` pieces and `AttachApply.epk`; 0 hides then detaches after 1 s |
| `cloak = 1` | `AtlasExile5Cape_NoChurch.ao` + `AtlasExile5Robe.ao`, `Cloak_Appear.epk` |
| `eyes` | `OrionEyes.epk` on / off |
| `point_light` | `Orion_Light.ao` on `aux_light_attach`, animated `on`/`idle_on` or `off`/`idle_off` |
| `apparition_merged = 1` | `Orion_Final.epk` (the two become one) |

`Actor` also teleports him onto `the_throne` on `Emerge`, `ChangeToStance2` and `Emerge2`'s teleport event
(`MoveToObject(the_throne, force, …)`), and `Animated` hangs `OrionDeath.ao` + `death.epk` on the death clip and
`OrionDeathEnergy.ao` on its energy-orb event. `Life { on_death = { PlayInterlude('Audio/Music/AtlasRework/CrusaderResolve',
0.5); } }`.

`Preload` names what the fight will need, and it is a good checklist of the mechanics: the four `EnvironmentEmbers*.ao`,
the ground effect `OrionMeteor`, the buffs **`corrupted_blood`**, **`atlas_orion_immunity`**,
**`atlas_orion_death_zone`**, **`atlas_orion_death_zone_core`**, `dz_debuff.ao`, `Orion_Arena.ao` and the buff visual
**`orion_beam_bomb_slow`**.

The teleport is a set of client `Functions` rather than one clip: `start_teleport` / `do_teleport` / `finish_teleport`
(`Orion_MoveOut.epk`, `Orion_MoveIn.epk`, `Orion_teleport_movement.ao`) and the `*_clone_teleport` twins
(`Orion_MoveOutFull` / `MoveInFull`). They are driven by `on_start_Teleport` / `on_event_contact_for_Teleport` /
`on_end_Teleport`, and by the `clone_beam_start_01` and `beam_barrage_01` animations — so the teleport art is played by
the client off the clip, and the server only has to move the body.

`AtlasExile5Throne.ot` extends `Monster` only, has `basic_action` FollowPath / Emerge / Emerge2, shared states
`human = 1; visible = 1; point_light = 1`, `object_size 0`, `blocking = false`,
`set_phase_through_objects` and — the important line — `BaseEvents { on_construction_complete = { globals.the_throne = this; } }`.
Its `human = 1` attaches `AtlasExile5OnThrone.ao` to `throne_jntBnd` and `Throne_FX.epk`.

`AtlasExile5ApparitionDetached.ot` extends `Monster`, shared states `beaming; x_pos; y_pos; z_pos`,
`Positioned { force_placement = true; blocking = false }`, **`Pathfinding { immobile = true }`** (the detached apparition
does not walk) and an empty `ControlZone`. `on_construction_complete` sets `globals.apparition = this` and adds
`orion_apparition_Power_01.epk`; its `Animated` block plays `apparitionSurge.ao` on the hip for
`double_beam_hands_small_01`, `Geyser_small_01` and `meteor_shower_small_01`.
`AtlasExile5ApparitionDetached.act` binds thirteen: Idle `idle_01`, Run `idle_throne_01`, TakeHit `flinch_large_01`,
Death `death_01`, Taunt `@Idle`, Emerge `emerge_01`, Emerge2 `throne_deactivate_01`, SpellAreaOfEffect `idleonce_01`,
DoubleStrike `double_beam_hands_small_01`, HeavyStrike `double_beam_hands_small_uber_01`,
SpellAreaOfEffectFire `Geyser_small_01`, SpellAreaOfEffectLightning `meteor_shower_small_01`, SpellBuffChaos `shield_01`.
The **non**-detached `AtlasExile5Apparition.act` (row 8378, 20 effects, `AOFiles` the same
`AtlasExile5Apparition.ao`) is the richer rig: it adds `Emerge3`, `ChangeToStance2/3`, `stance2`/`stance3`, and binds
HeavyStrike `double_beam_hands_01`, SpellAreaOfEffect `carpet_bomb_01`, SpellAreaOfEffectFire `Geyser_01`.
Row 8378 also carries `MonsterGauntletRuinEnabled` (4121) — the Gauntlet arena's ruin counter.

## The storms

Four varieties, all called "Creeping Fire" except the last, all on object types under
`Metadata/Monsters/AtlasExiles/ArenaMechanics/` and all animating `OrionArenaObjects/OrionDeathZone.ao`:

| row | Id | name | object type | effects | mods |
|---|---|---|---|---|---|
| 3282 | `InvisibleFireOrionDeathZone` | Creeping Fire | `AtlasExile5DeathZoneMobile` | 6833, 6834, 10981, 10982, 7074-7081 | — |
| 3283 | `InvisibleFireOrionDeathZoneStationary` | Creeping Fire | `AtlasExile5DeathZone` | 6833, 6834, 10981, 10982, 4858 | — |
| 3284 | `InvisibleFireOrionDeathZoneApparition` | Creeping Fire | `AtlasExile5DeathZoneApparition` | 6833, **6835**, 7074-7081 | — |
| 3285 | `InvisibleFireOrionDeathZoneSupporter` | **Sirus Storm** | `AtlasExile5DeathZoneMobileSupporter` | 6836, 6837, 10983, 10984, 7074-7081 | **37405** |

Every storm object type is untouchable by construction: `base_cannot_be_damaged`, `base_cannot_be_stunned`,
`cannot_be_knocked_back`, `untargetable_by_monster_ai`, `immune_to_curses`, `immune_to_auras_from_other_entities`,
`set_item_drop_slots = 0`, `corpse_usable = false`, `Targetable { no_highlight = true; targetable = false }`, and
`Positioned { collision_hull_type = "projectile"; blocking = false; locked_orientation = true }` (the Supporter states the
knockback immunity itself; mobile, apparition and supporter add `base_actor_scale_+% = -80`, the stationary one does not).
All four carry **`DiesAfterTime {}`** — empty, so the data does not say how long a storm lives. The stationary storm has
**no shockwaves at all**: its fifth effect is `MonsterWait` (4858, `base_skill_effect_duration` 5000).

The damage is an aura the storm puts on the player (`add_buff_to_target_triggered`, `action_speed_-%` 25), reported as
`base_{physical,fire,lightning,chaos}_damage_to_deal_per_minute`:

| effect | who | per minute | per second |
|---|---|---|---|
| 6833 `AtlasOrionDeathZoneAura` | storm rim (mobile, stationary) | 35,476 each | **591 ×4** |
| 6834 `AtlasOrionDeathZoneAuraCore` | the mobile storm's core | 354,762 each | **5,913 ×4** |
| 6835 `AtlasOrionDeathZoneAuraCoreApparition` | the apparition storm's core | 570,087 each | **9,501 ×4** |
| 10981 / 10982 | the minion storm's rim / core | 4,967 / 53,214 | 83 ×4 / 887 ×4 |
| 6836 / 6837, 10983 / 10984 | the Supporter's rim / core | the same as 6833 / 6834 | |

7074-7081 are the storm's eight shockwaves (`global_knockback`, `knockback_distance_+%` 100,
`base_knockback_speed_+%` 100) with `active_skill_area_of_effect_radius_+%_final` 0, 50, 100 … 350 — one step per
shockwave, which is how a ring grows. The Supporter's mod 37405 is `NecropolisImmuneToUnionOfSouls`
(`monster_immune_to_union_of_souls_effect` 1), a 3.24 mechanic and nothing to do with the storm.

`SSMOrionApparitionSummonDeathZone` (3629) is how the apparition gets a storm: `number_of_monsters_to_summon` 1,
`alternate_minion` **611**, `summon_specific_monsters_radius_+%` **-99** (it appears under itself),
`summoned_monsters_are_minions`, `minion_dies_when_parent_dies`, `minion_dies_when_parent_deleted`,
`monster_no_drops_or_experience`. The value 611 does not resolve to a `MonsterVarieties` row (row 611 there is a
Bonewarped Archer), so the summoned variety is not named anywhere the client data reaches — the same dead end the Shaper
note records for its own summon.

## The mods

| mod | Id | its stats | the server |
|---|---|---|---|
| 4134 | `MonsterMapBoss` | `monster_slain_experience_+%` 100, `monster_dropped_item_rarity_+%` 15000 (150 %) | not read from the mod: the server applies its own map-boss scaling (`Progression.cs`, `Monsters.cs`) |
| 4686 | `CannotBeSlowedBelowValueBosses` | `hellscape_boots_action_speed_+%_minimum_value` **-50** | **honoured** — `BossMods.SlowestActionFactor` |
| 23585 | `StunRepeatImmunityMapBossSuper` | `cannot_be_stunned_while_stunned` 1, `cannot_be_stunned_for_ms_after_stun_finished` **4000** | **honoured** — `BossMods.StunImmunityMilliseconds`; note 4000 ms here, where the act bosses state 2000 |
| 32441 | `PinnacleAtlasBoss` | **no stats at all**, only the tag `pinnacle_boss` | nothing to honour |
| 33065 | `MonsterDroppedItemsConqueror` | `dropped_items_have_random_conqueror_influence` 1, `monster_dropped_item_quantity_+%_final_for_influenced_monster` **-50** | not modelled: the server's own reward table does not roll conqueror influence |
| 7475 (special) | `ImmuneToKnockback` | `cannot_be_knocked_back` 1 | **honoured** — `BossMods.KnockbackImmune` |
| 4121 | `MonsterGauntletRuinEnabled` | `monster_gauntlet_ruin_enabled` 1 | only on row 8378; the ruin counter is not modelled |

The area's own mods are 12663, 22720 and 7490 (`MapAreas`-side multipliers), not monster mods.

## The clips and their contacts: the fight's clock

The body's timeline is `Art/Models/MONSTERS/AtlasExiles/TheOrion/Character/rig.amd` (version 5, 50 animations). The
repository's own `scripts/re/ot_components.py` reads it together with the actor's `.ao` chain —
`rig_clips(amd, ao)` then `all_contacts(events)` — so a clip's length and its `contact` keyframes are data, not a
guess. Milliseconds.

| clip | length | contacts | what the fight does with it |
|---|---|---|---|
| `awaken_01` / `awaken_beam_01` / `awaken_exit_01` | 5000 / 1500 / 1500 | — / 703 / 1050 | the introduction's three poses (clips 0, 1, 2 of `AtlasExile5.ao`) |
| `beam_01` | 1500 | **672** | the beam blast's hit: `Sirus.Combat` lands it at 672 ms in both stances — the client's own |
| `beam_02` | 1500 | 692 | second beam of the barrage |
| `beam_03` | 2000 | — | three blast contacts, but as `StandardEvent`s: see the next section |
| `beam_barrage_01` / `final_beam_barrage_01` | 7500 | — | the corridor "Die" beam, whose eight hits are `StandardEvent`s: see the next section |
| `clone_beam_start_01` | 1500 | **500, 567, 639, 728, 1450** | the clone circle: twelve clones play it as `AtlasExile5.ao` clip 7 |
| `clone_beam_01` / `clone_beam_end_01` | 1033 / 500 | — | |
| `trap_beam_01` | 5333 | — | the rotating quad beams |
| `teleport_01` / `final_teleport_01` | 500 / 500 | 250 / 250 | the blink (played as art, never as an action) |
| `meteor_maze_start_01` / `final_meteor_maze_start_01` | 1500 | — | |
| `meteor_maze_01` | 3500 | 2006 | |
| `meteor_maze_01_hold` / `meteor_maze_end_01` | 2000 / 1500 | — | |
| `throne_deactivate_01` / `final_throne_deactivate_01` | 2000 / 4000 | — | the stance change: the fight's 2 s and 4 s are the client's |
| `throne_activate_01` | 8000 | — | the ascent: the fight's 8 s is the client's |
| `death_01` | **6500** | — | the death sequence: the fight's 6.5 s is the client's |
| `flinch_large_01` / `idle_01` | 1000 / 3000 | — | |

**The durations the fight already uses are the client's own** (672 ms, 6.5 s, 8 s, 2 s, 4 s, 1.5 s) — someone read this
rig. Its `contact` keys are not the whole clock: the same blocks carry `StandardEventType` events, and those hold the
corridor's eight hit times, the three (or six) beam-blast times and the maze's creation time — the section below. What
the client does **not** state is how often the clone circle strikes: its clip has five contacts and the fight keeps one
hit on the first of them, where it used to land at 4.7 s, after the 1.5 s clip had ended.

The effect objects carry almost no damage events of their own: `ao_animation_events` on
`Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/` finds `nuke_beam_blast.ao` = `blast_damage`/
`attach_damage` at **0 ms**, `doom_geyser.ao` = `geyser_explode` contacts at **100/200/300/400/485 ms** and
`geyser_idle` at 100/200/300…, and **nothing** in `corridor_nuke.ao`, `death_carpeting.ao`,
`death_carpeting_wave.ao`, `clone_trap_beam.ao`, `maze_meteor.ao`, `meteor/meteor_150.ao`, `spam_beam.ao`,
`blocking_01/02.ao`, `maze_warp.ao` or `orion_arena.ao`. So the corridor's blast timing and the maze's geometry have
no client-side source at all.

## Why the geometry cannot be read out of the art

Every effect model carries a bounding box in the first floats of its `*.smd.header` (`[0,0,0, minX, minY, minZ, maxX,
maxY, maxZ, …]`, a 0.5 pad on each side):

| model (`.smd.header` of the `.ao` the fight plays) | X | Y | Z |
|---|---|---|---|
| `arena/arena_ring` (the arena's ring) | ±850.5 | ±850.5 | −425…0 |
| `death_zone/rig` (the storm) | ±1500.5 | ±1500.5 | −458.8…11.9 |
| `blocking_slams/beam_cannon/rig` (the corridor's beam) | ±107.5 | −950…−7.4 | ±107.2 |
| `blocking_slams/rig` (the `blocking_01/02.ao` walls) | — | — | — |
| `blocking_slams/meteor/rig` | ±750.5 | ±750.5 | −1000…0 |
| `destruction/meteor_rain/meteor_ground/rig` (the meteor's ground) | ±250.5 | ±250.5 | −120…0 |
| `environment/rig` | ±1250.5 | ±1250.5 | −500…0 |
| `corridor_slash_01/slash` (the corridor's slash) | ±0.27 | −93.1…217.2 | −279.3…0 |

These do **not** convert to cells. The arena's ring is 1701 units across while the arena's own six storm sites span
about 355 cells; the corridor's beam is 943 units deep while the fight walks it 100 cells; the meteor's ground is 501
units across while the fight burns an 8-cell circle. The pieces are authored at their own scales and the **spawn scale
is chosen by whoever creates the object** — `Sirus.Combat.cs` passes one (`Fx(…, scale)`, 1.2–1.8 for these), and GGG's
server passed its own. So none of the corridor's length or width, the maze's ring radii and gaps, the meteor's radius
or the storm's rim/core radii can be derived from the client's art or data; they are gameplay values decided on the
server. The `.ao` files carry no `radius`/`scale`/`size`/`length` key at all — nothing between the animation table and
the model's box.

## The fight the guides describe (third-party, not client data)

The client states the numbers above; it does not state the *rules* of the fight. For those the repository accepts what
the community publishes. The [PoE Wiki's Sirus page](https://www.poewiki.net/wiki/Sirus) says:

- once he reaches a health threshold he **becomes invulnerable and places a Deatomization Storm** at his position, and he
  **spawns a storm every time he reaches 75 %, 50 % or 25 %** life;
- a **safe zone** — a large animated ring around him — pushes the storms out, and standing outside it for too long
  spawns a storm as well;
- the storm phase **ends when the player comes close to him after** the storm is created; he retreats to his throne, and
  the phase takes a number of seconds during which a knockback pushes the player away from the new storm;
- the arena holds several storms; touching one deals **2102-3154** damage (50 % physical, 25 % fire, 25 % chaos) plus
  **548** base physical and **1368** base fire and chaos damage per second;
- the **maze** gains one wall ring per phase, up to three, and every ring has an exit marked by animated flares.

The guides add two things the wiki does not state: **phase 4 restores him to 75 %** life, and **the corridor blast and
the meteor maze arrive in phase 2** (the empowered Die Beam's four tracking beams and the twelve-illusion circle belong
to phase 4).

## The pairing: what the client draws and what the server sends

Every ability is two things that have to agree: the **resource side** (the clip the `.act` binds, its events, the `.ao`
and `.pet` the client draws with) and the **server side** (who casts, which effect, when the hits land, and whether the
server draws anything of its own). Change one without the other and the fight shows the wrong thing.

The resource side is read from four layers, in this order:

| layer | source | gives |
|---|---|---|
| 1 | `data/client.db` `GrantedEffects` / `Animation` | effect id → **animation slot index** (`Animation._row`) |
| 2 | `AtlasExile5.act` / `AtlasExile5Apparition.act` | slot name → **clip name** |
| 3 | `AtlasExile5.ao` / `AtlasExile5Apparition.ao` | clip → **event table** (time, type, resource) |
| 4 | the `.pet` / `.mat` files | what is actually drawn |

`GrantedEffects._row` is the effect id (`Id` is a different field); `GrantedEffects.Animation` is the *slot index*, not
the slot name — `Animation._row` gives the name. The whole Orion art set extracts with

```bash
G="dotnet run --no-build --project scripts/re/ggdump -- Client"
$G "Tools/re/out/sirus/art" extract "^metadata/effects/spells/monsters_effects/atlasexiles/orion/" "^$" 50
$G "Tools/re/out/sirus/art" extract "^metadata/monsters/atlasexiles/atlasexile5" "^$" 50
```

### The clip has to be played by the server

A cast alone does not make the client play a clip. The client builds the action class - the `OrionBeamSweep`
constructor's vtable slot 31 runs and the instance's vtable pointer is the class's own - but the clip's animation events
never arrive, so the class's `state` (the dword at `+0x318`) stays 0 and nothing is drawn.

The server has to ask for the clip explicitly, and it has to do it **as a world record for that object**: broadcast
(`All`) carries no object id, so the client receives the animation update and cannot route it to anyone - it is dropped
without a word. `SendWorldRecord(conn, apparition, () => InstanceProtocol.AnimationUpdate(apparition, 0, index, 1))`
is the form that works, the same one `CreateFx` uses for an effect object's animation.

`AnimationUpdate` is `0x1b2`: object header, u8 kind, then for kind 0/1 a u32 **animation index** and the speed. Kind 0
is "play animation", and the client bails when the index is past the actor's animation count - a wrong index is ignored
rather than fatal, which is what "no effect, no error" looks like.

The index is the clip's position in the monster's `ClientAnimationController` list, 0-based. For the apparition
(`atlasexile5apparition.ao`): 0 `Geyser_01`, 1 `Geyser_small_01`, 2 `carpet_bomb_01`, **3 `double_beam_hands_01`**,
4 `double_beam_hands_small_01`, **5 `meteor_shower_01`**, 6 `meteor_shower_small_01`,
7 `double_beam_hands_small_uber_01`, 8 `final_throne_deactivate_01`, 9 `shield_01`, 10 `throne_activate_01`. For the body
(`atlasexile5.ao`): 10 `final_beam_01`, 11 `final_beam_02`, 12 `final_beam_03`, 13 `final_beam_barrage_01`,
20 `final_meteor_maze_start_01`, 28 `trap_beam_01`.

With the clip played, the whole chain runs: the client builds the class, its event handler (vtable slot 40) receives the
clip's events, and slot 153 - the only writer of `state` - executes `mov dword ptr [rcx+318h],1`. That is where the
client's own beam is decided to appear.

### The beams are drawn by the server

**Revised 2026-11-17 (reverse engineering).** The clip does not draw the beam - but the official server does not place it
with `ServerBeamEffect` either. Of the art this fight needs, only `fusion_beam.ao` (row 227), `fusioned_nuke_beam.ao`
(228), `orb_beam.ao` (240) and `Kirac_beam.ao` (241) have a `MiscBeams` row at all; **`spam_beam.ao`, the "DIE" barrage's
`triple_beam_01/02/03.ao` and the corridor's `corridor_slash_01.ao` have none**, so they cannot be sent as a stretched
beam. They are effect objects - placed by the server, or built by the client's own action class.

Three ways the client can put a beam on screen, in full in [game/sirus/README.md](sirus/README.md):

- **the action class builds it itself** - `GeometrySpell` (the body's spell) creates a 584 B `Metadata/Effects/Effect`
  whose art comes from the action's own data (`sub_14228BED0(&out, 0x7A39632A, &ctx)`);
- **the server places the official `.ao`** as an effect object (`EffectObject`: parent + bone, e.g. `R_Weapon`);
- **the server stretches a mesh** (`ServerBeamEffect`) - the only way to get a beam that follows a moving bone.

Two things the stretch itself still needs, both learned live:

- **A beam needs a bone, or it is drawn inside the ground.** `Metadata/Effects/ServerBeamEffect` carries each end as a
  world position **plus an object id**, and the near end's *height* comes only from the object and bone named there: the
  flag byte after the `MiscBeams` row is `0x97` without a bone and `0xD7` with one, then a length-prefixed ASCII bone
  name. A beam sent without one is anchored on the caster's floor cell and drawn inside the terrain - the packets go out,
  the screen stays empty, which is exactly what "no beam on screen" was (live 2026-11-16/17). `IzaroGargoyles` has always
  used it (`Jnt_jaw`), and it is the only reason its beam leaves the mouth.
- **A beam's length runs along its own rotation.** An object's long axis lies along the rotation it is given, so the line
  through `from` and `to` uses `atan2(dy, dx)` itself, not that angle plus a quarter turn. Placed round a ring the
  rotation is `angle + pi/2` only because there the length *is* the tangent.

`MiscBeams` rows that name Orion art: 227 `AtlasExileOrionFusionBeam` → `fusion_beam.ao`; 228
`AtlasExileOrionNukeBeam` → `fusioned_nuke_beam.ao` (a fat molten column: stretched over a few cells it lies across the
arena like a slab); 239 `AtlasExileOrionBeamBombChargeBeam` → `fusion_beam.ao`; 240 `AtlasExileOrionBeamBombBeam` →
`Orion/orb_beam.ao` (a small orb, which stretched renders nothing).

### A decal with no animation draws nothing

The ground marks the fight wants - `destruction_assets/crater_small_01.ao`, `crater_big_01.ao`, `scorch_01..04.ao` - are a
`FixedMesh` (`destructionLow/Large.fmt`) plus a single `Append` render pass, with no `AnimationController` and no
`Animations` block. Created as an effect object they are invisible: standing them beside the player at scale 4 and 8 left
the floor unchanged (live 2026-11-17). The animated arts next to them (`meteors/impact_small|medium|large.ao`, a
`SkinMesh` with `default_animation`) do draw. So a lasting scorch cannot be made out of those decals, and the star rain
needs none - the client's own `MeteorImpact` draws its landing.

The client cannot do it either: its `OrionBeamSweep` class resolves the two arm tips on the bones `L_Weapon`,
`shoulder_l`, `l_arm` and `arm_r`; the apparition's rig carries `L_Weapon`, `R_Weapon` and the wrist bounds but not the
three arm bones, and the class never leaves state 0 in any case (the event tag it waits for is in no shipped clip).

Which clip the body plays depends on the stance it is in: `atlasexile5.act` binds `SpellProjectile` to `beam_01` in
stance 1 and to `final_beam_01` in `stance2`, and the clips are indices 1 and 10 of `atlasexile5.ao`; `SpellProjectileFire`
is `beam_03` (3) and `final_beam_03` (12).

### The body and the apparition are different in kind

The body is `AtlasExile5` (36 skills) and the apparition is `AtlasExile5Apparition` (20). Their action types differ, and
so does what the client can draw for them:

- **The body** (`7008`/`7012` `AtlasExileOrionSpammableBeamBlast`) is a `geometry_spell` (ActionType 807) - a plain action,
  not an Orion class. Its beam is a **spell projectile**: the beam itself is an object the *server* has to spawn, the same
  way every PoE projectile is. The framework already knows how: `AtlasBosses.ProjectileFor(conn, effect)` resolves the
  effect's `monster_projectile_variation` to a `Projectiles.dat` row and `ProjectileProfile`/`session.Schedule` spawn it.
  A cast that only sends `ActionStarted` and computes the damage itself leaves nothing for the client to render.
- **The apparition** (`10941` `AtlasOrionBeamSweep`) is an Orion class (ActionType 1034) and the client does build it -
  the class's vtable slot 31 runs and its instance's vtable pointer is the class's own. Its clip is
  `double_beam_hands_01`, and the client anchors the two beams on the bones **`L_Weapon`**, `shoulder_l`, `l_arm`,
  `arm_r`; the apparition's rig has `L_Weapon` and `R_Weapon` but none of the three arm bones.

### The abilities, paired

**Revised 2026-11-17.** Each ability now has its own document in [game/sirus/](sirus/README.md), which records the
client's action class, its slots, its serialized fields and what it draws. The table below is the map; the "art" column
is corrected against the reverse engineering.

The client has **three** Orion action classes (`ActionTypes` 1034-1036): `OrionBeamSweep` (HASH16 19042 = 0x4A62),
`OrionMeteor` (28504 = 0x6F58) and `OrionCorridorBlast` (40973 = 0xA00D); the maze, the beam bomb and the storm are
classes of their own (`OrionCircleMazeBlast` 1037, `OrionBeamBomb` 1039, `GeometryTrigger` 959, `GeometrySpell` 807).

| ability | effect | who casts | what the client draws | the server's part | doc |
|---|---|---|---|---|---|
| Star rain | 10145 / 10945 | the apparition / the body | **nothing** for 10145 (its `GeometryTrigger` row has no ground marker); one `Meteor_Rain.ao` per AS-1570 action, crater included | one 0x1ab per meteor; per-target damage at `contact` @1.92 s | [meteor](sirus/meteor.md) |
| Two-hand laser | 10941 | the apparition | **nothing** - the two `BeamTargetDaemon` anchors are invisible (`InvisibleFire.ao` has an empty skin and no particles) | the two beams and the daemon entities | [apparition-laser](sirus/apparition-laser.md) |
| Body beam | 7008 / 7012 | the body | its own effect (`GeometrySpell` builds a 584 B `Effect`), or the server's `spam_beam.ao` on `R_Weapon` | one cast, one hit | [body-beam](sirus/body-beam.md) |
| Multi beam | 8098 / 8099 | the body | the charge trail and the blast points; the three beams are `triple_beam_01/02/03.ao` effect objects | three effect objects per contact | [body-barrage](sirus/body-barrage.md) |
| Maze | 10949-10953 | the body | the rings `meteor_100/150/200.ao`, one every 0.2 s | the cast and the damage | [maze](sirus/maze.md) |
| Corridor | 10948 | the body | `corridor_slash_01.ao` per beam | eight effect objects; one damage circle per fireball | [corridor](sirus/corridor.md) |
| Die Beam | 10147 / 8100 | the body | `clone_trap_beam.ao` on the body | the clones' beams | [clones](sirus/clones.md) |
| Beam bomb | 10973 | the body | the `OrionBeamBomb` entity's own `.ao` | the entity, the position table, the rotation (10°/s) | [beam-bomb](sirus/beam-bomb.md) |
| Storm | 6833-6837 | the storm entity | `OrionDeathZone.ao` - the monster's own art | the entity and its twelve skills | [storm](sirus/storm.md) |

**The two arms are the server's, because the client's sweep is never armed.** `OrionBeamSweep` only resolves its two
arm tips while its own state is 1, and the only thing that writes that state is a shared event handler woken by an
animation event - so the class is built and then sits idle (the slots and their behaviour are in
[apparition-laser.md](sirus/apparition-laser.md)). The server draws the arms instead: one beam per hand, starting on
that hand's own bone (`L_Weapon`/`R_Weapon` - the clip's `daemon1` at model -559.03 and `daemon2` at +559.0 in x, i.e. 2.8
cells either side of the axis and 1.6 in front) and ending on the clip's `blast` point 7.54 cells ahead, alive from
`contact` (0.910 s) to `blast` (3.431 s). The start follows the bone, so the sweep the clip performs turns both beams as
the arms move; placed as a static mesh instead it drew two loose slabs in front of him with nothing joining the hands to
the target.

**A beam's length runs along its own rotation.** Every beam in this fight is a stretched mesh placed by the server,
and `blocking_02.ao` showed the convention: an object's long axis lies along the rotation it is given, so the line
through `from` and `to` uses `atan2(dy, dx)` itself, not that angle plus a quarter turn. Placed round a ring the rotation
is `angle + pi/2` only because there the length *is* the tangent.

**The corridor's beams are the server's (revised 2026-11-17).** `OrionCorridorBlast` (ActionType 1036) *is* instantiated by
a 10948 cast - it picks its own directions (slot 31 sweeps 2π in 10° steps and keeps the ones within 60 cells; slot 40
reads the `beam_left`/`beam_right` stats) - but **it creates no visible art**. The official beam is `corridor_slash_01.ao`
(`MiscAnimated` row 3692 `AtlasExileOrionCorridorNuke`), an effect object the server places; `corridor_nuke.ao` is the
fight's own ball, but its mesh is stretched between two bones by a `beam_length` group, so a plain effect object at a point
draws nothing. The corridor therefore marches eight of those down the middle, one per contact.

**The maze's rings are not a circle.** `meteor_{100,150,200}.ao` are meteors that fly and emit boxes as they go
(`boxEmit.pet` is an `AxisAlignedBoxEmitter` with a stretch curve, on the `aux_box_01..03` bones - nine boxes for the 150
and 200 variants). The original wall is therefore a moving zigzag, and the collision the server adds has to land on those
boxes rather than on a clean arc.

**The maze wall object.** The wall is two objects doing two jobs, because no single one does both.

- **Collision:** `OrionBlockingWall2` (`InstanceEntities.OrionWallObject`, the fight's own maze wall type), one every
  1.6 cells, rotated to face the centre. Its TriggerableBlockage is read by the client itself: the client closes the
  cells the model's footprint stands on (0x141d8ad70), so the player is stopped by his own pathfinder and the server
  never has to put him back. `Tutorial_Blocker_1` at scale 0.01 - what this fight used before 2026-11-16 - blocks
  **nothing**: the footprint shrinks with the scale, so a 0.01 wall closes 0.02 of a cell and the player walks through
  the ring (live: "the wall is fake, it does not hold the player"). The object's own `blocking_02.ao` stays on its
  default `wait` clip, which draws nothing, so the collision object is invisible and the picture is the ring below.
- **When it goes:** the wall carries the StateMachine state `detonate` (component 7), raised at the collapse the moment
  the centre meteor lands, and the objects are removed 0.6 s later. The official fight does the same, and the phase
  after the maze started 1.2 s after the hit in a live run.
- **The picture:** `Orion/blocking_02.ao` - the fight's own maze wall, a ring of broken rock with its lava in the `crack`
  bone group's particle - as a plain effect object, one every 2.6 cells, rotated to face the centre, at scale 1.5.
  **It has to be started on clip 2.** The `.ao`'s `default_animation` is `wait`, which carries no particle events at all,
  so an effect object of this art that is never given a clip draws nothing; clip 2 is `loop`, and that is the one that
  puts the wall on the screen (live 2026-11-16: `/sirus_art wall 2 1 12 24` in town drew a closed ring, and the same
  art at scale 1 sat too low to read against the arena's own black ground).

**Two things the resource layer settles that the code used to get wrong:**

- **The star rain is the apparition's cast, not the body's - but it is 10942, not 10145 (revised 2026-11-17).** One
  official meteor is one action of effect 10942 (`AtlasOrionMeteor`, ActiveSkill 1570 `orion_meteor`): the client class
  `OrionMeteor` builds exactly one `Meteor_Rain.ao` (its `MiscAnimated` hash 2133148678 = row 3622
  `AtlasExileOrionMeteorRain`), so a shower is **N such actions**, one per meteor. 10942 sits in the apparition's list
  (skill id 0x400B), so the rain is cast by the apparition. Effect 10145 (`AtlasExilesOrionMeteorRectangle`) is a
  `geometry_trigger` instead, and that path draws nothing here: its `Data/GeometryTrigger.dat` rows for this fight have no
  ground marker, and the stat it looks the row up by (6433) does not appear in the file at all - which is why casting 10145
  produced an empty arena.
- **The maze rings are the server's for now, and the client draws nothing for them yet.** `meteor_{100,150,200}.ao` are
  meteors that fly and emit boxes as they go (`boxEmit.pet` on the `aux_box_01..03` bones), which is why the original wall
  reads as a zigzag rather than a ring: they belong to `OrionCircleMazeBlast`, which the cast *does* now instantiate
  (effect 10949 -> ActiveSkill 1572 -> ActionType 1037). But the client only builds a ring when that action's own
  serialized state arrives with flag = 1 and the ring centre, and a 0x1ab cast carries no such state - so the client has no
  centre and no ring. The server draws its own ring with `blocking_02.ao` and lands the collision on the same circle until
  it can send that state.

**What the client will not do for Sirus:** draw the two-hand laser. `OrionBeamSweep` resolves two arm tips - the hands at
model `(+/-559.03, -160.46, 0)` (5.59 cells apart, 1.60 in front of the caster) and the blast at `(0, -753.94, 0)`, 7.54
cells straight ahead of the apparition, which is where the beams close, not at the player - but the class creates no art of
its own, so resolving the tips draws nothing on screen. The tips are only solved while the action's state is 1, and that
state is written by one slot (153, `0x141D1AF10`) reached from the shared event handler `0x1419E9D30` when an animation
event carries the tag `0x9b8d` - the same event id the meteor and beam-bomb actions key on (39821, this fight's "contact"
event). The server therefore draws the two arms itself, at the clip's own coordinates: the tag is in the clip, but the
drawing is not.

### The arena ring and the throne are objects the server has to spawn

`orionarenamarker.ot` is `server_only` and its only component is `Animated` on `Orion_Arena.ao`: nothing appears unless the
server spawns it. The art is the boundary band — `Orion/Arena/orion_arena.pet` is an infinite point emitter of the
`arenaedge.fmt` mesh at `scale 1214.29`, hung on the `line` bone group, so it runs round the edge of the arena at the
arena's own scale rather than round the player. Our server draws it on the arena centre from the moment he descends and
drops it when he ascends; it is a mark of where the arena ends, so it is read from near the edge, not from the middle.

The intermission is the same kind of pairing. `AtlasExile5Throne` (row 8377) is metadata of its own with
`on_construction_complete = { globals.the_throne = this; }`, and the body's `Emerge` / `Emerge2` / `ChangeToStance2`
all `MoveToObject(the_throne, …)` — so a server that never spawns it leaves those clips with nowhere to move to. Spawning
it is not enough either: `throne = 1` on the **body** only makes the *throne* play `Throne_Appear.epk`, while the seated
model is the **throne's own** `human = 1`, which hangs `AtlasExile5OnThrone.ao` + `Throne_FX.epk` on `throne_jntBnd`. Set
only the body's states and the whole intermission is an empty arena (that was this controller's bug until 2026-11-16).
Ours spawns a fresh throne on the arena centre at the ascent, gives it `human = 1`, takes it off again on the descent
before `throne_deactivate_01`'s `disintegrate_throne` (0.14 s) dissolves the seat, and retires the actor once that clip
has played.

## What is open

- **The art's circle is not the damage radius — do not "correct" the radii from the mesh.** The death-zone model
  (`Art/Models/Effects/monster_effects/AtlasExiles/Orion/death_zone/rig_3a3099b1.smd.header`) spans about ±1500 model
  units, while the fight damages a 38-cell rim and a 13-cell core: what is drawn is an order of magnitude larger than
  the circle that hurts. The rim/core radii, and the eight shockwave rings' base radius (`7074-7081` carry only
  `active_skill_area_of_effect_radius_+%_final` 0…350), are **gameplay values decided on GGG's server**; the client
  states no figure for them. Where the art *does* carry a gameplay distance, a bone names it: `corridor_nuke.ao`'s
  `beam_length`, the `blocking_*.ao` wall pieces, a meteor's authored scale (`meteor_150.ao`).
- **The storm is the arena's real killer and nothing in the data stops it.** 591 a second of four elements at the rim,
  5,913 at the core, 9,501 for the apparition's, and all of it before the resistance roll — a player caught in a core
  dies in a fraction of a second, and the storm cannot be killed, stunned or outrun (the mobile one walks at 12 and the
  static one waits). The client also preloads `atlas_orion_death_zone_core`, so the core is its own buff.
- **The two lists are the skill contract.** `BodyEffects`/`ApparitionEffects` must stay the client's
  `GrantedEffectsKeys` order and length: the index *is* the skill id. The apparition's list was five short (5905, 10945,
  10970, 10971, 10972) and two of the missing ones sit at the **front**, so every apparition skill id had been two too
  low, 3629 included.
- **`Sirus.Combat` plays `ChangeToStance1Immediate`** for rounds 1-2 and `ChangeToStance2Immediate` for round 3. Those
  action types exist (rows 580/581) and `Basic` resolves them, but the act binds only `ChangeToStance2`: the animated
  swap to the final stance has a clip (`final_throne_deactivate_01`), the immediate one deliberately has none.
- **Clip lengths are not in the `.ao`.** The contacts above are; the durations need the rig,
  `Art/Models/MONSTERS/AtlasExiles/TheOrion/Character/rig.amd` (named by `AtlasExile5.ao`'s `AnimationController`), which
  is not part of this extraction.
- The `.ao` also carries the `view_of` remaps that make one clip serve two skills: `beam_01/02 → beam_uber_01/02`,
  `beam_03 → beam_uber_03`, `trap_beam_01 → trap_beam_uber_01`, `clone_beam_start_01 → clone_beam_start_uber_01`,
  `final_beam_barrage_01 → standalone_beam_barrage_01` (excluding the `StandardEvent` events), so the Uber variants play
  the same animation with different timing.

## The `StandardEvent`s: what the clips time besides `contact`

The table above reads `contact` keys. The same animation blocks also carry `StandardEventType` events, where the `name`
is the event and `contact` is only one of them — and that is where the corridor's and the final stance's blast timing
lives. Milliseconds, from `Metadata/Monsters/AtlasExiles/AtlasExile5.ao` (and its `Atlasexile5Influence` twin, which
carries the same numbers):

| clip | `StandardEvent`s |
|---|---|
| `beam_barrage_01`, `final_beam_barrage_01` | `create_corridor` 461, `beam_right` **1173**, `beam_left` **1825**, `beam_right` **2485**, `beam_left` **3156**, `beam_right` **3818**, `beam_left` **4497**, `beam_right` **5167**, `beam_left` **5812**, `finish` 6536 |
| `beam_03` | `identify` 400, `small_beam_blast` **841**, `medium_beam_blast` **1098**, `large_beam_blast` **1370** |
| `final_beam_03` | `start_face` 0, `identify` 400, `small_beam_blast` **841**, **991**, `medium_beam_blast` **1125**, **1264**, `large_beam_blast` **1389**, **1516**, `end_face` 1526 |
| `trap_beam_01` / `trap_beam_uber_01` | `dialogue` 2000, `identify` 3742 (uber `end_face` 4694), `clone_beam_blast` **4141, 4282, 4388, 4500, 4597, 4722** |
| `meteor_maze_01` | `create_maze` 461, `contact` **2006** |
| `meteor_maze_start_01` / `final_meteor_maze_start_01` | `killminions` 801, 1145, 1452 / 754, 1110, 1500 |
| `beam_01`, `final_beam_01` | `contact` 672 in both |
| `clone_beam_start_01` / `clone_beam_start_uber_01` | `contact` 500, 567, 639, 728, 1450 / 830, 943 |
| `teleport_01`, `final_teleport_01` | `contact` 250 |

- **The corridor's eight contacts are the client's own.** `Sirus.Combat`'s 1.173 / 1.825 / 2.485 / 3.156 / 3.818 / 4.497
  / 5.167 / 5.812 are `beam_right`/`beam_left` to the millisecond, in both barrages. An earlier note in this file said
  the fight invented them; it did not — `scripts/re/ot_components.py`'s `all_contacts` reads `contact` keys only. The
  7500 the fight holds the corridor for is `GrantedEffects` 10948's `CastTime`, so the animation (`finish` 6536) ends
  0.96 s before the cast does, and both numbers are the client's.
- **The final stance doubles each blast and moves the second half.** The fight had 1098 / 1241 / 1370 for the final
  stance's medium and large pair — `beam_03`'s times — where `final_beam_03` puts them at 1125 / 1264 / 1389. Changed.
- **672 ms is not scaled by the cast time.** `beam_01` and `final_beam_01` both land the contact at 672, so 7012's
  1350 ms cast does not pull it in; the fight multiplied by `cast / 1.5` and landed the final stance's blast at 605 ms.
  Changed to the flat 672.
- **The quad beams' six blasts** are at 4141-4722 ms, 0.4-1.0 s after `identify` 3742. The fight starts the arms at the
  skill's 3000 ms cast and damages continuously until `base_skill_effect_duration` 13000 ms: a different model, not a
  different number, so it is recorded and not changed.
- **The maze is created 461 ms into `meteor_maze_01`**, about 1.96 s after the action starts (its start clip is 1.5 s),
  and that clip's own `contact` is at 2006. The fight builds the walls and moves the players at once, and keeps them for
  its own 5 / 8 / 11 s — a clip of 3500 ms is not what the walls live for, so their lifetime has no client figure.

### What the client still does not state

| figure | in the fight | what the data has instead |
|---|---|---|
| storm rim / core radii **38 / 13** | `Sirus.Damage.Ground` | 6833, 6834, 6835, 10981, 10982, 6836, 6837 resolve to four `base_*_damage_to_deal_per_minute` and nothing else; `ActiveSkills` 1116 is `add_buff_to_target_triggered` and has no radius field; the varieties have `ObjectSize` 0 and no mod grants one. The art's rings (`max_circle` = `aux_circle1` at 35 units, `center_circle` = `aux_circle3` at 23.29) do not convert — see the section above. |
| the eight shockwaves' **base** radius | never cast | 7074-7081 state only `active_skill_area_of_effect_radius_+%_final` 0 … 350 on a stat set that has no radius, plus `is_area_damage`, `global_knockback`, `base_knockback_speed_+%` 100 and `knockback_distance_+%` 100. There is no base to scale: the data describes a ring that grows, not where it starts. |
| corridor beam length 100, wall half-width 11, step 6, first wall 8, Sirus's ring 42, hit radius 7 | `Sirus.Combat.Corridor` | `corridor_nuke.ao` binds `bone_group = "beam_length false jntBnd_beam aux_beam"`, so the game stretches the beam between two bones and no length is authored; the rig's mesh is 943 units deep and `aux_beam_cyl` sits at -900. `blocking_02.ao`'s `Hull` gives one wall's footprint only (proj_tri ≈ 17.9 × 9.0 units). |
| maze radii 12 + 8·ring, gap 0.7 + 2.15·ring, rings `min(3, Round+1)`, 1.6-cell collision spacing, 2.6-cell wall spacing at scale 1.5, 0.32 rad gap, hit radius 17 + 15·(rings-1), player clamp 180, centre = the player | `Sirus.Combat.Maze` | `blocking_01.ao` is one wall segment (same `Hull`; `OrionBlockingWall1.ot` states `object_size = 1`), and nothing states a ring. The client's own figure (radius 20·circleIndex + 4, vtable 0x141D1CB40) is not used because the client never builds those rings. |
| meteor count 8, spread 4 + 16·rand, hit radius 6, burn radius 8, 5 s and 8 s | `SirusFight.Meteors` | the art has three sizes (`meteor_100/150/200.ao`, one impact `box` bone group then three) and 10945 is `infinite_skill_effect_duration` with 65,850 a minute per element: neither a radius nor a finite burn. |
| teleport ring 30 + 20·rand, arena clamp 110, storm-clear 70 | `RingPoint`, `Sirus.cs` | no client figure. The room those storms belong to places its six stationary ones 107.4, 117.6, 121.3, 170.4, 216.6 and 303.8 cells from `mapboss1` (`fountain.arm`), so the authored fight space reaches well past the 110 clamp, and `WalkableFor` is what actually holds a point inside the room. |

**One client number the fight does not use at all:** every storm aura (6833, 6834, 6835, 10981, 10982, 6836, 6837)
carries the constant `action_speed_-% = 25`. `Sirus.Combat.PlayerStats` applies `action_speed_-%` only for the beam
stacks, so standing in a storm does not slow anyone. That is a mechanic rather than a number, so it is recorded here
and left as a decision instead of being folded into this pass.

## Sources

- tables: `data/client.db` — `MonsterVarieties` (8376-8380, 3282-3285), `WorldAreas` 1700, `GrantedEffects`,
  `ActiveSkills`, `ActionTypes`, `Animation`, `GrantedEffectStatSets(PerLevel)`, `Stats`, `Mods`, `Tags`;
- metadata: `Client/Content.ggpk`, mirrored in `data/game-files.zip` (`.ot`/`.ao`) plus the archive for the `.act`
  files, which the pack does not carry; read with `iconv -f UTF-16LE -t UTF-8`;
- the working copies and the three scripts used for the tables are in `Tools/re/out/orion/` and `Tools/re/out/sirus/`
  (`effects.py` resolves the stats the way `GolemCatalog.Scaling` / `SkillCatalog.EffectStats` do, `mdtable.py` prints
  the two tables above, `ao_clips.py` prints the `.ao` clip events);
- the archive paths, for re-extraction:

  ```bash
  dotnet run --no-build --project scripts/re/ggdump -- Client "Tools/re/out/orion" list "^metadata/monsters/atlasexiles/"
  dotnet run --no-build --project scripts/re/ggdump -- Client "Tools/re/out/orion" extract \
    "^metadata/(monsters/atlasexiles/|npc/epilogue/atlasexile5|effects/spells/monsters_effects/atlasexiles/orion/)" \
    "\\.(dds|png|ogg|tga)$" 1
  ```

- the code: `Modules/Core/Game/Monster/Sirus.Native.cs` (the effect lists, the skill base and `EffectOf`),
  `Sirus.cs`, `Sirus.Combat.cs`, `Sirus.Damage.cs`, `Sirus.World.cs`, `SirusFight.cs`, `AtlasBosses.cs`;
  `BossMods.cs` for the mods.
