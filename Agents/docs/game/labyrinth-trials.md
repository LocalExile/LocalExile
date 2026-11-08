# Trials of Ascendancy

The trials in the acts that open each Labyrinth tier (#450, roadmap "The Labyrinth" in
[roadmap/world.md](../../roadmap/world.md)).
The Lower Prison's Normal trial is verified with the real client: its door, lever, Locked Door, plaque and the
Trial Plaque window work.
Its spike traps (the cascade ring and the plaque room's checkerboard) are matched against the original game by eye.
The Imperial Gardens' dart traps, the Catacombs' roombas, the Chamber of Sins' sawblades and toggling levers and the
Crypt's spinners, rail switches, levers and door are checked live against the original.
The Crematorium's hot plates, levers and gears are built from the user's description of the original.
The trials' monsters are fitted to it as well; Izaro's rooms' traps are not built yet.

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
| `crypt_intro` | the door, `Door_Closed`, `Switch`, `Switch_Multi`, `Switch_Once`, 12 `LabyrinthSpinner` on `Rail_Visible` and three `Rail_Visible_Swap_Anims01`, a plain `Door`, the Bronze Monograph, the plaque, the portal, `CryptFrostPriestSpawner` |
| `crematorium_intro` | the door, three `Door_Closed` and three `Switch_Once`, five `TimerGears`, nine hot plates, 17 in the walls with their 17 half lines, the Bronze Monograph, the plaque, the portal, `CremPyroSpawner` |
| `intro` / `intro_end` (Chamber of Sins, `RuinedTemple`) | the door, two `Door_Open` and `Switch`, nine `LabyrinthSawblade` on 45 `Rail_Visible`, the Bronze Monograph; the plaque and the portal |

The objects are `server_only` (BaseEvents): the client creates none of them, so the server places every one.

## The objects

Each type's 0x2bb blob follows its own component order (`scripts/re/ot_components.py`); BaseEvents reads nothing
but counts for the 0x2bc component index.

| Type | Components read | Transitionable index |
|---|---|---|
| `Labyrinth/Objects/LabyrinthIntroDoor` | Positioned, BaseEvents, Animated, InteractionAction, Transitionable, TriggerableBlockage | 4 |
| `MiscellaneousObjects/Door` (plain door) | Positioned, BaseEvents, Animated, InteractionAction, Transitionable, TriggerableBlockage | 4 |
| `Puzzle_Parts/Door_Closed` ("Locked Door") | Positioned, InteractionAction, BaseEvents, Animated, Transitionable, TriggerableBlockage, MinimapIcon | 4 |
| `Puzzle_Parts/Switch_Once` (lever) | Positioned, Transitionable, InteractionAction, BaseEvents, Animated, MinimapIcon | 1 |
| `Puzzle_Parts/Switch` (toggling lever) | Positioned, Transitionable, InteractionAction, BaseEvents, Animated, MinimapIcon | 1 |
| `Puzzle_Parts/Switch_Multi` (toggling lever) | Positioned, Transitionable, InteractionAction, BaseEvents, Animated, MinimapIcon | 1 |
| `Traps/Rail_Visible_Swap_Anims01` (rail switch) | Positioned, BaseEvents, Transitionable, Animated | 2 |
| `Puzzle_Parts/Door_Open` (cage) | Positioned, InteractionAction, BaseEvents, Animated, Transitionable, TriggerableBlockage, MinimapIcon | 4 |
| `QuestObjects/Labyrinth/LabyrinthTrialPlaque` | Positioned, BaseEvents, Animated, Transitionable, MinimapIcon, InteractionAction | 3 |
| `Labyrinth/Objects/LabyrinthTrialReturnPortal` | Positioned, InteractionAction, BaseEvents, Animated, Transitionable, MinimapIcon | 4 |
| `Puzzle_Parts/TimerGears` (gears) | Positioned, BaseEvents, Animated, Transitionable | 3 |
| `Traps/LabyrinthLavaAura`, `_3x3`, `_1x3` (hot plates) | Positioned, Animated, BaseEvents, Transitionable, Buffs | 3 |
| `Traps/LabyrinthLavaAura_inert` (wall plate) | Positioned, Animated, BaseEvents, Transitionable | 3 |

Animated carries the room line's model (`LabyrinthDoor.ao`, `doorbar_prison.ao`, `lever_v01_04.ao`,
`labyrinth_plaque.ao`, `LabyrinthAirlockPortal.ao`).
Transitionable state 1 runs `create_untransitioned` (closed, unpulled, dark), 2 the transition: the client plays the
`.ot`'s `on_transition` itself (the door's opening, the plaque's glow, Izaro's speech, the portal opening).
TriggerableBlockage is u8 blocked, then while blocked a u8 count of sections (none sent).

**What the Gateway does** (`LabyrinthTrials` in the Gateway, `Rules.LabyrinthTrials.cs` in QuestObjects):
- on entering a trial's area it creates these objects; a door also closes its line across the way between the
  area's spawn and the plaque on the server's walk estimate (`QuestBarriers`, `ClientState.DoorBarriers`);
- using the trial's door opens it (0x2bc state 2, then 0x1c6 unblocked);
- a lever opens itself and what its room links turn on (below); unlinked (room-points.json's objects), the nearest
  Locked Door that is still closed;
- the plaque sets the trial's quest flag, opens the portal and opens the Trial Plaque window; a done trial's plaque
  is lit when the player comes, but its portal is closed until the plaque is used (live 2026-10-31, the original: in
  every trial), and using the plaque again only opens the window;
- the open portal teleports the player back to the trial's door;
- what an instance opened stays open for later entries (`InstanceState.Transition`).

**The toggling parts** (the Chamber of Sins). `Switch` has `num_states = 3`: state 1 unpulled, 2 pulled
(`on_transition_to_2`, `on_untransition_to_1` play the lever both ways), 3 spent (not used). `Door_Open` has
`num_states = 2` the other way round from `Door_Closed`: state 1 is open (`create_state_1` plays the lifted bars), 2
closed (`on_transition_to_2` lowers them, `on_untransition_to_1` lifts them), and it starts unblocked
(`start_blocked = false`). A lever shuts the cage its `next` links (round its own room) and stops the sawblade its
`pause` links, and that sawblade's own `pause` stops the lone pair's one by the lever rooms; pulled again, the cage
opens and they run on (live 2026-11-01, the original: as the links say). Without links (`room-points.json`'s objects)
`LabyrinthTrials.SwitchedSaws` fits it by distance.
The Gateway pulls the lever both ways (`InstanceState.SetTransitioned`), shuts or opens the nearest `Door_Open` with
it (0x2bc state 2 or 1, 0x1c6, and its Hull `doorbar_ruinedtemple.ao` ±2.2 × ±8 cells on the server's walk estimate)
and stops or starts its sawblades; a later entry of the instance finds them as they were left.
The Crypt's `Switch_Multi` (`num_states = 3`: `create_state_1` and `on_untransition_to_1` play clip 3, state 2 clip 1,
state 3 disables it) goes both ways as `Switch` does, state 3 unused. Both levers there move the rail switches nearest
them ([Spinners](#spinners)).

## Room links

An `.arm` room file ends, after its doodad lines, with a count and one line `from to "name"` per link, `from` and
`to` being indexes of its doodad lines (plain doodads count). The Labyrinth's rooms, the trials' too, wire their
puzzles and traps there (`RoomFile.Links`, `ServerLevels.Level.RoomLinks`):

| Name | From → to |
|---|---|
| `next` | lever → door, gears; gear → gear; door → door; rail → rail; spike → spike; `Trigger_*` → spike; plate → `_Triggered` dart |
| `counter` | lever, crank, gear → `Counter_Handler`, `Counter_Handler_Any`, `Door_Counter` |
| `transition1`–`4` | `Counter_Handler(_Any)`, `Switch_Once_Multi`, `Switch_Multi`, `PressurePlate_ResetMulti_4` → doors, cranks, rail switches, darts |
| `disable`, `disable1`–`3` | door → its levers; lever → the other lever of a pair |
| `previous`, `left`, `right`, `teleport` | rails and swap pieces |
| `reset`, `reset1`, `reset2` | plate → roomba, roomba → rail |
| `delay`, `delayed` | dart → dart |
| `pause` | `Switch`, `Switch_Reset` → spinner, sawblade |
| `tracks1`–`8` | `PressurePlate_Multi` → rails |

`Counter_Handler` and `Counter_Handler_Any` are server-only lines with a stand-in model (a palm tree): nothing to
send. The links give the wiring only; the timings are the `.ot`s', the names' (`Switch_Reset_16`: a crank that
comes back up after 16 s, our reading) or fitted live. `LabyrinthWiring` follows `next`, `counter`, `transitionN`
and `disable`. A toggling `Switch` drives what its `next` and `transitionN` reach and, through its `TimerGears`, what
their `next` reaches (`RT_branch_bottleneck_2`: Switch → TimerGears → Door_Closed, live); where a room has links, a
`Switch` stops only the sawblades its `pause` links name.

## Unblocking a door

TriggerableBlockage has no 0x2bc reader (its vtables' slot 20 is `ret`).
Its own world message is **S→C 0x1c6** (read `0x141fe7cd0`, world handler `0x14218ddf0`): the world header, u8
blocked, and while blocked a u8 count and that many u8 sections.
Unblocked calls the component's +0xb8 (`0x141e47150`), which writes back the cells it saved when it blocked unless
the type's `disable_save` (+0x11) is set; blocked calls +0xb0 with the sections.
Of the `.ot` keys (`0x141e467f0`) `start_blocked` is +0x10, `disable_save` +0x11, `shunt_objects_away` +0x12;
`save_as_diff` is accepted and not kept.

Blocking (`0x141e46b10`) saves the grid under the object, stamps its Hull (`0x141d8ad70`) and keeps the difference;
the unblock adds that difference back (nibbles, clamped at 5). Both do nothing while a byte of the world (entity +0x78
→ +0x111, not identified) is set, and keep no cells in a hideout (the area's WorldAreas row, world +0xb0, byte +0x14e
IsHideout).

An object with a **static position** (the type's +0x158 → +0x19 set and Positioned +0x1e7 bit 1 clear; "Has Static
Position" in the info dump `0x141e36650`) has its Hull stamped by the client's object pass `0x141d8bd20` (and the
placement paths `0x141d87bb0`, `0x141d87640`) whatever its TriggerableBlockage says; for any other object the pass
stamps it only while the blockage is blocked. A static door stamped so before it blocked keeps a zero difference, and
its unblock frees nothing. Live 2026-10-31 the Locked Door (`doorbar_prison.ao`) stayed closed to walking after 0x1c6,
and also when created again open and unblocked (a movement skill still crossed it), and so did the trial's door in The
Catacombs, though The Lower Prison's opened. So the Gateway sends the trial doors' Positioned with flags1 0x20
(+0x1e7 bit 1: no static position; the client's own Positioned writer `0x141e33100` carries the bit too)
and 0x08 with 1 (+0x1e6 bit 0x40: a dynamic object that blocks no walker; [04](../protocol/04-player-entity.md)), so
only the blockage closes their cells.
The Gateway still removes an opened Locked Door (0x2bd) 1.5 s after its opening, when the bars have lifted, and does
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
The spikes show risen from about 400 ms (live 2026-11-01: a hit from 100 ms came before them) to 1000 ms (the
stretched clip's half), and lowering until 2000 ms.

**The pattern.** The real timers are the rooms' `Trigger_*` lines (`WG` is the line's flags, not the type): server
objects with no model whose `.ot` (BaseEvents `server_only`, `no_serialise`) has a LabyrinthTrigger component,
`trigger_method "timed"` and `next_only_path 1`:

| Trigger | `trigger_frequency` | `delay_between_traps` | `start_delay` |
|---|---|---|---|
| `Trigger_CascadeFast` | 5000 ms | 200 ms | |
| `Trigger_Delay_4_0`, `_4_2` | 5000 ms | 0 | 0, 2500 ms |
| `Trigger_Delay_3_0` | 3000 ms | 0 | |
| `Trigger_PulseSlow`, `_Delay` | 6000 ms | 450 ms | 0, 3000 ms |
| `Trigger_PulsePrison` | 7200 ms | 450 ms | |

Where a room has them the Gateway uses them (`LabyrinthTrials.TriggeredSpikes`): a trigger fires the traps joined to
its nearest trap, one step a tile from it; two triggers on the same traps share them as a checkerboard of the traps'
own tiles (live 2026-11-01, the original: a diagonal line of four rises from its `Trigger_CascadeFast` end).
The Normal trials' rooms reach the Gateway without them (`room-points.json` has no trigger lines), so their pattern is
fitted by eye against the original (live 2026-10-31):
- a ring (every trap side by side with two others) cascades from its trap nearest the trial's lever, straight towards
  its trap nearest the trial's doors and on round, 450 ms a trap, starting again at once;
- any other chain fires as a checkerboard: the tiles of the start's colour, then the others 2.5 s later, every 5 s.
  These are the timers of the `Trigger_Delay_4_0WG` / `_4_2WG` lines the Lower Prison plaque room
  (`Prison/Rooms/Unique/axiom_intro_end.arm`) places at (46, 46) and (69, 46), on two of its traps (#576).

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
  the client's walk speed, `0x141e827a0`) nor native TakeHit recovery was right.

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
flying one Positioned, BaseEvents, Animated, Stats, Buffs and Movement. Movement's reader (`0x141e7a870`) takes u8
(+0x30), i32 x, i32 y (a cell it sets Positioned to, `0x141e34540`), four u32 (+0x3c, +0x40, +0x4c, +0x50), u8 +0x76
(non-zero starts its own movement, `0x141e7b000`) and u8 +0x77; its 0x2bc slot is `ret`.
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
cells of a roomba's centre degenerates by 40% of maximum life plus 50% of maximum energy shield a second (our numbers,
fitted live to the original's with `/trial_roomba_damage`, 2026-11-01), physical, energy shield first, and can die of
it, energy shield and life alike, whether or not they can bleed (live 2026-11-01, the original: a character immune
to bleeding stood in one only by out-regenerating it). When the degeneration starts the Gateway logs it with the
player's life regeneration. `/trial_roomba_damage <life %> <energy shield %> [<radius>]` (dev) changes them.
While they are in one the Gateway puts that buff on the player (0x1c1); its BuffVisuals `rhoomba_damage` plays
`labyrinth/traps/roomba/roomba.epk` (the flying one's `flying_roomba.epk`) on them, and the buff goes when they leave
or die. A roomba glides at 35 cells a second, so one that passes a player standing on its run cuts them for about
half a second.

In the original the pools fall smoothly in it (live 2026-10-31/11-01). The client cannot drain a pool itself: its
pool step (`0x141e718c0`) adds the pool's native recovery rate only when it is above 0, so a negative
`energy_shield_recovery_per_minute` or `life_recovery_per_minute` is ignored (tried 2026-10-31, reverted). So the
degeneration (`ClientState.TrapDegenPerSecond`) is dealt by the player's 16 ms recovery loop (`RunResourceRecovery`,
beside regeneration, so one pool update carries both), and while it runs the player's stat updates carry both rates
as 0 (`PlayerStatsUpdateAtGate`, `SendPlayerEnergyShield`): with its own regeneration predicted up between the
updates and the damage dealt down at each, the pool went in a saw (live 2026-11-01).

## Sawblades

The Chamber of Sins trial's trap room (`RuinedTemple/Rooms/Unique/intro.arm`) has nine
`Metadata/Terrain/Labyrinth/Traps/LabyrinthSawblade` (`sawblade.ao`) on tracks of `Rail_Visible` pieces
(`sawblades_track_straight_v01_01.ao`, `…_end_…`): server objects (Positioned, BaseEvents, Animated) the client
shows, so the Gateway creates them too. A track is one or two lanes 9 cells apart, its pieces 23 cells apart along the
piece's angle. In the order the way meets them: a two-lane track with a sawblade on each lane, a one-lane track, then
round the two lever rooms three two-lane tracks with a pair of sawblades side by side (`Switch` in each room, a
`Door_Open` in each room's way in).
The `.ot`: Movement `FollowPath`, `reverse_when_path_ends`, `base_speed` 28 (`LabyrinthSawbladeSlow` 16), no pause at
a waypoint, `orient_to_first_movement`; Transitionable `on_transition` plays `spin_saw_toinactive` (the spin winds
down), and nothing for the way back. Its blob is the roomba's bytes (Positioned, BaseEvents, Animated, Stats, Buffs,
Movement, Transitionable).

**The paths** (`LabyrinthTrials.RoombaPaths`, as the roombas'): a visible rail joins only along its own piece, or the
ends of a track's two lanes, 9 cells apart, close a ring. A sawblade's room line angle lies along its track; it heads
first for its lane's nearer end (`RoombaPath.TowardNearerEnd`): the first track's two, each nearer one end, part from
its middle as in the original (live 2026-11-01), and each pair goes together. It keeps its room line's angle: at an
end it goes straight back without turning (the original; its `.ot` has no `rotate_to_movement_direction`), so the
Gateway sends a sawblade no 0x1b9.

**What it does**: BuffDefinitions `sawblade_damage` ("Death Rhoomba", invisible, BuffVisuals `sawblade_damage`
playing `labyrinth/traps/sawblade/sawblood.epk`) takes the roomba's two stats, with no values in the client data. So a
running sawblade cuts as a roomba does: a player within 5 cells (a lane) of its centre degenerates by 40% of maximum
life plus 50% of maximum energy shield a second (our numbers, the roomba's) with that buff on them;
`/trial_saw_damage <life %> <energy shield %> [<radius>]` (dev) changes them. A stopped one cuts nothing.

**Stopping and starting.** A lever stops a sawblade where it is: 0x2bc Transitionable 2 (its spin winds down) and a
0x1ba to its own cell. Its `.ot` plays nothing on the way back to state 1, so started again it is removed (0x2bd) and
created anew there, spinning, and goes on to the rail it was heading for.

## Spinners

The Crypt trial's room (`Crypt/Rooms/Unique/crypt_intro.arm`) has twelve `Metadata/Terrain/Labyrinth/Traps/LabyrinthSpinner`
(`spinning_morning_star_v01_01.ao`, its `spin` clip by default) on tracks of `Rail_Visible` pieces
(`spinner_rail_end`, `_corner_v01` / `_v02`, `_crossing_v02`; the straight pieces between them and the X crossing are
the client's own doodads) and three `Rail_Visible_Swap_Anims01` rail switches: two `SpinnerStopper_Straight` and one
`SpinnerStopper_TriCorner`.
The spinner's `.ot` is the roomba's: Movement `FollowPath`, `reverse_when_path_ends`, `base_speed` 24, no pause and no
turning; StateMachine `define_shared_state = "killswitch"` (0 plays clip 1, `spin`; 1 plays clip 0, `breakage`, to its
end). Its blob reads Positioned, Animated, BaseEvents, Stats, Movement, Buffs and StateMachine (one state, 0), in that
order (`LabyrinthTrials.SpinnerBlob`); the Gateway moves it with 0x1ba as the others.
A rail switch's Transitionable plays clip 0 in state 1 and clip 1 in state 2: a straight stopper's `activate` then
`deactivate` (its wedges up, then down), the TriCorner's `switch_corner` then `switch_straight`.

**The track** (`LabyrinthTrials.SpinnerTrack`): each piece leads its model's ways from its angle (direction
(sin a, −cos a)): an end one, back from a; a corner a and a + 90°; a crossing four; a straight stopper a and a + 180°;
the TriCorner a and a ± 90°. A way joins the nearest piece along it (within 6°) that leads back, so the X's two
diagonal runs join across it. A spinner sets out along the run it stands on, the way it faces, and at each piece:
- turns back at an end, and at a straight stopper in state 1;
- goes round a corner;
- at a junction goes straight on when its switch is in state 2 (the TriCorner itself; the crossing follows the
  TriCorner of its track), else the way it turns its corners (both of the upper room's spinners face round their
  square the same way).

What the room makes of it, in the original (live 2026-11-01, the user's observation; map numbers in the room file's
order): two spinners side by side in the way up from the Locked Door run to their stoppers and back until the
`Switch_Multi` between them lifts both, then to their lanes' ends and back; three go round a square clockwise on the
screen; one runs a U to and fro, two the diagonals of an X, two straight lanes; the upper room's two go round the two
small rings of its square (its middle, from the crossing to the TriCorner) until its `Switch` sets the TriCorner
straight, then round the square. The `Switch_Once` opens the `Door_Closed`, as in the other trials, while the spinners
run on. A lever moves the rail switches its links name (`Switch` `next`, `Switch_Multi` `transition1`, `2`); without
links, the nearest (`LabyrinthTrials.SwitchedRails`).
The original has a second gate at the crossing, which the room file has no switch object for.
Between the upper spinner room and the plaque room stands a plain `Metadata/MiscellaneousObjects/Door`
(`thinwallbar_door.ao`, `start_blocked`, opened by a click; the only room of the data with one). The Gateway places it
closed and opens it as the trial's door, its Hull (±2.3 × ±5.5 cells) closed on the server's walk estimate meanwhile:
without it the plaque room's monsters came through to the player in the spinner room (live 2026-11-01).

**What it does**: its `.ot` names no buff, so a spinner cuts as a roomba does (our reading): within 9 cells of its
centre (its mesh's bounding box, about ±100 units) the player degenerates by the roomba's shares with `roomba_damage`
on them.

## Hot plates

The Crematorium trial's room (`Prison/Rooms/Unique/crematorium_intro.arm`) is one room from the trial's door to the
plaque: a first room, a hall, a last room and the plaque's, three `Door_Closed` between them, each with its
`Switch_Once` and one or two `TimerGears` beside it.
Its floor has hot plates: `Metadata/Terrain/Labyrinth/Traps/LabyrinthLavaAura` (`hotplate.ao`, a tile),
`LabyrinthLavaAura_3x3` (`hotplate3_3.ao`, three tiles a side) and `LabyrinthLavaAura_1x3` (`hotplate1_3.ao`, three
tiles along its angle by one: the room's plates and walls meet them so, edge to edge). Seventeen
`LabyrinthLavaAura_inert` plates (`hotplate.ao`, no Buffs) stand half in the walls, an invisible
`LabyrinthLavaAura_half` line (`invisible_halftile.ao`, no client model, Buffs, `num_states = 3`) a quarter tile off
each one's centre on the floor side: half a tile along its angle by a tile. In the original the lava glows under the floor's grates all
round the walls and the stairs' feet, and burns there but not on the stairs (live 2026-11-01): the wall plates heat
with the others, and their half lines (server objects only) burn with them.

Every plate line has a fixed height, 125 (world z, down): the plates lie under the floor's grates and glow through
them. The Gateway sends it in Positioned (f0 0x04; `room-points.json` v9 keeps a server object's `z`); standing on the
terrain instead, a hot plate showed above the grates with its lava heaped on it, and a wall plate as a raised box
by the wall (live 2026-11-01). Other trial lines have heights too (the doors' between −125 and 157, some levers' and
plaques'), not sent: those objects were checked live on the terrain, and whether a line's height is the tile's or
the world's is not known.

A plate's blob reads Positioned, Animated (the room line's model; the `.ot` has `serialise_animation_progress`, not
sent), BaseEvents, Transitionable and Buffs (none), in that order. Its Transitionable: `create_untransitioned` holds
clip 2 paused at its start (cold); 2 plays clip 2, `hotplate_start` (5958 ms, its rig `hot_plates.amd`), then loops
clip 1, `hotplate_idle` (958 ms, aflame); back to 1 plays clip 0, `hotplate_end` (6833 ms, cooling).

**What it does** (live 2026-11-01, the original): the room's plates heat and cool together, about 5 s at their peak
and 6 s cold once cooled, and a plate burns the player standing on it while its lava lies on it, from when the lava
spills over it in the heating until it has drained in the cooling. The clips carry no events; the plate's sounds
(`hotplate.ao`) mark it: its burning from 2812 ms into `hotplate_start` to its `End` at 3269 ms into `hotplate_end`.
BuffDefinitions `labyrinth_lava_aura` ("Lava", invisible, BuffVisuals `lava_aura_ignited`: the player burning) carries
`base_fire_damage_%_of_maximum_life_taken_per_minute_in_normal` and `…_of_maximum_es_…` (and Cruel and Merciless
ones), with no values in the client data; the wiki: fire damage over time, less fire resistance, no burning ground.
So the Gateway heats the room's plates together, keeps them at their peak 5 s after the heating clip, cools them and
rests them 6 s cold, over and over; while the lava lies a player on a hot plate or a wall plate's half line degenerates by 40% of maximum life plus
50% of maximum energy shield a second (our numbers, the roomba's: it felt the same in the original) less their fire
resistance, energy shield first, with that buff on them, as a roomba's cut is dealt (`ClientState.TrapDegenPerSecond`).
`/trial_lava <life %> <energy shield %> [<peak s> [<cold s>]]` (dev) changes them.

**Levers and gears.** Each lever opens the nearest Locked Door still closed (the room's way meets them in that order,
so each opens its own: live 2026-11-01, the original) and sets the gears beside it turning: TimerGears
(`Cogs04_Quiet.ao`, `num_states = 3`) holds clip 1 (`start`) paused in state 1; to 2 it plays `start` (1000 ms), then
loops clip 0, `idle` (3333 ms); clip 2, `stop`, is for the way back (not used). Each gear goes with its nearest lever
(`LabyrinthTrials.TurnedGears`). Despite their name they turn on: the doors stay open.

## The Labyrinth's own rooms

Each `1_Labyrinth_*` area (WorldAreas `IsLabyrinthArea`) gets the trials' traps, levers and doors as above, without
plaque, flag or portal (`LabyrinthTrials.InLabyrinth`). Their objects are the Gateway's generated rooms' lines
(`ServerLevels`): `room-points.json` keeps only the Labyrinth's rooms with points, not its trap rooms.

**Tile objects.** The Labyrinth's tiles place server objects of their own (`AreaObjects.TileObjects`, not room
lines, so without links): the ElegantHouse doorways' roller traps (`LabyrinthRollerTrap`, `_02`, `_03`), most abyss
edge blades, `LabyrinthSmashableDoor` and `HiddenDoor_Long` (`ServerLevels.Level.TileLabyrinthObjects`, after the
room objects). The rollers have the cascade trap's components, at their tile line's
height (the first hangs 226 up, under the lintel). The first (`elegant_doorway_trap.ao`) is a spinning roller: the
Gateway plays its rig clips by 0x1b2 (`down`, `idle_bottom` for its .ot's `base_skill_effect_duration` 3.5 s, `up`,
then `idle_top`, by their order in its rig.amd), and while down it closes the doorway and cuts whoever is by it as a
roomba does. `_02` and `_03` are spikes rising from the floor, played by their clips too (`up`, `idle_up` for
`trap_idle_duration`, `down_medium`; their sounds are on these, none on `spike`): a spike trap's hit and bleed each
strike: `_03` once, at `up`'s contact; `_02`, whose spikes are long and reach beside the doorway too (±7 cells across, its wall only
its Hull's ±2.5), rising at `up`'s
`contact` and swinging at its `secondary` and at each round's `secondary` of `idle_up` (625 ms) while out (live
2026-11-01: it rises and swings four times, one hit a swing; `LabyrinthTrials.RisingSpikes`). Each strikes a player in the strip once, until the next, and throws them a little
back. They keep the doorway closed until back down. The tiles' blades swing as the rooms'. Live
(2026-11-01, the original): every tile trap closes its doorway or strip while out, throws a player it hits a little
back, and stays hidden a good while between goes (`LabyrinthTrials.TileTrapRestSeconds`: 5 s, ours). Their traps run
apart from the rooms' (`SpawnTileTraps`), each starting its own while after the area's start.

**Smashable and hidden walls.** `LabyrinthSmashableDoor` (ElegantHouse tiles' thin walls) extends
`Smashable` (`transition_on_damage_taken`, attackable), at its tile line's height (101 up; on the terrain its upper
part sank into the floor, live 2026-11-01) and with its static position, so the client stamps its Hull for walking and
projectiles until its transition plays the break and removes it (`RemoveCollidability`). A click on it, or a cast
within `LabyrinthTrials.SmashReach` (the Gateway traces no skill hits on objects), breaks it. Live (2026-11-01, the
original): a shortcut, or the way to a secret (an altar, another room). `HiddenDoor_Long` (a DryGarden tile's secret
wall) opens on its transition; the disguised `HiddenDoor_Switch` levers by it (`hide_hover`; four round DryGarden
`bottleneck_07`'s) link nothing: a switch belongs to its nearest secret wall, which opens once both switches on one
side of it are pulled (live 2026-11-01, the original: two on each side, either pair). The rooms' gates (`DryGarden_SmallGate`, `RuinedTemple_SmallGate`)
swing open and stay; only the trials' bars go once opened (`LabyrinthTrials.GoesWhenOpened`).

**Statues.** The DryGarden tiles place `Metadata/Monsters/Statue/*/Spawner_LabyrinthStatue*` (extending
`StatueSpawner.ot`: server-only, Transitionable, blocked; its transition plays the statue's animation tree and hides
its stone). Each wakes into the monster of its name (`Spawner_X` → `Metadata/Monsters/Statue/X`: Overgrown Colossus,
Fencer, Archer, Maul, Martial Statue) the spawner stands transitioned (its stone hidden, its pedestal
left), and the monster, a stone statue with its glow (its .ao's attached `statue_balls`), waits on the pedestal in its
Emerge clip's first frame (`wake_*`, index from data/monster-takehit.json, 0x1b2 at speed 0), turned as the statue. As
the player comes within `LabyrinthTrials.StatueWakeCells` (30, ours) or hits it, its Emerge plays: it steps down off
the pedestal. The statue types have no Transitionable, so the emerge monsters' rise does not take them. Live
(2026-11-01, the original): the waiting statue glows; a spawner breaking and a monster made apart looked jerky. No
MonsterPacks or MonsterGroups row names a Labyrinth area: its other monsters are the server's own choice among the
`*Labyrinth` varieties (Putrescent Contender, Maze Needleback, Shadow Lurker, the Claimants and Deceivers …). Ours
(`LabyrinthTrials.RoomSetPacks`, scattered as any area's packs, `TrapClearance` off the rooms' and tiles' traps), by
the area's room set: live (2026-11-02, the original) undead below ground (Prison, Crypt), Needlebacks, Shadow Lurkers
and Devourers above, Muck Elementals out of ElegantHouse's fountains, a summoner always alone among its minions (the
pack's boss member), and about a fifth denser than an ordinary area (`LabyrinthTrials.PackDensity`). The monsters'
own skills (Shadow Lurkers' Discharge, the Putrescent Contenders' tar, the Needlebacks' death nova) wait on the
monster skills of [roadmap/monsters](../../roadmap/monsters.md).

**Mirrored rooms** (orientation codes 4–7) mirror a line's angle only, and the client draws a server object
unmirrored, so a corner piece (`spinner_rail_corner`, `SpinnerStopper_Corner`) is sent a quarter turn back
(`LabyrinthTrials.PlacedAngle`): a mirrored L is the L turned a quarter (live 2026-11-01: corners faced away).

**Spinner rooms' swap piece.** Under each spinner of the Crypt set's two-lever rooms lies a
`Rail_Visible_Swap_Anims10` (`SpinnerStopper_Corner2`; the `.ot` is `Anims01`'s with the clips the other way round),
the corner between the track's short part (round its corner) and its long part. The spinner runs one part and turns
back at the piece; each part has a `Switch` with its gears. Live (2026-11-01, the original): it starts on the short
part with the long part's lever pulled and its gears turning; pulling the short part's lever sends the spinner to the
long part, and the two levers switch each other off. The piece's Transitionable state 1 shows its long arm; a mirrored
room's piece is sent the other state.

**ElegantHouse hot plates.** `LabyrinthLavaAura_EH`, `_1x3_EH`, `_3x3_EH` and `_inert_EH` extend the trial's
plates with their own `hotplate*_EH.ao`; the Gateway runs them as the Crematorium's (`LabyrinthTrials.Plate`). Live
(2026-11-01, the original): a room's plates all heat together, with the Crematorium's timings and burn.

**Pressure spike traps** (`LabyrinthSpikeTrap_Pressure`, `_EH_Pressure`; Positioned, Animated, Transitionable,
TriggerableBlockage, then as the cascade trap): one tile, `spike_fast` as the cascade's. Their `object_size` 11 made
the client wall the player off round them until Positioned flags1 0x08 (blocks no walker, as the doors). Live
(2026-11-01, the original): a step on one clicks (`spiketrap_trigger.ao`, played as an effect object on it) and it
rises about 1 s later, on its own; it fires again only after the player steps off it and on again.
`LabyrinthCascadeSpikeTrap_EH` (`spiketrap_house.ao`) is the trial's cascade trap.

**Abyss edge blades** (`LabyrinthAbyssEdgeBlade`, `_Rhythm1`–`3`, `_Offset1s`, `_Offset2s`: one .ot; `_Short` with
`ShatteredAbyssEdgeBlade.ao`; Positioned, BaseEvents, Animated, TriggerableBlockage, Pathfinding, Stats, Buffs, Life,
Transitionable, Actor). Emerge plays `blade.ao`'s `hit`: the swing at 421 ms, the impact at 601, the end at 1795.
At the impact a player in the blade's strip (its Hull, about ±2.4 × 25 cells along its angle, widened to ±4 × 28: live,
a little wider than the Hull) takes `LbEdgeTrap`'s hit (25% of life, 15% of energy shield, 20% more against players)
and bleeds: the spike's bleed with its −45% damage and −40% duration (3 s). The timers are the server's: ours, every
3 s, `_Rhythm2` / `_Offset1s` 1 s later, `_Rhythm3` / `_Offset2s` 2 s. Live (2026-11-01, the original): the rhythm
matches.

**Dart traps** (`LabyrinthTrials.Volley`). The rooms' wall darts (`walldart_*.ao`: `LabyrinthArrowTrap_Single`,
`_SingleSlow`, `_SingleSlowOffset`, `_Delayed`, `_Triggered`) shoot one dart ahead (`projectiles_nova 0`); the
`AlternateArrowTraps/` turrets a spiral nova (`base_number_of_projectiles_in_spiral_nova` darts over
`projectile_spiral_nova_angle` in `projectile_spiral_nova_time_ms`: Two90 two over 90° in 756 ms, Two180 two over
180° at once, Three three over 180° in 1024 ms). `_Triggered`'s .ot reads Animated before Transitionable.
The periods are the server's: the trial's 1.5 s, the slow ones 3 s, a delayed or offset one half a period later.
The `PressurePlate_Reset*` plates (`dryGardenPlate_01.ao`; Positioned, Transitionable 1 up / 2 down, BaseEvents,
Animated): a player stepping on one presses it and the `_Triggered` darts it links fire (its `next`, a
`_ResetMulti_4`'s `transition1`, `2`, and on along each dart's `next`, `delay`, `delayed`, all at once); unlinked, every
`_Triggered` of its room;
`Reset` comes up when the player steps off, `_4` 4 s after, `_ResetMulti_4` pressing again while they stay.
Live (2026-11-01, `1_Labyrinth_DG_end` seed 1): all as the original.

**Roomba runs**: a roomba's `reset` link names its first rail, and it follows the rails' `next` (a `teleport`, a jump)
until a rail comes again, then goes round from there (`LabyrinthTrials.LinkedPath`): `indoor_trap_var2_2`'s ground
roomba comes in to a ring of three and its flying one goes round the same three the other way; a roomba without one
runs the linked rails it stands on. Without links (`LabyrinthTrials.ScratchedRuns`) the rooms' scratch decals (`LabyrinthRoombaKit/R_scratch_*`) lie along every ground roomba's run: a run
joins two rails at most 100 cells apart with no rail between them and a scratch along it, the most scratched first and
none crossing another (a run across a ring's slant passes one of its scratches). A flying roomba scratches nothing:
the rails left join shortest first, at most two runs a rail. A roomba takes the nearest run of its kind (its room line
stands up to 13 cells off it), whatever way it faces. Checked live on `1_Labyrinth_OH_end` seed 1 (2026-11-01): a
ground ring of three and a flying ring of four in `indoor_trap_var2_2`, two chains of four and one of two in
`2x2_trap_05`.

## Monsters

In the original there are no monsters among a trial's traps (live 2026-10-31); what stands in its rooms the room file
places among its points (`data/room-points.json` v8):
- a monster by its MonsterVarieties Id: the Imperial Gardens trial's four `Metadata/Monsters/RootSpiders/RootSpiderHidden`
  (Devourers, which rise as the player comes near), normal;
- a pack point `normal`, `magic` or `rare`: one of the area's scattered packs of that rarity, with its boss monster
  (the Lower Prison trial's plaque platform: a Diabolist, `NecromancerEnfeebleCurse`, with skeletons);
- packs the original has where the room file has no point (`LabyrinthTrials.RoomPacks`, room cells, added to the
  file's points): the Chamber of Sins trial's two normal packs on the open ground before the upper lever room's cage,
  where the way comes down the one-lane track (live 2026-11-01: none came there among the scattered packs, which keep
  clear of the tracks); the Crypt trial's normal pack just past its door and a second in the plaque room (live
  2026-11-01: skeletons and spiders at the way in, spider packs by the plaque);
- a server spawner (`Metadata/QuestObjects/Labyrinth/…Spawner`, no `.ot` to tell what it spawns): the Lower Prison's
  `PrisonPoacherSpawner`, beside that pack, is a rare Brittle Poacher (`SkeletonBowLightning`), as the original shows a
  rare skeleton archer there (`LabyrinthTrials.Spawners`); the Crypt's `CryptFrostPriestSpawner`, by the plaque, a rare
  Frost Priest (`SkeletonCasterCold4`, by its name; the original shows rare skeletons there); the Crematorium's
  `CremPyroSpawner`, by the plaque beside the room's normal pack point, a rare Cinder Elemental (`MossMonster/FireMonster`,
  the area's burning rock monster: the original shows a rare pack of what look like stone golems there); the
  Crematorium's packs are `RoomPacks`: one in the first room and one past the second Locked Door, on open floor clear
  of the plates (the original: skeletons at the way in, monsters past the second door, none in the hall's plates).
  Its four `Metadata/Monsters/LavaTrap/LabyrinthElementalSpawner` points are monsters, the **Immolator** (wiki: an
  iron maiden that brings Ember Claimant skeletons; only on the plates, live 2026-11-01): an immobile monster whose
  `.act` plays `spawn` for Melee, whose one skill `SummonLabyrinthFireSkeleton` (2170 ms cast, 3 s cooldown) has
  `alternate_minion` 73 (SummonedSpecificMonsters → `SkeletonMeleeFireLabyrinthSummoned`, Ember Claimant),
  `number_of_monsters_to_summon` 1, `summon_specific_monsters_in_front_offset` 114 and
  `summoned_monsters_no_drops_or_experience`. The Gateway's monsters use a summoning skill so
  (`MonsterAttack.Summon`): within their aggro range, off cooldown, with fewer than five of their monsters alive (our
  cap), it opens the oven and at half its cast brings the monster that far ahead towards the player; it gives no
  experience or items and is not kept in the instance. The skill's action (0x1ab, ActionTypes
  `SummonSpecificMonstersInFront`) plays nothing on the client (live 2026-11-01: the oven stayed shut), so the Gateway
  plays the actor's clip instead: 0x1d1 with Animation.dat `SpellAreaOfEffect` (22), the `.act`'s `spawn`, over the
  cast time.

The Gateway places these for a trial's rooms (a room with the plaque or the trial's door, `AreaPopulation.PlacedRoom`)
and keeps the area's scattered packs' centres `LabyrinthTrials.TrapClearance` (25) cells from every trap, so a trap-free
passage of a trial can still have one (the original: a Diabolist with a few skeletons in the Lower Prison trial's).

## A completed trial

The client counts a trial done (`0x141f6fb00`, for each of a Labyrinths row's `RequiredTrials`) when the player has
**the row's quest flag** (column 7, a QuestFlags row: `NormalLabyrinthCompletedPrison` …
`MapLabyrinthCompletedCatacombs`, rows 2564–2581, tested by `0x1400f4540`) or **the league quest flag** of column 1
(263–286; Player +0x1d0, bit id − 1, `0x14012ae40`).
The real game shares trials across a league's characters; the league flags are how.
The plaque sets both: the character's quest flag and the trial's league quest flag, which the Gateway saves with the
account's stash of that league (`Game.SetLeagueQuestFlags`, [saves](../saves.md)), so every character of the account
in the league has the trial done.
`/trials [on|off] [1|2|3|4]` (dev) sets or clears both flags of one Labyrinth tier's trials (Labyrinths
`RequiredTrials`: 1 Normal's six, 2 Cruel's three, 3 Merciless's three, 4 Eternal's six) or of all, and opens the
Trial Plaque window.
Columns 2 and 3 are not read.
A Labyrinths row is unlocked by its own `QuestFlag` (`LabyrinthComplete1` …).

**The Trial Plaque window** (`AscendancyMapWindowTitle`, built by `0x140b71170`) is **S→C 0x7b kind 9**: instance
+0x19f8 → `0x140dcb2d0`, which shows the live HUD's +0x7e8.
It lights each trial from the flags above (`0x140b71750` → `0x141f6fb00`); the message carries nothing for it.
Kind 0x66 (HUD virtual +0x1028) shows the live HUD's +0xa30, Mirage's Wishes window, not this one: the other HUD class
(`0x140d632c0`) keeps the plaque window at +0xa30.

## The Lord's Labyrinth quest

Quest rows 51–61 (`labyrinth_a1` … `labyrinth_a11`, "The Lord's Labyrinth", one per act and one for the maps) have no
script: the client picks their tracker text from the flags (QuestStates, [12](../protocol/12-quests.md) §1).
Besides `FlagsPresent` / `FlagsMissing`, a row lists league quest flags that must be set (column 13: the trials of
the tiers before) or clear (column 14: the area's own trial), and the areas it shows in (`Area`).
In a trial's area the first matching row gives:

| Flags | Tracker |
|---|---|
| `LabyrinthQuestReceived`, the area's trial not done | Solve the Trial of Ascendancy in this Area |
| the six Normal trials done, no `LabyrinthAccess` (act 3: also in Sarn) | Enter the Labyrinth (in the Sarn Encampment) |
| `LabyrinthAccess`, no `LabyrinthComplete1` | Complete the Labyrinth |
| `LabyrinthQuestReceived`, not all done | Solve the Trials of Ascendancy |

The Cruel and Merciless rows go on with `LabyrinthComplete1` / `LabyrinthComplete2` and the tiers' trials.
The Gateway sets `LabyrinthQuestReceived` (`Quests.LabyrinthQuestReceived`) when the character enters an area with a
trial, before the player object is sent; which event sets it in the real game is not known.
`LabyrinthAccess` comes with the plaza's waypoint; Izaro's last trial sets `LabyrinthKilledIzaro` and the tier's
`LabyrinthComplete` flag (below).

## Izaro's arenas

Each section of the day's layout ends in an Aspirant's Trial: the node with `SectionEnd` (slot + 1; the Normal layout
has three). Its area (`N_Labyrinth_boss_K`, the last section's `…_end`) is the corridor (`boss_entry_*`, from the
LabyrinthNodeOverrides) and its "secret" node, which the node's first secret turns into the day's mechanic room
(LabyrinthSecrets `Id2`: `boss_statues`, `boss_gargoyles`, …; the "…2" rows add the mechanic again later and have no
room); the last area's arena is `labyrinth_final_boss`, with the Ascendancy Chamber behind it.
The server places, from the tiles: the corridor's gate `LabyrinthIzaroDoor` in front of the way into the arena
(`LabyrinthIzaroArenaTransition`), `Elevator_Izaro` in the arena's middle, two `LabyrinthIzaroArenaDoor` at its ways in
and out, three `BossObjectiveMarker` in an alcove (the mechanic's places) and, in the last arena, the ten
`IzaroChestMarker`.

- The arena doors' states are the other way round from the gate's: LargeDoor.ao's clips are 0 `closed`, 1 `open`;
  the arena door's state 1 pauses `closed` at its start (open), 2 plays it (shut), back to 1 plays `open`; the gate's
  1 pauses `open` at its start (shut), 2 opens it.
- The elevator: 1 lowered, 2 raising (10.666 s), 3 raised, 4 lowering (3.333 s), 5 lowered. Izaro (LabyrinthBoss.ot,
  5 states) is made in 5 (down, untargetable); his Emerge is `lift_raise_*` (10.733 s), state 2 makes him targetable,
  4 untargetable; his Hide `lift_lower_*` (rig clips 40–42) is what the server's `on_transition_to_4` plays.
- The original (live, the user): he rises on the elevator as the player comes in; his life bar is split in thirds
  carried from trial to trial (2/3, 1/3, death); at a trial's end he speaks, walks to the elevator's middle, stands and
  goes down on it, and the doors open as he goes. His weapon is the day's and the tier's (wiki); the client does not
  have the pick. A move order sent during his swing was lost on the client. At his trial's floor his boss bar is
  dimmed (live, as Malachai's): the bar draws `UniqueBossFrameImmuneOverlay` once the life is at the
  `cannot_drop_below_%_life` stat (client key 0xca8, checked on each life change, 3.29.3c: 0x14028f1e0), sent with 0x1b3.
  `is_hidden_monster`, `cannot_be_damaged`, `base_cannot_be_damaged` and the invisible `cannot_be_damaged` buff did not
  change the bar. No boss bar while he rises (live): `boss_life_bar` 1 goes only as he starts to fight.
  `disable_life_bar` does not hold it: BossLifeBar.ot's `on_state_boss_life_bar_1` shows the bar whatever
  `disable_life_bar` is, and `on_state_disable_life_bar_0` shows it even with `boss_life_bar` 0.
- His lines are NPCTextAudio rows of NPCs 147 (S→C 0x1c7, [17](../protocol/17-npcs.md)): `Izaro<n>_Prefight` as the
  player comes into the room of the server-only `IzaroSpeech/BossPrefight` object (live; nothing as the gate opens), `IzaroStart<mechanic>` as he rises, `IzaroFlee1`–`6` (trial 1) and `7`–`8` (trial 2), `IzaroDeath1`–`6`.
  The plaza door's line (`IzaroSpeechOpenDoor*`) the client says itself. At a trial's end he says two lines (the
  original, live): the Flee or Death line, then the arena's mechanic's `IzaroFinish<mechanic><n>` (n = how many of its
  three are left; the statues' 1 names the element). `IzaroAll<mechanic>Dead` when the last one is killed.
- The mechanics (LabyrinthSecretEffects `<Mechanic><Tier>_<Element>`: fire, cold, lightning; the "…2" rows are the
  ones an earlier trial left). The arena's three `Elevator_Player` (the wiki's "three small holes") carry its own:
  Toggle Statues rise one at a time, the first with Izaro (EmpowerStatueBase.ot: 1 lowered, 2 raising, 3 off, 4 on, 5 lowering, 6 down), a
  click turns one off for 15 s in Normal (live). The gargoyles (IzaroGargoyle.ot: 1 lowered, 2 `ascending_elevator`,
  3 raised, 4 lowering, 5 `elevator_down_idle`, the state a new one is in) rise together, stand 5 s, then one at a
  time a 10 s beam to Izaro (MiscBeams `IzaroGargoyleBeam`, `awaken_gargoyle` on the gargoyle; the Beam's end follows
  its object, its start stays where it began, so the start is the gargoyle) and it awakens (its Emerge, `awaken`), its
  buff on Izaro until it dies (live). Before its beam it takes little damage, from the beam on all of it (live). Elevators cannot be stood on while they move (live), and what rises can be hit only once it is up (live).
  The portals (IzaroPortal.ot: 1 lowered, 2 raising, 3 raised, 4 lowering, 5 lowered, the state a new one is in) rise
  one at a time; `elevator_ascend` and `elevator_descend` are the .ot's server events (0x1b2, rig clips 0 and 1). Each
  brings guardians (`IzaroSummonSkeletonCaster*`, `alternate_minion` 63–65) by its skill (2.17 s cast, 6 s cooldown, 3 s for the
  "…2" ones). The data says one at a time; the original brings many, so ours brings 4 and the arena has 20 at most
  (the user's numbers). When the trial ends the guardians stay but ignore the player (live).
  `PortalSummon1_*` have no tier and no buff; the "…2" ones (IzaroPortal2.ot, untargetable, `base_cannot_be_damaged`)
  give Izaro `izaro_buff_loot_bonus`. A new IzaroPortal2 is in state 3, whose create event plays the closing clip, so the
  server sends 1 and its idle clip.
- What is left when a trial ends (a statue on or not risen, a gargoyle alive, a portal not destroyed) comes back in later trials whose room
  has the mechanic's "…2" secret: in the arena behind an alcove's railing (`BossObjectiveBlocker`: 1 up, 2 down in
  3.3 s; its three `BossObjectiveMarker`) and on a ledge of the room before it (`BossEntryObjectiveMarker`, in threes).
  The original (live): the first trial's on the right ledge, the second's on the left. The final arena has two
  alcoves; which trial takes which is ours (the first the one with the least y). Statues stand as StatueDoodad1-3 and
  keep their buff; gargoyles (IzaroGargoyle2) come out with all their buffs when the railing lowers as he fights, and
  portals bring their guardians from the alcove once it lowers.
  The arena's gargoyles rise with Izaro; their `ascending_elevator` is the .ot's server event, so the server plays it
  (0x1b2, rig clip 0) or the client leaves them in the hole (live).
- The room before each arena and the Ascendancy Chamber have the account's stash (their `Stash` room object). The
  plaza's device (`LabyrinthDifficultySelect`) follows the door's state: transitioned, it plays `open` (the green
  flame) and its minimap icon goes (`hide_on_transition`).

## Not built yet

- The step-triggered spikes of Izaro's rooms, his arenas' other mechanics and the Goddess of Justice, and the hot
  plates' Cruel and Merciless rooms.
- The Labyrinth's `Door_Counter` puzzles and its rooms' monsters (the ElegantHouse rooms' Immolators among them).
- The Eternal trials' plaques drop an Offering to the Goddess.
