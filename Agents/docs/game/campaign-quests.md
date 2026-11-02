# Campaign quests: the quest engine, acts and how to add them

The campaign's quests run on one engine: a character's progress is its set of quest flags, each quest is a declarative
table of rules (events → flags, flags → world changes), and everything else comes from the client's own data (NPCTalk,
the quest items' `.it` files, QuestRewardOffers, QuestStaticRewards, QuestStates). The protocol side, the rule
reference and Act 1 quest by quest are in [12-quests](../protocol/12-quests.md) (§5 the engine, §6–§10 Act 1). This page
is where the pieces live and the checklists for adding a quest and an act.

## 1. Where it lives

| Piece | Where | What |
|---|---|---|
| Rule types, `On.*`, the queries, the registry | Gateway `src/GameServer.Gateway/Quest/Quests.cs` | `Quests.Rule` records, `Quests.Quest` (a table), `Quests.Act` (one act), `Quests.Register`, `Quests.OnMonsterKilled`, `FlagToEnter` … |
| Quest items, static rewards, the quest log | Gateway `Quest/QuestItems.cs`, `QuestStaticRewards.cs`, `QuestStates.cs`, `Item/ItemCatalog.cs` (`BaseType.Quest`) | the `.it` Quest component's grant, extra, remove and use flags; QuestStaticRewards' points and stats; QuestStates, CharacterStartQuestState presets, SessionQuestFlags |
| Quest objects and their placements | Gateway `Quest/QuestObjectTypes.cs`, Core `Protocol/Instance/QuestObject.cs` | a type's components from `data/object-types.json` (and Chests.dat rows), the generic blob, where an area places a type |
| The oracle | Gateway `Quest/QuestSimulator.cs`, `tests/GameServer.Gateway.Tests/QuestEngineTests.cs` | an act's events through the same tables and data; a step the game would refuse throws |
| Per-connection state | Gateway `Connection/ClientState.cs` (`QuestChests`, `QuestObjects`, `QuestNear`, `QuestNpcs` …), `Quest/QuestNpc.cs`, `Saves/CharacterSaves.cs` (`QuestRefundsCredited`), `Saves/InstanceStore.cs` (instance states) | kept across a module reload |
| The engine's hooks | Game module `Modules/Core/Game/World/Quest/` (`QuestEngine.cs`, `QuestNpcs.cs`, `NpcScripts.cs`), `World/Quests.cs` (flag commits, shown exits) | area entry, kills, chests, quest objects, NPCs, dialogue scripts, sights, thresholds, quest items, static rewards |
| The acts' tables | QuestObjects module `Modules/Core/World/QuestObjects/Acts/Act<n>.cs` | one class per act deriving `Quests.Act`, with the act's Id constants |
| Code-driven quests | QuestObjects module (a1q4's Glyph Wall, the opening tutorial), Gateway `Quest/GlyphQuest.cs` | what the data cannot say |

The QuestObjects module finds every `Quests.Act` of its assembly when it loads and registers them
(`Quests.Register("QuestObjects", …)`); unloading takes them away, a reload registers the new build's. With no act
registered every query is empty and the hooks do nothing. The module also hands the engine its events: entering an
area (`StartQuestArea`), a kill (`QuestKillEffects`, after the kill's flags), a click on a quest chest or object, and a
read of item moves (`SyncQuestItems`). The Game module calls the rest directly (the flag commit, the player tick's
sights and item sync, dialogue, rewards, thresholds, passive points, stat sources).

**Leagues.** A league runs the tables of the modules it lists, each registering module on its own
(`Quests.For(modules)` gives that set, a `Quests.RuleSet`; Game `QuestsOf(conn)` is the player's): the module's own
hooks run per league anyway (GameEvents), and every rule the Game module applies to a player is asked of the player's
set (kill flags and levels, sightings, exit gates, a quest's NPC stages, bosses a quest brings, thresholds, dialogue
rules), so a league without the module has none of them. A layout is shared by every league: the exits a quest adds
there (a gated disabled entrance, a quest portal, an exit's swapped version) carry the module that added them
(`AreaTransitionObject.QuestSource`) and stand only where its tables run, and the exits they replace
(`QuestReplaced`: the exit without a model a portal stands in for, the unswapped version) only where they do not, so a
league without the tables has the layout as generated (Game `TransitionShown`). What the client's data says alone
(quest items' `.it` flags, NPCTalk flags, QuestStaticRewards) applies in every league, as dialogue flags always did.
The static queries of `Quests` ask every registered table (`Quests.Everything`: the oracle, the layouts, the tools).
Every playable league lists `Core/World/QuestObjects`.

## 2. An act file

```csharp
using static Quests;
using static Act1Areas;

static class Act1Areas { public const string Strand = "1_1_1", LioneyesWatch = "1_1_town" /* … */; }
static class Act1Monsters { public const string Hillock = "Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal"; /* … */ }

sealed class Act1 : Act
{
    public override int Number => 1;

    public override IReadOnlyList<Quest> Table =>
    [
        new Quest("a1q1")
        {
            On.Seen(Strand, Act1Monsters.Hillock, "a1q1-SeenHillock"),
            On.Killed(Strand, Act1Monsters.Hillock, "a1q1-KilledHillock"),
            On.LevelUp(Strand, Act1Monsters.Hillock, 2),
            On.Exit(Strand, LioneyesWatch, "a1q1-KilledHillock"),
            On.Wall(Strand, "Metadata/Terrain/Act1/Area1/Objects/Tutorial_Blocker_3", "a1q1-KilledHillock"),
        },
        // …
    ];
}
```

- One file per act, `Acts/Act<n>.cs` (the Epilogue is `Number => 11`); the class needs a parameterless constructor.
  Nothing else lists the acts, so acts added side by side do not touch the same lines.
- The act's Ids go in its own static classes (`Act<n>Areas`, `Act<n>Monsters`, `Act<n>Items`, `Act<n>Objects`) with
  their table rows in comments; a later act uses an earlier one's with `using static Act1Areas;`.
- Quests in the game's order (Quest.dat rows); each rule's comment says where it comes from (a QuestStates row, an
  NPCTalk row, an `.ot` key).
- The rule kinds (`On.Killed`, `Seen`, `Near`, `NearWith`, `NearExit`, `Entered`, `Used`, `UsedWith`, `Cleared`,
  `KilledAll`, `Forms`, `Exit`, `LevelUp`, `Threshold`, `Chest`, `ScatteredChest`, `Drops`, `SpawnsNear`, `Npc`,
  `Object`, `Spawns`, `HandIn`, `TalkRow`, `Exists`, `Appears`, `Portal`, `TurnsInto`, `SecondForm`, `Blocker`, `Wall`)
  are the table in [12](../protocol/12-quests.md) §5 and the records' comments in `Quests.cs`. A state of the area
  instance rather than the character (never saved) is named `Quests.InstanceFlag("…")`.

## 3. Adding a quest

1. **Read the data.** `Quest` (its row and act), `QuestStates` (its log: every flag the quest uses, and which state is
   "complete"), the `NPCTalk` rows that name those flags in QuestFlag1–5 or their scripts (their flags need no rule),
   `QuestRewardOffers`/`QuestRewards`, `QuestStaticRewards`, the quest items' `.it` (`data/item-types.json` "quest"),
   `WorldAreas` (`Bosses`, `EndQuestFlags`), `SessionQuestFlags`, the rooms' objects and named points
   (`data/room-points.json`; `mapboss<n>` is the n-th WorldAreas boss) and each object's `.ot` (`unblock_quest_flag`,
   `object_size`, `blocking`, `serialisation_start_flag`).
2. **Write the table** in the act's file: one rule per event the server must see (the flags the client or NPCTalk do
   not set) and per world change (exits, objects, chests, drops, NPCs, forms, blockers). A quest object or chest the
   engine creates must be in `data/object-types.json` (a tile type missing there goes into `ot_components.py`
   `QUEST_TILE_TYPES`, a room type of a room `room-points.json` does not keep into `QUEST_ROOM_TYPES`, with the
   manifest bump in `scripts/dev/extract-data.ps1` and `GameDataFiles.cs`).
3. **Code only what the data cannot say**, in the QuestObjects module next to the act, called from the module's hooks;
   give the oracle a handler for a code-driven object (`QuestSimulator.ObjectHandlers`, as a1q4's Glyph Wall).
4. **The oracle.** Add the quest's events to its act's sequence in `QuestEngineTests` (the act's preset must hold
   afterwards and every quest reach its "complete" row); the data check (every rule names real flags, monsters, items,
   NPCs and creatable objects) runs over every registered act by itself. A PR adds at most two tests: an act extends
   the existing ones (its sequence after `Act1`, its preset beside `Act1Complete`) rather than adding a test per quest.
5. **Docs.** The quest's section in [12](../protocol/12-quests.md) (or the act's page in `campaign/act<n>.md`), its
   entry moved from [roadmap/quests.md](../../roadmap/quests.md) to [status/quests.md](../../status/quests.md) once it is
   played with the real client.

## 4. Adding an act (the porting checklist)

1. `Modules/Core/World/QuestObjects/Acts/Act<n>.cs`: `sealed class Act<n> : Quests.Act` with `Number` and `Table`, and
   the act's Id classes. No other engine file changes; if one seems needed, it is an engine change of its own.
2. Town NPC stages a quest decides (a second placement of the same town NPC, `On.Npc` in a town: Highgate's
   `DiallaTownWithered`, Oriath Docks' `InnocenceTown`) go in the town's list in `NPC/TownNpcs.cs`, in the same change as
   their `On.Npc` rules: a town NPC no rule manages always stands, so without the rules both stages would.
3. Code-driven pieces in the QuestObjects module; their oracle handlers.
4. `QuestEngineTests`: the act's sequence (`internal static void Act<n>(QuestSimulator s)`) played after the earlier
   acts, its CharacterStartQuestState preset checked as Act 1's.
5. `dotnet build GameServer.slnx` and the Core, Data and Gateway tests; the docs as above.
6. Play it with the real client.

## 5. What the engine covers (by feature)

The features the acts need, by the ids the code comments use. All are in the engine; what needs the world's own
generation is marked.

| Id | Feature | Rules / code |
|---|---|---|
| E1 | Numbered boss points (`mapboss<n>`, `WG`, `force:mapboss`, `mapbosspack`) | the population (MonsterPacks), not the engine; bosses a quest brings are left out (`Quests.QuestSpawned`) |
| E2 | Room and tile quest objects: chests with their item, objects used (with items taken), Transitionable states | `On.Chest`, `On.Object`, `On.Used`/`UsedWith`, `QuestObjectTypes.AreaPlacements` (see §6) |
| E3 | Blockers and gated exits | `On.Blocker`, `On.Wall`, `On.Exit`, `On.Portal` (`AreaTransitionCatalog.AddQuestPortals`: stands in for the layout's exit without a model to the same place), a disabled entrance a quest gates is created (`AreaTransitionCatalog.Gated`) |
| E4 | `serialisation_start_flag` / `serialisation_stop_flag` | `On.Exists` (NPCs, quest objects, area transitions by their tile type, `AreaTransitionObject.SourceType`); with `to`, two exits of one tile record that swap lead to their own version of the area and the missing twin is created, in the destination's act (`AreaTransitionCatalog.QuestExitDestinations`; the layout's version stays for leagues without the tables) |
| E5 | NPCTalk scripts: `SetQuestFlag`, `ClearQuestFlag`, `SetSessionQuestFlag`, `DirtyTransition`, `ForEachOfType`/`IfType` transitions, `DoNPCChat`, `FinishNPCChat`, `SendOpenScreen`, `AreaTransitionTo` | `NpcCatalog.Talk`, Game `NpcScripts.cs`, `On.TalkRow` for flags no column sets |
| E6 | NPCs outside towns and town NPC stages | `On.Npc` (`goneWith`, `transitionedBy`, `shownBy`, `freedBy`, `atObject`) |
| E7 | NPCs that appear where a boss died | `On.Appears` |
| E8 | Session flags | SessionQuestFlags: never saved (`CharacterSaves.ChangeQuestFlags`) |
| E9 | Books and static rewards | quest items' `use_flag`, `QuestStaticRewards` (passive, refund and ascendancy points, stats) |
| E10 | Rewards beyond level 1 | `QuestRewardCatalog.Create` with the item rolls in every mode; an offer nothing can build still sets its taken flag; quest-item offers taken by the item's flags |
| E11 | Quest items in general | `QuestItems`: grant/extra/remove/use flags, drops (`On.Drops`), hand-ins (QuestFlag4, scripts, `On.HandIn`, sibling rows) |
| E12 | More triggers | `On.Near`/`NearWith`, `On.NearExit`, `On.Cleared`, `On.KilledAll`, `On.SpawnsNear` |
| E14 | Scripted bosses, minimal | `On.SecondForm`, `On.Forms`, `On.TurnsInto`, `On.Spawns`, `On.Threshold` |

## 6. Where objects come from

`QuestObjectTypes.AreaPlacements` places a type from, in order: the Gateway's own generation of the area
(`ServerLevels.Level.TileQuestObjects`, `AreaObjects.TileObjects`: every tile object of a quest type,
`QuestObjectTypes.IsTileQuestObject`, with the tile record's height and virtual model, or the record's own `.ao` when that
model is no file); the rooms' lines (`AreaPopulation.LoadObjects`: the rooms `data/room-points.json` keeps); and the
generation's placed rooms (`ServerLevels.Level.RoomObjects`, `AreaObjects.RoomObjects(points: true)`: rooms
`room-points.json` does not keep, such as The Ascent's Resonator, and objects a room's point list names, such as The
Cavern of Anger's `BlackFlagChest`). Act 1 needs only room lines (Fairgraves, the slave girl's chest, Piety, the
blockers); Acts 2 and 3 also use tile records (the Thaumetic Seal, the poisoned tree, the Ancient Seal, the Dark Altar,
the sewer gratings, the Epic Door, the Library's candle); Acts 7–10 the Reverie Device, the Azmeri shrine, The
Quarry's membrane and Kitava's horns, and chests no room or tile places (The Dread Thicket's Fireflies:
`On.ScatteredChest` spreads them over the walk map, the same cells for the same seed). An area the Gateway does not
generate has only the room lines.
A boss that forms later (`On.Forms`) stands at
its own numbered boss point (`AreaPopulation.LaterFormPoints`, kept in `ClientState.LaterFormPoints` when the area is
populated), else where the last of the others died.

## 7. Quest passive skill points and respec points

From QuestStaticRewards (QSR): `__col1` is the passive skill points the flag grants, `__col7` the respec (refund)
points, `__col5` ascendancy points; StatsKeys/StatValues permanent stats. The client's data gives **24 passive points
with "kill all bandits" (23 otherwise) and 20 respec points** over the campaign. Each book is its own item
(`Metadata/Items/QuestItems/SkillBooks/Book-<quest>` for acts 1–3, `…/QuestItems/Act<N>/Book-<quest>` later): carrying it
sets `<quest>-HaveRewardBook` (`grant_flag`), using it sets `<quest>-UsedRewardBook` (`use_flag`) and removes it
(`remove_flag`).

| QSR | Act | Quest | Flag that grants it | Passive | Respec | Book and who gives it (offer, NPC, NPCTalk row, option) |
|---|---|---|---|---|---|---|
| 1 | 1 | a1q6 The Marooned Mariner | `a1q6-UsedRewardBook` | 1 | | `Book-a1q6`: offer 9, Bestel, NPCTalk 70 "Fairgraves Reward" |
| 0 | 1 | a1q7 The Dweller of the Deep | `a1q7-UsedRewardBook` | 1 | | `Book-a1q7`: offer 6, Tarkleigh, NPCTalk 43 "Dweller Reward" |
| 2 | 1 | a1q8 A Dirty Job | `a1q8-UsedRewardBook` | | 2 | `Book-a1q8`: offer 5, Tarkleigh, NPCTalk 48 "Necromancer Reward" |
| 3 | 1 | a1q9 The Way Forward | `a1q9-UsedRewardBook` | 1 | | `Book-a1q9`: offer 11, Bestel, NPCTalk 74 "Road Reward" (after a2q11 opens the pass) |
| 7 | 2 | a2q5 Through Sacred Ground | `a2q5-UsedRewardBook` | 1 | 2 | `Book-a2q5`: offer 12, Yeena, NPCTalk 136 "Fellshrine Reward" |
| 8 | 2 | a2q7 Deal with the Bandits | `a2q7-QuestRewardTaken` | 1 | | no book: Eramir, NPCTalk 95 "Bandit Amulets" (script sets it) |
| 9 | 3 | a3q11 Victario's Secrets | `a3q11-UsedRewardBook` | 1 | 2 | `Book-a3q11v2`: offer 20, Hargan, NPCTalk 356 "Platinum Bust Reward" |
| 10 | 3 | a3q9 Piety's Pets | `a3q9-UsedRewardBook` | 1 | | `Book-a3q9`: offer 26, Grigor, NPCTalk 371 "Piety Reward" |
| 11 | 4 | a4q6 An Indomitable Spirit | `a4q6-UsedRewardBook` | 1 | 2 | `Book-a4q6`: offer 31, Tasuni, NPCTalk 608 "Deshret Reward" |
| 12 | 5 | a5q3 In Service to Science | `a5q3-UsedRewardBook` | 1 | | `Book-a5q3`: offer 36, Vilenta, NPCTalk 850 "Miasmeter Reward" |
| 13 | 5 | a5q7 Kitava's Torments | `a5q7-UsedRewardBook` | 1 | 2 | `Book-a5q7`: offer 39, Lani, NPCTalk 829 "Torments Reward" |
| 14 | 6 | a6q3 The Father of War | `a6q3-UsedRewardBook` | 1 | | `Book-a6q3`: offer 41, Tarkleigh, NPCTalk 905 "Tukohama Reward" |
| 16 | 6 | a6q4 Fallen from Grace | `a6q4-UsedRewardBook` | | 2 | `Book-a6q4`: offer 42, Lilly Roth, NPCTalk 946 "Twilight Strand Reward" |
| 15 | 6 | a6q6 The Puppet Mistress | `a6q6-UsedRewardBook` | 1 | | `Book-a6q6`: offer 44, Tarkleigh, NPCTalk 912 "Puppet Mistress Reward" |
| 17 | 6 | a6q7 The Cloven One | `a6q7-UsedRewardBook` | 1 | | `Book-a6q7`: offer 45, Bestel, NPCTalk 930 "Abberath Reward" |
| 18 | 7 | a7q1 The Master of a Million Faces | `a7q1-UsedRewardBook` | 1 | | `Book-a7q1`: offer 46, Eramir, NPCTalk 1058 "Ralakesh Reward" |
| 20 | 7 | a7q6 Kishara's Star | `a7q6-UsedRewardBook` | 1 | 2 | `Book-a7q6`: offer 50, Weylam Roth, NPCTalk 1022 "Kishara's Star Reward" |
| 19 | 7 | a7q9 Queen of Despair | `a7q9-UsedRewardBook` | 1 | | `Book-a7q9`: offer 53, Eramir, NPCTalk 1068 "Gruthkul Reward" |
| 22 | 8 | a8q4 Reflection of Terror | `a8q4-UsedRewardBook` | 1 | | `Book-a8q4`: offer 55, Hargan, NPCTalk 1170 "Yugul Reward" |
| 21 | 8 | a8q6 Love is Dead | `a8q6-UsedRewardBook` | 1 | 2 | `Book-a8q6`: offer 57, Clarissa, NPCTalk 1146 "Tolman Reward" |
| 23 | 8 | a8q7 The Gemling Legion | `a8q7-UsedRewardBook` | 1 | | `Book-a8q7`: offer 58, Maramoa, NPCTalk 1189 "Gemling Legion Reward" |
| 25 | 9 | a9q2 The Ruler of Highgate | `a9q2-UsedRewardBook` | 1 | | `Book-a9q2`: offer 59, Tasuni NPCTalk 1304 / Irasha 1315 "Feather Reward" |
| 26 | 9 | a9q4 Fastis Fortuna | `a9q4-UsedRewardBook` | | 2 | `Book-a9q4`: offer 61, Petarus and Vanja, NPCTalk 1293 "Maraketh Calendar Reward" |
| 24 | 9 | a9q5 Queen of the Sands | `a9q5-UsedRewardBook` | 1 | | `Book-a9q5`: offer 62, Irasha, NPCTalk 1330 "Shakari Reward" |
| 27 | 10 | a10q4 No Love for Old Ghosts | `a10q4-UsedRewardBook` | | 2 | `Book-a10q4`: offer 67, Weylam Roth, NPCTalk 1444 "Elixir of Allure Reward" |
| 28 | 10 | a10q6 Vilenta's Vengeance | `a10q6-UsedRewardBook` | 1 | | `Book-a10q6`: offer 69, Lani, NPCTalk 1412 / 1501 "Vilenta Reward" |
| 29 | 10 | a10q3 An End to Hunger | `a10q3-UsedRewardBook` | 2 | | `Book-a10q3`: offer 66, Lani, NPCTalk 1416 / 1498 "Kitava Reward" |
| | | **Total** | | **24** | **20** | |

Other permanent stats: QSR 4–6 (`a2q1/a2q2/a2q3-QuestRewardTaken`: 8% movement speed, +15% to all elemental
resistances, +40 life; stat sources like a passive's, Game `WithQuestStats`), QSR 38 (`a5q6-CompletedBoss`) and 39
(`EndGameAvailable`, which every character is given for the Atlas but counts only once earned:
`ClientState.UnearnedQuestFlags`): Kitava's −30% each, mirrored by Game `ResistancePenalty`. Until earned,
`EndGameAvailable` is not among the character's flags and the client gets it only in a hideout or a map area
(`Quests.EndgameOnly`, Game `PlayerQuestFlags`), so the Epilogue's gates (Karui Shores, Lilly's voyage, Lani's Kitava
reward, Sin's portal home) stay shut until Kitava while the map device and the maps work. Passive and ascendancy
points are derived from the flags every time and sent in the 0x33 tree's `extra_skill_points`
([15](../protocol/15-progression.md) §2); refund points are spent, so a flag credits them once
(`CharacterSaves.QuestRefundsCredited`).
