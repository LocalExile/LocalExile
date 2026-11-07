# Status: World, movement and the network

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/world.md](../roadmap/world.md).

## Works (verified with the real client)

- Instance connection: opening, encryption switch (0x11), framing, dispatcher.
  Code: `GameServer.Gateway`, `GameServer.Core/Protocol/Instance`.
  Docs: [02](../docs/protocol/02-instance-connection.md).
- Entering Lioneye's Watch: area (0x1d), local player id, world activation, HUD.
  Code: Gateway.
  Docs: [03](../docs/protocol/03-area-and-world.md).
- Player object (0x2b9) with its component blob; the character's name and class model.
  Code: `InstanceEntities.cs`.
  Docs: [04](../docs/protocol/04-player-entity.md).
- Inventories and passive slots the UI needs (empty).
  Code: Gateway.
  Docs: [05](../docs/protocol/05-inventories-and-ui.md).
- Latency acks (0x1b/0x1c) and the HNC side channel, answered from the read loop ahead of other handlers, the client's
  stale tick queue cleared on each connection (server delay 0xFFFF, so F1 NETWORK reads 0 and spikes only on overdue
  ticks; ticks or handlers over 50 ms are logged), visibility (wake zone), click-to-move. A full skill-list rebuild,
  which holds the send gate, takes 5–10 ms for a 10-gem character (180–270 ms before 2026-10-29).
  Code: Gateway, `Hnc.cs`, `ClientInbox.cs`.
  Docs: [07](../docs/protocol/07-latency-visibility-movement.md).
- The player's walk on the server is the client's own pathfinder and stepping, ported 1:1 (A* with the player's
  ellipse limit, waypoint pruning, Bresenham steps at the client's step time, NPCs and stashes as blockers): on a
  recorded session every one of 424 client paths matched, and the reported cell is exact or within one step (client
  frame timing).
  Walk-ups to NPCs, stashes, waypoints and area transitions use the client's range (object size + 3);
  clicks are timed by their socket read; monster paths port 1:1 too (1,235 of 1,235, 100 expansions + coarse pass).
  Code: `GameServer.Core/World/ClientPathfinder.cs`, `PlayerWalk.cs`.
  Docs: [07](../docs/protocol/07-latency-visibility-movement.md) §5.
- Server frames (S→C 0x2b6 frame length, 0x2b5 frame end every 33 ms): what a lockstep client's world runs on;
  ignored by a predictive client.
  Code: `Game.RunLockstepFrames`, Gateway `WindowsTimerResolution`.
  Docs: [07](../docs/protocol/07-latency-visibility-movement.md) §6.
- Area transitions in every area, with their destinations, models (the arch highlighted on hover), rotation and
  height.
  In a layout the Gateway generates, each exit leads where the graph node of its room says: "entranceN" to
  Connections[N − 1], "teleportN" to the "telepadN" slot as a gate within the area (the Upper Prison's way to the
  Warden's Quarters, boss arenas).
  An exit the layout disables ("entranceNdisabled") is created when a quest gates it (`Quests.FlagToEnter`), and shown
  once the character has the flag, as for any gated exit (Act 3: The Upper Sceptre of God's way to The Aqueduct, The
  Library's into The Archives; Acts 7–10: The Ashen Fields' way north, The Harbour Bridge's into Act 9, Sin's portal
  home from The Feeding Trough; checked offline, not yet with the client on the modules).
  Other areas use the walk-map recorder's capture and the atlas.
  The offline audit `scripts/re/exits` checks every generated area at several seeds (`--quests`: with the act tables,
  so the gated disabled exits too);
  verified town ↔ The Coast ↔ The Tidal Island and The Coast ↔ The Mud Flats; area change through them;
  Ctrl+click opens the Instance Manager (list, join, create); `/goto <area>`.
  Old-area stat and recovery writers retire before their socket closes and cannot replace current equipment qualification.
  Native requirement removal/restoration, four town/Coast transfers, spell payment, ES recovery and relog controls pass.
  Code: Gateway (`AreaTransitionCatalog.cs`), `AreaExits.cs`, `scripts/walkmaps`, `scripts/re/atlas`,
  `scripts/re/exits`, Auth's instance registry.
  Docs: [08](../docs/protocol/08-area-transitions.md) §8, [09](../docs/protocol/09-area-change.md).
- Arrival points: entering through an exit (or `/goto`) puts the player next to the exit back;
  The Twilight Strand → Lioneye's Watch arrives at the town's `entrance1`; in a generated layout a one-way entrance
  arrives at its "entranceNdisabled" point (The Flooded Depths → The Submerged Passage's Depths return);
  Resurrect at Checkpoint revives there, or next to the area's waypoint once the character has activated it.
  `/tp` (dev) teleports the player to a cell or next to a named monster, NPC, transition, waypoint or captured object.
  Code: `AreaLayout.Arrival`, `TeleportTargets.cs`, Gateway.
  Docs: [09](../docs/protocol/09-area-change.md) §4.
- Instances: one per town (all 12 act towns from WorldAreas `IsTown`);
  other areas get private instances that live 6 minutes once empty.
  Code: `InstanceRegistry.cs`, `WorldAreaTowns.cs`.
  Docs: [09](../docs/protocol/09-area-change.md).
- Walk maps and area transitions captured from the client on first visit (optional, `live.ps1 -Walkmaps`: without
  it nothing is recorded); the Gateway uses them for the areas it does not generate itself.
  Code: `scripts/walkmaps`, `live.ps1 -Walkmaps`.
  Docs: [GettingStarted](../GettingStarted.md) step 9, [08](../docs/protocol/08-area-transitions.md) §7.
- Waypoints: the Waypoint object where the area's layout is captured (Lioneye's Watch built in), activated on entry
  (towns), within 25 cells or on click (the client plays the activation), saved per character;
  clicking opens the World panel in travel mode (S→C 0x7b), its pins and connection lines show from the character's
  visited areas and activated waypoints (S→C 0x4c, quest flags in the Player component via 0x2ba), and picking a pin
  (C→S 0x4d, Ctrl → instance chooser) takes the player next to the destination's waypoint; `/waypoints [all | reset]`.
  Code: `InstanceWaypoints.cs`, `WaypointCatalog.cs`, `AreaVisitFlags.cs`, Gateway.
  Docs: [16](../docs/protocol/16-waypoints.md).
- Pause: the Escape menu outside towns and hideouts pauses the area (C→S 0x1d4 → S→C 0x1d3): the Gateway's clock
  stops, monsters neither move nor swing, regeneration and flasks wait, and the client pauses its world;
  closing the menu resumes.
  Code: `InstancePause.cs`, Gateway (`SetPaused`).
  Docs: [22](../docs/protocol/22-pause.md).

- Instance framing retains split 0x020a and 0x0059 messages at their primary-writer lengths of 3 and 16 bytes.
  Every-split and coalesced tick/chat regressions pass; integrated native transfers preserve subsequent commands.
  Unknown message layouts and existing 0x0099 handling remain unchanged.
  Docs: [instance framing](../docs/protocol/02-instance-connection.md#fixed-message-boundaries-across-tcp-reads).

## Partly working

- Lockstep is served whenever the client picks it (no server setting; the log notes Predictive is preferred). It
  handles pickup/attack recovery, corpse collision and ground melee/chest approach;
  crowd and predictive gaps remain ([07](../docs/protocol/07-latency-visibility-movement.md) §6).
- League isolation is enforced by full league for towns and instance selection.
  SSF wilderness instances and Gateway ground-item state additionally belong to the stable character ID.
  Another character, including one reusing a deleted name, cannot inherit or join that state.
  Automated league/SSF instance checks pass.
  Shared peer presence preserves SSF town visibility while mutable world state remains private.
  Other players walk with the Move action the client animates.
  Multi-client world joining and pickup boundaries remain covered by automated checks.
  See [league modes](../docs/game/league-modes.md).
- **Predictive actor collision.** Player paths include living monsters and published players in the same instance.
  Monster paths use their `MonsterVarieties.ObjectSize` clearance and avoid other living bodies, including friendlies.
  A 50 ms pass steps the player's and the monsters' estimates in time order as the client does (no packets): a step
  blocked by terrain or a body ends the walk, and a path cut short paths again from its end. On a lockstep client the
  pass replays whole frames instead, each walker in object order (the player first) against the cells as moved so far,
  as the client's world ticks ([07](../docs/protocol/07-latency-visibility-movement.md) §5). Nothing is pushed out of a
  body. Death releases the occupied space and the corpse stays in the area.
  Melee monsters chase with their attack aimed at the player, about every 200 ms, and the swing starts on S→C 0x1ac.
  Ranged monsters and minions take rings around the player (`MonsterSurround`); the AI decides every 50 ms.
  Automated regressions cover player-body clearance for new and reused slots.
  Further automated checks cover short attack reach, stationary-blocked slots and queued channel/Rage refreshes.
  A 50 ms pass steps the estimates as the client does (no packets): a step blocked by terrain or a body ends the walk, a
  path cut short paths again from its end, and an attack does not stop the walk; death releases the occupied space and
  the corpse stays in the area.
  Chasers spread around the player on rings (`MonsterSurround`); the AI decides every 100 ms.
  Automated regressions cover direct chases for negative footprints and player-body clearance for new and reused slots.
  Further automated checks cover short attack reach, stationary-blocked slots, bounded crowd escape and queued
  channel/Rage refreshes.
  Combined regressions cover silent channel updates, removed gems and projectile contacts before crowd movement.
  AI and correction passes share indexed body snapshots; surround reservations use spatial queries and cached rings.
  Queue age, send/wire/socket timings and hot combat-lock timings are measured independently of tick latency.
  Stuns and freezes hold the walk and the newest click until they end; a speed change restarts the segment in progress;
  an attack follows its target to the swing; totems and the tutorial chest block paths.
  Melee hits land at the swing clip's contact keyframe.
  Resync (S→C 0x128): sent on every stun, after a movement skill, on the client's own request (C→S 0x16 command 8) and by `/oos`;
  it corrects errors of 20 cells or more and leaves smaller ones alone.
  Native-client checks cover a normal zombie, unique Hillock and an injected remote player object.
  Remote player creation, movement, life, departure and skill actions are replicated.
  Code: `LivingCollision.cs`, `ClientState.LivingCollision.cs`, `Program.LivingCollision.cs`, `InstancePlayers.cs`.
  Docs: [07](../docs/protocol/07-latency-visibility-movement.md).
- **Shared instance** (two clients, 2026-11-05): an instance's monsters are its own (`InstanceWorld`), on its clock and
  ids; every client is shown them, any player's hits hurt the same ones and each monster targets the nearest player.
  Ground items are shared, quest items and kill quest progress go to each player near, kill experience to all near.
  Ailment ticks and monster collisions run on one connection's loop; the King in the Mists' arena objects and the
  league encounters stay one player's.
  Code: `InstanceWorld.cs`, `SharedMonsters.cs`, `SharedKills.cs`, `SharedGround.cs`.
- **Spawn points.** Logins, Resurrect in Town and one-way entries other than The Twilight Strand → Lioneye's Watch use
  the area's spawn: the town's fixed point, or the walkable cell farthest from any edge in a generated area.
- **Area seeds.** A new instance of an area the Gateway generates itself gets a random seed (`Game_RANDOM_SEEDS=0`
  pins it to 1); towns, hideouts and the areas still read from captures use seed 1.
  An area the client cannot generate at seed 1 either (The Crossroads of acts 2 and 7, Act 6's Twilight Strand and
  Southern Forest, The Karui Fortress, some maps and league arenas) is played at a seed the client built in place of
  seed 1 (`ServerLevelAreas.SeedsInPlaceOfOne`, `AreaSeeds`); the Gateway generates it once a trace at that seed
  matches in every layer (re/011 §7), not yet the Labyrinth's Estate Crossing sections.
  A seed the port cannot generate (2 of 21 for The Coast, 5 of 21 for 1_2_12) is played at the first of 8 replacements
  derived from it that the port generates (`ServerLevels.PlayableSeed`, sent in 0x1d): before, such a map had no walk
  grid, area transitions or waypoint.
  An opened map is generated at the area level 0x1d sends (its tier: 83 for tier 16), the level the client generates
  at, not its WorldAreas level (2026-11-01). Before, a tier-16 Chateau (WorldAreas 71) took another topology than the
  client's, so the player spawned outside the client's layout (a black screen), and seeds the client cannot build
  were played.
- **Level generator port.** `GameServer.Data/Terrain/Generation` reproduces the client's level generator: the tile
  grid, the walkability and projectile grids, the doodads, every cell's ground height (`CellHeights.cs`) and the area
  transitions and waypoints (`AreaObjects.cs`: the tiles' objects, and a room's `Waypoint` line where no tile places
  one, as The Ledge's `waypoint.arm`).
  1669 of the 1671 seed-1 traces match the client in tile hash, both grids, every random draw the port makes and every doodad,
  and 2884 of the 2886 loads the 3.29.3 client logged match by tile hash (`scripts/re/levelgen/gate.ps1`); of the 118
  loads the client itself failed to generate, the port fails 112 the same way (115 expected once the gate reruns:
  the Sanctum Cellar, Nave and Vaults now fail as the client does). The 34 areas the client cannot
  generate at seed 1 (and that need no server data) were toured at random seeds the port generates: all 73 loads match.
  An unattended tour of every WorldAreas row with a topology (2026-10-27, `scripts/re/levelgen/tourloop.py`, which
  restarts and logs in the client by itself) left 234 of the 1932 rows with no client-side level to match: 17 without a
  topology, 202 that need data from the server (Incursion temples, Delve cells and biomes, Heist contracts, Synthesis
  memories, the Sanctum floors, the Deepwater biome grid; they come with each league's server side, see the
  [roadmap](../roadmap/world.md)), and 15 the client cannot generate at any seed tried (the
  list is written to data/areas-unavailable.txt). The port matches a load of every other row.
  A tour of 3 random seeds per matching area: all 519 match. Every traced load at another seed of that tour (1215,
  `lg.ps1 loads`) matches in draws, grids, doodads, heights and transitions too, the last one (a shrine's ground,
  2_6_2) since 2026-10-29. A 5-seed tour of every supported area, stopped after 475 stops (101 areas): 469 match in
  every layer and in the rooms' monster points; 4 do not (1_2_15 at 3 seeds, 1_2_14_2 at 1), and the port cannot
  generate 202 of the 7700 seeds (re/011 §8).
  Every load with a height capture matches cell for cell, and every one of the 186 with a transitions capture
  matches exactly (186 expected once the gate reruns: one capture was cut short by the recorder, now repaired).
  Act 5's crater effects in The Slave Pens (`GlowyFX`, `SlavePensIntroGround`: `server_only` types the client draws
  only when the server creates them) are sent at their end state (Transitionable 3); the client draws them
  (2026-10-29, seed 1).
  The Gateway generates the supported areas itself (`ServerLevels.cs`): those whose seed-1 trace passes every gate
  check (`gate-baseline.json`); their walk grid, projectile grid, cell heights, transitions and waypoint, and the
  doodads, rooms and quest tile objects the monster points and a1 quests are fitted to, need no capture.
  Entering any other area tells the player it is generated on the client and not supported by the server yet.
  The client's stages after the doodads (mesh variation, ground clutter, critters) are client-only and not ported.
  Code: `GameServer.Data/Terrain/Generation`, `scripts/re/levelgen`.
  Docs: [re/011](../docs/re/011-level-generation.md).
- **The Labyrinth's day layout.** The Gateway builds the day's layout as the client does (sections, rooms,
  connections and secrets: 168 client layouts match) and gives each room one of its LabyrinthAreas areas; entering
  one of those areas sends the room's node in 0x1d's extra data, and the client and the server's generation then
  apply the room's overrides and secret rooms alike (live: `1_Labyrinth_OH_quad_#` with its Gauntlet room, 2026-11-02).
  The day's seed (the UTC day number, `Game_LAB_SEED`) and the area pick are ours. `/lab_layout [tier]` lists the day.
  The secret rooms' objects (hidden doors, the Gauntlet's end) are not created yet.
  Code: `LabyrinthLayout.cs`, `LabyrinthPass.cs`, `LabyrinthDay.cs`, `ServerLevels.cs`.
  Docs: [03](../docs/protocol/03-area-and-world.md), [re/011](../docs/re/011-level-generation.md) ("Labyrinth pass").
- **A Labyrinth run (Normal).** The Sarn Encampment's (Acts 3, 8) plaque shows the trials until the six Normal ones are
  done, then opens the statue for good and the way to the plaza behind it (`/trials off` shuts it). At the plaza the
  device opens the select window (0x7b kind 0x10); choosing Normal (C→S 0x212 = 1) opens the door with its animation
  and shows the way into the day's first room; the portal goes back to town. Each room is its own instance (the run
  picks its seed, so the server knows its node), its "entranceN" exits lead to its neighbours, Izaro's arena's way on
  is open (no fight yet), the glyph statues open the Labyrinth map (0x7b kind 0x0f) with the run's rooms (S→C 0x1cb);
  being anywhere else ends the run, and the plaza's door shuts again. Checked live 2026-11-03.
  Not yet: Izaro, the Ascendancy Chamber, Cruel and harder tiers, the secret rooms' objects; a room's exits keep the
  plain AreaTransition type (not type 5).
  Code: `LabyrinthDay.cs`, `AreaTransitionCatalog.cs`, QuestObjects `Rules.LabyrinthTrials.cs`, `Act3.cs`, `Act8.cs`.
- **Trials of Ascendancy.** In each LabyrinthTrials area the Gateway places the trial rooms' door, levers, Locked
  Doors, plaque and portal back, and the Bronze Monograph at the door, which reads at once when clicked.
  The door opens, a lever opens what its room's links name (the `.arm` files' link section), the plaque sets the trial's quest flag and its league quest
  flag, saved for the account's league so the trial is done for each of its characters there (the client counts it
  done), opens the portal back to the door and opens the Trial Plaque window (0x7b kind 9) with the trials done; a
  done trial's portal too opens only when its plaque is used.
  Entering a trial's area gives the Lord's Labyrinth quest (`LabyrinthQuestReceived`): the client's tracker then says
  "Solve the Trial of Ascendancy in this Area" until it is done, and "Enter the Labyrinth" once the six Normal trials
  are ([labyrinth-trials](../docs/game/labyrinth-trials.md), "The Lord's Labyrinth quest"); checked live (2026-11-01).
  What an instance opened stays open; an opened trial Locked Door (bars) is removed, a room's gate stays open.
  Verified with the real client in The Lower Prison (2026-10-31).
  Its spike traps cascade round their ring from the lever towards the door (the plaque room's as a checkerboard), rise
  over a stretched Emerge animation (0x1d1), hit a player on their tile for LbRetractableTrap's share of life and energy
  shield, bleed for a second and wall their tile while they show; matched by eye against the original game.
  The Imperial Gardens' 14 dart turrets fire together every 1.5 s, four poison darts each a quarter turn apart, through
  the projectile world, stopping at walls and at the turrets' Hull; a dart hits for LbArrowTrap's share and adds a 2 s
  poison stack reckoned from the hit's life share, less armour, and slows movement by 60% for 2 s. Being checked live.
  The Catacombs' eight roombas run their rails (two rings round, two chains to and fro, set out as they face) and cut
  a player inside them, who degenerates by a share of life and energy shield a second (fitted live to the original,
  2026-11-01) with the roomba's own effect on them; the trial doors open for walking (checked in The Catacombs).
  The Chamber of Sins' nine sawblades run their tracks (shown, Rail_Visible) to and fro, each pair together, and cut
  as the roombas do; its two levers (`Switch`) go both ways: pulled, a lever shuts the cage round its room
  (`Door_Open`) and stops the sawblades its links pause, pulled back it opens it and they run on. Checked live
  against the original (2026-11-01).
  The Crypt's twelve spinners (spinning morning stars) run their track pieces piece by piece, turning back at ends
  and at stoppers, round corners, and at a junction as its rail switch stands, and cut as the roombas do; its
  `Switch_Multi` lifts the two stoppers in the way up so those spinners run to their lanes' ends, its `Switch` sets
  the upper room's TriCorner straight so its two spinners leave their small rings for the square, both going both
  ways; the plain door between its upper spinner room and the plaque room stays shut until clicked, keeping the
  plaque room's monsters in. Checked live against the original (2026-11-01).
  The Crematorium's hot plates, those under the grates by its walls too, lie at their lines' fixed height under the
  floor's grates and heat together on a timer (5 s at the peak, 6 s cold) and
  burn a player while their lava lies (a share of life and energy shield a second, less fire resistance, with the lava
  buff's burning look); each of its three levers opens its Locked Door and sets its gears turning; its Immolators
  bring Ember Claimants. Timings, damage and the Immolators' skeletons checked live against the original (2026-11-01);
  the plates' height under the grates is being checked.
  The area's packs keep away from a trial's traps; its rooms get what their room file places: the Gardens' four
  Devourers, the Lower Prison plaque platform's pack and its spawner's rare Brittle Poacher, the Crypt spawner's rare
  Frost Priest, the Crematorium spawner's rare Cinder Elemental and its four Immolators, and the packs the original has where the room file
  has none (two before the Chamber of Sins' upper cage, one past the Crypt trial's door and one by its plaque, one in
  the Crematorium trial's first room and one past its second Locked Door).
  Not built: the traps of Izaro's rooms and the hot plates' Cruel and Merciless rooms.
  The Labyrinth's own rooms (`1_Labyrinth_*`) get the same traps, levers and doors from the Gateway's generated rooms,
  wired by their rooms' links (spike chains and triggers, rails, levers, plates and their darts): spikes on their
  `Trigger_*` timers and pressure spikes a step fires, abyss edge blades, dart turrets and wall darts, roombas,
  sawblades, spinners with the two-lever rooms' swap piece, the ElegantHouse rooms' hot plates (`*_EH`), and their
  puzzles: `Door_Counter` gates, `Counter_Handler(_Any)`, cranks that come back up, `Switch_Multi` levers that open
  Locked Doors and flip shared cages. Their tiles add the ElegantHouse doorway rollers (a dropping roller that cuts while
  down, rising spikes striking with their clips), abyss edge blades, smashable walls a hit breaks, the DryGarden secret
  wall a pair of hidden switches opens, and statues whose monster steps off its pedestal when the player comes near.
  Their areas get packs of the Labyrinth's own monsters by room set (ours: no data names them). Checked live on seed 1
  of `1_Labyrinth_RT_straight`, `_C_straight`, `_OH_end`, `_P_straight`, `_EH_end`, `_EH_end_bottleneck`, `_DG_end`,
  `_DG_quad_bottleneck`, `_P_end_bottleneck` and `_P_straight_bottleneck` (2026-11-01, 2026-11-02). The monsters'
  skills beyond attacks (Discharge, tar, death novas) and the room exits (a run) are not built.
  Code: `LabyrinthTrials.cs`, QuestObjects `Rules.LabyrinthTrials.cs`.
  Docs: [labyrinth-trials](../docs/game/labyrinth-trials.md).

## Not working yet

- Area transitions of a generated area the Gateway does not generate itself need a `-Walkmaps` capture for its seed.
  Exits the game opens later are not modelled: "teleportNdisabled" gates (an arena's way in, a shortcut back) are
  created open.
  An "entranceN" with no object of its own (the portal to Highgate after Malachai, Weylam's boat) is an exit without a
  model at its point, there from the start.
  The move the game makes when a fight ends is not modelled: The Rotting Core's way to Weylam's boat (telepad16)
  after the Depraved Trinity ([08](../docs/protocol/08-area-transitions.md) §8).
  Quest objects with extra components are sent as plain AreaTransition, so The Twilight Strand's gate appears without
  its animation; the Doorman and the opening tutorial's wall are not created ([12](../docs/protocol/12-quests.md) §6).
  Only quests a1q1 and a1q4 are scripted; the client builds the quest tracker from the flags.
- Waypoint travel to a generated area without a `-Walkmaps` capture for its seed puts the player at an arbitrary cell
  (The Submerged Passage, seed 1); quest flags are derived from visited areas and waypoints, not from quests
  ([16](../docs/protocol/16-waypoints.md) "Open").
