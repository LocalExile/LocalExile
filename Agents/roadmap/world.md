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

### Server-side level generation

- **Known:** the port reproduces the client's generator for every recorded trace and logged load
  ([re/011](../docs/re/011-level-generation.md), [status/world.md](../status/world.md)).
  `/tour <file>` with `live.ps1 -Walkmaps` records new (area, seed) loads; `levelgen tourlist` writes the lists.
- **Start:** the supported areas get random seeds, and a 5-seed tour of them found 4 loads the port gets wrong
  (1_2_15 at `0x14211bc2d`, 1_2_14_2 at `0x1420386d8`, [re/011](../docs/re/011-level-generation.md) §8 Random seeds)
  and 202 seeds it cannot generate (the Gateway plays a new instance at such a seed at a replacement seed it can,
  `ServerLevels.PlayableSeed`; fixing the generator errors gives those seeds back).
- **To be implemented with their leagues (out of scope for now):** the 202 areas whose level the client builds from
  data the server sends (`data/areas-unavailable.txt`). They come with each league's server side, not with the
  generator:
  - Delve (82): the mine cells and biomes (`Delve_*`); the client needs the server's cell data.
    An initial Delve_Main block, hub entrance and mine return gate now work with native generation and captured movement.
    The initial Crawler has attached wheels, engine idle and a torch; the hub's native equipment console is placed.
    Equipment and completed chart points/edges persist per account/league; each trip saves one destination and parks
    for manual selection. Unfinished trips reset on interruption/death.
    Escort pacing, depth levels/packs/rewards, exact gradual
    sulphite debit, native walking/combat and the full chart inside the mine are implemented.
    Fresh server-generated Mines checkpoint windows replace the entrance layout after the first trip, with durable
    scene/route history and five-second final waves gating checkpoint saves.
    Remaining: native topology validation and physical biomes beyond the procedural Mines rooms,
    original biome populations, node-specific rewards, darkness stacks/resistance and consumable use.
    Restore laid trails on reentry, finish shared sulphite migration and equipment cable visual tiers.
    The ordinary Mines cell pass is ported; the initial entrance still uses its capture.
  - Heist (80): the contract and blueprint areas; the layout comes from the heist plan.
  - Synthesis (24): the memories (`Synthesis_Main*`, `Synthesis_Single*`).
  - Incursion (10): the temples (`Incursion_Temple*`); the client needs the room hash of the opened and upgraded
    rooms ("Unknown Incursion Temple room hash").
  - Sanctum (4): the floors (`SanctumCellar`, `SanctumCrypt`, `SanctumNave`, `SanctumVaults`); the client rebuilds
    the floor graph from the rooms the server sends.
  - Deepwater (2): the 3.29 league's DeepwaterEncounter and DeepwaterEncounterVoyage, built from a biome grid the
    server sends; the only seed-1 traces the port does not match.
- The daily Labyrinth layout is step 1 of [The Labyrinth](#the-labyrinth).
- **Done when:** any seed gets its layout without a capture.

## 3. Gameplay

### The game's start and resurrection

- **Known:** items, equipment, the skill bar, experience and passives are saved per character in `data/characters.db`
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

### The Wildwood (Affliction) league

- **Known:** the 3.29.3c client keeps the Wildwood league's data and UI: the Wisp mechanic (the Primalist's
  `azmeri_primalist_wisps_found_+%` is one of its consumers), the three Wildwood Descendancy rows
  ([status](../status/progression.md), "Descendancy") and the `AnimalCharm` item class. The server implements none of
  the league: no Wisps spawn, and there is no Wildwood area, NPC or reward flow.
- **Done when:** the Wildwood's Wisp collection and its areas exist server-side, and the Primal Wisps found node
  feeds `azmeri_primalist_wisps_found_+%` into that system.

### The Labyrinth

- **Known:** the server port generates the Labyrinth's rooms (249 of 257 match), and the gate baseline lists the
  supported ones ([re/011](../docs/re/011-level-generation.md), "Labyrinth pass").
  The day's layout is sent with each room in it, and a Normal run works ([status](../status/world.md), "A Labyrinth run").
  The Aspirants' Plaza (`Labyrinth_Airlock`, `WaypointCatalog.LabyrinthPlaza`) is a waypoint the World panel's
  Labyrinth pin always shows.
  The Ascendancy Chamber's font chooses the ascendancy and gives the tier's two points, and Izaro's three trials are
  fought ([status](../status/world.md), "A Labyrinth run";
  [labyrinth-trials](../docs/game/labyrinth-trials.md), "Izaro's arenas").
  The tables are extracted (the `Labyrinth*` client tables): 4 tiers with their required trials, quest flag and area
  level, 18 trials, 47 sections, 223 section layout rows, 58 secrets and the Izaro chests.
  AreaTransition type 5 is Labyrinth ([08](../docs/protocol/08-area-transitions.md) §7), and
  `AreaTransitionCatalog` does not know the Labyrinth areas yet.
- **Known (trials):** the Gateway places each trial's door, `Switch_Once` levers, Locked Doors, plaque, portal,
  spike traps, the Imperial Gardens' dart traps, the Catacombs' roombas, the Chamber of Sins' sawblades with
  their toggling `Switch` levers and `Door_Open` cages, and the Crypt's spinners with its `Switch_Multi` and rail
  switches;
  The Lower Prison's trial completes with the real client ([status](../status/world.md), "Trials of Ascendancy").
  A trial is done for the client by its quest flag or its league quest flag; the plaque sets both, and the league
  flag is saved for the account's league, so every character of it has the trial
  ([labyrinth-trials](../docs/game/labyrinth-trials.md)).
- **Start**, one pull request per step, in this order:
  1. Izaro's own fight: his skills are in (`IzaroSkills.cs`; `LbBossShield`, in his row, was not seen in the original's
     fight, so it is left out). Left: his taunt, the Spikes' slow, the
     cooldowns against the original (ours; no `.ais`), and set-backs of his model after an order
     (`scripts/re/sync_report.py`, Game_TRACE=sync with `trace-sync.js`, measures them; the Shield Bash aimed at a cell is
     not live-checked).
     Normal's five arena mechanics are done (Toggle Statues, Gargoyles, Portals, Chrono Statues, Lieutenants:
     `Rules.IzaroArena.cs` places them and what they leave); the Goddess of Justice is left. Ours, not the original's:
     the lieutenants' Storm Call is a skill of its own (the data casts it on an attack's hit), `bull_thorns` returns no
     damage (no values in the data), the circles' and their skills' radii.
     Her Guardians' circles are done without her (`IzaroGoddess.cs`): when she exists they become her skill, with its
     mortars flying from her. As the original plays (live, the user): the gargoyles' beam ends on her, above Izaro, not
     on him (ours ends on him: move it to her too).
  2. Izaro's chests in the last arena (LabyrinthIzaroChests at the IzaroChestMarkers).
  3. The Cruel and Merciless Labyrinths (their select-window choice) and trials, then the secret rooms' objects (hidden doors, the Gauntlet, and the Payload room's cart `LabyrinthTrapKeyDelivery`, `pushed_by_player`, whose rail leads to the door: live, the user, it goes on while the player stands in its radius, goes back a little slower when they leave, and monsters seem to spawn round it) and darkshrines, the Divine Font (LabyrinthCraftOptions),
     enchantments and the Eternal Labyrinth with its map trials (a dev command opens them while maps are missing).
     Those two have Sentinel traps (wiki, 2.3.0): monsters (`Metadata/Monsters/Totems/LabyrinthPopUpTotem*`, type
     `LabyrinthMonsterTotem`: Endless Pain, Hazard, Drought, Sting, Unending Lethargy, Frost, Fire, Storm, Vaal Totem,
     some with Proximity Shield) that rise from the floor and debuff by an aura, sink when damaged and rise again
     (`ReviveAfterDuration`). The map trials' end rooms place them as points (`TrialsEndgame/*/end*.arm`), and
     MonsterGroupEntries' group 35 lists them; no Normal, Cruel or Merciless room has one. The Merciless and Eternal
     Labyrinths also have Argus (wiki), a boss in one room of the day's layout who drops a Treasure Key.
- **Done when:** a character completes the six Normal trials, enters the day's Normal Labyrinth from the plaza,
  walks its rooms, defeats Izaro three times and chooses an ascendancy at the Altar, keeping it and its two points
  after a relog.

## 4. Server design

- **Predictive desync:** Shield Charge and the map device's walk are not traced, and two monsters racing for one cell can
  swap places between client and server ([07](../docs/protocol/07-latency-visibility-movement.md) §5, "Open").
- **Lockstep network mode:** the server decides every action and the client plays it back by server frames, each
  action start putting the client on the server's cell, which ends the parted copies of a crowd.
  It works locally when selected by the client; Predictive remains preferred, and network parity is not verified
  ([status](../status/world.md)).
  - **Known:** M5's dormant-body membership and saved-cell restoration are accepted with open issues.
    The player's walk, attacks, spells, pick-ups and object uses, monsters' orders, hit recoveries, emerges and
    corpses, and other players' copies follow the client's frame rules, and world messages are explicit records in
    the client's key order ([07](../docs/protocol/07-latency-visibility-movement.md) §5, §6).
    On the 3.29.3b client held walks and pack fights (wand, bow, sword) stayed on the client's cells within GGG's
    margin, and single probes followed natural packs, a dense area, pick-ups, spells after a Move, a reconnect and
    two players.
    Left: the same measurements on 3.29.3c; steps that fall exactly on a frame's end (the client decides them with
    its float accumulator); two places where the generated walk grid is not the client's; the King in the Mists,
    which moves during its skills on the client; monsters' and minions' hits, still written at the frame's start;
    a held skill's repeat, one frame later than the client would take it; attack in place for a predictive client.
  - **Start:** measure on 3.29.3c. Then move the encounters and skills added since (Izaro's arena, Doedre's
    sewers, the Depraved Trinity, the Vaal Oversoul; Bladefall of Trarthus, Winter Orb, Storm Call, Discharge, Dark
    Bargain, Flicker Strike's effects) and the gm-commands mod to world records (`Game.SendWorldCreate`,
    `SendWorldRecord`, `SendWorldRemoval`): their own objects' messages still go out outside the frame's records.
    Then special action producers, sequence wrap, ranged chase behavior and the remaining skills and world
    interactions.
  - **Done when:** on a fixed bench every sync figure is within GGG's margin. The bench is pack fights per weapon,
    natural packs and a dense area. The figures are monsters standing per order kind, monsters in a pack, monsters
    walking per speed, the player standing and walking, and snap episodes.
    The server sends nothing the client ignores and frames its orders as GGG does.
    Skills, objects, area changes, bosses, two players and a real network link work as on GGG, or as the client plays
    them where GGG was never recorded.
    Turning it on by default stays the maintainer's decision.
- **HNC packets from the client** (type 0xDD): what the server should do with them
  ([07](../docs/protocol/07-latency-visibility-movement.md)).
- **Server-side world simulation** (monsters, combat): no project for it yet; add one when it is needed.
- **Several players** in one instance: player presence, poses, life, departure and body blockers are published.
  Verify two authenticated connections together, then add party admission to private instances and shared combat.
  Trade between players and party invite / accept / leave work. Next: friend requests (C→S 0x80) and the friend list,
  and party Decline, kicks and disconnects ([28](../docs/protocol/28-player-trade.md) "Open").
  The native remote-player body and normal/unique monster clearance have single-client checks.
  See [status/world.md](../status/world.md) and [07](../docs/protocol/07-latency-visibility-movement.md).
