# Act 7 quests

Act 7 is the second act of Part II. Its town is The Bridge Encampment (`2_7_town`, Helena, Eramir, Yeena, Weylam Roth,
Sin). The player arrives from The Brine King's Reef (`2_6_15`) on Weylam's ship: `WeylamReef2` NPCTalk 1010 "Sail to
the Bridge Encampment" (needs `a6q1-KilledBoss`) runs `triggerer.AreaTransitionTo(2_7_town, 0, 0.5)`; every act 7
outdoor area up to The Crypt has EndQuestFlags `a6q1-KilledBoss` (the act 6 file documents the Brine King and the
reef). The main line: Sin sends the player after Maligaro (`a7q2`), whose map lies in The Crypt; the map opens
Maligaro's Sanctum from the Reverie Device in The Chamber of Sins Level 1; Silk wants Black Death's venom (`a7q3`) and
pays with the Obsidian Key, which opens the secret passage from Chamber of Sins Level 2 to The Den; through The Den and
The Ashen Fields the player kills Greust/Ralakesh (`a7q1`), which opens The Northern Forest and everything beyond;
Yeena needs seven Fireflies from The Dread Thicket (`a7q7`) to burn open the Temple of Decay in The Vaal City, where
Arakaali (`a7q4`) dies and the exit to Act 8 (The Sarn Ramparts `2_8_1`) opens. Side quests: The Silver Locket
(`a7q5`), Kishara's Star (`a7q6`, 1 passive + 2 respec), In Memory of Greust (`a7q8`), Queen of Despair (`a7q9`,
Gruthkul, 1 passive). Pantheon souls of this act: Ralakesh (`a7q1-KilledBoss`), Arakaali (major, `a7q4-KilledBoss`),
Gruthkul (`a7q9-KilledBoss`); Maligaro gives no Pantheon soul.

Sources: client tables (`data/tables`), the generated layouts (`lg.ps1 questobjects` / `generate`, seed 1; 2_7_2 at
seed 1585866662), the unpacked game text (`.ot`/`.it`), the wiki (Fandom mirror, 2026-10-30). The sections after the status are the
research done before the act was built: their "Risks" are what was expected then, the status says how it went.

Note on the inputs: `lg.ps1 questobjects` lists only the long room-line form (`x y rot … "model.ao" "type"`). Short
room lines (`x y rot "type"`) are missing from that listing: the Maligaro map chest, the locket chest, Kishara's
chest, `BanditBlock` and `YeenaPositionMarker` are all short lines, found with `lg.ps1 grep` (listed below).

## Status

On the quest engine (`Modules/Core/World/QuestObjects/Acts/Act7.cs`, [12](../../protocol/12-quests.md) §16), with
driver steps `a7q5`, `a7q2`, `a7q3`, `a7q1`, `a7q8`, `a7q9`, `a7q7`, `a7q6`, `a7q4` (a7q2 before a7q3: Silk appears with
the map). Played live on 2026-10-31 before the game moved into modules; on the modules the oracle (`QuestEngineTests`:
after Act 6 every quest of the act completes and Act 8 ends with `E3EndOfAct8`'s act 7–8 quest flags) and the offline
exit audit (`scripts/re/exits --quests`) check it, not yet the client.
What differs from the sections below:
- Every boss is placed (the numbered boss points: Maligaro, Black Death and Fidelitas at `mapboss1–3`; Gruthkul at
  `mapboss1`; The Ashen Fields' boss is Greust when all three bandit lords died, else the spared lord's avatar: each of
  the four sets `a7q1-KilledBoss`).
- The quest exits are created from the layout's graph nodes ([08](../../protocol/08-area-transitions.md) §8):
  MaligaroPassage into The Den and TempleOfDecayEntrance, gated by `a7q1-UsedKey` / `a7q7-DeliveredItems`; the bridge
  exit into The Sarn Ramparts by `a7q4-KilledBoss`, Arakaali's bridge (TriggerableBlockage) lowers with it; The Ashen
  Fields' way north, a disabled entrance the gate creates (`gated` in the exit audit), by `a7q1-KilledBoss`.
- `a7q2-ClearedSinBlock` is set coming to `MaligaroSinsBarrier` with the Obsidian Key (`On.NearWith`); its nine blockers
  lift with it. `a7q1-SeenBlockage` / `a7q4-SeenBlockage` / `a7q8-SeenShrine` are proximity rules.
- The seven Fireflies' chests have no placement in the data: `On.ScatteredChest` spreads them over The Dread Thicket's
  walk map (the same cells for the same seed).
- Maligaro's portal (`MaligaroPortalTransition`, spawned by the device beside itself) is a quest portal (`On.Portal`)
  shown with `a7q2-DeliveredItem`. Played live 2026-10-31 (all nine quests, no `/goto`).
- Not created: the arena machinery (Arakaali's turrets, Maligaro's door), Yeena
  after Arakaali. The Crossroads (`2_7_2`) plays at a seed the client builds; its BanditBlock is placed by no room the
  generation keeps at seed 1, so its sight (`a7q1-SeenBlockage`) is optional.
- Rewards: every offer built live, quest-item offers too (the Obsidian Key, Greust's Necklace).

## Areas

| Id | Name | Lvl | WP | Gen | EndQuestFlags | Unique bosses (MV, spawnable) | Quest objects / NPCs |
|---|---|---|---|---|---|---|---|
| `2_7_town` | The Bridge Encampment | 55 | yes | yes | `a6q1-KilledBoss` | — | Eramir, Helena, SinTown, Weylam, YeenaTown (room `broken_bridge_town.arm`) |
| `2_7_1` | The Broken Bridge | 50 | no | yes | `a6q1-KilledBoss` | — | `LocketChest` short line in `Area1/Rooms/manor.arm` (276, 282); manor placed at seed 1 |
| `2_7_2` | The Crossroads | 51 | yes | seed ≠ 1 (client also fails at 1) | `a6q1-KilledBoss` | — | `BanditBlock` short line in `Area2/Rooms/road_gate_entrance_main.arm` (77, 80) |
| `2_7_3` | The Fellshrine Ruins | 51 | no | yes | `a6q1-KilledBoss` | Bundle of Woe (4128, yes) | — |
| `2_7_4` | The Crypt | 51 | yes | yes | `a6q1-KilledBoss` | — | `MaligaroMapChest` short line in `Area4/Rooms/unique/maligarochest_01.arm` (91, 115); placed at seed 1 |
| `2_7_5_1` | The Chamber of Sins Level 1 | 52 | yes | yes | `a6q1-KilledBoss` | (mapboss room, no WA boss) | `SilkSins` (orrery.arm), `MaligaroOrrery` + `_Main` (tile temple_maporrery4), `MaligaroSinsBarrier` + 9 `…Blocker` (exit_strairsdown rooms) |
| `2_7_5_2` | The Chamber of Sins Level 2 | 52 | no | yes | `a7q2-ClearedSinBlock` | Plague Retch (1169, yes) | `MaligaroPassage` + `MaligaroPassageCover` (tile templeruinforest_maligaro_passage) |
| `2_7_5_map` | Maligaro's Sanctum | 52 | no | yes | `a6q1-KilledBoss` | Maligaro, the Artist (5692, yes, bar); Black Death, Pain Unending (5698, yes); Fidelitas, Loyalty Undying (5700, yes) | boss room `Area5Map/Rooms/boss.arm`: points `mapboss1..3`, `AnimateReversible` door, `ArenaMiddle`; `FleshAltar`; `MapReturnPortal` |
| `2_7_6` | The Den | 53 | yes | yes | `a7q1-UsedKey` | The Great White Bones (118), The Bone Sculptor (448) | — |
| `2_7_7` | The Ashen Fields | 53 | yes | yes | `a7q1-UsedKey` | Greust, Lord of the Forest (5724, yes, bar); Oak (5731), Alira (5734), Kraityn (5737) Ralakesh versions | `town_entrance.arm`: `mapboss`, `Ralakesh/Ralakesh` totem, spawn locators, `AliraCauldronSpawnerObject` |
| `2_7_8` | The Northern Forest | 53 | yes | yes | `a7q1-KilledBoss` | Tunnelworm (2812) | `GreustShrine` (tile forest_azmeri_shrine, record `…_clickable_bearhead`) |
| `2_7_9` | The Dread Thicket | 53 | no | yes | `a7q1-KilledBoss` | Gruthkul, Mother of Despair (6047, yes, bar); The Dreadstone (4225) | `despair_boss.arm`: point `mapboss1`, 9 `BearCrack` + 10 `BearCrackInert`; no firefly chests in any room/tile |
| `2_7_10` | The Causeway | 54 | yes | yes | `a7q1-KilledBoss` | — | `KisharaStarChest` short line in `Area10/Rooms/unique/city.arm` (289, 44); city placed at seed 1 |
| `2_7_11` | The Vaal City | 54 | yes | yes | `a7q1-KilledBoss` | — | `YeenaVaalCity`, `ArakaaliTempleEntranceBlock` (room temple_entrance_banteay_srei), `TempleOfDecayEntrance` (tile BanteaySrei_Web) |
| `2_7_12_1` | The Temple of Decay Level 1 | 54 | no | yes | `a7q7-DeliveredItems` | Blightblade (859) | — |
| `2_7_12_2` | The Temple of Decay Level 2 | 55 | no | yes | `a7q7-DeliveredItems` | Arakaali, Spinner of Shadows (5740, yes, bar) | `boss.arm`: `mapboss`, `SilkArakaali`, 26 turrets, `ArakaaliSequencer`, `Arakaali_Bridge`(+`_Transition`), `YeenaPositionMarker`, `YeenaFox` |

Room-points (`data/room-points.json`) for `2_7_5_map` and `2_7_9` list only the entrance rooms: their boss rooms use
the points `mapboss1`/`mapboss2`/`mapboss3`, not `mapboss`, so Maligaro, Black Death, Fidelitas and Gruthkul are not
placed today.

## a7q1 "The Master of a Million Faces" (Quest row 77, main)

**Start:** Eramir in town, option "Greust". **Finish:** Eramir "Ralakesh Reward" (Book of Skill).
**DONE flag (driver):** `a7q1-UsedRewardBook` (QS 774 Order 3; Orders 2–0 are the same with a bandit flag).
**Prerequisites:** none for the talk (Eramir rows 1052–1055 differ only by the act 2 bandit choice:
`a2q1/a2q2/a2q3-QuestRewardTaken`; the driver default "kill all three" takes row 1055). The way to Greust needs
`a7q1-UsedKey` (The Den, The Ashen Fields), i.e. `a7q2` + `a7q3` first.

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes / source |
|---|---|---|---|---|---|
| 1 | Get the quest | `2_7_town` | talk Eramir, NPCTalk 1052/1053/1054/1055 "Greust" | `a7q1-QuestReceived` (QF4) | row by bandit choice |
| 2 | (opt.) see the blocked road | `2_7_2` | come near `BanditBlock` | `a7q1-SeenBlockage` | server-only marker; who sets it is not in the data (assumed proximity) |
| 3 | (opt.) ask Helena | `2_7_town` | talk Helena, NPCTalk 1081 "The Den" (needs SeenBlockage) | `a7q1-DenClue` | log hint only |
| 4 | Get the Obsidian Key, use it | `2_7_5_2` | see `a7q2` / `a7q3` | `a7q1-HaveKey`, `a7q1-UsedKey` | done in a7q3 |
| 5 | Through The Den | `2_7_6` → `2_7_7` | enter areas | `Visited-7_6`, `Visited-7_7` | gates `a7q1-UsedKey` |
| 6 | See Greust | `2_7_7` | MV 5724 within range | `a7q1-SeenBoss` | `OnMonsterSeen` |
| 7 | Kill Greust | `2_7_7` | kill MV 5724 `Metadata/Monsters/Ralakesh/Greust/Greust` | `a7q1-KilledBoss` | Pantheon Ralakesh unlock |
| 8 | Sin harvests Ralakesh | `2_7_7` | `SinRalakesh` appears; NPCTalk 1130 "Ralakesh" | `SinRalakeshReward` | script opens Pantheon screen; SinTown 1101 sets it too |
| 9 | Eramir congratulates | `2_7_town` | NPCTalk 1057 "Ralakesh" | `a7q1-Congratulated` | removes `SinRalakesh` (`unblock_quest_flag`) |
| 10 | Reward | `2_7_town` | NPCTalk 1058 "Ralakesh Reward", offer 46 | `a7q1-QuestRewardSeen` | Book-a7q1 |
| 11 | Use the book | any | use `Metadata/Items/QuestItems/Act7/Book-a7q1` | `a7q1-HaveRewardBook` → `a7q1-UsedRewardBook` | QSR 18: 1 passive |

### Objects, NPCs and bosses
| What | Type / MV / item | Where | Server today | Needed |
|---|---|---|---|---|
| Greust | MV 5724 (boss bar) | WorldAreas `2_7_7` Bosses; `town_entrance.arm` `mapboss` | placed, but the pick among 4 bosses may not be Greust | always place Greust at `mapboss` |
| Bandit lords (Ralakesh) | MV 5731 Oak, 5734 Alira, 5737 Kraityn | WA Bosses / spawn locators | maybe one placed instead of Greust | not needed for the flag |
| Ralakesh totem | `Metadata/Monsters/Ralakesh/Ralakesh` (server_only, Transitionable: death anim) | room line `town_entrance.arm` (130, 192) | not created | optional: transition on Greust's death |
| Bandit block | `Metadata/QuestObjects/Act7/BanditBlock` (server_only, no_serialise) | short line `road_gate_entrance_main.arm` (77, 80) | none | proximity trigger → `a7q1-SeenBlockage` (optional) |
| Sin | `Metadata/NPC/Act7/SinRalakesh` (extends SinBossDeathParent; NPC+Transitionable) | spawned at the kill, not in room data | not created | spawn at Greust's death (Transitionable entrance) |

### Gates
- `a7q1-KilledBoss` opens `2_7_8`, `2_7_9`, `2_7_10`, `2_7_11` (client EndQuestFlags); the Gateway should refuse them
  too (`FlagToEnter`).
- `a7q1-UsedKey` gates `2_7_6` / `2_7_7` (see a7q3).

### Rewards
- Offer 46 `a7q1` (QuestRewards 1019): `Metadata/Items/QuestItems/Act7/Book-a7q1`, taken flag none (book flags).
- QuestStaticRewards 18: `a7q1-UsedRewardBook` → 1 passive point.

### Quest log check
Every state also exists with `a2q1/a2q2/a2q3-QuestRewardTaken` (3 more Orders each, same text). Base states:
- 59 `a7q1-QuestReceived` → travel to the old Forest Encampment, kill Greust
- 55 `a7q1-SeenBlockage` → talk to Helena
- 51 `a7q1-DenClue` → find a way into the Den
- 47 `a7q2-SeenSinsBlock` → talk to Sin about the barrier
- 43 `a7q2-SeenSinsBlock` + `a7q2-QuestReceived` → defeat Maligaro to access Chamber of Sins L2
- 39 `a7q2-SeenSinsBlock` + `a7q3-SpawnSilk` → help Silk to access L2
- 35 `a7q1-HaveKey` → use the Obsidian Key to access the Den
- 31 `Visited-7_5_2` → take the Obsidian Key from Silk; 27 `Visited-7_5_2` + `a7q1-HaveKey` → use the key
- 23 `a7q1-UsedKey` → find the old Forest Encampment
- 19 `a7q1-SeenBoss` → kill Greust; 15 `a7q1-KilledBoss` → talk to Eramir
- 11 `a7q1-QuestRewardSeen`; 7 `a7q1-HaveRewardBook` → use the book; 3 `a7q1-UsedRewardBook` → complete

### Driver steps (draft)
```python
[
    {"verb": "talk", "npc": "Eramir", "option": "Greust", "skip_if": "a7q1-QuestReceived"},
    # a7q2 and a7q3 must be done first (a7q1-UsedKey)
    {"verb": "goto", "area": "2_7_7", "skip_if": "a7q1-KilledBoss"},
    {"verb": "kill", "target": "Greust, Lord of the Forest", "skip_if": "a7q1-KilledBoss"},
    {"verb": "talk", "npc": "Sin", "option": "Ralakesh", "skip_if": "SinRalakeshReward"},
    {"verb": "talk", "npc": "Eramir", "option": "Ralakesh", "skip_if": "a7q1-Congratulated"},
    {"verb": "reward", "npc": "Eramir", "option": "Ralakesh Reward", "skip_if": "a7q1-QuestRewardSeen"},
    {"verb": "use_item", "item": "Book-a7q1", "skip_if": "a7q1-UsedRewardBook"},  # verb not in CONTRACT yet
]
```

### Risks
- Boss placement picks one of four WorldAreas Bosses for `2_7_7`; Greust must be the one at `mapboss`.
- `SinRalakesh` is not in any room: the server must spawn it at the kill; the driver can use SinTown 1101 instead.
- `a7q1-SeenBlockage` / `a7q2-SeenSinsBlock` setters are not in the client data (assumed proximity triggers);
  neither is needed for progression.
- No CONTRACT verb uses an inventory item (books): needs `use_item` or a command.

## a7q2 "Essence of the Artist" (Quest row 78, main)

**Start:** SinTown "Inquisitor Maligaro". **Finish:** Helena "Maligaro Reward".
**DONE flag (driver):** `a7q2-QuestRewardTaken` (offer 47 taken flag).
**Prerequisites:** none (Sin row 1097 needs nothing; QS 845 Order 14 just shows "Talk to Sin" on `Visited-7_town`).

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Get the quest | `2_7_town` | SinTown NPCTalk 1097 "Inquisitor Maligaro" | `a7q2-QuestReceived` | |
| 2 | Ask Helena | `2_7_town` | Helena NPCTalk 1077 "Maligaro" | `a7q2-MapClue` (QF4+QF5) | |
| 3 | Find the map | `2_7_4` | open `Metadata/Chests/QuestChests/MaligaroMapChest` (Chests 209 "Container of Sins") → pick up `Metadata/Items/QuestItems/Act7/MaligaroMap` (BIT 1583) | `a7q2-HaveItem`, extra `a7q3-SpawnSilk` | `.it`: `is_map = true`; removed only by `a7q3-DeliveredItem` |
| 4 | (opt.) Helena on the map | `2_7_town` | NPCTalk 1078 "Maligaro's Map" | `a7q2-MapClue2` | |
| 5 | Use the map in the device | `2_7_5_1` | use `Metadata/QuestObjects/Act7/MaligaroOrrery` with the map | `a7q2-DeliveredItem` | server script; setter not in data; orrery transitions, portals open |
| 6 | Enter the Sanctum | `2_7_5_map` | portal (`MaligaroPortalTransition`) | `Visited-7_5_map` | hides the orrery minimap icon |
| 7 | See Maligaro | `2_7_5_map` | MV 5692 within range | `a7q2-SeenBoss` | |
| 8 | Kill Maligaro | `2_7_5_map` | kill MV 5692 `Metadata/Monsters/Maligaro/Maligaro2` | `a7q2-KilledBoss` | also kill Black Death (a7q3) |
| 9 | Sin takes the soul | `2_7_5_map` | `SinMaligaro` NPCTalk 1137 "Maligaro" | `SinMaligaroReward` | no Pantheon; SinTown 1108 sets it too |
| 10 | Helena | `2_7_town` | NPCTalk 1079 "Maligaro" | `HelenaOnMaligaroDead` | required by 1080 |
| 11 | Reward | `2_7_town` | NPCTalk 1080 "Maligaro Reward", offer 47 | `a7q2-QuestRewardSeen`, `a7q2-QuestRewardTaken` | |

### Objects, NPCs and bosses
| What | Type / MV / item | Where | Server today | Needed |
|---|---|---|---|---|
| Map chest | `Metadata/Chests/QuestChests/MaligaroMapChest` (Chests 209 → BIT 1583); `.ot` not unpacked, not in `object-types.json` (assumed QuestChest like RhoaChest) | short room line `act7/area4/rooms/unique/maligarochest_01.arm` (91, 115, rot 0.87); room placed at seed 1 (node `maligarochest`) | not created | chest → item drop (a1q4 nest pattern) |
| Reverie Device button | `Metadata/QuestObjects/Act7/MaligaroOrrery` (Positioned, BaseEvents, Animated, Transitionable, MinimapIcon, InteractionAction; Targetable disabled by `a7q3-DeliveredItem`) | tile `Temple/Features/temple_maporrery4.tdt` record `temple_maporrery4_potralbutton` | not created | interact with map held → `a7q2-DeliveredItem`, transition, open portals |
| Orrery | `…/MaligaroOrrery_Main` (extends `QuestObjects/MapRoom/Orrery`: StateMachine, MinimapIcon) | same tile, record `temple_maporrery4_orrery` | not created | display only (StateMachine state) |
| Sanctum portals | `Metadata/QuestObjects/Act7/MaligaroPortalTransition` (AreaTransition + Transitionable, icon 28) | not in any room/tile: spawned by the device | none | spawn after step 5 (destination `2_7_5_map`); not in `object-types.json` |
| Maligaro | MV 5692 (boss bar) | `Area5Map/Rooms/boss.arm` `mapboss1` | NOT placed (no `mapboss`) | place at `mapboss1` |
| Black Death / Fidelitas | MV 5698 / MV 5700 | same room `mapboss2` / `mapboss3` | NOT placed | place (Black Death drops the venom) |
| Arena door | `MiscellaneousObjects/AnimateReversible` (maligarodoor.ao) | `boss.arm` (184, 138) | — | not needed for the driver |
| Sin | `Metadata/NPC/Act7/SinMaligaro` (extends SinBossNoPantheon) | spawned at the kill | not created | spawn (or use SinTown 1108) |

### Gates
- `2_7_5_map` itself has only `a6q1-KilledBoss`; the way in is the device portal. The client should only offer the
  portals after `a7q2-DeliveredItem`.

### Rewards
- Offer 47 `a7q2`, taken `a7q2-QuestRewardTaken`: class boots at item level 53, rarity 1 and 2 (Goliath Greaves,
  Soldier, Wyrmscale, Conjurer, Carnal, Shagreen Boots by class). None can be built today (magic/rare).

### Quest log check
- 14 `Visited-7_town` → talk to Sin; 13 `a7q2-QuestReceived` → talk to Helena
- 12 `a7q2-MapClue` → find the map in the Fellshrine Crypt; 11 + `Visited-7_4`
- 10 `a7q2-HaveItem` → find Maligaro in the Chamber of Sins; 9 + `a7q2-MapClue2` → use the map in the device;
  8 + `Visited-7_5_1`; 7 + both; 6 `a7q2-HaveItem` + `a7q3-QuestReceived` → use the map
- 5 `a7q2-DeliveredItem` → enter the Sanctum; 4 `Visited-7_5_map` → find Maligaro; 3 `a7q2-SeenBoss` → kill him
- 2 `a7q2-KilledBoss` → talk to Helena; 1 `a7q2-QuestRewardSeen`; 0 `a7q2-QuestRewardTaken`

### Driver steps (draft)
```python
[
    {"verb": "talk", "npc": "Sin", "option": "Inquisitor Maligaro", "skip_if": "a7q2-QuestReceived"},
    {"verb": "talk", "npc": "Helena", "option": "Maligaro", "skip_if": "a7q2-MapClue"},
    {"verb": "goto", "area": "2_7_4", "skip_if": "a7q2-HaveItem"},
    {"verb": "interact", "target": "MaligaroMapChest", "skip_if": "a7q2-HaveItem"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act7/MaligaroMap", "skip_if": "a7q2-HaveItem"},
    {"verb": "goto", "area": "2_7_5_1", "skip_if": "a7q2-DeliveredItem"},
    {"verb": "talk", "npc": "Silk", "option": "Black Venom", "skip_if": "a7q3-QuestReceived"},
    {"verb": "interact", "target": "MaligaroOrrery", "skip_if": "a7q2-DeliveredItem"},
    {"verb": "interact", "target": "MaligaroPortalTransition", "skip_if": "a7q2-KilledBoss"},
    {"verb": "kill", "target": "Black Death, Pain Unending", "skip_if": "a7q3-HaveItem"},
    {"verb": "kill", "target": "Maligaro, the Artist", "skip_if": "a7q2-KilledBoss"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act7/BlackVenom", "skip_if": "a7q3-HaveItem"},
    {"verb": "talk", "npc": "Sin", "option": "Maligaro", "skip_if": "SinMaligaroReward"},
    {"verb": "talk", "npc": "Helena", "option": "Maligaro", "skip_if": "HelenaOnMaligaroDead"},
    {"verb": "reward", "npc": "Helena", "option": "Maligaro Reward", "skip_if": "a7q2-QuestRewardTaken"},
]
```

### Risks
- Maligaro's Sanctum is a map-like area entered by a device: the orrery interaction, the portal spawning and the
  map-device behaviour (the real client may open a map-device window for `is_map` items) are new server work.
- Maligaro/Black Death/Fidelitas are not placed (`mapboss1..3`); the fight is scripted (closing door
  `AnimateReversible`, `ArenaMiddle`).
- The map chest is a short room line not reported by `questobjects`; its object type is not in `object-types.json`.
- Who sets `a7q2-DeliveredItem` is not in the client data (server script; inferred from QS 836 "You have opened the
  portals using his Map").

## a7q3 "Web of Secrets" (Quest row 79, main)

**Start:** Silk in The Chamber of Sins Level 1 ("Black Venom"). **Finish:** Silk's reward, the Obsidian Key.
**DONE flag (driver):** `a7q1-UsedKey` (QS 846 Order 0: the key used at the secret passage; Order 1 is `a7q1-HaveKey`).
**Prerequisites:** Silk is visible only with `a7q3-SpawnSilk` (picking up Maligaro's Map); Black Death is in Maligaro's
Sanctum (a7q2).

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Silk appears | `2_7_5_1` | `SilkSins` gated by `block_quest_flag = a7q3-SpawnSilk`, `unblock_quest_flag = a7q1-UsedKey` | — | client-side visibility (assumed: shown from the map pickup until the key is used) |
| 2 | Get the quest | `2_7_5_1` | SilkSins NPCTalk 1046 "Black Venom" | `a7q3-QuestReceived` | |
| 3 | Kill Black Death + Maligaro | `2_7_5_map` | kill MV 5698, MV 5692 | `a7q2-SeenBoss`, `a7q2-KilledBoss` | |
| 4 | Take the venom | `2_7_5_map` | pick up `Metadata/Items/QuestItems/Act7/BlackVenom` (BIT 1588) | `a7q3-HaveItem`, extra `a7q3-SpawnSilk` | dropper not in the data (wiki/QS: Black Death) |
| 5 | Deliver | `2_7_5_1` | NPCTalk 1047 "Black Venom" | `a7q3-DeliveredItem` | Script `RemoveQuestItem("…/Act7/MaligaroMap")`; `.it` removes venom and map |
| 6 | Reward | `2_7_5_1` | NPCTalk 1048 "Black Death Reward", offer 48 | `a7q3-QuestRewardSeen`; key pickup → `a7q1-HaveKey` | |
| 7 | Barrier to L2 drops | `2_7_5_1` | `MaligaroSinsBarrier` transitions | `a7q2-ClearedSinBlock` | setter not in data (see Risks) |
| 8 | Use the key | `2_7_5_2` | use `MaligaroPassageCover` holding the key | `a7q1-UsedKey` | key removed (`remove_flag`), cover transitions, `MaligaroPassage` enabled |

### Objects, NPCs and bosses
| What | Type / MV / item | Where | Server today | Needed |
|---|---|---|---|---|
| Silk | `Metadata/NPC/Act7/SilkSins` (NPC, MinimapIcon) | room `act7/area5level1/rooms/orrery.arm` (181, 92) | NOT created (out of town) | create the NPC with its Positioned quest flags |
| Black Death | MV 5698 | `boss.arm` `mapboss2` | NOT placed | place; drop BlackVenom on death |
| Obsidian Key | `Metadata/Items/QuestItems/Act7/ObsidianKey` (BIT 1584) | offer 48 (QuestRewards 1065) | quest-item reward not built | reward offer of a quest item |
| Barrier | `Metadata/QuestObjects/Act7/MaligaroSinsBarrier` (Positioned, BaseEvents, Animated, Transitionable, MinimapIcon `show_flag a7q1-HaveKey`, `hide_flag a7q2-ClearedSinBlock`); not in `object-types.json` | room lines `exit_strairsdown_1a/2a/3a.arm` (137, 68) | not created | create; transition on `a7q2-ClearedSinBlock` |
| Barrier blockers ×9 | `…/MaligaroSinsBarrierBlocker` (`blocking`, `unblock_quest_flag = a7q2minusClearedSinBlock`, no_serialise) | same rooms | client-side | QuestBarriers until the flag |
| Passage cover | `…/MaligaroPassageCover` (InteractionAction, Transitionable, MinimapIcon hide on UsedKey, "SecretPassage") | tile `RuinedTempleForest/Hidden/templeruinforest_maligaro_passage.tdt` record `…_secret_passage` | not created | interact with key → `a7q1-UsedKey` |
| Passage | `…/MaligaroPassage` (AreaTransition + Transitionable, `show_flag a7q1-UsedKey`) | same tile, record `…_area_transition` → `2_7_6` | not created (quest transition) | create untransitioned; transition on UsedKey |

### Gates
- `2_7_5_2` needs `a7q2-ClearedSinBlock` (client); `2_7_6`/`2_7_7` need `a7q1-UsedKey`.
- Client blockers: 9 `MaligaroSinsBarrierBlocker` per stairs room (`a7q2-ClearedSinBlock`).

### Rewards
- Offer 48 `a7q3`: the Obsidian Key (quest item, level 1); no QuestStaticRewards row. BaseItemTypes row 1597
  `Metadata/Items/QuestItems/Act7/Book-a7q3` exists but no offer, no `.it` file and no QuestStaticRewards row use it:
  a leftover; the wiki agrees the reward is the key.

### Quest log check
- 7 `a7q3-QuestReceived` → open Maligaro's Map, find the venom; 6 + `a7q2-DeliveredItem` → search the Sanctum
- 5 `a7q2-SeenBoss` → kill Maligaro and Black Death; 4 `a7q2-KilledBoss` → take the venom
- 3 `a7q3-HaveItem` → bring it to Silk; 2 `a7q3-DeliveredItem` → talk to Silk
- 1 `a7q1-HaveKey` → complete (key); 0 `a7q1-UsedKey` → complete

### Driver steps (draft)
```python
[
    # Silk talk and the Sanctum fight are in a7q2's steps
    {"verb": "goto", "area": "2_7_5_1", "skip_if": "a7q1-HaveKey"},
    {"verb": "talk", "npc": "Silk", "option": "Black Venom", "skip_if": "a7q3-DeliveredItem"},
    {"verb": "reward", "npc": "Silk", "option": "Black Death Reward", "pick": ["Obsidian Key"],
     "skip_if": "a7q1-HaveKey"},
    {"verb": "wait_flag", "flag": "a7q2-ClearedSinBlock"},
    {"verb": "goto", "area": "2_7_5_2", "skip_if": "a7q1-UsedKey"},
    {"verb": "interact", "target": "MaligaroPassageCover", "skip_if": "a7q1-UsedKey"},
]
```

### Risks
- Who sets `a7q2-ClearedSinBlock` is unknown: the barrier icon shows with `a7q1-HaveKey` and hides with the flag, so
  it is probably set when the player with the key comes to the barrier (or by Silk's reward). The flag is in
  `EpilogueAtlasDemo` but not in `E3EndOfAct8` (which does contain `Visited-7_5_2`). Proposal: set it on proximity
  with `a7q1-HaveKey`, and save it.
- `SilkSins` visibility semantics of `block_quest_flag` on an NPC are unverified.
- Quest-item reward offers (Obsidian Key here, Greust's Necklace in a7q8) are not built by `QuestRewardCatalog`.

## a7q4 "The Mother of Spiders" (Quest row 80, main; ends the act)

**Start:** YeenaTown "Silk" (cat 4, sets the quest). **Finish:** enter The Sarn Ramparts.
**DONE flag (driver):** `Visited-8_1` (QS 854 Order 0). Optional: SinTown "Arakaali" sets `a7q4-QuestRewardTaken`
(no item).
**Prerequisites:** `a7q3-DeliveredItem` for the talk; `a7q7-DeliveredItems` for the Temple of Decay.

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Get the quest | `2_7_town` | YeenaTown NPCTalk 1030 "Silk" | `a7q4-QuestReceived` (QF4+QF5) | |
| 2 | (opt.) Eramir | `2_7_town` | NPCTalk 1060 "Silk" | `EramirOnSilkFound` | log variant; 1063 sets `EramirOnArakaaliTemple` |
| 3 | See the webbed temple | `2_7_11` | come near the temple entrance | `a7q4-SeenBlockage` | setter not in data (assumed proximity); enables Yeena's a7q7 row 1037 |
| 4 | Fireflies to Yeena | `2_7_11` | see a7q7 | `a7q7-DeliveredItems` | opens the temple |
| 5 | Enter the temple | `2_7_12_1` → `2_7_12_2` | enter | `Visited-7_12` (from 2_7_12_1) | |
| 6 | See Arakaali | `2_7_12_2` | MV 5740 within range | `a7q4-SeenBoss` | |
| 7 | Kill Arakaali | `2_7_12_2` | kill MV 5740 `Metadata/Monsters/VaalSpiderGod/Arakaali` | `a7q4-KilledBoss` | Pantheon Arakaali (major) |
| 8 | Sin | `2_7_12_2` | `SinArakaali` NPCTalk 1135 "Arakaali" | `SinArakaaliReward` | Pantheon screen; SinArakaali removed by `Visited-8_1` |
| 9 | (opt.) Yeena | `2_7_12_2` | `YeenaArakaali` NPCTalk 1044 | `YeenaOnArakaaliDead` | |
| 10 | (opt.) Sin in town | `2_7_town` | SinTown NPCTalk 1107 "Arakaali" | `a7q4-QuestRewardTaken` | no reward offer |
| 11 | Act 8 | `2_7_12_2` → `2_8_1` | area transition across the bridge | `Visited-8_1` | `2_8_1` gate `a7q4-KilledBoss` |

### Objects, NPCs and bosses
| What | Type / MV / item | Where | Server today | Needed |
|---|---|---|---|---|
| Arakaali | MV 5740 (boss bar) | WA Bosses; `boss.arm` `mapboss` | placed (spawnable) | targetable; no phases |
| Arena | `ArakaaliSequencer`, `ArakaaliTurret_A`/`_B` ×13 each (StateMachine, Transitionable, Timer client-only), 26 `ArakaaliTurretDisplay`, `ArakaaliArenaMiddle`, 4 `TeleportMarkerSerialized1..4`, `ArakaaliLaserTurretDaemon` | `boss.arm` | not created | not needed for `/kill` |
| Silk | `Metadata/NPC/Act7/SilkArakaali` (not targetable, Transitionable: anim 0 → 1) | `boss.arm` (149, 256) | NOT created | optional (story) |
| Yeena / fox | `YeenaFox` (`Metadata/Monsters/FireFox/YeenaFox`), `YeenaPositionMarker` | short lines in `boss.arm` (166, 115) / (168, 208) | not created | optional |
| Bridge | `Terrain/Act7/Area12Level2/Objects/Arakaali_Bridge` (Transitionable + TriggerableBlockage `start_blocked`) and `Arakaali_Bridge_Transition` | `boss.arm` (149, 241) / (149, 80) | not created | create; transition (unblock) on `a7q4-KilledBoss` |
| Sin | `Metadata/NPC/Act7/SinArakaali` (NPC, Transitionable; `unblock_quest_flag Visitedminus8_1`) | spawned at the kill | not created | spawn at the kill |
| Yeena after the fight | `Metadata/NPC/Act7/YeenaArakaali` | not in any room/tile | not created | optional spawn |

### Gates
- `2_8_1`, `2_8_town`, `2_8_2_1`, `2_8_2_2` need `a7q4-KilledBoss` (client); Gateway `FlagToEnter` for `2_8_1`.
- `2_7_12_1`/`2_7_12_2` need `a7q7-DeliveredItems`.
- Act 7 → 8: the exit to `2_8_1` is in `2_7_12_2` past Arakaali's bridge. The exact transition record was not
  identified (the boss sub-graph `_tomb_spidergod_boss_01.dgr`; `questobjects` groups all `AreaTransition` records of
  the level under one line). No NPC transports the player: it is a normal area transition, gated by
  `a7q4-KilledBoss`.

### Rewards
- No offer. Optional SinTown 1107 sets `a7q4-QuestRewardTaken` without items.

### Quest log check
- 9 `a7q4-QuestReceived`; 8 + `EramirOnSilkFound`; 7 + `a7q1-UsedKey`; 6 + both → find Silk / Arakaali's temple
- 5 `a7q4-SeenBlockage` → get past the webs; 4 `a7q7-DeliveredItems` → enter the temple
- 3 `Visited-7_12` → search the Temple of Decay; 2 `a7q4-SeenBoss` → kill Arakaali
- 1 `a7q4-KilledBoss` → the exit to Act 8 is open; 0 `Visited-8_1` → complete

### Driver steps (draft)
```python
[
    {"verb": "talk", "npc": "Yeena", "option": "Silk", "skip_if": "a7q4-QuestReceived"},
    # a7q7 (fireflies) must be done first
    {"verb": "goto", "area": "2_7_12_2", "skip_if": "a7q4-KilledBoss"},
    {"verb": "kill", "target": "Arakaali, Spinner of Shadows", "skip_if": "a7q4-KilledBoss"},
    {"verb": "talk", "npc": "Sin", "option": "Arakaali", "skip_if": "SinArakaaliReward"},
    {"verb": "talk", "npc": "Sin", "option": "Arakaali", "skip_if": "a7q4-QuestRewardTaken"},  # SinTown 1107
    {"verb": "goto", "area": "2_8_1", "via": "transition", "skip_if": "Visited-8_1"},
]
```

### Risks
- Arakaali's real fight is scripted (sequencer, turrets, teleports, invulnerable web phases, Silk's transformation);
  the driver only needs her targetable at `mapboss`.
- The bridge (`TriggerableBlockage start_blocked`) must open on the kill, else the exit to Act 8 is unreachable on
  foot; the exit transition record must be confirmed on a generated layout.
- `SinArakaali` disappears with `Visited-8_1`: talk to it before leaving (SinTown 1107 only sets the reward flag;
  the Pantheon flag `SinArakaaliReward` comes from 1135 or the Pantheon screen).

## a7q5 "The Silver Locket" (Quest row 81, side)

**Start:** Weylam "The Silver Locket" (needs `WeylamOnBrokenBridge`). **Finish:** Weylam "Silver Locket Reward".
**DONE flag (driver):** `a7q5-QuestRewardTaken`.
**Prerequisites:** Weylam NPCTalk 1011 "The Broken Bridge" (sets `WeylamOnBrokenBridge`).

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Intro | `2_7_town` | Weylam NPCTalk 1011 "The Broken Bridge" | `WeylamOnBrokenBridge` | |
| 2 | Get the quest | `2_7_town` | NPCTalk 1017 "The Silver Locket" | `a7q5-QuestReceived` | |
| 3 | Find the lockbox | `2_7_1` | open `Metadata/Chests/QuestChests/LocketChest` (Chests 210 "Dirty Lockbox") → `Metadata/Items/QuestItems/Act7/SilverLocket` (BIT 1586) | `a7q5-HaveItem` | |
| 4 | Deliver | `2_7_town` | NPCTalk 1018 "The Silver Locket" | `a7q5-DeliveredItem` | `.it` removes the locket |
| 5 | Reward | `2_7_town` | NPCTalk 1019 "Silver Locket Reward", offer 49 | `a7q5-QuestRewardSeen`, `a7q5-QuestRewardTaken` | |

### Objects, NPCs and bosses
| What | Type | Where | Server today | Needed |
|---|---|---|---|---|
| Lockbox | `Metadata/Chests/QuestChests/LocketChest` (Chests 210 → BIT 1586); not in `object-types.json` | short line `act7/area1/rooms/manor.arm` (276, 282, rot 0); room placed at seed 1 (node `manor`, `field_ruins_*.tgr`) | not created | chest → item drop |

### Gates
None.

### Rewards
- Offer 49 `a7q5`, taken `a7q5-QuestRewardTaken`: flasks at item level 54 — magic (rarity 1) Diamond, Granite, Jade,
  Sulphur, Quartz, Aquamarine, Corundum (`FlaskUtility1/5/9/13/8/11/17`) and normal (rarity 0) Quartz, Aquamarine,
  Corundum (`FlaskUtility8/11/17`, QuestRewards 1073–1075). The three normal flasks can be built today.

### Quest log check
- 4 `a7q5-QuestReceived` → find the locket; 3 `a7q5-HaveItem` → deliver; 2 `a7q5-DeliveredItem` → talk for reward
- 1 `a7q5-QuestRewardSeen`; 0 `a7q5-QuestRewardTaken`

### Driver steps (draft)
```python
[
    {"verb": "talk", "npc": "Weylam", "option": "The Broken Bridge", "skip_if": "WeylamOnBrokenBridge"},
    {"verb": "talk", "npc": "Weylam", "option": "The Silver Locket", "skip_if": "a7q5-QuestReceived"},
    {"verb": "goto", "area": "2_7_1", "skip_if": "a7q5-HaveItem"},
    {"verb": "interact", "target": "LocketChest", "skip_if": "a7q5-HaveItem"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act7/SilverLocket", "skip_if": "a7q5-HaveItem"},
    {"verb": "talk", "npc": "Weylam", "option": "The Silver Locket", "skip_if": "a7q5-DeliveredItem"},
    {"verb": "reward", "npc": "Weylam", "option": "Silver Locket Reward", "pick": ["Quartz Flask"],
     "skip_if": "a7q5-QuestRewardTaken"},
]
```

### Risks
- The lockbox is a short room line (not in `questobjects`); `manor.arm` is a special node in only some graphs —
  check it is placed for every seed the driver uses.

## a7q6 "Kishara's Star" (Quest row 82, side)

**Start:** Weylam "Kishara's Star" (needs `a7q1-KilledBoss`). **Finish:** Weylam "Kishara's Star Reward" (book).
**DONE flag (driver):** `a7q6-UsedRewardBook`.
**Prerequisites:** `a7q1-KilledBoss` (talk and The Causeway gate).

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Get the quest | `2_7_town` | Weylam NPCTalk 1020 "Kishara's Star" | `a7q6-QuestReceived` | 1023 "Kishara" → `WeylamOnKisharaStar` |
| 2 | Find the lockbox | `2_7_10` | open `Metadata/Chests/QuestChests/KisharaStarChest` (Chests 211 "Kishara's Lockbox") → `Metadata/Items/QuestItems/Act7/KisharaStar` (BIT 1585) | `a7q6-HaveItem` | |
| 3 | Deliver | `2_7_town` | NPCTalk 1021 "Kishara's Star" | `a7q6-DeliveredItem` | |
| 4 | Reward | `2_7_town` | NPCTalk 1022 "Kishara's Star Reward", offer 50 | `a7q6-QuestRewardSeen` | Book-a7q6 |
| 5 | Use the book | any | use `Metadata/Items/QuestItems/Act7/Book-a7q6` | `a7q6-HaveRewardBook` → `a7q6-UsedRewardBook` | QSR 20: 1 passive + 2 respec |

### Objects, NPCs and bosses
| What | Type | Where | Server today | Needed |
|---|---|---|---|---|
| Kishara's Lockbox | `Metadata/Chests/QuestChests/KisharaStarChest` (Chests 211 → BIT 1585); not in `object-types.json` | short line `act7/area10/rooms/unique/city.arm` (289, 44); room placed at seed 1 (node `city`) | not created | chest → item drop |

### Gates
`2_7_10` needs `a7q1-KilledBoss`.

### Rewards
- Offer 50 `a7q6` (QuestRewards 1076): `Book-a7q6` (`AbstractPassiveSkillAnd2RespecsBook`).
- QuestStaticRewards 20: `a7q6-UsedRewardBook` → 1 passive point (`col1`) and 2 respec points (`col7`).

### Quest log check
- 5 `a7q6-QuestReceived` → travel to the Causeway; 4 + `Visited-7_10` → find the Star
- 3 `a7q6-HaveItem` → deliver; 2 `a7q6-DeliveredItem` → talk for reward; 1 `a7q6-HaveRewardBook`;
  0 `a7q6-UsedRewardBook`

### Driver steps (draft)
```python
[
    {"verb": "talk", "npc": "Weylam", "option": "Kishara's Star", "skip_if": "a7q6-QuestReceived"},
    {"verb": "goto", "area": "2_7_10", "skip_if": "a7q6-HaveItem"},
    {"verb": "interact", "target": "KisharaStarChest", "skip_if": "a7q6-HaveItem"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act7/KisharaStar", "skip_if": "a7q6-HaveItem"},
    {"verb": "talk", "npc": "Weylam", "option": "Kishara's Star", "skip_if": "a7q6-DeliveredItem"},
    {"verb": "reward", "npc": "Weylam", "option": "Kishara's Star Reward", "skip_if": "a7q6-QuestRewardSeen"},
    {"verb": "use_item", "item": "Book-a7q6", "skip_if": "a7q6-UsedRewardBook"},  # verb not in CONTRACT yet
]
```

### Risks
- Short-line chest (see a7q5). Weylam has two options named "Kishara's Star" (1020 take, 1021 deliver): the driver
  must pick the one the client shows (they are mutually exclusive by flags).

## a7q7 "Lighting the Way" (Quest row 83, main)

**Start:** YeenaTown "Fireflies" (needs `a7q4-SeenBlockage`), YeenaVaalCity "Fireflies" (no needs), or picking up the
first Firefly (wiki; the HaveItem states need no QuestReceived). **Finish:** YeenaVaalCity "Fireflies" with all seven.
**DONE flag (driver):** `a7q7-DeliveredItems`.
**Prerequisites:** `a7q1-KilledBoss` (The Dread Thicket and The Vaal City gates).

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Get the quest | `2_7_town` / `2_7_11` | YeenaTown 1037 or YeenaVaalCity 1043 "Fireflies" | `a7q7-QuestReceived` | |
| 2 | Collect 7 fireflies | `2_7_9` | open `Metadata/Chests/QuestChests/Fireflies/FireflyChest1`…`7` (Chests 212–218) → `Metadata/Items/QuestItems/Act7/Firefly1`…`7` (BIT 1589–1595) | `a7q7-HaveItem1`…`7` | chests are not in any room/tile |
| 3 | (opt.) Yeena in town | `2_7_town` | YeenaTown 1038 "Arakaali's Temple" (needs all 7) | `a7q7-DeliveryClue` | log only |
| 4 | Deliver | `2_7_11` | YeenaVaalCity NPCTalk 1042 "Fireflies" | `a7q7-DeliveredItems` (QF4+QF5), script sets `a7q7-YeenaTransform` | `.it` removes all fireflies |
| 5 | Web burns | `2_7_11` | script: Yeena `DirtyTransition()`, every `ArakaaliTempleEntranceBlock` transitions, `temple_entrance.DirtyTransition()` | — | `TempleOfDecayEntrance` becomes usable (Timer 9.3 s, client) |

NPCTalk 1042 Script (full): `triggerer.SetQuestFlag("a7q7-YeenaTransform"); triggerer.FinishNPCChat();
DirtyTransition(); ForEachOfType( "Metadata/QuestObjects/Act7/ArakaaliTempleEntranceBlock", { DirtyTransition(); } );
temple_entrance.DirtyTransition();`

### Objects, NPCs and bosses
| What | Type | Where | Server today | Needed |
|---|---|---|---|---|
| Fireflies ×7 | `FireflyChest1..7` (extend `FireflyChestBase` → `QuestChest`: Chest, Transitionable, MinimapIcon … all known components) | not in room/tile data nor in `room-points.json` | not created | server picks 7 spots in `2_7_9` (seed-independent rule needed); chest → item drop |
| Yeena (Vaal City) | `Metadata/NPC/Act7/YeenaVaalCity` (NPC + Transitionable; `unblock_quest_flag a7q7minusDeliveredItems`) | room `act7/area11/rooms/unique/temple_entrance_banteay_srei.arm` (152, 101) | NOT created (out of town) | create; transition on delivery; gone after |
| Web | `Metadata/QuestObjects/Act7/ArakaaliTempleEntranceBlock` (Positioned size 0, BaseEvents, Animated, Transitionable) | same room (152, 127) | not created | create; state transitioned with `a7q7-DeliveredItems` |
| Temple entrance | `Metadata/QuestObjects/Act7/TempleOfDecayEntrance` (AreaTransition + Transitionable; Timer client-only) | tile `DungeonWeb/Features/BanteaySrei_Web.tdt` record `BanteaySrei_Web_decay_entrance` → `2_7_12_1` | not created (quest transition) | create; untransitioned = not targetable; transition on delivery |

### Gates
`2_7_12_1` and `2_7_12_2` need `a7q7-DeliveredItems` (client); the Gateway should refuse them too.

### Rewards
None.

### Quest log check
- 11 `a7q7-QuestReceived` → search the Dread Thicket; 10..4 one of `a7q7-HaveItem7..1` → remaining fireflies
- 3 all seven → deliver to Yeena in town; 2 all seven + `a7q7-QuestReceived` → deliver at the temple;
  1 all seven + `a7q7-DeliveryClue` → same; 0 `a7q7-DeliveredItems` → complete

### Driver steps (draft)
```python
[
    {"verb": "goto", "area": "2_7_9", "skip_if": "a7q7-DeliveredItems"},
] + [
    step
    for n in range(1, 8)
    for step in (
        {"verb": "interact", "target": f"FireflyChest{n}", "skip_if": f"a7q7-HaveItem{n}"},
        {"verb": "pickup", "item": f"Metadata/Items/QuestItems/Act7/Firefly{n}", "skip_if": f"a7q7-HaveItem{n}"},
    )
] + [
    {"verb": "goto", "area": "2_7_11", "skip_if": "a7q7-DeliveredItems"},
    {"verb": "talk", "npc": "Yeena", "option": "Fireflies", "skip_if": "a7q7-DeliveredItems"},
]
```

### Risks
- The fireflies have no placement data at all (no room/tile line, no room point): the real server places them;
  the implementer needs a placement rule (e.g. walkable cells away from the entrance).
- YeenaVaalCity is an out-of-town NPC with a scripted transition (`DirtyTransition` on herself, the web and the
  entrance) — `ForEachOfType` and the named `temple_entrance` are server-script constructs.

## a7q8 "In Memory of Greust" (Quest row 84, side)

**Start:** Helena "Greust's Necklace" (needs `a7q1-KilledBoss`). **Finish:** Helena "Greust's Necklace Reward".
**DONE flag (driver):** `a7q8-QuestRewardTaken`.
**Prerequisites:** `a7q1-KilledBoss`.

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | (opt.) see the shrine | `2_7_8` | come near `GreustShrine` | `a7q8-SeenShrine` | QF 161; setter not in data |
| 2 | Get the quest | `2_7_town` | Helena NPCTalk 1087 "Greust's Necklace" | `a7q8-QuestReceived` | |
| 3 | Take the necklace | `2_7_town` | NPCTalk 1088 "Take Greust's Necklace", offer 52 | `a7q8-SeenItem`; pickup → `a7q8-HaveItem` | quest item via reward window |
| 4 | Place it | `2_7_8` | use `Metadata/QuestObjects/Act7/GreustShrine` holding the necklace | `a7q8-DeliveredItem` | necklace removed; shrine transitions (not targetable) |
| 5 | Helena | `2_7_town` | NPCTalk 1089 "Greust's Necklace" | `a7q8-Congratulated` | |
| 6 | Reward | `2_7_town` | NPCTalk 1090 "Greust's Necklace Reward", offer 51 | `a7q8-QuestRewardSeen`, `a7q8-QuestRewardTaken` | |

### Objects, NPCs and bosses
| What | Type | Where | Server today | Needed |
|---|---|---|---|---|
| Azmeri shrine | `Metadata/QuestObjects/Act7/GreustShrine` (Positioned static, InteractionAction, BaseEvents, Animated, Transitionable, MinimapIcon `show_flag a7q8-HaveItem`, `hide_flag a7q8-DeliveredItem`); not in `object-types.json` | tile `Forest/Features/forest_azmeri_shrine.tdt` record `forest_azmeri_shrine_clickable_bearhead` (cell 23, 50) | not created | create; interact with necklace → flag, transition |
| Necklace | `Metadata/Items/QuestItems/Act7/GreustNecklace` (BIT 1587) | offer 52 (QuestRewards 1085) | quest-item reward not built | reward offer of a quest item |
| (act 2) disabler | `GreustShrineDisabler` | room `act2/area8/rooms/azmeri_quest_shrine.arm` | — | none (keeps the act 2 shrine inert) |

### Gates
None.

### Rewards
- Offer 52 `a7q8_offer`: Greust's Necklace (quest item, no taken flag; NPCTalk 1088 excluded by HaveItem).
- Offer 51 `a7q8`, taken `a7q8-QuestRewardTaken`: amulets at item level 57 — rarity 2 Gold, Onyx, Turquoise, Agate,
  Citrine; rarity 1 Turquoise, Agate, Citrine. None can be built today.

### Quest log check
- 6 `a7q8-SeenShrine` → ask Helena; 5 `a7q8-QuestReceived` → take the necklace; 4 `a7q8-HaveItem` → take it to the
  shrine; 3 + `a7q8-SeenShrine` → place it; 2 `a7q8-DeliveredItem` → talk to Helena; 1 `a7q8-QuestRewardSeen`;
  0 `a7q8-QuestRewardTaken`

### Driver steps (draft)
```python
[
    {"verb": "talk", "npc": "Helena", "option": "Greust's Necklace", "skip_if": "a7q8-QuestReceived"},
    {"verb": "reward", "npc": "Helena", "option": "Take Greust's Necklace", "pick": ["Greust's Necklace"],
     "skip_if": "a7q8-HaveItem"},
    {"verb": "goto", "area": "2_7_8", "skip_if": "a7q8-DeliveredItem"},
    {"verb": "interact", "target": "GreustShrine", "skip_if": "a7q8-DeliveredItem"},
    {"verb": "talk", "npc": "Helena", "option": "Greust's Necklace", "skip_if": "a7q8-Congratulated"},
    {"verb": "reward", "npc": "Helena", "option": "Greust's Necklace Reward", "skip_if": "a7q8-QuestRewardTaken"},
]
```

### Risks
- Two Helena options are named "Greust's Necklace" (1087 and 1089), exclusive by flags.
- The amulet reward needs magic/rare item construction.

## a7q9 "Queen of Despair" (Quest row 85, side)

**Start:** SinTown "Gruthkul" (needs `a7q1-KilledBoss`). **Finish:** Eramir "Gruthkul Reward" (book).
**DONE flag (driver):** `a7q9-UsedRewardBook`.
**Prerequisites:** `a7q1-KilledBoss`.

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Get the quest | `2_7_town` | SinTown NPCTalk 1102 "Gruthkul" | `a7q9-QuestReceived` | 1103 / Eramir 1066 lore |
| 2 | See Gruthkul | `2_7_9` | MV 6047 within range | `a7q9-SeenBoss` | |
| 3 | Kill Gruthkul | `2_7_9` | kill MV 6047 `Metadata/Monsters/DespairBear/DespairBear` | `a7q9-KilledBoss` | Pantheon Gruthkul (minor) |
| 4 | Sin | `2_7_9` | `SinGruthkul` NPCTalk 1132 "Gruthkul" | `SinGruthkulReward` | Pantheon screen; SinTown 1104 also |
| 5 | Eramir | `2_7_town` | NPCTalk 1067 "Gruthkul" | `EramirGruthkulReward` | removes `SinGruthkul` |
| 6 | Reward | `2_7_town` | NPCTalk 1068 "Gruthkul Reward", offer 53 | `a7q9-SeenQuestReward` | Book-a7q9 |
| 7 | Use the book | any | use `Book-a7q9` | `a7q9-HaveRewardBook` → `a7q9-UsedRewardBook` | QSR 19: 1 passive |

### Objects, NPCs and bosses
| What | Type / MV | Where | Server today | Needed |
|---|---|---|---|---|
| Gruthkul | MV 6047 (boss bar) | WA Bosses `2_7_9`; `despair_boss.arm` point `mapboss1` (74, 189) | NOT placed (no `mapboss`, room not in room-points) | place at `mapboss1` |
| Ground cracks | `DespairBear/BearCrack` ×9 (Transitionable), `BearCrackInert` ×10 | `despair_boss.arm` | not created | not needed for `/kill` |
| Sin | `Metadata/NPC/Act7/SinGruthkul` (SinBossDeathParent; `unblock_quest_flag EramirGruthkulReward`) | spawned at the kill | not created | spawn (or SinTown 1104) |

### Gates
`2_7_9` needs `a7q1-KilledBoss`.

### Rewards
- Offer 53 `a7q9` (QuestRewards 1086): `Book-a7q9`.
- QuestStaticRewards 19: `a7q9-UsedRewardBook` → 1 passive point.

### Quest log check
- 5 `a7q9-QuestReceived` → travel to the Dread Thicket; 4 + `Visited-7_9` → kill Gruthkul; 3 `a7q9-SeenBoss`
- 2 `a7q9-KilledBoss` → talk to Eramir; 1 `a7q9-HaveRewardBook`; 0 `a7q9-UsedRewardBook`

### Driver steps (draft)
```python
[
    {"verb": "talk", "npc": "Sin", "option": "Gruthkul", "skip_if": "a7q9-QuestReceived"},
    {"verb": "goto", "area": "2_7_9", "skip_if": "a7q9-KilledBoss"},
    {"verb": "kill", "target": "Gruthkul, Mother of Despair", "skip_if": "a7q9-KilledBoss"},
    {"verb": "talk", "npc": "Sin", "option": "Gruthkul", "skip_if": "SinGruthkulReward"},
    {"verb": "talk", "npc": "Eramir", "option": "Gruthkul", "skip_if": "EramirGruthkulReward"},
    {"verb": "reward", "npc": "Eramir", "option": "Gruthkul Reward", "skip_if": "a7q9-SeenQuestReward"},
    {"verb": "use_item", "item": "Book-a7q9", "skip_if": "a7q9-UsedRewardBook"},  # verb not in CONTRACT yet
]
```

### Risks
- Gruthkul is in a dedicated boss room with `mapboss1`: the boss placement must accept `mapboss1` (same for
  Maligaro's room).
- SinTown has two "Gruthkul" options (1102 take, 1104 reward) and Eramir two (1066, 1067), exclusive by flags.

## Pantheon flags set in act 7 (for the coordinator)

| Flag | Set by | Pantheon |
|---|---|---|
| `a7q1-KilledBoss` | Greust's death | PantheonPanelLayout Ralakesh (minor) unlock |
| `a7q4-KilledBoss` | Arakaali's death | Arakaali (major) unlock |
| `a7q9-KilledBoss` | Gruthkul's death | Gruthkul (minor) unlock |
| `SinRalakeshReward` | SinRalakesh 1130 (script `SetSessionQuestFlag(CanChangePantheon)`, opens PantheonScreen) / SinTown 1101 | soul delivered |
| `SinArakaaliReward` | SinArakaali 1135 (same script) | soul delivered |
| `SinGruthkulReward` | SinGruthkul 1132 (same script) / SinTown 1104 | soul delivered |
| `SinMaligaroReward` | SinMaligaro 1137 / SinTown 1108 | none (Maligaro soul is a map upgrade, `ArakaaliUpgrade1`) |

"Open Pantheon" rows (cat 38) need the boss flag and `a6q3-KilledBoss`, not `HardMode`. SinTown 1110–1129 "Unlock
Power" (divine vessel upgrades) are endgame.

## Implementation summary

| Quest | Main/side | Features | Risk | Est. size |
|---|---|---|---|---|
| a7q2 Essence of the Artist | main | NPC talk, chest→item drop (short line), map-device object use, spawned portals, map-like area, boss placement at `mapboss1..3`, out-of-town Sin, reward offer (magic/rare boots) | high: device + portals + unplaced bosses | L |
| a7q3 Web of Secrets | main | out-of-town NPC Silk (flag-gated), kill-drop item (Black Venom), NPC hand-in with item removal script, quest-item reward (key), barrier + client blockers, object use (passage cover) + quest transition | high: unknown `ClearedSinBlock` setter, key reward | M |
| a7q1 Master of a Million Faces | main | NPC talk (bandit variants), area gate, boss placement (Greust among 4), kill trigger, spawned Sin, book of skill | med: boss pick, Sin spawn | M |
| a7q9 Queen of Despair | side | NPC talk, boss placement at `mapboss1`, kill trigger, spawned Sin, book of skill | med: boss point | S |
| a7q8 In Memory of Greust | side | quest-item reward (necklace), object use (shrine), proximity flag, reward offer (magic/rare amulets) | med: quest-item reward, amulets | S |
| a7q6 Kishara's Star | side | chest→item drop (short line), NPC hand-in, book of skill (passive + 2 respec) | low | S |
| a7q5 The Silver Locket | side | chest→item drop (short line), NPC hand-in, reward offer (3 normal flasks buildable) | low | S |
| a7q7 Lighting the Way | main | 7 server-placed chests→items, out-of-town NPC with scripted transitions, quest transition (temple entrance), area gate | high: no placement data for fireflies | M |
| a7q4 The Mother of Spiders | main | NPC talk, proximity flag, scripted/arena boss (Arakaali) at `mapboss`, spawned Sin, bridge TriggerableBlockage, act transition to `2_8_1` | high: arena + bridge + exit record | M |
