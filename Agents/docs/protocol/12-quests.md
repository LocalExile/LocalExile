# 12 — Quests

The quest engine (§5: declarative tables per quest, quest items, dialogue, rewards, the oracle; "Adding a quest") runs
all of Act 1: **a1q1 "Enemy at the Gate"** (§6), **a1q5 "Mercy Mission"** (§7), **a1q4 "Breaking Some Eggs"** in The Mud
Flats (§1–4), which opens the Submerged Passage, **a1q8 "A Dirty Job"** and **a1q7 "The Dweller of the Deep"** (§8),
**a1q2 "The Caged Brute"**, **a1q3 "The Siren's Cadence"** and Act 1's part of **a1q9 "The Way Forward"** (§9), and
**a1q6 "The Marooned Mariner"** (§10), then Acts 2 to 6 (§11–15) and Acts 7–10 with the Epilogue (§16–19).
Without a1q4's flag an invisible wall in front of the cave stops the player (§2).

## 1. The data model

| Table (`data/tables/`) | What it holds | a1q4 |
|---|---|---|
| `Quest.json` | one row per quest: `Id`, `Act`, `Name`, `QuestId`, icon | row 2: `a1q4`, act 1, "Breaking Some Eggs", `QuestId` 4, `Art/2DArt/QuestIcons/mudflats.dds` |
| `QuestFlags.json` | named boolean flags, `Id` + `HASH32` | rows 23–25, 29, 34–37, 50–52 below |
| `QuestStates.json` | the quest log text per state: `Quest` (Quest row), `Order`, `FlagsPresent` / `FlagsMissing` (QuestFlags rows), `Text`, `Message`, `MapPinsKeys1`, `MapPinsTexts` | rows 20–28 |
| `QuestItems.json` | league quest items (`TriggeredQuestFlag`, `Script` …) | 53 rows, none of the campaign's: the campaign's quest items name their flags in their `.it` Quest component (§5) |
| `BaseItemTypes.json` | the items | rows 1531–1533: the glyphs |
| `Chests.json` | chest types: `Id`, `Name`, `AOFiles`, `BaseItemTypesKey` | rows 183–185: the Rhoa Nests |

A quest's progress is the set of its flags; the quest log shows the first `QuestStates` row (by `Order`) whose
`FlagsPresent` are all set and whose `FlagsMissing` are all clear.

Quest rewards are `QuestRewardOffers` rows (their `QuestFlag` is the offer's "taken" flag) with their `QuestRewards`
rows: `__col1 + 1` is the choice's inventory id, `Reward` the item, `RewardLevel` the gem's level or the item's level,
`Characters` the class rows (empty for any), `__col5` the rarity and `__col16` the mode (0 both, 1 regular only, 2
Ruthless only: offer 8's level-8 magic armours are Ruthless's alternatives). NPCTalk's `QuestRewardOffersKey` points at
the offer, and the row's QuestFlag1/2 lists decide when the client shows it. The Gateway offers every table row whose
items it can build (`QuestRewardCatalog.Create`: gems, currency and flasks at their row's level, gear rolled at its item
level and rarity with the mode's `ItemRolls`); an offer with no such row opens nothing, so there is no per-offer allow
list. Offers without a `QuestFlag` (the quest books 5, 6, 9, 11 and the later acts' keys and amulets) give a quest item
whose own flags tell that it was taken: its grant_flag is held (a1q7-HaveRewardBook) or its remove_flag set
(a1q7-UsedRewardBook), and the claim sets the grant flag in the same save; an offer whose item has no such flags either
(the bandit respec books, heist coins) opens nothing yet. Checked live 2026-10-26: offer 3 (Tarkleigh's a1q4 "Glyph
Reward").

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
| Open a nest (0x24), drop its glyph, pick it up | the player walks up (size 1); the nest is opened once per instance (`InstanceState.OpenChest`) and replaced by an opened one under an id of its own (the same id made the client log "Replacing awake object"). Its glyph drops on the nest unless the character no longer needs it (the engine's quest item rules, §5: `PlacedGlyphs` set, or a copy carried or on this instance's ground; the stash no longer counts since the engine, as for a1q5's chest). A glyph lost another way (dropped in another instance, destroyed, sold) drops again. The pickup puts the glyph and sets the flags the save lacks (`KnowsAboutGlyphs`, `HaveGlyphN`) in one save transaction (`QuestItems.Claim`); once the glyphs are placed a glyph is not picked up but removed (a copy left in another instance), and carried ones are removed on entering an area (the `.it` `remove_flag`; on the message path, so the 0xbf keeps its order with the other inventory changes). About once a second the player tick sets and clears the HaveGlyph flags to follow the glyphs carried (`QuestItems.Sync`), so a dropped, sold or stashed glyph clears its flag; it changes no inventory. Quest flags are changed under the Monsters lock and read from other threads as a copy (`ClientState.QuestFlagsSnapshot`, `HasQuestFlag`); a change and its 0x2ba send run one at a time per connection (`ClientState.QuestGate`, held by `SetQuestFlags` and the sync, which calls `SetQuestFlagsHeld`), so an older flag set is never sent last. Live 2026-10-25: the client drops a glyph from the cursor like any item (0x29), the flag cleared within a second, and in a new instance its nest dropped it again; a glyph given after the placement was removed (since then: on the next area entry) |
| Quest flags S→C | 0x2ba to the Player component with update flag 0x02 replaces the set ([16](16-waypoints.md) §3, `InstanceEntities.PlayerQuestFlagsUpdate`); the client unblocks the blockers and updates the tracker by itself (§2, §3). Decompile pointers: Positioned's `.ot` keys `unblock_quest_flag` / `block_quest_flag` (`0x141e2dbb0`), flag name lookup (`0x141de7b20`, `Data/QuestFlags.dat`), interaction `required_quest_flag` / `disable_quest_flag` (`0x141e1cab0`, `0x141e5c790`) |
| Glyph Wall | coming within 60 cells, or using it, sets `SeenGlyphWall`. Used (0x24, walk-up size 7) while the client lacks `PlacedGlyphs`, with the three glyphs in the main inventory or on the cursor: one transaction looks them up, removes them and sets `PlacedGlyphs` (clearing `HaveGlyph1–3`); transactions run one at a time, so a second quick use finds nothing. 0xbf removes them in the client |
| The transition | **0x2ba to Transitionable** (component index 3 for both; data u8 target state, §6): the wall 1 → 2 runs `on_transition` (`DisableTargetable` and the animation: the label went away live), the waterfall 1 → 2 likewise. Created later with state 2 they run `create_transitioned`. Whether the waterfall's animation shows was not confirmed in screenshots |
| The invisible wall on the server | the 19 blockers (from the wall's record through `to_cave.arm`) are blocking objects of size 3 on the server's walk estimate until `PlacedGlyphs`: live, the server and the client both stopped at (862, 351), 0 cells apart (without them the server walked on and was 20 cells off) |
| The exit | `Quests.BreakingSomeEggs.FlagToEnter(1_1_3 → 1_1_4_1)` = `PlacedGlyphs`: the Gateway refuses the cave entrance without the flag and creates it when the flag is set (as a1q1's gate, §6). Every quest check reads the flag as the client has it (`Quests.ClientHas`): the character's own flags plus the World panel's access flags, e.g. `PlacedGlyphs` from an activated Submerged Passage waypoint ([16](16-waypoints.md) §4). Exits, room objects and the passage change only when that changes (`Quests.EffectiveChanges`), so a flag the client already had creates nothing twice, and clearing it (dev) removes nothing the client still opens. Not checked live for a waypoint-only character |
| The cave entrance's Transitionable | live 2026-10-18: `WaterCaveAreaTransition` inherits AreaTransition and adds Transitionable, but the client reads only the parent's components (a Transitionable byte became the task count: Buffer Underflow) and the component keeps state 0, whose post-create event crashes (`0x141ded7b9`). The Gateway creates it as a plain AreaTransition ([08](08-area-transitions.md) §7). How the real server sets that state is open |
| Tarkleigh | his NPCTalk row 40 "Glyph Reward" (needs `PlacedGlyphs`, not `QuestRewardTaken`) opens QuestRewardOffers 3: six level 1 skill gems per class, claimed like Hillock's (live: Frost Bomb, then `a1q4-QuestRewardTaken`). Row 32 "Rhoas" (Tarkleigh's clue; its QuestFlag5 is `TarkleighClue`) is listed by the client; that the generic dialogue finish sets the flag was not checked live |

## 5. The quest engine

**Quest flags are the truth.** A character's quest progress is the set of its quest flags, `QuestFlags.dat` Ids (e.g.
`a1q1-KilledHillock`), saved by name in its save (`data/saves.db`, `QuestFlags`; [saves](../saves.md)): names are
readable and survive game patches, where row numbers do not. The client keeps its own copy as QuestFlags **rows** in the
Player component ([16](16-waypoints.md) §3): the Gateway sends them with the player object and again (0x2ba, flag 0x02,
`PlayerQuestFlagsUpdate`) whenever one changes, together with the World panel's visit and access flags
(`PlayerQuestFlags`; WorldAreas `StartQuestFlags`, the `Visited-` flags, come from there). The client acts on them by
itself: an area is reachable only when its WorldAreas `EndQuestFlags` are set (`0x140797750`), blockers with
`unblock_quest_flag` read them (§2), and the quest log and tracker follow `QuestStates` (§1). `/quest_flag [<id> [off]]`
lists, sets or clears them (dev). Session flags (`SessionQuestFlags.dat`, e.g. `a1q6-FairgravesAggro`) are kept in
memory only: never saved, gone on the next area.

**One table per quest**, one file per act (`Modules/Core/World/QuestObjects/Acts/Act<n>.cs`, a class deriving
`Quests.Act`): a `Quests.Quest` with its rules. The QuestObjects module registers every act of its assembly while it is
loaded (`Quests.Register`); the Gateway and the Game module ask the tables (`Quests.OnMonsterKilled`, `FlagToEnter` …,
Gateway `Quest/Quests.cs`) and the engine's hooks (Game `World/Quest/`) do the rest. Where everything lives and the
porting checklist: [campaign quests](../game/campaign-quests.md).

| Rule | Reacts to / does | Example |
|---|---|---|
| `On.Killed(area, variety, flags…)` | a monster's death | Brutus → `a1q2-KilledBrutus` |
| `On.Seen(area, variety, flags…)` | the player within 60 cells (`SightRange`) of a living one | Merveil → `a1q3-SeenMerveil` |
| `On.Near(area, objectType, flags…)` | the player within 60 cells of a room object (from its room, below) | `PietyBlockage` → `a1q9-SeenBlockage`, `RoadBlocked` |
| `On.NearExit(area, to, flags…)` | the player within 60 cells of an exit, shown or not | (none in Act 1) |
| `On.Entered(area, flags…)` | entering the area | (Act 1's are the `Visited-` flags) |
| `On.Used(area, objectType, flags…)` | a click (0x24) on a quest object the engine created | the Ancient Seal → `a2q8-UnleashedDarkness` |
| `On.UsedWith(area, objectType, [needs], flags…)` | the same, only with the needs; the carried quest items whose remove_flag is among the flags are taken in the same save (`QuestItems.Consume`) | the tree with the spike and the gem → `a2q9-PoisonedTree` |
| `On.Cleared(area, after, flags…)` | the instance's last hostile monster dying, once `after` is set | The Fetid Pool → `a1q8-Cleared` |
| `On.Exit(from, to, flag)` | the exit needs the flag: refused (0x24) and only created once the client has it | 1_1_7_2 → 1_1_8: `a1q2-KilledBrutus` |
| `On.LevelUp(area, variety, level)` | the kill tops experience up to the level | Hillock → 2 |
| `On.Threshold(area, variety, lifePct, flags…)` | the monster never dies: the hit that takes it to `lifePct` % or below (a lethal hit and `/kill` too) leaves it at ≥ 1 life, sets the flags and it leaves the fight (removed: no death, drops or experience) | Kitava on the Cathedral Rooftop → `a5q6-CompletedBoss` |
| `On.Chest(area, chestType, item, opensWith)` | a quest chest where its room or tile puts it; opened once per instance, drops the item while needed; with `opensWith` its Transitionable is 1 (untargetable) until the flag, then 2, and its last state once opened | `AllFlameSlaveGirl` → Allflame; the Strange Device after Helena's rescue |
| `On.Drops(area, variety, item)` | the monster drops the quest item where it dies, while needed | Hailrake → Medicine Chest |
| `On.Npc(area, npcType, goneWith, transitionedBy, shownBy, freedBy, atObject)` | a room NPC where its room puts it (or where `atObject` stands), until `goneWith`, from `shownBy`; Transitionable 2 with `transitionedBy` or once `freedBy` (a variety) dies; in a town the stage of a town NPC its room lists; created, removed and transitioned live as the flags change (Game `World/Quest/QuestNpcs.cs`) | Fairgraves on his ship; Helena climbing up after Fidelitas; Clarissa freed by her guards' captain; Dialla at Dominus' DeathPortalLocator |
| `On.Object(area, objectType, transitionedBy)` | a room or tile quest object the engine creates (`QuestObjectTypes`, the generic `QuestObject` blob): Transitionable 1, its last state once the flag is set (0x2ba when it comes); a TriggerableBlockage (no update reader) is created again unblocked | the Thaumetic Seal, PoisonTree, the Ancient Seal, the Dark Altar, the sewer gratings, the BioWall, the Epic Door's lock |
| `On.Spawns(area, objectType, variety)` | the object's use brings the monster where it stands; the area's population leaves it out | the Dark Altar → the Vaal Oversoul |
| `On.HandIn(npc, option, item)` | a dialogue row of the NPC with the option takes every carried copy and sets its remove_flag | Hargan's busts (`a3q11-DeliveredBust1`–`3` have no NPCTalk setter) |
| `On.Exists(objectType, from, until)` | the type exists only from / until a flag in every area: its `.ot` BaseEvents `serialisation_start_flag` / `serialisation_stop_flag`. Area transitions (by tile type), NPCs | Act 5 Lilly after `a5q6-CompletedBoss`; the Control Blocks' two exits |
| `On.Appears(area, variety, npcType, goneWith)` | when the monster dies the NPC appears where it fell (no room places it); with `goneWith` also beside the boss on a later visit while the kill flags are set | the god Sins, `SinInnocence` |
| `On.TurnsInto(area, npcType, variety)` | a dialogue row whose script names the variety (`IfVarietyContains( "…", { Transition(); } )`) turns the NPC into it: the NPC's Transitionable steps to 2 (it fades), the monster appears in its place; the area's population leaves that boss out | Fairgraves' "Thank You" |
| `On.SecondForm(area, variety, next)` | the next form appears where the first died; the area leaves it out | Merveil, the Siren → the Twisted |
| `On.Blocker(area, objectType, size, flag)` | objects the client spawns from the room and stops at until the flag: the server's walk estimate stops there too | `MerveilExitBlocker`, `SpikeBlockageBlocker` |
| `On.Wall(area, objectType, flag)` | a room object the server creates as a blocking wall (BlockerObject, QuestBarriers) until the flag | the Strand's force wall |

**Room objects.** Rooms (`.arm`) list server objects next to their doodads: a type other than
`Metadata/MiscellaneousObjects/Doodad`, e.g. `Metadata/NPC/Act1/Fairgraves` (the client spawns only plain doodads, and
objects whose `.ot` says `no_serialise`, like the blockers). `scripts/re/atlas room-points` keeps them in
`data/room-points.json` with the rooms that have monster or start points and, since version 5, the rooms that hold a
quest object (an NPC, a `Metadata/QuestObjects/` object, a quest chest); such a room takes its actual placement
(captured or from the Gateway's own generation, [14](14-monster-packs.md) §3), so `AreaPopulation.LoadObjects` gives
each object once, where the client has it.

**Generic, from the client's data** (no rule needed):
- **Quest items** (`QuestItems`): the `.it` Quest component (`data/item-types.json` "quest",
  `ItemCatalog.BaseType.Quest`; `minus` in a name stands for `-` unless the literal name is a flag): `grant_flag` is
  held while one is carried (synced after a pickup or an item move, on entering an area and about once a second),
  `extra_flag` set when first carried, `remove_flag` takes carried copies away (on entering an area or after an item
  move), removes a clicked copy from the ground and stops drops, and `use_flag` is set by using the item (the quest
  books: C→S 0x58 uses it up). A quest item drops (`On.Drops`, `On.Chest`, a1q4's nests) unless its remove_flag is set,
  one is carried or one lies on the instance's ground; the account stash does not count.
- **Hand-ins**: a dialogue row whose `QuestFlag4` is a quest item's remove_flag takes one carried copy with the flag in
  one save (Nessa's "The Medicine Chest", Fairgraves' "Thank You"); without one the option list comes back. A row whose
  script sets remove_flags takes those items too (Eramir's "Bandit Amulets", NPCTalk 95:
  `SetQuestFlag("a2q7-QuestRewardTaken", "a2b-DeliveredAmulets")`; a script call may name several flags). Every
  hand-in takes all its items in one click and save, as online: a row whose flag finishes several carried item types
  takes them all (Siosa's four pages share `a3q12-DeliveredPages`), and `On.HandIn(npc, option, item, row)` names one
  row of a shared text (Fairgraves' row 496 takes the plum and the decanter, where the data's rows 497/498 took one
  each).
- **Quest objects** (`QuestObjectTypes`, Core `QuestObject`): a type's deserialized components come from
  `data/object-types.json` (since manifest v6 it has every room server object and the quest tile and room objects,
  `ot_components.py QUEST_TILE_TYPES`, `QUEST_ROOM_TYPES`); Chests.dat rows without an `.ot` (the Golden Hand's altar,
  Victario's stashes, Siosa's book stands) take their InheritsFrom's (QuestChest) with the row's model. Placements come
  from the Gateway's own generation (`ServerLevels.Level.TileQuestObjects`: a tile record's height and virtual model),
  the room lines, or the generation's placed rooms (`ServerLevels.Level.RoomObjects`; `QuestObjectTypes.AreaPlacements`).
  The
  blob writes Positioned, Animated, Transitionable, Chest, Stats, ObjectMagicProperties, TriggerableBlockage, NPC,
  InteractionAction and MinimapIcon as the traced objects do; it reproduces the Rhoa Nest's and the Glyph Wall's bytes.
- **Rewards nothing can build**: an offer none of whose items the server can build (magic or rare jewellery) sets its
  taken flag without an item, so its quest completes (a3q4, a3q13).
- **Dialogue** ([17](17-npcs.md)): `QuestFlag4` set on choosing, `QuestFlag5` when the talk ends, and the scripts'
  `SetQuestFlag`, `ClearQuestFlag`, `SetSessionQuestFlag` (Fairgraves' "Marooned": `a1q6-ReceivedQuest`, clears
  `a1q6-DeliveredAllFlame`). `QuestFlag3` marks a row heard: of two rows with the same text (Tarkleigh's two "Dweller of
  the Deep") the unheard one is the quest's next.
- **Static rewards** (`QuestStaticRewards`, from `QuestStaticRewards.dat`): column 1 passive points, column 5 ascendancy
  points, column 7 refund points, StatsKeys/StatValues permanent stats. Passive points are derived from the flags every
  time and sent in the 0x33 tree's `extra_skill_points` ([15](15-progression.md) §2): the client's tree check knows only
  `points_from_level`, `skill_points_from_passives` and `extra_skill_points`, so it does not count quest points itself.
  Refund points are spent, so a flag credits its refund points once, in the save write that sets it
  (`QuestRefundsCredited`; a save that has such a flag uncredited gets it at login). Stats are stat sources like a
  passive's (Game `WithQuestStats`: Act 2's help rewards a2q1–a2q3, 8% movement speed, +15% resistances, +40 life;
  the pools and the panel follow a change); Kitava's penalty stays `ResistancePenalty`'s.
- **The quest log** (`QuestStates`): the first `QuestStates` row by Order whose flags match, Order 0 "Quest Complete";
  `CharacterStartQuestState` presets (`Act1Complete` …) with their quests and flags.

**The oracle** (`QuestSimulator`, `QuestEngineTests`): an act's events (enter, see, kill, near, open a chest, pick
up, talk, reward, use an item) played through the same tables, NPCTalk rows, item flags, static rewards, the Gateway's
exits and the client's WorldAreas gates; a step the game would refuse throws. After Act 1 the flags contain
`Act1Complete`'s and a1q1–a1q8 are complete; after Act 2 (all three bandits killed) `Act2CompleteEramir`'s, after
Act 3 `Act3Complete`'s, after Act 4 `Act4Complete`'s, each act's quests complete (Acts 5 and 6 have no preset of their
own: every quest of theirs reaches its "complete" row); after Act 6 Acts 7 and 8 reach `E3EndOfAct8`'s act 7–8
flags, Acts 9 and 10 `EpilogueAtlasDemo`'s act 7–10 flags, every
quest of acts 7–10 and the Epilogue completes with the campaign's 24 quest passive and 20 refund points, and Kitava's
Act 10 penalty counts only once his end earns `EndGameAvailable` (`QuestSimulator.Unearned`, as
`ClientState.UnearnedQuestFlags`); later acts add their sequence and preset.
Played live on 2026-10-30 with the real client (OfflineWitch in a copied save, seed 1 instances, the bridge, `/tp`, and a
temporary kill command): a1q8, a1q7, a1q2, a1q9's part, a1q6 and a1q3 into Act 2, and
a1q5 and a1q4 again on the engine; afterwards the save had every `Act1Complete` flag, 2 quest passive points and 2
credited refund points, also after a relog. That run was on the engine before the Gateway moved the game into modules;
the port to the modules has not been played with the client yet.

### Adding a quest

The checklist (data to read, the act file, the oracle, the docs) is [campaign quests](../game/campaign-quests.md)
("Adding a quest", "Adding an act"). In short: read the quest's rows, add its `Quest` to its act's table
(`Modules/Core/World/QuestObjects/Acts/Act<n>.cs`) with one rule per event the server must see and per world change,
code only what the data cannot say (a1q4's Glyph Wall: `GlyphQuest`, with an oracle handler in
`QuestSimulator.ObjectHandlers`), and extend the act's oracle sequence in `QuestEngineTests`.

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
Played through live on 2026-10-25 (`MercyMission.cs`; since the quest engine, the generic quest items and `Act1.cs`).

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
His death drops the chest where he fell (`On.Drops`), unless Nessa has one already, this character carries one or one
lies on this instance's ground; a chest lost any other way drops again from the next Hailrake.
The stash is not counted: it is the account's, so another character's stashed chest must not block this one (whose
tracker would still say "Find the Medicine Chest"); a character that stashes its own chest can get a second one.
Hailrake is placed from the area's doodads and rooms: the captures (`1_1_2a_seed<seed>.doodads.json` and `.rooms.json`,
from `live.ps1 -Walkmaps`) when there are some, else the Gateway's own generation of the seed (`ServerLevels`).

**The item.** BaseItemTypes 1534 `Metadata/Items/QuestItems/MedicineSet1`, "Medicine Chest", 2×3, item class 31.
Its `.it` Quest component has `grant_flag = "a1q5minusHaveMedicineChest"` and
`remove_flag = "a1q5minusNessaCongratulated"`.
The engine's quest item rules (§5) keep 49 equal to "a chest is carried" (inventories and cursor): on entering an area,
after a pickup, after a read with an item move (drop, place, pick-up, the stash, a sale, a chat command), and about once
a second for the flag alone.
Once 42 is set, a carried chest is taken away (on entering an area or after an item move: the inventory changes stay on
the message path, in revision order), and a click on a chest on the ground removes it from the ground.
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

## 8. a1q8 "A Dirty Job" and a1q7 "The Dweller of the Deep"

| Flag | Set by |
|---|---|
| `a1q8-QuestReceived` | Tarkleigh's "The Fetid Pool" (NPCTalk 44–46: after the Bone Rhoas, The Fetid Pool or Nessa's first reward) |
| `a1q8-SeenUndeadRhoa`, `a1q8-SeenNecromancer` | seeing a Bone Rhoa (MonsterVarieties 469 `Rhoas/RhoaSkeleton`, The Fetid Pool's packs) and Kadavrus the Defiler (430 `NecromancerBossFetidPool`, `mapboss` of `Area3a/Rooms/miniboss.arm`) |
| `a1q8-KilledNecromancer` | Kadavrus' death |
| `a1q8-Cleared` | the last hostile monster of The Fetid Pool dying, after Kadavrus (QuestStates: "Wipe out the remaining monsters"; poedb: all monsters) |
| `a1q8-Congratulated`, `-HaveRewardBook`, `-UsedRewardBook` | Tarkleigh's "The Necromancer", the Book of Regrets (offer 5) carried, then used: two refund points |
| `a1q7-ReceivedQuest` | Tarkleigh's first "Dweller of the Deep" (after `PlacedGlyphs`) |
| `a1q7-SeenDweller`, `-KilledDweller` | The Dweller of the Deep (541 `ShieldCrabBossSlowAura`, `mapboss` of The Flooded Depths' `boss_water_wet_unique_10a.arm`) |
| `a1q7-Congratulated`, `-HaveRewardBook`, `-UsedRewardBook` | Tarkleigh's second "Dweller of the Deep", the Book of Skill (offer 6), used: one passive point |

## 9. a1q2 "The Caged Brute", a1q3 "The Siren's Cadence", a1q9 "The Way Forward" (Act 1)

**The Upper Prison** (1_1_7_2) is a dummy graph of two sub-graphs, "Prison" and "Warden" (`Area7Level2/filegroups.fgp`),
joined by telepads. Brutus (150 `Brute/BossBrute`) is WorldAreas' second boss and stands at `mapboss2` of the
`boss_cells_*` rooms before his arena (`boss_exit.arm`); the Necromancer is `mapboss1` (`special_necroboss1.arm`).
Before the room-points generator followed sub-graphs (14-monster-packs.md §3) the area had no boss room and Brutus never
appeared. On the way, Piety reads in `Piety.arm` (`NPC/Act1/PietyPrison`, not targetable): coming near her sets
`SeenPietyPrison` (Act1Complete has it). His death sets `a1q2-KilledBrutus`, which opens the exit to Prisoner's Gate
(its `EndQuestFlags`, and `On.Exit`). The arena's doors (`BruteArenaEntranceDoor`, `BossArenaExitDoor`:
TriggerableBlockage, `start_blocked`, opened by script) are not created, so nothing blocks the way. Tarkleigh's "Brutus"
gives `a1q2-ReceivedQuest` (after The Lower Prison is visited); Nessa's "Brutus" and "Prison Reward" (offer 8) help
before; Tarkleigh's "Brutus Reward" is offer 7.

**The Cavern of Anger** (1_1_11_2) embeds Merveil's arena (`_cave_river_boss1.dgr`, room `river_boss_1a.arm`, tag
`act1endboss`, `mapboss2`): Merveil, the Siren (162 `seawitchillusion/BossMerveil`, `set_monster_has_second_form`, no
drops) and on her death Merveil, the Twisted (164 `Seawitch/BossMerveil2`, in WorldAreas' Monsters, not placed by the
area: `On.SecondForm`). The second form's death sets `a1q3-KilledSeaWitch`: the room's six
`QuestObjects/MerveilExitBlocker` (`blocking`, `object_size 5`, `unblock_quest_flag a1q3-KilledSeaWitch`; the client
spawns them) stop blocking, and the exit into The Southern Forest is created (`On.Exit`; 1_2_1's `EndQuestFlags` too).
Ambrosia (518 `SeaWitchBossFirestorm`, `mapboss1`) is the daughter, not Merveil. Nessa's "Merveil", "Merveil's Lair" and
"Merveil Reward" (offer 10) are dialogue; the quest is complete in Act 2 (`Visited-2_1` with `a1q3-QuestRewardTaken`).

**Prisoner's Gate** (1_1_8): the road to The Western Forest is blocked (`Area8/Rooms/blocked_passage.arm`: `NPC/Act1/
PietyBlockage`, the `SpikeBlockage` animations and seven `Spikes/SpikeBlockageBlocker`, `unblock_quest_flag
a1q9-RoadOpened`; the exit `Spikes/BlockedPassageTransition` is hidden until then). Coming near Piety's blockage sets
`a1q9-SeenBlockage` and `a1q9-RoadBlocked` (both in Act1Complete); `On.Exit(1_1_8 → 1_2_9)` needs `a1q9-RoadOpened`, set
in Act 2 with the Thaumetic Emblem (`SpikeSealKey`: grant `a2q11-HaveKey`, extra `a1q9-RoadBlocked`, remove
`a1q9-RoadOpened`). Tarkleigh's "Reaching the Forest" gives `a1q9-QuestReceived`; Bestel's reward (offer 11) comes
after.

## 10. a1q6 "The Marooned Mariner"

Captain Fairgraves (`NPC/Act1/Fairgraves`, in `Area9/Rooms/intact_ship.arm` with the room's `mapboss`) stands on his
ship in The Ship Graveyard (1_1_9). His boss (550 `Skeletons/SkeletonBossFairgraves`, base `SkeletonBossEmerge`: four
Transitionable states, invisible in state 1) is not placed with the area (`On.TurnsInto`); unique monsters are created
in their last state, so he appears visible and targetable.
- "Introduction" and "Marooned" (script: `SetQuestFlag("a1q6-ReceivedQuest")`,
  `ClearQuestFlag("a1q6-DeliveredAllFlame")`).
- The Allflame (BaseItemTypes 1535 `AllFlameLantern1`: grant `a1q6-HaveAllFlame`, remove `a1q6-DeliveredAllFlame`) is
  the wounded slave girl's (Chests 186 `QuestChests/Fairgraves/AllFlameSlaveGirl`, `BaseItemTypesKey` 1535) in The Ship
  Graveyard Cave's treasure room (`Cave/Rooms_new/Unique/treasure1a.arm`, with Stranglecharm at `mapboss`): `On.Chest`.
- "Thank You" (NPCTalk 170, needs `HaveAllFlame`; 171 again after `DeliveredAllFlame` while he lives and the session
  flag is not set) hands the Allflame in (its remove_flag) and its script transitions him (`ForEachInRange( 100, {
  IfVarietyContains( "…SkeletonBossFairgraves", { Transition(); } ) } )`,
  `SetSessionQuestFlag("a1q6-FairgravesAggro")`): the NPC fades (Transitionable 2) and the boss appears in his place.
- His death sets `a1q6-KilledFairgraves`; the NPC is not created again (his `.ot` `unblock_quest_flag`, `On.Npc`).
- Bestel's "Captain Fairgraves" and "Fairgraves Reward" (offer 9): a Book of Skill, one passive point once used.

## 11. Act 2 (`Acts/Act2.cs`)

The quests step by step, their flags, objects and rewards are in [act 2](../game/campaign/act2.md); the table is what
the data does not say:

- **a2q11** (a1q9's road): Arteri (`mapboss2` of The Western Forest's passageblock.arm) seen → `a2q11-SeenCaptain`,
  killed → `a2q11-SeenKey`, drops the Thaumetic Emblem; the Thaumetic Seal (`Spikes/SpikeBlockageButton`, a tile record)
  used with it → `a1q9-RoadOpened`: the seal and the spike walls transition, the client's SpikeBlockageBlockers lift,
  the exits between The Western Forest and Prisoner's Gate appear. Bestel's book (offer 11) gives a passive point.
- **a2q10**: the Great White Beast seen/killed in The Den.
- **a2q6**: HelenaSins below the ledge (Transitionable 1, untargetable) climbs up when Fidelitas dies
  (`a2q6-KilledDemon`); her "Rescued" → `a2q6-RescuedHelena` lets the Strange Device (PoisonCart, Transitionable 1 → 2)
  be opened: the Baleful Gem. HelenaSins and PietySins come near → `a2q6-SeenHelena`, `a2q6-SeenPiety`. Greust's start
  row needs `a2b-StartNewQuests`, which nothing sets this early (the spike's `extra_flag2` would, later): the quest runs
  without it.
- **a2q4**: the Weaver drops Maligaro's Spike; Silk's rows look at it without taking it.
- **a2q5**: the crypt's altar (ChurchAltar: a Chests.dat row without an `.ot`, at a tile record) drops the Golden Hand;
  Yeena's second "The Fellshrine Hand" (NPCTalk 134; 133 gave the quest and stays listed) takes it.
- **a2q9**: the tree (PoisonTree, tile record) used with the spike and the gem → `a2q9-PoisonedTree` (both taken), its
  IncaTreeTransitionBlockers lift and the exit into The Vaal Ruins (IncaTreeTransition) appears.
- **a2q7**: each bandit lord's death sets his kill flag and drops his amulet; Eramir's "Bandit Amulets" (NPCTalk 95,
  `SetQuestFlag("a2q7-QuestRewardTaken", "a2b-DeliveredAmulets")`) takes the three: one passive point; "Take the Apex"
  (offer 17) gives The Apex. The bandit vote (help a lord) is not implemented ([status](../../status/quests.md)).
- **a2q8**: the Ancient Seal (IncaDarknessRelease, tile record; its model is the record's `bossrelease.ao`) →
  `a2q8-UnleashedDarkness`; the Apex door in The Caverns transitions with `a2b-SeenApexReward`; the Dark Altar
  (IncaBossSpawner) takes The Apex (`a2q8-PlacedApex`) and brings the Vaal Oversoul (MV 158, no WorldAreas boss), whose
  death sets `a2q8-KilledShadowMonster` and `FinishedVaalOversoulQuest` (no setter in the data; Act 3's areas need it)
  and shows the pyramid's exit to The City of Sarn.

## 12. Act 3 (`Acts/Act3.cs`)

Details in [act 3](../game/campaign/act3.md):

- **a3q1/a3q2**: ClarissaSlums kneels (Transitionable 1) until the Guard Captain dies (`freedBy`), her "Tolman" →
  `a3q1-RescuedGirl`; Tolman's chair (a chest with an NPC component, 3 states) drops the bracelet, ClarissaTown takes
  it; her Sewer Keys (offer 19) open the sewer gratings of The Slums and The Marketplace (`a3q11-UsedSewerKeys`, the
  gate of seven areas).
- **a3q11**: Victario's three stashes (Chests.dat rows without an `.ot`); no NPCTalk row sets
  `a3q11-DeliveredBust1`–`3`, so Hargan's "Victario's Holdfasts" hands each carried bust in (`On.HandIn`).
- **a3q3/a3q4/a3q5**: Lady Dialla in The Solaris Temple Level 2 (room NPC); the Ribbon Spool chest; the Supply
  Container's sulphite, Dialla's Infernal Talc (offer 22) on the BioWall → `a3q5-UsedTalc` (its blockers lift, the west
  opens). Offer 21 (amulets) and 25 (rings) count as taken when nothing in them can be built.
- **a3q8**: the bridge barrier (a tile object) seen, Gravicius seen and killed. **a3q12**: Siosa and the four book
  stands (no `.ot`); the loose candle (HiddenDoorTrigger) opens the hidden door for the instance (`Quests.InstanceFlag`,
  kept on the instance, never saved) and the way into The Archives, a layout-disabled exit the rule gates, appears.
  **a3q13**: Fairgraves in The Docks only after his Act 1 death (`shownBy a1q6-KilledFairgraves`), the decanter and the
  plum tree (a tile chest).
- **a3q9**: Piety (`mapboss1`) dies → `a3q9-KilledPiety`, `a3q10-QuestReceived` and the Tower Key.
- **a3q10**: the Epic Door's lock used with the key → `a3q9-UsedTowerKey`; Dominus (`mapboss2` of city_tower.arm) dies
  into Dominus, Ascendant (`On.SecondForm`), whose death sets `a3q10-KilledDominus`; Lady Dialla (DiallaTower, no room
  line) appears at the DeathPortalLocator (`atObject`), and the tower top's exit to The Aqueduct (city_tower.arm's
  `entrance2disabled`: created because the quest gates it, [08](08-area-transitions.md) §8) appears.

## 13. Act 4 (`Acts/Act4.cs`)

Details in [act 4](../game/campaign/act4.md):

- **a4q2**: Voll drops Deshret's Banner; the banner on Deshret's Seal (MineEntranceSeal, `UsedWith`) → `a4q2-UsedBanner`:
  its MineEntranceSealBlockers lift and the Mines open. **a4q6**: Deshret's spirit in The Mines Level 2 come near
  (`DeshretSpiritSeen`, no setter in the data) and used → `a4q6-DeshretFreed`; Tasuni's book.
- **a4q3/a4q4**: Kaom and Daresso drop their Eyes; DiallaRapture's rows 530/531 take them (their remove_flags).
- **a4q1/a4q5**: Lady Dialla stands on The Aqueduct, at the Rapture Device (coming near: `a4q1-SeenRaptureDialla`) and
  in Highgate in two stages (`DiallaTown` until `a4q1-RaptureActivated`, `DiallaTownWithered` after: both town room
  lines, `NPC/TownNpcs.cs`). NPCTalk 534 transitions the Rapture Device (an engine quest object, `ForEachOfType`) and
  535 opens the wound into the Belly. Piety, the Abomination dies into PietyBelly where she fell (`On.Appears`), whose
  621 opens The Harvest; PietyHarvest's rows 624–627 share one text: one click hands in every organ carried
  (`On.HandIn`), then the Black Core. Malachai's second stage appears where the first falls (`On.SecondForm`) and its
  death sets `a4q1-QuestComplete`; `a4q1-KillGuardians` is not set (no data sets it, the "complete" rows need it
  missing). The withered Dialla's 521 (`DiallaOnMalachaiDead`) opens The Ascent.
- **a5q1a**: the Resonator on The Ascent's summit (a room `room-points.json` does not keep: `AreaObjects.RoomObjects`);
  the lever come near → `a5q1-SeenResonator`, pulled → `a5q1-UsedResonator`, the Oriath Portal to The Slave Pens.

## 14. Act 5 (`Acts/Act5.cs`)

Details in [act 5](../game/campaign/act5.md):

- **a5q1b/a5q2/a5q3**: Krow guards the tower (`a5q1-SeenTown` from seeing its exit, `On.NearExit`); Casticus drops the
  Eyes of Zeal and opens Oriath Square; the Experimental Supplies hold the Miasmeter.
- **a5q4**: the Templar Courts door used with the Eyes → `a5q2-UsedKey` and `a5q4-QuestReceived` (no setter in the
  data); the court gate exists from `a5q2-UsedKey` (`On.Exists`, its `serialisation_start_flag`). Avarius forms into
  Innocence, whose death sets `a5q4-KilledBoss`: Oriath burns, the Control Blocks' and the Chamber's exits swap by
  their serialisation flags and each leads to its own version of the area (`On.Exists(…, to:)`), and SinInnocence
  appears where Innocence fell. **a5q5**: Bannon after Avarius, then in town; Utula leaves town and fights in The
  Ruined Square. **a5q7**: the three Relic Cases; Lani takes the Torments in one click.
- **a5q6**: the Ossuary tomb holds the Sign of Purity (`a5q6-SeenItem` lifts the Ruined Square's flame wall and opens
  the rooftop); the Sign in its socket (`ArenaSocket`) brings Kitava (`On.Spawns`). Kitava never dies: a hit (or
  `/kill`) that takes him to 50 % sets `a5q6-CompletedBoss` (`On.Threshold`) and he leaves; QuestStaticRewards 38's
  −30 % to all resistances is the Game module's `ResistancePenalty` (`a5q6-CompletedBoss`, `SinOnKitavaDefeat`). Sin
  and Lilly (her `serialisation_start_flag`) stand on the dock, and Lilly's 893 sails to Lioneye's Watch (2_6_town).

## 15. Act 6 (`Acts/Act6.cs`)

Details in [act 6](../game/campaign/act6.md):

- **a6q1**: Nessa is seen three times (coming near sets `a6q1-SeenNessa1`–`3` and she transitions away); the Black
  Flag chest is a point line of her room in Merveil's cavern; at the Beacon the ignition lever → `a6q1-RefueledBeacon`
  (the fuel carts, a Movement object, are left out) and the statue takes the flag → `a6q1-DeliveredFlag`, which shows
  WeylamBeacon; his 1006 sails to the Reef. The Brine King's death sets `a6q1-KilledBoss`, WeylamReef2 takes his throne
  (`transitionedBy`) and SinBrineKing appears; WeylamReef2's 1010 sails to The Bridge Encampment.
- **a6q2**: Shavronne's death sets `a6q2-KilledBoss` (the data does not say whose). **a6q3**: the Dishonoured Queen
  drops the Eye of Conquest for the Mud Flats' totem door; Tukohama's death opens The Ridge and Lilly's ship leaves
  town (`serialisation_stop_flag`). **a6q4**: The Twilight Strand cleared after Lilly's task (`On.Cleared`). **a6q5**:
  Bestel's manuscript chest. **a6q6**: Ryslatha. **a6q7**: Abberath's second phase (`On.SecondForm`) sets the kill.
- The god Sins (SinTukohama, SinAbberath, SinRyslatha, SinBrineKing; SinShavronne has no god) appear where the bosses
  die (`On.Appears`, until their `Sin<God>Reward`); their row sets the session flag `CanChangePantheon` and opens the
  Pantheon panel ([pantheon](../game/campaign/pantheon.md)).

## 16. Act 7 (`Acts/Act7.cs`)

The quests step by step are in [act 7](../game/campaign/act7.md); the table is what the data does not say:

- **a7q2/a7q3**: the Container of Sins in The Crypt drops Maligaro's Map (its `extra_flag` `a7q3-SpawnSilk` shows
  SilkSins beside the Reverie Device, `shownBy`); the map used at the device (`MaligaroOrrery`, a tile record) sets
  `a7q2-DeliveredItem` and opens Maligaro's portal (`On.Portal`: `MaligaroPortalTransition` beside the device, gated by
  the flag). Black Death drops her venom (Silk's "Black Venom" takes it: the Obsidian Key, offer 48). With the key,
  coming near `MaligaroSinsBarrier` sets `a7q2-ClearedSinBlock` (`On.NearWith`: its nine blockers lift, Level 2
  opens); the key used at `MaligaroPassageCover` sets `a7q1-UsedKey` (the passage into The Den).
- **a7q1**: Ralakesh's avatar (Greust, or the spared bandit lord) killed → `a7q1-KilledBoss`: The Ashen Fields' way
  north, a disabled entrance the gate creates. Sin appears where he fell (`On.Appears`).
- **a7q7/a7q4**: the seven Fireflies' chests have no room or tile: `On.ScatteredChest` spreads them over The Dread
  Thicket's walk map; YeenaVaalCity's "Fireflies" takes all seven in one click (`a7q7-DeliveredItems`: she, the webs and
  the temple entrance transition). Arakaali's death lowers her bridge into The Sarn Ramparts.
- **a7q5, a7q6, a7q8, a7q9**: the locket and Kishara's Star in their lockboxes, the Azmeri shrine used with Greust's
  Necklace, Gruthkul seen and killed.

## 17. Act 8 (`Acts/Act8.cs`)

Details in [act 8](../game/campaign/act8.md):

- **a8q1**: Doedre killed → `a8q1-KilledBoss`, the gate of the rest of the act.
- **a8q2/a8q3**: Dusk and Dawn drop the Moon and Sun Orbs; the fight starter on The Harbour Bridge used with both
  (`a8q23-DeliveredItems`, both taken) brings Lunaris and Solaris (`On.Spawns`); both dead (`On.KilledAll`) →
  `a8q23-KilledBosses`: the bridge's exit into The Blood Aqueduct, a disabled entrance the gate creates.
- **a8q5**: Titucius' tomb (Transitionable) opens once he is dead (`opensWith`). **a8q6**: the Sealed Casket's Ankh;
  ClarissaQuay's row 1153 takes it and completes the fight. **a8q7**: the Gemling Legion's seven (`On.KilledAll`).
  **a8q4**: Yugul.

## 18. Act 9 (`Acts/Act9.cs`)

Details in [act 9](../game/campaign/act9.md):

- **a9q1**: General Adus' death opens the Theurgic Precipitate Machine (the Trarthan Powder), The Basilisk drops its
  acid; Sin's "_take_ingredients_" takes both in one click. SinQuarry's "The Beast" (`SinEnterBeast`) transitions the
  membrane and lifts its seven blockers into The Belly of the Beast. In The Rotting Core the three spirits' deaths form
  The Depraved Trinity at its own boss point (`On.Forms`); its death sets `a9q1-KilledBoss` and Lilly stands to sail
  to Oriath Docks (NPCTalk 1395's `AreaTransitionTo`).
- **a9q2**: rows that set flags no column names (`On.TalkRow`: `a9q2-QuestReceived`, `IrashaTakeFeather`,
  `TasuniTakeFeather`); Garukhan drops the Sekhema Feather. **a9q3**: coming to the mummy event chest brings its sand
  warriors (`On.SpawnsNear`); both dead end it and drop the Storm Blade. **a9q4**: Boulderback drops the calendar.
- **a9q5**: the Bottled Storm carried to the sand barrier is used there (`On.NearWith`: the barrier has no interaction);
  The Oasis opens. Shakari's second stage forms where the first falls (`On.SecondForm`).

## 19. Act 10 and the Epilogue (`Acts/Act10.cs`)

Details in [act 10](../game/campaign/act10.md):

- **a10q1**: coming near BannonRooftop brings Plaguewing from its spawner; its death rescues him (Transitionable 2).
- **a10q2**: Avarius drops his staff; Bannon's rows take it and set `a10q2-BannonTransformed`: Oriath Docks' town room
  lists BannonTown and InnocenceTown on one cell, and the two `On.Npc` rules (with their lines in `TownNpcs`) show one.
- **a10q3**: InnocenceSquare's "Kitava" (`a10q3-OpenedGate`) breaks the horns (twelve blockers) and opens The Canals.
  Kitava never dies (`On.Threshold` at 1%): his end earns `EndGameAvailable`, QuestStaticRewards 39 (−30% elemental and
  chaos resistances, Game `ResistancePenalty`), the Epilogue's gate (Karui Shores' and Oriath's EndQuestFlags) and the
  condition of Lilly's "Set Sail from Oriath" (1433) and Lani's "Kitava" / "Kitava Reward" (1415/1416). Every character
  is given that flag for the Atlas, but until an event sets it (`ClientState.UnearnedQuestFlags`) it is not the
  character's and the client gets it only in a hideout and the maps (`Quests.EndgameOnly`): the Epilogue's gates, rows
  and the second −30% come with Kitava, not before. Sin's EpiloguePortal home is a quest portal gated by the same flag.
  Burnt Oriath has Act 5's tiles: an exit Act 5's rules send to an Act 5 area (`On.Exists(…, to:)`) keeps the layout's
  destination here (`AreaTransitionCatalog.SameAct`).
- **A11q0, a10q3a11** (the Epilogue): Lilly's "Set Sail from Oriath" (1433: `A10LillySailedAway`, `AreaTransitionTo`
  Karui Shores) and Lani's Kitava reward in Karui Shores need no rules; the Epilogue has no act file.
  **a10q4, a10q5, a10q6**: the Elixir and the Teardrop in their chests, Vilenta.

## Open

- How the real server gives an AreaTransition subtype its Transitionable state (blob tasks?), so the Strand's gate and
  the Mud Flats' cave entrance play their opening.
- Why the Doorman is not drawn; the list of u8 TriggerableBlockage reads while blocked (none sent).
- QuestItems.dat covers league items only; the campaign's items are their `.it` Quest components (§5).
- Transitionable on inherited types (`WaterCaveAreaTransition`): how its state gets set.
- How the real server chooses which nests hold which glyph (RhoaChest / 2 / 3); the Gateway shuffles them by the seed.
- Whether the waterfall (`WaterTransition`, `waterdrop.ao`) shows its animation, and its height (the room line has
  37.0245 after the quaternion; it is created at the terrain height).

## Opening encounter (2026-10-25)

New characters retain the class weapon as a pending ground item and both class gems as `OpeningTutorial` rewards in
their save. Flasks are available immediately. The old implementation equipped the weapon and granted both gems in the
main inventory, and never spawned the exile, scene zombie, or chest. Existing characters have no `Tutorial` record and
keep their equipment, gems, and quest progress unchanged.

The fitted `washed_up3.arm` supplies the start point, WoundedExile and BiteZombieSpawner positions.
An untouched tutorial character starts in the native WashedUp stance, facing the fitted `default` point's angle.
The room fit transforms named-point angles together with their coordinates; seed 1's start faces approximately pi
radians, rather than the previous fixed zero (the opposite direction).
The Player WashedUp flag and Actor stance 68 must both be sent ([04](04-player-entity.md) §4.6 and §4.9).
The first ground click sends basic action `WashedUp` (ActionTypes HASH16 47065, skill id `0x37d9`), which plays getup
in place; the server echoes that action without walking its position estimate toward the click.
A first click on an object takes the server's path instead: `0x141e94fc0` answers a washed-up player's move request
with `WashedUp` at the player's own cell and a top-bit sequence, so the object is not walked to.
`Tutorial.WakeUpShown` saves the introduction once the player is sent; later logins and progressed saves start standing.
Talking without an equipped weapon plays the weapon prompt and saves `Tutorial.WeaponPromptHeard`.
With a weapon, the first conversation uses NPCTalk option 2; after the prompt it uses option 3.
`TalkToWoundedExile(8.2,22.8)` gives the short and long dialogue lengths, rather than two stages of one scene.
The extracted DBG_02 and DBG_03 audio lasts 7.951 and 22.763 seconds respectively.
The bite starts after the selected dialogue's 8.2 or 22.8 seconds, or immediately when the player closes that dialogue.
Closing the unarmed weapon prompt does not start the bite.
The scene zombie's rig gives its bite clip 3100 ms; after those 3.1 seconds it is replaced by the combat zombie.
The bite wait starts at its actual transition time, so skipped speech does not leave the original voice-length delay.
Both waits use the paused instance clock, and skipping during pause waits for resume.
Another exile click cannot restart the scene; it closes the dialogue, so it starts the bite as closing does.
The former fixed 8.2-second bite and 22.8-second handoff started the bite before the long voice finished and left it still
for approximately 11.5 seconds after its animation finished.
Saves without `WeaponPromptHeard` default to the introductory dialogue; other saved progress is preserved.
Transitionable states are 1 (waiting) and 2 (bitten); state 0 is invalid.
The scene rig's bite clip is index 1, sent with 0x1b2 (speed 0
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
The dialogue timing correction was checked with an isolated Duelist equipped before his first conversation and
a Witch who heard the weapon prompt before equipping.
The Witch's measured bite/handoff times were 8.218/11.328 seconds, including a duplicate interaction during the wait.
Native combat-zombie samples advanced through the walk and attack clips at positive playback speed.
The Duelist killed that monster and claimed Double Strike through the client.
