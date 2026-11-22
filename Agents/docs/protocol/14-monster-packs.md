# 14 — Monster packs in areas

State of 2026-10-18, verified live in The Coast (seed 1): on entering, the Gateway fills the area with monster packs
from the game tables: normal, magic and rare packs scattered over the walk map; cannibal packs at the bonfires; the
unique boss **Fire Fury** and her guards at the cannibal camp, next to its campfire. Code: `src/GameServer.Gateway/Monster/MonsterPacks.cs`
(`PackCatalog`, `AreaPopulation`), `PopulateArea` and `SpawnMonster` in `Modules/Core/Game/Monster/Monsters.cs`.

## 1. The data

| Client table (client.db) | What it gives |
|---|---|
| `MonsterPacks` | one row per pack: `Id`, `WorldAreasKeys` and their weights (`__col9`: one weight per area, or one for all), `MinCount`/`MaxCount`, `BossMonsterSpawnChance`, `BossMonsterCount`, `BossMonster_MonsterVarietiesKeys`, `Grounds` |
| `MonsterPackEntries` | each `MonsterVarietiesKey` for a `MonsterPacksKey`. `Weight` gives a draw weight when `Flag` is set and a fixed member count when clear. |
| `WorldAreas` | `AreaLevel` (the monsters' level) and `Bosses_MonsterVarietiesKeys` (the area's unique boss) |
| `MapInhabitants` | `StatsKey` selects a list of `MonsterPacksKeys` for the ordinary population. The Vaal row lists 11 packs without relative weights. |

The 3.29.3c pack-size tooltip (`0x1424d7240`, `ClientStrings.ItemPopupPackSize`) scales `MinCount` down to the
nearest whole number and `MaxCount` up. It then adds `MonsterPacks.__col1` to both. This equals the sum of the
unflagged entry counts in all 3,168 rows. The separate boss count contributes to the lower bound at 100% chance
and to the upper bound. At default size, `Act8-ThugTorch` has two to four Cruel Arsonists, one Spiteful Smuggler
and one Twisted Firestarter. Positive variable ranges also occur on rows with only unflagged entries. The Gateway
keeps its existing variety draw for those variable slots. It places fixed members first among ordinary members, so a
local member cap or the first rare slot can favour them. The original server's variety and rarity choices are unknown.
The tooltip describes one pack. Scattered centre density is a separate local rule.

The Coast (`1_1_2`, WorldAreas row 6, level 2):

| Pack | Weight | Members | Placed |
|---|---|---|---|
| `Act1-CoastCrabs` (row 2) | 100 | 2–4 Savage / Kelp / Shore / Coconut / Spider Crabs; 80%: one large Savage Crab (row 4515) | scattered |
| `Act1-ZombieDrowned` (row 3) | 100 | 3–4 Drowned | scattered on one of five wet beach/ocean grounds in generated layouts |
| `Act1-SandspitterEmerge` (row 4) | 100 | 3–4 Gravel Eaters | scattered, buried: they rise when the player comes near ([10](10-chat-and-commands.md) §5) |
| `1_1_2-cannibalfire` (row 620) | 0 | 2–4 Cannibals (male, female, fire throwers, Fire-eater) | room points `%area%-cannibalfire` |
| `1_1_2-bosspack` (row 621) | 0 | 6–8 Cannibals | with the boss at `mapboss` |
| boss: Fire Fury (row 2845, `CannibalFemaleBossWitch`) | | Unique | room point `mapboss` |

Packs whose `Id` is `<area>-<name>` have weight 0: the layout places them, through **room points** (§3).

`MonsterPacks.Grounds` lists names from the first line of the terrain's `.gt` files. For a scattered centre or a
room point that specifies only rarity, the Gateway draws from unrestricted rows and rows naming the local ground,
with their original weights. An empty pool leaves the site free. The sample uses the nearest vertex of the final
tile grid, as the client's random doodads do. Members can cross that boundary within their usual radius.
The Twilight Strand's Drowned use a separate row with an empty list. Untainted Paradise spells the `Knoll` ground
`knoll` in two pack rows. The Pantheon soldier pack has only `-`. Its area weight and its room points both apply
locally. Their official priority is unknown.

**Not recovered:** how many packs an area gets, and how often a pack is magic or has a rare: the official server
decides those. Ours (`AreaPopulation`): one scattered pack per 9 000 walkable cells (`CellsPerPack`; The Coast: 44
packs), 12% magic packs (every member magic), 6% packs with one rare member, at least 2 rare packs per area. Pack
centres keep 60 cells from the arrival point, 25 from area transitions, 28 from other packs and need 5 walkable cells
around them; members stand within 9 cells. Population uses a stable MurmurHash2 over invariant ASCII
`population:<area HASH16, 4 hex digits>:<area seed, 8 hex digits>`.
The previous `HashCode.Combine` used a process-random salt, so a restart changed the population for the same seed.
This is a local seed derivation; the official server population RNG and placement algorithm remain unrecovered.

## 2. Spawning

For `MonsterVarieties` with main-hand class `Bow`, the model uses the same three-slot bow layout as a player
([13](13-items.md) "Bow model records", `BowSetup`): slot 0 (right hand) takes `Weapon1_ItemVisualIdentityKeys`
(the arrow visual: `SkeletonArrow1` for Brittle Archer, `DefaultArrow` for Kraityn's Scout) with the `Bow` class and
stance; slot 1 (left hand) takes `Weapon2_ItemVisualIdentityKeys` (the held bow: `Bow4` / `Bow3`) with type `0x68` and
stance `0xff`; and slot 7 (the back) takes `Back_ItemVisualIdentityKey` (`SkeletonQuiver1` / `Quiver6`) with type
`0x68` and stance `0xff`. A null off-hand class must not discard slot 1 or slot 7.

`SpawnMonster` is `/spawn_mob`'s path ([10](10-chat-and-commands.md) §5): 0x20 type at the area level, 0x2bb with the
rarity in ObjectMagicProperties, 0x1a9 wake zone, and the monster joins the AI (chase, first granted effect as its
attack). Its mods (§5) set its life, hit, speed and defences.
A magic or rare monster then gets its glow, a 0x1c1 buff with no optional fields (`MonsterRarityBuffs`):
BuffDefinitions 66 `monster_magic_effect_buff` (BuffVisuals 58, `StatusAilments/monsters/champions/blue/blue.epk`)
or 67 `monster_rare_effect_buff` (BuffVisuals 59, `champions/gold/gold.epk` and `gold_large.epk`). Both are
invisible and cancel on death. No Mods.dat row grants them, and the rarity in ObjectMagicProperties alone drew no
glow. The ObjectMagicProperties `.ot` keys `enable_rarity` / `disable_rarity` (parser 0x1421e4390) set a
per-rarity mask at type +0xc9 (bits normal 1, magic 2, rare 4, unique 8); what reads it was not traced. Monsters are created after the player (0x7d →
`SendPlayer` → skill bar → `PopulateArea`); towns and areas without a walk map get none.

GGG's server has every monster of the area from the entry and sends each client only the ones near its player (retail
3.29.3b and 3.30.0b captures: a monster first sent a minute in has an id below the player's, one that left and came
back has the id it had, and "monsters remain" arrives before any create; creates lie in a box of about 250 cells
around the player, awake within about 170). Ours places them all too: one placed more than 120 cells from the player
is held back in the instance (`InstanceWorld.HeldBackMonsters`), counted and saved but not created, and is created
when a player comes within 120 cells, or the nearest few per pass out to 163. Differs from GGG: no sleeping copy
between 170 and 250 cells, a held-back monster cannot be hit, and it gets its id and its mods when it is created.

**The map overlay's "X monsters remain"** is S→C 0x1a0, one byte, and the client never counts monsters itself. The
instance constructor (0x14214b4a0, at 0x14214e686) starts `instance+0xd7cd` at 0xff, and 0x1a0 is its only writer in
the whole client (dispatcher case 0x7e at 0x14215718b copies `byte [msg+0x10]` there), so with nobody sending it every
area read "More than 50 monsters remain". The text builder 0x1406b24e0, reached from the overlay builder 0x1406b25f0 at
0x1406b279b, picks ClientStrings 0x1ab6 "1 monster remains" for 1, 0x1ab7 "{0} monsters remain" for 0..50 and 0x1ab8
"More than 50 monsters remain" for 51..255, so an area of more than 50 living monsters is sent 255. The Gateway sends
the byte once the area's packs are placed (after 0x21) and again whenever the count changes: a kill, a hostile
summon or a removal. It counts living hostile monsters in both the active and dormant rosters; friendly summons and
corpses never count. Emerging moves between those rosters without changing the total. Live 2026-11-14 with `/raw` in an entered area: `01a0 05` → "5
monsters remain", `01a0 01` → "1 monster remains", `01a0 00` → "0 monsters remain", `01a0 33` → "More than 50 monsters
remain".

**An instance keeps its monsters** (#12), as in the real game. When a connection closes, the instance
(`InstanceStore`) saves each living monster's variety, level, rarity, mods, team, life and current position (its walk
estimate, `SavedMonster.Save`); dead ones are not saved. The next connection into that instance re-creates exactly
those monsters instead of planning the packs: where they were (a dragged group stays a group), idle until the player
comes within aggro range, and a damaged one gets its life with 0x1b1 right after its 0x2bb. A buried emerge monster
is kept buried; one that has risen comes back risen (Transitionable state 2). Only a new instance plans
fresh packs. The saved state goes with the instance's ground items: kept in the Gateway's memory and wiped when the
instance expires.
`Game_MONSTERS=0` turns it off. Only unique and rare monsters are logged one by one; a summary line lists the rest.
Shared delivery must copy plaintext per recipient: `Program.Write` encrypts its input in place, possibly after a
send-gate wait. Reusing one buffer across viewers corrupts another viewer's payload or the owner's later send;
copying once for the entire fan-out is insufficient. `Game.ToOthers` and the combat/action mirrors own a separate
copy for each recipient, including after rewriting player identities and action sequences.

## 3. Room points: where the camp is

`data/room-points.json` also keeps rooms with a `default` start point, and marks each room's server objects with their
type ([12](12-quests.md) §5). Only the client's own doodads count for the fit.

The layout is generated by the client, so where a room landed has to be captured. Rooms (`.arm`) carry named points
before their slot grid, e.g. `Metadata/Terrain/Act1/Area2/Rooms/scavenger_camp_2a.arm` (the "camp" node of the graph):

```
32 106 0.436332 "mapboss"
81 68 0.785398 "%area%-cannibalfire"
```

and the bonfire rooms (`bonfire1`–`6.arm`, scattered by the master's `FillTiles` `random_fill_tiles.gft`) one or three
`%area%-cannibalfire` points. The client does not use these points; it does place the room's **doodads**:

- The tile-object loop (`0x141d82d90` → `0x142047e30`, [08](08-area-transitions.md) §7) only sees `.tmd` records; room
  doodads never pass there (traced 2026-10-18: ~40 records per area, no bonfire).
- **`0x142048bb0`(?, record, u8)** creates one client-side doodad from a record laid out like a `.tmd` record, already in
  world terms: i32 grid x, i32 grid y, f32 world x, f32 world y, f32 z (+0x10), f32 angle (+0x14), type handle +0x30,
  `.ao` handle +0x38 (handle +8 → UTF-16 path). It runs for every doodad while the area loads, room doodads included
  (The Coast: `beachfire_04.ao` at (1154, 656), the camp's `beach_sheet_03.ao` at (1181, 627), …).

So:

1. `scripts/re/atlas` with `room-points` writes `data/room-points.json`: per area, the rooms of its RoomSet and
   FillTiles that have boss points (below) or `%area%-…` points, with the points and the room's doodads (room
   cells). The room sets of the sub-graphs a graph embeds (nodes tagged `graph`, a graph file or a FileGroups group)
   count too: The Upper Prison's graph is only two sub-graphs, "Prison" and "Warden" (Brutus), and The Cavern of
   Anger embeds Merveil's arena (`_cave_river_boss1.dgr`). Before manifest version 5 these areas had no boss rooms.
   The boss points the room files write (`AreaPopulation.BossPoint`; version 6 keeps them all): `mapboss<n>` is the
   n-th of WorldAreas `Bosses_MonsterVarietiesKeys` (The Upper Prison: the Necromancer at `mapboss1`, Brutus at
   `mapboss2` in the `boss_cells_*` rooms before his arena; The Cavern of Anger: the sea witch and Merveil; The Grain
   Gate: `mapboss1` … `mapboss7`, the seven Gemling Legionnaires; The Harvest: Malachai `mapboss1`, his second form
   `mapboss2` in the inner room, Doedre/Maligaro/Shavronne `mapboss3`-`5` at the organs), `mapboss<n>&<m>` two of them
   (maps), with or without a `WG` suffix (Prisoner's Gate's `bossphase1/2` rooms: `mapboss1WG` Abberath, `mapboss2WG`
   his second form; The Oasis: `mapboss1WG` Shakari in the Sand Pit, `mapboss2` her second form in the Swallowing
   Sands). `mapboss`, `force:mapboss` (Kitava's arenas, The Cathedral Rooftop and The Feeding Trough), `mapbosspack`
   (The Toxic Conduits) and `mapbossWG` take the area's bosses no other point took. Not used: `mapboss-<variety>`
   (Shaper guardian maps) and the league points `mapbosswarband…`, `mapbossexilepack`. Before version 6 only `mapboss`
   and `mapboss<n>` were kept, and neither Kitava, Shakari, Abberath nor the Lingering Aberrations had a point.
   What a room places itself is in its points too (version 8 keeps them in the rooms kept for other reasons): monsters
   by MonsterVarieties Id (`Metadata/Monsters/…`: boss arenas' daemons and guards, the Imperial Gardens trial's four
   `RootSpiderHidden`), server spawners (`Metadata/QuestObjects/…Spawner`) and pack points `normal`, `magic` and
   `rare`. A pack point is one of the area's scattered packs of that rarity. Every placed room of a generated layout
   places its own (`ServerLevels.Level.PackRooms`, read from the generated rooms, not from `room-points.json`); a
   captured layout only its Trial of Ascendancy's ([labyrinth-trials](../game/labyrinth-trials.md), "Monsters").
   A generated room's spawn list (section 0) can also name a complete `MonsterPacks.Id`. Kaom's Stronghold names
   `kaomwarriorpack`. The Lair's shared waterfall room names five beast packs, of which its `Maps.MonsterPacksKeys`
   lists four. A pack assigned through `WorldAreasKeys` may have zero area weight. Successful room packs count
   against ordinary density. Direct placement handles positive variable ranges, including the Bone Warden in
   Olmec's third guard pack. Zero-range rows can describe encounter bosses and still need their placement verified.
   The survey of 3 265 generated layouts (795 areas, two seeds each) found rarity points in 1 498 layouts.
   Of the 2 126 points, 1 663 were `magic`, 450 `rare` and 13 `normal`, with 99% on walkable cells.
   The area's remaining ordinary packs use the scattered budget
   (§1). A quest room placed where the client placed it is also returned with its points and objects
   (`AreaPopulation.PlacedRoom`).
   **Shrine guards** (`ShrineGuards`, `PopulateArea`): a shrine the level generator placed (`Metadata/Shrines/Shrine` in
   its room; `ShrinePlacement`) gets one guard pack at its cell. Which pack and how many come from the shrine's rule:
   a shrine whose `Shrines.MonsterPacks` names packs takes one of those rows. A basic shrine draws an area pack.
   Both use the row's variable members and fixed roster. The client's shrine rooms carry no monster points of their own
   (`metadata/shrines/*.arm`: the statue and decals only), nothing in the data scales a guard count by area level, Act,
   map tier or difficulty, and the Atlas passives that make a map shrine's guard pack magic
   (`map_shrines_have_guaranteed_magic_pack`) and add a chance of a second (`map_shrines_additional_pack_chance_%`) are
   the only other statement the game makes about them: normal rarity, one pack, in the campaign. A map picks its rule
   through `ShrineGuards.Resolve(..., mapArea)` so a map's rule can change without the campaign's. A shrine within
   `AreaPopulation.TransitionClearance` (25 cells) of the arrival or an area transition is not guarded (the 1.0.0d note,
   "Monster packs around shrines are now not spawned if they are too close to an area entrance or waypoint"). The guards
   stand **around** the shrine and never on it: a guard is kept at least `ShrinePlacement.ObjectSize` (7) cells from the
   shrine's cell, so the statue and its base stay clear, and out to `AreaPopulation.ShrineGuardRadius` (12) cells rather
   than an ordinary pack's 9, where a pack of four still fits around a 2×2 shrine room. The log line says the spread
   (`shrine Regen at (506, 643): guarded by Act1-Rhoa (2–4): 2 around it at (-6,6) (7,-7)`). The members are spread
   around the statue rather than scattered from it: their bearings are `2π/n` apart from a rotation the instance's rng
   gives, on radii `ObjectSize + 1` and `ObjectSize + 3` in turn (`RingCells`), so a pack of two stands on opposite sides
   and neighbours never share a spot; a blocked ring cell falls back to the ordinary search. A guard has to be seen
   standing, so an emerge variety — buried and rising only when the player is on top of it, which made a shrine guarded
   by `Act1-ZombieMiredEmerge` look empty — is passed over while the pack has a standing variety (`guardSpawnable`); a
   pack of emerge monsters only is still used rather than leaving the shrine bare. The guards
   are planned with the area's monsters, so an instance keeps them: re-entry restores them rather than adding a pack, and
   activating a shrine neither respawns nor removes its guards. The shrine's monster buff (`Monster_ShrineBuffsKey`) is
   on the monsters around it while it is unclaimed (`MonsterAuraEffect`): every hostile monster within 20 cells
   (`ShrineManager.NearbyRadius`) gets the row's own monster values as `EncounterStats`, the client is sent no buff icon,
   and the buff comes off again when the shrine is claimed or the monster leaves the neighbourhood, restoring the
   monster's previous `EncounterStats`. Live (2026-11-14, The Ledge, `/shrines Acceleration`): four Rhoas gained
   `acceleration_monster` and lost it on the claim.
   **Which boss stands where** (`AreaPopulation.Plan`): each boss once. First the numbered points take theirs (a boss
   whose number two placed rooms carry stands at the first). Then the named packs: a pack's own boss
   (`BossMonster_MonsterVarietiesKeys`, at its `BossMonsterSpawnChance`) comes with it, unique when it is one of the
   area's bosses (The Mines Level 1's `%area%-minerhammerbosspack`: Pikerivet; The Crystal Veins'
   `minerlanternbosspack`: The Burning Man); before, named packs never brought their boss. Last, one of the plain points
   (picked at random when several rooms with one are placed) takes every boss still left, side by side: The Den (Act 7)
   has The Great White Bones with The Bone Sculptor, The Solaris Temple Level 1 both seals, The Toxic Conduits its five
   Lingering Aberrations. Before, a plain point took one boss at random (The Ashen Fields: Greust one time in four)
   and every placed plain room took one more. A boss whose point is off the walk map (Kitava in his pit, Arakaali in
   her web, Doedre the Vile at her cauldron) stands on the nearest free walkable cell within 60 cells; before, it was
   silently left out. A boss's **later form** is not placed with the area (`AreaPopulation.LaterForms`, marked
   `AreaPacks.Absent`): a later Bosses row with an earlier row's name whose Id is that row's Id + "2" (Prisoner's
   Gate's Abberath2 at `mapboss2WG`, The Oasis's Shakari2 at `mapboss2`, The Harvest's MalachaiBoss2 at `mapboss2`: the
   official fight brings it when the first form flees or falls; bosses that only share a name, The Mud Flats' Forgotten
   Warriors or The Grain Gate's Gemling Legionnaires, are separate), and The Rotting Core's Depraved Trinity, which forms in
   the Black Heart once Doedre, Darksoul, Maligaro, The Broken and Shavronne, Unbound are dead. A quest or the fight
   creates them; before, they stood from the start beside the first form. The area's `bosspack` stands once: at its own
   `%area%-bosspack` points when the layout has them, else at the first boss point; before, every boss point added one
   (The Cavern of Anger, The Dried Lake, The Lunaris Temple Level 2 up to three times).
   **Spawner objects** (`AreaPopulation.BossSpawners`): a room's server-side spawner that stands for one of the area's
   bosses becomes that boss's point. The Quay's `boss.arm` (kept for Clarissa, a quest NPC): `TolmanBossSpawner`, Tolman
   in the stocks, who breaks out in the official game (its `spawning` states), is created as Tolman at the spawner.
   **Map boss override** (`MapBossOverride`): an area with a `MapBossOverride…` mod (stat `map_boss_override`) lists
   every candidate boss and meets one. The Ashen Fields (`MapBossOverrideAshenFields`; Greust, Oak, Alira, Kraityn of
   `Metadata/Monsters/Ralakesh/`): the bandit lord the character spared in Act 2 (the other two
   `a2b-Killed{Str,Int,Dex}Bandit` flags set) is Ralakesh's avatar, Greust when all three died or no bandit quest was
   done. The client's own lines say so: Eramir's `EramirRalakeshQuestOak`/`…Alira`/`…Kraityn`/`…Greust` and Helena's
   `HelenaOn…Quest` ("He truly believed that if he stood against Oak…").
   The campaign's quest engine sets the kill flags. An instance's boss follows the first character to enter it, and
   other players who join the instance meet the same boss.
   **Not placed** (the check below lists them with the reason): Marceus the Defaced (The Marketplace) is a tile's
   statue spawner (`Spawner_StoneStatueLargeMaleBoss`) that wakes as the player comes near, and tile spawners are not
   among the generated quest objects (`AreaObjects.QuestTypes`); The Solaris Temple Level 2's three Banners have their
   point only in `tencent_lunaris_boss_1.arm`, a room the layouts never place (the area's unique is The Goddess of
   Purity, a gold statue spawner); Thistlesage's pack `1_3_15-bosspack` is named by no room or tile of The Imperial
   Gardens' room set. The areas the Gateway does not generate (The Crossroads, The Twilight Strand, The Karui Fortress,
   The Southern Forest of Act 6) have their boss points only with a recorder capture.
   Rooms with a quest server object (an NPC, a `Metadata/QuestObjects/` object or a quest chest; lore objects under
   `Metadata/NPC/Info/` excepted, `RoomFile.IsQuestObject`) are kept too, without any point (version 4): Fairgraves'
   ship, Piety's room in The Upper Prison, the blocked road at Prisoner's Gate, so quests find their objects
   ([12](12-quests.md) §5).
2. `scripts/walkmaps` (`live.ps1 -Walkmaps`) hooks `0x142048bb0` and writes `data/walkability/<area>_seed<seed>.doodads.json`.
3. `AreaPopulation.LoadPoints` fits each candidate room: anchored on the room doodad whose model is rarest in the
   capture, it tries each captured doodad of that model × the 8 turns/flips, and keeps a placement when ≥ 70% of the
   room's doodads land within 2 cells of a captured doodad of the same model. The points go through the same placement.
   A room with a unique boss point (and The Tidal Island's `medicine_boat` rooms, `MercyMission.BoatRooms`) takes its
   actual placement instead, from `<area>_seed<seed>.rooms.json` (12-quests.md, opening encounter) or the Gateway's
   own generation of the area (`ServerLevels`), whenever there is one: the fit misses sparse rooms (the boat rooms have
   only two snowfall emitters around Hailrake's `mapboss`, Brutus's `boss_cells` rooms are bare) and matched a repeated
   room several times (four Necromancers in The Upper Prison seed 1, three goatmen on The Ledge), each match one more
   unique boss. When the placements are known (a generated area, or a capture with its `rooms.json`), a boss or quest
   room the layout did not place has no points or objects even if its doodads fit: sibling rooms share their doodads
   (The Broken Bridge's `broken_bridge` and `broken_bridge_left`, The Wetlands' four `bandit_boss_*` rooms) and the
   unplaced sibling's fit on the placed one was a second Kraityn, a third Oak.
   Doing so for every room would change
   other areas: it adds twelve nest packs around a1q4's Rhoa Nests and moves the start points of The Coast's
   `to_coast` and The Tidal Island's `toislandcliff`, so other rooms keep the fit.
   A room with a quest server object takes its actual placement for its objects (its points keep the fit), from
   `rooms.json` or the Gateway's own generation (`ServerLevels`), whenever there is one: the fit misses sparse rooms and
   matches a repeated one more than once. Seed 1 places Fairgraves on his ship in The Ship Graveyard at (742, 374), the
   Allflame's slave girl in The Ship Graveyard Cave at (666, 233), Piety in The Upper Prison at (1486, 553) and her
   blockage at Prisoner's Gate at (1275, 390), The Twilight Strand's force wall at (1403, 679) as before.

The Coast seed 1 (live): `scavenger_camp_2a` at (1241, 598) turned [0 −1; 1 0], 32/33 doodads; `mapboss` (1135, 630),
its `cannibalfire` (1173, 679); 8 bonfire rooms (12/12 or 21/21 doodads). The doodad angles agree (room angle + Ï€/2).
Fire Fury stands at `mapboss`, about 30 cells from the campfire, with `1_1_2-bosspack` around her.
The Tidal Island seed 1 (live 2026-10-25): `medicine_boat_1` captured at (138, 345), code 3; Hailrake stands at its
`mapboss` (265, 437) on the shipwreck, with `1_1_2a-bosspack` at the room's `%area%-bosspack` (292, 420) (until the
boss pack stood once per area, a second one stood at `mapboss`).

## 4. Open

The existing opening capture contains 39 distinct monster creation records and six additional actors inferred
from monster-like updates. Its first 35 seconds are missing: the reconstructed 45 is an approved provisional
calibration target, not a verified complete-area total or evidence of normal-only packs.
For new Act 1 Twilight Strand instances, reserve one slot for the separate tutorial zombie and retain named/boss
spawns, then cap scattered members at the remaining target (normally 43 plus Hillock). The final pack can be
shortened, and insufficient candidate/placement space can produce fewer monsters. Existing instances keep their
population. Other areas, rarity rolls, life and damage retain their existing behavior; official allocation and
placement algorithms remain unknown.

- Pack density and magic/rare chances are guesses (§1); so are the mod counts (§5).
- Unique bosses in the map's WorldAreas list get the life and damage increases from `MonsterMapBossDifficulty` and
  `MonsterMapDifficulty`. Their level sets the factors (×6.75 life and ×1.07 damage at 75, ×20.73 and ×1.15 at 83).
  Both tables name Stats rows 2500 and 2501, the hidden life and damage stats already sent for the map tier. The
  boss's percentage adds to each tier value. All 152 boss entries across the 110 layouts in the 3.29.3c atlas
  selector lack a dedicated health bar. Boss damage and speed affixes also affect the ordinary AI's secondary skills.
  The separate boss area-of-effect roll on the life affix still needs its skill-area calculation.
- The official pack sampler at mixed-ground boundaries and the priority of ground restrictions for explicitly named
  encounters are unknown. Captured walk maps carry no ground names for the weighted draw.
- Emerge packs (Gravel Eaters, Dripping Dead, the Strand's Drowned, Tide Striders …) are placed waiting and rise within
  24 cells of the player ([10](10-chat-and-commands.md) §5); the distance is ours (the official one is in the server's
  AI files). The pack rule for the 161 varieties whose Id contains "Emerge": the 85 without a Transitionable are
  ordinary monsters and are placed now (before, every "Emerge" Id was skipped; e.g. PusElementalEmerge in 1_4_6_3
  stands as usual); the 63 whose type waits to rise are placed waiting; the 13 whose Transitionable is something else
  (it sets `num_states`, or pauses no clip) are still skipped.
- A campaign boss plays its whole kit, not just its first granted effect (an act boss, or any variety with
  `BossHealthBar`, whose row grants more than one attack, spell or projectile; `Modules/Core/Game/Monster/BossKits.cs`).
  The skill it always had stands first and still chooses on a tie, a ready skill with a cooldown goes before one without,
  the rest rotate by least recently used, and a skill that reaches nothing in range waits while the boss walks in.
  Bosses whose MonsterVarieties row has `BossHealthBar` and whose type
  extends `Metadata/Monsters/BossLifeBar` show the big life bar once they aggro (12-quests.md §6).
- A layout whose room fit finds no boss room at all still places the area's unique bosses: each stands at the walk map's
  deepest cell from the entrance (clear of the area transitions), the place a boss room would be. Our fallback, chosen so
  a seed nobody recorded has its boss; the room itself stays unplaced.
- **Rarity life:** a magic, rare or unique monster has `monster_life_+%_final_from_rarity` of its rarity's monster mod
  (Mods.dat MonsterMagic2 148, MonsterRare2 390, MonsterUnique2 698) on top of its base life; the Gateway applies it
  and sends the stat and the final `maximum_life` in the monster's Stats (the client applies no modifier itself: with
  only the rarity stat Hillock's bar stayed full above his base 39).
  Magic and rare monsters also get `monster_life_+%_final_from_rarity_table` (Stats.dat row 9877, on no Mods.dat
  row): MagicMonsterLifeScalingPerLevel / RareMonsterLifeScalingPerLevel `Life` at their level (magic +50% at level 1
  to +75% from 71, rare +33% to +100% from 71). poewiki's "Rarity" page confirms both parts: the hidden MonsterMagic2
  / MonsterRare2 "148% / 390% more maximum Life", and "Additionally magic / rare monsters receive a life multiplier
  based on their level", whose tables match these two for all 100 levels. A rare Hungry Corpse at level 70 has
  4.9 × 2 × its base life.
- **Hits:** DefaultMonsterStats `Damage` (`MinionDamage` for an IsPlayerMinion MonsterTypes row, PoB's
  `monsterAllyDamageTable`) × DamageMultiplier × the attack time in seconds (unless MonsterTypes
  `BaseDamageIgnoresAttackSpeed`), spread ± MonsterTypes `DamageSpread`; then the rarity's monster mods:
  `monster_rarity_damage_+%_final` (magic 30, rare 50, unique 70) and
  `monster_rarity_attack_cast_speed_+%_and_damage_-%_final` (20, 33, 33: that much faster, that much less damage per
  hit; read from the stat's name, no description exists). Hillock at level 1: 6.6–12.3 every 1.24 s.
- Kept monsters lose their corpses (a corpse stays while the player is in the area, but is not saved) and a `/spawn_mob` name.
- Each new (area, seed) needs a `-Walkmaps` visit for its room points; without the capture there is no unique boss
  (the log says so).

## 5. Mods

State of 2026-10-25, verified live (The Twilight Strand, The Coast). Code: `MonsterMods.cs` (`MonsterModCatalog`,
`MonsterModEffects`), `Modules/Core/Game/Monster/Mods/MonsterMods.cs` (the hooks), `MagicProperties.cs` (Core).

**Rolling.** An affix is a Mods.dat row with the monster Domain (3) and GenerationType 1 (prefix) or 2 (suffix); 111
rows have a spawn weight. It can roll when its `Level` is at most the monster's level and the first of its SpawnWeight
tags that the monster has gives a weight above 0. The monster's tags are its MonsterVarieties `TagsKeys`, `default`,
`magic` or `rare` (the client adds the same tag when it reads the mods, `0x142205d50`; many mods have `magic` 0, so
only rares get them) and `not_part_of_pack` for a monster on its own (`/spawn_mob`). Each affix is drawn by weight
from the allowed ones, a ModFamily once, at most Rarity.dat's MaxPrefix / MaxSuffix of each kind; every monster mod
has a fixed value. The counts are not in the client's data: Rarity.dat's `MinMods` / `MaxMods` (1–2, 4–6) are the
items', and Monster.ot, MonsterMapDifficulty and WorldAreas have no count. So they follow poewiki's "Rarity" page: a
magic monster is "given one monster modifier", a rare one "two to four monster modifiers" (the 3.20 FAQ also balances
around 2 to 4). The 3.19.0 patch notes gave campaign rares "only … one or two modifiers", but 3.20 replaced that mod
system, so the current page wins; the campaign keeps fewer: below area level 68 2 at 75%, 3 at 25%, from 68 2 at 40%,
3 at 40%, 4 at 20%. **These percentages are invented.** The magic monsters of one name share one roll per instance
("All monsters in an instance that are the same or very similar will have the same magic monster modifier", 3.19.0;
The Coast's Drowned are several MonsterVarieties rows), as long as the affix could roll on the variety: a shared roll
with a weight of 0 for its tags (Blurred on the large Savage Crab, which is `not_dex`; of Rime on a fire-throwing
Cannibal) is not given to it; the monsters of its name and tags share their own roll instead. Normal and unique
monsters roll none.

**The rarity's own mods** (Mods.dat MonsterMagic1–11, MonsterRare1–12, MonsterUnique1–10: life, damage, speed,
size, rewards) go with every magic, rare and unique monster in the hidden list (flags bit 11), which the client does
not show; the server applies their stats as it does the affixes'.

**What the client gets** (ObjectMagicProperties' base part, `0x142208700` as for items, [13](13-items.md) §2a): the
affixes in the explicit list (bit 5), prefixes first, and a rare monster's name as two Words.dat keys (bit 8): a
`MONSTER_PREFIX` (word list 3) and a `MONSTER_SUFFIX` (list 4) word drawn by the same tags ("Gore" + "walker"; the
only default-weighted words are placeholders such as " Broken"). Live, hovering a rare showed "Gorewalker" in yellow
on its life bar at the top of the screen and under it one line per affix from the monster stat descriptions ("Extra
Life", "Fire and Ignite Resistant"); a magic one showed "Quick Hungry Corpse" in blue and "Hasted". The client
applies none of the mods itself: a rare with Fecund (40% increased life) at half its server life showed a half bar.
So the server sends the final `maximum_life`, the rarity's life stats, `movement_velocity_+%` (the client walks a
monster at its base × (1 + value / 100), `0x141e827a0`: a Quick magic Hungry Corpse stepped every 0.083 s, 12
cells/s, a normal one every 0.125–0.133 s; the server walks it at the same speed), `actor_scale_+%` and
`life_regeneration_rate_per_minute_%`.

**A change while it lives.** S→C 0x2bc to component 2 with the same ObjectMagicProperties data as the create (and its
trailing `u8`) redraws a living monster's name plate: a monster a Tormented Spirit touched showed "Rogue's Touch" under
its name (live 2026-11-18). Its stats still come only from the server (0x1b3).

**Effects** (`MonsterModEffects`, summed per stat over all its mods):

| Stats | Effect |
|---|---|
| `maximum_life_+%`, `monster_life_+%_final_from_rarity`, the rarity table (§4) | its life: base × (1 + increased) × each final |
| `damage_+%`, `physical_damage_+%`, the rarity's damage and attack-speed stats | its hit on the player (increased, then the rarity's more and less) |
| `physical_damage_%_to_add_as_fire` / `_cold` / `_lightning`, `non_chaos_damage_to_add_as_chaos_damage_%` | extra damage of that type in its hit, through the player's resistances |
| `attack_speed_+%` (`base_cast_speed_+%` is the same on every mod) | its attack time |
| `base_movement_velocity_+%` | its walk, on the client and in the server's estimate |
| `base_*_damage_resistance_%`, `base_resist_all_elements_%` | the player's hits, Righteous Fire's burn and the player's ignites, per type, capped at 75% |
| `base_self_ignite_duration_-%` | the player's ignites on it last that much less (Fire and Ignite Resistant: 80%; [20](20-damage.md) §3) |
| `base_additional_physical_damage_reduction_%` | the physical part of the player's hits (capped at 75%) |
| `base_damage_taken_+%`, `rare_monster_mod_spell_damage_taken_+%_final` | the player's hits (the second only spell hits: the stat has no description, so it is not taken to cover Righteous Fire's burn) |
| `monster_base_block_%` | blocks the player's attacks: rolled before the hit, a blocked one has no damage, impale, flinch or 0x1af (the client has no block result we know); the log says "blocked" |
| `evasion_rating_+%`, `accuracy_rating_+%` | the player's chance to hit it, its chance to hit the player |
| `always_crit` | its critical strikes (monster attacks have no base chance; × 130% less the player's `base_self_critical_strike_multiplier_-%`, at least 100%). An override: the client's effective chance (`FUN_142762c00`) is 10000 under `virtual_always_crit` before any increase, so reduced critical strike chance (Devout of Solaris) does not lower it |
| `hit_damage_+%_per_10%_missing_enemy_life` | its hit on a wounded player |
| `self_take_no_extra_damage_from_critical_strikes` | the player's critical hits deal their normal damage |
| `cannot_have_life_leeched_from` | prevents Life-source leech from accepted player hits; does not prevent Mana/native ES leech (damage §5b) |
| `cannot_have_energy_shield_leeched_from` | prevents native and Ghost Reaver converted ES leech; leaves Mana available (damage §5b) |
| `life_regeneration_rate_per_minute_%` | it regains life (a 0.5 s server tick, 0x1b1); the player's `enemy_life_regeneration_rate_+%_for_4_seconds_on_hit` scales it for 4 s after each player hit ([Polytheist](../game/polytheist.md)) |
| `base_cannot_be_stunned` | no hit reaction (the flinch is the hit's stun) |
| `chill_effectiveness_on_self_+%`, `base_self_freeze_duration_-%` (of the Ice), `shocked_effect_on_self_+%` (of the Tempest) | the player's chill and shock effect and freeze duration on it ([20](20-damage.md#outgoing-chill-freeze-and-shock-on-monsters)) |
| `action_speed_cannot_be_reduced_below_base`, `movement_speed_cannot_be_reduced_below_base` (Unstoppable) | the player's chill and freeze do not slow or stop it |
| `reflect_non_damaging_ailments_%_chance` (Mirrored) | the chill, freeze and shock a player hit inflicts on it act on the player too |
| `monster_chance_to_not_flee_%` (the rarity's MonsterMagic7 50, MonsterRare7 and MonsterUnique7 100) | its chance to ignore a player hit's flee ([11](11-skills.md) §6) |

**Unsupported mods do not roll.** 37 of the 111 can be honoured: 31 fully (every stat acts, or only stops what the
server never does to monsters: Hexproof, of Congealment, Reflector's, the wither parts of the resistance mods) and 6
in part (of Flames / of Rime / of Electricity: the extra damage without the exposure on hit; of
Haste / of Damaging: the aura reaches only the monster itself; of Precision: its accuracy, not its increased critical
strike chance, which raises a base of 0). The other 74 need a system the server does not have: an aura or buff without
stats of its own, a granted or retaliation skill, a monster or ground on death, ailments, curses, blind or stun on the
player, charges, energy shield, leech, projectiles, area of effect, minions; Berserking's buff has a trigger of its
own; of Incision only increases a critical strike chance monster attacks do not have. Such a mod's name would promise something that does not happen, so the
pool leaves them out and the others keep their weights against each other. `Game_MONSTER_MODS=all` rolls the whole
pool (to see how the client shows them). `MonsterModCatalog.SupportOf` decides.

**Dev:** `/mob_mods [id]` lists a monster's affixes and name; `/mob_mods [id] <mod …>` creates it again with those
affixes (a Mods.dat Id, the Id without `MonsterMod`, or the name with `_` for spaces: `/mob_mods Quick of_Flames`);
`/mob_mods [id] pool` lists what it could roll.

**Live** (2026-10-25/26, besides the display above): Fireball dealt 11–14 to a normal level-70 Hungry Corpse and 3–4
to one with of the Magma; the default attack dealt 8–11 to a normal one and 5 to a Carapaced one, which blocked 3 of
its 12 attacks with Stalwart (no hit message, "blocked" in the log); Blurred doubled the evasion the attack rolled
against (5 081 → 10 162: 45% → 29% to hit); of Deadliness crit on every hit and a normal monster never; of the Hydra
regenerated 3% per second; a kept rare came back with its name, mods and life. The monsters' accuracy is checked
only by the tests (the player was never evaded).

The Sanguine Wave and Mammothcage both reference `MonsterImplicitLifeRegen1` (80% of maximum life per minute).
The Gateway includes direct recovery rows in the hidden list and sends their summed rate in Stats. The official
server's choice of modifier list for these rows has yet to be captured.

Both Grinning Totem varieties carry `MonsterTotemAuraLifeRegen1`. BuffTemplates row 355 gives a radius of 60 cells.
BuffDefinitions row 90 names the recipient effect “Allies Regenerate Life” and grants stat 453. The modifier supplies
150% of maximum life per minute. The Gateway samples living hostile sources and recipients on the instance recovery
tick, including the source itself. Overlapping instances of this buff use the strongest rate. Direct regeneration
adds to it. BuffVisuals row 94 has separate emitter and recipient effects. Each observer receives 0x1c1 on entry and
0x1c2 on loss. Their appearance and the official server's sampling cadence still need a graphical client capture.

**Open:** other variety modifiers and their aura recipients are incomplete; monsters have no base
resistance or armour yet (open PRs #225 / #226 add base resistance), the player no penetration, so the mods'
resistances cannot be penetrated; no monster energy shield; the unsupported mods' systems;
whether the real server sends the rarity's own mods, and in which list. The monster's attack stats on the client
(`MonsterAttack.Stats`) keep only its physical damage: the client shows no monster damage, and the server rolls the
hit.

## Arakaali's arena lasers

`SpiderGodActivateLaser` (ActionTypes row 722, HASH16 `0xf2d4`) enters
`0x141c79bd0`, which gathers `ArakaaliTurret_A` and `ArakaaliTurret_B` objects. Its shuffle starts at `begin + 8`
without checking an empty list: with both begin/end pairs null, the count becomes -1 and it dereferences address 8
at `0x141c7a0e4`. The Toward/Away effects use the same action. These are encounter drivers, not standalone attacks;
generic boss AI cannot call them without the turret objects. A missing `SpellProjectileCold` clip in the default
stance is a separate phase-admission problem, not the faulting instruction.

## ControlZone and Orion's projection

The 3.29.3c ControlZone vtables `0x1435a86b0`, `0x1435a8840`, `0x1435a2078` and `0x1435a7218`
all use `0x1400f1180` for slot 17: `ret 0`, with no component bytes.
`AtlasExile5ApparitionDetached` therefore adds nothing after its StateMachine data.
Orion's ACT maps animation types 36/37/38 to the meteor-maze start/hold/end sequence in both stances.
His BeamBomb stat set gives separate per-type degeneration; its hit conversion does not convert that degeneration.

## Maps

A map's area has no MonsterPacks row of its own: its packs are the map's Maps.dat `MonsterPacksKeys` (by
`Regular_WorldAreasKey`; Beach → `atlas-crab1`, 6–9 parasite crabs), scattered like an area's with weight
`PackCatalog.MapPackWeight` (100), at the area's level (Beach 69). The boss at `mapboss` comes from WorldAreas `Bosses`
as for any area (Beach: Glace). 21-hideouts.md §5.

Strand Wake Drowned use a scoped 60-cell calibration, capped by a shorter aggro range. Eight opening events from
the older 3.29.3b capture support that range. Other emerge types retain 24; general AI parity remains unproven.

## Essence monolith

`Metadata/MiscellaneousObjects/Monolith` reads Positioned, Animated, MinimapIcon, InteractionAction,
Transitionable, Monolith, StateMachine and BaseEvents, in that order.
The Monolith reader `0x1415df360` takes little-endian `u8`, MonsterVarieties `HASH16`, `u8` count,
then count × Essences `HASH32`, then `u32` (component +0x104; meaning unverified, currently sent as zero).
The first byte is stored at component +0xf0; the essence references occupy +0xc8/+0xd0.
The object's four Transitionable states start at 1 and release at 4; its interaction distance override is 20.
Its `targetable` shared state is inverted: 0 enables targeting, 1 disables it.
Transition 4 removes collision and hides its marker; use component indices 4 and 6 for transition and shared-state updates.
Essence-list changes recreate the same object id; the verified `0x74` currency target is also used for monoliths.
Vaal Orbs replaced Remnants in 3.27; the legacy `CurrencyCorruptMonolith` table row is not a current gameplay input.
The historical `deal_chaos_damage_per_second_for_10_seconds_on_hit` id has an **8-second** duration in the pinned
client's `stat_descriptions` block. Its Decay icon is BuffDefinitions row 766 (`IsRecovery=false`, `__col44=false`):
no inline values or `u82`, despite its granted per-minute chaos-damage stat.

`EssenceDaemonSummonMovingKaruiSpirit` resolves `alternate_minion=185` to `EssenceKaruiSpiritMovingAxe`;
its cast/cooldown are 1/6 seconds and it summons one spirit.
One is the cast batch; [Contempt's encounter](https://www.poewiki.net/wiki/Essence_of_Contempt) has three chasing spirits.
Both it and `EssenceDaemonSummonSpectralSkull` carry implicit `minion_dies_when_parent_dies`,
`minion_dies_when_parent_deleted` and `summoned_monsters_no_drops_or_experience`.
`EssenceMovingAncestor.ot` makes the Karui spirit untargetable, damage/stun immune and unknockable.
Its Heavy Strike has +50% damage and +80% **increased** attack speed, not a more-speed modifier.
The server-only `.ais` files are absent; daemon activation, summon scatter and `DiesAfterTime` duration remain unverified.

`essence_delirium_spawn_monster_on_death_variation=10` selects row 9 of `SummonedSpecificMonstersOnDeath`:
`EssenceInsectWildExplosiveColossal`, one insect (`__col4`).
Its `InsectSuicideExplosionHuge` action casts for 3 seconds and deals the stat set's level-scaled secondary fire damage.
On 3.29.3d, `SuicideExplosion` factory `0x1419b53d0` constructs `0x141a46170`; its execute function `0x141a46250`
computes `floor(15 * radius_percentage / 100)` at `0x141a462cc`–`0x141a462e5`.
The percentage getter `0x14193efd0` reads stat `0x1591` plus 100; the skill's +100% final radius therefore gives 30 cells.
These code addresses also hold for 3.29.3c; the disassembly used the allowed d executable, SHA-256 `e9ba3d31…f41f53d`.
