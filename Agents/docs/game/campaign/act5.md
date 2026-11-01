# Act 5 quests

Act 5 takes place in Oriath. The player arrives in The Slave Pens (`1_5_1`) from the act 4 row `a5q1a` (row 62, in
[act4.md](act4.md): the Resonator in The Ascent sets `a5q1-UsedResonator`, which the client needs to enter `1_5_1`).
The town is Overseer's Tower (`1_5_town`: Lani, Utula, Vilenta, later Bannon). The main line runs in this order:
- `a5q1b`: kill Overseer Krow to reach the town.
- `a5q2`: kill Justicar Casticus in The Control Blocks; he drops the Eyes of Zeal.
- `a5q4`: use the Eyes of Zeal on the Templar Courts door in Oriath Square, then kill High Templar Avarius (and his
  Innocence form) in The Chamber of Innocence.
- `a5q6`: Sin appears there and gives the quest. Kill Avarius and Oriath turns into its "destroyed" version (The Ruined
  Square, The Torched Courts, The Ossuary, The Reliquary). Take the Sign of Purity from the Ossuary tomb to The
  Cathedral Rooftop. The Kitava fight cannot be won: at about half life Kitava destroys the Sign, `a5q6-CompletedBoss`
  is set (permanent −30% to all elemental and chaos resistances), and Sin saves the player.
- Lilly Roth's NPCTalk script then sails the player to Lioneye's Watch (`2_6_town`, act 6).

The side quests are `a5q3` (Vilenta's Miasmeter, 1 passive point), `a5q5` (kill Utula in The Ruined Square; Quest
Type 0 but not needed for act 6) and `a5q7` (Kitava's Torments, 1 passive point and 2 respec points).

Sources:
- client tables (`data/tables`, 3.29.3b);
- the generated layouts (`lg.ps1 questobjects`, seed 1) and `lg.ps1 grep` over `Act5/**/*.arm` (short-form room lines
  that `questobjects` misses);
- game text `.ot`/`.it` (`references/ggpk-text`);
- the coordinator's `BOSS-FINDINGS.md` and `dump/boss-verdicts.json`;
- the Fandom wiki mirror (pages fetched 2026-10-30);
- the server code in this worktree.

## Status (implemented, played live 2026-10-30)

Every quest of the act runs on the engine (`Modules/Core/World/QuestObjects/Acts/Act5.cs`,
[12](../../protocol/12-quests.md) §14) and was played unattended by the campaign runner (driver steps `a5q1b`, `a5q3`,
`a5q2`, `a5q4`, `a5q5`, `a5q7`, `a5q6`) before the game moved into modules; on the modules the oracle
(`QuestEngineTests`) plays it after Act 4: every quest of the act complete, `a5q6-CompletedBoss`, arrival in Lioneye's
Watch. What differs from the sections below:
- Kitava never dies: `On.Threshold` sets `a5q6-CompletedBoss` when a hit (or `/kill`) takes him to 50 % and he leaves;
  the −30 % is the Game module's `ResistancePenalty` (`ResistancePenaltyAct5`). The Sign in its socket (`ArenaSocket`, placed from the server's own rooms) wakes
  him there; the area does not place him.
- The Templar Courts door, the Ossuary tomb and the socket take their items; the Control Blocks' and the Chamber's
  exits, the court gate and Lilly follow their serialisation flags; Innocence appears where Avarius dies, Sin where
  Innocence dies (also on a later visit until `a5q6-QuestReceived`); Bannon appears after Avarius, Utula leaves town.
- `a5q4-QuestReceived` (no setter in the data) is set with the door. Reward gear (magic and rare rings, jewels,
  weapons) is built with the standard rolls; an offer with nothing buildable still sets its taken flag.
- Lilly's "Sail to Wraeclast" runs its `AreaTransitionTo` (NPCTalk scripts, 17-npcs.md).

## Areas

| WorldAreas Id | Name | Lvl | WP | Gateway generates | EndQuestFlags (gate) | Unique bosses (MV Id, spawnable?) | Quest objects / NPCs found |
|---|---|---|---|---|---|---|---|
| `1_5_1` | The Slave Pens | 41 | yes | yes | `a5q1-UsedResonator` | Overseer Krow `Metadata/Monsters/TemplarSlaveDriver/TemplarSlaveDriverBoss` (MV 5199, spawnable; PLACED at `tower_01.arm` mapboss (116, 98)) | `BloodWingIntro` (Casticus taunt, `Unique/entrance_01.arm` (134, 323), Transitionable + Timer); tower `AreaTransition` (tile `tower_v01_01.tdt`) |
| `1_5_town` | Overseer's Tower | 45 | yes | yes | `a5q1-KilledBoss` | — | `Lani`, `Utula`, `Vilenta`, `BannonTown` (all in `town_01.arm`) |
| `1_5_2` | The Control Blocks | 41 | no | yes | `a5q1-KilledBoss` | Justicar Casticus `Metadata/Monsters/BlackguardInquisitor/Bloodwing` (MV 5213, spawnable; PLACED at `Unique/boss_01.arm` mapboss (204, 353)) | `Chests/QuestChests/Vilenta/SupplyChest` (`Unique/pietylab_01.arm` (95, 82)); `BloodWingBlockingDoor` ×2 (boss tile); `ControlBlocksToSquare` / `ControlBlocksToDestroyedSquare` (tile `security_exit_v01_01.tdt` (14, 70)); `DestroyedOriathObstructions` ×10 (`Unique/exit_01.arm`) |
| `1_5_3` | Oriath Square | 42 | yes | yes | `a5q2-KilledBoss` | The Matriarch (MV 5312, PLACED; not a quest boss) | `TemplarCourtsDoor` + `OriathSquareToCourts` (tile `Oriath_AreaTransition_v01_03.tdt`, (34, 24) / (34, 32)) |
| `1_5_4` | The Templar Courts | 42 | yes | yes | `a5q2-UsedKey` | — | — |
| `1_5_5` | The Chamber of Innocence | 43 | yes | yes | `a5q2-UsedKey` | High Templar Avarius `Metadata/Monsters/AvariusCasticus/AvariusCasticus` (MV 5466, spawnable, boss bar; PLACED at `Unique/boss_02.arm` mapboss (227, 227)); phase 2 `AvariusCasticusDivine` "Innocence, God-Emperor of Eternity" (MV 5475) is not a Bosses row | `BannonInnocence` (`Unique/entrance_01.arm` (263, 313)); arena markers/spawners in `boss_02.arm`; `AvariusCasticusStatue` ×4 (short-form monster lines); `InnocenceToCourts` / `InnocenceToDestroyedCourts` (tile `transition_chamber_to_courts_v01_01.tdt` (49, 49)); `InnocenceShortcutDoor`; SinInnocence: in no room or tile |
| `1_5_3b` | The Ruined Square | 44 | yes | yes | `a5q4-KilledBoss` | Utula, Stone and Steel `Metadata/Monsters/BlacksmithGolem/BlacksmithGolem` (MV 5770, NOT spawnable: needs NPC; room point `mapboss1` in `Unique/park.arm` (104, 150)); The Risen Matriarch (MV 5318, `mapboss2` in `Unique/fountain.arm` (291, 164)); neither is placed | `CathedralRoofBlocker` ×18 + `CathedralRoofFlame` (`Unique/entrance_04.arm`) |
| `1_5_4b` | The Torched Courts | 44 | no | yes | `a5q4-KilledBoss` | Cato, Scholar of Light (MV 1446, PLACED; not a quest boss) | — |
| `1_5_6` | The Ossuary | 44 | no | yes | `a5q4-KilledBoss` | — | `Chests/QuestChests/Ossuary/PurityBox` (tile `ossuary_quest_v01_01.tdt` (11, 11)); `SinOssuary`, `Ossuary_BossSwitch` (`Unique/boss_01.arm`); `Ossuary_HiddenDoor` (tile) |
| `1_5_7` | The Reliquary | 44 | yes | yes | `a5q4-KilledBoss` | — | `RelicCase1/2/3` (`Unique/kitavachest_01/02/03.arm`); vault keys/levers (optional) |
| `1_5_8` | The Cathedral Rooftop | 45 | yes | yes | `a5q4-KilledBoss`, `a5q6-SeenItem` | Kitava, the Insatiable `Metadata/Monsters/KitavaBoss/Kitava1` (MV 5747, spawnable, boss bar; room point `force:mapboss` in `Unique/bossentry.arm` (217, 658): NOT placed) | `SignOfPurity` + `ArenaSocket` (`bossentry.arm` (218, 558)); `PortalToggleable` (218, 584); `KitavaDoor` (tile); `CathedralKitavaLadder` (tile); `SinKitava`, `Lilly`, `LandingZone` (`Unique/sin_dock.arm`) |
| `1_SideArea5_*` | Vaal side areas (8) | — | no | yes | — | Vaal side-area bosses (all spawnable) | no quest flags; optional, not part of any quest |

Act 6 gates set by this act: `a5q6-CompletedBoss` is the EndQuestFlags of `2_6_town`, `2_6_1`, `2_6_2`, `2_6_3` and
`2_6_4`. `Visited-6_town` (StartQuestFlags of `2_6_town`) gates the Heist areas and is a5q6's Order 0 flag.

## a5q1b "Return to Oriath" (Quest row 63, main)

**Start:** entering The Slave Pens (`Visited-5_1`), after act 4's `a5q1a`. **Finish:** Lani's reward in town.
**DONE flag (driver):** `a5q1-QuestRewardTaken`. Offer 34 has only magic and rare rings, so it cannot be built today.
Until it can, the driver should treat `a5q1-Congratulated` as done and come back for the reward later.
**Prerequisites:** `a5q1-UsedResonator` (the client gate of `1_5_1`, set in act 4).

### Sequence

| # | Real-game step | Area | Event | Flags set | Notes / source |
|---|---|---|---|---|---|
| 1 | Arrive in the Slave Pens | `1_5_1` The Slave Pens | enter area | `Visited-5_1` | StartQuestFlags (Gateway already sets it) |
| 2 | See Overseer's Tower | `1_5_1` | near the tower transition (tile `tower_v01_01.tdt` record `tower_v01_01_area_transition`) | `a5q1-SeenTown` | no setter in the client data; only in preset `EpilogueAtlasDemo`. Optional (log Order 5) |
| 3 | See Krow | `1_5_1` | seen MV 5199 `TemplarSlaveDriver/TemplarSlaveDriverBoss` | `a5q1-SeenBoss` | `OnMonsterSeen` |
| 4 | Kill Krow | `1_5_1` | kill MV 5199 | `a5q1-KilledBoss` | `OnMonsterKilled`; opens `1_5_town` and `1_5_2` (client gate) |
| 5 | Enter the town | `1_5_town` Overseer's Tower | enter area | `Visited-5_town` | StartQuestFlags |
| 6 | Talk to Lani | `1_5_town` | NPCTalk 812 Lani idx 1 "Overseer Krow" (needs `a5q1-KilledBoss`, not `a5q1-QuestRewardTaken`) | `a5q1-Congratulated` (QF4) | generic dialogue |
| 7 | Reward | `1_5_town` | NPCTalk 813 Lani idx 2 "Overseer Reward" (needs `a5q1-Congratulated`), offer 34 | `a5q1-QuestRewardSeen` (QF4), `a5q1-QuestRewardTaken` (offer taken) | offer cannot be built (see Rewards) |

### Objects, NPCs and bosses

| What | Type / MV Id / BaseItemTypes Id | Where it comes from | Server today | Needed |
|---|---|---|---|---|
| Overseer Krow | MV 5199 | WorldAreas Bosses of `1_5_1`, `Unique/tower_01.arm` point `mapboss` (116, 98) | placed (boss-verdicts PLACED) | kill and seen triggers |
| Casticus intro | `Metadata/QuestObjects/Act5/BloodWingIntro` (Positioned, BaseEvents server_only, Animated, Transitionable, **Timer**) | room line `Unique/entrance_01.arm` (134, 323) | not created | cosmetic, skip (Timer would be new serialisation) |
| Lani | `Metadata/NPC/Act5/Lani` | `town_01.arm` | placed (town NPC) | — |

### Gates

`1_5_town` and `1_5_2` need `a5q1-KilledBoss` (client EndQuestFlags; the Gateway should refuse the transition the same
way, `FlagToEnter`). No `unblock_quest_flag` blockers.

### Rewards

- Offer 34 `a5q1b` (taken `a5q1-QuestRewardTaken`), Lani NPCTalk 813 "Overseer Reward", any class. The choices are
  Topaz, Sapphire, Ruby and Two-Stone (×3) Ring at item level 41, rarity 2 (rare), and Topaz, Sapphire and Ruby Ring at
  level 41, rarity 1 (magic). **None can be built today**, so the offer opens nothing.
- No static reward.

### Quest log check

- 6 → `Visited-5_1` → Escape the Slave Pens
- 5 → `a5q1-SeenTown` → Reach Overseer's Tower
- 4 → `a5q1-SeenBoss` → Kill Overseer Krow
- 3 → `a5q1-KilledBoss` → Enter Overseer's Tower
- 2 → `a5q1-KilledBoss` + `Visited-5_town` → Talk to Lani for a reward
- 1 → `a5q1-QuestRewardSeen` → Talk to Lani for your reward
- 0 → `a5q1-QuestRewardTaken` → Lani has rewarded you

### Driver steps (draft)

```python
QUEST = "a5q1b"
DONE = "a5q1-QuestRewardTaken"  # until magic/rare rings can be built, stop at a5q1-Congratulated
STEPS = [
    {"verb": "goto", "area": "1_5_1", "skip_if": "a5q1-KilledBoss"},
    {"verb": "kill", "target": "TemplarSlaveDriverBoss", "skip_if": "a5q1-KilledBoss"},
    {"verb": "wait_flag", "flag": "a5q1-KilledBoss"},
    {"verb": "goto", "area": "1_5_town", "via": "transition"},
    {"verb": "talk", "npc": "Lani", "option": "Overseer Krow", "skip_if": "a5q1-Congratulated"},
    {"verb": "reward", "npc": "Lani", "option": "Overseer Reward", "pick": ["Two-Stone Ring", "Ring"],
     "skip_if": "a5q1-QuestRewardTaken"},
]
```

### Risks

- Offer 34 has only magic and rare rings. Building them (RewardLevel 41) is needed before the quest can reach Order 0.
- `a5q1-SeenTown` has no setter in the data. It is cosmetic (log Order 5 only).

## a5q2 "The Key to Freedom" (Quest row 64, main)

**Start:** Utula in town (after his introduction). **Finish:** Lani's reward.
**DONE flag (driver):** `a5q2-QuestRewardTaken`. Take a **normal** Diamond, Granite or Jade Flask: those three rows
have no rarity, so they can be built today.
**Prerequisites:** `UtulaIntroduction` (Utula NPCTalk 869 "Introduction" sets it with QF4 and QF5). `1_5_2` needs
`a5q1-KilledBoss`.

### Sequence

| # | Real-game step | Area | Event | Flags set | Notes / source |
|---|---|---|---|---|---|
| 1 | Meet Utula | `1_5_town` | NPCTalk 869 Utula idx 0 "Introduction" | `UtulaIntroduction` | |
| 2 | Get the quest | `1_5_town` | NPCTalk 870 Utula idx 1 "Justicar Casticus" (needs `UtulaIntroduction`, not `a5q2-KilledBoss`) | `a5q2-QuestReceived` (QF4 and QF5) | the row's QuestFlag3 (read state) also lists `a5q4-QuestReceived` |
| 3 | Enter the Control Blocks | `1_5_2` The Control Blocks | enter area | `Visited-5_2` | StartQuestFlags |
| 4 | See Casticus | `1_5_2` | seen MV 5213 `BlackguardInquisitor/Bloodwing` | `a5q2-SeenBoss` | `OnMonsterSeen` |
| 5 | Kill Casticus | `1_5_2` | kill MV 5213 | `a5q2-KilledBoss` | opens `1_5_3` (client gate) |
| 6 | Take the Eyes of Zeal | `1_5_2` | pick up `Metadata/Items/QuestItems/Act5/TemplarCourtKey` "Eyes of Zeal" (BaseItemTypes 1569, 1×1) dropped by Casticus | `a5q2-HaveKey` | `.it` Quest `grant_flag a5q2-HaveKey`, `remove_flag a5q2-UsedKey`. The drop is not in the client data; the wiki's `drop_monsters = Bloodwing` confirms it |
| 7 | Talk to Lani | `1_5_town` | NPCTalk 814 Lani idx 3 "Justicar Casticus" (needs `a5q2-KilledBoss`) | `a5q2-Congratulated` (QF4) | |
| 8 | Reward | `1_5_town` | NPCTalk 815 Lani idx 4 "Casticus Reward", offer 35 | `a5q2-QuestRewardSeen`, `a5q2-QuestRewardTaken` | |
| — | (optional) Utula after the kill | `1_5_town` | NPCTalk 871 Utula idx 2 "Justicar Casticus" | `UtulaOnCasticusDead` (QF5) | gossip; per the wiki this is where Utula sends the player to Avarius |

The Eyes of Zeal are used in `a5q4` (Templar Courts door), which sets `a5q2-UsedKey`.

### Objects, NPCs and bosses

| What | Type / MV Id / BaseItemTypes Id | Where it comes from | Server today | Needed |
|---|---|---|---|---|
| Justicar Casticus | MV 5213 | Bosses of `1_5_2`, `Unique/boss_01.arm` point `mapboss` (204, 353) | placed | kill and seen triggers; quest-item drop on death |
| Eyes of Zeal | BaseItemTypes 1569 `Metadata/Items/QuestItems/Act5/TemplarCourtKey` | dropped by Casticus (server rule, as Hailrake's Medicine Chest in a1q5) | no | drop once per instance while the character has neither `a5q2-HaveKey` nor `a5q2-UsedKey`, and holds no key and none lies on the ground; pickup sets `a5q2-HaveKey` |
| Casticus' arena doors | `Metadata/QuestObjects/Act5/BloodWingBlockingDoor` ×2 (Positioned, BaseEvents server_only, Animated, Transitionable, TriggerableBlockage start_blocked, save_as_diff) | tile `Metadata/Terrain/Theopolis/SlaveDens/Feature/slave_thinwall_doorway_straight_boss_v01_03.tdt` record `slave_thinwall_doorway_straight_Boss_v01_03_animation` (11, 11) | not created (not a transition type) | none for the driver: a server_only door that is never created does not block the client. A faithful fight would close them while Casticus lives |
| Utula, Lani | town NPCs | `town_01.arm` | placed | — |

### Gates

`1_5_3` Oriath Square needs `a5q2-KilledBoss` (client gate). The Control Blocks exit tile `security_exit_v01_01.tdt`
holds two transitions at the same record (14, 70):
- `ControlBlocksToSquare` (`serialisation_stop_flag a5q4-KilledBoss`);
- `ControlBlocksToDestroyedSquare` (`serialisation_start_flag a5q4-KilledBoss`).

The server ignores `serialisation_start_flag` / `serialisation_stop_flag` today (no code reads them), so it sends both
as plain AreaTransitions. See the a5q4 Risks.

### Rewards

Offer 35 `a5q2` (taken `a5q2-QuestRewardTaken`), Lani NPCTalk 815 "Casticus Reward", any class:
- Ruby, Sapphire, Topaz, Amethyst, Granite, Jade and Diamond Flask at level 42, rarity 1 (magic): cannot be built.
- Diamond, Granite and Jade Flask at level 42, no rarity (normal): **can be built today**.

The magic and normal choices share names, so the driver's `pick` must land on a normal row. The 3.15 wiki list
agrees on the flask types. No static reward.

### Quest log check

- 5 → `a5q2-QuestReceived` → Kill Justicar Casticus (travel to the Control Blocks)
- 4 → `a5q2-QuestReceived` + `Visited-5_2` → Kill Justicar Casticus
- 3 → `a5q2-SeenBoss` → Kill Justicar Casticus
- 2 → `a5q2-KilledBoss` → Talk to Lani for your reward
- 1 → `a5q2-QuestRewardSeen` → Talk to Lani for your reward
- 0 → `a5q2-QuestRewardTaken` → Quest Complete

### Driver steps (draft)

```python
QUEST = "a5q2"
DONE = "a5q2-QuestRewardTaken"
STEPS = [
    {"verb": "talk", "npc": "Utula", "option": "Introduction", "skip_if": "UtulaIntroduction"},
    {"verb": "talk", "npc": "Utula", "option": "Justicar Casticus", "skip_if": "a5q2-QuestReceived"},
    {"verb": "goto", "area": "1_5_2", "via": "transition", "skip_if": "a5q2-KilledBoss"},
    {"verb": "kill", "target": "Bloodwing", "skip_if": "a5q2-KilledBoss"},
    {"verb": "wait_flag", "flag": "a5q2-KilledBoss"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act5/TemplarCourtKey", "skip_if": "a5q2-HaveKey"},
    {"verb": "talk", "npc": "Lani", "option": "Justicar Casticus", "skip_if": "a5q2-Congratulated"},
    {"verb": "reward", "npc": "Lani", "option": "Casticus Reward", "pick": ["Diamond Flask", "Granite Flask"],
     "skip_if": "a5q2-QuestRewardTaken"},
]
```

`pickup` must be skipped as well once `a5q2-UsedKey` is set. The step allows only one `skip_if`; the driver should add
a second condition or check `a5q2-UsedKey` before the step.

### Risks

- The Eyes of Zeal drop is server logic that does not exist yet (a1q5 Medicine Chest pattern). Without it, `a5q4`
  cannot start: the Templar Courts door needs the key, and the client gates `1_5_4` and `1_5_5` on `a5q2-UsedKey`.
- The reward pick has to choose the normal flask rows (indexes 7–9) and not the magic ones of the same name.

## a5q3 "In Service to Science" (Quest row 65, side)

**Start:** Vilenta in town. **Finish:** use Vilenta's Book of Skill.
**DONE flag (driver):** `a5q3-UsedRewardBook` (QuestStaticRewards 12: 1 passive point).
**Prerequisites:** none (NPCTalk 848 has no needs). `1_5_2` needs `a5q1-KilledBoss`.

### Sequence

| # | Real-game step | Area | Event | Flags set | Notes / source |
|---|---|---|---|---|---|
| 1 | Get the quest | `1_5_town` | NPCTalk 848 Vilenta idx 14 "Miasmeter" (not `a5q3-DeliveredItem`) | `a5q3-QuestReceived` (QF4) | |
| 2 | Open the supplies | `1_5_2` The Control Blocks | use chest `Metadata/Chests/QuestChests/Vilenta/SupplyChest` "Experimental Supplies" (Chests 201) | — | room line `Metadata/Terrain/Act5/Area2/Rooms/Unique/pietylab_01.arm` (95, 82), model `MiasmeterChest.ao`; seed 1 room at (414, 1380) |
| 3 | Pick up the Miasmeter | `1_5_2` | pick up BaseItemTypes 1568 `Metadata/Items/QuestItems/Act5/Miasmeter` (1×3) | `a5q3-HaveItem` | `.it` `grant_flag a5q3-HaveItem`, `remove_flag a5q3-DeliveredItem` |
| 4 | Hand it in | `1_5_town` | NPCTalk 849 Vilenta idx 15 "Miasmeter" (needs `a5q3-HaveItem`) | `a5q3-DeliveredItem` (QF4) | server takes the Miasmeter (remove_flag) |
| 5 | Reward | `1_5_town` | NPCTalk 850 Vilenta idx 16 "Miasmeter Reward" (needs `a5q3-DeliveredItem`, not `a5q3-HaveRewardBook` / `a5q3-UsedRewardBook`), offer 36 | `a5q3-QuestRewardSeen` (QF4) | |
| 6 | Take the book | `1_5_town` | BaseItemTypes 1574 `Metadata/Items/QuestItems/Act5/Book-a5q3` "Book of Skill" into the inventory | `a5q3-HaveRewardBook` | `.it` `grant_flag` |
| 7 | Use the book | — | use the book (C→S 0x58) | `a5q3-UsedRewardBook` | `.it` `use_flag`, `remove_flag`; QSR 12 → +1 passive point |

### Objects, NPCs and bosses

| What | Type / MV Id / BaseItemTypes Id | Where it comes from | Server today | Needed |
|---|---|---|---|---|
| Experimental Supplies | `Metadata/Chests/QuestChests/Vilenta/SupplyChest` (extends `QuestChest`: Positioned (blocking, static), BaseEvents, Animated, Chest, Stats, ObjectMagicProperties, InteractionAction, MinimapIcon, Transitionable (open animation)) | room server object, `pietylab_01.arm` (95, 82) | not created | create the room object (the a1q4 nest pattern, but from a room line); drop the Miasmeter once while the character needs it (no `a5q3-HaveItem` / `a5q3-DeliveredItem`) |
| Miasmeter | BaseItemTypes 1568 | Chests 201 `BaseItemTypesKey` | — | pickup flag; hand-in removes it |
| Vilenta | `Metadata/NPC/Act5/Vilenta` | `town_01.arm` | placed | — |

### Gates

None.

### Rewards

- Offer 36 `a5q3` contains one Book of Skill, but its taken flag is **null**. `QuestRewardCatalog.Load` skips offers
  without a taken flag, so the offer opens nothing today. The server must give the book (and keep
  `a5q3-HaveRewardBook`) through another path, for example by treating `a5q3-HaveRewardBook` as the taken flag.
- QuestStaticRewards 12: flag `a5q3-UsedRewardBook`, 1 passive point (col1 = 1), no respec points.
- Using a book today (`Program.Items.cs` `UseSkillBook`) adds 1 extra passive point and removes the book. It does
  **not** set the `.it` `use_flag`, so the quest never reaches Order 0.

### Quest log check

- 5 → `a5q3-QuestReceived` → Find Vilenta's Miasmeter (travel to the Control Blocks)
- 4 → `a5q3-QuestReceived` + `Visited-5_2` → Find Vilenta's Miasmeter
- 3 → `a5q3-HaveItem` → Return the Miasmeter to Vilenta
- 2 → `a5q3-DeliveredItem` → Talk to Vilenta for your reward
- 1 → `a5q3-HaveRewardBook` → use the Book of Skill
- 0 → `a5q3-UsedRewardBook` → Quest Complete

### Driver steps (draft)

```python
QUEST = "a5q3"
DONE = "a5q3-UsedRewardBook"
STEPS = [
    {"verb": "talk", "npc": "Vilenta", "option": "Miasmeter", "skip_if": "a5q3-QuestReceived"},
    {"verb": "goto", "area": "1_5_2", "skip_if": "a5q3-HaveItem"},
    {"verb": "interact", "target": "SupplyChest", "skip_if": "a5q3-HaveItem"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act5/Miasmeter", "skip_if": "a5q3-HaveItem"},
    {"verb": "talk", "npc": "Vilenta", "option": "Miasmeter", "skip_if": "a5q3-DeliveredItem"},
    {"verb": "reward", "npc": "Vilenta", "option": "Miasmeter Reward", "pick": ["Book of Skill"],
     "skip_if": "a5q3-HaveRewardBook"},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act5/Book-a5q3"},  # not in CONTRACT yet
]
```

### Risks

- The `SupplyChest` room object is not created. Its drop rule and the Miasmeter pickup flag are needed.
- The book offer has a null taken flag and is skipped by the reward catalog.
- Using a book does not set `a5q3-UsedRewardBook`.
- CONTRACT has no verb for using an inventory item.

## a5q4 "Death to Purity" (Quest row 66, main)

**Start:** the log shows it as soon as `a5q2-KilledBoss` is set (Order 8). **Finish:** Lani's reward after Avarius.
**DONE flag (driver):** `a5q4-QuestRewardTaken`. Take the **Uncarved Gemstone**: offer 37 row 3,
`Metadata/Items/Currency/CurrencySkillGemToken`, level 1, no rarity, so it is probably buildable as currency. The
jewel rows are rare.
**Prerequisites:** `a5q2-KilledBoss` and the Eyes of Zeal (`a5q2-HaveKey`).

### Sequence

| # | Real-game step | Area | Event | Flags set | Notes / source |
|---|---|---|---|---|---|
| 1 | Get the quest | — | **no setter in the client data** | `a5q4-QuestReceived` | Needed only by log Order 9 and gossip rows 840, 878, 879. It is in E3KitavaKey but not in ConsoleOriathSquare, which already has `a5q2-HaveKey`, so it is set after the key is taken. Proposal: set it together with `a5q2-UsedKey` (unverified) |
| 2 | Go to Oriath Square | `1_5_3` Oriath Square | enter (needs `a5q2-KilledBoss`) | `Visited-5_3` | |
| 3 | Use the Eyes of Zeal on the door | `1_5_3` | interact `Metadata/QuestObjects/Act5/TemplarCourtsDoor` while holding BaseItemTypes 1569 | `a5q2-UsedKey`; the server removes the Eyes of Zeal (`.it` `remove_flag a5q2-UsedKey`) | door Transitionable → transitioned (client plays the open animation and disables Targetable); MinimapIcon `show_flag a5q2-HaveKey`, `hide_flag a5q2-UsedKey`; glyph-wall pattern of a1q4 |
| 4 | The court gate appears | `1_5_3` | `Metadata/QuestObjects/Act5/OriathSquareToCourts` (extends AreaTransition, `serialisation_start_flag a5q2-UsedKey`, object_size 8) | — | destination `1_5_4` (client gate `a5q2-UsedKey`) |
| 5 | Through the Templar Courts | `1_5_4` The Templar Courts | enter | `Visited-5_4` | |
| 6 | Enter the Chamber | `1_5_5` The Chamber of Innocence | enter (needs `a5q2-UsedKey`) | `Visited-5_5` | waypoint at `Unique/entrance_01.arm` (172, 172) |
| 7 | See Avarius | `1_5_5` | seen MV 5466 `AvariusCasticus/AvariusCasticus` | `a5q4-SeenBoss` | |
| 8 | Fight Avarius, then Innocence | `1_5_5` | MV 5466 (BossLifeBar, Transitionable 3 states, `set_monster_has_second_form`) becomes MV 5475 `AvariusCasticusDivine` "Innocence, God-Emperor of Eternity" | `a5q4-SeenInnocence` (no setter in the data; gossip rows 2653/2713 only) | arena: `AvariusStatueSpawner` ×4, `AvariusTemplarMarker1..5` (+Ranged/Boss), `ArenaMiddle`, `TeleportMarkerSerialized` ×7, statues `AvariusCasticusStatue` (MV 5480 "Guardian of Eternity") ×4, all in `boss_02.arm` |
| 9 | Kill Innocence | `1_5_5` | kill MV 5475 (or MV 5466 if phase 2 is not built) | `a5q4-KilledBoss` | world change: Ruined/Torched areas (below); opens `1_5_3b`, `1_5_4b`, `1_5_6`, `1_5_7` and half of the `1_5_8` gate |
| 10 | Sin appears | `1_5_5` | `Metadata/NPC/Act5/SinInnocence` appears (Transitionable: `entrance_avarius` animation, then targetable) | — | in no room or tile: the server must spawn it near the arena middle |
| 11 | Talk to Sin | `1_5_5` | NPCTalk 882 SinInnocence idx 0 "Kitava" | `a5q6-QuestReceived` (QF4) | `Script triggerer.DoNPCChat(1);` chains rows 883, 884 (needs `NEVER_SET`: dialogue pages). Starts a5q6 and moves a5q4 to Order 2 |
| 12 | Talk to Lani | `1_5_town` | NPCTalk 823 Lani idx 12 "High Templar Avarius" (needs `a5q4-KilledBoss`) | `a5q4-Congratulated` (QF4) | |
| 13 | Reward | `1_5_town` | NPCTalk 824 Lani idx 13 "Avarius Reward", offer 37 | `a5q4-QuestRewardSeen`, `a5q4-QuestRewardTaken` | |

The real game swaps areas on `a5q4-KilledBoss`:
- Oriath Square → The Ruined Square, The Templar Courts → The Torched Courts;
- the Chamber's exit `InnocenceToCourts` (`serialisation_stop_flag`) is replaced by `InnocenceToDestroyedCourts`
  (`serialisation_start_flag`);
- in the Control Blocks, `ControlBlocksToSquare` is replaced by `ControlBlocksToDestroyedSquare`, and the
  `DestroyedOriathObstructions` corpse piles (`serialisation_start_flag a5q4-KilledBoss`) block that exit.

### Objects, NPCs and bosses

| What | Type / MV Id / BaseItemTypes Id | Where it comes from | Server today | Needed |
|---|---|---|---|---|
| Templar Courts door | `Metadata/QuestObjects/Act5/TemplarCourtsDoor` (Positioned object_size 6, BaseEvents server_only, Animated, InteractionAction, Transitionable, MinimapIcon icon 5 with show/hide flags; client Render "ObjectDisplayTextTemplarCourtDoor", Targetable) | tile `Metadata/Terrain/Theopolis/OriathSquare/Stonewall/Oriath_AreaTransition_v01_03.tdt` record `Oriath_AreaTransition_v01_03_open`, tile cell (34, 24) (seed 1 tile at (2553, 483)) | not created (not in `AreaObjects.QuestTypes`) | create it (Transitionable state 1, or 2 once `a5q2-UsedKey` is set); on use with the key: take the key, set `a5q2-UsedKey`, send the transition. No `object-types.json` entry: check that its MinimapIcon `show_flag`/`hide_flag` need no new bytes |
| Court gate | `Metadata/QuestObjects/Act5/OriathSquareToCourts` | same tile, record `Oriath_AreaTransition_v01_03_area_transition` (34, 32) | created as a plain AreaTransition (AreaTransitionCatalog `Resolve`), always | create only once `a5q2-UsedKey` is set (serialisation_start_flag); until then the client gate on `1_5_4` still refuses |
| High Templar Avarius | MV 5466 | Bosses of `1_5_5`, `boss_02.arm` mapboss (227, 227) | placed | kill and seen triggers; phase 2 (MV 5475) as in Hillock's two stages, or set `a5q4-KilledBoss` on MV 5466's death |
| Innocence form | MV 5475 `Metadata/Monsters/AvariusCasticus/AvariusCasticusDivine` | spawned by the fight (not a Bosses row) | no | optional phase 2 |
| Arena helpers | `AvariusStatueSpawner`, `AvariusTemplarMarker*`, `ArenaMiddle` (server_only), `TeleportMarkerSerialized` (Positioned, BaseEvents), `AvariusCasticusStatue` ×4 (short-form monster lines (271, 172), (172, 272), (237, 108), (107, 236)) | `boss_02.arm` | not created | not needed for the driver |
| Sin | `Metadata/NPC/Act5/SinInnocence` (NPC, Transitionable) | spawned at Avarius' death | no (out-of-town NPC) | spawn it after `a5q4-KilledBoss` (state 2 = idle, targetable), give it NPCTalk 882–887 |
| Chamber exits | `InnocenceToCourts` / `InnocenceToDestroyedCourts` (extend AreaTransition) | tile `Metadata/Terrain/Theopolis/Innocence/Feature/transition_chamber_to_courts_v01_01.tdt` records `…_area_courts_normal` / `…_area_courts_burned` (49, 49) | both created as plain transitions | pick one by `a5q4-KilledBoss` (serialisation flags) |
| Shortcut door | `Metadata/QuestObjects/Act5/InnocenceShortcutDoor` (TriggerableBlockage start_blocked, save_as_diff) | tile `Innocence_boss_exit_v01_01.tdt` record `Innocence_boss_exit_v01_01_door` (34, 10) | not created | not needed (a door that is never created blocks nothing) |
| Lore objects | `Metadata/NPC/Info/StainedGlassWindow1..8`, `TriumphOfInnocence` (NPCTalk 1264–1272 "Read") | tiles / `innocence_statue_concave.arm` | not created | optional |

### Gates

- `1_5_4` and `1_5_5` need `a5q2-UsedKey`.
- `a5q4-KilledBoss` is the EndQuestFlags of `1_5_3b`, `1_5_4b`, `1_5_6`, `1_5_7` and (with `a5q6-SeenItem`) `1_5_8`.
- `FlagToEnter` should mirror all of these.
- The serialisation-flag swaps above need a generic rule: honour `BaseEvents` `serialisation_start_flag` /
  `serialisation_stop_flag` when creating objects for a character. They are also needed for Lilly (a5q6) and
  `CathedralRoofFlame`. Without it, the Chamber's exit keeps both destinations and the Control Blocks keep both exits.

### Rewards

Offer 37 `a5q4` (taken `a5q4-QuestRewardTaken`), Lani NPCTalk 824 "Avarius Reward", any class:
- Crimson, Viridian and Cobalt Jewel at level 42, rarity 2 (rare): cannot be built (no jewels, no rares).
- Uncarved Gemstone (`CurrencySkillGemToken`, BaseItemTypes 342) at level 1, no rarity: buildable if the Gateway builds
  level-1 currency of item class 30. Check before relying on it.

The wiki notes that the jewel choices follow the Enemy at the Gate gem list; the 3.29 table has generic rare jewels plus
the gemstone. No static reward.

### Quest log check

- 9 → `a5q4-QuestReceived` → Gain access to the Templar Courts
- 8 → `a5q2-KilledBoss` → Take the Eyes of Zeal
- 7 → `a5q2-HaveKey` → Enter the Templar Courts
- 6 → `a5q2-UsedKey` → Find High Templar Avarius and kill him
- 5 → `Visited-5_5` → Find High Templar Avarius and kill him
- 4 → `a5q4-SeenBoss` → Kill High Templar Avarius
- 3 → `a5q4-KilledBoss` → Talk to Sin
- 2 → `a5q4-KilledBoss` + `a5q6-QuestReceived` → Talk to Lani for your reward
- 1 → `a5q4-QuestRewardSeen` → Talk to Lani for your reward
- 0 → `a5q4-QuestRewardTaken` → Quest Complete

### Driver steps (draft)

```python
QUEST = "a5q4"
DONE = "a5q4-QuestRewardTaken"
STEPS = [
    {"verb": "goto", "area": "1_5_3", "skip_if": "a5q2-UsedKey"},
    {"verb": "interact", "target": "TemplarCourtsDoor", "skip_if": "a5q2-UsedKey"},
    {"verb": "wait_flag", "flag": "a5q2-UsedKey"},
    {"verb": "goto", "area": "1_5_4", "via": "transition", "skip_if": "a5q4-KilledBoss"},
    {"verb": "goto", "area": "1_5_5", "via": "transition", "skip_if": "a5q4-KilledBoss"},
    {"verb": "kill", "target": "AvariusCasticus", "skip_if": "a5q4-KilledBoss"},
    {"verb": "kill", "target": "AvariusCasticusDivine", "skip_if": "a5q4-KilledBoss"},  # only if phase 2 exists
    {"verb": "wait_flag", "flag": "a5q4-KilledBoss"},
    {"verb": "talk", "npc": "SinInnocence", "option": "Kitava", "skip_if": "a5q6-QuestReceived"},
    {"verb": "talk", "npc": "Lani", "option": "High Templar Avarius", "skip_if": "a5q4-Congratulated"},
    {"verb": "reward", "npc": "Lani", "option": "Avarius Reward", "pick": ["Uncarved Gemstone"],
     "skip_if": "a5q4-QuestRewardTaken"},
]
```

### Risks

- The Templar Courts door is a tile quest object that is not created. It needs the use-with-item rule (take the key,
  set `a5q2-UsedKey`, transition). Without it act 5 stops in Oriath Square.
- `a5q4-QuestReceived` and `a5q4-SeenInnocence` have no setter in the client data. Neither is needed to progress.
- Avarius' second form, the arena spawners and the statues are not built. For the driver, the phase-1 death must set
  `a5q4-KilledBoss` (or spawn MV 5475).
- SinInnocence is an out-of-town NPC that must be spawned. Without him `a5q6-QuestReceived` is never set. The Ossuary
  and the Sign do not check that flag, but the a5q6 log and gossip rows do.
- The server ignores the serialisation flags, so the pre- and post-Avarius transitions are both sent. The Chamber's
  exit can lead to the old Templar Courts after the kill (`1_5_4` is still enterable with `a5q2-UsedKey`).
- `DestroyedOriathObstructions` are never shown.

## a5q5 "The King's Feast" (Quest row 67, Quest Type 0; optional in practice)

**Start:** Bannon in town ("Utula"). **Finish:** Bannon's reward.
**DONE flag (driver):** `a5q5-QuestRewardTaken`. Offer 38 has only magic and rare weapons and shields, so it cannot be
built. Until it can, `a5q5-Congratulated` counts as done.
**Prerequisites:**
- `BannonRescued`, so that BannonTown is in town (the log's Order 2 needs it).
- NPCTalk 863 has no needs except not `a5q5-KilledBoss`.
- `1_5_3b` needs `a5q4-KilledBoss`.

### Sequence

| # | Real-game step | Area | Event | Flags set | Notes / source |
|---|---|---|---|---|---|
| 1 | Rescue Bannon | `1_5_5` The Chamber of Innocence | NPCTalk 854 BannonInnocence idx 0 "Rescue" (not `BannonRescued`) | `BannonRescued` (QF4) | `Script triggerer.FinishNPCChat();`; row 855 is the repeat (needs `BannonRescued`). Bannon stands at `Unique/entrance_01.arm` (263, 313), by the waypoint. Log text: "Talk to Bannon in the Chamber of Innocence" |
| 2 | Meet Bannon in town | `1_5_town` | NPCTalk 856 BannonTown idx 0 "Introduction" | `BannonIntroduction` | |
| 3 | Get the quest | `1_5_town` | NPCTalk 863 BannonTown idx 7 "Utula" (not `a5q5-KilledBoss`) | `a5q5-QuestReceived` (QF4) | Utula has left town (wiki) |
| 4 | Find Utula | `1_5_3b` The Ruined Square | seen MV 5770 `BlacksmithGolem/BlacksmithGolem` "Utula, Stone and Steel" | `a5q5-SeenBoss` | |
| 5 | Kill Utula | `1_5_3b` | kill MV 5770 | `a5q5-KilledBoss` | |
| 6 | Talk to Bannon | `1_5_town` | NPCTalk 864 BannonTown idx 8 "Utula" (needs `a5q5-KilledBoss`) | `a5q5-Congratulated` (QF4) | |
| 7 | Reward | `1_5_town` | NPCTalk 865 BannonTown idx 9 "Utula Reward", offer 38 | `a5q5-QuestRewardSeen`, `a5q5-QuestRewardTaken` | |

### Objects, NPCs and bosses

| What | Type / MV Id / BaseItemTypes Id | Where it comes from | Server today | Needed |
|---|---|---|---|---|
| Bannon (Chamber) | `Metadata/NPC/Act5/BannonInnocence` (NPC, scale 95) | room line `Metadata/Terrain/Act5/Area5/Rooms/Unique/entrance_01.arm` (263, 313) | not created (out-of-town NPC) | create it once `a5q4-KilledBoss` is set (the wiki says he is met after Avarius; the client data shows no flag rule); NPCTalk 854/855 with `FinishNPCChat` |
| Bannon (town) | `Metadata/NPC/Act5/BannonTown` | `town_01.arm` | placed, from the first visit | should appear only once `BannonRescued` is set (no client data rule found; server-side) |
| Utula (town) | `Metadata/NPC/Act5/Utula` | `town_01.arm` | placed, always | should leave town on `a5q4-KilledBoss` (wiki; Lani's 818 / Vilenta's 837 "Utula" traitor gossip) |
| Utula, Stone and Steel | MV 5770 (Monster with **NPC** (`marker_enabled = false`), Transitionable, MinimapIcon `hide_on_transition`) | Bosses[0] of `1_5_3b`, point `mapboss1` in `Metadata/Terrain/Act5/Area3b/Rooms/Unique/park.arm` (104, 150) (room placed at seed 1, (1265, 161)) | **not placed**: numbered `mapboss1` is ignored (BOSS-FINDINGS 1) and the variety is "NOT spawnable: needs NPC" | the shared numbered-mapboss fix, plus the NPC component on a monster (new serialisation), plus kill and seen triggers |
| The Risen Matriarch | MV 5318 (not a quest boss) | Bosses[1], `mapboss2` in `Unique/fountain.arm` (291, 164) | not placed | optional |

### Gates

`1_5_3b` needs `a5q4-KilledBoss`. No blockers.

### Rewards

Offer 38 `a5q5` (taken `a5q5-QuestRewardTaken`), Bannon NPCTalk 865 "Utula Reward". Six item types per class, each
offered rare (r2) and magic (r1), at level 44:

| Class | Choices |
|---|---|
| Marauder | Tomahawk, Headsman Axe, Barbed Club, Steelhead, Cutlass, Painted Tower Shield |
| Duelist | Decurve Bow, Cutlass, Highland Blade, Barbed Club, Tomahawk, Maple Round Shield |
| Ranger | Decurve Bow, Blunt Arrow Quiver, Burnished Foil, Butcher Knife, Cutlass, Oak Buckler |
| Shadow | Gouger, Butcher Knife, Coiled Wand, Decurve Bow, Blunt Arrow Quiver, Redwood Spiked Shield |
| Witch | Coiled Wand, Military Staff, Sekhem, Butcher Knife, Gouger, Walnut Spirit Shield |
| Templar | Military Staff, Sekhem, Barbed Club, Steelhead, Coiled Wand, Etched Kite Shield |
| Scion | Military Staff, Coiled Wand, Tomahawk, Highland Blade, Decurve Bow, Blunt Arrow Quiver |

- None can be built today.
- **Wiki conflict:** the wiki says Bannon offers "a choice of three unique jewels". The 3.29 table has class weapons
  instead. The table wins.
- No static reward.

### Quest log check

- 5 → `a5q5-QuestReceived` → Kill Utula (travel to the Ruined Square)
- 4 → `a5q5-SeenBoss` → Kill Utula
- 3 → `a5q5-KilledBoss` → Talk to Bannon (in the Chamber of Innocence)
- 2 → `a5q5-KilledBoss` + `BannonRescued` → Talk to Bannon for your reward
- 1 → `a5q5-QuestRewardSeen` → Talk to Bannon for your reward
- 0 → `a5q5-QuestRewardTaken` → Bannon has rewarded you

### Driver steps (draft)

```python
QUEST = "a5q5"
DONE = "a5q5-QuestRewardTaken"  # until magic/rare weapons can be built, stop at a5q5-Congratulated
STEPS = [
    {"verb": "goto", "area": "1_5_5", "via": "waypoint", "skip_if": "BannonRescued"},
    {"verb": "talk", "npc": "BannonInnocence", "option": "Rescue", "skip_if": "BannonRescued"},
    {"verb": "talk", "npc": "BannonTown", "option": "Introduction", "skip_if": "BannonIntroduction"},
    {"verb": "talk", "npc": "BannonTown", "option": "Utula", "skip_if": "a5q5-QuestReceived"},
    {"verb": "goto", "area": "1_5_3b", "skip_if": "a5q5-KilledBoss"},
    {"verb": "kill", "target": "BlacksmithGolem", "skip_if": "a5q5-KilledBoss"},
    {"verb": "wait_flag", "flag": "a5q5-KilledBoss"},
    {"verb": "talk", "npc": "BannonTown", "option": "Utula", "skip_if": "a5q5-Congratulated"},
    {"verb": "reward", "npc": "BannonTown", "option": "Utula Reward", "skip_if": "a5q5-QuestRewardTaken"},
]
```

### Risks

- Utula is not placed: numbered `mapboss1`, and the variety needs the NPC component. This is high risk, but the quest
  is not needed for act 6.
- BannonInnocence is an out-of-town NPC that is not created.
- The town NPC visibility rules (Utula gone after `a5q4-KilledBoss`, BannonTown only after `BannonRescued`) are not in
  the client data I found. The server places town NPCs from the room line alone.
- The reward cannot be built.

## a5q6 "The Ravenous God" (Quest row 68, main) and the Act 5 → 6 transition

**Start:** Sin in The Chamber of Innocence (NPCTalk 882). **Finish:** arrival in Lioneye's Watch (`2_6_town`).
**DONE flag (driver):** `Visited-6_town` (log Order 0, "Quest Failed": Kitava cannot be beaten in act 5).
**Prerequisites:** `a5q4-KilledBoss` (opens `1_5_3b`, `1_5_6`; the Ossuary has no other gate).

### Sequence

| # | Real-game step | Area | Event | Flags set | Notes / source |
|---|---|---|---|---|---|
| 1 | Get the quest | `1_5_5` | NPCTalk 882 SinInnocence "Kitava" | `a5q6-QuestReceived` | see a5q4 step 11 |
| 2 | Back through the Torched Courts | `1_5_4b` The Torched Courts | enter | `Visited-5_4b` | |
| 3 | The Ruined Square | `1_5_3b` The Ruined Square | enter | `Visited-5_3b` | waypoint next to the Ossuary exit (wiki) |
| 4 | The Ossuary | `1_5_6` The Ossuary | enter | `Visited-5_6` | |
| 5 | Open the Tomb of the First Templar | `1_5_6` | use chest `Metadata/Chests/QuestChests/Ossuary/PurityBox` (Chests 202) | — | tile `Metadata/Terrain/Theopolis/Ossuary/Feature/ossuary_quest_v01_01.tdt` record `ossuary_quest_v01_01_animation`, tile cell (11, 11) (seed 1 tile at (644, 598)) |
| 6 | Take the Sign of Purity | `1_5_6` | pick up BaseItemTypes 1570 `Metadata/Items/QuestItems/Act5/KitavaKey` "Sign of Purity" (2×3) | `a5q6-HaveItem`, `a5q6-SeenItem` | `.it` `grant_flag a5q6-HaveItem`, `extra_flag a5q6-SeenItem`, `remove_flag a5q6-DeliveredItem`. `a5q6-SeenItem` opens the rooftop (gate and client blockers) |
| 7 | (optional) Sin in the Ossuary | `1_5_6` | `Metadata/NPC/Act5/SinOssuary` appears (NPCBlock, Transitionable `entrance_no_soul_01`); NPCTalk 888 "The Sign of Purity" (not `SinPCHasSign`) | `SinPCHasSign` (QF4) | `FinishNPCChat`. Room line `Unique/boss_01.arm` (180, 116). When he transitions is not in the data (probably after the pickup) |
| 8 | (optional) lever and hidden door | `1_5_6` | `Metadata/Terrain/Act5/Area6/Objects/Ossuary_BossSwitch` (3-state lever with an **NPC** component, `boss_01.arm` (125, 168)) opens `Ossuary_HiddenDoor` (TriggerableBlockage, tile `hidden_Ossuary_SecretWall_OpenAnimation_v01_01.tdt` (50, 34)) | — | shortcut back to the entrance (wiki "hidden passage"); not needed with teleports |
| 9 | The flame wall drops | `1_5_3b` | `a5q6-SeenItem` set | — | `CathedralRoofBlocker` ×18 (`unblock_quest_flag a5q6minusSeenItem`, client-side) and `CathedralRoofFlame` (server_only, `serialisation_stop_flag a5q6-SeenItem`) in `Unique/entrance_04.arm` |
| 10 | Climb to the rooftop | `1_5_8` The Cathedral Rooftop | enter (needs `a5q4-KilledBoss`, `a5q6-SeenItem`) | `Visited-5_8` | |
| 11 | See Kitava | `1_5_8` | seen MV 5747 `KitavaBoss/Kitava1` | `a5q6-SeenBoss` | only in preset EpilogueAtlasDemo; optional (Order 4) |
| 12 | Place the Sign | `1_5_8` | interact `Metadata/Terrain/Act5/Area8/Objects/ArenaSocket` ("ObjectDisplayTextPuritySignSlot") holding the Sign | `a5q6-DeliveredItem`; the server removes the Sign (remove_flag) | ArenaSocket 1 → 2 (then 3); `SignOfPurity` transitions (loop animation); `KitavaDoor` opens; Kitava emerges (Kitava1 Transitionable `phase1_transition_in`, basic_action `Emerge`) |
| 13 | Fight Kitava | `1_5_8` | MV 5747 (ActBoss, immobile, `set_monster_has_second_form`, `set_monster_no_drops`) | — | he never dies: at about 50% life he enrages and his beam destroys the Sign (wiki) |
| 14 | Kitava destroys the Sign | `1_5_8` | Kitava reaches the threshold (driver: `/kill` Kitava) | `a5q6-CompletedBoss` | QuestStaticRewards 38: −30% to all elemental and chaos resistances, ClientStrings 2910 `KitavaWeaken1` |
| 15 | Sin saves the player | `1_5_8` | the player is moved to `LandingZone` (`Unique/sin_dock.arm` (704, 132); BaseEvents no_serialise, server_only) | — | inferred from the object and the wiki ("revived by Sin"). Lilly appears (`lilly.ot` `serialisation_start_flag a5q6-CompletedBoss`) |
| 16 | Talk to Sin | `1_5_8` | NPCTalk 890 SinKitava idx 0 "Kitava" (not `SinOnKitavaDefeat`) | `SinOnKitavaDefeat` (QF4) | `FinishNPCChat`; 891 is the repeat. Sin at `sin_dock.arm` (746, 132) |
| 17 | Talk to Lilly | `1_5_8` | NPCTalk 892 Lilly idx 0 "Introduction" | `LillyKitavaIntro` (QF4 and QF5) | optional |
| 18 | **Sail to Wraeclast** | `1_5_8` → `2_6_town` | NPCTalk 893 Lilly idx 1 "Sail to Wraeclast" (no needs) | — | `Script triggerer.AreaTransitionTo(2_6_town, 0, 0.5); triggerer.FinishNPCChat();`, `Script2 triggerer.FadeToBlack( 500, 5000, 1000);`. Lilly at `sin_dock.arm` (428, 335) |
| 19 | Arrive in Lioneye's Watch | `2_6_town` Lioneye's Watch | enter (needs `a5q6-CompletedBoss`) | `Visited-6_town` | StartQuestFlags. **a5q6 DONE**; act 6 starts ([act6.md](act6.md)) |

**How the player gets to act 6:** an NPC script, not an object or an automatic move. Lilly's NPCTalk 893 calls
`AreaTransitionTo(2_6_town, 0, 0.5)`, which puts the player in the town itself, not in The Twilight Strand (`2_6_1`).
The `0` is probably the arrival index and `0.5` a delay; neither is decoded.

WorldAreas also lists `2_6_town` among `1_5_8`'s connections (and `1_5_8` among `2_6_town`'s). The rooftop's four
`cathedral_roof_transition_v01_01` AreaTransitions and the `PortalToggleable` (bossentry (218, 584)) are the way back
for later visits. `2_6_1` is entered from `2_6_town` (act 6).

### Objects, NPCs and bosses

| What | Type / MV Id / BaseItemTypes Id | Where it comes from | Server today | Needed |
|---|---|---|---|---|
| Tomb of the First Templar | `Metadata/Chests/QuestChests/Ossuary/PurityBox` (QuestChest: Chest, MinimapIcon, InteractionAction, Stats, ObjectMagicProperties; blocking, static) | tile record (above) | not created (not in `AreaObjects.QuestTypes`) | add it as a tile quest chest (a1q4 nest pattern); drop the Sign once while the character has no `a5q6-HaveItem` / `a5q6-DeliveredItem` |
| Sign of Purity | BaseItemTypes 1570 | Chests 202 `BaseItemTypesKey` | — | pickup sets `a5q6-HaveItem` + `a5q6-SeenItem`; removed at the socket |
| Sin (Ossuary) | `Metadata/NPC/Act5/SinOssuary` | `boss_01.arm` (180, 116) | not created | optional |
| Lever / hidden door | `Ossuary_BossSwitch` (Transitionable 3 states, **NPC**, MinimapIcon "LabyrinthLever", InteractionAction) / `Ossuary_HiddenDoor` | `boss_01.arm` / tile | not created | optional (the NPC component on a lever would be new) |
| Rooftop flame wall | `Metadata/QuestObjects/Act5/CathedralRoofBlocker` ×18 (object_size 4, blocking, `unblock_quest_flag a5q6minusSeenItem`, no_serialise) | room lines `Metadata/Terrain/Act5/Area3b/Rooms/Unique/entrance_04.arm` (458..528, 186..228) | client-side: the flag alone opens it | close the server's own walk estimate (`QuestBarriers`) until `a5q6-SeenItem` |
| Flame effect | `Metadata/QuestObjects/Act5/CathedralRoofFlame` (server_only, Animated, model `forcewall.ao`) | `entrance_04.arm` (504, 215) | not created | create it while `a5q6-SeenItem` is missing (cosmetic) |
| Kitava | MV 5747 `Metadata/Monsters/KitavaBoss/Kitava1` | Bosses of `1_5_8`; point **`force:mapboss`** in `Metadata/Terrain/Act5/Area8/Rooms/Unique/bossentry.arm` (217, 658) (room placed at seed 1, (1518, 2484)) | **not placed**: `force:mapboss` is ignored and the room is not in `room-points.json` (BOSS-FINDINGS 1) | the shared numbered/forced mapboss fix; spawn him after the Sign is placed (or at once); his "death" or life threshold sets `a5q6-CompletedBoss` (no death animation needed); no drops |
| Sign of Purity (arena) | `Metadata/Monsters/KitavaBoss/SignOfPurity/SignOfPurity` (Positioned team 1, Animated, Transitionable, Pathfinding immobile, BaseEvents server_only, Stats, **Buffs, Life, Actor**) | room line `bossentry.arm` (218, 558) | not created | monster-like object, which is new serialisation; cosmetic for the driver |
| Sign socket | `Metadata/Terrain/Act5/Area8/Objects/ArenaSocket` (Transitionable 3 states, InteractionAction, `set_disable_during_hellscape`) | room line `bossentry.arm` (218, 558) | not created | create it; use with the Sign → state 2/3, `a5q6-DeliveredItem`, take the item, start the fight |
| Arena doors | `Metadata/Terrain/Act5/Area8/Objects/KitavaDoor` (Transitionable) | tile `Metadata/Terrain/Theopolis/CathedralRoof/Feature/cathedralroof_boss_area_v02_01.tdt` record `cathedralroof_boss_area_v02_01_granddoors` (104, 34) | not created | cosmetic |
| Ladder | `Metadata/QuestObjects/Act5/CathedralKitavaLadder` (AreaTransition + Transitionable: targetable only after its transition) | tile `cathedral_roof_transition_v01_02.tdt` record `cathedral_roof_transition_v01_02_ladder_transition` (33, 21) | created as a plain AreaTransition (always targetable) | send it disabled until the fight is over |
| Portal | `Metadata/MiscellaneousObjects/PortalToggleable` (AreaTransition + Transitionable) | `bossentry.arm` (218, 584) | as above | same; the destination is not decoded |
| Sin (dock) | `Metadata/NPC/Act5/SinKitava` | `Metadata/Terrain/Act5/Area8/Rooms/Unique/sin_dock.arm` (746, 132) | not created (out-of-town NPC) | create it; NPCTalk 890/891 |
| Lilly Roth | `Metadata/NPC/Act5/Lilly` (NPCBlock, model `Metadata/NPC/Act6/Lilly.ao`, `serialisation_start_flag a5q6-CompletedBoss`) | `sin_dock.arm` (428, 335) | not created | create it once `a5q6-CompletedBoss` is set; NPCTalk 893's `AreaTransitionTo` script (and `FadeToBlack`) is **new server work** |
| Landing zone | `Metadata/Terrain/Act5/Area8/Objects/LandingZone` (no_serialise, server_only) | `sin_dock.arm` (704, 132) | — | teleport target after the fight |

### Gates

- `1_5_8` needs `a5q4-KilledBoss` + `a5q6-SeenItem` (client).
- Client blockers `CathedralRoofBlocker` (`unblock_quest_flag a5q6-SeenItem`) in The Ruined Square; the server's
  `QuestBarriers` must mirror them.
- `2_6_town`, `2_6_1`…`2_6_4` need `a5q6-CompletedBoss`. The Gateway must also accept Lilly's scripted transition.

### Rewards

There is no reward offer. The quest's "reward" is a penalty:
- QuestStaticRewards 38, quest a5q6, flag `a5q6-CompletedBoss`: stats `base_resist_all_elements_%` = −30 and
  `base_chaos_damage_resistance_%` = −30 (hard values [−30, −30]), passive points 0, message ClientStrings 2910
  `KitavaWeaken1` ("permanently weakened … −30% to all Resistances").
- **Server today:** `Program.Player.cs` `ResistancePenaltyAct5 = ["a5q6-CompletedBoss", "SinOnKitavaDefeat"]` already
  mirrors −30% from either flag. That matches QSR 38's flag; `SinOnKitavaDefeat` is an extra trigger that never fires
  without `a5q6-CompletedBoss` in normal play.
- Showing the ClientStrings 2910 message is not done; it is cosmetic.

### Quest log check

- 10 → `a5q6-QuestReceived` → Travel back through the Templar Courts
- 9 → `Visited-5_4b` → (travel back through the Torched Courts)
- 8 → `Visited-5_3b` → Find the entrance to the Ossuary
- 7 → `Visited-5_6` → Find the Sign of Purity
- 6 → `a5q6-HaveItem` → Use the Sign of Purity to defeat Kitava
- 5 → `a5q6-HaveItem` + `Visited-5_8` → (use the Sign)
- 4 → `a5q6-HaveItem` + `a5q6-SeenBoss` → Kill Kitava
- 3 → `a5q6-DeliveredItem` → Kill Kitava
- 2 → `a5q6-CompletedBoss` → Talk to Sin
- 1 → `SinOnKitavaDefeat` → Return to Wraeclast (take the ship)
- 0 → `Visited-6_town` → Quest Failed

### Driver steps (draft)

```python
QUEST = "a5q6"
DONE = "Visited-6_town"
STEPS = [
    {"verb": "goto", "area": "1_5_6", "skip_if": "a5q6-SeenItem"},
    {"verb": "interact", "target": "PurityBox", "skip_if": "a5q6-SeenItem"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act5/KitavaKey", "skip_if": "a5q6-SeenItem"},
    {"verb": "wait_flag", "flag": "a5q6-SeenItem"},
    {"verb": "goto", "area": "1_5_8", "skip_if": "a5q6-CompletedBoss"},
    {"verb": "interact", "target": "ArenaSocket", "skip_if": "a5q6-DeliveredItem"},
    {"verb": "kill", "target": "Kitava1", "skip_if": "a5q6-CompletedBoss"},
    {"verb": "wait_flag", "flag": "a5q6-CompletedBoss"},
    {"verb": "talk", "npc": "SinKitava", "option": "Kitava", "skip_if": "SinOnKitavaDefeat"},
    {"verb": "talk", "npc": "Lilly", "option": "Sail to Wraeclast"},
    {"verb": "wait_flag", "flag": "Visited-6_town", "timeout": 60},
]
```

### Risks

- **Kitava is not placed:** `force:mapboss` and `bossentry.arm` are missing from `room-points.json`. This is the shared
  numbered-mapboss fix. His fight never ends in a death: the server must set `a5q6-CompletedBoss` when he reaches the
  threshold or when `/kill` hits him. `OnMonsterKilled` on MV 5747 is enough for the driver.
- The Ossuary's `PurityBox` tile chest is not created. Without the Sign there is neither `a5q6-SeenItem` (needed for
  the rooftop gate) nor `a5q6-DeliveredItem`.
- `ArenaSocket` (use with item) is not created. A driver could skip it if the server let Kitava's kill trigger work
  without `a5q6-DeliveredItem`, but the log then jumps from Order 4 to 2.
- `SinKitava` and `Lilly` are out-of-town NPCs that are not created. Lilly must honour `serialisation_start_flag`.
- **NPCTalk scripts are not implemented:** `AreaTransitionTo`, `FadeToBlack`, `FinishNPCChat`, `DoNPCChat` (no code
  in `src`). Without `AreaTransitionTo` the only way to act 6 is `/goto 2_6_town`, which still needs
  `a5q6-CompletedBoss`.
- Moving the player to `LandingZone` after the fight is inferred, not proven. With teleports the driver can `/tp` to
  SinKitava instead.
- `SignOfPurity` (Actor, Life, Buffs) and the Kitava phase cut-scenes are cosmetic and can be skipped.

## a5q7 "Kitava's Torments" (Quest row 69, side)

**Start:** Lani after Avarius (NPCTalk 827). **Finish:** use Lani's Book of Skill.
**DONE flag (driver):** `a5q7-UsedRewardBook` (QuestStaticRewards 13: 1 passive point + 2 respec points).
**Prerequisites:** `a5q4-KilledBoss` (NPCTalk 827 needs it; `1_5_7` is gated by it).

### Sequence

| # | Real-game step | Area | Event | Flags set | Notes / source |
|---|---|---|---|---|---|
| 1 | Get the quest | `1_5_town` | NPCTalk 827 Lani idx 16 "Kitava's Torments" (needs `a5q4-KilledBoss`, not `a5q7-DeliveredItems`) | `a5q7-QuestReceived` (QF4) | |
| 2 | Enter the Reliquary | `1_5_7` The Reliquary | enter | `Visited-5_7` | waypoint (tile `Reliquary_waypoint_v01_01.tdt`) |
| 3 | Tukohama's Tooth | `1_5_7` | use `Metadata/Chests/QuestChests/Reliquary/RelicCase1` (Chests 203) and pick up BaseItemTypes 1571 `Metadata/Items/QuestItems/Act5/Torment1` | `a5q7-HaveItem1` | room line `Metadata/Terrain/Act5/Area7/Rooms/Unique/kitavachest_01.arm` (262, 230) |
| 4 | Hinekora's Hair | `1_5_7` | `RelicCase2` (Chests 204) → BaseItemTypes 1572 `…/Act5/Torment2` (2×2) | `a5q7-HaveItem2` | `kitavachest_02.arm` (29, 69) |
| 5 | Valako's Jaw | `1_5_7` | `RelicCase3` (Chests 205) → BaseItemTypes 1573 `…/Act5/Torment3` (2×1) | `a5q7-HaveItem3` | `kitavachest_03.arm` (172, 69) |
| 6 | Hand them in | `1_5_town` | NPCTalk 828 Lani idx 17 "Kitava's Torments" (needs all three HaveItem flags) | `a5q7-DeliveredItems` (QF4) | the server takes all three (`remove_flag a5q7-DeliveredItems` on each `.it`) |
| 7 | Reward | `1_5_town` | NPCTalk 829 Lani idx 18 "Torments Reward", offer 39 | `a5q7-QuestRewardSeen` | |
| 8 | Take and use the book | — | BaseItemTypes 1575 `Metadata/Items/QuestItems/Act5/Book-a5q7` (extends `AbstractPassiveSkillAnd2RespecsBook`) | `a5q7-HaveRewardBook`, then `a5q7-UsedRewardBook` | QSR 13 |

### Objects, NPCs and bosses

| What | Type / MV Id / BaseItemTypes Id | Where it comes from | Server today | Needed |
|---|---|---|---|---|
| Karui Relic Case ×3 | `Metadata/Chests/QuestChests/Reliquary/RelicCase1/2/3` (QuestChest + Transitionable open animation; blocking, static) | room server objects in `kitavachest_01/02/03.arm` (all three placed at seed 1: (966, 138), (828, 966), (276, 276)) | not created | create the room chests; each drops its Torment once while needed (no HaveItemN, no DeliveredItems) |
| Torments | BaseItemTypes 1571–1573 | Chests 203–205 `BaseItemTypesKey` | — | pickup flags; hand-in takes all three |
| Lore stands | `Metadata/NPC/Info/UtulaGlyph1/2/3` (NPCTalk 1239–1241 "Read") | same rooms | not created | optional |
| Vault (not a quest) | `ReliquaryVaultKeyLeft/Right` chests (Key of Devotion BaseItemTypes 1623, Key of Strength 1624), `LeverMissingLeft/Right`, `LeverHidden`, `VaultDoor`, `ReliquaryValutMajor/Minor` | `kitavachest_02/03.arm`, `vault_blocked_01.arm`, tile `Reliquary_Vault_Act_5.tdt` | not created | none (optional loot vault, no quest flags) |
| Reliquary spawners | `Metadata/Monsters/ReliquaryMonster/ReliquaryMonsterSpawner` ×152 | wall tiles | not created | none |

### Gates

`1_5_7` needs `a5q4-KilledBoss`.

### Rewards

- Offer 39 `a5q7`: one Book of Skill, taken flag **null**, so the reward catalog skips it (as offer 36).
- QuestStaticRewards 13: flag `a5q7-UsedRewardBook`, 1 passive point (col1 = 1) and **2 respec points** (col7 = 2).
  The book type `AbstractPassiveSkillAnd2RespecsBook` agrees, and so does the wiki ("1 Passive Skill Point and 2 Passive
  Respec Points").
- **Server today:** `UseSkillBook` grants only +1 passive point for any "Book of Skill". The 2 respec points and the
  `use_flag` are missing.

### Quest log check

- 11 → `a5q7-QuestReceived` → Find Kitava's Torments (travel to the Reliquary)
- 10 → `Visited-5_7` → Find Kitava's Torments
- 9 / 8 / 7 → one of `a5q7-HaveItem3` / `HaveItem2` / `HaveItem1` → Find the rest of Kitava's Torments
- 6 / 5 / 4 → two of them → Find the last of Kitava's Torments
- 3 → all three → Deliver Kitava's Torments to Lani
- 2 → `a5q7-DeliveredItems` → Talk to Lani for your reward
- 1 → `a5q7-HaveRewardBook` → use the Book of Skill
- 0 → `a5q7-UsedRewardBook` → Quest Complete

### Driver steps (draft)

```python
QUEST = "a5q7"
DONE = "a5q7-UsedRewardBook"
STEPS = [
    {"verb": "talk", "npc": "Lani", "option": "Kitava's Torments", "skip_if": "a5q7-QuestReceived"},
    {"verb": "goto", "area": "1_5_7", "skip_if": "a5q7-DeliveredItems"},
    {"verb": "interact", "target": "RelicCase1", "skip_if": "a5q7-HaveItem1"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act5/Torment1", "skip_if": "a5q7-HaveItem1"},
    {"verb": "interact", "target": "RelicCase2", "skip_if": "a5q7-HaveItem2"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act5/Torment2", "skip_if": "a5q7-HaveItem2"},
    {"verb": "interact", "target": "RelicCase3", "skip_if": "a5q7-HaveItem3"},
    {"verb": "pickup", "item": "Metadata/Items/QuestItems/Act5/Torment3", "skip_if": "a5q7-HaveItem3"},
    {"verb": "talk", "npc": "Lani", "option": "Kitava's Torments", "skip_if": "a5q7-DeliveredItems"},
    {"verb": "reward", "npc": "Lani", "option": "Torments Reward", "pick": ["Book of Skill"],
     "skip_if": "a5q7-HaveRewardBook"},
    {"verb": "use_item", "item": "Metadata/Items/QuestItems/Act5/Book-a5q7"},  # not in CONTRACT yet
]
```

Lani has two rows with the option text "Kitava's Torments" (827 gives the quest, 828 is the hand-in). The driver
picks whichever the client shows; only one is eligible at a time.

### Risks

- The three relic-case room chests and their drops are missing.
- The book offer has a null taken flag.
- The 2 respec points and the `use_flag` are not granted.

## Implementation summary

| Quest | Main/side | Features | Risk | Est. size |
|---|---|---|---|---|
| `a5q1b` Return to Oriath | main | kill trigger + seen trigger (Krow, placed), area gate, NPC dialogue, reward offer (magic/rare rings) | low for progress; the reward is blocked on magic/rare items | S |
| `a5q2` The Key to Freedom | main | kill trigger (Casticus, placed), boss quest-item drop (Eyes of Zeal), item pickup flag, NPC dialogue, reward offer (normal flasks buildable) | med: the key drop is new | S |
| `a5q3` In Service to Science | side | room chest → item drop (`SupplyChest`), item pickup flag, NPC hand-in, book of skill (null-flag offer, `use_flag`) | med: room chest and book path | M |
| `a5q4` Death to Purity | main | tile object used with an item (`TemplarCourtsDoor`), quest transition (`OriathSquareToCourts`), area gates, boss with phase (Avarius → Innocence), out-of-town NPC spawned on death (SinInnocence), serialisation-flag area swaps, reward offer (Uncarved Gemstone) | high: the door blocks the act; the phase, Sin spawn and transition swaps are all new | L |
| `a5q5` The King's Feast | side in practice (Type 0) | out-of-town NPC (BannonInnocence), town NPC visibility (Utula, BannonTown), boss on `mapboss1` with an NPC component (Utula), reward offer (magic/rare weapons) | high: Utula cannot be placed or spawned today (not needed for act 6) | M |
| `a5q6` The Ravenous God | main | tile chest → item (PurityBox), item pickup with extra flag, client blocker (`CathedralRoofBlocker`) + QuestBarriers, area gate, socket used with an item (ArenaSocket), scripted boss on `force:mapboss` (Kitava, threshold instead of death), static penalty (already mirrored), out-of-town NPCs (SinKitava, Lilly with serialisation flag), NPCTalk `AreaTransitionTo` transition to act 6 | high: Kitava placement, the socket, both NPCs and the NPC script are all missing | L |
| `a5q7` Kitava's Torments | side | three room chests → items, item pickup flags, NPC hand-in taking three items, book of skill (+1 passive, +2 respec) | med: room chests and book path | M |
