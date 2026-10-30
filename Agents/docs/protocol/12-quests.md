# 12 — Quests

The quest engine (§5), **a1q1 "Enemy at the Gate"** (§6) and **a1q4 "Breaking Some Eggs"** in The Mud Flats (§1–4),
which opens the Submerged Passage, are in.
Without a1q4's flag an invisible wall in front of the cave stops the player (§2).

## 1. The data model

| Table (`data/tables/`) | What it holds | a1q4 |
|---|---|---|
| `Quest.json` | one row per quest: `Id`, `Act`, `Name`, `QuestId`, icon | row 2: `a1q4`, act 1, "Breaking Some Eggs", `QuestId` 4, `Art/2DArt/QuestIcons/mudflats.dds` |
| `QuestFlags.json` | named boolean flags, `Id` + `HASH32` | rows 23–25, 29, 34–37, 50–52 below |
| `QuestStates.json` | the quest log text per state: `Quest` (Quest row), `Order`, `FlagsPresent` / `FlagsMissing` (QuestFlags rows), `Text`, `Message`, `MapPinsKeys1`, `MapPinsTexts` | rows 20–28 |
| `QuestItems.json` | quest item rules (`TriggeredQuestFlag`, `Script` …) | 53 rows; the glyph rows did not decode with our schema (open, not needed: each glyph's `.it` names its flags, below) |
| `BaseItemTypes.json` | the items | rows 1531–1533: the glyphs |
| `Chests.json` | chest types: `Id`, `Name`, `AOFiles`, `BaseItemTypesKey` | rows 183–185: the Rhoa Nests |

A quest's progress is the set of its flags; the quest log shows the first `QuestStates` row (by `Order`) whose
`FlagsPresent` are all set and whose `FlagsMissing` are all clear.

Quest rewards are `QuestRewardOffers` rows (their `QuestFlag` is the offer's "taken" flag) with their `QuestRewards`
rows: `__col1 + 1` is the choice's inventory id, `Reward` the item, `RewardLevel` the gem's level or the item's level,
`Characters` the class rows (empty for any) and `__col5` the rarity. NPCTalk's `QuestRewardOffersKey` points at the
offer, and the row's QuestFlag1/2 lists decide when the client shows it. The Gateway offers every table row whose
items it can build (`QuestRewardCatalog.Create`: level-1 items and normal flasks at their row's level); an offer with
no such row opens nothing, and rows it cannot build yet (offer 8's level-8 magic armours, offer 15's magic belts) are
skipped, so there is no per-offer allow list. Checked live 2026-10-26: offer 3 (Tarkleigh's a1q4 "Glyph Reward").

### a1q4's flags

| QuestFlags row | Id | Set when |
|---|---|---|
| 23 | `a1q4-KnowsAboutGlyphs` | a glyph is picked up (first one) |
| 25 | `a1q4-SeenGlyphWall` | the player sees/clicks the glyph wall |
| 29 | `a1q4-TarkleighClue` | Tarkleigh's hint (town) |
| 50, 51, 52 | `a1q4-HaveGlyph1`, `-HaveGlyph2`, `-HaveGlyph3` | holding each glyph: `GlyphN.it`'s Quest component has `grant_flag` HaveGlyphN, `extra_flag` KnowsAboutGlyphs and `remove_flag` PlacedGlyphs. The Gateway sets them with the pickup and clears them when the wall takes the glyphs |
| **24** | **`a1q4-PlacedGlyphs`** | the glyphs are placed in the wall: **opens the passage** |
| 34–37, 2147, 2148 | `NessaCongratulated`, `QuestRewardSeen`, `QuestRewardTaken`, `Congratulated`, `QuestReward2Seen/Taken` | reward dialogue |

### a1q4's states (QuestStates, highest Order first = earliest)

| Order | Needs | Log text (short) |
|---|---|---|
| 8 | 29 | Investigate the Rhoa Nests (Tarkleigh's clue) |
| 7 | 23 | Find a use for the strange glyph |
| 6 | 25 | Investigate the Mud Flats for a way to lower the water |
| 5 | 25, 29 | Investigate the Rhoa Nests |
| 4 | 23, 25 | Find the Glyphs in the Rhoa Nests |
| 3 | 25, 50, 51, 52 | Take the Glyphs to the Glyph Wall |
| 2 | 24 | Talk to Tarkleigh for your reward |
| 1 | 24, missing 27, 128, 3, 129, 130 | The Submerged Passage is now open |
| 0 | 36 | Quest Complete |

## 2. The objects in The Mud Flats (1_1_3)

| Object | Type (`.ot`) | Where it comes from | Components the 0x2b9 blob carries |
|---|---|---|---|
| Rhoa Nest ×3 | `Metadata/Chests/QuestChests/RhoaChest`, `RhoaChest2`, `RhoaChest3` (Chests rows 183–185; model `Metadata/Chests/QuestChests/beach_rhoanest_v01_01_chest.ao`) | tile `Metadata/Terrain/Beach/Features/rhoanest_v01_01.tdt` (tag `rhoanest`): its `.tmd` has one record `beach_rhoanest_v01_01_chest`, type `RhoaChest`, at tile cell (34.5, 34.5). Only `RhoaChest` appears in tile data, so the server picks which nests become `RhoaChest2`/`3` (one per glyph). The walk-map recorder captures the records (The Mud Flats seed 1: (793, 563), (425, 609), (678, 770), z −57.5) | Positioned, BaseEvents, Animated, **Chest**, Stats, ObjectMagicProperties, InteractionAction, MinimapIcon (extends `Chests/QuestChests/QuestChest` → `Chests/Chest`; all 8 read). Blob: the tutorial chest's ([§ opening encounter](#opening-encounter-2026-10-25)) with the record's fixed height and one more u8 (MinimapIcon flags 0) before the tasks |
| the glyphs | `Metadata/Items/QuestItems/Glyphs/Glyph1` **Haliotis Glyph** (1×1), `Glyph2` **Ammonite Glyph** (1×1), `Glyph3` **Roseus Glyph** (1×2); BaseItemTypes 1531–1533, item class 31 | each nest chest's `BaseItemTypesKey` | items with Base, Quest, RenderItem (Quest has no data): ordinary ground items, picked up into the main inventory |
| Glyph Wall (the thing on the wall the player clicks) | `Metadata/QuestObjects/WaterCave/GlyphWall` (`object_size 8`, MinimapIcon icon 5 `hide_on_transition`, Render "ObjectDisplayTextGlyphWall" ("Strange Glyph Wall"), display type Quest) | cave tile `Metadata/Terrain/Beach/Transitions/BeachToWatercave_v2/beach_to_watercave_v2.tdt`: record `…_stone_switch` at tile cell (38.5, 70.5), z −165.6, model `…/beach_to_watercave_v2_stone_switch.ao`; captured (seed 1: (829, 375), code 3) | Positioned, BaseEvents, Animated, **Transitionable**, MinimapIcon, InteractionAction (`extends "nothing"`, so Transitionable is read). Blob: Positioned with the record's height, Animated with the record's model, u8 state (1 waiting, 2 placed), MinimapIcon `00`, InteractionAction `00`, tasks `00` |
| the waterfall | `Metadata/QuestObjects/WaterCave/WaterTransition` (`waterdrop.ao`; client events: `create_untransitioned` pauses animation 0, `on_transition` plays it, `create_transitioned` goes to its end) | room `Metadata/Terrain/Act1/Area3/Rooms/to_cave.arm`, server object at room (71, 147). The room shares its cells with the cave tile: the wall's record is room cell (38, 70), so the waterfall is placed through the wall's record and its code (seed 1: (906, 342)) | Positioned, BaseEvents, Animated, **Transitionable**. Blob: Positioned (terrain height), Animated `waterdrop.ao`, u8 state, tasks |
| the cave entrance | `Metadata/QuestObjects/WaterCave/WaterCaveAreaTransition` (extends AreaTransition + empty Transitionable) | cave tile record `…_area_transition` at (55.5, 174.5), z −61.3 | live: the client reads only AreaTransition's components (§4) |
| **the invisible wall** | `Metadata/QuestObjects/WaterCave/SunkenPassageBlocker`: `object_size 4`, **`blocking = true`**, **`unblock_quest_flag = "a1q4minusPlacedGlyphs"`**, BaseEvents `no_serialise` | room `to_cave.arm`: 19 of them on a staircase line across the path (room (55..109, 60..114)), model `dead_seagull_v01_03.ao` (not drawn; the doodad capture lists them, seed 1 from (819, 304) to (873, 358)) | **client-side**: the client spawns them from the room and removes the blocking when the flag is set. `minus` stands for `-` (the `.ot` parser `0x141de7b20` replaces it): the flag is `a1q4-PlacedGlyphs`. **Live 2026-10-25: the flag alone opens the way** (`/quest_flag a1q4-PlacedGlyphs`): before it the client stopped at (862, 351) on the line, after it walked on to (881, 335) and entered 1_1_4_1 |
| other cave doodad | `…_waterfall` (Doodad, client) | cave tile record | — |

The nest rooms (`to_cave` excluded) carry the tag `rhoanest` and named points `"%area%-nestpack"` (monster packs
around each nest).

## 3. The sequence (as the player sees it)

1. Open three Rhoa Nests (chests). Each drops one glyph; picking it up sets `KnowsAboutGlyphs` and `HaveGlyphN`.
2. At the Glyph Wall (`SeenGlyphWall`), with all three glyphs: interact. The glyphs are taken, `PlacedGlyphs` is set,
   the wall transitions (client script: `DisableTargetable(); PlayAnimationByIndex(0); FadeOutMusic(500);
   PlayGlobalAudio('Audio/Music/Act1_SandBeach/Mudflats_Resolve.ogg')`), the waterfall transitions (its animation),
   and the blockers stop blocking (quest flag).
3. The Submerged Passage (1_1_4_1) is reachable. Tarkleigh rewards the player in town (`QuestRewardTaken`).

The client builds the quest tracker from the flags alone (live 2026-10-25): "Explore the Mud Flats" after the first
glyph, "Take the Glyphs to the Glyph Wall" with all three and the wall seen, "Talk to Tarkleigh for your reward" after
`PlacedGlyphs`, and the quest leaves the tracker once the reward is taken.

## 4. What the server does (`GlyphQuest.cs`, live 2026-10-25)

| Piece | How |
|---|---|
| The Rhoa Nests | created at the captured records (`<area>_seed<seed>.objects.json`, the same capture as the tutorial chest's); the nests in cell order are shuffled by the seed, and then the first becomes `RhoaChest` (glyph 1), the next `RhoaChest2`, then `RhoaChest3`. They block the server's walk estimate like the client's (Chest.ot `blocking`, size 1). a1q4's objects have ids of their own (0x2e80: nests, 0x2e90 wall, 0x2e91 waterfall, 0x2ea0: opened nests), clear of the ids other code takes |
| Open a nest (0x24), drop its glyph, pick it up | the player walks up (size 1); the nest is opened once per instance (`InstanceState.OpenChest`) and replaced by an opened one under an id of its own (the same id made the client log "Replacing awake object"). Its glyph drops on the nest unless the client has `PlacedGlyphs` or a copy is within reach: carried (any inventory or the cursor), in the personal stash, or on this instance's ground (`GlyphQuest.NestDrops`). A glyph lost another way (dropped in another instance, destroyed, sold) drops again. The pickup puts the glyph and sets the flags the save lacks (`KnowsAboutGlyphs`, `HaveGlyphN`) in one save transaction; once the glyphs are placed a glyph is not picked up but removed (a copy left in another instance), and carried ones are removed on entering an area (the `.it` `remove_flag`; on the message path, so the 0xbf keeps its order with the other inventory changes). About once a second the player tick sets and clears the HaveGlyph flags to follow the glyphs carried (`GlyphQuest.SyncFlags`), so a dropped, sold or stashed glyph clears its flag; it changes no inventory. Quest flags are changed under the Monsters lock and read from other threads as a copy (`ClientState.QuestFlagsSnapshot`, `HasQuestFlag`); a change and its 0x2ba send run one at a time per connection (`ClientState.QuestGate`, held by `SetQuestFlags` and the sync, which calls `SetQuestFlagsHeld`), so an older flag set is never sent last. Live 2026-10-25: the client drops a glyph from the cursor like any item (0x29), the flag cleared within a second, and in a new instance its nest dropped it again; a glyph given after the placement was removed (since then: on the next area entry) |
| Quest flags S→C | 0x2ba to the Player component with update flag 0x02 replaces the set ([16](16-waypoints.md) §3, `InstanceEntities.PlayerQuestFlagsUpdate`); the client unblocks the blockers and updates the tracker by itself (§2, §3). Decompile pointers: Positioned's `.ot` keys `unblock_quest_flag` / `block_quest_flag` (`0x141e2dbb0`), flag name lookup (`0x141de7b20`, `Data/QuestFlags.dat`), interaction `required_quest_flag` / `disable_quest_flag` (`0x141e1cab0`, `0x141e5c790`) |
| Glyph Wall | coming within 60 cells, or using it, sets `SeenGlyphWall`. Used (0x24, walk-up size 7) while the client lacks `PlacedGlyphs`, with the three glyphs in the main inventory or on the cursor: one transaction looks them up, removes them and sets `PlacedGlyphs` (clearing `HaveGlyph1–3`); transactions run one at a time, so a second quick use finds nothing. 0xbf removes them in the client |
| The transition | **0x2ba to Transitionable** (component index 3 for both; data u8 target state, §6): the wall 1 → 2 runs `on_transition` (`DisableTargetable` and the animation: the label went away live), the waterfall 1 → 2 likewise. Created later with state 2 they run `create_transitioned`. Whether the waterfall's animation shows was not confirmed in screenshots |
| The invisible wall on the server | the 19 blockers (from the wall's record through `to_cave.arm`) are blocking objects of size 3 on the server's walk estimate until `PlacedGlyphs`: live, the server and the client both stopped at (862, 351), 0 cells apart (without them the server walked on and was 20 cells off) |
| The exit | `Quests.BreakingSomeEggs.FlagToEnter(1_1_3 → 1_1_4_1)` = `PlacedGlyphs`: the Gateway refuses the cave entrance without the flag and creates it when the flag is set (as a1q1's gate, §6). Every quest check reads the flag as the client has it (`Quests.ClientHas`): the character's own flags plus the World panel's access flags, e.g. `PlacedGlyphs` from an activated Submerged Passage waypoint ([16](16-waypoints.md) §4). Exits, room objects and the passage change only when that changes (`Quests.EffectiveChanges`), so a flag the client already had creates nothing twice, and clearing it (dev) removes nothing the client still opens. Not checked live for a waypoint-only character |
| The cave entrance's Transitionable | live 2026-10-18: `WaterCaveAreaTransition` inherits AreaTransition and adds Transitionable, but the client reads only the parent's components (a Transitionable byte became the task count: Buffer Underflow) and the component keeps state 0, whose post-create event crashes (`0x141ded7b9`). The Gateway creates it as a plain AreaTransition ([08](08-area-transitions.md) §7). How the real server sets that state is open |
| Tarkleigh | his NPCTalk row 40 "Glyph Reward" (needs `PlacedGlyphs`, not `QuestRewardTaken`) opens QuestRewardOffers 3: six level 1 skill gems per class, claimed like Hillock's (live: Frost Bomb, then `a1q4-QuestRewardTaken`). Row 32 "Rhoas" (Tarkleigh's clue; its QuestFlag5 is `TarkleighClue`) is listed by the client; that the generic dialogue finish sets the flag was not checked live |

## 5. The quest engine

**Quest flags are the truth.** A character's quest progress is the set of its quest flags, `QuestFlags.dat` Ids (e.g.
`a1q1-KilledHillock`), saved by name in its save (`data/saves.db`, `QuestFlags`; [saves](../saves.md)): names are readable
and survive game patches, where row numbers do not. The client keeps its own copy as QuestFlags **rows** in the Player
component ([16](16-waypoints.md) §3): the Gateway sends them with the player object and again (0x2ba, flag 0x02,
`PlayerQuestFlagsUpdate`) whenever one changes, together with the World panel's visit and access flags
(`PlayerQuestFlags`). The client acts on them by itself: an area is reachable only when its WorldAreas `EndQuestFlags`
are set (`0x140797750`), and blockers with `unblock_quest_flag` read them (§2). `/quest_flag [<id> [off]]` lists, sets
or clears them (dev).

**One script per quest** (`Quests.cs`, `Quests.Script`). The Gateway asks every script:

| Question | What the Gateway does with the answer |
|---|---|
| `OnMonsterKilled(area, variety)` → flags | sets and saves them when a monster dies (`MonsterKilled` → `SetQuestFlags`) |
| `FlagToEnter(from, to)` → flag | refuses that area transition (C→S 0x24) without the flag, even if the client lets the player reach it |

**Room server objects.** Rooms (`.arm`) list server objects next to their doodads: a type other than
`Metadata/MiscellaneousObjects/Doodad`, e.g. `Metadata/NPC/Act1/Doorman` (the client spawns only plain doodads).
`scripts/re/atlas room-points` marks them with their type in `data/room-points.json` (the room fit skips them, since the
walk-map capture has only the client's doodads), and keeps the rooms that have a `default` start point. Quests do not
create room objects yet: the first ones (§6) do not look or behave right.

**The plan for later quests**, in the same shape:
- more events: an area entered (WorldAreas `StartQuestFlags`, e.g. `Visited-1_1` for The Twilight Strand), an object
  used (0x24), an item picked up (QuestItems `TriggeredQuestFlag`), an NPC talked to;
- more effects: the quest log (`QuestStates`, §1), rewards, server objects of rooms (§6);
- dev presets from `CharacterStartQuestState` (e.g. `Act1Complete`: its quests and flags) to start a character at any act.

## 6. a1q1 "Enemy at the Gate": The Twilight Strand

New characters wash up on The Twilight Strand (`AccountService.StartingArea` = `1_1_1`) and log in there until they
first enter a town ([09](09-area-change.md) §4). Hillock stands before Lioneye's Watch; the way in opens once he is dead.

| What | Where it comes from | What the server does |
|---|---|---|
| the start point | the `washedup` room (`washed_up3.arm`, graph node `washedup` in `macro_beach4.tgr`), point `default` | the area's spawn (`AreaLayouts.For`: a room's `default` point, else the deepest cell) |
| Hillock | WorldAreas `Bosses`: `Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal` (MonsterVarieties 1166) | placed at `mapboss` of `town_entrance_exclude.arm` like any unique boss; within 60 cells (`Quests.SightRange`) he sets `a1q1-SeenHillock`; his death sets `a1q1-KilledHillock` and levels the character to 2 (below) |
| the force wall | `Metadata/Terrain/Act1/Area1/Objects/Tutorial_Blocker_3` in `town_entrance_exclude.arm` at room (92, 127), angle −0.988, **scale 2.7** (model `…/twilight_strand/force_wall/force_wall.ao`) | created blocked while the character lacks `a1q1-KilledHillock`, removed (0x2bb) when it is set (below) |
| the Doorman | `Metadata/NPC/Act1/Doorman` in the same room at (130, 156): Transitionable `num_states 3`, state 3 plays `door_open` | not created yet (below) |
| the gate into Lioneye's Watch | a tile record of `beachtown_south_entrance_test.tdt`: `Metadata/QuestObjects/SouthBeachTownEntrance` (extends AreaTransition, Transitionable `num_states 3`: states 1–2 not targetable, 3 opens and becomes targetable after 0.8 s) | captured by the walk-map recorder; created as a plain AreaTransition (see below). The client lets the player click it once its own quest flags hold `a1q1-KilledHillock` (Lioneye's Watch's `EndQuestFlags`); the Gateway also refuses it without the flag |

The `washedup` room also has a force wall of its own (`Tutorial_Blocker_1`), the `WoundedExile` NPC and a
`BiteZombieSpawner`: the opening tutorial, not done yet (no quest manages them, so none of them is created).

**Hillock's fight** (live 2026-10-21; `BossFights.cs`):
- **Boss life bar.** `ZombieBoss.ot` extends `Metadata/Monsters/BossLifeBar` (MonsterVarieties `BossHealthBar` set): its
  StateMachine shared states are `boss_life_bar`, `disable_life_bar`, `global_first_aggro`, then ZombieBoss's `aggro`.
  `on_state_boss_life_bar_1` calls `SetStaticLifeBarVisibilty(True)`. The StateMachine has an update reader
  (`0x141e3f4a0`, slot +0xa0): 0x2ba with the same data as the creation blob (state count, timer count, per state a
  compact int + u8 flags, per timer u32 + u8); a changed value runs that state's events; a state whose flags lack 0x02
  may take the type's default instead (a check on the state's name). When Hillock aggroes the Gateway sends
  `boss_life_bar` 1 and `aggro` 1 (flags 0x02).
- **Life.** Unique rarity adds `monster_life_+%_final_from_rarity` 698 (Mods.dat MonsterUnique2): 39 × 7.98 = 311 at
  level 1 (14-monster-packs.md).
- **Second stage.** His second granted effect is `ZombieBossExtract` (GrantedEffects 2612, ActiveSkills
  `zombie_boss_extract`, action `ZombieBossExtract`, CastTime 3000; the .act maps it to the `extract` animation). Its stat
  set has no values, so what he regains is the server's: all his life. The hit that takes him to 50% leaves at least 1
  life; he stops and uses the skill (0x1ab, skill id 0x0103 in his Actor list, with the attack's skill stats and a 3 s
  time: without `skill_is_non_pathing` the client played nothing and kept him in the action, frozen, live 2026-10-21;
  S→C 0x1ac, the object header only, ends the action after the stage), cannot drop below 1 life during the
  cast, then 0x2ba steps his Transitionable to 2 (`on_transition`: `HideMeshSegment(…Sword…)` +
  `AddAttached(OneHandSwordZombieHeld.ao, R_Weapon)`: the sword from his back into his hand) and 0x1b1 fills his life.
  Transitionable's `num_states` must be ≥ 2 when an .ot gives it (`0x141e42a80` throws "That number of states is
  unsupported" otherwise); `Transitionable {}` takes the type's default, which the sword step treats as 2.
- **Level 2.** QuestStates' a1q1 text "killed Hillock … and levelled up. Open the Passive Skill Screen" — his kill tops
  the character's experience up to level 2's start (`Quests.LevelAfterKill`); his own experience (about 150) does not
  reach it.
- **No portals.** WorldAreas 1_1_1 has the area mod `MapNoPortals` (`map_disable_portal_use` 1); `AreaModCatalog`
  reads areas' own mods and a Portal Scroll there does nothing and is not spent.

**The force wall** (live 2026-10-21): created at the room fit's place with the room's angle and scale (Positioned flags0
0x20's uniform scale, the x / y stretches at 1: [04](04-player-entity.md) §4.0; `BlockerObject`); `data/room-points.json` (version 2) carries each server object's angle and
scale. The client blocks cells from the wall model's own footprint where the object stands (`0x141d876e0`, through the
Animated model), so without the 2.7 scale the wall was a third of its size, standing beside the gap (2026-10-19). Until 2026-10-23 the 2.7 went into all three scale floats, which the client
multiplies, so the wall was drawn 7.3 times as wide (and blocked that footprint); now only the uniform scale carries
it (not yet seen live).
TriggerableBlockage has no update reader: the Gateway removes the object (0x2bb) when Hillock dies.

The client blocks the wall by itself, but the server's walk estimate did not: it walked on through the wall, and the
next echo (≥ ~92 units apart) snapped the player to the far side (live 2026-10-21). The wall's centre (1403, 679) is in
the rock; it blocks where its line, at its angle as given (grid y down), crosses the neck from Hillock's beach into the
gate corridor, about (1411, 667)–(1421, 652): a player put at (1410, 666) and (1414, 660), 1.3 cells from the line,
stood stuck in it. The server closes that stretch on its own walk map (`QuestBarriers`): the walkable cells within 2.5
cells of the wall's line, up to 60 cells from its centre, kept only when closing them cuts the gate off from Hillock's
`mapboss` point (a stretch over open beach is left open). The player's walk estimate goes around it, so the server
stops where the client stops; nothing is teleported. (Teleporting the player to the server's position once a walk
ended put them inside the client's wall, where they could not move: removed.)

The gate's minimap icon comes with the gate object (its `create_state_1` hides it, the plain AreaTransition the Gateway
sends does not); the Gateway creates it only when Hillock dies. The client's minimap cache (`%LOCALAPPDATA%\Path of
Exile\Minimap`) keeps icons from earlier visits of the same area and seed, so an icon seen before the kill can be an
old one.

**Quest log after the kill.** a1q1's state Order 2 ("… and levelled up. Open the Passive Skill Screen …") needs
`CompletedPassivePointTutorial` (280) and `CompletedAllTutorials` (367) missing. Every character has
`CompletedAllTutorials` (`Quests.EveryCharacter`, as on an account that has done the tutorials), so the log goes to
Order 3, "Enter through the gate for a reward" ("Enter town for your reward").

**What was tried for the wall and the Doorman** (live 2026-10-19; the code was not kept, the layouts below are enough to
write it again): both were created
from the room's place and angle (the room fit is exact), the wall blocked while Hillock lived and removed after; the
Doorman in state 1, then S→C 0x2ba → 3. The wall showed up beside the gap, not across it, and did not block (with
`object_size 0`, its blocking probably comes from cells the terrain marks for TriggerableBlockage); the Doorman was
never drawn, though the client read his blob and the update. Their blobs, as the client read them
(`scripts/re/frida/check-components.js`):
- `Tutorial_Blocker_3`: Positioned (16 bytes), BaseEvents, Animated (u8 0: the type's own model), **TriggerableBlockage**
  (`0x141e44290`: u8 blocked, then **only while blocked** a u8 count and that many u8; a count after an unblocked 0
  shifted the blob: Buffer Underflow), Transitionable (u8 state 1, `num_states` 1).
- `Doorman`: Positioned, Animated (u8 0), BaseEvents, Transitionable (u8 state). 21 bytes, all read.

**S→C 0x2ba to a Transitionable** (update reader `0x1421c2310`, vtable `0x1435dd290` slot +0xa0): data = u8 target
state. The client steps its state (`+0x120`) one at a time toward the target and runs each step's events (the
type's `on_transition` and `on_transition_to_N` going up, the matching events going down). TriggerableBlockage has no
update reader (+0xa0 is a `ret`).

**The gate** (live 2026-10-19): without `a1q1-KilledHillock` in the client's quest flags it shows shut with nothing to
hover; once the Gateway sends the flag (Hillock's death) it can be clicked and leads into Lioneye's Watch. It opens
without its animation: created as a plain AreaTransition, it has no Transitionable. Created as its own type it crashed
the client at once (access violation `0x141df4a96`, right after the blob's tasks): the creation blob stops before
Transitionable (index 8), so its state stays 0, and the post-create step runs before any 0x2ba can arrive. The blob's
**tasks** (`0x141ee00f0`: u8 count, then per task a u16 task type looked up in a registry, `0x141ee0ba0`, and that
type's data; an unknown type throws "Unrecognised task serialised by server; missing registration?") are the next place
to look for how the real server sets such a state.

### The gate's animation: what is known (paused 2026-10-19, low priority)

The gate works without it, so this is cosmetic; the notes are here so the next attempt starts from them.

- **Proven live (Frida):** sent as its own type `SouthBeachTownEntrance` with the 8-component AreaTransition blob, its
  Transitionable (vtable `0x1435dd290`, the client-side subclass) runs its post-create (`0x1421c26f0`, slot 1) with
  **state 0** and `num_states` 3; the post-create then runs the event at index state − 1 and the client crashes in the
  event-script interpreter (`0x141df4a96`, a null `[r12]`). The base Transitionable constructor (`0x141e42410`) sets
  state 1, but this subclass is left at 0 when the blob does not reach it.
- **Creation order** (`0x141ebe190`): every component's slot 2 (+0x10; a `ret` for Transitionable), the u8 header
  (bit0 → object `+0x8c |= 0x80`, bit1 → `+0x8d |= 2`), the **base data** (`0x141dea920`: u8 count, then entries of
  u32 hash + u8 …; they set and remove the object's **script variables**, "Attempted to remove variable '{}' on
  read-only variable container"), the type's first N components (N = type `+0x80`: 8 here, Transitionable is index 8),
  the **tasks** (`0x141ee00f0`), then every component's post-create (`0x141ebe420`, slot 1). Only the base data and
  the tasks run before the post-create, so one of them must set the state if the real server sends this type.
- **Tasks:** a hash map at `0x1445cd8b0` (getter `0x141643780`, lookup `0x141ee0ba0`, FNV-1a of the u16 id) filled by
  139 registrations around `0x141637133`, each `mov edx, <u16 id>` and two function pointers (+0x18, +0x20; the lookup
  calls +0x20, the stub `0x1400f22f0` marks the side a type does not have). The ids look like 16-bit name hashes; the
  factories build their objects through helpers, so the classes' RTTI names were not reached yet.
- **Script commands:** the GEAL function table (registered around `0x141ed56a6`) has `SetTransitionPerPlayerQuestFlag`
  and `SetTransitionPerPlayerLeagueQuestFlag`, and `GetTransitionState`. A Transitionable that follows the player's
  quest flags is what this gate is; no cached `.ot` uses them, so they may live in server-side scripts or other files.
- **Next:** decompile (Ghidra) the tasks' +0x20 factories and the `SetTransitionPerPlayerQuestFlag` implementation;
  or try base-data script variables on the gate, watching with `check-components.js` plus a Frida hook on `0x1421c26f0`
  that logs `+0x120` (the state the post-create sees).

**The Doorman** (paused, low priority): why a created and updated object is not drawn (maybe its `.ao` is the gate's
door, hidden by the tile).

**What does not work yet:**
- The gate opens without its animation (above).
- No Doorman (above). The gate is created only once Hillock is dead, and the Gateway still checks the flag on it.
- No quest log or tracker text beyond what the client derives from the flags, no Tarkleigh reward (needs NPCs).

## 7. a1q5 "Mercy Mission": The Tidal Island

Nessa wants a medicine chest; Bestel says his ship ran aground on The Tidal Island (1_1_2a); Hailrake, the area's
unique boss, drops the Medicine Chest; Nessa takes it and offers two rewards.
Played through live on 2026-10-25 (`MercyMission.cs`, `Program.MercyMission.cs`).

| QuestFlags row | Id | Set by |
|---|---|---|
| 39 | `a1q5-ReceivedQuest` | Nessa's NPCTalk row 5 "Lioneye's Watch" (QuestFlag4/5) |
| 44 | `a1q5-BestelClue` | Bestel's row 62 "The Medicine Chest" (needs 39, not 42 or 49) |
| 49 | `a1q5-HaveMedicineChest` | the server, while the chest is carried (below) |
| 42 | `a1q5-NessaCongratulated` | Nessa's row 1 "The Medicine Chest" (needs 49, not 41): she takes the chest |
| 40, 41 | `a1q5-QuestRewardSeen`, `-QuestRewardTaken` | row 2 "Medicine Chest Reward" (needs 42), offer 1 |
| 1876, 1877 | `a1q5-QuestReward2Seen`, `-QuestReward2Taken` | row 3 "Medicine Chest Reward 2" (needs 42), offer 2 |

QuestStates rows 29–34 give the log and tracker from these flags alone (seen live): "Talk to Bestel" after 39, "Travel
to the Tidal Island / Find the Medicine Chest" after 44, "Travel to Lioneye's Watch" (outside town) or "Talk to Nessa
for your reward" with 49, and the quest leaves the tracker with 42.
The dialogue rows are handled by the generic NPCTalk flags ([17](17-npcs.md)); Tarkleigh's row 55 and Bestel's row 75
are gossip after 42.

**Where the chest comes from: Hailrake.**
The tile `Metadata/Terrain/Beach/Special/kyrenia_boat_medicinequest_v01_01.tdt` has no `.tmd` (no chest record), and
the rooms `Area2a/Rooms/medicine_boat_1.arm` / `_2.arm` (tag `medicineboat`) hold only the points `mapboss` and
`%area%-bosspack` and two snowfall doodads.
WorldAreas 1_1_2a's Bosses is MonsterVarieties 2887 `Metadata/Monsters/Cannibal/CannibalMaleBossHailrake`; the
wiki's Medicine Chest page lists that variety as its `drop_monsters` ("acquired after defeating Hailrake"), and a
separate `…HailrakeGauntletNoQuest` variety exists.
The rooms have too few doodads for the room fit, so they use their captured placement ([14](14-monster-packs.md) §3):
seed 1 puts `mapboss` at (265, 437) on the wreck.
`CannibalBossHailrake.ot` extends `Cannibal`, not `BossLifeBar`, and his row has no `BossHealthBar`: no big life bar.
His death drops the chest where he fell, unless Nessa has one already, this character carries one or one lies on this
instance's ground; a chest lost any other way drops again from the next Hailrake.
The stash is not counted: it is the account's, so another character's stashed chest must not block this one (whose
tracker would still say "Find the Medicine Chest"); a character that stashes its own chest can get a second one.
Hailrake is placed from the area's doodads and rooms: the captures (`1_1_2a_seed<seed>.doodads.json` and `.rooms.json`,
from `live.ps1 -Walkmaps`) when there are some, else the Gateway's own generation of the seed (`ServerLevels`).

**The item.** BaseItemTypes 1534 `Metadata/Items/QuestItems/MedicineSet1`, "Medicine Chest", 2×3, item class 31.
Its `.it` Quest component has `grant_flag = "a1q5minusHaveMedicineChest"` and
`remove_flag = "a1q5minusNessaCongratulated"`.
The Gateway keeps 49 equal to "a chest is carried" (inventories and cursor): on entering an area, after a pickup,
after a read with an item move (drop, place, pick-up, the stash, a sale, a chat command), and about once a second for
the flag alone.
Once 42 is set (or `QuestRewardTaken`), a carried chest is taken away (on entering an area or after an item move: the
inventory changes stay on the message path, in revision order), and a click on a chest on the ground walks the player
to it and removes it from the ground.
Stashed chests stay in the stash; one taken out by a character that is done goes at its next item move.
Choosing Nessa's row 1 removes the chest and sets 42 and clears 49 in one save transaction; without a chest carried
(or with it on offer to a vendor) nothing changes and the option list comes back.

**Rewards.** Offer 1: Medium Life, Medium Mana or Quicksilver Flask, QuestRewards RewardLevel 4 and rarity column 0
(normal): created at item level 4 (poedb lists them as iLv4).
Offer 2: the class's level 1 support gems (Witch: Elemental Proliferation, Spell Cascade, Infused Channelling, Chance
to Poison, Summon Phantasm).
Both are claimed like Hillock's reward (one save with the taken flag, once).
QuestRewardOffers' `__col8` is set on offer 2 (and on a1q4b); its meaning is unknown, the client lists the option
anyway.

## Open

- How the real server gives an AreaTransition subtype its Transitionable state (blob tasks?), so the Strand's gate and
  the Mud Flats' cave entrance play their opening.
- Why the Doorman is not drawn; the list of u8 TriggerableBlockage reads while blocked (none sent).
- The QuestItems row layout (which row is which glyph; `TriggeredQuestFlag`).
- Transitionable on inherited types (`WaterCaveAreaTransition`): how its state gets set.
- How the real server chooses which nests hold which glyph (RhoaChest / 2 / 3); the Gateway shuffles them by the seed.
- Whether the waterfall (`WaterTransition`, `waterdrop.ao`) shows its animation, and its height (the room line has
  37.0245 after the quaternion; it is created at the terrain height).

## Opening encounter (2026-10-25)

New characters retain the class weapon as a pending ground item and both class gems as `OpeningTutorial` rewards in
their save. Flasks are available immediately. The old implementation equipped the weapon and granted both gems in the
main inventory, and never spawned the exile, scene zombie, or chest. Existing characters have no `Tutorial` record and
keep their equipment, gems, and quest progress unchanged.

The fitted `washed_up3.arm` supplies WoundedExile and BiteZombieSpawner positions. Talking without an equipped weapon
plays the weapon prompt; with a weapon it uses the actual NPCTalk script's 8.2/22.8-second scene timing. Transitionable
states are 1 (waiting) and 2 (bitten); state 0 is invalid. The scene rig's bite clip is index 1, sent with 0x1b2 (speed 0
while waiting, 1 when biting). Afterwards the scene is removed and MonsterVarieties **ZombieBite**, row 1159, spawns:
`Metadata/Monsters/Zombies/ZombieBite` uses base object `Metadata/Monsters/Zombies/Drowned/BiteZombie`. These names are
different; looking up the base object as a monster variety silently found nothing. Killing it drops Characters.dat's
StartSkillGem; it is not pre-granted. A disconnect after the conversation resumes with the combat zombie.

The support chest is a **named point**, not a tile object: `tutorial_01.arm` has
`41 64 5.84685 "Metadata/Chests/TutorialSupportGemChest"`. Room-points manifest version 3 retains this point and its
angle. The room has no ordinary room doodads, so it cannot use the usual doodad fit. The recorder captures actual
.arm placements at `0x142040770` (placed room in argument 3: resource handle +0, tile origin +8/+0xc, code +0x1b;
resource data +0x138/+0x139 = untouched room dimensions). `<area>_seed<seed>.rooms.json` stores these placements with
area/seed identity. Seed 1's tutorial room is at (759,483), 138×207 cells, code 0: its chest is therefore **(800,547)**.
Its model comes from Chests.dat AOFiles (`Metadata/Chests/Chests/TutorialChest.ao`). Without the relevant capture the
server waits briefly and logs the missing placement. Use `live.ps1 -Walkmaps` and re-enter the area to record it.
The hook's offsets were located using PR #136's DoodadStage/SpecialNodes notes and verified against native code and the
captured washed-up room's existing NPC positions.

Chest's reader `0x141e138b0` consumes three bytes; bit 0 of the first is opened. Its update slot is empty, so opening
replaces the closed object with an opened one, then drops StartSupportGem. The player must reach interaction range;
a failed path cannot open it remotely. Gem claims and their saved flags commit atomically and cannot be repeated
after reconnect. Ground rollback preserves the item id used by the pending reward.

Live verified on a separate level-one Witch: ground weapon pickup/equip, exile conversation and bite transition,
Hungry Corpse spawn/death, Fireball drop/pickup, Large Chest at the captured location, Arcane Surge Support drop/pickup,
and saved claim flags. Dev `/god` was enabled for combat/navigation verification. The full tutorial popup/acknowledgement
workflow and force walls 1/2 are not implemented; `CompletedAllTutorials` remains the existing account-style projection.
