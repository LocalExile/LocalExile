# Act 6 quests

Act 6 (Part Two) starts in Lioneye's Watch (`2_6_town`), where Lilly Roth's NPCTalk 893 "Sail to Wraeclast" in act 5
drops the player (`triggerer.AreaTransitionTo(2_6_town, 0, 0.5)`, see [act5.md](act5.md); entering sets
`Visited-6_town`, every act 6 area before the Karui Fortress needs `a5q6-CompletedBoss`). The town has four quest NPCs,
all town-room NPCs: Tarkleigh (`Metadata/NPC/Act6/Tarkleigh`, model of act 1's), Bestel, Lilly Roth and Sin (`SinTown`).
The main line is three quests: a6q2 "Essence of Umbra" (Sin: kill Shavronne in Axiom Prison) needs a6q3 "The Father of
War" first (the Submerged Passage is flooded: the way to the Prison goes through the Karui Fortress, opened with the Eye
of Conquest, and its exit opens when Tukohama dies); `a6q2-KilledBoss` opens Prisoner's Gate and the forests; a6q1
"The Brine King" (Tarkleigh: find Nessa) runs through the whole act: Nessa is seen three times (Mud Flats, Prisoner's
Gate, Cavern of Anger), the Black Flag is taken from a chest in the Cavern of Anger and burnt at the Beacon, Weylam Roth
ships the player to the Brine King's Reef, and after the Brine King dies Weylam's NPCTalk 1010 moves the player to the
Bridge Encampment (`2_7_town`, act 7). Side quests: a6q4 "Fallen from Grace" (Lilly, clear the Twilight Strand: Book of
Regrets), a6q5 "Bestel's Epic" (manuscript chest on the Tidal Island), a6q6 "The Puppet Mistress" (Tarkleigh: Ryslatha
in the Wetlands), a6q7 "The Cloven One" (Bestel: Abberath at Prisoner's Gate). The Pantheon starts here: after each god
dies a Sin NPC appears next to the corpse and opens the Pantheon screen (Tukohama is the first, it unlocks "Open
Pantheon" at Sin in town). Four Books of Skill (+1 passive each: a6q3, a6q6, a6q7; a6q4's is a Book of Regrets, +2
respec only).
Sources: client tables (`data/tables`), the generated layouts (`lg.ps1 questobjects`, `lg.ps1 grep` of the room files),
the unpacked `.ot`/`.it` text (references, not copied), the wiki (Fandom mirror, 2026-10-30), poedb not needed.

## Status (implemented, played live 2026-10-30)

Every quest of the act runs on the engine (`Modules/Core/World/QuestObjects/Acts/Act6.cs`,
[12](../../protocol/12-quests.md) §15) and was played unattended by the campaign runner (driver steps `a6q4`, `a6q5`,
`a6q3`, `a6q2`, `a6q7`, `a6q6`, `a6q1`) before the game moved into modules; on the modules the oracle
(`QuestEngineTests`) plays it after Act 5: every quest of the act complete, the Brine King, Tukohama, Abberath and
Ryslatha unlocked in the [Pantheon](pantheon.md), arrival in the Bridge Encampment. What differs from the sections below:
- The Twilight Strand, The Karui Fortress and The Southern Forest fail at seed 1 in the client as in the port: they are
  played at a seed the client built (`ServerLevels.PlayableSeed`, `ServerLevelAreas.SeedsInPlaceOfOne`).
- The Beacon is simplified: the fuel carts (`BeaconPayload`, a Movement object) are not created; the ignition lever
  sets `a6q1-RefueledBeacon` and the beacon statue takes the Black Flag (`a6q1-DeliveredFlag`), which brings Weylam
  ashore. The Black Flag chest is a point line of Merveil's room, placed from the server's own rooms.
- The three Nessas set their Seen flags when the player comes near and transition away; Weylam's two sailing rows run
  their `AreaTransitionTo`. The god Sins appear where the gods die; their row sets `CanChangePantheon` and opens the
  Pantheon panel, and the runner chooses the gods (`pantheon` verb).
- `a6q2-KilledBoss` is Shavronne's death (the data does not say whose); Abberath's second phase appears where the first
  dies and sets `a6q7-KilledBoss`.

## Areas

| WorldAreas Id | name | lvl | wp | Gateway | EndQuestFlags (gate) | unique bosses (MV Id, spawnable?) | quest objects / NPCs found |
|---|---|---|---|---|---|---|---|
| `2_6_town` | Lioneye's Watch | 50 | town | yes | `a5q6-CompletedBoss` | — | Tarkleigh, Bestel, Lilly (x2 room lines), SinTown, `Metadata/QuestObjects/Act6/LillyShip` (room `part2_beach_town.arm` (404, 228)) |
| `2_6_1` | The Twilight Strand | 45 | no | yes (seed 1 fails, client too) | `a5q6-CompletedBoss` | Rhys of Abram `OriathanCitizenZombieMale4Boss` (5556, yes; `washed_up3.arm` mapboss) | client-only `Metadata/NPC/Act1/Doorman`, lore `BrineKingGlyph1` |
| `2_6_2` | The Coast | 45 | yes | yes | `a5q6-CompletedBoss` | Raihara `HoundBoss2` (4241, yes) | `MudFlatsBlocker` (Brequel, league) |
| `2_6_3` | The Tidal Island | 45 | no | yes | `a5q6-CompletedBoss` | Riptide `WaterElementalUnique` (1146, yes) | `Metadata/Chests/QuestChests/BestelChest` room line `medicine_boat_1.arm` (106, 90) / `medicine_boat_2.arm` (181, 96) |
| `2_6_4` | The Mud Flats | 46 | no | yes | `a5q6-CompletedBoss` | The Dishonoured Queen `KaomWarrior/KaruiElder1` (5933, yes), Forgotten Warrior `KaomArcherMinion1/2/3` (5938/5940/5942, yes) — points `mapboss1..4` of `floodedcave.arm`, NOT `mapboss` | `NessaMudFlats` (`floodedcave.arm` (63, 229)), `SunkenPassageFlooded` (same room (67, 216)), `MudFlatsKaruiDoor` + `MudFlatsKaruiTransition` (tile `beach_karuipools_v01_01.tdt` records `…_gate` / `…_area_transition`, tile cell (134, 70)), act 1's GlyphWall/WaterCaveAreaTransition (tile, inert here) |
| `2_6_5` | The Karui Fortress | 46 | no | yes (seed 1 fails, client too; seed 2018765590 ok) | `a6q3-DeliveredItem` | Tukohama `Tukohama/Tukohama` (5783, yes, boss bar; `miniboss.arm` mapboss (277, 390)) | `FortressKaruiDoor` (tile `swamp_to_ridge_v01_01.tdt` record `…_animated`), `ToRidgeTransition` (same tile, `…_toridge_transition`), `BeamTotemSpawner` x1, `ShieldingTotemSpawner` x4, `ArenaMiddle`, the Mud Flats side of the Karui door (`beach_karuipools_v01_02.tdt`) |
| `2_6_6` | The Ridge | 46 | yes | yes | `a6q3-KilledBoss` | Fleetfreak `CorpseWheelBoss` (5599, yes) | — |
| `2_6_7_1` | The Lower Prison | 47 | yes | yes | `a6q3-KilledBoss` | — | prison doors |
| `2_6_7_2` | Shavronne's Tower | 47 | no | yes | `a6q3-KilledBoss` | Shavronne the Returned `ShavronneTower` (5488, yes, bar), Reassembled Brutus `BruteTower` (5482, yes, bar) — real arena `Area7Level2/Rooms/Unique/boss1.arm` points `mapboss1` (257, 257) / `mapboss2` (217, 253), not in room-points | `BruteCradlePart2`, `TeleportMarker` x33, `GenericBlockerDisable`, `AreaTransitionToggleable` x6, lore `ShavronneManifesto` |
| `2_6_8` | Prisoner's Gate | 47 | yes | yes | `a6q2-KilledBoss` | Abberath `Abberath/Abberath` (6008, yes, bar), `Abberath/Abberath2` (6012, yes, bar) — arena points `mapboss1WG` (`bossphase1.arm`) / `mapboss2WG` (`bossphase2.arm`) | `NessaPrisonersGate` (`shipgraveyard.arm` (134, 77)), `BlockedPassageTransition` + `SpikeBlockageButton` (tile `beach_passageblock_v01_01.tdt`), `Part2EntranceEnabler` (`blocked_passage.arm` (72, 80)), `DoorAbberathExterior` x3 / `Phase1` / `Phase2`, goat spawners, meteor markers |
| `2_6_9` | The Western Forest | 48 | yes | yes | `a6q2-KilledBoss` | Defiled Proclamation `RockGolemProclamationBoss` (4754, yes) | `BlockedPassageTransition` (tile `forest_mountainpass_v01_01.tdt`), `Part2EntranceEnabler` (`passageblock.arm` (154, 70)), `BanditDoor`, Doedre stones (optional) |
| `2_6_10` | The Riverways | 48 | yes | yes | `a6q2-KilledBoss` | The Animal Pack `AnimalPackBoss` (104, yes) | bandit tent spawners |
| `2_6_11` | The Wetlands | 48 | no | yes | `a6q2-KilledBoss` | Ryslatha `InsectSpawnerParasiteBoss` (1610, yes, bar; `bossfight.arm` mapboss (163, 163)) | `RyslathaFightManager`, `ParasiteNestRyslatha` x3 / `RyslathaCausticGroundDaemon` x7 (named points of `bossfight.arm`), `BanditDoor` |
| `2_6_12` | The Southern Forest | 49 | yes | yes (seed 1 fails, client too) | `a6q2-KilledBoss` | Hollowskull (395, yes) | — |
| `2_6_13` | The Cavern of Anger | 49 | no | yes | `a6q2-KilledBoss` | Brinecrack `NessaCrab` (6004, yes) | `NessaMerveilsCaverns` (`merveil_empty_nessa.arm` (281, 214)), **`BlackFlagChest`** (same room, named point (255, 217), angle 4.36), `Part2MerveilExit` (same room (221, 243)), `MerveilExitTransition` + `MerveilExit` x2 (tile `merveil_exit_clean_v01_01.tdt`), `BossArenaEntranceTransition`, `Door_Hideout`/`Switch_Hideout`, `AreaTransitionToggleableReverse` |
| `2_6_14` | The Beacon | 49 | yes | yes | `a6q2-KilledBoss` | — (statue spawners) | `WeylamBeacon` (`lighthouse.arm` (421, 890)), `BeaconPayload` x2 (`lighthouse.arm` (471, 448), (540, 586)), `BeaconFuelTank` (tile `beacon_stairs_v01_01.tdt` `…_FuelTankLiquid`), `BlackCrest_BeaconLever` (`lighthouse.arm` (482, 782)), `BlackCrest_BeaconInteract` (tile `…_BeaconStatue_Scaled`), `BlackCrest_BeaconFlame` (tile `…_BeaconBrazier`), `BlackCrest_Beacon` (`lighthouse.arm` (479, 867)), ghost pirate/totem spawners + `BeaconPayload_SpawnController` x10 |
| `2_6_15` | The Brine King's Reef | 50 | yes | yes | `a6q1-DeliveredFlag` | Tsoagoth `BrineKing/BrineKing` (5493, yes, bar; `boss_1.arm` mapboss (211, 278)) | `WeylamReef1` (`entrance_1.arm` (106, 164)), `WeylamReef2` (`boss_1.arm` (305, 210)), `BlackCrest_Reef` (`boss_1.arm` (380, 254)), `BrineKingMinionSpawner` x11, `ArenaMiddle` |
| `1_SideArea6_4` / `1_SideArea6_6` | Radiant Pools / Clouded Ledge | — | no | yes | — | Vaal side-area bosses | not quest-related |

Not created by any room data: `Metadata/NPC/Act6/NessaBrineKing`, `SinTukohama`, `SinShavronne`, `SinAbberath`,
`SinRyslatha`, `SinBrineKing` (spawned by the real server's boss scripts) and the act 6 Eye of Conquest drop.

## a6q1 "The Brine King" (Quest row 70, main)
**Start:** Tarkleigh, NPCTalk 895 "Nessa". **Finish:** Weylam Roth on the Reef sails to `2_7_town` (NPCTalk 1010).
**DONE flag (driver):** `Visited-7_town` (QuestStates Order 0; it is also act 7's arrival). `a6q1-KilledBoss` is the
"boss dead" flag; Tarkleigh's 897 (`a6q1-Congratulated`) is talk only, no reward offer.
**Prerequisites:** `TarkleighIntroduction2` (Tarkleigh's NPCTalk 894 "Introduction"). The Beacon and forests need
`a6q2-KilledBoss`; the Reef needs `a6q1-DeliveredFlag`.

### Sequence
| # | Real-game step | Area (Id, name) | Event | Flags set | Notes / source |
|---|---|---|---|---|---|
| 1 | Tarkleigh's introduction | `2_6_town` | talk Tarkleigh NPCTalk 894 "Introduction" | `TarkleighIntroduction2` (QF4+QF5) | generic dialogue |
| 2 | Receive the quest | `2_6_town` | NPCTalk 895 "Nessa" (needs `TarkleighIntroduction2`, not `a6q1-KilledBoss`/`SeenNessa1`/`SeenNessa2`) | `a6q1-QuestReceived` (QF4) | |
| 3 | See Nessa (mermaid) in the flooded cave | `2_6_4` The Mud Flats | come near `Metadata/NPC/Act6/NessaMudFlats` (`floodedcave.arm` (63, 229)); server transitions it (client plays her line, dives after 18 s) | `a6q1-SeenNessa1` | no data sets it: server script ("NPC seen" trigger). Tarkleigh 896 then sets `TarkleighNessaSeen` |
| 4 | See Nessa at the waterfall | `2_6_8` Prisoner's Gate | come near `NessaPrisonersGate` (`shipgraveyard.arm` (134, 77)); transition (24 s, jumps back) | `a6q1-SeenNessa2` | server script |
| 5 | See Nessa in Merveil's cave | `2_6_13` The Cavern of Anger | come near `NessaMerveilsCaverns` (`merveil_empty_nessa.arm` (281, 214)); transition (27 s) | `a6q1-SeenNessa3` | server script; log "Take the Black Flag" |
| 6 | Open the Flag Chest | `2_6_13` | open `Metadata/Chests/QuestChests/BlackFlagChest` (Chests row 208 "Flag Chest", named point (255, 217) of `merveil_empty_nessa.arm`); drops `Metadata/Items/QuestItems/Act6/BlackFlag` (BIT 1578 "The Black Flag", 2x2) | — | chest→item drop (a1q4 nest pattern) |
| 7 | Pick up the Black Flag | `2_6_13` | pickup BIT 1578 | `a6q1-HaveFlag` (`blackflag.it` grant_flag) | `remove_flag` = `a6q1-DeliveredFlag` |
| 8 | Reach the Beacon, see it | `2_6_14` The Beacon | come near the beacon (lighthouse) | `a6q1-SeenBeacon` | server script (trigger object: probably `BlackCrest_BeaconInteract` / `CameraZoom/Act6Beacon` (482, 748)) |
| 9 | Push the two fuel carts to the tank | `2_6_14` | `BeaconPayload` x2 move along their rail while the player pushes (Movement `FollowPath`, `pushed_by_player`, speed 13); each arrival transitions `BeaconFuelTank` (states 1→2→3); ghost pirates spawn on the way | `a6q1-RefueledBeacon` (after both) | server script; `RefueledBeacon` is in no preset |
| 10 | Pull the ignition switch | `2_6_14` | interact `BlackCrest_BeaconLever` (state 2 → 3); `BlackCrest_BeaconFlame` 1 → 2 (lit), `BlackCrest_BeaconInteract` 1 → 2 (targetable) | — | server script |
| 11 | Throw the Black Flag into the flames | `2_6_14` | interact `BlackCrest_BeaconInteract` ("Beacon") holding the flag: flag taken, flame 2 → 3, `BlackCrest_Beacon` transitions (ship arrives), `WeylamBeacon` transitions (jumps ashore, then targetable) | `a6q1-DeliveredFlag` | server script (like a1q4's glyph wall) |
| 12 | Talk to Weylam | `2_6_14` | NPCTalk 1005 "Introduction" | `WeylamBeaconIntro` (QF4+QF5) | |
| 13 | Sail to the Reef | `2_6_14` → `2_6_15` | NPCTalk 1006 "Sail to the Brine King's Reef": Script `triggerer.AreaTransitionTo(2_6_15, 1, 0.5); triggerer.FinishNPCChat();`, Script2 `triggerer.FadeToBlack( 500, 5000, 1000);` | `Visited-6_15` on entry | NPC script transport (new) |
| 14 | Weylam at the landing | `2_6_15` | NPCTalk 1007 "The Brine King's Reef" (`WeylamReef1`) | `WeylamReefIntro` | optional |
| 15 | Find Nessa / the Brine King | `2_6_15` | see Tsoagoth (MV 5493) | `a6q1-SeenBoss` | `OnMonsterSeen`; real game also spawns `NessaBrineKing` (server-only NPC, not in room data) |
| 16 | Kill the Brine King | `2_6_15` | kill MV 5493 `Metadata/Monsters/BrineKing/BrineKing` | `a6q1-KilledBoss` | scripted fight (below); Pantheon: unlocks major god TheBrineKing |
| 17 | Ship and Weylam appear, Sin appears | `2_6_15` | on the kill: `BlackCrest_Reef` transitions, `WeylamReef2` 1 → 2, spawn `SinBrineKing` at the corpse | — | server script |
| 18 | Sin's Pantheon gift | `2_6_15` | NPCTalk 1003 "The Brine King" (`SinBrineKing`): Script `triggerer.SetSessionQuestFlag(CanChangePantheon); triggerer.FinishNPCChat(); triggerer.SendOpenScreen(PantheonScreen);` (fallback: SinTown 971) | `SinBrineKingReward` | Pantheon section (coordinator) |
| 19 | Sail to act 7 | `2_6_15` → `2_7_town` | NPCTalk 1010 "Sail to the Bridge Encampment" (`WeylamReef2`, needs `a6q1-KilledBoss`): Script `triggerer.AreaTransitionTo(2_7_town, 0, 0.5); triggerer.FinishNPCChat();`, Script2 FadeToBlack | `Visited-7_town` on entry (`2_7_town` gate `a6q1-KilledBoss`) | **act 6 → 7 transition** |
| 20 | (optional) Tarkleigh | `2_6_town` | NPCTalk 897 "Nessa" | `a6q1-Congratulated` | no reward |

### Objects, NPCs and bosses
| What | Type / MV Id / BaseItemTypes Id | Where it comes from | Server today | Needed |
|---|---|---|---|---|
| Nessa (Mud Flats) | `Metadata/NPC/Act6/NessaMudFlats` (server_only; Animated, Transitionable, NPC, MinimapIcon; client Timer) | room server object `Act6/Area4/Rooms/floodedcave.arm` (63, 229) | not created (out-of-town NPC) | create; transition on proximity; set `a6q1-SeenNessa1`; no NPCTalk rows |
| Nessa (Prisoner's Gate) | `NessaPrisonersGate` | `Act6/Area8/Rooms/shipgraveyard.arm` (134, 77) | not created | same, `a6q1-SeenNessa2` |
| Nessa (Cavern of Anger) | `NessaMerveilsCaverns` | `Act6/Area13/Rooms/merveil_empty_nessa.arm` (281, 214) | not created | same, `a6q1-SeenNessa3` |
| Flag Chest | `Metadata/Chests/QuestChests/BlackFlagChest` (Chests 208, no own `.ot`: inherits `Chests/QuestChests/QuestChest` = the Rhoa nest components) | a named point line of `merveil_empty_nessa.arm` (255, 217) (point-list form, not a server-object line: `lg.ps1 questobjects` misses it) | not created | create; drop BIT 1578 once while the character lacks `a6q1-HaveFlag`/`a6q1-DeliveredFlag` |
| The Black Flag | `Metadata/Items/QuestItems/Act6/BlackFlag` (BIT 1578) | chest | pickup works like ground items | sync `a6q1-HaveFlag`; take it at the beacon |
| Fuel carts | `Metadata/QuestObjects/Act6/BeaconPayload` x2 (team 4, Stats, **Movement** FollowPath `pushed_by_player`, Transitionable, MinimapIcon; `set_disable_during_hellscape`) | `lighthouse.arm` (471, 448), (540, 586) | not created | **new serialisation (Movement) + rail path following**; or a simplified server move along the rail when the player stands next to it |
| Fuel tank | `BeaconFuelTank` (Transitionable `num_states = 3`) | tile `Beach/Beacon/beacon_stairs_v01_01.tdt` record `beacon_stairs_v01_01_FuelTankLiquid` | not created | state 2 after cart 1, 3 after cart 2 |
| Ignition switch | `BlackCrest_BeaconLever` (InteractionAction, Transitionable 3 states, MinimapIcon) | `lighthouse.arm` (482, 782), scale 0.85 | not created | state 2 when refuelled, interact → 3 |
| Brazier | `BlackCrest_BeaconFlame` (Transitionable 3 states, MinimapIcon) | tile record `beacon_stairs_v01_01_BeaconBrazier` | not created | 2 on lever, 3 on flag |
| Beacon statue | `BlackCrest_BeaconInteract` (InteractionAction, Transitionable 3 states; "Beacon") | tile record `beacon_stairs_v01_01_BeaconStatue_Scaled` | not created | 2 on lever; interact with the flag → `a6q1-DeliveredFlag`, 3 |
| The Black Crest (beacon) | `BlackCrest_Beacon` (Transitionable) | `lighthouse.arm` (479, 867) | not created | transition on `DeliveredFlag` |
| Weylam (beacon) | `WeylamBeacon` (NPC, Transitionable: hidden/untargetable until transitioned) | `lighthouse.arm` (421, 890) | not created | create; transitioned when `a6q1-DeliveredFlag`; NPCTalk 1005/1006 (1006 needs the transport script) |
| Weylam (landing) | `WeylamReef1` (NPC) | `Act6/Area15/Rooms/entrance_1.arm` (106, 164) | not created | create (gossip only) |
| Weylam (throne) | `WeylamReef2` (NPC, Transitionable states 1/2) | `boss_1.arm` (305, 210) | not created | create; state 2 when `a6q1-KilledBoss`; NPCTalk 1010 transport |
| The Black Crest (reef) | `BlackCrest_Reef` (Transitionable) | `boss_1.arm` (380, 254) | not created | transition on the kill |
| Brine King | MV 5493 `Metadata/Monsters/BrineKing/BrineKing` | WorldAreas Bosses, `boss_1.arm` mapboss (211, 278) | placed (spawnable, boss bar) if the room is fitted | kill trigger → `a6q1-KilledBoss`; `a6q1-SeenBoss` on sight |
| Nessa (reef) | `Metadata/NPC/Act6/NessaBrineKing` (server_only NPC, MinimapIcon; no model line) | not in room data | not created | optional (story only) |
| Sin (reef) | `Metadata/NPC/Act6/SinBrineKing` (NPC, Transitionable entrance; Positioned `unblock_quest_flag = "Visitedminus7_town"`) | spawned by script | not created | spawn at the corpse on the kill; NPCTalk 1003 script (Pantheon) |

### Gates
- `2_6_15` EndQuestFlags `a6q1-DeliveredFlag`; `2_7_town`, `2_7_1`…`2_7_5_map` EndQuestFlags `a6q1-KilledBoss`.
- The only way to the Reef and to act 7 is the two Weylam NPC scripts (`AreaTransitionTo`). WorldAreas also lists the
  connections `2_6_14`↔`2_6_15` and `2_6_15`→`2_7_town`; after the first visit the Reef waypoint works.
- Cavern of Anger exit to The Beacon: `Metadata/QuestObjects/MerveilExitTransition` (AreaTransition with
  `disable_auto = true`, untargetable until transitioned) beside `MerveilExit` x2 (TriggerableBlockage start_blocked,
  3 states). In act 6 the server-only `Metadata/Terrain/Cave/Objects/Part2MerveilExit` (room line, `NPC` component) is
  the real server's enabler: the server must create the transition in its transitioned state (and the `MerveilExit`
  blockers in state 3), else `2_6_14` is unreachable on foot. Unverified whether it opens on entry or on `SeenNessa3`.
- Prisoner's Gate ↔ Western Forest: `Metadata/QuestObjects/Spikes/BlockedPassageTransition` (tile
  `beach_passageblock_v01_01.tdt` in `2_6_8`, `forest_mountainpass_v01_01.tdt` in `2_6_9`; `disable_auto`,
  untargetable until transitioned) with `Part2EntranceEnabler` (server-only `NPC` room objects). Act 6 has no
  `SpikeBlockageBlocker` lines (act 2's need `a1q9-RoadOpened`), so the server only has to send both transitions as
  transitioned (Part2 = always open). The `SpikeBlockageButton` (MinimapIcon `show_flag a2q11-HaveKey`) stays inert.

### Rewards
No reward offer, no static reward. Pantheon: `a6q1-KilledBoss` is PantheonPanelLayout TheBrineKing's unlock flag;
`SinBrineKingReward` (NPCTalk 1003 or SinTown 971).

### Quest log check
13 `a6q1-QuestReceived` → Find Nessa (Mud Flats) · 12 `SeenNessa1` → Travel through Prisoner's Gate to the Ship
Graveyard · 11 `SeenNessa2` → Travel through the forest to Merveil's Caverns · 10 `SeenNessa3` → Take the Black Flag ·
9 `HaveFlag` → Take the Black Flag to the Beacon · 8 `HaveFlag`+`SeenBeacon` → Refuel the Beacon · 7 `HaveFlag`+
`RefueledBeacon` → Light the Beacon · 6 `RefueledBeacon`, missing `HaveFlag`,`SeenNessa3` → Find the Black Flag ·
5 `DeliveredFlag` → Talk to Weylam Roth · 4 `WeylamBeaconIntro` → Sail to the Brine King's Reef · 3 `Visited-6_15` →
Find Nessa · 2 `a6q1-SeenBoss` → Defeat the Brine King · 1 `a6q1-KilledBoss` → Talk to Weylam Roth to access Act 7 ·
0 `Visited-7_town` → Quest Complete.

### Driver steps (draft)
```python
QUEST = "a6q1"
DONE = "Visited-7_town"
STEPS = [
    {"verb": "talk", "npc": "Tarkleigh", "option": "Introduction", "skip_if": "TarkleighIntroduction2"},
    {"verb": "talk", "npc": "Tarkleigh", "option": "Nessa", "skip_if": "a6q1-QuestReceived"},
    {"verb": "goto", "area": "2_6_4", "skip_if": "a6q1-SeenNessa1"},
    {"verb": "tp", "target": "NessaMudFlats", "skip_if": "a6q1-SeenNessa1"},
    {"verb": "wait_flag", "flag": "a6q1-SeenNessa1"},
    # Prisoner's Gate is reached only after a6q2 (order.py: run a6q3, a6q2 first)
    {"verb": "goto", "area": "2_6_8", "skip_if": "a6q1-SeenNessa2"},
    {"verb": "tp", "target": "NessaPrisonersGate", "skip_if": "a6q1-SeenNessa2"},
    {"verb": "wait_flag", "flag": "a6q1-SeenNessa2"},
    {"verb": "goto", "area": "2_6_13", "skip_if": "a6q1-SeenNessa3"},
    {"verb": "tp", "target": "NessaMerveilsCaverns", "skip_if": "a6q1-SeenNessa3"},
    {"verb": "wait_flag", "flag": "a6q1-SeenNessa3"},
    {"verb": "interact", "target": "BlackFlagChest", "skip_if": "a6q1-HaveFlag"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act6/BlackFlag", "skip_if": "a6q1-HaveFlag"},
    {"verb": "goto", "area": "2_6_14", "via": "transition", "skip_if": "a6q1-DeliveredFlag"},
    {"verb": "tp", "target": "BlackCrest_BeaconInteract", "skip_if": "a6q1-SeenBeacon"},
    {"verb": "wait_flag", "flag": "a6q1-SeenBeacon"},
    {"verb": "interact", "target": "BeaconPayload", "skip_if": "a6q1-RefueledBeacon"},  # mechanism TBD (push)
    {"verb": "wait_flag", "flag": "a6q1-RefueledBeacon", "timeout": 120},
    {"verb": "interact", "target": "BlackCrest_BeaconLever", "skip_if": "a6q1-DeliveredFlag"},
    {"verb": "interact", "target": "BlackCrest_BeaconInteract", "skip_if": "a6q1-DeliveredFlag"},
    {"verb": "wait_flag", "flag": "a6q1-DeliveredFlag"},
    {"verb": "talk", "npc": "Weylam Roth", "option": "Introduction", "skip_if": "WeylamBeaconIntro"},
    {"verb": "talk", "npc": "Weylam Roth", "option": "Sail to the Brine King's Reef", "skip_if": "Visited-6_15"},
    {"verb": "wait_flag", "flag": "Visited-6_15"},
    {"verb": "kill", "target": "Metadata/Monsters/BrineKing/BrineKing", "skip_if": "a6q1-KilledBoss"},
    {"verb": "wait_flag", "flag": "a6q1-KilledBoss"},
    {"verb": "talk", "npc": "Sin", "option": "The Brine King", "skip_if": "SinBrineKingReward"},
    {"verb": "pantheon", "major": "Soul of the Brine King", "minor": "Soul of Tukohama"},  # coordinator decides
    # claim every act 6 town reward before this step (see order notes)
    {"verb": "talk", "npc": "Weylam Roth", "option": "Sail to the Bridge Encampment", "skip_if": "Visited-7_town"},
    {"verb": "wait_flag", "flag": "Visited-7_town"},
]
```

### Risks
- Three out-of-town Nessa NPCs and their "seen" trigger: no flag is set by data; needs an NPC-proximity hook
  (like `OnMonsterSeen`) plus a Transitionable state change.
- `BlackFlagChest` is a named point (`x y angle "type"`) of the room's point list, not a server-object line: the room
  point reader must keep chest types (the generic room-points fitter only keeps `mapboss`/packs).
- The Beacon is the biggest act 6 blocker: two pushable carts (`Movement` component, rail path) + three 3-state objects
  + script order; nothing exists. A driver needs a simplified mechanism (e.g. interact a cart = it slides to the tank).
- NPC script transport `AreaTransitionTo(area, index, 0.5)` + `FadeToBlack` (NPCTalk 1006, 1010; also act 5's 893):
  the target index (0/1) picks the arrival spot; not implemented today.
- Brine King fight: phases at 75/50/25 % flood the arena and make him untargetable, `BrineKingMinionSpawner` x11,
  `ArenaMiddle`. `/kill` skips it all; only placement at `boss_1.arm` mapboss is needed.
- `MerveilExitTransition` (Cavern of Anger → Beacon) and `BlockedPassageTransition` (Prisoner's Gate ↔ Western
  Forest) must be sent transitioned in act 6, otherwise the main path is blocked on foot (`/goto` still works).
- `a6q1-RefueledBeacon` appears in no preset (E3StartOfAct7 has `SeenBeacon`, `DeliveredFlag`): possibly cleared
  later or never saved; keep it saved (log Order 7/6 need it).

## a6q2 "Essence of Umbra" (Quest row 71, main)
**Start:** Sin in town, NPCTalk 955 "Shavronne of Umbra". **Finish:** Tarkleigh, NPCTalk 908 "Shavronne Reward".
**DONE flag (driver):** `a6q2-QuestRewardTaken` (offer 40).
**Prerequisites:** none for the quest (log Order 10 shows on `Visited-6_town`); the Prison needs `a6q3-KilledBoss`.

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Sin's task | `2_6_town` | NPCTalk 955 "Shavronne of Umbra" (SinTown) | `a6q2-QuestReceived` | |
| 2 | Way blocked | — | (a6q3 runs here) | — | log Order 8 with `a6q3-QuestReceived`, 7 with `a6q3-KilledBoss` |
| 3 | Ridge, Lower Prison, Tower | `2_6_6`, `2_6_7_1`, `2_6_7_2` | enter | `Visited-6_6`, `Visited-6_7_1`, `Visited-6_7_2` | gates `a6q3-KilledBoss` |
| 4 | Find Shavronne | `2_6_7_2` Shavronne's Tower | see MV 5488 | `a6q2-SeenBoss` | `OnMonsterSeen` |
| 5 | Kill Shavronne and Brutus | `2_6_7_2` | kill MV 5488 `Shavronne/ShavronneTower` (+ MV 5482 `Brute/BruteTower`) | `a6q2-KilledBoss` | log "Kill Shavronne and Brutus"; which death(s) set the flag is unverified: safest = both dead |
| 6 | Sin harvests her soul | `2_6_7_2` | spawn `SinShavronne` (SinBossNoPantheon); NPCTalk 995 "Shavronne" Script `triggerer.FinishNPCChat();` | `SinShavronneReward` | no Pantheon god |
| 7 | Tarkleigh | `2_6_town` | NPCTalk 907 "Shavronne of Umbra" (needs `a6q2-KilledBoss`) | `TarkleighOnShavronneDead` | |
| 8 | Reward | `2_6_town` | NPCTalk 908 "Shavronne Reward" offer 40 | `a6q2-QuestRewardSeen`, taken `a6q2-QuestRewardTaken` | |

### Objects, NPCs and bosses
| What | Type / MV Id | Where | Server today | Needed |
|---|---|---|---|---|
| Shavronne the Returned | MV 5488 `Metadata/Monsters/Shavronne/ShavronneTower` (boss bar) | WorldAreas Bosses; real arena `Act6/Area7Level2/Rooms/Unique/boss1.arm` `mapboss1` (257, 257) | placed only at a fitted `mapboss` (room-points lists `special_skeletonboss1.arm`) | place both at `mapboss1`/`mapboss2` of `boss1.arm` (named points with digits) |
| Reassembled Brutus | MV 5482 `Metadata/Monsters/Brute/BruteTower` (boss bar) | `mapboss2` (217, 253); `BruteCradlePart2` (217, 274) | as above | place; real fight: Brutus rises from the cradle |
| Sin | `Metadata/NPC/Act6/SinShavronne` (extends SinBossNoPantheon: NPC, Transitionable) | spawned by script | not created | spawn on the kill (or rely on nothing: no flag depends on `SinShavronneReward` for progress) |
| Tower exit | `AreaTransitionToggleable` x6 (tile `prison_exit_up_v01_01.tdt`) | tile | created as transitions | check whether the exit to `2_6_8` is a toggleable one that must open after the fight |

### Gates
`a6q2-KilledBoss` is EndQuestFlags of `2_6_8`, `2_6_9`, `2_6_10`, `2_6_11`, `2_6_12`, `2_6_13`, `2_6_14`.

### Rewards
Offer 40 `a6q2` (taken `a6q2-QuestRewardTaken`), Tarkleigh NPCTalk 908 "Shavronne Reward" (needs
`TarkleighOnShavronneDead`): helmets L47, two per class in rarity 2 and rarity 1 (Marauder: Siege Helmet, Zealot Helmet,
Fencer Helm; Witch: Steel Circlet, Callous Mask, Zealot Helmet; …; Scion: all six). None buildable today (RewardLevel
47, rarity 1/2: needs the ItemRolls gear path, which exists for Ruthless only).

### Quest log check
10 `Visited-6_town` → Talk to Sin · 9 `QuestReceived` → Kill Shavronne · 8 +`a6q3-QuestReceived` → Clear the way ·
7 +`a6q3-KilledBoss` → Karui Fortress exit open · 6 +`Visited-6_6` · 5 +`Visited-6_7_1` · 4 +`Visited-6_7_2` ·
3 `SeenBoss` → Kill Shavronne and Brutus · 2 `KilledBoss` → Talk to Tarkleigh · 1 `QuestRewardSeen` · 0
`QuestRewardTaken` → complete.

### Driver steps (draft)
```python
QUEST = "a6q2"
DONE = "a6q2-QuestRewardTaken"
STEPS = [
    {"verb": "talk", "npc": "Sin", "option": "Shavronne of Umbra", "skip_if": "a6q2-QuestReceived"},
    {"verb": "wait_flag", "flag": "a6q3-KilledBoss", "timeout": 5},  # a6q3 must run first (order.py)
    {"verb": "goto", "area": "2_6_7_2", "skip_if": "a6q2-KilledBoss"},
    {"verb": "kill", "target": "Metadata/Monsters/Brute/BruteTower", "skip_if": "a6q2-KilledBoss"},
    {"verb": "kill", "target": "Metadata/Monsters/Shavronne/ShavronneTower", "skip_if": "a6q2-KilledBoss"},
    {"verb": "wait_flag", "flag": "a6q2-KilledBoss"},
    {"verb": "talk", "npc": "Sin", "option": "Shavronne", "skip_if": "SinShavronneReward"},
    {"verb": "talk", "npc": "Tarkleigh", "option": "Shavronne of Umbra", "skip_if": "TarkleighOnShavronneDead"},
    {"verb": "reward", "npc": "Tarkleigh", "option": "Shavronne Reward", "skip_if": "a6q2-QuestRewardTaken"},
]
```

### Risks
- Boss placement: the fitted room list misses the real arena (`boss1.arm` points `mapboss1`/`mapboss2`); both bosses
  may land in `special_skeletonboss1.arm` or not at all.
- The `SinShavronne` NPC is never created today; harmless for progress (Sin town has no Shavronne fallback row, so
  `SinShavronneReward` stays unset without it: it is in E3StartOfAct7).
- Offer 40 is not buildable: the driver cannot take it; `DONE` would never be set — the driver should accept
  `a6q2-QuestRewardSeen` or the server must build magic/rare helmets.

## a6q3 "The Father of War" (Quest row 72, main)
**Start:** Tarkleigh, NPCTalk 901 "The Karui Fortress" (needs `Visited-6_4`), or the log starts on
`a6q3-SeenEntrance`/`a6q3-HaveItem`. **Finish:** Tarkleigh, NPCTalk 905 "Tukohama Reward" (Book of Skill).
**DONE flag (driver):** `a6q3-UsedRewardBook` (QuestStates Order 0; QSR 14 +1 passive).
**Prerequisites:** `Visited-6_4` for the talk; `2_6_5` needs `a6q3-DeliveredItem`.

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Enter the Mud Flats | `2_6_4` | enter | `Visited-6_4` | |
| 2 | Tarkleigh's task | `2_6_town` | NPCTalk 901 "The Karui Fortress" | `a6q3-QuestReceived` | Sin 960, Bestel 923 gossip |
| 3 | See the sealed totem door | `2_6_4` | come near `MudFlatsKaruiDoor` | `a6q3-SeenEntrance` | server script (proximity) |
| 4 | Kill the Dishonoured Queen | `2_6_4` | kill MV 5933 `KaomWarrior/KaruiElder1` (with Forgotten Warriors 5938/5940/5942) | — | drops the eye (wiki); no data names the drop |
| 5 | Pick up the Eye of Conquest | `2_6_4` | pickup `Metadata/Items/QuestItems/Act6/KaruiEye` (BIT 1576 "Eye of Conquest") | `a6q3-HaveItem` (`karuieye.it` grant_flag) | Tarkleigh 904 gossip sets `TarkleighOnKaruiEye` |
| 6 | Put the eye in the totem | `2_6_4` | interact `Metadata/QuestObjects/Act6/MudFlatsKaruiDoor` holding the eye: eye taken, door transitions (opens, untargetable), `MudFlatsKaruiTransition` transitions (targetable after 2 s) | `a6q3-DeliveredItem` (eye `remove_flag`) | server script; client stops blocking (`unblock_quest_flag`) |
| 7 | Enter the Karui Fortress | `2_6_5` | area transition `MudFlatsKaruiTransition` | `Visited-6_5` | gate `a6q3-DeliveredItem`; Tarkleigh 902 sets `a6q3-BossClue` |
| 8 | Find the locked exit | `2_6_5` | come near `FortressKaruiDoor` | `a6q3-SeenExit` | server script |
| 9 | See Tukohama | `2_6_5` | see MV 5783 | `a6q3-SeenBoss` | |
| 10 | Kill Tukohama | `2_6_5` | kill MV 5783 `Metadata/Monsters/Tukohama/Tukohama` | `a6q3-KilledBoss` | Pantheon: unlocks minor god Tukohama; opens the exit (client blockers) |
| 11 | Exit opens | `2_6_5` | on the kill: `FortressKaruiDoor` transitions, `ToRidgeTransition` transitions (targetable after 1.33 s) | — | server script |
| 12 | Sin appears, first Pantheon | `2_6_5` | spawn `SinTukohama` at the corpse; NPCTalk 993 "Tukohama": Script `triggerer.SetSessionQuestFlag(CanChangePantheon); triggerer.FinishNPCChat(); triggerer.SendOpenScreen(PantheonScreen);` (fallback SinTown 962) | `SinTukohamaReward` | Pantheon section |
| 13 | Tarkleigh | `2_6_town` | NPCTalk 903 "Tukohama" | `a6q3-Congratulated` | |
| 14 | Reward | `2_6_town` | NPCTalk 905 "Tukohama Reward" offer 41 | `a6q3-QuestRewardSeen` | |
| 15 | Read the book | anywhere | use `Metadata/Items/QuestItems/Act6/Book-a6q3` (BIT 1579) | `a6q3-HaveRewardBook` on pickup, `a6q3-UsedRewardBook` on use | QSR 14: +1 passive |

### Objects, NPCs and bosses
| What | Type / MV Id / BIT | Where | Server today | Needed |
|---|---|---|---|---|
| The Dishonoured Queen (+3 warriors) | MV 5933 / 5938 / 5940 / 5942 | WorldAreas Bosses of `2_6_4`; `Act6/Area4/Rooms/floodedcave.arm` points `mapboss1` (201, 72) … `mapboss4` (202, 46) | NOT placed: the room list for `2_6_4` has only `to_coast.arm` (`default`); points are `mapbossN` | place at `mapboss1..4`; drop BIT 1576 on the Queen's death while the character needs it (Hailrake/medicine chest pattern) |
| Eye of Conquest | BIT 1576 `Metadata/Items/QuestItems/Act6/KaruiEye` (1x1) | dropped by MV 5933 (wiki) | — | item pickup flag sync |
| Totem door (Mud Flats) | `MudFlatsKaruiDoor` (server_only; Animated, Transitionable, MinimapIcon `hide_flag a6q3-DeliveredItem`, InteractionAction; Positioned blocking + `unblock_quest_flag = "a6q3minusDeliveredItem"`) | tile `Beach/Transitions/beach_karuipools_v01_01.tdt` record `beach_karuipools_v01_01_gate` (and `_v01_02` in `2_6_5`) | not created | create; use with the eye → flag + transition; `QuestBarriers` until the flag |
| Fortress entrance | `MudFlatsKaruiTransition` (AreaTransition + Transitionable) | same tile, record `…_area_transition` | transition created from the tile, untransitioned = untargetable | send transitioned when `a6q3-DeliveredItem` |
| Fortress exit door | `FortressKaruiDoor` (server_only; Transitionable; blocking, `unblock_quest_flag = "a6q3minusKilledBoss"`) | tile `Beach/Transitions/swamp_to_ridge_v01_01.tdt` record `…_animated` | not created | create; transitioned when `a6q3-KilledBoss` |
| Exit blocker | `FortressKaruiBlocker` (client-side, `no_serialise`, blocking until `a6q3-KilledBoss`) | not found in the seed layouts (probably a tile record of the same tile) | client spawns | server walk estimate (`QuestBarriers`) |
| Way to the Ridge | `ToRidgeTransition` (AreaTransition + Transitionable) | same tile, record `…_toridge_transition` | created, untargetable until transitioned | transition on `a6q3-KilledBoss` |
| Tukohama | MV 5783 (boss bar) | `miniboss.arm` mapboss (277, 390); `BeamTotemSpawner`, `ShieldingTotemSpawner` x4 | placed (spawnable) | kill trigger; totem phase skipped by `/kill` |
| Sin | `SinTukohama` (extends SinBossDeathParent; `unblock_quest_flag = "a6q3minusQuestRewardSeen"`) | spawned by script | not created | spawn on the kill; NPCTalk 993 script |
| Lilly's ship (town) | `LillyShip` (server_only, `serialisation_stop_flag = "a6q3minusKilledBoss"`) | `part2_beach_town.arm` (404, 228) | not created | cosmetic: show until `a6q3-KilledBoss` |

### Gates
`2_6_5` EndQuestFlags `a6q3-DeliveredItem`; `2_6_6`, `2_6_7_1`, `2_6_7_2` EndQuestFlags `a6q3-KilledBoss`.
Client blockers: `MudFlatsKaruiDoor` (`a6q3-DeliveredItem`), `FortressKaruiDoor` + `FortressKaruiBlocker`
(`a6q3-KilledBoss`): the server needs matching walk barriers. Pantheon: `a6q3-KilledBoss` is the Tukohama unlock and the
`needs` of every "Open Pantheon"/"View Pantheon" NPCTalk row (972, 994, 998, 1001, 1004, 1109, …).

### Rewards
Offer 41 `a6q3` (no taken flag): Book of Skill L1 = `Metadata/Items/QuestItems/Act6/Book-a6q3` (QuestRewards 1002),
Tarkleigh NPCTalk 905. Static reward QSR 14: flag `a6q3-UsedRewardBook`, +1 passive point.

### Quest log check
9 `QuestReceived` → Access the Karui Fortress · 8 `SeenEntrance` → Find the missing eye · 7 `HaveItem` → Access the
Karui Fortress (use the eye) · 6 `DeliveredItem` · 5 `Visited-6_5` → Find the exit · 4 `SeenExit` → Find a way to
unlock the exit · 3 `SeenBoss` → Defeat Tukohama · 2 `KilledBoss` → exit unlocked, Talk to Tarkleigh · 1
`HaveRewardBook` → use the book · 0 `UsedRewardBook` → complete.

### Driver steps (draft)
```python
QUEST = "a6q3"
DONE = "a6q3-UsedRewardBook"
STEPS = [
    {"verb": "goto", "area": "2_6_4", "skip_if": "Visited-6_4"},
    {"verb": "talk", "npc": "Tarkleigh", "option": "The Karui Fortress", "skip_if": "a6q3-QuestReceived"},
    {"verb": "goto", "area": "2_6_4", "skip_if": "a6q3-DeliveredItem"},
    {"verb": "tp", "target": "MudFlatsKaruiDoor", "skip_if": "a6q3-SeenEntrance"},
    {"verb": "kill", "target": "Metadata/Monsters/KaomWarrior/KaruiElder1", "skip_if": "a6q3-HaveItem"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act6/KaruiEye", "skip_if": "a6q3-DeliveredItem"},
    {"verb": "interact", "target": "MudFlatsKaruiDoor", "skip_if": "a6q3-DeliveredItem"},
    {"verb": "wait_flag", "flag": "a6q3-DeliveredItem"},
    {"verb": "goto", "area": "2_6_5", "via": "transition", "skip_if": "a6q3-KilledBoss"},
    {"verb": "kill", "target": "Metadata/Monsters/Tukohama/Tukohama", "skip_if": "a6q3-KilledBoss"},
    {"verb": "wait_flag", "flag": "a6q3-KilledBoss"},
    {"verb": "talk", "npc": "Sin", "option": "Tukohama", "skip_if": "SinTukohamaReward"},
    {"verb": "talk", "npc": "Tarkleigh", "option": "Tukohama", "skip_if": "a6q3-Congratulated"},
    {"verb": "reward", "npc": "Tarkleigh", "option": "Tukohama Reward", "pick": ["Book of Skill"],
     "skip_if": "a6q3-HaveRewardBook"},
    {"verb": "use_item", "item": "Book-a6q3", "skip_if": "a6q3-UsedRewardBook"},  # verb not in CONTRACT yet
    {"verb": "wait_flag", "flag": "a6q3-UsedRewardBook"},
]
```

### Risks
- The Dishonoured Queen is not placed (points `mapboss1..4` in `floodedcave.arm`, a room not in the fitted list) and
  nothing drops the eye: two missing features (named-point boss placement, monster quest drop).
- Two server-only doors with client-side blocking and two Transitionable area transitions (a1q4's wall/waterfall
  pattern; all components already serialisable).
- Book offers have no taken flag (`QuestRewardOffers.QuestFlag` null): `QuestRewardCatalog.Load` skips them today
  (`continue` on a null flag), so offers 41/42/44/45 open nothing. Needs "taken" = `…HaveRewardBook` or
  `…UsedRewardBook` (the NPCTalk `not[]` already hides the option after that).
- `UseSkillBook` grants +1 passive but does not set the book's `use_flag`; the server must also set
  `a6q3-UsedRewardBook` (and `HaveRewardBook` on pickup), otherwise the log stays at Order 1 and the oracle differs.
- `2_6_5` generation fails at seed 1 (client too); the Gateway must pick a working seed.

## a6q4 "Fallen from Grace" (Quest row 73, side)
**Start:** Lilly Roth in town, NPCTalk 944 "The Twilight Strand". **Finish:** Lilly, NPCTalk 946 "Twilight Strand
Reward" (Book of Regrets). **DONE flag (driver):** `a6q4-UsedRewardBook` (QSR 16: 2 respec points, no passive).
**Prerequisites:** `LillyIntroduction` (Lilly NPCTalk 939 "Introduction").

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Meet Lilly | `2_6_town` | NPCTalk 939 "Introduction" | `LillyIntroduction` | |
| 2 | Task | `2_6_town` | NPCTalk 944 "The Twilight Strand" | `a6q4-QuestReceived` | Tarkleigh 914 gossip |
| 3 | Clear the beach | `2_6_1` The Twilight Strand | kill every monster of the instance (incl. Rhys of Abram MV 5556 at `washed_up3.arm` mapboss) | `a6q4-AreaCleared` | server script: "area cleared" trigger (count alive hostile monsters = 0); `Visited-6_1` on entry |
| 4 | Report | `2_6_town` | NPCTalk 945 "The Twilight Strand" | `a6q4-Congratulated` | 943 "Trade" gossip sets `LillyOnAid` |
| 5 | Reward | `2_6_town` | NPCTalk 946 offer 42 | `a6q4-QuestRewardSeen`; `a6q4-HaveRewardBook` on pickup | 950 "Virtue Gems" then sets `UnlockedLillyGemShop` |
| 6 | Read the book | anywhere | use `Metadata/Items/QuestItems/Act6/Book-a6q4` (BIT 1580 "Book of Regrets") | `a6q4-UsedRewardBook` | +2 refund points |

### Objects, NPCs and bosses
| What | Type | Where | Server today | Needed |
|---|---|---|---|---|
| Lilly Roth | `Metadata/NPC/Act6/Lilly` (NPCBlock) | town room, two lines (one probably on the ship) | placed (town NPC) | — |
| Rhys of Abram | MV 5556 | `washed_up3.arm` mapboss | placed if fitted | part of the clear |
| Book of Regrets | BIT 1580 (`AbstractPassiveRespecBook`) | offer 42 | use handled (`RegretBook`: +2 refund points) | set `use_flag`; offer must be built (no taken flag) |

### Gates
`2_6_1` EndQuestFlags `a5q6-CompletedBoss`. No blockers.

### Rewards
Offer 42 `a6q4` (no taken flag): Book of Regrets L1 (QuestRewards 1003). Static QSR 16: flag `a6q4-UsedRewardBook`,
col1 = 0 passive, col7 = 2 respec points.

### Quest log check
3 `QuestReceived` → Clear the Twilight Strand · 2 `AreaCleared` → Talk to Lilly · 1 `HaveRewardBook` → use it for two
respec points · 0 `UsedRewardBook` → complete.

### Driver steps (draft)
```python
QUEST = "a6q4"
DONE = "a6q4-UsedRewardBook"
STEPS = [
    {"verb": "talk", "npc": "Lilly Roth", "option": "Introduction", "skip_if": "LillyIntroduction"},
    {"verb": "talk", "npc": "Lilly Roth", "option": "The Twilight Strand", "skip_if": "a6q4-QuestReceived"},
    {"verb": "goto", "area": "2_6_1", "via": "transition", "skip_if": "a6q4-AreaCleared"},
    {"verb": "kill", "target": "all", "skip_if": "a6q4-AreaCleared"},
    {"verb": "wait_flag", "flag": "a6q4-AreaCleared"},
    {"verb": "talk", "npc": "Lilly Roth", "option": "The Twilight Strand", "skip_if": "a6q4-Congratulated"},
    {"verb": "reward", "npc": "Lilly Roth", "option": "Twilight Strand Reward", "pick": ["Book of Regrets"],
     "skip_if": "a6q4-HaveRewardBook"},
    {"verb": "use_item", "item": "Book-a6q4", "skip_if": "a6q4-UsedRewardBook"},  # verb not in CONTRACT yet
    {"verb": "wait_flag", "flag": "a6q4-UsedRewardBook"},
]
```

### Risks
- "Area cleared" is a new trigger; what counts (all monsters spawned in the instance incl. packs and the boss) is
  inferred from the quest text. `/kill all` must reach every monster of the instance.
- `2_6_1` fails to generate at seed 1 (client too): the Gateway must use a working seed.
- E3StartOfAct7 has `a6q4-HaveRewardBook` but not `a6q4-UsedRewardBook` (a demo preset; E3EndOfAct8 has the used one).

## a6q5 "Bestel's Epic" (Quest row 74, side)
**Start:** Bestel, NPCTalk 933 "Bestel's Epic". **Finish:** Bestel, NPCTalk 935 "Bestel's Epic Reward".
**DONE flag (driver):** `a6q5-QuestRewardTaken` (offer 43). **Prerequisites:** none.

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Task | `2_6_town` | NPCTalk 933 | `a6q5-QuestReceived` | |
| 2 | Open the storage chest in the wreck | `2_6_3` The Tidal Island | open `Metadata/Chests/QuestChests/BestelChest` (Chests 207 "Storage Chest", model `Chests/Chests/Chest8old.ao`) | — | chest→item drop |
| 3 | Pick up the manuscript | `2_6_3` | pickup `Metadata/Items/QuestItems/Act6/BestelsManuscript` (BIT 1577, 2x2) | `a6q5-HaveItem` | |
| 4 | Hand it in | `2_6_town` | NPCTalk 934 (needs `a6q5-HaveItem`) — take the item | `a6q5-DeliveredItem` (item `remove_flag`) | NPC hand-in |
| 5 | Reward | `2_6_town` | NPCTalk 935 offer 43 | `a6q5-QuestRewardSeen`, taken `a6q5-QuestRewardTaken` | |

### Objects, NPCs and bosses
| What | Type / BIT | Where | Server today | Needed |
|---|---|---|---|---|
| Storage Chest | `BestelChest` (no own `.ot`; QuestChest components) | room server object `Act6/Area3/Rooms/medicine_boat_1.arm` (106, 90) or `medicine_boat_2.arm` (181, 96) — the `medicineboat` room, which also has Riptide's `mapboss` + `%area%-bosspack` | not created (room objects only for listed quests) | create; drop BIT 1577 once while needed |
| Riptide | MV 1146 | `medicine_boat_*` mapboss | placed if the room is fitted | not required |

### Gates
None (`2_6_3` needs `a5q6-CompletedBoss`).

### Rewards
Offer 43 `a6q5` (taken `a6q5-QuestRewardTaken`), any class: rarity 2 L45 Rustic Sash, Chain Belt, Leather Belt, Heavy
Belt, Turquoise/Agate/Citrine Amulet; rarity 1 L45 Rustic Sash, Chain Belt, Leather Belt, Heavy Belt, Cloth Belt,
Studded Belt. None buildable today (magic/rare at item level 45).

### Quest log check
4 `QuestReceived` → Find Bestel's Manuscript · 3 `HaveItem` → Deliver it · 2 `DeliveredItem` → Talk to Bestel · 1
`QuestRewardSeen` · 0 `QuestRewardTaken` → complete.

### Driver steps (draft)
```python
QUEST = "a6q5"
DONE = "a6q5-QuestRewardTaken"
STEPS = [
    {"verb": "talk", "npc": "Bestel", "option": "Bestel's Epic", "skip_if": "a6q5-QuestReceived"},
    {"verb": "goto", "area": "2_6_3", "skip_if": "a6q5-HaveItem"},
    {"verb": "interact", "target": "BestelChest", "skip_if": "a6q5-HaveItem"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act6/BestelsManuscript", "skip_if": "a6q5-DeliveredItem"},
    {"verb": "talk", "npc": "Bestel", "option": "Bestel's Epic", "skip_if": "a6q5-DeliveredItem"},
    {"verb": "reward", "npc": "Bestel", "option": "Bestel's Epic Reward", "skip_if": "a6q5-QuestRewardTaken"},
]
```

### Risks
- NPCTalk 934 has no `Script`: taking the manuscript on hand-in is the item's `remove_flag` (`a6q5-DeliveredItem`),
  like a1q5's Nessa; the server must remove the item when the flag is set.
- Offer 43 not buildable (same as a6q2).

## a6q6 "The Puppet Mistress" (Quest row 75, side)
**Start:** Tarkleigh, NPCTalk 910 (needs `a6q2-KilledBoss`). **Finish:** Tarkleigh, NPCTalk 912 (Book of Skill).
**DONE flag (driver):** `a6q6-UsedRewardBook` (QSR 15, +1 passive). **Prerequisites:** `a6q2-KilledBoss`.

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Task | `2_6_town` | NPCTalk 910 "The Puppet Mistress" | `a6q6-QuestReceived` | Sin 966 gossip |
| 2 | Find Ryslatha | `2_6_11` The Wetlands | see MV 1610 | `a6q6-SeenBoss` | |
| 3 | Kill Ryslatha | `2_6_11` | kill MV 1610 `Metadata/Monsters/InsectSpawner/InsectSpawnerParasiteBoss` | `a6q6-KilledBoss` | Pantheon: unlocks minor god Ryslatha |
| 4 | Sin | `2_6_11` | spawn `SinRyslatha`; NPCTalk 1000 "Ryslatha" (Pantheon script as 993; fallback SinTown 968) | `SinRyslathaReward` | |
| 5 | Report | `2_6_town` | NPCTalk 911 | `a6q6-Congratulated` | |
| 6 | Reward | `2_6_town` | NPCTalk 912 offer 44 | `a6q6-QuestRewardSeen`; `HaveRewardBook` | |
| 7 | Read | anywhere | use `Book-a6q6` (BIT 1581) | `a6q6-UsedRewardBook` | QSR 15 |

### Objects, NPCs and bosses
| What | Type / MV | Where | Server today | Needed |
|---|---|---|---|---|
| Ryslatha | MV 1610 (boss bar) | `bossfight.arm` mapboss (163, 163); `RyslathaFightManager` (201, 201), nests/caustic daemons as named points | placed (spawnable) | kill trigger; the arena script is skipped by `/kill` |
| Sin | `SinRyslatha` (`unblock_quest_flag = "a6q6minusQuestRewardSeen"`) | spawned | not created | spawn + script |

### Gates
None (`2_6_11` needs `a6q2-KilledBoss`).

### Rewards
Offer 44 `a6q6` (no taken flag): Book of Skill L1 `Book-a6q6` (QuestRewards 1004). QSR 15: `a6q6-UsedRewardBook`, +1
passive.

### Quest log check
4 `QuestReceived` → Defeat the Puppet Mistress · 3 `SeenBoss` · 2 `KilledBoss` → Talk to Tarkleigh · 1
`HaveRewardBook` · 0 `UsedRewardBook` → complete.

### Driver steps (draft)
```python
QUEST = "a6q6"
DONE = "a6q6-UsedRewardBook"
STEPS = [
    {"verb": "talk", "npc": "Tarkleigh", "option": "The Puppet Mistress", "skip_if": "a6q6-QuestReceived"},
    {"verb": "goto", "area": "2_6_11", "skip_if": "a6q6-KilledBoss"},
    {"verb": "kill", "target": "Metadata/Monsters/InsectSpawner/InsectSpawnerParasiteBoss", "skip_if": "a6q6-KilledBoss"},
    {"verb": "wait_flag", "flag": "a6q6-KilledBoss"},
    {"verb": "talk", "npc": "Sin", "option": "Ryslatha", "skip_if": "SinRyslathaReward"},
    {"verb": "talk", "npc": "Tarkleigh", "option": "The Puppet Mistress", "skip_if": "a6q6-Congratulated"},
    {"verb": "reward", "npc": "Tarkleigh", "option": "Puppet Mistress Reward", "skip_if": "a6q6-HaveRewardBook"},
    {"verb": "use_item", "item": "Book-a6q6", "skip_if": "a6q6-UsedRewardBook"},  # verb not in CONTRACT yet
    {"verb": "wait_flag", "flag": "a6q6-UsedRewardBook"},
]
```

### Risks
- In the real game Ryslatha only appears after the arena's nests/waves (`RyslathaFightManager`); our server places her
  directly at the mapboss: fine for the driver.
- Book offer / `use_flag` gaps as a6q3.

## a6q7 "The Cloven One" (Quest row 76, side)
**Start:** Bestel, NPCTalk 928 "Abberath" (needs `a6q2-KilledBoss`). **Finish:** Bestel, NPCTalk 930 (Book of Skill).
**DONE flag (driver):** `a6q7-UsedRewardBook` (QSR 17, +1 passive). **Prerequisites:** `a6q2-KilledBoss`.

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Task | `2_6_town` | NPCTalk 928 | `a6q7-QuestReceived` | Sin 963 gossip |
| 2 | Find Abberath | `2_6_8` Prisoner's Gate | see MV 6008 | `a6q7-SeenBoss` | |
| 3 | Phase 1 | `2_6_8` | Abberath (MV 6008) fights in `bossphase1.arm`, then leaves (`AbberathJumpOut`), doors `DoorAbberathPhase1/2/Exterior` change | — | scripted |
| 4 | Kill Abberath | `2_6_8` | kill MV 6012 `Metadata/Monsters/Abberath/Abberath2` (phase 2, `bossphase2.arm`) | `a6q7-KilledBoss` | which variety sets the flag is inferred (phase 2 = final); Pantheon: unlocks minor god Abberath |
| 5 | Sin | `2_6_8` | spawn `SinAbberath`; NPCTalk 997 "Abberath" (Pantheon script; fallback SinTown 965) | `SinAberrathReward` (spelling of the flag) | |
| 6 | Report | `2_6_town` | NPCTalk 929 | `a6q7-Congratulated` | |
| 7 | Reward | `2_6_town` | NPCTalk 930 offer 45 | `a6q7-QuestRewardSeen`; `HaveRewardBook` | |
| 8 | Read | anywhere | use `Book-a6q7` (BIT 1582) | `a6q7-UsedRewardBook` | QSR 17 |

### Objects, NPCs and bosses
| What | Type / MV | Where | Server today | Needed |
|---|---|---|---|---|
| Abberath phase 1 | MV 6008 (boss bar) | `Act6/Area8/Rooms/bossphase1.arm` point `mapboss1WG` (323, 167) | WorldAreas boss; room-points has only the `stairs_miniboss*` rooms' `mapboss` → wrong place | place at `mapboss1WG` or simply place MV 6012 |
| Abberath phase 2 | MV 6012 (boss bar) | `bossphase2.arm` `mapboss2WG` (372, 139) | same | kill trigger on 6012 |
| Arena doors / spawners | `DoorAbberathExterior` x3, `DoorAbberathPhase1`, `DoorAbberathPhase2` (TriggerableBlockage), `BossGoatSpawner*`, `MeteorShower*` | `bossphase1/2.arm` | not created | not needed for `/kill` (Phase1 door `start_blocked = true`: check that the arena is reachable without it) |
| Sin | `SinAbberath` (`unblock_quest_flag = "a6q7minusQuestRewardSeen"`) | spawned | not created | spawn + script |

### Gates
None (`2_6_8` needs `a6q2-KilledBoss`).

### Rewards
Offer 45 `a6q7` (no taken flag): Book of Skill L1 `Book-a6q7` (QuestRewards 1005). QSR 17: `a6q7-UsedRewardBook`, +1
passive.

### Quest log check
4 `QuestReceived` → Defeat Abberath · 3 `SeenBoss` · 2 `KilledBoss` → Talk to Bestel · 1 `HaveRewardBook` · 0
`UsedRewardBook` → complete.

### Driver steps (draft)
```python
QUEST = "a6q7"
DONE = "a6q7-UsedRewardBook"
STEPS = [
    {"verb": "talk", "npc": "Bestel", "option": "Abberath", "skip_if": "a6q7-QuestReceived"},
    {"verb": "goto", "area": "2_6_8", "skip_if": "a6q7-KilledBoss"},
    {"verb": "kill", "target": "Metadata/Monsters/Abberath/Abberath2", "skip_if": "a6q7-KilledBoss"},
    {"verb": "wait_flag", "flag": "a6q7-KilledBoss"},
    {"verb": "talk", "npc": "Sin", "option": "Abberath", "skip_if": "SinAberrathReward"},
    {"verb": "talk", "npc": "Bestel", "option": "Abberath", "skip_if": "a6q7-Congratulated"},
    {"verb": "reward", "npc": "Bestel", "option": "Abberath Reward", "skip_if": "a6q7-HaveRewardBook"},
    {"verb": "use_item", "item": "Book-a6q7", "skip_if": "a6q7-UsedRewardBook"},  # verb not in CONTRACT yet
    {"verb": "wait_flag", "flag": "a6q7-UsedRewardBook"},
]
```

### Risks
- Two-variety scripted boss; the server picks one WorldAreas boss for a `mapboss` point today: it must place MV 6012
  (or both) and fire the kill trigger on the right one.
- Named points `mapboss1WG`/`mapboss2WG` are not `mapboss`.

## Pantheon flags set in act 6 (for the coordinator)
| Kill | Unlock flag (PantheonPanelLayout) | Sin NPC (spawned) | NPCTalk | Reward flag | Town fallback |
|---|---|---|---|---|---|
| Tukohama (MV 5783) | `a6q3-KilledBoss` (minor Tukohama; also gates every "Open Pantheon" row) | `SinTukohama` | 993 (Script opens PantheonScreen, session flag `CanChangePantheon`) | `SinTukohamaReward` | SinTown 962 |
| Abberath (MV 6012) | `a6q7-KilledBoss` (minor Abberath) | `SinAbberath` | 997 | `SinAberrathReward` | SinTown 965 |
| Ryslatha (MV 1610) | `a6q6-KilledBoss` (minor Ryslatha) | `SinRyslatha` | 1000 | `SinRyslathaReward` | SinTown 968 |
| Brine King (MV 5493) | `a6q1-KilledBoss` (major TheBrineKing) | `SinBrineKing` | 1003 | `SinBrineKingReward` | SinTown 971 |
| Shavronne (MV 5488) | — (not a god) | `SinShavronne` | 995 (`FinishNPCChat` only) | `SinShavronneReward` | none |

SinTown "Open Pantheon" rows 972/1109/1203/1349/1471 (cat 38, need `a6q3-KilledBoss`, not `HardMode`); "Unlock Power"
rows 973–992 (Script `UnlockPantheonPowerFromItem`, captured-soul items BIT 3613–3632) are map-era upgrades.

## Implementation summary
| Quest | Main/side | Features | Risk | Est. size |
|---|---|---|---|---|
| a6q4 Fallen from Grace | side | NPC talk, area-cleared trigger, book offer (no taken flag), Book of Regrets use_flag | med: new "area cleared" trigger; `2_6_1` seed | S |
| a6q5 Bestel's Epic | side | room-object chest→item drop, item pickup flag, NPC hand-in, reward offer (magic/rare belts) | med: reward not buildable | S |
| a6q3 The Father of War | main | named-point boss placement (`mapbossN`), monster quest drop (eye), item pickup flag, object use (server-only door) + 2 Transitionable transitions, client blockers/QuestBarriers, area gate, boss kill, Sin NPC spawn + Pantheon script, book | high: Queen not placed, eye drop missing, doors | M |
| a6q2 Essence of Umbra | main | two-boss kill (arena points `mapboss1/2`), Sin NPC spawn, reward offer (magic/rare helmets) | med: placement, reward not buildable | S |
| a6q7 The Cloven One | side | two-phase boss (place MV 6012), kill trigger, Sin NPC + Pantheon script, book | med: variety/placement | S |
| a6q6 The Puppet Mistress | side | boss kill, Sin NPC + Pantheon script, book | low: boss placed today | S |
| a6q1 The Brine King | main | 3 out-of-town NPC proximity triggers + transitions, named-point chest→item drop, beacon (pushable carts with Movement, 3-state objects, object use with item), NPC transport scripts (1006, 1010), transitioned Part2 exits, scripted/arena boss, Sin NPC + Pantheon, act 6→7 transition | high: beacon carts (new serialisation), transport scripts, Merveil exit | L |
