# Roadmap: World, movement and the network

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/world.md](../status/world.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 1. Quality of life

### A server tick loop

- Continuous player recovery has its own 16 ms elapsed-game-time loop (regeneration, flasks and self RF);
  native ES regeneration, source removal and transfer/relog controls pass.
  Broader recovery and self RF visual verification remains separate.
  See Status and protocol/20-damage.md.
- Same-character concurrent/relog connections still need an explicit stat-publication owner and ordering contract.
  The closed/retired connection guard and its limits are in
  [character stat lifecycle](../docs/dev/character-stat-lifecycle.md).
- **Known:** `Program.TickRate` (30/s) only bounds attack times; hits use timers, the AI a 100 ms loop.
  Queue/lock/send timings and shared per-pass blocker indexes are implemented; there is no unified instance loop yet.
- **Done when:** one 30 Hz loop per instance drives movement estimates, AI and combat.

### Remaining client-message layouts

- **Known:** fixed 0x020a and 0x0059 messages retain every partial prefix using their primary client writers.
  Existing 0x0099 and chat-channel layouts remain unchanged.
- **Start:** establish each remaining unknown layout from its client writer before adding a fixed length.
  Do not infer a complete message size from a TCP read or attribute old disconnects without a trace.
- **Done when:** every supported client request remains framed across split and coalesced reads.

## 2. The world

### Every area's layout at seed 1

- **Known:** generated areas only exist in the client; `live.ps1 -Walkmaps` captures an area's walk map, area
  transitions and waypoint when the client generates it ([08](../docs/protocol/08-area-transitions.md) §7).
  About 30 areas are captured; waypoint travel to an uncaptured one (The Submerged Passage) puts the player outside
  the map ([16](../docs/protocol/16-waypoints.md) "Open").
- **Start:** a dev command that walks the WorldAreas list with `/goto` and waits for each capture.
- **Done when:** every act area has its seed-1 capture, and every waypoint area its waypoint in it.

### Server-side level generation

- **Known:** the port reproduces the client's generator for every recorded trace and logged load
  ([re/011](../docs/re/011-level-generation.md), [status/world.md](../status/world.md)).
  `/tour <file>` with `live.ps1 -Walkmaps` records new (area, seed) loads; `levelgen tourlist` writes the lists.
- **Start:** the supported areas get random seeds, and a 5-seed tour of them found 4 loads the port gets wrong
  (1_2_15 at `0x14211532d`, 1_2_14_2 at `0x142031e88`, [re/011](../docs/re/011-level-generation.md) §8 Random seeds)
  and 202 seeds it cannot generate (the Gateway plays a new instance at such a seed at a replacement seed it can,
  `ServerLevels.PlayableSeed`; fixing the generator errors gives those seeds back).
- **To be implemented with their leagues (out of scope for now):** the 202 areas whose level the client builds from
  data the server sends (`data/areas-unavailable.txt`). They come with each league's server side, not with the
  generator:
  - Delve (82): the mine cells and biomes (`Delve_*`); the client needs the server's cell data.
    An initial Delve_Main block, hub entrance and mine return gate now work with native generation and captured movement.
    The initial Crawler has attached wheels, engine idle and a torch; the hub's native equipment console is placed.
    Equipment purchases/upgrades have account/league persistence. The first quest trip has a bounded authored-rail
    implementation with gradual exact-total debit, escort pacing and tutorial pack reveals. Native checks confirmed movement/light/combat and exact debit using assisted escort.
    Remaining: validate/price general routes against a persistent mine graph, original biome population and encounter
    wave scripts, darkness stacks/resistance and flare/dynamite use. Initial victory rewards and highest completed
    depth now persist; general node-specific rewards and the mine graph still need implementation. Finish chart continuity inside the mine
    restore laid lamp/cable trails on re-entry, and verify a continuous walking escort without debug teleports.
    Also finish shared sulphite migration, equipment cable visual tiers and other biome sections.
    This does not add Delve to the server level generator's supported areas.
  - Heist (80): the contract and blueprint areas; the layout comes from the heist plan.
  - Synthesis (24): the memories (`Synthesis_Main*`, `Synthesis_Single*`).
  - Incursion (10): the temples (`Incursion_Temple*`); the client needs the room hash of the opened and upgraded
    rooms ("Unknown Incursion Temple room hash").
  - Sanctum (4): the floors (`SanctumCellar`, `SanctumCrypt`, `SanctumNave`, `SanctumVaults`); the client rebuilds
    the floor graph from the rooms the server sends.
  - Deepwater (2): the 3.29 league's DeepwaterEncounter and DeepwaterEncounterVoyage, built from a biome grid the
    server sends; the only seed-1 traces the port does not match.
- The daily Labyrinth layout is the first step of [The Labyrinth](#the-labyrinth).
- **Done when:** any seed gets its layout without a capture.

## 3. Gameplay

### The game's start and resurrection

- **Known:** items, equipment, the skill bar, experience and passives are saved per character in `data/saves.db`
  ([saves](../docs/saves.md)) ([13](../docs/protocol/13-items.md) §7, [11](../docs/protocol/11-skills.md) §11,
  [15](../docs/protocol/15-progression.md)), and every login starts at the last town's spawn point
  ([09](../docs/protocol/09-area-change.md) §4).
  This follows the real game on purpose: the area and position a character was in are not saved, because the real
  client never logs anyone into them.
  New characters start on The Twilight Strand and log in there until they enter a town;
  the ground weapon, dying-exile/zombie skill-gem encounter and support chest are implemented and live-checked.
  Tutorial popup acknowledgements and force walls 1/2 remain open, and placements still need local captures.
  Hillock's death (quest a1q1) opens the gate into Lioneye's Watch ([12](../docs/protocol/12-quests.md) §6).
  The death screen's "Resurrect in Town" goes to the last town, with a loading screen, and is not offered before the
  character has one; "Resurrect at Checkpoint" goes next to the area's activated waypoint, else to the area's arrival
  point ([09](../docs/protocol/09-area-change.md) §7).
- **Done when** (low priority: the start and the gate work, these are visual): the Strand's gate plays its opening
  when Hillock dies, the force wall blocks the way while he lives and the Doorman opens the door
  ([12](../docs/protocol/12-quests.md) §6, "The gate's animation");
  "Resurrect at Checkpoint" goes to the last activated checkpoint of the area, with no loading screen.
  Waypoints are checkpoints, and the activated waypoint is done;
  the separate `Metadata/MiscellaneousObjects/Checkpoint` objects are not placed yet, so "last activated" across
  several checkpoints is open.
  Dying in a boss arena revives just outside the arena's door, even with a checkpoint activated;
  dying in a map revives in the hideout (hideouts exist, [21](../docs/protocol/21-hideouts.md); maps do not).
- **One-way entries:** the arrival next to the exit back needs an exit back.
  Lioneye's Watch from The Twilight Strand uses the town's named point `entrance1` (`AreaLayout.OneWayArrivals`,
  [08](../docs/protocol/08-area-transitions.md) §3): in the real game the player comes out on the town side of the
  gate.
  Other one-way links fall back to the area's spawn until their named entrance points are added.

### The Labyrinth

- **Known:** the server port generates the Labyrinth's rooms (249 of 257 match), and the gate baseline lists the
  supported ones ([re/011](../docs/re/011-level-generation.md), "Labyrinth pass").
  The boss ends use the layout the client builds with seed 0: the server sends no daily layout.
  The Aspirants' Plaza (`Labyrinth_Airlock`, `WaypointCatalog.LabyrinthPlaza`) is a waypoint the World panel's
  Labyrinth pin always shows.
  Ascendancy points come only from the `/ascendancy_points` dev command (`AscendancyProgress`: two per tier, once);
  choosing the ascendancy through the native Altar is #151.
  The tables are extracted (`data/tables/Labyrinth*.json`): 4 tiers with their required trials, quest flag and area
  level, 18 trials, 47 sections, 223 section layout rows, 58 secrets and the Izaro chests.
  AreaTransition type 5 is Labyrinth ([08](../docs/protocol/08-area-transitions.md) §7), and
  `AreaTransitionCatalog` does not know the Labyrinth areas yet.
- **Known (trials):** the server generates all 18 trial areas (LabyrinthTrials; the Normal six are The Lower
  Prison, The Crypt Level 1, The Chamber of Sins Level 2, The Crematorium, The Catacombs and The Imperial Gardens).
  The trial is two rooms the generator already places: in The Lower Prison the `labyrinth_intro` node takes
  `axiom_intro.arm` and `labyrinth_end` takes `axiom_intro_end.arm`.
  Their object lines hold the trial: `LabyrinthIntroDoor`, `Puzzle_Parts/Door_Closed`, `Puzzle_Parts/Switch_Once`,
  a `LabyrinthTrialGlyph`, `LabyrinthCascadeSpikeTrap`s, `LabyrinthTrialPlaque` and `LabyrinthTrialReturnPortal`.
  The Gateway places none of them yet: `AreaObjects` keeps only transitions, waypoints and `QuestTypes`.
- **Start**, one pull request per step, in this order:
  1. The Normal Trials of Ascendancy: place the trial rooms' object lines (as `AreaObjects.RoomObjects` places room
     waypoints), open the intro door, the switch and its door, and complete the trial at the plaque, whose portal
     returns to the door.
     Find the client's interaction with these objects and how it learns a completed trial (LabyrinthTrials columns
     1–3 are unread) before choosing what the save keeps; the real game shares trials per account and league.
  2. The trial traps: each trial teaches one kind (spikes, saws, furnaces, darts, ...), the same objects as the
     Labyrinth's; their timing and damage.
  3. The day's layout: port the rest of `0x1424e30f0` (connections, secrets) and send the layout in the area's extra
     data (`0x141ec7220`: seed, node, difficulty; [03](../docs/protocol/03-area-and-world.md)).
     The node's LabyrinthNodeOverrides and secrets then apply in `LabyrinthPass`; the levelgen gate stays green.
  4. A run: the plaza's device opens a tier once its required trials are done (Labyrinths `RequiredTrials`), and each
     room is an instance linked by the layout's connections.
     No waypoints inside; a portal out, a death or a logout ends the run.
     Find what the client sends at the device and at a type 5 transition before choosing a server state.
  5. Izaro: three Aspirant's Trials, each ending when he loses a third of his life (the boss stages of
     `BossFights.cs`); the third opens the Ascendancy Chamber and its Izaro chests (LabyrinthIzaroChests).
  6. The Ascendancy Chamber: the Altar (`LabyrinthAscendancyDevice`) chooses the ascendancy on the first Normal run,
     awards the tier's two points through `AscendancyProgress` and sets the tier's quest flag (Labyrinths
     `QuestFlag`); agree the shared code with #151.
  7. The Cruel and Merciless trials, then secrets and darkshrines, the Divine Font (LabyrinthCraftOptions),
     enchantments and the Eternal Labyrinth with its map trials (a dev command opens them while maps are missing).
- **Done when:** a character completes the six Normal trials, enters the day's Normal Labyrinth from the plaza,
  walks its rooms, defeats Izaro three times and chooses an ascendancy at the Altar, keeping it and its two points
  after a relog.

## 4. Server design

- **Lockstep network mode:** the server would decide every position and the client play it back by server frames,
  which makes client and server identical by construction.
  Found so far: the frame protocol (S→C 0x2b6 / 0x2b5, already sent), the client's lockstep messages (0x018e, 0x018f,
  0x0192, 0x0195, 0x0196) and the target update 0x1ad.
  Still needed: the client's action sequence (0x1ad is ignored without it), every action through the lockstep messages
  (NPCs, stashes, transitions, items, skills), and stepping every object by the server's frames
  ([07](../docs/protocol/07-latency-visibility-movement.md) §6).
- **HNC packets from the client** (type 0xDD): what the server should do with them
  ([07](../docs/protocol/07-latency-visibility-movement.md)).
- **Server-side world simulation** (monsters, combat): no project for it yet; add one when it is needed.
- **Several players** in one instance: player presence, poses, life, departure and body blockers are published.
  Verify two authenticated connections together, then add party admission to private instances and shared combat.
  Trade between players and party invite / accept / leave work. Next: friend requests (C→S 0x80) and the friend list,
  and party Decline, kicks and disconnects ([28](../docs/protocol/28-player-trade.md) "Open").
  The native remote-player body and normal/unique monster clearance have single-client checks.
  See [status/world.md](../status/world.md) and [07](../docs/protocol/07-latency-visibility-movement.md).
