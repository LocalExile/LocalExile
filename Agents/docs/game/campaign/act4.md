# Act 4 quests

Act 4 is played from one town, Highgate (`1_4_town`). The player arrives from Act 3 through The Aqueduct (`1_4_1`).
The client lets the player into every Act 4 area before the Mines once `a3q10-KilledDominus` is set (EndQuestFlags of
`1_4_town`, `1_4_1`, `1_4_2`). Coming from `1_3_18_2` The Upper Sceptre of God, Lady Dialla waits on The Aqueduct.
The main line is a4q1 "The Eternal Nightmare", which wraps the other main quests:
- a4q2 "Breaking the Seal": kill Voll in The Dried Lake and use Deshret's Banner on the seal in town. This opens the
  Mines (`a4q2-UsedBanner` gates every Mines/Dream/Arena area).
- a4q3 "The King of Fury" (Kaom, The Eye of Fury) and a4q4 "The King of Desire" (Daresso, The Eye of Desire): bring
  both Eyes to Dialla at the Rapture Device in The Crystal Veins. That opens The Belly of the Beast
  (`a4q1-RaptureActivated`).
- Kill Piety in The Belly of the Beast Level 2. Her talk starts a4q5 "Corpus Malachus" and opens The Harvest
  (`a4q5-QuestReceived`).
- In The Harvest, kill Doedre, Maligaro and Shavronne for Malachai's three organs and give them to Piety. She opens the
  Black Core. Killing Malachai sets `a4q1-QuestComplete`, which gates The Ascent (`1_4_7`).

The side quest is a4q6 "An Indomitable Spirit": free Deshret's spirit in The Mines Level 2 for a Book of Skill.
The act ends with row 62 `a5q1a` "Return to Oriath". Climb The Ascent, pull the Resonator lever (`a5q1-UsedResonator`)
and click the Oriath Portal (an area transition object) into `1_5_1` The Slave Pens. There is no boat and no Lilly
Roth in this transition.
Sources: client tables (`data/tables`: Quest, QuestStates, QuestFlags, NPCTalk, QuestRewardOffers, QuestStaticRewards,
WorldAreas, MonsterVarieties, CharacterStartQuestState), the generated layouts (`lg.ps1 questobjects`, seed 1),
`.arm` rooms read with `lg.ps1 cat`, unpacked `.ot`/`.it` game text, the wiki (Fandom mirror, 2026-10-30), poedb.

## Status (implemented, played live 2026-10-30)

Every quest of the act runs on the engine (`Modules/Core/World/QuestObjects/Acts/Act4.cs`,
[12](../../protocol/12-quests.md) §13) and was played unattended by the campaign runner (driver steps `a4q2`, `a4q6`,
`a4q3`, `a4q4`, `a4q5`, `a4q1`, `a5q1a`: the main line split where its quests meet) from `/quest_preset Act3Complete`
to The Slave Pens with every `Act4Complete` flag, before the game moved into modules; on the modules the oracle
(`QuestEngineTests`) plays it after Acts 1–3 to `Act4Complete`. The sections after this one are the research done
before the act was built. What differs from them:
- Every boss is placed by the bosses workstream (numbered points, Malachai's two forms from the start); Dialla's four
  places, Piety in the Belly (where she falls) and in The Harvest, the seal, Deshret's spirit, the Rapture Device and
  the Resonator (from the server's own rooms: `ascent_summit_01.arm` is not in room-points) are created.
- `a4q1-KillGuardians` is not set (log rows 5–12): no data sets or clears it and the "complete" rows need it missing.
- The dream portals (Crystal Veins ↔ Kaom's and Daresso's Dreams) and the Oriath Portal (`a5q1-UsedResonator`) are
  room-line exits of the generated layouts; the whole act is played through exits and waypoints. Not created: the beast
  wall and the arena machinery (cosmetic).
- `/quest_preset Act3Complete` now completes the preset's quests, which brings `a3q10-KilledDominus` (the gate of
  every Act 4 area).

## Areas

Bosses are listed with MonsterVarieties Id and row. "NPC" means MonsterCatalog does not support the NPC component.
"pt" is the room point the boss uses in its room's `.arm`. The server places a unique boss only at a point named
exactly `mapboss`.

| WorldAreas Id | Name | Lvl | WP | Gateway | EndQuestFlags | Unique bosses (MV Id, spawnable?) | Quest objects / NPCs (type, tile/room) |
|---|---|---|---|---|---|---|---|
| `1_4_1` | The Aqueduct | 33 | yes | yes | `a3q10-KilledDominus` | `VultureParasite/VultureParasiteBoss` (4014, yes) | `NPC/Act4/DiallaAqueduct` (room `Aqueduct/.../hub_1.arm` (255,116)), `QuestObjects/Act4/GiantShadow` (client_only) |
| `1_4_town` | Highgate | 40 | yes | yes | `a3q10-KilledDominus` | - | NPCs in `town.arm`: `DiallaTown`, `DiallaTownWithered`, `Kira`, `Oyun`, `PetarusVanja`, `Tasuni`; `QuestObjects/Act4/MineEntranceSeal` (room (134,329)), `MineEntranceSealBlocker` x6 (room (131,310)), `MineEntranceTransition` (tile `mountian_town_v01_01.tdt` record `..._area_transition3`), `AscentTransition` (same tile, `..._area_transition4`) |
| `1_4_2` | The Dried Lake | 34 | no | yes | `a3q10-KilledDominus` | `Voll/VollBoss` (4095, yes; pt `mapboss1` in `DriedLake/Rooms/Unique/boss_2..6.arm`, not `mapboss`), `SkeletonSoldier/SkeletonSoldierRangedBoss` (4071, yes) | `NPC/Info/VollBookStand` (lore) |
| `1_4_3_1` | The Mines Level 1 | 34 | no | yes | `a4q2-UsedBanner` | `Miner/MinerHammerBoss` (4158, yes) | `Mines/Objects/BeastShake1` (cosmetic) |
| `1_4_3_2` | The Mines Level 2 | 35 | no | yes | `a4q2-UsedBanner` | `Handfinger/HandfingerBoss` (4189), `MineBat/MineBatBoss` (4143), both yes | `QuestObjects/Act4/DeshretSpirit`, `DeshretHand`, `DeshretAura` (room `Mines/Rooms/Unique/deshret_quest.arm` (106,~138)) |
| `1_4_3_3` | The Crystal Veins | 36 | yes | yes | `a4q2-UsedBanner` | `Miner/MinerLanternBoss` (4179, yes) | room `Mines/Rooms/Unique/rapture_1.arm`: `NPC/Act4/DiallaRapture` (189,313), `QuestObjects/Act4/RaptureDevice` (204,314), `Mines/Objects/DreamPortal` x2 (250,323)/(150,312); tile `mine_beastwall_v01_01.tdt`: `QuestObjects/Act4/RaptureTransition`, `BeastSkinDoor`, `BeastSkinDoorFX`, `BeastSkinDoorExplosion` |
| `1_4_4_1` | Kaom's Dream | 37 | no | yes | `a4q2-UsedBanner` | `Totems/PuiTotemBoss` (4249), `Taniwha/TaniwhaBoss` (4262), both yes | `Mines/Objects/DreamPortal` (room `NewKaom1/Unique/entrance1_01.arm` (214,150)) |
| `1_4_4_3` | Kaom's Stronghold | 38 | yes | yes | `a4q2-UsedBanner` | `Kaom/KaomBoss` (4270, NOT: NPC; boss bar; pt `mapboss` in `Plateau/Rooms/boss_epic_2a.arm` (132,146)) | in `boss_epic_2a.arm`: `Kaom/ArenaMiddle`, `Kaom/Volcano/kaom_volcano` x3, `KaomBossArcherSpawner` x9, `KaomBossSkullSpawnerHigh/Low/Medium` |
| `1_4_5_1` | Daresso's Dream | 37 | no | yes | `a4q2-UsedBanner` | `Viking/Viking` (4305), `Cockerel/CockerelBowBoss` (4294), `Pitbull/PitbullBoss` (4281), all yes | tile records: `Arena/Pit/Objects/PitGate` x7, `PitGateTransition` x7, `PitSpawner` x54, `PitSpawnerGate` x18; `DreamPortal` (room `entrance_1_1.arm`) |
| `1_4_5_2` | The Grand Arena | 38 | yes | yes | `a4q2-UsedBanner` | `Daresso/DaressoBoss` (4334, NOT: NPC; boss bar; pt `mapboss1` in `Colosseum/Rooms/Unique/boss_1.arm`, room not in room-points), `GladiatorBosses/GladiatorBossSupport` (4688), `...Leader` (4690), `...Elite` (4692) (yes; pts `mapboss2..4` in `Colosseum/gladiators.arm`) | `AreaTransitionToggleableReverse` x4 (tile `colosseum_wall_door_v01_02.tdt`), `ColosseumDoubleDoors*`, `ColosseumDoubleDoorsDaresso`, `ColosseumTrapDoorSpawner*`, statue spawners `Statue/*/Spawner_*`, `Daresso/ArenaMiddle`, `DaressoArenaTalk2` |
| `1_4_6_1` | The Belly of the Beast Level 1 | 38 | no | yes | `a4q1-RaptureActivated` | `Revenant/RevenantBoss` (4679, yes) | `BellyEggSpawner`, grass (cosmetic) |
| `1_4_6_2` | The Belly of the Beast Level 2 | 39 | no | yes | `a4q1-RaptureActivated` | `Axis/PietyBeastBoss` (1425, NOT: NPC; boss bar; pt `mapboss` (184,266) in `Belly/Rooms/Unique/boss_piety_1.arm`) | `Belly/Objects/BellyHarvestBlocker` (room `boss_piety_1.arm` (125,295)), `BellyArenaDoor`, `BellyArenaTransition` (tile `mapmarkeronly_bellyspikes_v01_01.tdt`), `BellyArseDoor` x3 (tile `belly_buttdoor_v02_01.tdt`) |
| `1_4_6_3` | The Harvest | 40 | yes | yes | `a4q5-QuestReceived` | `Malachai/MalachaiBoss` (4407), `Malachai/MalachaiBoss2` (4409), `Doedre/Doedre` (4391), `Maligaro/Maligaro` (4382), `Shavronne/Shavronne` (4400): all NOT (NPC); pts `mapboss1`..`mapboss5` (see a4q5) | `NPC/Act4/PietyHarvest` (room `core_entrance_1.arm` (135,123)); tile `belly_taster_entrance_v01_01.tdt`: `QuestObjects/Act4/CoreMouth`, `CoreTransition`; `MalachaiGroundHole` (tile `malachai_fight_v01_01.tdt`), `MalachaiDepthsReturn` (room `boss_2.arm` (325,233)), `BellyArenaDoor` x3, `BellyArenaTransition` x3, `BellyArseDoor` x2, guardian arena markers `Doedre/DoedreArenaMiddle`, `Maligaro/MaligaroArenaMiddle`, `Shavronne/ShavronneArenaMiddle`, `Malachai/ArenaMiddle` |
| `1_4_7` | The Ascent | 40 | no | yes | `a4q1-QuestComplete` | `Vendigo/VendigoBoss` (5504, yes) | room `Act4/Area7/Rooms/Unique/ascent_summit_01.arm`: `Terrain/Act4/Area7/Objects/PortalDevice` (214,154), `PortalDeviceLever` (220,128), `PortalDeviceTransition` (202,130), `NPC/Info/ResonatorInstructions`, `CameraZoom/AscentTeleporter` |
| `1_5_1` | The Slave Pens (Act 5) | 41 | - | - | `a5q1-UsedResonator` | - | destination of the Oriath Portal |

Room-points coverage (`data/room-points.json`): `hub_1.arm`, `town.arm`, `boss_epic_2a.arm`, `boss_piety_1.arm` and
`core_entrance_1.arm` are fitted. These rooms are not fitted, so their objects and boss points have no position today:
`rapture_1.arm`, `deshret_quest.arm`, the Dried Lake `boss_N.arm` (only the `%area%-vollpack` points were kept), the
Colosseum `boss_1.arm`/`gladiators.arm`, the Harvest `organ_1..3.arm`/`boss_1.arm`/`boss_2.arm` and
`ascent_summit_01.arm`.

## a4q1 "The Eternal Nightmare" (Quest row 34, main)
**Start:** entering The Aqueduct (`Visited-4_1`, QS Order 28 "Talk to Dialla").
**Finish:** kill Malachai (`a4q1-QuestComplete`), then talk to Tasuni (`TasuniOnMalachaiDead`).
**DONE flag (driver):** `a4q1-QuestComplete`. QS Order 0 needs `TasuniOnMalachaiDead` with `a4q1-KillGuardians`
missing, and the presets show `a4q1-KillGuardians` staying set (see Quest log check), so the Order 0 log line is not a
reliable DONE flag.
The Eyes reward (offer 29) belongs to this quest row: take it from DiallaTownWithered.
**Prerequisites:** `a3q10-KilledDominus` (area gate). Dialla's row 509 needs `a3q8-DiallaCongratulated` (Act 3;
DiallaAqueduct row 508 "Dominus" also sets it).

### Sequence
| # | Real-game step | Area (Id, name) | Event | Flags set | Notes / source |
|---|---|---|---|---|---|
| 1 | Enter The Aqueduct | `1_4_1` The Aqueduct | enter area | `Visited-4_1` | StartQuestFlags (Gateway sets it) |
| 2 | Talk to Dialla on the Aqueduct (optional) | `1_4_1` | talk `DiallaAqueduct`, NPCTalk 509 idx 1 "Highgate" (needs `a3q8-DiallaCongratulated`) | `a4q1-QuestReceived` (QF4+QF5) | out-of-town NPC, not created today. Row 707 (`DiallaTower`, Act 3) sets the same flag. Only the log uses it |
| 3 | Enter Highgate | `1_4_town` | enter area | `Visited-4_town` | |
| 4 | Dialla sends you to Oyun | `1_4_town` | talk `DiallaTown`, NPCTalk 511 idx 0 "Oyun" (not `a4q2-QuestReceived`) | `a4q1-OyunQuest` | generic |
| 5 | a4q2 (Oyun, Voll, banner, seal) | | see a4q2 | `a4q2-UsedBanner` | |
| 6 | Dialla: find the Rapture Device | `1_4_town` | talk `DiallaTown`, NPCTalk 512 idx 1 "Malachai" (needs `a4q2-UsedBanner`) | `a4q1-FindRapture` | generic |
| 7 | Reach Dialla at the Rapture Device | `1_4_3_3` The Crystal Veins | come near `DiallaRapture` | `a4q1-SeenRaptureDialla` | no NPCTalk sets it: needs a proximity trigger (as `OnMonsterSeen`, but for an NPC) |
| 8 | Dialla asks for the two Eyes | `1_4_3_3` | talk `DiallaRapture`, NPCTalk 526 idx 0 "The Rapture" (not `a4q3-DeliveredGem`, `a4q4-DeliveredGem`) | `a4q1-GemQuestsReceived` | picking up either Eye also sets it (`extra_flag` of both `.it`) |
| 9 | a4q3 + a4q4 (Kaom, Daresso, deliver the Eyes) | | see a4q3/a4q4 | `a4q3-DeliveredGem`, `a4q4-DeliveredGem` | |
| 10 | Activate the Rapture Device | `1_4_3_3` | talk `DiallaRapture`, NPCTalk 534 idx 8 "The Rapture" (needs both DeliveredGem, not `a4q1-RaptureActivated`) | `a4q1-RaptureActivated` | Script (needs code): `DirtyTransition()` on Dialla (state 1 to 2), `FinishNPCChat()`, `DirtyTransition` on every `BeastSkinDoor`, `BeastSkinDoorFX`, `BeastSkinDoorExplosion`, `RaptureDevice`. Opens `1_4_6_1`/`1_4_6_2` (EndQuestFlags) |
| 11 | Talk to the withered Dialla | `1_4_3_3` | talk `DiallaRapture`, NPCTalk 535 idx 9 "The Rapture" (needs `a4q1-RaptureActivated`) | `a4q1-DiallaOnTransformed` | Script: `DirtyTransition` on `QuestObjects/Act4/RaptureTransition` (the wound into the Belly) |
| 12 | Enter the Belly | `1_4_6_1` | enter via `RaptureTransition` | `Visited-4_6_1` | |
| 13 | See Piety | `1_4_6_2` | seen MV `Metadata/Monsters/Axis/PietyBeastBoss` | `a4q1-SeenPiety` | `OnMonsterSeen` |
| 14 | Kill Piety | `1_4_6_2` | kill MV `Metadata/Monsters/Axis/PietyBeastBoss` (1425) | `a4q1-KilledPiety` | boss not spawnable (NPC component). Piety then becomes `NPC/Act4/PietyBelly` (not created) |
| 15 | Piety's parley | `1_4_6_2` | `PietyBelly` NPCTalk 620 idx 0 "Nightmare", then 621 idx 1 "Malachai" | `a4q5-PietyThanks`, `a4q5-QuestReceived` | see a4q5 |
| 16 | a4q5 (organs, guardians) | `1_4_6_3` The Harvest | see a4q5 | `a4q1-KillGuardians`, `a4q1-KilledDoedre`, `a4q1-KilledMaligaro`, `a4q1-KilledShavronne`, `a4q5-QuestComplete` | |
| 17 | Enter the Black Core, see Malachai | `1_4_6_3` | `CoreTransition` (opened by Piety row 627); seen `MalachaiBoss` | `a4q1-SeenMalachai` | `PietyHarvest.ot` has `unblock_quest_flag = a4q1-SeenMalachai` |
| 18 | Kill Malachai (two stages, three hearts) | `1_4_6_3` | kill MV `Metadata/Monsters/Malachai/MalachaiBoss2` (4409) | `a4q1-QuestComplete` | scripted fight, see Objects. Assumed: the final death of stage 2 sets the flag (no table names the trigger) |
| 19 | Back to town; Tasuni | `1_4_town` | talk `Tasuni`, NPCTalk 615 idx 23 "Malachai" (needs `a4q1-QuestComplete`) | `TasuniOnMalachaiDead` | cat 4 (gossip) but it is the a4q1 Order 0 flag and the `a5q1a` start |
| 20 | Withered Dialla opens the Ascent | `1_4_town` | talk `DiallaTownWithered`, NPCTalk 521 idx 4 "Malachai" (needs `a4q1-QuestComplete`) | `DiallaOnMalachaiDead` | Script: `DirtyTransition` on `QuestObjects/Act4/AscentTransition` (the town exit to `1_4_7`) |
| 21 | Rapture reward | `1_4_town` | `DiallaTownWithered` NPCTalk 523 idx 6 "The Rapture Device" (not `a4q3q4-QuestRewardTaken`), then 524 idx 7 "Rapture Reward" (offer 29) | `a4q3q4-Congratulated`, `a4q3q4-QuestRewardSeen`, `a4q3q4-QuestRewardTaken` | rewards section |

### Objects, NPCs and bosses
| What | Type / MV Id / BaseItemTypes Id | Where it comes from | Server today | Needed |
|---|---|---|---|---|
| Dialla on the Aqueduct | `Metadata/NPC/Act4/DiallaAqueduct` (Transitionable) | room `hub_1.arm` (255,116), fitted | not created (out of town) | optional: create it with its NPCTalk (rows 508-510) |
| Dialla in town | `NPC/Act4/DiallaTown`, later `NPC/Act4/DiallaTownWithered` | `town.arm` (253,555) / (250,553) | only the first stage is placed | swap to `DiallaTownWithered` from `a4q1-RaptureActivated` on. Rows 517-525 (offer 29, Ascent script) live only on the withered variant |
| Dialla at the Rapture | `NPC/Act4/DiallaRapture` (Transitionable `num_states 3`, Timer) | room `rapture_1.arm` (189,313), not fitted | not created | create it from a room fit; NPCTalk 526-538; row 534 steps her to state 2 |
| Rapture Device | `QuestObjects/Act4/RaptureDevice` (Positioned, BaseEvents, Animated, Transitionable) | room `rapture_1.arm` (204,314) | not created | create; transition 1 to 2 on row 534 (cosmetic) |
| Beast skin wall | `Mines/Objects/BeastSkinDoor`, `BeastSkinDoorFX` (Timer, unread), `BeastSkinDoorExplosion` | tile `mine_beastwall_v01_01.tdt` | not created | transitions on row 534 (cosmetic) |
| Wound into the Belly | `QuestObjects/Act4/RaptureTransition` (AreaTransition + Transitionable; MinimapIcon `show_flag a4q1-RaptureActivated`) | tile `mine_beastwall_v01_01.tdt` record `crystals_openAnimation_v01_01_area_transition` | created as a plain AreaTransition if taken from tile data (unverified) | Gateway: refuse without `a4q1-RaptureActivated` (`FlagToEnter 1_4_3_3 -> 1_4_6_1`), create it then. The client gate is the Belly's EndQuestFlags |
| Piety, the Abomination | MV `Metadata/Monsters/Axis/PietyBeastBoss` (1425; BossLifeBar, Transitionable intro, NPC) | WorldAreas `1_4_6_2` Bosses, `mapboss` of `boss_piety_1.arm` | NOT spawnable (NPC component) | NPC component serialisation; intro Transitionable; on death spawn `PietyBelly` |
| Piety (parley) | `NPC/Act4/PietyBelly` (Transitionable, states 1/2) | not in any room: appears where the boss dies (inferred) | not created | create at the boss's death spot, state 1 then 2 |
| Malachai and the Black Core | see a4q5 | | | |
| Tasuni | `NPC/Act4/Tasuni` | `town.arm` (311,407) | placed | generic row 615 |
| Ascent exit | `QuestObjects/Act4/AscentTransition` (AreaTransition + Transitionable, `create_untransitioned` hides it) | tile `mountian_town_v01_01.tdt` record `..._area_transition4` | quest-gated transition not created | create it (state 2) once `a4q1-QuestComplete` is set, or on row 521. `FlagToEnter 1_4_town -> 1_4_7` = `a4q1-QuestComplete` (also the client's EndQuestFlags) |

### Gates
- `1_4_6_1`, `1_4_6_2` EndQuestFlags `a4q1-RaptureActivated`. `1_4_7` EndQuestFlags `a4q1-QuestComplete`.
- Server checks: `FlagToEnter(1_4_3_3, 1_4_6_1)` = `a4q1-RaptureActivated`; `FlagToEnter(1_4_town, 1_4_7)` =
  `a4q1-QuestComplete`.
- No `unblock_quest_flag` blockers of its own. `PietyHarvest.ot` has `unblock_quest_flag = a4q1-SeenMalachai` on an
  NPC (Positioned), so after it is set her blocking stops (client side).

### Rewards
- Offer 29 `a4q1` (taken flag `a4q3q4-QuestRewardTaken`, not col8). Given by `DiallaTownWithered` NPCTalk 524 idx 7
  "Rapture Reward" (needs `a4q3q4-Congratulated`, not `a4q3q4-QuestRewardTaken`; QF4 `a4q3q4-QuestRewardSeen`).
  `a4q3q4-Congratulated` comes from row 523 idx 6 "The Rapture Device".
  Items: per class level 1 support gems (e.g. Witch: Spell Echo, Multistrike, Greater Multiple Projectiles, Chain,
  Increased Area of Effect, Decay, Immolate, Bonechill, Hex Bloom, Unleash), plus any class: Ruby, Sapphire, Topaz and
  Amethyst Flask at level 38, rarity 0. All can be built today (L1 gems, normal flasks).
- Offer 21 (Act 3 `a3q4` Ribbon Spool reward) is also on `DiallaAqueduct` 510, `DiallaTown` 516, `DiallaRapture` 538 and
  `DiallaTownWithered` 525: the Act 3 file owns it.
- Trap rows: `DiallaRapture` 527, 528 and 529 ("The Rapture", needing the Eyes in different held/delivered combinations)
  set `a4q3q4-QuestRewardTaken` as QF4 with no offer. Choosing one forfeits offer 29 and does not deliver the Eyes, so
  the driver must never pick them (see Risks).

### Quest log check
- 28 → `Visited-4_1` → Talk to Dialla; 27 → `Visited-4_1`,`a4q1-QuestReceived` → Go to Highgate
- 26 → `Visited-4_town` → Talk to Dialla; 25 → `a4q1-OyunQuest` → Talk to Oyun
- 24 → `a4q2-QuestReceived` → find a way into the mountain; 23 → `a4q2-QuestRewardSeen` → Talk to Dialla
- 22 → `a4q1-FindRapture` → Find the Rapture Device; 21 → `a4q1-SeenRaptureDialla` → Talk to Dialla
- 20 → `a4q1-GemQuestsReceived` → Find the Eyes of Fury and Desire
- 19 → `a4q1-RaptureActivated`; 18 → `a4q1-DiallaOnTransformed` → Enter the Belly of the Beast; 17 → `Visited-4_6_1`
- 16 → `a4q1-SeenPiety` → Kill Piety; 15 → `a4q1-KilledPiety` → Talk to Piety
- 14 → `a4q5-QuestReceived` → find a way into the Black Core; 13 → `a4q5-QuestComplete` → Enter the Black Core
- 12 → `a4q1-KillGuardians` → Enter the Black Core; 5..11 → `a4q1-KillGuardians` with some of
  `a4q1-KilledDoedre`/`-KilledMaligaro`/`-KilledShavronne` missing → kill the remaining guardians
- 4 → `a4q1-SeenMalachai` + the three Killed flags → Kill Malachai
- 3 → `a4q1-QuestComplete`,`AfterBanditFight` (pin area `1_4_6_3`) → Talk to Tasuni; 2 → `a4q1-QuestComplete`, missing
  `a4q1-KillGuardians` → Talk to Tasuni
- 1 → `TasuniOnMalachaiDead`,`AfterBanditFight` (pin `1_4_6_3`); 0 → `TasuniOnMalachaiDead`, missing
  `a4q1-KillGuardians` → Quest Complete
- Note: `ConsoleAscent`/`ConsoleSlavePens` keep `a4q1-KillGuardians` set after the act. With it set and the
  `a4q1-Killed*` flags clear (no preset has them), the lowest matching row is Order 5 ("Kill Malachai's three
  guardians"), not Complete. The real server probably sets `a4q1-KilledDoedre`/`-KilledMaligaro`/`-KilledShavronne`
  on the kills. `AfterBanditFight` is a session flag (see a4q5). Unverified; test the tracker live.

### Driver steps (draft)
```python
STEPS = [
    {"verb": "goto", "area": "1_4_1", "skip_if": "a4q1-QuestComplete"},
    {"verb": "talk", "npc": "DiallaAqueduct", "option": "Highgate", "skip_if": "a4q1-QuestReceived"},  # optional
    {"verb": "goto", "area": "1_4_town", "via": "transition"},
    {"verb": "talk", "npc": "DiallaTown", "option": "Oyun", "skip_if": "a4q1-OyunQuest"},
    # a4q2 runs here (a4q2.py)
    {"verb": "talk", "npc": "DiallaTown", "option": "Malachai", "skip_if": "a4q1-FindRapture"},
    {"verb": "goto", "area": "1_4_3_3", "skip_if": "a4q1-RaptureActivated"},
    {"verb": "tp", "target": "DiallaRapture", "skip_if": "a4q1-SeenRaptureDialla"},
    {"verb": "wait_flag", "flag": "a4q1-SeenRaptureDialla", "timeout": 10, "skip_if": "a4q1-SeenRaptureDialla"},
    {"verb": "talk", "npc": "DiallaRapture", "option": "The Rapture", "index": 0,
     "skip_if": "a4q1-GemQuestsReceived"},
    # a4q3.py and a4q4.py run here (kill Kaom/Daresso, deliver both Eyes)
    {"verb": "talk", "npc": "DiallaRapture", "option": "The Rapture", "index": 8, "skip_if": "a4q1-RaptureActivated"},
    {"verb": "talk", "npc": "DiallaRapture", "option": "The Rapture", "index": 9,
     "skip_if": "a4q1-DiallaOnTransformed"},
    {"verb": "goto", "area": "1_4_6_1", "via": "transition"},
    {"verb": "goto", "area": "1_4_6_2", "via": "transition"},
    {"verb": "kill", "target": "PietyBeastBoss", "skip_if": "a4q1-KilledPiety"},
    # a4q5.py runs here (Piety talk, guardians, organs, Black Core)
    {"verb": "kill", "target": "MalachaiBoss", "skip_if": "a4q1-QuestComplete"},
    {"verb": "kill", "target": "MalachaiBoss2", "skip_if": "a4q1-QuestComplete"},
    {"verb": "wait_flag", "flag": "a4q1-QuestComplete", "timeout": 30},
    {"verb": "talk", "npc": "Tasuni", "option": "Malachai", "skip_if": "TasuniOnMalachaiDead"},
    {"verb": "talk", "npc": "DiallaTownWithered", "option": "Malachai", "skip_if": "DiallaOnMalachaiDead"},
    {"verb": "talk", "npc": "DiallaTownWithered", "option": "The Rapture Device",
     "skip_if": "a4q3q4-Congratulated"},
    {"verb": "reward", "npc": "DiallaTownWithered", "option": "Rapture Reward",
     "pick": ["Spell Echo", "Increased Area of Effect", "Ruby Flask"], "skip_if": "a4q3q4-QuestRewardTaken"},
]
```
`index` (the NPCTalk `__col1`) is a proposed extension of `talk`. Several rows share the text "The Rapture", and
picking a wrong one (527-529) forfeits the reward.

### Risks
- `DiallaTownWithered` is a later town-NPC variant and is not placed. Without it there is no offer 29 and no row 521
  `AscentTransition` script. Highgate needs a flag-driven variant swap: `DiallaTown` until `a4q1-RaptureActivated`,
  `DiallaTownWithered` after (the Act4Complete preset lists both NPCs).
- `DiallaRapture`, `RaptureDevice` and the `DreamPortal`s stand in `rapture_1.arm`, which has no room fit, so no
  positions. The whole act hinges on this NPC (rows 526, 530, 531, 534, 535).
- Row 534's `DirtyTransition` script and Dialla's Transitionable state 2 are new NPC-script work. The flag part is
  generic.
- `PietyBeastBoss` is not spawnable (NPC component), and `PietyBelly` has no placement source.
- Malachai's final death trigger for `a4q1-QuestComplete` is inferred; no table names it.
- `a4q1-SeenRaptureDialla` needs an NPC proximity trigger, not a monster one.

## a4q2 "Breaking the Seal" (Quest row 35, main)
**Start:** Oyun, NPCTalk 540 "The Red Banner". **Finish:** Oyun's reward (offer 30).
**DONE flag (driver):** `a4q2-QuestRewardTaken` (QS Order 0 needs `a4q2-UsedBanner` + `a4q2-QuestRewardTaken`).
**Prerequisites:** `OyunIntroduced` (Oyun NPCTalk 539 idx 0 "Introduction", not `a4q1-QuestComplete`). Row 540 excludes
`a4q2-HaveBanner` and `a4q2-UsedBanner`.

### Sequence
| # | Real-game step | Area (Id, name) | Event | Flags set | Notes / source |
|---|---|---|---|---|---|
| 1 | Meet Oyun | `1_4_town` | talk `Oyun`, NPCTalk 539 idx 0 "Introduction" | `OyunIntroduced` | generic |
| 2 | Oyun asks for Deshret's Banner | `1_4_town` | talk `Oyun`, NPCTalk 540 idx 1 "The Red Banner" | `a4q2-QuestReceived` | generic; Tasuni 603, Kira 565 and Dialla 513/514 have gossip |
| 3 | Find Voll | `1_4_2` The Dried Lake | seen MV `Metadata/Monsters/Voll/VollBoss` | `a4q2-SeenVoll` | `OnMonsterSeen` |
| 4 | Kill Voll | `1_4_2` | kill MV `Metadata/Monsters/Voll/VollBoss` (4095) | `a4q2-KilledVoll` | Voll's room point is `mapboss1`, not `mapboss` |
| 5 | Take the banner | `1_4_2` | Voll drops BaseItemTypes 1561 `Metadata/Items/QuestItems/Act4/RedBanner` "Deshret's Banner" (2x3); pick it up | `a4q2-HaveBanner` (`grant_flag`), `a4q2-QuestReceived` (`extra_flag`) | drop source from the wiki ("acquired after defeating Voll"); QuestItems rows are not decoded. Drop it as Hailrake drops the Medicine Chest (a1q5 pattern) |
| 6 | Show Oyun (optional) | `1_4_town` | `Oyun` NPCTalk 541 idx 2 "The Red Banner" (needs `a4q2-HaveBanner`) | `a4q2-OyunShownBanner` (QF5) | gossip |
| 7 | Use the banner on Deshret's Seal | `1_4_town` | use object `Metadata/QuestObjects/Act4/MineEntranceSeal` with the banner carried | `a4q2-UsedBanner` | needs code: object use takes the banner (`remove_flag a4q2-UsedBanner`), sets the flag, steps the seal's Transitionable 1 to 2 and the `MineEntranceTransition` 1 to 2. The client lifts the 6 `MineEntranceSealBlocker`s |
| 8 | Oyun congratulates | `1_4_town` | `Oyun` NPCTalk 542 idx 3 "The Red Banner" (needs `a4q2-UsedBanner`, not `a4q2-Congratulated`) | `a4q2-Congratulated` | |
| 9 | Reward | `1_4_town` | `Oyun` NPCTalk 543 idx 4 "Red Banner Reward" (offer 30) | `a4q2-QuestRewardSeen`, `a4q2-QuestRewardTaken` | |

### Objects, NPCs and bosses
| What | Type / MV Id / BaseItemTypes Id | Where it comes from | Server today | Needed |
|---|---|---|---|---|
| Voll | MV `Metadata/Monsters/Voll/VollBoss` (4095) | WorldAreas `1_4_2` Bosses; `.arm` point `mapboss1` in `DriedLake/Rooms/Unique/boss_2..6.arm` | spawnable, but NOT placed: no `mapboss` point (room-points kept only `%area%-vollpack`) | place unique bosses at `mapbossN` too (N = index in WorldAreas Bosses), or keep `mapboss1` in room-points |
| Deshret's Banner | BaseItemTypes 1561 `Metadata/Items/QuestItems/Act4/RedBanner` | dropped by Voll | no drop | kill → drop (a1q5 pattern); flag sync from `.it` (`grant_flag`, `remove_flag`, `extra_flag`) |
| Deshret's Seal | `Metadata/QuestObjects/Act4/MineEntranceSeal` (Positioned, BaseEvents server_only, InteractionAction, Animated, Transitionable, MinimapIcon `hide_flag a4q2-UsedBanner`; Render "ObjectDisplayTextMineEntranceSeal") | room `town.arm` (134,329) | not created (town quest object) | create; use = step 7 |
| Seal blockers | `Metadata/QuestObjects/Act4/MineEntranceSealBlocker` x6 (`blocking`, `unblock_quest_flag a4q2-UsedBanner`, `no_serialise`) | room `town.arm` from (131,310) | client spawns them | QuestBarriers: close the server's walk estimate until `a4q2-UsedBanner` |
| Mines entrance | `Metadata/QuestObjects/Act4/MineEntranceTransition` (AreaTransition, `disable_auto`, Transitionable hides it until transitioned) | tile `mountian_town_v01_01.tdt` record `..._area_transition3` | not created as quest transition | create once `a4q2-UsedBanner` is set; `FlagToEnter(1_4_town, 1_4_3_1)` = `a4q2-UsedBanner` |
| Oyun | `NPC/Act4/Oyun` | `town.arm` (260,566) | placed | generic rows |

### Gates
- `a4q2-UsedBanner` is the EndQuestFlag of `1_4_3_1`, `1_4_3_2`, `1_4_3_3`, `1_4_4_1`, `1_4_4_3`, `1_4_5_1` and
  `1_4_5_2`.
- Blocker `MineEntranceSealBlocker` (`unblock_quest_flag a4q2-UsedBanner`, room `town.arm`): QuestBarriers.
- `FlagToEnter(1_4_town, 1_4_3_1)` = `a4q2-UsedBanner`.

### Rewards
- Offer 30 `a4q2` (taken `a4q2-QuestRewardTaken`): Oyun NPCTalk 543 idx 4 "Red Banner Reward". Per class level 1
  skill gems: the golems (Chaos, Ice, Flame, Stone, Lightning, plus Carrion for Witch/Templar), Sigil of Power, Void
  Sphere, Frost Shield, Hydrosphere, Frozen Legion, Berserk, Tornado, Snipe, Temporal Rift, Ambush, Soul/Flame/Intuitive
  Link (class dependent). All can be built today.

### Quest log check
- 7 → `a4q2-QuestReceived` → Find Deshret's Banner; 6 → `a4q2-SeenVoll` → Kill Voll
- 5 → `a4q2-KilledVoll` → Take Deshret's Banner; 4 → `a4q2-HaveBanner` → Use the Banner to break Deshret's Seal
- 3 → `a4q2-QuestRewardSeen` → (same); 2 → `a4q2-UsedBanner` → Talk to Oyun for a reward
- 1 → `a4q2-UsedBanner`,`a4q2-QuestRewardSeen`; 0 → `a4q2-UsedBanner`,`a4q2-QuestRewardTaken` → Quest Complete

### Driver steps (draft)
```python
STEPS = [
    {"verb": "talk", "npc": "Oyun", "option": "Introduction", "skip_if": "OyunIntroduced"},
    {"verb": "talk", "npc": "Oyun", "option": "The Red Banner", "skip_if": "a4q2-QuestReceived"},
    {"verb": "goto", "area": "1_4_2", "skip_if": "a4q2-UsedBanner"},
    {"verb": "kill", "target": "VollBoss", "skip_if": "a4q2-KilledVoll"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act4/RedBanner", "skip_if": "a4q2-HaveBanner"},
    {"verb": "goto", "area": "1_4_town", "via": "waypoint"},
    {"verb": "interact", "target": "MineEntranceSeal", "skip_if": "a4q2-UsedBanner"},
    {"verb": "wait_flag", "flag": "a4q2-UsedBanner", "timeout": 10},
    {"verb": "talk", "npc": "Oyun", "option": "The Red Banner", "skip_if": "a4q2-Congratulated"},
    {"verb": "reward", "npc": "Oyun", "option": "Red Banner Reward", "pick": ["Summon Flame Golem"],
     "skip_if": "a4q2-QuestRewardTaken"},
]
```

### Risks
- Voll is not placed: his rooms name the point `mapboss1`, and the room-points extractor dropped it. This blocks the
  whole act, since the Mines need the banner.
- The banner drop from Voll is a wiki fact (QuestItems not decoded); the `.it` flags are certain.
- The seal is a town room server object; it needs object use with item consumption plus two transitions (as a1q4's
  glyph wall).

## a4q3 "The King of Fury" (Quest row 36, main)
**Start:** `a4q1-GemQuestsReceived` (Dialla at the Rapture, row 526, or picking up the Eye).
**Finish:** deliver The Eye of Fury to `DiallaRapture` (row 530).
**DONE flag (driver):** `a4q3-DeliveredGem`. QS Order 0 is the shared `a4q3q4-QuestRewardTaken`, taken in a4q1 from
DiallaTownWithered.
**Prerequisites:** `a4q2-UsedBanner` (area gates). Kaom's Dream is entered through a `DreamPortal` in The Crystal Veins.

### Sequence
| # | Real-game step | Area (Id, name) | Event | Flags set | Notes / source |
|---|---|---|---|---|---|
| 1 | Through Kaom's Dream | `1_4_4_1` Kaom's Dream | enter via `Mines/Objects/DreamPortal` (room `rapture_1.arm`) | `Visited-4_4_1` | DreamPortal: AreaTransition + TriggerableBlockage `start_blocked`; what unblocks it is not in the data (assumed: Dialla's row 526) |
| 2 | Reach Kaom's Stronghold | `1_4_4_3` | enter | `Visited-4_4_3` | waypoint area |
| 3 | See Kaom | `1_4_4_3` | seen MV `Metadata/Monsters/Kaom/KaomBoss` | `a4q3-SeenKaom` | `OnMonsterSeen`; Kaom's Transitionable plays a prefight speech (`on_transition`) |
| 4 | Kill Kaom | `1_4_4_3` | kill MV `Metadata/Monsters/Kaom/KaomBoss` (4270) | `a4q3-KilledKaom` | |
| 5 | Take the Eye | `1_4_4_3` | pick up BaseItemTypes 1562 `Metadata/Items/QuestItems/Act4/KaomGem` "The Eye of Fury" (2x2), dropped by Kaom (wiki) | `a4q3-HaveGem`, `a4q1-GemQuestsReceived` | `.it`: `grant_flag a4q3-HaveGem`, `remove_flag a4q3-DeliveredGem`, `extra_flag a4q1-GemQuestsReceived` |
| 6 | Deliver | `1_4_3_3` | `DiallaRapture` NPCTalk 530 idx 4 "The Eye of Fury" (needs `a4q3-HaveGem`) | `a4q3-DeliveredGem` | the server must take the Eye and clear `a4q3-HaveGem` (Medicine Chest pattern) |

### Objects, NPCs and bosses
| What | Type / MV Id / BaseItemTypes Id | Where it comes from | Server today | Needed |
|---|---|---|---|---|
| King Kaom | MV `Metadata/Monsters/Kaom/KaomBoss` (4270; `.ot` `Kaom/kaom`: BossLifeBar, Transitionable, NPC `marker_enabled false`, MinimapIcon) | WorldAreas `1_4_4_3` Bosses; `mapboss` (132,146) of `Plateau/Rooms/boss_epic_2a.arm` (fitted) | NOT spawnable: NPC component | NPC component serialisation; boss bar (Hillock pattern); intro Transitionable 1 to 2 on aggro |
| Kaom's arena | `Kaom/ArenaMiddle` (server_only), `Kaom/Volcano/kaom_volcano` x3, `Plateau/Objects/KaomBossArcherSpawner` x9, `KaomBossSkullSpawnerHigh/Low/Medium` (Positioned, BaseEvents, Animated, Transitionable) | room `boss_epic_2a.arm` | not created | not needed for a `/kill` run (fight adds only) |
| The Eye of Fury | BaseItemTypes 1562 `.../Act4/KaomGem` | Kaom's death | no drop | kill → drop |
| Dream portals | `Metadata/Terrain/Mountain/Mines/Objects/DreamPortal` (Positioned, BaseEvents, Animated, AreaTransition, Stats, ObjectMagicProperties, MinimapIcon, TriggerableBlockage `start_blocked`, InteractionAction) | room server objects: `rapture_1.arm` x2 (not fitted), `NewKaom1/Unique/entrance1_01.arm` (fitted), `Pit/Rooms/Unique/entrance_1_1.arm` | NOT created: transitions come only from tile records | create these room transitions (destinations `1_4_4_1` / `1_4_5_1` and back), unblocked; TriggerableBlockage blob as `Tutorial_Blocker_3` |

### Gates
- `1_4_4_1`, `1_4_4_3` EndQuestFlags `a4q2-UsedBanner`.
- The `DreamPortal` TriggerableBlockage (`start_blocked = true`). Its unblock condition is unknown; simplest, create it
  unblocked once `a4q1-GemQuestsReceived` is set.

### Rewards
None of its own: offer 29 (shared with a4q4, taken flag `a4q3q4-QuestRewardTaken`) is in a4q1.

### Quest log check
- 6 → `a4q1-GemQuestsReceived` → Find Kaom; 5 → `a4q3-SeenKaom` → Kill Kaom; 4 → `a4q3-KilledKaom` → Take the Eye
- 3 → `a4q3-HaveGem` → Deliver the Eye of Fury; 2 → `a4q3-DeliveredGem` → Complete (wait for both Eyes)
- 1 → `a4q4-DeliveredGem`,`a4q3-DeliveredGem` → Talk to Dialla in town; 0 → `a4q3q4-QuestRewardTaken` → Complete

### Driver steps (draft)
```python
STEPS = [
    {"verb": "goto", "area": "1_4_4_1", "via": "transition", "skip_if": "a4q3-HaveGem"},
    {"verb": "goto", "area": "1_4_4_3", "via": "transition", "skip_if": "a4q3-HaveGem"},
    {"verb": "kill", "target": "KaomBoss", "skip_if": "a4q3-KilledKaom"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act4/KaomGem", "skip_if": "a4q3-HaveGem"},
    {"verb": "goto", "area": "1_4_3_3", "via": "waypoint"},
    {"verb": "talk", "npc": "DiallaRapture", "option": "The Eye of Fury", "index": 4, "skip_if": "a4q3-DeliveredGem"},
]
```

### Risks
- Kaom is not spawnable (NPC component); his room point exists.
- The Crystal Veins dream portals are room server objects in an unfitted room; without them neither Dream area is
  reachable except by `/goto`.
- After delivery `a4q3-HaveGem` must clear at once. Otherwise row 529 "The Rapture" (needs `a4q4-HaveGem` +
  `a4q3-DeliveredGem`) or 528 offers the trap path.

## a4q4 "The King of Desire" (Quest row 37, main)
**Start:** `a4q1-GemQuestsReceived`. **Finish:** deliver The Eye of Desire to `DiallaRapture` (row 531).
**DONE flag (driver):** `a4q4-DeliveredGem` (QS Order 0 is the shared `a4q3q4-QuestRewardTaken`, taken in a4q1).
**Prerequisites:** `a4q2-UsedBanner`; Daresso's Dream through a `DreamPortal` in The Crystal Veins.

### Sequence
| # | Real-game step | Area (Id, name) | Event | Flags set | Notes / source |
|---|---|---|---|---|---|
| 1 | Daresso's Dream (the pits) | `1_4_5_1` | enter via `DreamPortal`; cross the pit arena | `Visited-4_5_1`; `a4q4-EnterPit` (unverified) | `a4q4-EnterPit`, `-EnterArena`, `-StartArena`, `-FinishedArenas` (QF 838-841) have no setter in the tables or `.ot`; server-side arena flags, used by no QuestStates row |
| 2 | The Grand Arena | `1_4_5_2` | enter | `Visited-4_5_2` | the arena sections are joined by `AreaTransitionToggleableReverse` x4 (Transitionable) and `ColosseumDoubleDoors*` |
| 3 | Gladiators (on the way) | `1_4_5_2` | kill `GladiatorBossSupport`/`Leader`/`Elite` (4688/4690/4692) | - | points `mapboss2..4` in `Colosseum/gladiators.arm` |
| 4 | See Daresso | `1_4_5_2` | seen MV `Metadata/Monsters/Daresso/DaressoBoss` | `a4q4-SeenDaresso` | |
| 5 | Kill Daresso | `1_4_5_2` | kill MV `Metadata/Monsters/Daresso/DaressoBoss` (4334) | `a4q4-KilledDaresso` | scripted (intro speech, clones `DaressoClone*Frozen`, StateMachine `reave`) |
| 6 | Take the Eye | `1_4_5_2` | pick up BaseItemTypes 1563 `Metadata/Items/QuestItems/Act4/DaressoGem` "The Eye of Desire" (2x2), dropped by Daresso (wiki) | `a4q4-HaveGem`, `a4q1-GemQuestsReceived` | `.it` `remove_flag a4q4-DeliveredGem` |
| 7 | Deliver | `1_4_3_3` | `DiallaRapture` NPCTalk 531 idx 5 "The Eye of Desire" (needs `a4q4-HaveGem`) | `a4q4-DeliveredGem` | take the item, clear `a4q4-HaveGem` |

### Objects, NPCs and bosses
| What | Type / MV Id / BaseItemTypes Id | Where it comes from | Server today | Needed |
|---|---|---|---|---|
| Daresso | MV `Metadata/Monsters/Daresso/DaressoBoss` (4334; `.ot` `Daresso/daresso`: BossLifeBar, NPC, Transitionable, StateMachine) | WorldAreas `1_4_5_2` Bosses; point `mapboss1` (193,218) in `Colosseum/Rooms/Unique/boss_1.arm` (not fitted; only `entrance_1.arm` is) | NOT spawnable (NPC) and no `mapboss` point | NPC component; room fit for `boss_1.arm`; `mapbossN` placement |
| Arena machinery | `Daresso/ArenaMiddle`, `ColosseumDoubleDoorsDaresso` (tile `daressotier3_wall_door_v01_01.tdt`), `ColosseumDoubleDoorsSpawner*`, `ColosseumTrapDoorSpawner*`, statue spawners, `MerveilGladiator`, `DaressoArenaTalk2` | tile records / rooms | not created | for a `/kill` run only the doors/transitions that block the way matter: `AreaTransitionToggleableReverse` x4 must be usable |
| Pit arena | `Arena/Pit/Objects/PitGate` x7, `PitGateTransition` x7 (`pit_teleport`), `PitSpawner` x54, `PitSpawnerGate` x18 (no `.ot` in object-types.json) | tile records of `arena_pit_*.tdt` | not created | unknown whether the path through Daresso's Dream needs them; check with the generated layout (`/goto 1_4_5_2` works around it) |
| The Eye of Desire | BaseItemTypes 1563 `.../Act4/DaressoGem` | Daresso's death | no drop | kill → drop |

### Gates
- `1_4_5_1`, `1_4_5_2` EndQuestFlags `a4q2-UsedBanner`; `DreamPortal` as in a4q3.
- `AreaTransitionToggleableReverse` (Transitionable) inside The Grand Arena: in-area transitions whose state the real
  server toggles as the arena waves end. Their destinations are in-level and not known from the tables.

### Rewards
None of its own (offer 29 in a4q1).

### Quest log check
- 6 → `a4q1-GemQuestsReceived` → Find Daresso; 5 → `a4q4-SeenDaresso` → Kill Daresso
- 4 → `a4q4-KilledDaresso` → Take the Eye of Desire; 3 → `a4q4-HaveGem` → Deliver it
- 2 → `a4q4-DeliveredGem`; 1 → both DeliveredGem → Talk to Dialla in town; 0 → `a4q3q4-QuestRewardTaken`

### Driver steps (draft)
```python
STEPS = [
    {"verb": "goto", "area": "1_4_5_1", "via": "transition", "skip_if": "a4q4-HaveGem"},
    {"verb": "goto", "area": "1_4_5_2", "skip_if": "a4q4-HaveGem"},
    {"verb": "kill", "target": "DaressoBoss", "skip_if": "a4q4-KilledDaresso"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act4/DaressoGem", "skip_if": "a4q4-HaveGem"},
    {"verb": "goto", "area": "1_4_3_3", "via": "waypoint"},
    {"verb": "talk", "npc": "DiallaRapture", "option": "The Eye of Desire", "index": 5,
     "skip_if": "a4q4-DeliveredGem"},
]
```

### Risks
- Daresso is twice blocked: NPC component, and his room `boss_1.arm` is not fitted (point `mapboss1`).
- The Grand Arena's in-area toggleable transitions and doors may cut the way to Daresso even with `/tp`. The `/tp` must
  land inside his arena section.
- The Daresso's Dream pit gates' role is unverified.

## a4q5 "Corpus Malachus" (Quest row 38, main)
**Start:** `PietyBelly` NPCTalk 621 idx 1 "Malachai". **Finish:** `PietyHarvest` NPCTalk 627 idx 4 "Malachai's Organs".
**DONE flag (driver):** `a4q5-QuestComplete` (QS Order 0).
**Prerequisites:** `a4q1-KilledPiety`. `PietyBelly` row 620 "Nightmare" sets `a4q5-PietyThanks`, which row 621 needs.

### Sequence
| # | Real-game step | Area (Id, name) | Event | Flags set | Notes / source |
|---|---|---|---|---|---|
| 1 | Piety's thanks | `1_4_6_2` | `PietyBelly` NPCTalk 620 idx 0 "Nightmare" | `a4q5-PietyThanks` | |
| 2 | Piety: Malachai is in the Black Core | `1_4_6_2` | `PietyBelly` NPCTalk 621 idx 1 "Malachai" | `a4q5-QuestReceived` | Script (needs code): within 300 units `Transition()` every `Belly/Objects/BellyHarvestBlocker`, `BellyArenaDoor`, `BellyArenaTransition`; then `EnableEntrance( 2 )`. `a4q5-QuestReceived` is The Harvest's EndQuestFlag |
| 3 | Enter The Harvest | `1_4_6_3` | enter | `Visited-4_6_3` | `PietyHarvest` stands at the entrance (`core_entrance_1.arm`). Row 623 idx 0 "Malachai's Organs" (not `a4q5-QuestComplete`) sets `PietyHarvestHelp` (gossip) |
| 4 | Guardians | `1_4_6_3` | enter the guardian arenas (one-way doors `BellyArenaDoor`/`BellyArenaTransition`) | `a4q1-KillGuardians` (assumed here) | `a4q1-KillGuardians` has no setter in data; it is in `ConsoleAscent` |
| 5a | Kill Doedre | `1_4_6_3`, room `organ_1.arm` | kill MV `Metadata/Monsters/Doedre/Doedre` (4391) | `a4q1-KilledDoedre` | drops BaseItemTypes 1564 `.../Act4/Organ1` "Malachai's Lungs" (wiki) |
| 5b | Kill Maligaro | `1_4_6_3`, room `organ_2.arm` | kill MV `Metadata/Monsters/Maligaro/Maligaro` (4382) | `a4q1-KilledMaligaro` | drops 1565 `.../Act4/Organ2` "Malachai's Heart" (wiki) |
| 5c | Kill Shavronne | `1_4_6_3`, room `organ_3.arm` | kill MV `Metadata/Monsters/Shavronne/Shavronne` (4400) | `a4q1-KilledShavronne` | drops 1566 `.../Act4/Organ3` "Malachai's Entrails" (wiki) |
| 6 | Pick up each organ | `1_4_6_3` | pickup Organ1/2/3 | `a4q5-HaveOrgan1`/`2`/`3` (`grant_flag`), `a4q5-SeenOrgan` (`extra_flag`) | `.it` `remove_flag a4q5-DeliveredOrganN` |
| 7 | Give the organs to Piety | `1_4_6_3` | `PietyHarvest` NPCTalk 624 idx 1 (needs `a4q5-HaveOrgan1`), 625 idx 2 (needs `-HaveOrgan2`), 626 idx 3 (needs `-HaveOrgan3`), all "Malachai's Organs" | `a4q5-DeliveredOrgan1`/`2`/`3` (QF4+QF5) | each Script: `triggerer.SetSessionQuestFlag(AfterBanditFight);` (session flag). The server takes the organ item (remove_flag) |
| 8 | Piety opens the Black Core | `1_4_6_3` | `PietyHarvest` NPCTalk 627 idx 4 "Malachai's Organs" (needs Delivered 1-3, not `a4q5-QuestComplete`) | `a4q5-QuestComplete` (QF4; QF5 `NEVER_SET`) | Script (needs code): `DirtyTransition` on `QuestObjects/Act4/CoreTransition`; `ClearQuestFlag(AfterBanditFight)` |
| 9 | Partial hand-ins (fallback) | `1_4_6_3` | rows 628-633 (needs `AfterBanditFight` + some Delivered) | none (QF4/QF5 `NEVER_SET`) | Script: `ClearQuestFlag(AfterBanditFight)`. Dialogue only |

### Objects, NPCs and bosses
| What | Type / MV Id / BaseItemTypes Id | Where it comes from | Server today | Needed |
|---|---|---|---|---|
| Piety (Belly) | `NPC/Act4/PietyBelly` | appears after the Piety boss (no room line found) | not created | create at Piety's death; rows 620-622 |
| Belly doors | `Belly/Objects/BellyHarvestBlocker` (TriggerableBlockage `start_blocked`, Transitionable; room `boss_piety_1.arm` (125,295)); `BellyArenaDoor` (TriggerableBlockage), `BellyArenaTransition` (AreaTransition `disable_auto`, Transitionable), tile `mapmarkeronly_bellyspikes_v01_01.tdt` | | not created | row 621 transitions them. If the server never creates the blocker the way is open, unless the terrain marks TriggerableBlockage cells (open question in 12-quests.md §6) |
| Belly side doors | `Belly/Objects/BellyArseDoor` (InteractionAction, TriggerableBlockage `start_blocked`, `save_as_diff`) x3 in `1_4_6_2`, x2 in `1_4_6_3` | tile `belly_buttdoor_v02_01.tdt` / `_v03_02.tdt` | not created | clickable doors: object use → Transitionable 2, unblock; needed if they sit on the path |
| Piety (Harvest) | `NPC/Act4/PietyHarvest` (Transitionable; `unblock_quest_flag a4q1-SeenMalachai`) | room `core_entrance_1.arm` (135,123), fitted | NOT created (out of town) | create; rows 623-633 |
| Doedre | MV `Metadata/Monsters/Doedre/Doedre` (4391; NPC, Transitionable intro) | WorldAreas Bosses #3; point `mapboss3` (198,273) of `Belly/Rooms/Unique/organ_1.arm` (tags `doedrearena`, `organ1`) | NOT spawnable (NPC); room not fitted; point is not `mapboss` | NPC component; room fit; `mapbossN` = Bosses order (`mapboss1` MalachaiBoss, `2` MalachaiBoss2, `3` Doedre, `4` Maligaro, `5` Shavronne: inferred from the rooms) |
| Maligaro | MV `Metadata/Monsters/Maligaro/Maligaro` (4382) | `mapboss4` (181,272) of `organ_2.arm` (`maligaroarena`) | as Doedre | as Doedre |
| Shavronne | MV `Metadata/Monsters/Shavronne/Shavronne` (4400) | `mapboss5` (229,302) of `organ_3.arm` (`shavronnearena`) | as Doedre | as Doedre |
| Organs | BaseItemTypes 1564 `Organ1` Malachai's Lungs, 1565 `Organ2` Malachai's Heart, 1566 `Organ3` Malachai's Entrails (2x2 each) | boss drops (wiki) | no drop | kill → drop; flag sync |
| Black Core mouth | `QuestObjects/Act4/CoreMouth` (InteractionAction, Transitionable, MinimapIcon "QuestObject", Render "MalachaiArenaName1") and `CoreTransition` (AreaTransition `disable_auto`, Transitionable) | tile `belly_taster_entrance_v01_01.tdt` records `TasterMouth_openAnimation_v01_01_animation` / `..._area_transition` | not created | row 627 transitions `CoreTransition` to 2 (targetable). CoreMouth is probably used/transitioned too (unverified). The transition leads within `1_4_6_3` to Malachai's platform (`boss_1.arm`) |
| Malachai stage 1 | MV `Metadata/Monsters/Malachai/MalachaiBoss` (4407; `.ot` `Malachai/malachai`: ActBoss, Transitionable `num_states 5`, `set_cannot_die 1`, `cannot_drop_below_%_life 70`, NPC) | `mapboss1` (289,288) of `Belly/Rooms/Unique/boss_1.arm` (`malachaiplatform`); ally `Metadata/Monsters/Axis/PietyMalachaiAlly` (MV 1433) at a named point (254,290); `Malachai/ArenaMiddle`, `MalachaiTriProjectile`, `BloodWormSpawnerMalachai`; `MalachaiGroundHole` (Counter) | NOT spawnable; room not fitted | scripted: emerges (states 1-3), cannot die; at 70% he submerges. The server must move the fight to stage 2 |
| Malachai stage 2 and hearts | MV `Metadata/Monsters/Malachai/MalachaiBoss2` (4409; Transitionable `num_states 8`, `initial_life_percentage 92`, `set_cannot_die 1`); `Malachai/BeastHeart1/2/3` (MV 4671-4673; immobile, Transitionable 3: idle → vulnerable → dead) | `mapboss2` (311,322) and the heart points of `boss_2.arm` (`depthsboss`); `MalachaiDepthsReturn` (AreaTransition) at (325,233); `MalachaiTeleportMarker`, `FlayedSpawnerMalachai`, `BellyHeartBloodPillar` | NOT spawnable; room not fitted | scripted: the hearts heal him (states 3/5), and he can die only after them. On death `QuestObjects/Act4/MalachaiDeathPortal` (AreaTransition, Transitionable) appears; it presumably leads to Highgate (unverified) |

### Gates
- `1_4_6_3` EndQuestFlags `a4q5-QuestReceived`; `FlagToEnter(1_4_6_2, 1_4_6_3)` = `a4q5-QuestReceived` (the
  `BellyArenaTransition` opened by row 621).
- The in-area Black Core entrance (`CoreTransition`) after `a4q5-QuestComplete`.

### Rewards
None (no offer, no static reward).

### Quest log check
- 26 → `a4q5-QuestReceived` → Find Malachai's organs
- 25..17 → one `a4q5-DeliveredOrganN` or one `a4q5-HaveOrganN` → Find the remaining organs
- 16..2 → combinations of HaveOrgan/DeliveredOrgan → Find/Deliver the remaining organ(s)
- 1 → `a4q5-HaveOrgan1`,`2`,`3` → Deliver the organs to Piety
- 0 → `a4q5-QuestComplete` → Quest Complete

### Driver steps (draft)
```python
STEPS = [
    {"verb": "talk", "npc": "PietyBelly", "option": "Nightmare", "skip_if": "a4q5-PietyThanks"},
    {"verb": "talk", "npc": "PietyBelly", "option": "Malachai", "skip_if": "a4q5-QuestReceived"},
    {"verb": "goto", "area": "1_4_6_3", "via": "transition"},
    {"verb": "kill", "target": "Doedre", "skip_if": "a4q5-DeliveredOrgan1"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act4/Organ1", "skip_if": "a4q5-DeliveredOrgan1"},
    {"verb": "kill", "target": "Maligaro", "skip_if": "a4q5-DeliveredOrgan2"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act4/Organ2", "skip_if": "a4q5-DeliveredOrgan2"},
    {"verb": "kill", "target": "Shavronne", "skip_if": "a4q5-DeliveredOrgan3"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act4/Organ3", "skip_if": "a4q5-DeliveredOrgan3"},
    {"verb": "talk", "npc": "PietyHarvest", "option": "Malachai's Organs", "index": 1,
     "skip_if": "a4q5-DeliveredOrgan1"},
    {"verb": "talk", "npc": "PietyHarvest", "option": "Malachai's Organs", "index": 2,
     "skip_if": "a4q5-DeliveredOrgan2"},
    {"verb": "talk", "npc": "PietyHarvest", "option": "Malachai's Organs", "index": 3,
     "skip_if": "a4q5-DeliveredOrgan3"},
    {"verb": "talk", "npc": "PietyHarvest", "option": "Malachai's Organs", "index": 4,
     "skip_if": "a4q5-QuestComplete"},
]
```

### Risks
- Five Harvest bosses: none is spawnable (NPC component), none of their rooms is fitted, and all use `mapboss1..5`
  points. This is the largest single blocker of the act.
- Malachai's fight is fully scripted: two varieties, cannot-die stages, three hearts, ground hole, Piety ally,
  in-level transitions. For the automation the minimum is to place `MalachaiBoss2` reachable (or skip stage 1 and the
  hearts) and let `/kill` set `a4q1-QuestComplete`. `set_cannot_die` and `cannot_drop_below_%_life` must not stop
  `/kill`.
- `SetSessionQuestFlag(AfterBanditFight)` / `ClearQuestFlag`: session (not saved) flags are new NPC-script work. The
  same flag name is used in Act 2 for the bandits, so the server must not save it.
- Each organ row shares the text "Malachai's Organs" (rows 623-633); the driver needs the option index.

## a4q6 "An Indomitable Spirit" (Quest row 39, side)
**Start:** Tasuni NPCTalk 610 idx 18 "Deshret" (needs `a4q2-UsedBanner`, not `a4q6-DeshretFreed`).
**Finish:** Tasuni's Book of Skill (offer 31), used.
**DONE flag (driver):** `a4q6-UsedRewardBook` (QS Order 0).
**Prerequisites:** `a4q2-UsedBanner`.

### Sequence
| # | Real-game step | Area (Id, name) | Event | Flags set | Notes / source |
|---|---|---|---|---|---|
| 1 | Tasuni hears Deshret | `1_4_town` | `Tasuni` NPCTalk 610 idx 18 "Deshret" | `a4q6-QuestReceived` | generic |
| 2 | Find the spirit | `1_4_3_2` The Mines Level 2 | come near `QuestObjects/Act4/DeshretSpirit` | `a4q6-DeshretSpiritSeen` | no setter in data: proximity trigger (as a1q4's `SeenGlyphWall`) |
| 3 | Free the spirit | `1_4_3_2` | use object `Metadata/QuestObjects/Act4/DeshretSpirit` | `a4q6-DeshretFreed` | object use: set the flag, Transitionable 1 to 2 on `DeshretSpirit`, `DeshretHand`, `DeshretAura`. `DeshretSpirit` MinimapIcon `hide_flag a4q6-DeshretFreed` |
| 4 | Tasuni congratulates | `1_4_town` | `Tasuni` NPCTalk 609 idx 17 "Deshret" (needs `a4q6-DeshretFreed`, not `a4q6-UsedRewardBook`) | `a4q6-Congratulated` (QF4) | |
| 5 | Reward | `1_4_town` | `Tasuni` NPCTalk 608 idx 16 "Deshret Reward" (needs `a4q6-Congratulated`, not `a4q6-HaveRewardBook`/`-UsedRewardBook`), offer 31 | `a4q6-QuestRewardSeen` | the book's pickup into the inventory sets `a4q6-HaveRewardBook` (`grant_flag`) |
| 6 | Read the book | anywhere | use BaseItemTypes 1567 `Metadata/Items/QuestItems/Act4/Book-a4q6` | `a4q6-UsedRewardBook` (`use_flag`) | QuestStaticRewards 11: 1 passive point + 2 respec points; the book is consumed (`remove_flag`) |

### Objects, NPCs and bosses
| What | Type / MV Id / BaseItemTypes Id | Where it comes from | Server today | Needed |
|---|---|---|---|---|
| Deshret's spirit | `Metadata/QuestObjects/Act4/DeshretSpirit` (Positioned size 4, BaseEvents server_only, Animated, InteractionAction, Transitionable, MinimapIcon; Render "ObjectDisplayTextDeshretSpirit") | room `Mines/Rooms/Unique/deshret_quest.arm` (106,~138), not fitted | not created | room fit; create; use → step 3 |
| Spirit effects | `QuestObjects/Act4/DeshretHand`, `DeshretAura` (Transitionable) | same room | not created | cosmetic |
| Book of Skill | BaseItemTypes 1567 `.../Act4/Book-a4q6` (`AbstractPassiveSkillAnd2RespecsBook`) | offer 31 | offer cannot be built (a book is not a gem, currency or normal flask) | build the book item; book use = `use_flag` + QSR 11 |

### Gates
None.

### Rewards
- Offer 31 `a4q6` (no taken flag in QuestRewardOffers; the book's `grant_flag`/`use_flag` exclude row 608 afterwards):
  Tasuni row 608 "Deshret Reward", any class, Book of Skill L1. Offer 32 `a4q6b_tencent` (col8, Tencent only):
  ignore.
- QuestStaticRewards 11: flag `a4q6-UsedRewardBook`, `__col1` = 1 (passive skill point), `__col7` = 2 (respec points);
  the book type `AbstractPassiveSkillAnd2RespecsBook` and the wiki agree.

### Quest log check
- 4 → `a4q6-QuestReceived` → Find Deshret's spirit; 3 → `a4q6-DeshretSpiritSeen` → Free it
- 2 → `a4q6-DeshretFreed` → Talk to Tasuni; 1 → `a4q6-HaveRewardBook` → use the book; 0 → `a4q6-UsedRewardBook`

### Driver steps (draft)
```python
STEPS = [
    {"verb": "talk", "npc": "Tasuni", "option": "Deshret", "skip_if": "a4q6-QuestReceived"},
    {"verb": "goto", "area": "1_4_3_2", "skip_if": "a4q6-DeshretFreed"},
    {"verb": "interact", "target": "DeshretSpirit", "skip_if": "a4q6-DeshretFreed"},
    {"verb": "talk", "npc": "Tasuni", "option": "Deshret", "skip_if": "a4q6-Congratulated"},
    {"verb": "reward", "npc": "Tasuni", "option": "Deshret Reward", "pick": ["Book of Skill"],
     "skip_if": "a4q6-HaveRewardBook"},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act4/Book-a4q6", "skip_if": "a4q6-UsedRewardBook"},
    {"verb": "passives"},
]
```
`use_item` is not in CONTRACT.md yet; every Book of Skill quest needs it (or the driver's own book handling).

### Risks
- The spirit is a room server object in an unfitted room.
- The Book of Skill reward cannot be built today, and book use (passive + 2 respec points) is new work.

## a5q1a "Return to Oriath" (Quest row 62, main; the Act 4 → 5 transition)
**Start:** `TasuniOnMalachaiDead` (Tasuni NPCTalk 615 idx 23 "Malachai", needs `a4q1-QuestComplete`).
**Finish:** entering `1_5_1` The Slave Pens (`Visited-5_1`: every QS row has `Visited-5_1` missing, so the quest
leaves the log on entry).
**DONE flag (driver):** `Visited-5_1`.
**Prerequisites:** `a4q1-QuestComplete` (EndQuestFlags of `1_4_7`).

The data places no NPCTalk row on this transition: no Lilly Roth, no boat, no transport script. The only NPCTalk row is
Tasuni's 615, which starts the log. `DiallaTownWithered` row 521 (a4q1) transitions the town's `AscentTransition`.
The player walks The Ascent and pulls the Resonator lever, then clicks the Oriath Portal (an AreaTransition object)
into The Slave Pens. The wiki's "Return to Oriath" page describes the Act 5 quest (Overseer Krow, Lani) instead, and
does not match this Act 4 row (wiki/table mismatch).

### Sequence
| # | Real-game step | Area (Id, name) | Event | Flags set | Notes / source |
|---|---|---|---|---|---|
| 1 | Talk to Tasuni | `1_4_town` | `Tasuni` NPCTalk 615 idx 23 "Malachai" | `TasuniOnMalachaiDead` | log: "Access the Ascent" |
| 2 | Open the Ascent | `1_4_town` | `DiallaTownWithered` NPCTalk 521 (a4q1 step 20) | `DiallaOnMalachaiDead` | Script: `DirtyTransition` on `AscentTransition`. Alternative: the Gateway creates the transition from `a4q1-QuestComplete` |
| 3 | Enter The Ascent | `1_4_7` The Ascent | enter via `QuestObjects/Act4/AscentTransition` | `Visited-4_7` | EndQuestFlags `a4q1-QuestComplete` |
| 4 | Find the Resonator | `1_4_7` | come near `Terrain/Act4/Area7/Objects/PortalDevice` / the lever | `a5q1-SeenResonator` | no setter in data: proximity trigger |
| 5 | Pull the lever | `1_4_7` | use object `Metadata/Terrain/Act4/Area7/Objects/PortalDeviceLever` | `a5q1-UsedResonator` | object use: set the flag, lever Transitionable 1 to 2 (`DisableTargetable`), `PortalDevice` 1 to 2, `PortalDeviceTransition` 1 to 2 (`EnableTargetable`, portal animation) |
| 6 | Enter the Oriath Portal | `1_4_7` → `1_5_1` The Slave Pens | use `Metadata/Terrain/Act4/Area7/Objects/PortalDeviceTransition` (AreaTransition, `disable_auto`, Transitionable, MinimapIcon "EntrancePortal" `show_on_transition`) | `Visited-5_1` | the player is moved by the area transition object. EndQuestFlags of `1_5_1` = `a5q1-UsedResonator`. `CharacterStartQuestState` `ConsoleSlavePens` = `ConsoleAscent` + `a5q1-UsedResonator` |

### Objects, NPCs and bosses
| What | Type | Where it comes from | Server today | Needed |
|---|---|---|---|---|
| Town exit | `QuestObjects/Act4/AscentTransition` (AreaTransition + Transitionable; MinimapIcon "Entrance") | tile `mountian_town_v01_01.tdt` record `..._area_transition4` | not created (quest-gated) | create transitioned when `a4q1-QuestComplete`; `FlagToEnter(1_4_town, 1_4_7)` |
| Resonator | `Terrain/Act4/Area7/Objects/PortalDevice` (static, Transitionable) | room `Act4/Area7/Rooms/Unique/ascent_summit_01.arm` (214,154) | not created; room not fitted | room fit; create |
| Lever | `Terrain/Act4/Area7/Objects/PortalDeviceLever` (InteractionAction, Transitionable, MinimapIcon "QuestObject" `hide_on_transition`; Render "ObjectDisplayTextLever") | same room (220,128) | not created | create; use → step 5 |
| Oriath Portal | `Terrain/Act4/Area7/Objects/PortalDeviceTransition` (AreaTransition + Transitionable) | same room (202,130); destination `1_5_1` (WorldAreas `1_4_7` connections: `1_4_town`, `1_5_1`) | not created (room server object, not a tile record) | create (state 1, untargetable) and step to 2 on the lever; `FlagToEnter(1_4_7, 1_5_1)` = `a5q1-UsedResonator` |
| The White Death | MV `Metadata/Monsters/Vendigo/VendigoBoss` (5504) | `mapboss` of `Unique/join_boss.arm` / `join_boss2.arm` | spawnable | optional |

### Gates
- `1_4_7` EndQuestFlags `a4q1-QuestComplete`; `1_5_1` EndQuestFlags `a5q1-UsedResonator`.
- `FlagToEnter(1_4_town, 1_4_7)` = `a4q1-QuestComplete`; `FlagToEnter(1_4_7, 1_5_1)` = `a5q1-UsedResonator`.

### Rewards
None.

### Quest log check
- 3 → `TasuniOnMalachaiDead`, missing `Visited-5_1` → Access the Ascent from the eastern side of Highgate
- 2 → `Visited-4_7` → Climb the Ascent; 1 → `a5q1-SeenResonator` → Activate the Resonator
- 0 → `a5q1-UsedResonator` → Enter the portal to Oriath; all rows vanish with `Visited-5_1`

### Driver steps (draft)
```python
STEPS = [
    {"verb": "talk", "npc": "Tasuni", "option": "Malachai", "skip_if": "TasuniOnMalachaiDead"},
    {"verb": "talk", "npc": "DiallaTownWithered", "option": "Malachai", "skip_if": "DiallaOnMalachaiDead"},
    {"verb": "goto", "area": "1_4_7", "via": "transition", "skip_if": "Visited-5_1"},
    {"verb": "interact", "target": "PortalDeviceLever", "skip_if": "a5q1-UsedResonator"},
    {"verb": "wait_flag", "flag": "a5q1-UsedResonator", "timeout": 10},
    {"verb": "goto", "area": "1_5_1", "via": "transition"},
]
```

### Risks
- The whole summit (lever, device, portal) is room server objects in an unfitted room; the portal is a room-object
  area transition (the Gateway only creates tile-record transitions).
- The town's `AscentTransition` is quest-gated and needs the a1q1-style gated creation.

## Implementation summary

| Quest | Main/side | Features | Risk | Est. size |
|---|---|---|---|---|
| a4q1 The Eternal Nightmare | main | NPC hand-in, out-of-town NPCs (DiallaAqueduct, DiallaRapture, PietyBelly), town NPC variant swap (DiallaTownWithered), NPCTalk scripts (534, 535, 521), NPC proximity trigger, reward offer 29, scripted/arena boss (Piety, Malachai), transition (`RaptureTransition`, `AscentTransition`), area gate | high: unfitted `rapture_1.arm`, NPC-component bosses, Malachai's scripted stages, withered-Dialla swap | L |
| a4q2 Breaking the Seal | main | kill trigger, boss placement (`mapboss1`), kill → item drop, item pickup flag, object use with item consumption (seal), blocker (`MineEntranceSealBlocker`), quest transition, reward offer 30, area gate | high: Voll not placed today (`mapboss1` dropped), act-wide gate | M |
| a4q3 The King of Fury | main | room-object transitions (`DreamPortal`), boss placement, scripted/arena boss (Kaom, NPC component), kill → item drop, item pickup flag, NPC hand-in (out-of-town Dialla) | high: Kaom not spawnable; dream portals missing | M |
| a4q4 The King of Desire | main | room-object transitions, boss placement (unfitted room, `mapboss1`), scripted/arena boss (Daresso, NPC component, toggleable in-area transitions), kill → item drop, item pickup flag, NPC hand-in | high: Daresso not spawnable and not placed; arena doors | L |
| a4q5 Corpus Malachus | main | out-of-town NPCs (PietyBelly, PietyHarvest), NPCTalk scripts (621, 627, session flags), boss placement x5 (`mapbossN`, unfitted rooms), NPC-component bosses, kill → item drop x3, item pickup flags, NPC hand-in with item removal, in-area transitions (`CoreTransition`, `BellyArenaTransition`), area gate | high: five unspawnable bosses + Malachai's fight | L |
| a4q6 An Indomitable Spirit | side | NPC talk, object use (DeshretSpirit, unfitted room), proximity trigger, reward offer 31 (book not buildable), book of skill (1 passive + 2 respec) | med: room fit + book item/use | S |
| a5q1a Return to Oriath | main (transition) | NPC talk, quest-gated transition (`AscentTransition`), proximity trigger, object use (lever), room-object area transition to `1_5_1` | med: unfitted `ascent_summit_01.arm`, room-object transition | S |
