# Act 2 quests

Act 2 takes place in the forest. The town is The Forest Encampment (`1_2_town`), with the NPCs Eramir, Greust,
Helena (`HelenaTown`), Silk and Yeena. The player arrives from Act 1's The Cavern of Anger (`1_1_11_2`) in The
Southern Forest (`1_2_1`, `Visited-2_1`). Every Act 2 area has the EndQuestFlags `a1q3-KilledSeaWitch` (Merveil, end
of Act 1), so the Act 1 file already covers the entry.

The main line is a chain of three quests:
- a2q6 Intruders in Black: kill Fidelitas in The Chamber of Sins and rescue Helena; this gets the Baleful Gem.
- a2q4 Sharp and Cruel: get Maligaro's Spike from The Weaver.
- a2q9 The Root of the Problem: poison the tree in The Wetlands with both items. `a2q9-PoisonedTree` gates The Vaal
  Ruins.

In The Vaal Ruins the Ancient Seal releases the darkness. `a2q8-UnleashedDarkness` gates The Northern Forest, The
Dread Thicket, The Caverns and The Ancient Pyramid.

a2q7 Deal with the Bandits is also required. Killing or helping the three bandit lords (Kraityn at The Broken Bridge,
Alira in The Western Forest, Oak in The Wetlands) leads to The Apex. Taking The Apex sets `a2b-SeenApexReward`, which
opens the Apex door in The Caverns.

a2q8 Shadow of the Vaal ends the act. The Apex goes on the Dark Altar on top of The Ancient Pyramid, the Vaal Oversoul
emerges, and its death opens the pyramid exit to Act 3's The City of Sarn (`1_3_1`, gate `FinishedVaalOversoulQuest`).

Side quests:
- a2q11 The Way Forward: an Act 1 quest that is shown on Act 2. It uses the `a1q9-*` flags; Bestel gives the reward.
- a2q5 Through Sacred Ground: the Golden Hand.
- a2q10 The Great White Beast.
- a2q1, a2q2, a2q3: the three "help a bandit" quests.

Quest passive points in Act 2:
- Book of Skill from a2q11 (QSR 3, Act 1 numbering).
- Book of Skill from a2q5 (QSR 7).
- Eramir's point for killing all three bandits (QSR 8).

Sources: client tables (`data/tables`, 3.29.3b), the generated layouts (`lg.ps1 questobjects`, seed 1), room text
(`lg.ps1 cat`), the unpacked `.ot`/`.it` text, the campaign's boss placement findings
([status](../../../status/quests.md)), and the wiki (Fandom mirror, 2026-10-30). The sections after the status are the
research done before the act was built: their "Risks" are what was expected then, the status says how it went.

## Status (implemented, played live 2026-10-30)

Every quest of the act runs on the engine (`Modules/Core/World/QuestObjects/Acts/Act2.cs`,
[12](../../protocol/12-quests.md) §11) and was played unattended by the campaign runner
(`scripts/dev/campaign/quests/a2q*.py`) from `/quest_preset Act1Complete` (seed 1), before the game moved into modules;
on the modules the oracle (`QuestEngineTests`) plays it after Act 1 to `Act2CompleteEramir`, and the offline exit audit
(`scripts/re/exits --quests`) has every Act 2 exit at seed 1, the tree's into The Vaal Ruins included.
What the risks below predicted and how it went: Arteri, the bandit lords and the other bosses are placed (boss
workstream, and the monster blob's InteractionAction); the Thaumetic Seal, the tree, the Ancient Seal, the Apex door and
the Dark Altar are engine quest objects; books set their `use_flag` and give their points; Eramir's row 95 takes the
three amulets (its script names two flags); The Apex is built as a reward; `FinishedVaalOversoulQuest` is set with
`a2q8-KilledShadowMonster`. Found live: The Vaal Ruins had no exits and The Wetlands no tree exit (fixed: exits
resolved by their room, numbered exit models), the Ancient Seal's static model hung the client (its record's
`bossrelease.ao` now), and of two dialogue rows with one text the runner must take the unheard one (Yeena 133/134).
Not done: the bandit vote (help a lord); the automation kills all three. Greust's start row needs
`a2b-StartNewQuests`, which nothing sets this early.

## Areas

The Gateway generates every Act 2 area (`ServerLevels`). Unless the row says otherwise, the gate is
`a1q3-KilledSeaWitch`.

The "Bosses" column gives each unique boss's MonsterVarieties (MV) row and whether the server placed it when this was
researched. "Spawnable" is the MonsterCatalog replica. Under the Gateway's rule then (`mapboss` point only), a boss with
a numbered `mapbossN` point was not placed; every numbered area boss is placed now (the boss placement fixes).

| Id | Name | Lvl | WP | Gate (EndQuestFlags) | Bosses (MV row: placed today?) | Quest objects / NPCs found |
|---|---|---|---|---|---|---|
| 1_2_town | The Forest Encampment | 23 | town | - | - | town NPCs Eramir, Greust, HelenaTown, Silk, Yeena (`forest_town.arm`) |
| 1_2_1 | The Southern Forest | 13 | y | - | Calaf 1: no boss room | - |
| 1_2_2 | The Old Fields | 14 | n | - | Gneiss 409: placed (layout error logged) | - |
| 1_2_2a | The Den | 15 | n | - | **The Great White Beast 116: placed** (`fields_cave_treasure_1a.arm` `mapboss`) | - |
| 1_2_3 | The Crossroads | 15 | y | - | Bravalo 888: no boss room | lore NPCs (Info) |
| 1_2_15 | The Fellshrine Ruins | 16 | n | - | - | `GeofriPlaque` (Info NPC), BossSummon* objects |
| 1_2_5_1 | The Crypt Level 1 | 17 | y | - | - | - |
| 1_2_5_2 | The Crypt Level 2 | 18 | n | - | Geofri 547: placed (`Unique/treasure.arm`) | **Golden Hand altar** `Metadata/Chests/QuestChests/ChurchAltar`, tile `church_relic_altar_v01_01.tdt` record `dungeon_church_relic_altar_v01_01_dungeon_church_relic_altar` cell (20, 34) |
| 1_2_6_1 | The Chamber of Sins Level 1 | 15 | y | - | Black Death 976: placed | - |
| 1_2_6_2 | The Chamber of Sins Level 2 | 16 | n | - | **Fidelitas 154: placed** (`Unique/boss_1a.arm` `mapboss`) | room `boss_1a.arm`: `PoisonCart` (Strange Device) (128, 218), `HelenaSins` + `HelenaSinsHelper` (304, 195), `PietySins` (565, 240), `PietySinsPortal`, `PietySinsRunes`, `GemCart`, 2 `FidelitasLightningTower`; room `virtue_chamber.arm`: `VirtueGemController` |
| 1_2_4 | The Broken Bridge | 16 | y | - | **Kraityn 37: NOT placed** (needs InteractionAction, NPC; `broken_bridge*.arm` `mapboss`) | `BanditDoor`, `TentBanditSpawnerKraityn1-3` (room `broken_bridge_left.arm`), `ExileSpawner_1-3` |
| 1_2_7 | The Riverways | 15 | y | - | - | - |
| 1_2_9 | The Western Forest | 17 | y | - | **Alira 33: NOT placed** (NPC components; `intbanditfight.arm` `mapboss1`); **Captain Arteri 1419: NOT placed** (spawnable; `passageblock.arm` `mapboss2`) | `BanditDoor`, `CauldronSpawner` (`intbanditfight.arm`); `ArteriLetter` (Info NPC); `SpikeBlockageButton` (Thaumetic Seal), `BlockedPassageTransition` (tile `forest_mountainpass_v01_01.tdt`); `SpikeBlockageForest`/`2`, 4 `SpikeBlockageBlocker` (room `passageblock.arm`) |
| 1_2_10 | The Weaver's Chambers | 18 | n | - | **The Weaver 979: placed** (`Unique/treasure.arm`, boss bar) | ceiling spider spawners |
| 1_2_12 | The Wetlands | 19 | y | - | **Oak 42: NOT placed** (NPC components; 4 `bandit_boss_*.arm` `mapboss`) | `BanditDoor`, `TentBanditSpawnerOak1-3`; **`PoisonTree`** and **`IncaTreeTransition`** (tile `forest_caveentrance_inca_v01_02.tdt`); 4 `IncaTreeTransitionBlocker` (room `exit_1.arm`) |
| 1_2_11 | The Vaal Ruins | 20 | n | `a2q9-PoisonedTree` | - | **`IncaDarknessRelease`** (Ancient Seal; tile `dungeon_bossrelease_v01_01.tdt` record `..._ball_replacement` cell (46, 11)); 8 `IncaDarknessReleaseBlocker` (room `releaseblack.arm`); `CorruptionDevice` |
| 1_2_8 | The Northern Forest | 21 | y | `a2q8-UnleashedDarkness` | - | `GreustShrine` (Act 7) |
| 1_2_13 | The Dread Thicket | 21 | n | `a2q8-UnleashedDarkness` | - | - |
| 1_2_14_2 | The Caverns | 22 | y | `a2q8-UnleashedDarkness` | Q'uru 3323: placed | **`ApexDoor`** + 4 `ApexDoorBlocker` (room `sewer_connection.arm` (149, 57)); `BossArenaEntranceTransition` x2; `Recipe_Door`/`Recipe_Switch` |
| 1_2_14_3 | The Ancient Pyramid | 23 | n | `a2q8-UnleashedDarkness` | none in WorldAreas (the Oversoul comes from an object) | **`IncaBossSpawner`** (Dark Altar; tile `inca_boss_emerge.tdt` record `inca_boss_emerge_incabossemerge_setpiece` cell (23, 23)); **`IncaPyramidDoor`** + **`IncaPyramidTransition`** (exit to 1_3_1; tile `dungeon_huangdoor_v01_01.tdt`); `IncaBossRoomEffect`, 16 `IncaPyramidRockSpawner` (room `inca_boss_exit.arm`); `BossArenaEntranceTransition` |
| 1_SideArea2_1..8 | Vaal side areas | 1 | n | - | side bosses, spawnable | not quest areas |

## a2q11 "The Way Forward" (Quest row 9, side)

**Start:** Tarkleigh in Lioneye's Watch (Act 1). NPCTalk 50 or 51 "Reaching the Forest" sets `a1q9-QuestReceived`.
**Finish:** Bestel in Lioneye's Watch (`1_1_town`), row 74 "Road Reward", Book of Skill.
**DONE flag (driver):** `a1q9-UsedRewardBook`. QS Order 0 needs `a1q9-RoadOpened` with `UsedRewardBook` missing, so the
quest leaves the log once the book is used. Until book use sets the `use_flag`, check `a1q9-HaveRewardBook` instead.
**Prerequisites:** `a1q9-SeenBlockage` and `a1q9-RoadBlocked` from Act 1's Prisoner's Gate (both are in
`Act1Complete`). Bestel's row 71 needs `a1q9-RoadBlocked`; the emblem's `extra_flag` also sets it.

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes / source |
|---|---|---|---|---|---|
| 1 | Tarkleigh sends the player to the Emperor's Road | 1_1_town | talk Tarkleigh, NPCTalk 50/51 "Reaching the Forest" | `a1q9-QuestReceived` (QF4 and QF5) | Act 1 |
| 2 | Find the road closed | 1_1_8 Prisoner's Gate | Act 1 script | `a1q9-SeenBlockage`, `a1q9-RoadBlocked` | already in Act1Complete |
| 3 | Optional clue from Silk | 1_2_town | talk Silk, NPCTalk 189 "Blocked Passage" (needs SeenBlockage, not RoadOpened) | `a1q9-SilkClue` | QS Order 4 |
| 4 | Come near Captain Arteri at the blocked pass | 1_2_9 | sight of MV 1419 `Metadata/Monsters/Axis/Lieutenant/LieutenantLetter` | `a2q11-SeenCaptain` | no table setter: server `OnMonsterSeen` (QS Order 3 "Kill the Blackguards") |
| 5 | Kill Arteri | 1_2_9 | kill MV 1419 | `a2q11-SeenKey` (assumed) | no table setter; QS Order 2 "Take the Thaumetic Emblem from Captain Arteri". The emblem drops on his death (wiki) |
| 6 | Pick up the emblem | 1_2_9 | pick up `Metadata/Items/QuestItems/SpikeSealKey` "Thaumetic Emblem" (BIT 1560) | `a2q11-HaveKey` (grant), `a1q9-RoadBlocked` (extra) | `.it` `remove_flag a1q9-RoadOpened` |
| 7 | Use the Thaumetic Seal | 1_2_9 | use `Metadata/QuestObjects/Spikes/SpikeBlockageButton` with the emblem carried | `a1q9-RoadOpened`, clear `a2q11-HaveKey` | the emblem is taken; spikes open; blockers clear; exit to 1_1_8 becomes usable |
| 8 | Bestel congratulates | 1_1_town | NPCTalk 72 "Inspirational Poem" (needs RoadOpened) | `a1q9-Congratulated` | |
| 9 | Reward | 1_1_town | NPCTalk 74 "Road Reward" (needs Congratulated, not HaveRewardBook/UsedRewardBook), offer 11 | `a1q9-QuestRewardSeen`; book pickup sets `a1q9-HaveRewardBook` | QF5 `NEVER_SET` |
| 10 | Read the book | any | use `Metadata/Items/QuestItems/SkillBooks/Book-a1q9` | `a1q9-UsedRewardBook` | QSR 3: +1 passive |

### Objects, NPCs and bosses
| What | Type / MV / BIT | Where it comes from | Server today | Needed |
|---|---|---|---|---|
| Captain Arteri | MV 1419 `Axis/Lieutenant/LieutenantLetter` (WorldAreas 1_2_9 Bosses[1]) | room `passageblock.arm` point `mapboss2`, with pack `%area%-axis1` | not placed (numbered point) | numbered `mapbossN` support; sight sets `a2q11-SeenCaptain`; death sets `a2q11-SeenKey` and drops the emblem (Hailrake pattern: once, unless carried or `RoadOpened`) |
| Thaumetic Emblem | BIT 1560 `SpikeSealKey` (1x1) | Arteri's drop | no drop | quest-item drop plus `grant_flag`/`extra_flag` sync (a1q5 pattern) |
| Thaumetic Seal | `Metadata/QuestObjects/Spikes/SpikeBlockageButton` (`object_size 7`; MinimapIcon `show_flag a2q11minusHaveKey`, `hide_flag a1q9minusRoadOpened`; InteractionAction; Transitionable) | tile `forest_mountainpass_v01_01.tdt` record `forest_mountainpass_v01_01_spike_switch`, cell (53, 49) | not created; not in `object-types.json` | add to the generator: Positioned, BaseEvents, Transitionable, Animated, MinimapIcon, InteractionAction. Use with the emblem → `RoadOpened`, Transitionable 1→2 (a1q4 glyph wall pattern) |
| Spike walls | `SpikeBlockageForest`, `SpikeBlockageForest2` (Animated `spike_passageway.ao`, Transitionable) | room `passageblock.arm` (270, 80), (223, 80) | not created | room objects; Transitionable → 2 plays `open` (cosmetic) |
| Blockers | `SpikeBlockageBlocker` x4, `unblock_quest_flag a1q9minusRoadOpened`, `no_serialise` | room `passageblock.arm` x=323, y 67..89 | client-side | `QuestBarriers` entry until `a1q9-RoadOpened` |
| Exit to Prisoner's Gate | `Metadata/QuestObjects/Spikes/BlockedPassageTransition` (AreaTransition + Transitionable, `disable_auto`, untargetable until transition) | tile record `forest_mountainpass_v01_01_area_transition` | created as a plain AreaTransition (tile records) | `FlagToEnter(1_2_9 → 1_1_8) = a1q9-RoadOpened` |
| Arteri's letter | `Metadata/NPC/Info/ArteriLetter`, NPCTalk 455 "Read" (QF4/QF5 `NEVER_SET`) | tile `tent_mid_square_alteri_v01_01.tdt` | Info NPC, not quest relevant | - |

### Gates
- `FlagToEnter(1_2_9 → 1_1_8)` = `a1q9-RoadOpened`.
- The blockers (`SpikeBlockageBlocker`) lift on `a1q9-RoadOpened` on the client; the server needs the matching
  `QuestBarriers` stretch.

### Rewards
- Offer 11 `a1q9` (no taken flag): Bestel row 74 "Road Reward". One item, `Book-a1q9` "Book of Skill", level 1.
  The `.it` flags (`HaveRewardBook`/`UsedRewardBook`) hide the row after it is taken.
- QSR 3: `a1q9-UsedRewardBook`, col1 = 1 passive point.

### Quest log check
Every state also needs `a1q9-UsedRewardBook` missing.
- 9: `Visited-2_1` → Find the Forest Encampment.
- 8: `Visited-2_town`, with `SeenBlockage` missing → Find the Emperor's Road.
- 7: `Visited-2_1` + `SeenBlockage` → Find the Forest Encampment (and someone who knows the pass).
- 6: + `a2q6-SeenPiety` → Talk to Silk.
- 5: `Visited-2_town` + `SeenBlockage` → Talk to Silk.
- 4: `SilkClue` + `SeenBlockage` → Open the blocked pass.
- 3: `a2q11-SeenCaptain` → Kill the Blackguards.
- 2: `a2q11-SeenKey` → Take the Thaumetic Emblem.
- 1: `a2q11-HaveKey` → Activate the Thaumetic Seal.
- 0: `a1q9-RoadOpened` → Talk to Bestel for your reward.

### Driver steps (draft)
```python
QUEST = "a2q11"
DONE = "a1q9-UsedRewardBook"
STEPS = [
    {"verb": "talk", "npc": "Silk", "option": "Blocked Passage", "skip_if": "a1q9-SilkClue"},
    {"verb": "goto", "area": "1_2_9", "skip_if": "a1q9-RoadOpened"},
    {"verb": "kill", "target": "LieutenantLetter", "skip_if": "a2q11-HaveKey"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/SpikeSealKey", "skip_if": "a2q11-HaveKey"},
    {"verb": "interact", "target": "SpikeBlockageButton", "skip_if": "a1q9-RoadOpened"},
    {"verb": "wait_flag", "flag": "a1q9-RoadOpened"},
    {"verb": "talk", "npc": "Bestel", "option": "Inspirational Poem", "skip_if": "a1q9-Congratulated"},
    {"verb": "reward", "npc": "Bestel", "option": "Road Reward", "pick": ["Book of Skill"], "skip_if": "a1q9-HaveRewardBook"},
    # proposed verb (also used by act4.md): right-click the book in the main inventory
    {"verb": "use_item", "item": "Book-a1q9", "skip_if": "a1q9-UsedRewardBook"},
]
```

### Risks
- Arteri is not placed: `passageblock.arm` uses `mapboss2` (= Bosses[1]), which room-points drops (shared fix
  "numbered mapboss points").
- No table sets `a2q11-SeenCaptain` or `a2q11-SeenKey`. The kill-sets-SeenKey reading is inferred from the QuestStates
  order.
- The Thaumetic Seal and the spike objects are not in `object-types.json`. Their components come from the `.ot` text
  above: no component outside the supported set.
- Book use (`UseSkillBook`, `Program.Items.cs`) gives +1 passive point. It does not set the `.it` `use_flag`
  (`a1q9-UsedRewardBook`), so the quest log and DONE never complete. Whether the Gateway can build a Book of Skill
  as an offer item is unverified.

## a2q10 "The Great White Beast" (Quest row 12, side)

**Start:** Yeena, NPCTalk 130 "The Great White Beast" (not `a2q10-KilledBeast`) → `a2q10-QuestReceived`.
**Finish:** Yeena, row 131 "The Great White Beast", then row 132 "Great White Beast Reward" (offer 14).
**DONE flag (driver):** `a2q10-QuestRewardTaken`.
**Prerequisites:** none. Choosing the reward row sets `a2q10-QuestRewardSeen`, which a2q5's start needs.

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Yeena gives the quest | 1_2_town | NPCTalk 130 "The Great White Beast" | `a2q10-QuestReceived` | |
| 2 | Find the beast | 1_2_2a The Den | sight of MV 116 `Metadata/Monsters/Beasts/BeastBossWhite` | `a2q10-SeenBeast` | server `OnMonsterSeen` (no table setter) |
| 3 | Kill it | 1_2_2a | kill MV 116 | `a2q10-KilledBeast` | server `OnMonsterKilled` (no table setter) |
| 4 | Report | 1_2_town | NPCTalk 131 (needs KilledBeast, not QuestRewardTaken) | `a2q10-Congratulated` | |
| 5 | Reward | 1_2_town | NPCTalk 132 "Great White Beast Reward", offer 14 | `a2q10-QuestRewardSeen` (QF4), then taken flag `a2q10-QuestRewardTaken` | |

### Objects, NPCs and bosses
| What | Type / MV | Where | Server today | Needed |
|---|---|---|---|---|
| The Great White Beast | MV 116 `Beasts/BeastBossWhite` | WorldAreas 1_2_2a Bosses; `fields_cave_treasure_1a.arm` `mapboss` (placed at seed 1) | placed | seen and kill triggers |

### Gates
None.

### Rewards
- Offer 14 `a2q10` (taken flag `a2q10-QuestRewardTaken`), level 15, any class:
  - magic (r1): Greater Life, Greater Mana, Quicksilver and Bismuth Flasks;
  - rare (r2): Chain, Leather and Heavy Belts and Rustic Sash;
  - normal: Greater Life, Greater Mana and Bismuth Flasks.
- Only the three normal flasks can be built today. Driver pick: "Greater Life Flask" (the normal row).

### Quest log check
- 4: `QuestReceived` → Hunt down the Great White Beast.
- 3: `SeenBeast` → Kill the Great White Beast.
- 2: `KilledBeast` → Talk to Yeena for a reward.
- 1: `QuestRewardSeen` → Talk to Yeena for your reward.
- 0: `QuestRewardTaken` → Quest Complete.

### Driver steps (draft)
```python
QUEST = "a2q10"
DONE = "a2q10-QuestRewardTaken"
STEPS = [
    {"verb": "talk", "npc": "Yeena", "option": "The Great White Beast", "skip_if": "a2q10-QuestReceived"},
    {"verb": "goto", "area": "1_2_2a", "skip_if": "a2q10-KilledBeast"},
    {"verb": "kill", "target": "BeastBossWhite", "skip_if": "a2q10-KilledBeast"},
    {"verb": "wait_flag", "flag": "a2q10-KilledBeast"},
    {"verb": "talk", "npc": "Yeena", "option": "The Great White Beast", "skip_if": "a2q10-Congratulated"},
    {"verb": "reward", "npc": "Yeena", "option": "Great White Beast Reward", "pick": ["Greater Life Flask", "Bismuth Flask"]},
]
```

### Risks
- The magic flasks and rare belts cannot be built. The client lists all 11 choices, so the claim code must refuse
  the ones it cannot build.
- The reward's QF4 `a2q10-QuestRewardSeen` gates a2q5 (Yeena row 133). Opening the reward dialogue must set it even
  if no item is taken.

## a2q6 "Intruders in Black" (Quest row 11, main)

**Start:** Greust, NPCTalk 152 "Blackguards" (needs `a2b-StartNewQuests`, not `a2q6-HelenaInTown`) →
`a2q6-ReceivedQuest`.
**Finish:** Greust, row 153 "Helena" → `a2q6-Congratulated`, then row 155 "Blackguard Reward" (offer 13).
**DONE flag (driver):** `a2q6-QuestRewardTaken`.

**Prerequisites:** `a2b-StartNewQuests` (QF 808). The only setter in the data is Maligaro's Spike's `extra_flag2`,
which comes later in the act. The flag appears elsewhere only as a QuestFlag3 highlight, and no preset carries it.
The real server must set it some other way, probably on the first town visit (unverified). Greust's row is not
needed for the rest of the quest; the quest log starts at `a2q6-ReceivedQuest`.

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Greust gives the quest | 1_2_town | NPCTalk 152 "Blackguards" | `a2q6-ReceivedQuest` | needs `a2b-StartNewQuests` (above) |
| 2 | See Helena in the chamber | 1_2_6_2 | sight of `Metadata/NPC/Act2/HelenaSins` (or Fidelitas) | `a2q6-SeenHelena` | no table setter (QS Order 5 "Kill Fidelitas to rescue Helena") |
| 3 | Piety runs off (cutscene) | 1_2_6_2 | `PietySins` Transitionable → 2 (her run, then the portal and the runes) | `a2q6-SeenPiety` (assumed) | no table setter. a2q11's log and Sister Cassia's rows use it |
| 4 | Kill Fidelitas | 1_2_6_2 | kill MV 154 `Metadata/Monsters/DemonFourLegged/BossFourLeggedDemon` | `a2q6-KilledDemon` | server `OnMonsterKilled` |
| 5 | Helena climbs up | 1_2_6_2 | `HelenaSins` Transitionable 1 → 2 (`halyna_climbUp`, becomes targetable) | - | after the kill (inferred from her .ot) |
| 6 | Talk to Helena | 1_2_6_2 | NPCTalk 457 "Rescued" (not HaveSkillGem, not RescuedHelena), Script `triggerer.FinishNPCChat();` | `a2q6-RescuedHelena` | row 456 is the same with the gem carried |
| 7 | The Strange Device opens | 1_2_6_2 | `Metadata/Chests/QuestChests/Helena/PoisonCart` (Chests 188) state 2 (targetable) → open (state 3) | drops `PoisonSkillGem` | state 2 after the rescue (assumed) |
| 8 | Pick up the Baleful Gem | 1_2_6_2 | pick up `Metadata/Items/QuestItems/PoisonSkillGem` (BIT 1543) | `a2q6-HaveSkillGem` (grant), `a2q6-HelenaInTown` (extra) | `remove_flag a2q9-PoisonedTree` |
| 9 | Helena in town | 1_2_town | NPCTalk 462 "Rescued" (`HelenaTown`, no conditions) | `HelenaIntroduced` | QS Order 1 |
| 10 | Greust | 1_2_town | NPCTalk 153 "Helena" (needs HelenaInTown, not QuestRewardTaken) | `a2q6-Congratulated` | |
| 11 | Reward | 1_2_town | NPCTalk 155 "Blackguard Reward", offer 13 | `a2q6-QuestRewardSeen`, taken `a2q6-QuestRewardTaken` | |

### Objects, NPCs and bosses
| What | Type / MV / BIT | Where | Server today | Needed |
|---|---|---|---|---|
| Fidelitas, the Mourning | MV 154 `DemonFourLegged/BossFourLeggedDemon` | WorldAreas 1_2_6_2 Bosses; `Unique/boss_1a.arm` `mapboss` | placed (seed 1) | kill trigger. The 2 `FidelitasLightningTower` objects in the room are part of his fight (not created; optional) |
| Helena (chamber) | `Metadata/NPC/Act2/HelenaSins` (NPC + Transitionable `num_states 3` + ProximityTrigger client) | room `boss_1a.arm` (304, 195) | **not created** (out-of-town NPC) | create as an NPC in state 1, 2 after Fidelitas dies; NPCTalk 456/457; the generic NPC blob supports Transitionable |
| HelenaSinsHelper | `Metadata/NPC/Act2/HelenaSinsHelper` (Transitionable; plays Helena's call) | same room cell | not created | optional (audio) |
| Piety | `Metadata/NPC/Act2/PietySins` (Animated `Piety.ao`, Transitionable, MinimapIcon), `PietySinsPortal`, `PietySinsRunes` | room `boss_1a.arm` (565, 240), (599, 227/237) | not created; not in `object-types.json` | cosmetic cutscene; only `a2q6-SeenPiety` matters |
| Strange Device | `Metadata/Chests/QuestChests/Helena/PoisonCart`, Chests 188 "Strange Device", BaseItemTypesKey 1543 (Positioned, BaseEvents, Animated, Chest, Stats, ObjectMagicProperties, InteractionAction, MinimapIcon, Transitionable `num_states 3`) | room `boss_1a.arm` (128, 218) | not created | room-object chest. Transitionable is new on a chest; the a1q4 nest pattern plus the state. Drop the gem once unless carried, delivered or `a2q9-PoisonedTree` |
| Baleful Gem | BIT 1543 `PoisonSkillGem` (1x1) | the device | - | pickup flags (grant `HaveSkillGem`, extra `HelenaInTown`, remove on `PoisonedTree`) |
| `VirtueGemController` / `SinsVirtueGem` (Chests 226, "Virtue Gem") | room `virtue_chamber.arm` | - | not quest items (no BaseItemTypesKey) | none |

### Gates
None. The quest's items feed a2q9's gate.

### Rewards
- Offer 13 `a2q6` (taken `a2q6-QuestRewardTaken`): level 1 skill gems per class (Witch: Herald of Ash, Herald of
  Thunder, Herald of Ice, Desecrate, Arctic Armour, Cold Snap, Arcane Cloak, Galvanic Field, Wall of Force). All
  can be built. Driver pick: the first gem of the class.

### Quest log check
- 6: `ReceivedQuest` → Investigate the Chamber of Sins.
- 5: `SeenHelena` → Kill Fidelitas to rescue Helena.
- 4: `KilledDemon` → Talk to Helena.
- 3: `RescuedHelena`, with `a2q9-PoisonedTree` missing → Get the Baleful Gem.
- 2: `RescuedHelena` + `HaveSkillGem` → Meet Helena in Town.
- 1: `HelenaIntroduced` → Talk to Greust for a reward.
- 0: `QuestRewardTaken` → Quest Complete.

### Driver steps (draft)
```python
QUEST = "a2q6"
DONE = "a2q6-QuestRewardTaken"
STEPS = [
    {"verb": "talk", "npc": "Greust", "option": "Blackguards", "skip_if": "a2q6-ReceivedQuest"},
    {"verb": "goto", "area": "1_2_6_2", "skip_if": "a2q6-HelenaInTown"},
    {"verb": "kill", "target": "BossFourLeggedDemon", "skip_if": "a2q6-KilledDemon"},
    {"verb": "wait_flag", "flag": "a2q6-KilledDemon"},
    {"verb": "talk", "npc": "Helena", "option": "Rescued", "skip_if": "a2q6-RescuedHelena"},
    {"verb": "interact", "target": "PoisonCart", "skip_if": "a2q6-HelenaInTown"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/PoisonSkillGem", "skip_if": "a2q6-HelenaInTown"},
    {"verb": "talk", "npc": "Helena", "option": "Rescued", "skip_if": "HelenaIntroduced"},
    {"verb": "talk", "npc": "Greust", "option": "Helena", "skip_if": "a2q6-Congratulated"},
    {"verb": "reward", "npc": "Greust", "option": "Blackguard Reward"},
]
```

### Risks
- `a2b-StartNewQuests` has no known setter (above). Without it Greust never offers the quest, but nothing later
  checks `a2q6-ReceivedQuest`: the driver can skip step 1 and still finish.
- HelenaSins is an out-of-town NPC with a Transitionable state. The PoisonCart is a room-object chest with
  Transitionable. Neither is created today.
- `a2q6-SeenHelena`, `a2q6-SeenPiety` and `a2q6-KilledDemon` have no table setter: they need sight and kill hooks.
- The server places `HelenaTown` from the town room from the start. The real game presumably shows her only after the
  rescue; how is unknown. That is harmless for the driver.

## a2q4 "Sharp and Cruel" (Quest row 13, main)

**Start:** Helena (`HelenaTown`), NPCTalk 464 "Maligaro's Spike" (not `a2q9-PoisonedTree`, not
`a2q4-HavePoisonSpear`) → `a2q4-ReceivedQuest`. The wiki says Silk; the tables say Helena, and Silk gives the clue.
**Finish:** Silk, row 183 "Maligaro's Spike" → `a2q4-DeliveredSpear`, then row 185 "Maligaro's Spike Reward" (offer
15).
**DONE flag (driver):** `a2q4-QuestRewardTaken`.
**Prerequisites:** Helena is in town only after a2q6 in the real game. The Weaver's Chambers is not gated.

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Helena asks for the spike | 1_2_town | NPCTalk 464 "Maligaro's Spike" | `a2q4-ReceivedQuest` | |
| 2 | Silk's clue | 1_2_town | NPCTalk 182 "Maligaro's Spike" (needs ReceivedQuest) | `a2q4-SilkClue` | QS Order 7 |
| 3 | See the Weaver | 1_2_10 | sight of MV 979 `Metadata/Monsters/Spiders/SpiderBossWeaver` | `a2q4-SeenWeaver` | no table setter |
| 4 | Kill the Weaver | 1_2_10 | kill MV 979 | spike drops from the corpse | QS Order 6 text; no table drop rule |
| 5 | Pick up the spike | 1_2_10 | pick up `Metadata/Items/QuestItems/PoisonSpear` "Maligaro's Spike" (BIT 1542, 1x4) | `a2q4-HavePoisonSpear` (grant), `a2q4-ReceivedQuest` (extra), `a2b-StartNewQuests` (extra2) | `remove_flag a2q9-PoisonedTree` |
| 6 | Show it to Silk | 1_2_town | NPCTalk 183 (needs HavePoisonSpear, not DeliveredSpear) | `a2q4-DeliveredSpear` | the spike stays with the player (QS Order 3 text); row 184 is the variant after the tree |
| 7 | Reward | 1_2_town | NPCTalk 185 "Maligaro's Spike Reward", offer 15 | `a2q4-QuestRewardSeen`, taken `a2q4-QuestRewardTaken` | |

### Objects, NPCs and bosses
| What | Type / MV / BIT | Where | Server today | Needed |
|---|---|---|---|---|
| The Weaver | MV 979 `Spiders/SpiderBossWeaver` (boss bar) | WorldAreas 1_2_10 Bosses; `Unique/treasure.arm` `mapboss` | placed | sight and kill triggers; drop the spike on death unless carried, delivered or `PoisonedTree` (a1q5 Hailrake pattern) |
| Maligaro's Spike | BIT 1542 `PoisonSpear` | the Weaver's drop | no drop | pickup flag sync, including `extra_flag2` |

### Gates
None.

### Rewards
- Offer 15 `a2q4` (taken `a2q4-QuestRewardTaken`):
  - level 1 support gems per class (Witch: Faster Casting, Minion Speed, Concentrated Effect, Controlled Destruction,
    Elemental Focus, Predator, Trinity, Cruelty, Overcharge, Fresh Meat, Sacred Wisps, Kinetic Instability, Living
    Lightning Support);
  - magic belts at level 18 (Rustic Sash, Chain, Leather, Heavy Belt; r1).
- Gems can be built, belts cannot. Driver pick: a support gem.

### Quest log check
- 8: `ReceivedQuest` → Find Maligaro's Spike (Talk to Silk).
- 7: `SilkClue` → Recover Maligaro's Spike from the Weaver's Chambers.
- 6: `SeenWeaver` → Kill the Weaver and get Maligaro's Spike.
- 5: `a2q9-PoisonedTree` → Show it to Silk.
- 4: `HavePoisonSpear` → Talk to Silk.
- 3: `DeliveredSpear` → Talk to Silk for your reward (keep the spike).
- 2: `DeliveredSpear` + `PoisonedTree` → reward.
- 1: `QuestRewardTaken` → Quest Complete (keep the spike).
- 0: `QuestRewardTaken` + `PoisonedTree` → Quest Complete.

### Driver steps (draft)
```python
QUEST = "a2q4"
DONE = "a2q4-QuestRewardTaken"
STEPS = [
    {"verb": "talk", "npc": "Helena", "option": "Maligaro's Spike", "skip_if": "a2q4-ReceivedQuest"},
    {"verb": "talk", "npc": "Silk", "option": "Maligaro's Spike", "skip_if": "a2q4-SilkClue"},
    {"verb": "goto", "area": "1_2_10", "skip_if": "a2q4-HavePoisonSpear"},
    {"verb": "kill", "target": "SpiderBossWeaver", "skip_if": "a2q4-HavePoisonSpear"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/PoisonSpear", "skip_if": "a2q4-HavePoisonSpear"},
    {"verb": "talk", "npc": "Silk", "option": "Maligaro's Spike", "skip_if": "a2q4-DeliveredSpear"},
    {"verb": "reward", "npc": "Silk", "option": "Maligaro's Spike Reward"},
]
```

### Risks
- The spike drop from the Weaver is not in any table; the source is the QS text and the wiki. Low risk: the Weaver is
  placed.
- Silk's row 183 does not take the spike: the item must stay carried for a2q9. Its `HavePoisonSpear` flag must
  follow the inventory, as a1q5's does.

## a2q9 "The Root of the Problem" (Quest row 14, main)

**Start:** no start row. The log starts at Order 11 with `a2q9-SeenTree`, or Order 10 with `HelenaIntroduced` and the
gem missing.
**Finish:** the tree is poisoned (`a2q9-PoisonedTree`). The log says Complete once the player enters The Northern
Forest, The Dread Thicket or The Caverns (`Visited-2_8`/`2_13`/`2_14`).
**DONE flag (driver):** `a2q9-PoisonedTree`. That is the gate; the Complete states need areas behind a2q8's gate.
**Prerequisites:** both items carried: `a2q4-HavePoisonSpear` and `a2q6-HaveSkillGem`.

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | See the tree that blocks the way | 1_2_12 | sight of `Metadata/QuestObjects/Inca/PoisonTree` | `a2q9-SeenTree` | no table setter; Eramir row 109 "Ancient Gateway" needs it (gossip) |
| 2 | Helena's hint (optional) | 1_2_town | NPCTalk 467 "Lorrata" (needs HaveSkillGem + HavePoisonSpear) | `PoisonTreeClue` | QS Order 7 "Poison the ancient tree" |
| 3 | Poison the tree | 1_2_12 | use `PoisonTree` with spike and gem carried | `a2q9-PoisonedTree`; both items removed (`.it` `remove_flag`) | tree Transitionable 1→2 (animation, untargetable); the client lifts `IncaTreeTransitionBlocker` and `IncaTreeTransition` becomes targetable |
| 4 | Enter the ruins | 1_2_11 | area entry | `Visited-2_11` | WorldAreas gate `a2q9-PoisonedTree` |

Helena's NPCTalk 460 "The Baleful Gem" (needs `HelenaIntroduced`, not `HaveSkillGem`, not `PoisonedTree`; QF4
`a2q9-PoisonedTree`, Script `FinishNPCChat`) sets `a2q9-PoisonedTree` from dialogue alone. It is the real game's
fallback for a player who has lost the gem. It is a cheap emergency path, but it is not the quest.

### Objects, NPCs and bosses
| What | Type | Where | Server today | Needed |
|---|---|---|---|---|
| The tree (Lorrata) | `Metadata/QuestObjects/Inca/PoisonTree` (`object_size 4`; Animated, Transitionable, MinimapIcon `hide_flag a2q9minusPoisonedTree`, InteractionAction; Render "ObjectDisplayTextPoisonTree", Quest) | tile `forest_caveentrance_inca_v01_02.tdt` record `forest_caveentrance_inca_v01_01_forest_cave_entrance`, cell (33, 39) | not created; not in `object-types.json` | add to the generator and to `AreaObjects.QuestTypes`. Use with both items → take them, set the flag, Transitionable 1→2 (a1q4 glyph wall pattern) |
| Exit into the ruins | `Metadata/QuestObjects/Inca/IncaTreeTransition` (AreaTransition + Transitionable: untargetable until the transition) | same tile, record `..._area_transition1` cell (34, 43) | created as a plain AreaTransition | `FlagToEnter(1_2_12 → 1_2_11) = a2q9-PoisonedTree`; create only once it is set (as a1q4's exit) |
| Blockers | `IncaTreeTransitionBlocker` x4 (`unblock_quest_flag a2q9minusPoisonedTree`) | room `exit_1.arm` (23, 108) | client-side | `QuestBarriers` until the flag |

### Gates
- WorldAreas 1_2_11 EndQuestFlags `a2q9-PoisonedTree` (client).
- `FlagToEnter(1_2_12 → 1_2_11)`.

### Rewards
- None in the live game. Offer 16 `a2q9b_tencent` is a Tencent tutorial offer (taken flag
  `TencentTutorial-a2q9b_tencent-RewardTaken`); ignore it.

### Quest log check
- 11: `SeenTree` → Find a way into The Vaal Ruins.
- 10: `HelenaIntroduced`, with `HaveSkillGem` missing → Find the Baleful Gem.
- 9: `a2q4-DeliveredSpear`, with `HavePoisonSpear` missing → Find Maligaro's Spike.
- 8: both missing → find both.
- 7: `PoisonTreeClue` + spike + gem → Poison the ancient tree.
- 6: `PoisonedTree` → Enter the Vaal Ruins.
- 5..0: `Visited-2_14` / `Visited-2_13` / `Visited-2_8` (and pairs) → Quest Complete.

### Driver steps (draft)
```python
QUEST = "a2q9"
DONE = "a2q9-PoisonedTree"
STEPS = [
    {"verb": "talk", "npc": "Helena", "option": "Lorrata", "skip_if": "PoisonTreeClue"},
    {"verb": "goto", "area": "1_2_12", "skip_if": "a2q9-PoisonedTree"},
    {"verb": "interact", "target": "PoisonTree", "skip_if": "a2q9-PoisonedTree"},
    {"verb": "wait_flag", "flag": "a2q9-PoisonedTree"},
    {"verb": "goto", "area": "1_2_11", "via": "transition"},
]
```

### Risks
- PoisonTree is a tile quest object that is not in `object-types.json` and not created today. This blocks the main
  line: no tree means no Vaal Ruins.
- The use needs both items in the main inventory. Decide whether the cursor counts, as with a1q4's glyphs.

## a2q5 "Through Sacred Ground" (Quest row 10, side)

**Start:** Yeena, NPCTalk 133 "The Fellshrine Hand" (needs `a2q10-QuestRewardSeen` and `a2q6-HelenaInTown`, not
`a2q5-DeliveredGoldenHand`) → `a2q5-ReceivedQuest`.
**Finish:** Yeena, row 134 "The Fellshrine Hand" (delivers the hand), then row 136 "Fellshrine Reward" (offer 12,
Book of Skill).
**DONE flag (driver):** `a2q5-UsedRewardBook` (QS Order 0). Use `a2q5-HaveRewardBook` until book use sets the flag.
**Prerequisites:** a2q10's reward dialogue seen and the Baleful Gem picked up (a2q6). The wiki names neither.

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Yeena's dream | 1_2_town | NPCTalk 133 | `a2q5-ReceivedQuest` | the hand's `extra_flag` sets it too |
| 2 | Open the altar in the crypt | 1_2_5_2 | use (open) chest `Metadata/Chests/QuestChests/ChurchAltar` (Chests 189 "Altar", BaseItemTypesKey 1544) | drops the Golden Hand | Geofri (MV 547) guards the room; not needed |
| 3 | Pick up the hand | 1_2_5_2 | pick up `Metadata/Items/QuestItems/GoldenHand` (BIT 1544, 1x2) | `a2q5-HaveGoldenHand`, `a2q5-ReceivedQuest` (extra) | `remove_flag a2q5-DeliveredGoldenHand` |
| 4 | Give it to Yeena | 1_2_town | NPCTalk 134 (needs HaveGoldenHand, not Delivered) | `a2q5-DeliveredGoldenHand` | the hand must be taken away (remove_flag), as Nessa's medicine chest is: server code |
| 5 | Reward | 1_2_town | NPCTalk 136 "Fellshrine Reward" (needs Delivered, not HaveRewardBook/UsedRewardBook), offer 12 | `a2q5-QuestRewardSeen`; book pickup `a2q5-HaveRewardBook` | |
| 6 | Read the book | any | use `Book-a2q5` (BIT 1508) | `a2q5-UsedRewardBook` | QSR 7 |

### Objects, NPCs and bosses
| What | Type / BIT | Where | Server today | Needed |
|---|---|---|---|---|
| Altar | `Metadata/Chests/QuestChests/ChurchAltar`: no `.ot` of its own, Chests 189 `InheritsFrom` `Metadata/Chests/QuestChests/QuestChest` (as the Rhoa Nests), AOFiles `Barrel1.ao` | tile `Metadata/Terrain/Church/Features/church_relic_altar_v01_01.tdt` record `dungeon_church_relic_altar_v01_01_dungeon_church_relic_altar`, cell (20, 34) | not created (not in `AreaObjects.QuestTypes`) | a1q4 nest pattern: chest at the tile record, drops the hand once per instance unless carried, delivered or the book is held/used |
| Golden Hand | BIT 1544 `GoldenHand` | the altar | - | pickup flags and removal on delivery |
| Geofri | MV 547 `Skeletons/SkeletonBossMelee` | `Unique/treasure.arm` `mapboss` | placed | nothing |

### Gates
None.

### Rewards
- Offer 12 `a2q5` (no taken flag): `Book-a2q5` "Book of Skill", level 1. The `.it` extends
  `AbstractPassiveSkillAnd2RespecsBook`.
- QSR 7: `a2q5-UsedRewardBook`, col1 = 1 passive point, col7 = 2 (respec points, matching the book type).
- Unused flags: `a2q5-Congratulated`, `a2q5-JewelRewardSeen`, `a2q5-JewelRewardTaken` (no rows).

### Quest log check
- 4: `ReceivedQuest` → Find the Golden Hand.
- 3: `HaveGoldenHand` → Bring the Golden Hand to Yeena.
- 2: `DeliveredGoldenHand` → Talk to Yeena for your reward.
- 1: `HaveRewardBook` → use the Skill Book.
- 0: `UsedRewardBook` → Quest Complete.

### Driver steps (draft)
```python
QUEST = "a2q5"
DONE = "a2q5-UsedRewardBook"
STEPS = [
    {"verb": "talk", "npc": "Yeena", "option": "The Fellshrine Hand", "skip_if": "a2q5-ReceivedQuest"},
    {"verb": "goto", "area": "1_2_5_2", "skip_if": "a2q5-HaveGoldenHand"},
    {"verb": "interact", "target": "ChurchAltar", "skip_if": "a2q5-HaveGoldenHand"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/GoldenHand", "skip_if": "a2q5-HaveGoldenHand"},
    {"verb": "talk", "npc": "Yeena", "option": "The Fellshrine Hand", "skip_if": "a2q5-DeliveredGoldenHand"},
    {"verb": "reward", "npc": "Yeena", "option": "Fellshrine Reward", "pick": ["Book of Skill"], "skip_if": "a2q5-HaveRewardBook"},
    {"verb": "use_item", "item": "Book-a2q5", "skip_if": "a2q5-UsedRewardBook"},
]
```

### Risks
- The altar is a tile quest chest with no `.ot` (Chests.dat row only). Its components are the QuestChest set that the
  Rhoa Nests already use.
- Book use gives a point but not `a2q5-UsedRewardBook` and not the 2 respec points (col7). Two NPCTalk rows (2883,
  2909, Sanctum's Divinia) also read `a2q5-DeliveredGoldenHand`; they are not relevant.

## a2q7 "Deal with the Bandits" (Quest row 15, main) and the bandit choice

**Start:** Eramir, NPCTalk 87 "The Bandit Scourge" (no requirement; excluded once any bandit is killed or the
amulets are delivered) → `a2q7-ReceivedQuest`. Rows 88–93 are the same option after one or two kills.
**Finish (kill all three):** Eramir rows 94 "Bandit Lords" → 95 "Bandit Amulets" → 96 "Bandit Amulets" → 97 "Take the
Apex".
**DONE flag (driver):** `a2q7-QuestRewardTaken`. The driver must also take The Apex (`a2q8-HaveApex`, a2q8 needs it).
**Prerequisites:** none. Kraityn is in 1_2_4, Alira in 1_2_9 and Oak in 1_2_12, all behind `a1q3-KilledSeaWitch`.

The amulets are named by attribute:

| Bandit | Area | MV row | Amulet (BIT) | Kill flag (amulet `extra_flag`) | Hold flag (`grant_flag`) | Help quest | Help reward (QSR) |
|---|---|---|---|---|---|---|---|
| Kraityn, Scarbearer | 1_2_4 The Broken Bridge | 37 `BanditLeaderDexInt/BanditLeaderKraityn` | `DexAmulet` "Kraityn's Amulet" (1539) | `a2b-KilledDexBandit` | `a2b-HaveDexAmulet` | a2q1 | QSR 4, flag `a2q1-QuestRewardTaken`: `base_movement_velocity_+%` 8 |
| Alira Darktongue | 1_2_9 The Western Forest | 33 `BanditLeaderInt/BanditLeaderAlira` | `IntAmulet` "Alira's Amulet" (1538) | `a2b-KilledIntBandit` | `a2b-HaveIntAmulet` | a2q2 | QSR 5, `a2q2-QuestRewardTaken`: `base_resist_all_elements_%` 15 |
| Oak, Skullbreaker | 1_2_12 The Wetlands | 42 `BanditLeaderStrDex/BanditLeaderOak` | `StrAmulet` "Oak's Amulet" (1540) | `a2b-KilledStrBandit` | `a2b-HaveStrAmulet` | a2q3 | QSR 6, `a2q3-QuestRewardTaken`: `base_maximum_life` 40 |
| (kill all) | - | - | - | - | - | a2q7 | QSR 8, `a2q7-QuestRewardTaken`: col1 = 1 passive point |

All four amulets' `.it` have `remove_flag a2bminusDeliveredAmulets`. The Apex is `CombinedAmulet` (BIT 1541),
`grant_flag a2q8-HaveApex`, `remove_flag a2q8-PlacedApex`.

### How the choice works (tables)
1. **Talk to the bandit.** Kraityn's row 258, Alira's 298 and Oak's 222 are the first-meeting options "Kraityn's /
   Alira's / Oak's Rivals". Each needs no flags and excludes the ally, amulet, kill and reward flags. Rows 259–269,
   299–309 and 223–233 are the same option once the player already holds amulets or allies.
   - QF4 `BanditVoteCalled` (a session flag); QF5 `KraitynChatPersuade` / `AliraChatPersuade` / `OakChatPersuade`.
   - Script: `triggerer.SetSessionQuestFlag( BanditVoteCalled ); triggerer.FinishNPCChat();
     triggerer.DispatchRulesetEvent( BanditCallVote );`
2. **The vote.** The ruleset event opens the client's vote panel, `VoteType`:
   - row 2 `BanditVoteDex`: "Help Kraityn" / "Kill Kraityn", `__col4` = QF 54 `a2q1-QuestRewardTaken`;
   - row 0 `BanditVoteInt`: "Help Alira" / "Kill Alira", QF 56;
   - row 1 `BanditVoteStr`: "Help Oak" / "Kill Oak", QF 89.

   `VoteState` holds the states `KillBandit`, `HelpBandit`, `WaitingForVote` and `WaitingToEnterArea`. The vote
   messages (S→C panel, C→S answer) are not reverse-engineered.
3. **Kill.** The bandit (a Monster with NPC, InteractionAction and Transitionable components; Targetable
   `attackable = true`) fights inside his arena.
   - Server-only parts: `BanditDoor` (TriggerableBlockage + Transitionable, the `arenagate.ao` closes) and the
     reinforcement spawners (`TentBanditSpawnerKraityn1-3`, `TentBanditSpawnerOak1-3`, Alira's `CauldronSpawner`).
   - On death he drops his amulet. Picking it up sets the hold flag and the kill flag (`extra_flag`).
4. **Help.** The vote's accept path is for the a2q1/a2q2/a2q3 quests (next section). The driver does not use it.
5. **Eramir, kill all three:**
   - row 94 "Bandit Lords" (needs all three kill flags, no bandit reward) → `a2b-Congratulated`;
   - row 95 "Bandit Amulets" (needs the three hold flags + `a2b-Congratulated`, not `a2q7-QuestRewardTaken`/
     `a2q7-EramirCombined`) → QF4/QF5 `a2q7-QuestRewardTaken`, Script `SpawnServerEffect(gold.ao);
     SetQuestFlag("a2q7-QuestRewardTaken", "a2b-DeliveredAmulets")`; the amulets go (`remove_flag`); QSR 8 grants
     the passive point;
   - row 96 "Bandit Amulets" (needs QuestRewardTaken) → `a2q7-EramirCombined`;
   - row 97 "Take the Apex" (needs the three kill flags + `a2b-DeliveredAmulets`, not `a2q8-PlacedApex`/`HaveApex`)
     → offer 17 (The Apex), QF4 `a2b-SeenApexReward`.

   Row 117 "Claim Passive Skill Point" (needs `a2b-DeliveredAmulets`, no bandit reward) is the fallback that grants
   `a2q7-QuestRewardTaken` alone.

### Sequence (driver default: kill all three, Eramir's passive point)
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Eramir's quest | 1_2_town | NPCTalk 87 "The Bandit Scourge" | `a2q7-ReceivedQuest` | |
| 2 | Kraityn: talk, vote "Kill Kraityn" | 1_2_4 | NPCTalk 258 "Kraityn's Rivals" → vote `BanditVoteDex` reject | session `BanditVoteCalled`, `KraitynChatPersuade` | see Risks: the driver may `/kill` him directly |
| 3 | Kill Kraityn | 1_2_4 | kill MV 37 | drop `DexAmulet` | |
| 4 | Pick up Kraityn's Amulet | 1_2_4 | pick up BIT 1539 | `a2b-HaveDexAmulet`, `a2b-KilledDexBandit` | |
| 5 | Alira, the same | 1_2_9 | NPCTalk 298 → `BanditVoteInt` reject; kill MV 33; pick up BIT 1538 | `a2b-HaveIntAmulet`, `a2b-KilledIntBandit` | |
| 6 | Oak, the same | 1_2_12 | NPCTalk 222 → `BanditVoteStr` reject; kill MV 42; pick up BIT 1540 | `a2b-HaveStrAmulet`, `a2b-KilledStrBandit` | |
| 7 | Eramir | 1_2_town | NPCTalk 94 "Bandit Lords" | `a2b-Congratulated` | |
| 8 | Hand in the amulets | 1_2_town | NPCTalk 95 "Bandit Amulets" | `a2q7-QuestRewardTaken`, `a2b-DeliveredAmulets` (Script); amulets removed; +1 passive (QSR 8) | the Script and the passive need server code |
| 9 | Eramir combines them | 1_2_town | NPCTalk 96 "Bandit Amulets" | `a2q7-EramirCombined` | |
| 10 | Take The Apex | 1_2_town | NPCTalk 97 "Take the Apex", offer 17 | `a2b-SeenApexReward` (QF4); Apex pickup `a2q8-HaveApex` | `SeenApexReward` opens the Apex door in The Caverns |

### Objects, NPCs and bosses
| What | Type / MV | Where | Server today | Needed |
|---|---|---|---|---|
| Kraityn | MV 37 (Bosses[0] of 1_2_4) | `broken_bridge.arm` / `broken_bridge_left.arm` `mapboss` (seed 1 places `broken_bridge_left.arm` at (529, 138)) | NOT placed: the variety needs InteractionAction and NPC in the monster blob | monster blob with InteractionAction + NPC (+ MinimapIcon, Transitionable); kill → amulet drop |
| Alira | MV 33 (Bosses[0] of 1_2_9) | `intbanditfight.arm` point `mapboss1` (139, 129); placed at seed 1 at (1656, 414) | NOT placed (NPC components and numbered point) | the same, plus numbered `mapbossN` |
| Oak | MV 42 (Bosses[0] of 1_2_12) | 4 `bandit_boss_*.arm` `mapboss` (seed 1: `bandit_boss_dune_stream_1a.arm` (483, 759)) | NOT placed (NPC components) | the same |
| Arena gate | `Metadata/QuestObjects/BanditDoor` (Positioned, BaseEvents, Animated, TriggerableBlockage, Transitionable) | the bandit rooms (e.g. (92, 384) in `broken_bridge_left.arm`) | not created | optional: closes during the fight (all supported components) |
| Reinforcements | `TentBanditSpawnerKraityn1-3`, `TentBanditSpawnerOak1-3`, `Zombies/CauldronSpawner` | the same rooms | not created; not in `object-types.json` | optional |
| Amulets / The Apex | BIT 1538–1541 | drops / offer 17 | - | quest-item drop on death; pickup flag sync; removal on row 95; offer 17 must build a quest item |

### Help paths (a2q1, a2q2, a2q3: not the driver default)
The table shows Kraityn; Alira and Oak have the same row shapes. The other bandit's flags are in brackets.

| Step | Kraityn (a2q1) | Flags |
|---|---|---|
| vote accept "Help Kraityn" | `VoteType` 2, `__col4` QF 54 | real server: presumably `a2b-DexAlly` (QF 81) and/or the session flag `AfterBanditFight` (QF 55). No table setter for either: unverified |
| after the fight, talk | NPCTalk 254 "Kraityn's Rivals" (needs `AfterBanditFight`, no amulets) | QF4 session `BanditAfterFightTalk`, QF5 `a2q1-ReceivedQuest` |
| kill the two rivals, bring their amulets | NPCTalk 253 "Hand in Amulets" (needs `HaveIntAmulet` + `HaveStrAmulet` + `AfterBanditFight`) | `a2q1-QuestRewardTaken` → QSR 4 (+8% movement speed) |
| The Apex | NPCTalk 270 "Get the Apex" (needs `a2q1-QuestRewardTaken`), offer 17 | `a2b-SeenApexReward` (QF4 and Script) |

- Alira: rows 294 / 293 / 310, `a2q2-ReceivedQuest` / `a2q2-QuestRewardTaken` (QSR 5: +15% all elemental
  resistances), `a2b-IntAlly` (QF 80).
- Oak: rows 218 / 217 / 234, `a2q3-*` (QSR 6: +40 maximum life), `a2b-StrAlly` (QF 82).
- The `*Gauntlet` and `AfflictionDemonBossPhysicalWildOakGauntlet` rows ("Definitely Oak") are league variants;
  ignore them.
- After the choice, a later Act 7 row reads the helped bandit (Eramir 1052–1054 "Greust").

### Gates
- `a2b-SeenApexReward` lifts the `ApexDoorBlocker` in The Caverns (a2q8). This is why a2q7 (or a help quest) is
  required.

### Rewards
- QSR 8 (`a2q7-QuestRewardTaken`): 1 passive point, granted when the flag is set. There is no book: the Gateway must
  add it to the character's quest passive points (only Books of Skill add points today).
- QSR 4/5/6: the help rewards as permanent stats. The Gateway does not apply QuestStaticRewards stats today.
- Offer 17 `a2q7` (no taken flag): The Apex (BIT 1541), level 1, any class.

### Quest log check (a2q7)
- 12/11/10: one amulet held (Dex/Str/Int) → Talk to Eramir.
- 9: `ReceivedQuest` → Stop the bandit lords (pins: kill Kraityn / Alira / Oak).
- 8..3: `ReceivedQuest` + one or two amulets → "You have killed X ...".
- 2: all three amulets + `ReceivedQuest` → Take the amulets back to Eramir.
- 1: `a2b-DeliveredAmulets` → Quest Abandoned (aided a bandit).
- 0: `a2q7-QuestRewardTaken` → Quest Complete.

The help quests (a2q1 shown; a2q2/a2q3 the same with their flags):
- 9: `BanditVoteCalled` + `KraitynChatPersuade`, with `KilledDexBandit` missing → decide whether to help.
- 8..5: `a2q1-ReceivedQuest` + rival amulets → acquire them / talk to Kraityn.
- 4: + `KilledDexBandit` → Quest Abandoned (slain).
- 3: + `DeliveredAmulets` → Abandoned.
- 2: `a2q1-QuestRewardTaken` → get the Apex.
- 1/0: + `HaveApex` / `PlacedApex` → Complete.

### Driver steps (draft)
```python
QUEST = "a2q7"
DONE = "a2q7-QuestRewardTaken"
STEPS = [
    {"verb": "talk", "npc": "Eramir", "option": "The Bandit Scourge", "skip_if": "a2q7-ReceivedQuest"},
    # Kraityn: the real flow is talk + vote "Kill"; until the vote exists the server must let /kill finish him
    {"verb": "goto", "area": "1_2_4", "skip_if": "a2b-KilledDexBandit"},
    {"verb": "kill", "target": "BanditLeaderKraityn", "skip_if": "a2b-KilledDexBandit"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/BanditAmulets/DexAmulet", "skip_if": "a2b-HaveDexAmulet"},
    {"verb": "goto", "area": "1_2_9", "skip_if": "a2b-KilledIntBandit"},
    {"verb": "kill", "target": "BanditLeaderAlira", "skip_if": "a2b-KilledIntBandit"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/BanditAmulets/IntAmulet", "skip_if": "a2b-HaveIntAmulet"},
    {"verb": "goto", "area": "1_2_12", "skip_if": "a2b-KilledStrBandit"},
    {"verb": "kill", "target": "BanditLeaderOak", "skip_if": "a2b-KilledStrBandit"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/BanditAmulets/StrAmulet", "skip_if": "a2b-HaveStrAmulet"},
    {"verb": "talk", "npc": "Eramir", "option": "Bandit Lords", "skip_if": "a2b-Congratulated"},
    {"verb": "talk", "npc": "Eramir", "option": "Bandit Amulets", "skip_if": "a2q7-QuestRewardTaken"},
    {"verb": "talk", "npc": "Eramir", "option": "Bandit Amulets", "skip_if": "a2q7-EramirCombined"},
    {"verb": "reward", "npc": "Eramir", "option": "Take the Apex", "pick": ["The Apex"], "skip_if": "a2q8-HaveApex"},
    {"verb": "passives"},
]
```
a2q1/a2q2/a2q3 driver files: `DONE = "a2b-DeliveredAmulets"` (their "Quest Abandoned" state), `STEPS = []`.

### Risks
- All three bandits are NOT spawnable: their monster type adds InteractionAction and NPC (and MinimapIcon,
  Transitionable) to the Monster components. Alira also needs the numbered `mapboss1` point. This blocks the main
  line (no Apex, no Caverns door).
- The vote is a separate client UI driven by `DispatchRulesetEvent( BanditCallVote )`, with tables `VoteType` and
  `VoteState`; its messages are unknown. Minimum for the driver: bandits that are hostile and killable without the
  vote (`/kill` through the normal death path), dropping their amulet. The full Help/Kill choice needs the vote
  reverse-engineered.
- The kill flags come only from the amulet `extra_flag` in the data. Setting `a2b-Killed*Bandit` on the bandit's
  death too would be safer, since Eramir's rows read them.
- Row 95's Script sets `a2b-DeliveredAmulets` through `SetQuestFlag`; the generic dialogue sets only QF4/QF5. It needs
  a handler: set the flag and take the three amulets in one save.
- QSR 8's passive point and QSR 4/5/6's stats are not applied by the server (no QuestStaticRewards code).
- Wiki inconsistency: the wiki's walkthrough says Eramir gives "2 passive skill points". Its own reward table and QSR 8
  (col1 = 1) say 1. The tables win.

## a2q8 "Shadow of the Vaal" (Quest row 19, main)

**Start:** the Ancient Seal in The Vaal Ruins (`a2q8-UnleashedDarkness`). Yeena's NPCTalk 137 "The Darkness" (needs
`a2q8-HaveApex` + `UnleashedDarkness`) → `a2q8-QuestReceived`.
**Finish:** kill the Vaal Oversoul on top of The Ancient Pyramid. The exit opens to `1_3_1` The City of Sarn. Quest
row 20 `a2q8a3` (act3.md) continues from there.
**DONE flag (driver):** `a2q8-KilledShadowMonster`. The act boundary also needs `FinishedVaalOversoulQuest` (QS Order 2,
and the EndQuestFlags of `1_3_town`, `1_3_1`, `1_3_2`, `1_3_3_1`). QS Order 1/0 need `Visited-3_1` / `Visited-3_town`.
**Prerequisites:** `a2q9-PoisonedTree` (into the ruins) and The Apex from a2q7 or a help quest. Its `a2b-SeenApexReward`
opens the Caverns door.

### Sequence
| # | Real-game step | Area | Event | Flags set | Notes |
|---|---|---|---|---|---|
| 1 | Touch the Ancient Seal | 1_2_11 | use `Metadata/QuestObjects/Inca/IncaDarknessRelease` | `a2q8-UnleashedDarkness` | no table setter; the seal's Transitionable 1→2; the client lifts the 8 `IncaDarknessReleaseBlocker` |
| 2 | Townsfolk react (optional) | 1_2_town | NPCTalk 104 / 139 / 159 / 186 / 468 "The Darkness" | `a2q8-*Scared` | gossip |
| 3 | Show The Apex to Yeena | 1_2_town | NPCTalk 137 "The Darkness" | `a2q8-QuestReceived` | row 138 is the variant after `PlacedApex` |
| 4 | Eramir's clue | 1_2_town | NPCTalk 113 "Yeena's Vision" (needs QuestReceived) | `a2q8-EramirClue` | QS Order 15 |
| 5 | Pass the Apex door | 1_2_14_2 The Caverns | sight of `ApexDoor` | `a2q8-SeenApexDoor` (no table setter) | the door's blockers lift on `a2b-SeenApexReward` (client) |
| 6 | Reach the pyramid | 1_2_14_3 | area entry / sight | `Visited-2_14`; `a2q8-SeenPyramid` (no table setter) | QS Order 14 |
| 7 | See the Dark Altar | 1_2_14_3 | sight of `IncaBossSpawner` | `a2q8-SeenAltar` (no table setter) | Eramir row 112 "The Dark Altar" (clue `a2q8-EramirDarkAltarClue`) if the Apex is missing |
| 8 | Place The Apex | 1_2_14_3 | use `Metadata/Monsters/IncaShadowBoss/IncaBossSpawner` with The Apex carried | `a2q8-PlacedApex`; Apex removed (`remove_flag`) | spawner Transitionable 1→2 (emerge); the Oversoul spawns |
| 9 | The Oversoul emerges | 1_2_14_3 | sight of MV 158 `Metadata/Monsters/IncaShadowBoss/BossIncaShadow` "Vaal Oversoul" | `a2q8-SeenShadowMonster` (no table setter) | the monster's Transitionable → 2 makes it targetable |
| 10 | Kill it | 1_2_14_3 | kill MV 158 | `a2q8-KilledShadowMonster`, `FinishedVaalOversoulQuest` | no table setter for either; `IncaPyramidDoor` + `IncaPyramidTransition` → 2 (exit opens), `IncaBossRoomEffect`/`IncaPyramidRockSpawner` → 2 (cosmetic) |
| 11 | Leave for Act 3 | 1_2_14_3 → 1_3_1 | use `IncaPyramidTransition` | `Visited-3_1` | gate `FinishedVaalOversoulQuest` |

### Objects, NPCs and bosses
| What | Type | Where | Server today | Needed |
|---|---|---|---|---|
| Ancient Seal | `Metadata/QuestObjects/Inca/IncaDarknessRelease` (`object_size 6`; Animated, InteractionAction, Transitionable, MinimapIcon `hide_flag a2q8minusUnleashedDarkness`; client Render "ObjectDisplayTextAncientSeal", Targetable, SoundParameterDistance) | tile `Metadata/Terrain/Dungeon/Features/dungeon_bossrelease_v01_01.tdt` record `dungeon_bossrelease_v01_01_ball_replacement`, cell (46, 11) | not created; not in `object-types.json` | tile quest object. Use → `UnleashedDarkness` + Transitionable 1→2 (glyph wall pattern). SoundParameterDistance is client-only |
| Seal blockers | `IncaDarknessReleaseBlocker` x8 (`unblock_quest_flag a2q8minusUnleashedDarkness`) | room `releaseblack.arm` (144, 141) | client-side | `QuestBarriers` |
| Apex door | `Metadata/QuestObjects/Inca/ApexDoor` (Animated, Transitionable, InteractionAction; create_transitioned = open) | room `sewer_connection.arm` (149, 57) | not created; not in `object-types.json` | room object, state 2 once `a2b-SeenApexReward` (cosmetic; the blockers decide passage) |
| Apex door blockers | `ApexDoorBlocker` x4 (`unblock_quest_flag a2bminusSeenApexReward`) | same room (138, 60) | client-side | `QuestBarriers` until `a2b-SeenApexReward` |
| Dark Altar | `Metadata/Monsters/IncaShadowBoss/IncaBossSpawner` (Positioned, BaseEvents, Animated `inca_boss_emerge_incabossemerge_setpiece.ao` with `serialise_animation_progress`, InteractionAction, Transitionable `num_states 3`, TriggerableBlockage `start_blocked`, MinimapIcon `hide_flag a2q8minusKilledShadowMonster`; Render "ObjectDisplayTextDarkAltar") | tile `Metadata/Terrain/Dungeon/Features/inca_boss_emerge.tdt` record `inca_boss_emerge_incabossemerge_setpiece`, cell (23, 23) (seed 1 at (2208, 414)) | not created | create it at the tile record. Use with The Apex → `PlacedApex`, state 2, spawn MV 158 at it; state 3 after the kill. `serialise_animation_progress` may change the Animated blob (check) |
| Vaal Oversoul | MV 158 `Metadata/Monsters/IncaShadowBoss/BossIncaShadow` (type `IncaShadowBoss`: Monster + StateMachine + Transitionable, extends `ActBoss`) | spawned by the altar, not a WorldAreas boss (1_2_14_3 has none) | NOT placed | spawn from the altar; Transitionable 1→2 (`EnableTargetable`); the `Emerge` action; kill → flags. Its components are the Hillock set (StateMachine needs the type's data) |
| Pyramid exit | `Metadata/QuestObjects/Inca/IncaPyramidTransition` (AreaTransition + Transitionable, `disable_auto`, untargetable until transition) + `IncaPyramidDoor` (Transitionable door) | tile `dungeon_huangdoor_v01_01.tdt` records `..._area_transition` (46, 29) and `..._door` (46, 30) | plain AreaTransition from the tile record | `FlagToEnter(1_2_14_3 → 1_3_1) = FinishedVaalOversoulQuest`; create after the kill (a1q1 gate pattern); the door is cosmetic |
| `BossArenaEntranceTransition` | AreaTransition subtype | tile `dungeon_inca_exit_boss_v01_01.tdt` (Caverns x2, Pyramid x1) | plain AreaTransition | where it leads (Caverns → Pyramid?) unverified |

### Gates
- EndQuestFlags `a2q8-UnleashedDarkness`: 1_2_8, 1_2_13, 1_2_14_2, 1_2_14_3.
- EndQuestFlags `FinishedVaalOversoulQuest`: 1_3_town, 1_3_1, 1_3_2, 1_3_3_1.
- `FlagToEnter(1_2_11 → 1_2_8)` = `a2q8-UnleashedDarkness`.
- `FlagToEnter(1_2_14_3 → 1_3_1)` = `FinishedVaalOversoulQuest`.
- Client blockers: `IncaDarknessReleaseBlocker` (`UnleashedDarkness`) and `ApexDoorBlocker` (`a2b-SeenApexReward`).

### Rewards
None (no offer, no static reward).

### Quest log check
- 19: `UnleashedDarkness` → Find a way to lift the Darkness.
- 18: `SeenApexDoor` → Deal with the Bandit Lords.
- 17: `HaveApex` + `UnleashedDarkness` → Show the Apex to Yeena.
- 16: `QuestReceived` → Banish the Darkness (Talk to Eramir).
- 15: `EramirClue` → Find the Ancient Pyramid.
- 14: `SeenPyramid` + `HaveApex` + `EramirClue` → Ascend the Ancient Pyramid.
- 13: `SeenAltar` → Ask Eramir about the Dark Altar.
- 12: `SeenAltar` + `HaveApex` → Activate the Dark Altar.
- 11: `SeenAltar` + `EramirDarkAltarClue`, with `HaveApex` missing → Obtain the amulets.
- 10: `SeenShadowMonster` + `HaveApex` → Kill the Vaal Oversoul.
- 9..6: `a2b-SeenApexReward` + the bandit or Eramir reward flag, with `HaveApex` missing → Get the Apex from Eramir
  (or the bandit).
- 5: `PlacedApex` → Kill the Vaal Oversoul.
- 4: `KilledShadowMonster` + `HaveApex` → the pyramid's exit is open.
- 3: `KilledShadowMonster` + `PlacedApex` → the exit is open.
- 2: `FinishedVaalOversoulQuest` → the exit is open.
- 1: `Visited-3_1` → Find the Sarn encampment.
- 0: `Visited-3_town` → Complete.

### Driver steps (draft)
```python
QUEST = "a2q8"
DONE = "a2q8-KilledShadowMonster"
STEPS = [
    {"verb": "goto", "area": "1_2_11", "skip_if": "a2q8-UnleashedDarkness"},
    {"verb": "interact", "target": "IncaDarknessRelease", "skip_if": "a2q8-UnleashedDarkness"},
    {"verb": "wait_flag", "flag": "a2q8-UnleashedDarkness"},
    {"verb": "talk", "npc": "Yeena", "option": "The Darkness", "skip_if": "a2q8-QuestReceived"},
    {"verb": "talk", "npc": "Eramir", "option": "Yeena's Vision", "skip_if": "a2q8-EramirClue"},
    {"verb": "goto", "area": "1_2_14_3", "skip_if": "a2q8-PlacedApex"},
    {"verb": "interact", "target": "IncaBossSpawner", "skip_if": "a2q8-PlacedApex"},
    {"verb": "wait_flag", "flag": "a2q8-PlacedApex"},
    {"verb": "kill", "target": "BossIncaShadow", "skip_if": "a2q8-KilledShadowMonster"},
    {"verb": "wait_flag", "flag": "FinishedVaalOversoulQuest"},
    {"verb": "goto", "area": "1_3_1", "via": "transition"},
]
```

### Risks
- **The Vaal Oversoul is scripted:**
  - the Dark Altar (`IncaBossSpawner`, tile record) must exist and take The Apex;
  - the boss is not a WorldAreas boss and must be spawned at the altar;
  - it starts untargetable (Transitionable state 1);
  - no table sets `PlacedApex`, `SeenShadowMonster`, `KilledShadowMonster` or `FinishedVaalOversoulQuest`.

  Minimum: altar use sets `PlacedApex` and spawns MV 158 at the altar in state 2. Its kill sets
  `a2q8-KilledShadowMonster` + `FinishedVaalOversoulQuest` and creates the exit. Highest risk of the act.
- The Ancient Seal (`IncaDarknessRelease`) is a tile quest object that is not created and not in `object-types.json`.
  It blocks the whole second half of the act.
- The Act 2 presets (`Act2Complete*`) do not contain `FinishedVaalOversoulQuest`, only `a2q8-KilledShadowMonster` etc.
  A character started with `/quest_preset Act2CompleteEramir` cannot enter Act 3 until the server sets
  `FinishedVaalOversoulQuest`. Either set it together with `a2q8-KilledShadowMonster`, or derive it in
  `/quest_preset`. (act3.md notes the same.)
- Offer 17 must be able to build The Apex (a quest item, not a gem or flask). Whether `QuestRewardCatalog` does is
  unverified.
- Wiki: the page says the quest starts at the seal and that Yeena gives it once the bandits are done. The tables need
  `a2q8-HaveApex` for Yeena's row 137 (row 138 after `PlacedApex`).

## Implementation summary

| Quest | Main/side | Features | Risk + why | Est. size |
|---|---|---|---|---|
| a2q11 The Way Forward | side | kill trigger (Arteri), numbered boss point, item drop + pickup flags, object use (Thaumetic Seal, tile record), client blocker + `QuestBarriers`, quest-gated transition to 1_1_8, NPC hand-in (Act 1 town), reward offer (book), book use flag | med: Arteri not placed (`mapboss2`); seal object new to the generator; book `use_flag` missing | M |
| a2q10 The Great White Beast | side | seen + kill trigger, NPC dialogue, reward offer (normal flasks only) | low: boss placed; only the reward-item limits | S |
| a2q6 Intruders in Black | main | kill trigger (Fidelitas), out-of-town NPC (HelenaSins, Transitionable states), room-object chest with Transitionable (PoisonCart) → item drop, pickup flags, NPC dialogue, reward offer (gems) | med: Helena and the cart are not created; `a2b-StartNewQuests` has no setter (Greust's start row) | M |
| a2q4 Sharp and Cruel | main | seen + kill trigger (Weaver), boss drop (spike), pickup flags, NPC hand-in that keeps the item, reward offer (gems; belts not buildable) | low: boss placed; drop rule from QS text | S |
| a2q9 The Root of the Problem | main | object use with two items (PoisonTree, tile record), item removal, client blocker + `QuestBarriers`, area gate + `FlagToEnter` (1_2_12 → 1_2_11) | med: tree not created and not in `object-types.json`; blocks the act | M |
| a2q5 Through Sacred Ground | side | tile quest chest (ChurchAltar, Chests.dat only) → item drop, pickup flags, NPC hand-in with item removal, reward offer (book), book use flag + 2 respec points | low/med: nest pattern reuse; book flags | S |
| a2q7 Deal with the Bandits (+ a2q1/a2q2/a2q3) | main | NPC-component monsters (3 bandits), numbered boss point (Alira), boss drops (3 amulets), pickup flags, the bandit vote UI (`VoteType`), NPC hand-in with Script (`SetQuestFlag` + item removal), QSR passive point (and help stats), reward offer (The Apex, quest item) | high: bandits not spawnable (InteractionAction/NPC), vote protocol unknown, QuestStaticRewards not applied | L |
| a2q8 Shadow of the Vaal | main | object use (Ancient Seal, tile record), area gates (4 areas), client blockers (seal, Apex door), scripted boss (altar spawner + Apex + emerge → Oversoul MV 158), kill trigger setting two flags, quest-gated transition to Act 3 | high: altar and boss spawn entirely new; seal not created; preset lacks `FinishedVaalOversoulQuest` | L |
