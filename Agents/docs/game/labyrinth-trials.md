# Trials of Ascendancy

The trials in the acts that open each Labyrinth tier (#450, roadmap "The Labyrinth" in
[roadmap/world.md](../../roadmap/world.md)).
The Lower Prison's Normal trial is verified with the real client: its door, lever, Locked Door, plaque and the
Trial Plaque window work.
Its spike traps (the cascade ring and the plaque room's checkerboard) are matched against the original game by eye.
The Imperial Gardens' dart traps are checked live against the original; the Catacombs' roombas are built from its
description and not yet checked live.
The other traps, the trials' monsters and the other trials' puzzles are not built yet.

## Where a trial is

`LabyrinthTrials.dat` has 18 rows: the six Normal, three Cruel and three Merciless trials in act areas, and the six
Eternal ones (`EndGame_Labyrinth_trials_*`, reached from maps).
The server's level generator already generates every one of these areas.

A trial is two rooms of the area's graph: the `labyrinth_intro` node and the `labyrinth_end` node.
In The Lower Prison they take `Labyrinth/RoomSets/Prison/Rooms/Unique/axiom_intro.arm` and `axiom_intro_end.arm`.
Some trials have both ends in one room (`crypt_intro.arm`, `crematorium_intro.arm`).
The rooms' object lines hold the whole trial; `data/room-points.json` keeps them with their type, model, angle and
scale, and `AreaPopulation.LoadObjects` places them through the room's placement.

| Room | Objects (besides doodads) |
|---|---|
| `axiom_intro` (Lower Prison) | `LabyrinthIntroDoor`, `Door_Closed`, `Switch_Once`, 16 `LabyrinthCascadeSpikeTrap`, `Trigger_PulsePrisonWG`, the Bronze Monograph |
| `axiom_intro_end` | `LabyrinthTrialPlaque`, `LabyrinthTrialReturnPortal`, 10 spike traps, two delay triggers, `PrisonPoacherSpawner` |
| `labyrinth_intro` (Imperial Gardens, `RuinedCity/Garden`) | the door, 14 `LabyrinthArrowTrap`, the plaque, the portal, the Bronze Monograph: one room, no puzzle |
| `catacomb_intro` / `_end` | the door, one `Door_Closed` and `Switch_Once`, roombas; the plaque and the portal |
| `crypt_intro` | the door, `Door_Closed`, `Switch`, `Switch_Multi`, `Switch_Once`, spinners, the plaque, the portal, `CryptFrostPriestSpawner` |
| `crematorium_intro` | the door, three `Door_Closed` and `Switch_Once`, five `TimerGears`, lava, the plaque, the portal, `CremPyroSpawner` |
| `intro` / `intro_end` (Chamber of Sins) | the door, two `Door_Open` and `Switch`, sawblades; the plaque and the portal |

The objects are `server_only` (BaseEvents): the client creates none of them, so the server places every one.

## The objects

Each type's 0x2b9 blob follows its own component order (`scripts/re/ot_components.py`); BaseEvents reads nothing
but counts for the 0x2ba component index.

| Type | Components read | Transitionable index |
|---|---|---|
| `Labyrinth/Objects/LabyrinthIntroDoor` | Positioned, BaseEvents, Animated, InteractionAction, Transitionable, TriggerableBlockage | 4 |
| `Puzzle_Parts/Door_Closed` ("Locked Door") | Positioned, InteractionAction, BaseEvents, Animated, Transitionable, TriggerableBlockage, MinimapIcon | 4 |
| `Puzzle_Parts/Switch_Once` (lever) | Positioned, Transitionable, InteractionAction, BaseEvents, Animated, MinimapIcon | 1 |
| `QuestObjects/Labyrinth/LabyrinthTrialPlaque` | Positioned, BaseEvents, Animated, Transitionable, MinimapIcon, InteractionAction | 3 |
| `Labyrinth/Objects/LabyrinthTrialReturnPortal` | Positioned, InteractionAction, BaseEvents, Animated, Transitionable, MinimapIcon | 4 |

Animated carries the room line's model (`LabyrinthDoor.ao`, `doorbar_prison.ao`, `lever_v01_04.ao`,
`labyrinth_plaque.ao`, `LabyrinthAirlockPortal.ao`).
Transitionable state 1 runs `create_untransitioned` (closed, unpulled, dark), 2 the transition: the client plays the
`.ot`'s `on_transition` itself (the door's opening, the plaque's glow, Izaro's speech, the portal opening).
TriggerableBlockage is u8 blocked, then while blocked a u8 count of sections (none sent).

**What the Gateway does** (`LabyrinthTrials` in the Gateway, `Rules.LabyrinthTrials.cs` in QuestObjects):
- on entering a trial's area it creates these objects; a door also closes its line across the way between the
  area's spawn and the plaque on the server's walk estimate (`QuestBarriers`, `ClientState.DoorBarriers`);
- using the trial's door opens it (0x2ba state 2, then 0x1c6 unblocked);
- a lever opens itself and the nearest Locked Door that is still closed (our rule: no file links a lever to its door);
- the plaque sets the trial's quest flag, opens the portal and opens the Trial Plaque window; a done trial's plaque
  is lit when the player comes, but its portal is closed until the plaque is used (live 2026-10-31, the original: in
  every trial), and using the plaque again only opens the window;
- the open portal teleports the player back to the trial's door;
- what an instance opened stays open for later entries (`InstanceState.Transition`).

## Unblocking a door

TriggerableBlockage has no 0x2ba reader (its vtables' slot 20 is `ret`).
Its own world message is **S→C 0x1c6** (read `0x141fe14b0`, world handler `0x142187480`): the world header, u8
blocked, and while blocked a u8 count and that many u8 sections.
Unblocked calls the component's +0xb8 (`0x141e43b60`), which writes back the cells it saved when it blocked unless
the type's `disable_save` (+0x11) is set; blocked calls +0xb0 with the sections.
Of the `.ot` keys (`0x141e43200`) `start_blocked` is +0x10, `disable_save` +0x11, `shunt_objects_away` +0x12;
`save_as_diff` is accepted and not kept.

Blocking (`0x141e43520`) saves the grid under the object, stamps its Hull (`0x141d876e0`) and keeps the difference;
the unblock adds that difference back (nibbles, clamped at 5). Both do nothing while a byte of the world (entity +0x78
→ +0x111, not identified) is set, and keep no cells in a hideout (the area's WorldAreas row, world +0xb0, byte +0x14e
IsHideout).

An object with a **static position** (the type's +0x158 → +0x19 set and Positioned +0x1e7 bit 1 clear; "Has Static
Position" in the info dump `0x141e33310`) has its Hull stamped by the client's object pass `0x141d88690` (and the
placement paths `0x141d84520`, `0x141d83fb0`) whatever its TriggerableBlockage says; for any other object the pass
stamps it only while the blockage is blocked. A static door stamped so before it blocked keeps a zero difference, and
its unblock frees nothing. Live 2026-10-31 the Locked Door (`doorbar_prison.ao`) stayed closed to walking after 0x1c6,
and also when created again open and unblocked (a movement skill still crossed it), and so did the trial's door in The
Catacombs, though The Lower Prison's opened. So the Gateway sends the trial doors' Positioned with flags1 0x20
(+0x1e7 bit 1: no static position; the client's own Positioned writer `0x141e2fdc0` carries the bit too)
and 0x08 with 1 (+0x1e6 bit 0x40: a dynamic object that blocks no walker; [04](../protocol/04-player-entity.md)), so
only the blockage closes their cells.
The Gateway still removes an opened Locked Door (0x2bb) 1.5 s after its opening, when the bars have lifted, and does
not create it again in that instance.

Where a door's line parts nothing on the server's walk map (the Catacombs: the arrival reached the plaque round some
other way), the server closes the door's own walk Hull instead (`LabyrinthTrials.DoorHull`: `LabyrinthDoor.ao`
±14 × ±6 cells), or the walk up to the closed door stopped on its far side, where the client could not be.

## The Bronze Monograph

Each early trial's door has one (wiki: a "bronze monograph" at the entrance of each early trial).
Its room line is `Metadata/NPC/Labyrinth/LabyrinthTrialGlyph<Prison|Sins|Church|Crematorium|Hedge|Catacombs>` with
`Metadata/Terrain/Doodads/Labyrinth/StoryGlyph.ao`: an NPCs.dat row (136–141) with no `.ot` of its own, filed on its
`Metadata` `.ot`, `StoryGlyphBase` (Positioned, Animated, BaseEvents, InteractionAction, NPC, MinimapIcon).
The Gateway places it as an NPC (`NpcCatalog`, `SpawnNpc`) with the room line's model.
Each has one NPCTalk row, index 0 "Read" (777–782, NPCTextAudio 929–934).
In the original it reads at once on the click, and the voice goes on as the player walks away, so a click sends
**S→C 0x84 with that index**, which opens the talk on the row instead of the option list (live 2026-10-31).

## Spike traps

`Metadata/Terrain/Labyrinth/Traps/LabyrinthCascadeSpikeTrap` (The Lower Prison: a ring of 16 in the first room, 10 in
the plaque room) is a monster-like object.
Its blob reads Positioned (the `.ot`'s team 14), Animated (the `.ot`'s `spiketrap_prison.ao`), Pathfinding,
TriggerableBlockage, Transitionable, Stats, Buffs, Life and Actor, written as a monster writes them, with no skills.
Its Hull (±11.27) is one tile: the traps stand 23 cells apart, so each trap's square is its whole tile.

**How it fires.** Its actor (`LabyrinthSpikeTrap.act`, like every spike trap `.act`) names one clip, `spike_fast`
(`Spikes02/rig.amd`: 1000 ms, a "contact" at 500 ms; `spiketrap_prison.ao` plays `Up` at 0 ms and `MechDown_Fast` at
500 ms); `spike_medium` and `spike_slow` exist in the rig but no `.act` names them.
The Emerge action (0x1ab, basic action 0x0101) played the plain clip, and the spikes showed about a second after it.
**S→C 0x1d1** with Animation.dat `Emerge` (row 43) stretched over 2000 ms matches the original: four traps of the
cascade stand up at once and lower slowly.
The spikes are up from about 100 ms to 1000 ms (the stretched clip's half), and lowering until 2000 ms.

**The pattern.** The real timers are the rooms' `Trigger_PulsePrisonWG`, `Trigger_Delay_4_0WG` and
`Trigger_Delay_4_2WG` lines: server objects with no `.ot` or model, absent from `room-points.json`.
So the pattern is fitted by eye against the original (live 2026-10-31):
- a ring (every trap side by side with two others) cascades from its trap nearest the trial's lever, straight towards
  its trap nearest the trial's doors and on round, 450 ms a trap, starting again at once;
- any other chain fires as a checkerboard: the tiles of the start's colour, then the others 2 s later, every 4 s.

**What it does.** The cascade traps do not answer a step (the spikes that do are another trap, in Izaro's rooms).
- A player on the tile while the spikes are up is hit once: `LbRetractableTrap`'s stat set (GrantedEffectStatSets
  5413) deals 25% of maximum life plus 15% of maximum energy shield, 16% more against players
  (`damage_+%_vs_players`), always hitting; armour and resistances are not applied.
  The hit packet (0x1af) carries the blood and particle flags.
- The hit bleeds (the `.ot`: 100% chance, 80% less duration): the wiki's bleed from a monster, 10% of the hit a second,
  three times that while the player moves, for 1 s, not reduced by armour, only the strongest running; none on a
  player who cannot bleed (the panel's `cannot_gain_bleeding` or `virtual_immune_to_bleeding`).
  BuffDefinitions `bleeding` shows it; `labyrinth_trap_bleed` and `trap_pinned` ("Spike Pinned") carry shares of the
  pools whose values the client data lacks.
- Risen and lowering spikes are a wall: the trap's TriggerableBlockage (0x1c6) and its tile's cells on the server's walk
  estimate, from the rise until the clip ends. A player already on the tile is not walled in: closing the cells under
  them made the collision escape throw them off the tile.
- No stun or pin: the original shows no buff, and neither BuffDefinitions `pinned` (`no_movement_speed`, not read by
  the client's walk speed, `0x141e7c0b0`) nor native TakeHit recovery was right.

The timings are dev-tunable for a live check: `/trial_spike_hit <from> <to>` (the hit window, ms after firing),
`/trial_spike_step <ms>` (the ring's step) and `/trial_spike_anim <ms>` (the stretched clip).
Firings within about two tiles of the player are logged with the player's position.

## Dart traps

`Metadata/Terrain/Labyrinth/Traps/LabyrinthArrowTrap` (The Imperial Gardens: 14 statue turrets, `statueturret_circleclean.ao`
on the room line, facing 45°, 90° or 135°) is a monster-like object like the spike trap.
Its blob reads Positioned (the room line's angle, the `.ot`'s team 14), Transitionable, Animated (the room line's
model), Pathfinding, BaseEvents, Stats, Buffs, Life and Actor, written as the spike trap's.

**How it fires.** Its skill is `LbArrowTrap` (ActiveSkills `lb_arrow_trap`); the `.ot` gives it
`number_of_additional_projectiles` 3 and `projectiles_nova`, so four darts a quarter turn apart from the way the turret
faces: a 45° turret shoots along the diagonals, a 90° one straight.
Each dart leaves `projectile_nova_extend_projectiles_forward_by_x_units` (50 units, 4.6 cells) ahead of the turret,
at the height of the turret's `cast` contact (`StatueTurret/rig.amd`: 118.757 up).
The original fires every turret at once, about every 1.5 s (live 2026-10-31, the user's observation); the Gateway
does so for as long as the player is in the area, each turret playing Animation.dat `SpellProjectile` (its `.act`'s
`cast`, 0x1d1) as it fires.
The darts are `Metadata/Projectiles/PoisonArrowTrapProjectile` (the poison dart rig; `ArrowTrapProjectile` is the
plain one), 2000 units a second, flown by the connection's projectile world: a wall stops them, the player is their
only target.
The turret's Hull (every turret model but `statueturret_circlevine_noblock.ao`: ±3.25 to walking, ±2.26 to
projectiles, turned with it) is stamped by the client: live (2026-10-31) the darts vanished at the turrets in their
way while the server's flew on, unseen, and could hit a player behind one.
So the Gateway closes those cells too (`ClientState.ProjectileWalls` for the darts, `DoorBarriers` for the walk
estimate).
The rig's flight clip `spine_shoot` is a `loopnoint` clip, which the `.amd` reader did not know: it dropped every rig
with one (321 of them), so these darts had no clips in `data/projectile-clips.json` before.

**What it does.** A dart that reaches the player hits once and stops:
- `LbArrowTrap`'s stat set (GrantedEffectStatSets 5469) deals 15% of maximum life plus 9% of maximum energy shield,
  33% more against players, as the spike trap's share does; the dart is an arrow (`skill_can_fire_arrows`), so the
  hit is physical and the player's armour reduces it (our reading; evasion and block are not rolled);
- it poisons (`global_poison_on_hit`) for 2 s, 10% of the hit's life share a second, fitted by eye: the wiki's 30%
  with the `.ot`'s `monster_poison_damage_+%_final` −50 made 111 a second for a 3 707 life character, against about
  70 in the original (live 2026-10-31); `/trial_dart_poison <percent>` (dev) changes it.
  Every hit adds a stack with its own BuffDefinitions `poison` ("Poisoned",
  whose count the client shows; the client keeps a buff until it is removed, through a death and the resurrection,
  so a death ends every stack and the spike traps' bleed with their buffs). Poison is chaos damage: past energy
  shield, less the player's chaos resistance.
  Its base is the hit's life share alone (our reading): reckoned from the whole hit, a character with about 100 000
  energy shield took a 13 135 hit whose poison (3 940 over 2 s) killed their 3 707 life (live 2026-10-31).
- it slows: BuffDefinitions `labyrinth_arrow_slow` (invisible) with `labyrinth_arrow_movement_speed_+%_final`, for
  the skill's `base_skill_effect_duration` (2 s) from the latest hit, not stacking. The value is not in the client
  data; in the original a character's movement speed went from +19% to −52% under the darts (live 2026-10-31), so
  60% less, final: the pools apply it to the whole `movement_velocity_+%`, which the client walks with and the panel
  shows, and the walk estimate follows.

## Roombas

The Catacombs trial's trap room (`catacomb_intro`) has seven `Metadata/Terrain/Labyrinth/Traps/LabyrinthRoomba`
(`roomba.ao`) and one `LabyrinthFlyingRoomba` (`flying_roomba.ao`), and 15 `Rail_Invisible` points (server objects with
no `.ot` component but BaseEvents, their models only the editor's crossing, corner and end pieces) they run along.
The `.ot`s' Movement: `FollowPath`, `reverse_when_path_ends`, a 100 ms pause at each waypoint, `base_speed` 35 (the
flying one 25, `flying = 1`), turned to where they go; the ground one's Transitionable plays `walk_toinactive` (nothing
in the trial turns it: live 2026-10-31, the lever only opens its door).

**The blob.** LabyrinthRoomba reads Positioned, Animated, BaseEvents, Stats, Buffs, Movement and Transitionable; the
flying one Positioned, BaseEvents, Animated, Stats, Buffs and Movement. Movement's reader (`0x141e741e0`) takes u8
(+0x30), i32 x, i32 y (a cell it sets Positioned to, `0x141e31200`), four u32 (+0x3c, +0x40, +0x4c, +0x50), u8 +0x76
(non-zero starts its own movement, `0x141e74970`) and u8 +0x77; its 0x2ba slot is `ret`.
The Gateway sends Movement idle at the roomba's cell and moves it itself with **S→C 0x1ba** (rail to rail, following the
terrain) and 0x1b9 to turn it.

**The paths** (`LabyrinthTrials.RoombaPaths`): a rail joins its nearest rail along each axis within 6° (one run is 5°
off), when each is the other's nearest that way. A roomba faces along its run, the way it sets out (its room line's
angle as a projectile object's rotation, (sin a, −cos a)); where two runs cross under it (the first ground roomba's
and the flying one's), the one along its facing is its own. The Catacombs' rails make a square with four roombas and a
92 × 69 rectangle with two, every roomba of each facing round it the same way (the original's square: clockwise on the
screen, live 2026-10-31), a crooked U for one ground roomba and a run with a 90° corner for the flying one, which go to
their ends and back.

**What it does** (live 2026-10-31, the original): damage for as long as the player is in it, with no buff icon and
no knockback, and it does not block the way. No skill: BuffDefinitions `roomba_damage` ("Death Rhoomba", invisible;
the flying one's `flying_roomba_damage`) carries `base_physical_damage_%_of_maximum_life_taken_per_minute` and
`…_of_maximum_energy_shield_…`, with no values in the client data (nor in the wiki or PoEDB). So a player within 10
cells of a roomba's centre degenerates by 30% of maximum life plus 50% of maximum energy shield a second (our numbers,
fitted live to the original's with `/trial_roomba_damage`, 2026-11-01), physical, energy shield first, and can die of
it. A player who cannot bleed (the panel's `cannot_gain_bleeding` or `virtual_immune_to_bleeding`) takes nothing in
one (live 2026-11-01, the original: a character immune to bleeding stands in a roomba unharmed), as if the cut were a
bleed. When the degeneration starts the Gateway logs it with the player's life regeneration. `/trial_roomba_damage <life %> <energy shield %> [<radius>]` (dev) changes them.
While they are in one the Gateway puts that buff on the player (0x1c1); its BuffVisuals `rhoomba_damage` plays
`labyrinth/traps/roomba/roomba.epk` (the flying one's `flying_roomba.epk`) on them, and the buff goes when they leave
or die.

In the original the pools fall smoothly in it (live 2026-10-31/11-01). The client cannot drain a pool itself: its
pool step (`0x141e6b320`) adds the pool's native recovery rate only when it is above 0, so a negative
`energy_shield_recovery_per_minute` or `life_recovery_per_minute` is ignored (tried 2026-10-31, reverted). So the
degeneration (`ClientState.TrapDegenPerSecond`) is dealt by the player's 16 ms recovery loop (`RunResourceRecovery`,
beside regeneration, so one pool update carries both), and while it runs the player's stat updates carry both rates
as 0 (`PlayerStatsUpdateAtGate`, `SendPlayerEnergyShield`): with its own regeneration predicted up between the
updates and the damage dealt down at each, the pool went in a saw (live 2026-11-01).

## Monsters

In the original there are no monsters among a trial's traps (live 2026-10-31); what stands in its rooms the room file
places among its points (`data/room-points.json` v8):
- a monster by its MonsterVarieties Id: the Imperial Gardens trial's four `Metadata/Monsters/RootSpiders/RootSpiderHidden`
  (Devourers, which rise as the player comes near), normal;
- a pack point `normal`, `magic` or `rare`: one of the area's scattered packs of that rarity, with its boss monster
  (the Lower Prison trial's plaque platform: a Diabolist, `NecromancerEnfeebleCurse`, with skeletons);
- a server spawner (`Metadata/QuestObjects/Labyrinth/…Spawner`, no `.ot` to tell what it spawns): the Lower Prison's
  `PrisonPoacherSpawner`, beside that pack, is a rare Brittle Poacher (`SkeletonBowLightning`), as the original shows a
  rare skeleton archer there (`LabyrinthTrials.Spawners`). `CryptFrostPriestSpawner` and `CremPyroSpawner` are not
  mapped yet.

The Gateway places these for a trial's rooms (a room with the plaque or the trial's door, `AreaPopulation.PlacedRoom`)
and keeps the area's scattered packs' centres `LabyrinthTrials.TrapClearance` (25) cells from every trap, so a trap-free
passage of a trial can still have one (the original: a Diabolist with a few skeletons in the Lower Prison trial's).

## A completed trial

The client counts a trial done (`0x141f69230`, for each of a Labyrinths row's `RequiredTrials`) when the player has
**the row's quest flag** (column 7, a QuestFlags row: `NormalLabyrinthCompletedPrison` …
`MapLabyrinthCompletedCatacombs`, rows 2564–2581, tested by `0x1400f42b0`) or **the league quest flag** of column 1
(263–286; Player +0x1d0, bit id − 1, `0x14012aae0`).
The real game shares trials across a league's characters; the league flags are how, but the Gateway does not save
league quest flags yet, so a trial is the character's own quest flag.
Columns 2 and 3 are not read.
A Labyrinths row is unlocked by its own `QuestFlag` (`LabyrinthComplete1` …).

**The Trial Plaque window** (`AscendancyMapWindowTitle`, built by `0x140b75b90`) is **S→C 0x7b kind 9**: instance
+0x19f8 → `0x140dcda40`, which shows the live HUD's +0x7e8.
It lights each trial from the flags above (`0x140b76170` → `0x141f69230`); the message carries nothing for it.
Kind 0x66 (HUD virtual +0x1028) shows the live HUD's +0xa30, Mirage's Wishes window, not this one: the other HUD class
(`0x140d65b50`) keeps the plaque window at +0xa30.

## Not built yet

- The other traps (spinners, sawblades, lava) and the step-triggered spikes of Izaro's rooms.
- The Labyrinth's other dart traps (`_Single`, `_Delayed`, `_Triggered`,
  the alternate arrow traps).
- The other puzzle parts: `Switch` and `Door_Open` (toggling), `Switch_Multi`, `TimerGears` (timed levers).
- The other trials' spawners (`CryptFrostPriestSpawner`, `CremPyroSpawner`).
- Saving league quest flags, so that trials are shared across a league's characters.
- The Eternal trials' plaques drop an Offering to the Goddess.
