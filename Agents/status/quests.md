# Status: Quests

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/quests.md](../roadmap/quests.md).

## Works (verified with the real client)

- Quests: flags per character in the save, one script per quest (`Quests.cs`), `/quest_flag`. a1q1: new characters
  start on The Twilight Strand's beach and log in there until they reach a town;
  coming within 60 cells of Hillock sets `a1q1-SeenHillock` ("Kill Hillock");
  Hillock has the boss life bar at the top of the screen once he aggroes (StateMachine `boss_life_bar`, 0x2ba), unique
  life (311 at level 1) and a second stage: at half life he pulls out his sword (ZombieBossExtract, Transitionable → 2)
  and his life fills up again; the force wall (`Tutorial_Blocker_3`, the room's angle and scale 2.7) stands across the
  way to the gate until he dies, and the server closes the way on its own walk map too (`QuestBarriers`), so its
  position estimate stops where the client stops; after his death the quest log says to enter the gate (every
  character has `CompletedAllTutorials`); his death levels the character to 2, removes the wall and creates the gate
  into Lioneye's Watch (the Gateway checks the flag too); Portal Scrolls do nothing on the Strand (its area mod
  MapNoPortals); no Resurrect in Town before a town.
  Code: `Quests.cs`, `BossFights.cs`, `QuestBarriers.cs`, `AreaModCatalog.cs`, Gateway.
  Docs: [12](../docs/protocol/12-quests.md) §5–6.

## Partly working

- New characters pick up their class weapon, talk to the dying exile and kill the biting zombie for their skill gem.
  The support gem comes from its chest, which requires the player to walk into range.
  Placements use captured room data and gem claims are saved once; existing characters keep their items and progress.
  The complete Witch weapon, zombie/gem and chest/support-gem sequence was checked live.
  Tutorial popup acknowledgements and force walls 1/2 remain unimplemented, and placement still needs local captures.
  See [opening encounter](../docs/protocol/12-quests.md#opening-encounter-2026-10-25).

## Not working yet

Nothing yet.
