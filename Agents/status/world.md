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
  ticks; ticks or handlers over 50 ms are logged), visibility (wake zone), click-to-move.
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
  Code: Gateway (`RunServerFrames`).
  Docs: [07](../docs/protocol/07-latency-visibility-movement.md) §6.
- Area transitions in every area, with their destinations, models (the arch highlighted on hover), rotation and
  height: fixed layouts from the atlas, generated layouts from the walk-map recorder's capture;
  verified town ↔ The Coast ↔ The Tidal Island and The Coast ↔ The Mud Flats; area change through them;
  Ctrl+click opens the Instance Manager (list, join, create); `/goto <area>`.
  Code: Gateway (`AreaTransitionCatalog.cs`), `scripts/walkmaps`, `scripts/re/atlas`, Auth's instance registry.
  Docs: [08](../docs/protocol/08-area-transitions.md), [09](../docs/protocol/09-area-change.md).
- Arrival points: entering through an exit (or `/goto`) puts the player next to the exit back;
  The Twilight Strand → Lioneye's Watch arrives at the town's `entrance1`;
  Resurrect at Checkpoint revives there, or next to the area's waypoint once the character has activated it.
  `/tp` (dev) teleports the player to a cell or next to a named monster, NPC, transition, waypoint or captured object.
  Code: `AreaLayout.Arrival`, `TeleportTargets.cs`, Gateway.
  Docs: [09](../docs/protocol/09-area-change.md) §4.
- Instances: one per town (all 12 act towns from WorldAreas `IsTown`);
  other areas get private instances that live 6 minutes once empty.
  Code: `InstanceRegistry.cs`, `WorldAreaTowns.cs`.
  Docs: [09](../docs/protocol/09-area-change.md).
- Walk maps and area transitions captured from the client on first visit;
  the Gateway uses them for position estimates, spawns and area transitions.
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

- **Predictive actor collision.** Player paths include living monsters and published players in the same instance.
  Monster paths use their `MonsterVarieties.ObjectSize` clearance and avoid other living bodies, including friendlies.
  A 50 ms correction loop sweeps monster movement and separates overlaps in the server's estimate only (no packets:
  the client paths around bodies itself); death releases the occupied space and the corpse stays in the area.
  Chasers spread around the player on rings (`MonsterSurround`); the AI decides every 100 ms.
  A player boxed in by estimated bodies follows the client through them instead of being pulled back.
  Native-client checks cover a normal zombie, unique Hillock and an injected remote player object.
  Two authenticated clients in the same instance still need an end-to-end check.
  Remote player creation, movement, life and departure are replicated; party formation and shared combat remain open.
  Code: `LivingCollision.cs`, `ClientState.LivingCollision.cs`, `Program.LivingCollision.cs`, `InstancePlayers.cs`.
  Docs: [07](../docs/protocol/07-latency-visibility-movement.md).
- **Spawn points.** Logins, Resurrect in Town and one-way entries other than The Twilight Strand → Lioneye's Watch use
  the area's spawn: the town's fixed point, or the walkable cell farthest from any edge in a generated area.
- **Area seeds.** Non-town areas use seed 1 unless `Game_RANDOM_SEEDS=1`; each new seed needs its own walk map.

## Not working yet

- Area transitions of a generated area need a `-Walkmaps` capture for its seed.
  Quest objects with extra components are sent as plain AreaTransition, so The Twilight Strand's gate appears without
  its animation; the Doorman and the opening tutorial's wall are not created ([12](../docs/protocol/12-quests.md) §6).
  Only quests a1q1 and a1q4 are scripted; the client builds the quest tracker from the flags.
- The client's lockstep network mode (also what "auto" picks on localhost): the player is drawn (the Gateway sends
  server frames) but cannot move or use objects.
  Only Predictive is supported; the Gateway asks for it once per connection
  ([07](../docs/protocol/07-latency-visibility-movement.md) §6).
- Waypoint travel to a generated area without a `-Walkmaps` capture for its seed puts the player at an arbitrary cell
  (The Submerged Passage, seed 1); quest flags are derived from visited areas and waypoints, not from quests
  ([16](../docs/protocol/16-waypoints.md) "Open").
