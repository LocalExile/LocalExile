# Trials of Ascendancy

The trials in the acts that open each Labyrinth tier (#450, roadmap "The Labyrinth" in
[roadmap/world.md](../../roadmap/world.md)).
The Lower Prison's Normal trial is verified with the real client: its door, lever, Locked Door, plaque and the
Trial Plaque window work.
Its spike traps (the cascade ring and the plaque room's checkerboard) are matched against the original game by eye.
The Imperial Gardens' dart traps fire; they are built from the original's description and not yet checked live.
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
- the plaque sets the trial's quest flag, opens the portal and opens the Trial Plaque window; using it again only
  opens the window;
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

This opens the trial's door.
The Locked Door (`doorbar_prison.ao`, whose Hull fills the gap between the two posts of the `PDwall_blocking01` doodad
at the same spot) stayed closed to walking after 0x1c6, and also when created again open and unblocked; a movement
skill still crossed it (live 2026-10-31).
The client's object pass `0x141d88690` stamps an object's Hull (`0x141d876e0`) unconditionally for some types and only
while their TriggerableBlockage is blocked for the others (type +0x158 → +0x19, type +0x1e7 bit 1); which flag puts the
Locked Door in the first group is not found.
So the Gateway removes an opened Locked Door (0x2bb) 1.5 s after its opening, when the bars have lifted, and does not
create it again in that instance.

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
  three times that while the player moves, for 1 s, not reduced by armour, only the strongest running.
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

## Monsters

In the original a trial's rooms have none of the area's monster packs (live 2026-10-31: no monsters among the traps).
The Gateway keeps the scattered packs out of the cells a trial room's doodads span (a room with the plaque or the
trial's door, `AreaPopulation.PlacedRoom`), and places the monsters the room file itself names among its points: the
Imperial Gardens trial's four `Metadata/Monsters/RootSpiders/RootSpiderHidden` (Devourers, which rise from the ground
as the player comes near), normal rarity, at the area's level.
The other Normal trials' rooms name none; their monsters come from spawner objects (below).

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

- The other traps (spinners, sawblades, roombas, lava) and the step-triggered spikes of Izaro's rooms.
- The Labyrinth's other dart traps (`_Single`, `_Delayed`, `_Triggered`,
  the alternate arrow traps).
- The other puzzle parts: `Switch` and `Door_Open` (toggling), `Switch_Multi`, `TimerGears` (timed levers).
- The trials' monster spawners (`PrisonPoacherSpawner`, `CryptFrostPriestSpawner`, `CremPyroSpawner`).
- Saving league quest flags, so that trials are shared across a league's characters.
- The Eternal trials' plaques drop an Offering to the Goddess.
