# 14 — Monster packs in areas

State of 2026-10-18, verified live in The Coast (seed 1): on entering, the Gateway fills the area with monster packs
from the game tables: normal, magic and rare packs scattered over the walk map; cannibal packs at the bonfires; the
unique boss **Fire Fury** and her guards at the cannibal camp, next to its campfire. Code: `src/GameServer.Gateway/Monster/MonsterPacks.cs`
(`PackCatalog`, `AreaPopulation`), `PopulateArea` and `SpawnMonster` in `Modules/Core/Game/Monster/Monsters.cs`.

## 1. The data

| Client table (client.db) | What it gives |
|---|---|
| `MonsterPacks` | one row per pack: `Id`, `WorldAreasKeys` and their weights (`__col9`: one weight per area, or one for all), `MinCount`/`MaxCount`, `BossMonsterSpawnChance`, `BossMonsterCount`, `BossMonster_MonsterVarietiesKeys`, `Grounds` |
| `MonsterPackEntries` | the pack's varieties: `MonsterPacksKey`, `MonsterVarietiesKey`, `Weight` |
| `WorldAreas` | `AreaLevel` (the monsters' level) and `Bosses_MonsterVarietiesKeys` (the area's unique boss) |

The Coast (`1_1_2`, WorldAreas row 6, level 2):

| Pack | Weight | Members | Placed |
|---|---|---|---|
| `Act1-CoastCrabs` (row 2) | 100 | 2–4 Savage / Kelp / Shore / Coconut / Spider Crabs; 80%: one large Savage Crab (row 4515) | scattered |
| `Act1-ZombieDrowned` (row 3) | 100 | 3–4 Drowned; `Grounds`: the beach/ocean ground types | scattered (grounds not checked) |
| `Act1-SandspitterEmerge` (row 4) | 100 | 3–4 Gravel Eaters | scattered, buried: they rise when the player comes near ([10](10-chat-and-commands.md) §5) |
| `1_1_2-cannibalfire` (row 620) | 0 | 2–4 Cannibals (male, female, fire throwers, Fire-eater) | room points `%area%-cannibalfire` |
| `1_1_2-bosspack` (row 621) | 0 | 6–8 Cannibals | with the boss at `mapboss` |
| boss: Fire Fury (row 2845, `CannibalFemaleBossWitch`) | | Unique | room point `mapboss` |

Packs whose `Id` is `<area>-<name>` have weight 0: the layout places them, through **room points** (§3).

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
   The points are few: 2 126 in 1 498 of 3 265 generated layouts (795 areas, two seeds each): 1 663 `magic`, 450 `rare`
   and 13 `normal`, 99% of them on walkable cells. So the room files do not say where an area's ordinary packs stand,
   or how many it has (§1). A quest room placed where the client placed it is also returned with its points and objects
   (`AreaPopulation.PlacedRoom`).
   **Shrine guards** (`ShrineGuards`, `PopulateArea`): a shrine the level generator placed (`Metadata/Shrines/Shrine` in
   its room; `ShrinePlacement`) gets one guard pack at its cell. Which pack and how many come from the shrine's rule:
   a shrine whose `Shrines.MonsterPacks` names packs (the eight apparition shrines, one of 2–9 monsters a pack, in
   area-agnostic rows) stands one of those packs; a basic shrine names none and stands one of the area's own scattered
   packs, at that pack's own `MinCount`–`MaxCount`. The client's shrine rooms carry no monster points of their own
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
- Unique map bosses get `MonsterMapBossDifficulty` plus `MonsterMapDifficulty` life and damage percentages
  (`MonsterCatalog.MapBossScaling`; level 83 is ×20.73 life and ×1.15 damage). The area level is the map level here; a
  future map-affix system may add more.
- `Grounds` (Drowned on wet beach) is not checked; we have no ground-type grid.
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
- Kept monsters lose their corpses (a corpse stays available while the player is in the area, but is not saved, and stops
  being drawn a visual minute after the death) and a `/spawn_mob` name.
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

**Open:** the variety's own mods (MonsterVarieties `ModsKeys`) are not sent or applied; monsters have no base
resistance or armour yet (open PRs #225 / #226 add base resistance), the player no penetration, so the mods'
resistances cannot be penetrated; no monster energy shield and no auras on their allies; the unsupported mods' systems;
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
then count × Essences `HASH32`, then `u32` (component +0x104).
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

### The imprisoned monsters and the essence panel (2026-11-20)

**What the official client shows.** The encounter is one large blue crystal (`Monolith`) with a rare monster in it and
3 to 5 small crystals (`MiniMonolith`) around it, each holding a magic monster of the same pack. The crystals are the
only objects: the monsters are separate monster entities standing exactly at the crystals, so their models show through
the ice (a magic mini looks like a blue, frozen spider). The main crystal is activated three times (Transitionable 1 → 2 →
3 → 4); the fourth transition removes collision, plays the break and releases the rare. The small crystals break with it.
The panel that the official client draws over the crystal (the two screenshots of 2026-11-20) is, top to bottom:

| Row | Source |
|---|---|
| Ornate gold name, e.g. "Shavronne's Greed" or "Deep Crustacean" | the imprisoned monster's **MonsterVarieties.Name**, not its generated rare name (the rare name stays on the top life bar). Both screenshots' names are `MonsterVarieties` rows (`Metadata/Monsters/Undying/CityStalkeFemaleShavronneCaster` and `Metadata/Monsters/ShieldCrabs/ShieldCrabTempestShieldCharge`). |
| 1–4 white mod lines, e.g. "Life Regen", "Extra Life", "Fire and Ignite Resistant", "Energy Shield Aura" | the monster's rarity affixes, i.e. `ObjectMagicProperties` (the same lines a rare's hover panel shows; §5). |
| One coloured line per essence, with the essence item's icon, e.g. "Shrieking Essence of Anger", "Deafening Essence of Doubt", "Remnant of Corruption" | the Monolith component's Essences list; the text is the `Essences.BaseItemTypesKey` name (tier included), the icon its 2D art (`Art/2DItems/Currency/Essence/<Type><Tier>`). |
| Italic footer "The monster is imprisoned by powerful Essences." | `ClientStrings` row 2337, Id `MonolithHelperText`. |
| Round portrait at the top right | the essence art (red Essence of Anger in the first screenshot). |

The imprisoned preview monster carries **only its own rare affixes** in `ObjectMagicProperties` (the top section), not the
essence mods: those apply to the released rare only. Spawning the preview with the essence mods baked the essence names
into the top section, so a corruption that changed the essences left the old names there (live 2026-11-20). The essence
names in the panel come solely from the Monolith component's list (the bottom section), which updates on corruption.

The strings that name the interaction come from the same block: `ClientStrings` 2340 `ItemErrorCannotApplyToMonolith`
("Item cannot apply to monsters trapped by Essences."), `BackendErrors` 466 `InteractErrorEssence` ("Something else is
keeping this Essence imprisoned."), `ClientStrings` 1547–1549 (mission tracking "Slay the monster(s) trapped in
Essences."), 3914–3918 (the tutorial: "large blue crystals … have imprisoned several monsters. By activating the central
crystal you can free the monster within …"), 2357 (three of a type upgrade at the vendor) and `ReminderText` 337 (white
maps at most Screaming, yellow and red at most Shrieking). None of these strings is in the exe; the panel is assembled at
run time from the tables.

**Where the panel's data can and cannot come from.** `ObjectMagicProperties` carries no essence list: the 3.29.3c reader
`0x142208700` (called by the component wrapper `0x141e7e080` with `rcx = component + 0xc8`) fills only the mod lists
(implicit at +0x88, explicit +0xa0, enchant +0xb8, scourge +0xd0, hidden +0xe8), the rare/unique name (Words vector at
+0x0, custom name at +0x18) and the alternate quality pair (+0x1c0/+0x1d0). The monolith reader `0x1415df360` stores the
variety reference at +0xe0, the essences at +0xc8 (16-byte entries: the Essences row and its +8 field) and the trailing
`u32` at +0x104. A `GetComponent<ObjectMagicProperties>` → name path (`0x1415dee10` → `0x14220ba50`) builds a monster's
name from the Words vector, its rarity and the custom name; it never sees essences. So the panel has to be attached to the
**monolith** and the only wire field that can link it to the monster (whose name and mods it needs) is the free `u32` at
+0x104. Live 2026-11-20: with the preview monster's entity id there the panel draws the monster's name and mod lines,
the essences and the helper text; with 0 the crystal still renders but the panel has no monster. The field is therefore
the imprisoned monster's entity id (or a reference the client resolves to it), and a monolith built with 0 must keep
working.

**Vaal corruption (wiki, verified against `MapEssenceCatalog`).** A Vaal Orb on an unreleased monolith picks one of four
outcomes at random: release the monsters with the essences unchanged; raise every essence one tier; add one essence of a
new type; or transform each essence to a random type of the same tier in the **next colour group** (raising the tier to
that group's minimum when it is higher — Muttering Fear → a Weeping group-C essence). Group E (Misery/Envy/Dread/Scorn,
"Purple MEDS") transforms into the corrupted four Hysteria/Insanity/Horror/Delirium regardless of tier. The Stability scarab
(`map_stable_essence_corruption`) leaves only upgrade/transform. Crystal Resonance (`map_essence_remnant_convert`; the 3.30
Commensurate Corruption node) replaces the whole list with one of the essences the monster **already had** — an original
essence, not a transformed one — which disables the transform-to-corrupted outcome (the guides advise avoiding the node when
farming corrupted essences). `Corrupt`/`Transform`/`Higher`/`Additional` implement these.

**The corrupted indicator is the `+0xf0` byte's bit 1 (confirmed live 2026-11-20).** The Monolith reader takes only
`u8, u16, u8, count × u32, u32` — no separate corrupted byte — and there is no `MonolithCorrupted` client string; the
corruption buff visuals (653 `monster_remnant_of_corruption_effect`, 654 `remnant_of_corruption_aura`, BuffDefinitions 768)
are the Remnant essence's monster aura, not a monolith flag. Its mod is `RemnantOfCorruptionMonsterAura` (`BuffTemplates`
`AuraRadius` **30**, BuffDefinitions 768); the only other essence aura mod with a radius is Envy's
`MonsterAuraEssenceEnemyLifeDegen1` (radius **50**, BuffDefinitions 100), on Envy tiers 5 to 7. Items carry corrupted as the
Base component's `+0xc7` bit 0;
monsters have no such flag. The component's first byte `+0xf0` (the .ot `is_mini_monolith`) is a bitfield — bit 0 mini,
bit 1 corrupted — so a corrupted main monolith sends 2 and the client shows it as corrupted (verified; the crystal does not
shrink to a mini, so the byte is not read as a plain bool).

**Official 3.30 data (extracted 2026-11-20).** The Steam client's `Bundles2` can be read with
`Tools/PoEDataExtractor` (`cat`/`decode` take the install folder, not a GGPK); its 3.30 `BuffDefinitions.datc64` and
`BuffVisuals.datc64` decode with `data/schema/schema.min.json` with **no column mismatch**. The composition is unchanged
from 3.29.3c, only the row numbers moved: `frozen_in_time` → `legion_petrify` (3.30 `BuffVisuals` 1085, 3.29 1082) and
grants the `frozen_in_time` stat (3.30 stat 9473); `monolith_monster_freedom` → 660 (657); `monster_essence_effect_buff`
→ 661 (658); `frozen_in_time_0/1/2` → 665/666/667 (662/663/664). No `BuffDefinitions` row in either build uses
`monster_essence_effect_buff`, so the only way the client can show it is an `AddBuff` visual override. That is the essence
effect: `frozen_in_time` (for the animation-stopping stat) with its visual overridden to `monster_essence_effect_buff`.
`legion_petrify` is the row's own default and belongs to Legion, not to the essence encounter. The magic/rare rarity glow
buff is also not part of the official imprisoned monster and must not be sent.

**The crystals' client data** (`object_types` in `client.db`, all of 3.29.3c):

| Type | Components (read) | States | Client transition events |
|---|---|---|---|
| `Metadata/MiscellaneousObjects/Monolith` | Positioned, Animated, MinimapIcon, InteractionAction, Transitionable, Monolith, StateMachine, BaseEvents (8); object_size 5, blocking, `animated_object = …/essence/monolith/rig.ao` | 4 | `create_state_1 = PlayAnimation(stage_01); PauseAnimation();`; `create_state_2/3 = PlayAnimation(stage_01b/stage_02b)`; `create_state_4 = PlayAnimation(stage_03); GoToAnimationEnd(); PauseAnimation(); DisableTargetable(); RemoveCollidability(); HideMinimapIcon();` — from the client block of the `.ot` (the parsed `object_types` columns only cover the server block, so they read empty) |
| `Metadata/MiscellaneousObjects/MiniMonolith` | Positioned, Animated, Transitionable, Monolith, BaseEvents, Render (5); object_size 2, blocking, `animated_object = …/essence/monolith/rig_small.ao` | 1 | `on_transition = RemoveCollidability(); PlayAnimation(break);` and `create_transitioned = PlayAnimation(break); GoToAnimationEnd(); PauseAnimation(); RemoveCollidability();` |

The `.ot` `object_size` is **client-side**: the Positioned wire byte is a flag (sending the `.ot` value 5 there made the
client read past the components and answer `0x02fc "Buffer Underflow"`). The **state values are 1..4**: state 1 (the
created state) plays `stage_01` and pauses, which is the intact crystal; each click steps 2, 3, 4, whose client events
play `stage_01`, `stage_02`, `stage_03` and the Animated `on_end_stage_01/02` play `stage_01b`/`stage_02b`. Recreating
the object at a later state (`create_state_2/3`) plays the `b` animation instead, so a change of state should be a
Transitionable update, not a recreate.

The only client-side encasing/ice code in the whole `object_types` table is on
`Metadata/Monsters/LeagueDelve/SkeletonAbyssEssence` (a Delve monster): `create_untransitioned` is
`AddEffectPack('Metadata/Effects/StatusAilments/monsters/frozen_in_time.epk'); PlayAnimationRandom(attack1_sword_sword_left);
PauseAnimation();` and `on_transition` is
`RemoveEffectPack('…/frozen_in_time.epk'); PlayAnimationAttached('…/essence/monolith/rig_small.ao', break);`. That is the
recipe for a statue: an effect pack plus a paused animation, then the mini crystal's break. Ordinary map monsters have no
Transitionable component in their `.ot` (the generic `Metadata/Monsters/Monster` chain is Positioned, Stats,
ObjectMagicProperties, BaseEvents, Pathfinding, Buffs, Life, Animated, Inventories, Actor, Monster, Render, Targetable), so
the map essence encounter cannot use that path directly; it is recorded here because it is the client's own definition of
the encased look and of the break.

The matching buffs: `BuffDefinitions` 772 `monolith_monster_freedom` (Invisible, category 18, granted
`base_damage_taken_+%` 479) uses `BuffVisuals` 657, `Metadata/Effects/StatusAilments/monsters/frozen_in_time_stage04.epk`
— the shatter. `BuffVisuals` 662/663/664 are the three intact stages (`frozen_in_time.epk`, `…stage02.epk`, `…stage03.epk`)
and no BuffDefinitions row uses them; they are the monolith's own animation/effect stages. `BuffDefinitions` 2 `frozen`
(`BuffVisuals` 4: `StatusAilments/Frozen.epk` + `supports/aurafy/ice.epk`) is the ordinary freeze ailment, not a statue.
`BuffDefinitions` 1311 `frozen_in_time` grants stat 9468 `frozen_in_time` (with 981 and 1310), the stat the client stops a
Legion monster's animation with; its own visual is `BuffVisuals` 1082 `legion_petrify`, but an AddBuff can override the
visual (flags bit4), which is how the essence statue gets `frozen_in_time_0/1/2` while keeping the freeze stat.

**Data for the essences themselves.** `Essences` rows: `BaseItemTypesKey` (name and art), `HASH32` (sent in the Monolith
blob), `Level` = tier 1..7, `EssenceTypeKey`, `DropLevel`, `Monster_ModsKeys` and `Display_Monster_ModsKey`;
`EssenceType` gives the colour group (`EssenceType` 1–5 for the normal types, 6 for the corrupted Hysteria/Insanity/
Horror/Delirium, 0 for `NotEssence`), `IsCorruptedEssence`, and the words row. Tier names: Whispering 1, Muttering 2,
Weeping 3, Wailing 4, Screaming 5, Shrieking 6, Deafening 7. `Essences` row 104 is `Remnant of Corruption` (type 25,
`Level` 0, DropLevel 32) — the special Remnant essence (`CurrencyCorruptMonolith`); it carries the
`RemnantOfCorruptionMonsterBuff/Aura/Stats` mods plus an on-death fleshmonster summon
(`Display_Monster_ModsKey` → `EssenceFleshmonsterSummonedOnDeath`), so it is a monster modifier, not a plain panel line.
Row 105 is **Desolation** (`CurrencyEssenceFaridun1`, `Level` 0, DropLevel **68**, display mod `EssenceFaridunDaemon` →
`Metadata/Monsters/FaridunLeague/FaridunDaemon/EssenceDaemon_`). It sits in **colour group 6 with the corrupted four but
`IsCorruptedEssence` 0**, so a group's tier floor must be taken over its tiered rows only — counting Desolation drops group
6's minimum to 0 and leaves the transform outcome with no candidate. Both are untiered specials, not bad rows: the catalog
keeps a tier-0 row that carries a drop level, and `Pool` still excludes every tier 0, so they reach a monolith only through
their own source (the atlas Remnant chance) or the dev spawn.

**Atlas passives and scarabs (3.29, checked against the 3.30 Steam client).** The essence stats reach the module through
the map's area stats (`Game.MapAreaStats` composes the active atlas preset; `MapModsOf` reads `KeptAreaStats`). The monolith
**count** needs no module code: the `CoreLeagues` Essence row (row 6) rolls base = `map_extra_monoliths` +
`map_leaguestone_additional_monoliths`, bonus chance = `map_extra_monolith_%_chance`, first-chance add = `map_monolith_chance_%`,
max 10 — so Prolific Essence (`map_extra_monoliths` +1) and the Essence Chance small nodes (`map_extra_monolith_%_chance` +5)
feed `LeagueCount` directly. Applied in `MapEssenceCatalog.Roll` / `MapEssences.Drops`: additional-essence chance
(`map_essence_monsters_have_an_additional_essence_%_chance` +5), Crystal Lattice
(`map_essence_monsters_chance_for_3_additional_essences_%` 15), Amplified Energies (`map_essence_X_essences_at_highest_tier` 1 →
X essences raised to the highest tier their type reaches in the area pool), higher-tier chance
(`map_essences_are_1_tier_higher_chance_%`), and Essence Rare Chance
(`map_essence_monsters_drop_rare_item_with_random_essence_mod_%_chance` +25 → a rare item with a random essence's guaranteed
mod via `EssenceCrafting.Apply`). Crystal Resonance's `map_essence_remnant_convert` is read by `Corrupt`, and its second
stat `map_essence_X_mods_inherited_by_map_boss` is implemented in `EssenceBoss.cs`: each slain released rare hands X random
essences to one map boss (not imprisoned), the tick applies them to the live boss (mods, scaled max life, life recovered
to full, attack/speed, name plate, instance record) following `MapTorment.Apply`, and the boss drops the inherited essences
when slain (wiki: "if multiple map bosses, one receives the mod … the boss that has the modifier drops the corresponding
essence … if it already took damage, its life is recovered"). The boss is matched against the area's `WorldAreas`
`Bosses_MonsterVarietiesKeys` (cached per area): Mesa's Oak the Mighty (`MapBanditLeaderOak`) has `BossHealthBar` 0, so the
big-life-bar test alone never found it. The Remnant-of-Corruption chance
(`map_essence_monolith_contains_essence_of_corruption_%`) adds the tier-0 Remnant essence (`CurrencyCorruptMonolith`, kept in
the catalog but out of `Pool`) to the monolith; its `RemnantOfCorruption` monster mods apply on release, while its on-death
fleshmonster summon stays a specialised-daemon gap. **Still not applied (needs a source):** the Adaptation scarab's
`map_essence_imprisoned_monsters_enhanced_for_each_essence` (the data gives no value mod for it; the `map_imprisoned_monsters_*`
stats belong to the dead domain-12 leaguestone mods "of Burden"/"of Wealth"/"Refined").

The five essence scarabs are **identical in 3.29 and 3.30** (nothing removed): Essence Scarab (`map_has_monoliths` +
`map_extra_monoliths` 3), Ascent (`map_essences_are_1_tier_higher_chance_%` 100), Calcification
(`map_rare_monsters_trapped_in_essences` 1), Stability (`map_stable_essence_corruption` 1), Adaptation (the transfer chance 100 +
`enhanced_for_each_essence` 1). The 3.30 **atlas** cluster was reworked (61 nodes): most small nodes became
`map_additional_monolith_surpassing_chance_%`, Prolific Essence grants that chance instead of +1 monolith, Crystal Resonance
swaps `map_essence_remnant_convert` for `map_boss_drop_calcified_map_chance_per_essence_up_to_15%` (the convert moves to a new
Commensurate Corruption notable), and new notables add double corruption (`map_essences_monster_can_be_twice_corrupted`,
Recrystallisation), possessed monsters / Rogue Exiles (Supernatural Genesis) and per-type essence chance selectors. None of that
is in the 3.29 client, so the replica implements the 3.29 cluster only.

**A daemon can ride a monster mod, not only the display mod.** Three essences name their behaviour through
`Monster_ModsKeys` → `Mods.MonsterMetadata` while their display mod names no daemon at all: Anguish's
`EssenceDaemonMeteorFirestorm` (a firestorm, spell fire damage 1602–2404, 1000 ms ball delay, −90% storm area, +150% final
radius), Insanity's `SpiritMinionEssenceMonster1` → `EssenceDaemonSummonSpectralSkull` (alternate_minion 188, 3 spirits),
and Contempt's `EssenceDaemonSummonMovingKaruiSpirit1`. Reading only the display mod left Anguish and Insanity casting
nothing.

**Monster spell damage is in `FloatStats` + `BaseResolvedValues`, not `ConstantStats`/`AdditionalStats`.** A granted
effect's per-level row pairs those two arrays, and `SkillCatalog.EffectStats` merges them, so
`spell_minimum_base_<type>_damage` is present for a monster spell even though the stat-set's own constant/additional lists
hold only area, duration and delay. A dump that reads just the constant and additional stats reports these spells as
dealing nothing. Per-essence numbers found that way: Dread's `EssenceChaosstorm` 64–96 chaos, Insanity's
`EssenceLightningBeacon` 11–15 lightning with `number_of_beacons` **2** and `beacon_placement_radius` 20 (cooldown 0),
Greed's `EssenceBloodProjectileNova` 8–12 physical. Sorrow's totem effect `EssenceTotemCastVortexGroundEffect` carries
**only** `base_cold_damage_to_deal_per_minute` (223718) for 1000 ms — a ground DoT with no hit damage, so the hit kit's
"does nothing" filter drops it; Spite's totem has that plus `EssenceTotemArc`. Doubt's `EssenceSummonSpiderWithMortar`
launches its spiders as projectiles (`monster_projectile_variation` 1, minimum range 15, spread 20) with
`skill_repeat_count` **3**, and the spider's `MonsterFlickerStrike` is a **Movement** skill, which the kit filter drops.
Torment's orb summon is 2 daemons on a 1500 ms cooldown, and the orb's own `EssenceDaemonBallLightning` was dropped by the
kit's "Daemon" *name* filter. Suffering's `EssenceFrostBomb` exposure is −20% at gem level 1 and **−30%** at 3, on a 3500 ms
cooldown. Loathing's damage half is `active_skill_damage_+%_final` 50 and Scorn's defence
`base_self_critical_strike_multiplier_-%` 75; their BuffDefinitions (750/753/756) are `Invisible` with
`Id2 = GenericAlternatingBuff`. Anguish's on-death swarm is `SummonedSpecificMonstersOnDeath` rows 5/6/7 — 1st stage
(variety 423) ×**4**, 2nd (424) ×**2**, 3rd (425) ×**2**, with a 4th-stage variety 426 — selected by
`essence_anguish_spawn_monster_on_death_variation` 6.

**The client's own essence art folder** (`MiscAnimated`, `Metadata/Effects/Spells/monsters_effects/essence/`), by its row
name: `MonolithNovaEffect` (1059) `monolith_pulse/monolith_pulse.ao` — the essence pulse's own art, also reused by Legion
(3270) and Crucible (6968); `BloodOrbBuildupEffect` (1057) `bloodball/rig.ao` — Greed's red bubble building up;
`MortarSummonSpiderImpact` (1051) `spidermortar/spidermortarimpact.ao` — Doubt's spider landing; `EssenceSpawnEffect` (1056)
`monolith/essencesummon.ao` — the release; `demon_grasp/*` (1024–1026) — Envy's grab. `fire_wave_cascade/*` (1022/1107/5861)
is named `GlacialCascadeSeawitch`/`GlacialCascadeWave`, so it is Glacial Cascade's, not the essence's. The rune is an
**object**, not an effect: `object_types` 1488 `Metadata/MiscellaneousObjects/TrapFireRune` has
`animated_object = .../talisman/rune_trap/red/trap.ao`, and `SkillTrapVariations` Id 11 names that object.

**The buff essences alternate two visible halves.** BuffDefinitions 750/753/756 are the invisible *alternators*
(`Id2 = GenericAlternatingBuff`); the rows beside them are the halves, each `Invisible` but with its own `BuffVisualsKey`,
so the icon is an AddBuff visual override: Zeal 751 speed (visual 640) ↔ 752 `avoid_damage_%` (641); Loathing 754
`active_skill_damage_+%_final` (642) ↔ 755 `base_damage_taken_+%` (639); Scorn 757
`base_self_critical_strike_multiplier_-%` (643) ↔ 758 `always_crit` (644). Only one half is active at a time — Zeal and
Scorn alternate too, not just Loathing. No row carries the alternation period.

**On-death summons are one family of four stats**, each selecting a `SummonedSpecificMonstersOnDeath` row (variation − 1):
`essence_anguish_spawn_monster_on_death_variation` 6 → row 5 (1st stage ×4, then rows 6/7 ×2 each),
`essence_suffering_spawn_monster_on_death_variation` 9 → row 8 (`EssenceArcticGolemSummoned` ×1),
`essence_delirium_spawn_monster_on_death_variation` 10 → row 9 (the colossal insect ×1),
`essence_corrupt_spawn_monster_on_death_variation` → row 3 (`EssenceFleshmonsterSummonedOnDeath` ×2). Only Delirium's is
implemented. Other essence stats worth knowing: `base_number_of_essence_spirits_allowed` (Insanity's spirit cap),
`essence_buff_ground_fire_damage_to_deal_per_second` + `essence_buff_ground_fire_duration_ms` with BuffDefinitions 747
`essence_burning_ground` (Hysteria), BuffTemplates 234 `ElementalDamageTakenWhileStationaryEssence1` (buff 746) and 235
`BurningGroundWhileMovingEssence1` (buff 747) — a stationary/moving pair — and 233 `EssenceHorrorCharges1` (buff 773
`essence_random_charge`, 774 `essence_charge_max_and_duration`). BuffVisuals 762 is `essence_of_contempt`.

**The release.** An essence's release spell comes from `Essences.Display_Monster_ModsKey` → that mod's `MonsterMetadata`:
a `Metadata/Monsters/Daemon/EssenceDaemon*` variety whose granted effects are the spell (Hatred → `EssenceDaemonColdPulse`,
Anger → `EssenceDaemonFirePulse`, Wrath → `EssenceDaemonLightningPulse`, Woe → `EssenceDaemonSummonLivingCrystals`,
Greed → `EssenceDaemonBloodProjectileDaemon`, Contempt → `EssenceDaemonSummonKaruiSpirit`, Sorrow →
`EssenceDaemonTotemGroundEffectVortex`, Torment → `EssenceDaemonSummonOrbOfStormsDaemon`, Fear → `EssenceDaemonSummonGhost`,
Suffering → `EssenceDaemonFrostBomb`, Rage → `EssenceDaemonFireRuneTrap`, Doubt → `EssenceDaemonSummonSpiders`,
Spite → `EssenceDaemonTotemGroundEffectShocked`, Misery → `EssenceDaemonChaosDegenPulse`, Dread →
`EssenceDaemonSummonChaosGolem`, Envy → `EssenceDaemonGrab`; the corrupted four → `EssenceDaemonMadness1`/`Insanity1`/
`Horror1`/`Terror1_`, Desolation → `Metadata/Monsters/FaridunLeague/FaridunDaemon/EssenceDaemon_`). Anguish, Loathing,
Zeal and Scorn have an empty `MonsterMetadata` (their display mods name alternating buffs / on-death elementals, not a
daemon). `Monster_ModsKeys` are the stat mods instead: `EssenceDaemonModLifeAndDamageLevel1..7`,
`EssenceDaemonModRarityAndQuantityLevel1..7` and a physical-added-and-converted-to-cold mod — all with **empty names**, so
they do not draw on a hover panel. The essence's name on the monster (released rare, or a boss that inherited it) comes
from the `Display_Monster_ModsKey` mod itself, a name-only mod ("Whispering Essence of Hatred", "Essence of Hysteria"); it
is added to the monster's mods alongside the stat mods so the essence shows on hover. The league's terrain room is
`Metadata/Terrain/Leagues/Essence/monolith.arm` ("EssenceMonolith", 3×3).

**Contempt's two Karui skills.** The display daemon's `EssenceDaemonSummonKaruiSpirit` resolves `alternate_minion=162`
to `EssenceKaruiSpiritMaceSlam`; `EssenceDaemonSummonMovingKaruiSpirit` resolves 185 to `EssenceKaruiSpiritMovingAxe`.
The mod carrying the second, `EssenceDaemonSummonMovingKaruiSpirit1`, is in `Monster_ModsKeys` only from **tier 5 up**:
tiers 1 to 4 carry only the life/damage and rarity/quantity stat mods. Delirium's `EssenceInsectSummonedOnDeath`
(variation 10) is likewise a monster mod, separate from its display daemon's direct `EssenceDaemonSummonExplosiveInsect`.

**Dread's two effects.** `EssenceDaemonSummonChaosGolem` holds both the summon (`alternate_minion=165` →
`Metadata/Monsters/ChaosElemental/EssenceChaosElemental`, MonsterVarieties 4224, whose own effects are Melee,
`EssenceChaosCascade` and `EssenceAbyssalCry`) and the chaos Firestorm `EssenceChaosstorm` (1392): cast 900 ms, cooldown
3000 ms, `active_skill_base_area_of_effect_radius` 10 with `firestorm_base_area_of_effect_+%` −49,
`fire_storm_fireball_delay_ms` 125, duration 2000→3000 ms, `spell_maximum_action_distance_+%` −50, and the implicit
`firestorm_use_server_effects` — the server places the impacts. Its damage is the monster's base spell damage at the actor's
level through `BaseEffectiveness` 0.2667 / `IncrementalEffectiveness` 0.045, not a stat on the effect, which is why the hit
kit read it as doing nothing.

**The spells the hit kit cannot represent (`EssenceSpells.cs`).** A daemon effect with no hit damage, summon or projectile
is dropped from the rare's kit (`BossKits.AttackFor`), so these essences would cast nothing through it: Misery's
`RepeatedPulseDegen` (5996) and `EssenceDaemonProximityShield` (2842), Envy's `EssenceDaemonGrab` (2929), Dread's
`EssenceChaosstorm` (1392) and the
resistance pulses `RepeatedPulseCold`/`Fire`/`Lightning` (5990/5995/5992 — Hatred/Anger/Wrath's nova damage is the *other*
daemon effect, `EssenceArcticBreathNova` etc., which the kit keeps). These are **not** kit specials: a non-hit special in the
kit fights the auto-attack (a kit of nothing but unready specials leaves `BossKits.Choose` no chase attack, so the rare stands
still between casts), stalls the rare and desyncs its position on the client, and the rare's rig has no
`RepeatedPulse`/`DominusGrab` clip so the cast
draws no animation. Instead a periodic caster in the essence tick casts them on their own cooldown while the released rare
lives, independent of its AI (which fights normally with its default attack and the hit/summon spells); cooldowns live on the
instance (`MapEssences`' per-instance cast state) so several players' ticks cast a spell once. Each cast is instant and
self-centred. The **grab** visual is the client's own spell art spawned as a one-shot **effect object** (`demon_grasp` circle
+ hand, `Metadata/Effects/Spells/monsters_effects/essence/demon_grasp/demon_grasp_circle.ao` / `demon_grasp.ao`, MiscAnimated
1025/1024) where the target stood; after a 1 s windup the grasp end (`demon_grasp_end.ao`, 1026) plays and a target still
inside the circle is pulled to the rare — stepping out evades it. The **pulse** is the element's own
`Metadata/Effects/Spells/pulses/<element>pulse.ao`, spawned at the rare's live cell and re-spawned for each pulsation of the
wind-up: the client draws it as a one-shot effect, so the cast never stops or holds the rare's AI (an earlier daemon actor
playing the `RepeatedPulse` action could not follow a walking rare and left the pulsation behind). That action is ActiveSkill
898 `repeated_pulse` (ActionType 526 `RepeatedPulse` 0xe7db, TargetType 4); the grab is ActiveSkill 373 `dominus_grab`
(ActionType 203 `DominusGrab` 0xb975).
**No essence pulse skill holds a radius stat and ActionType 526 has no radius column**, so the client's own action code owns
the reach; 12 is calibrated from retail (the replica's aggro-range reach, 60+, was about five times too wide) and awaits RE
of the action. `MiscAnimated` carries all four pulse arts — `pulses/icepulse.ao`, `firepulse.ao`, `lightningpulse.ao`,
`chaospulse.ao` — so each element spawns its own at scale **1**; the 5x it was spawned at drew
a pulsation five times too wide, and lightning no longer borrows `ShrineShockNova`. The cast windows and cooldowns
(`GrantedEffects.CastTime` / `GrantedEffectsPerLevel.Cooldown`, ms): Misery 5000/8000, Anger 4000/4000, Wrath 4000/8000.
`RepeatedPulse` pulses **across** its cast, not once per cooldown: Misery's 2.5 s debuff over a 5 s cast is two pulses.
**The resistance nova's projectiles (`FireNova`).** The daemon first plays the `RepeatedPulse` wind-up — accelerating
pulsations (0.5 / 0.2 / 0.1 s) — then the nova bursts; the rare is rooted (`BusyUntil`) for the cast so the pulsation and the
release stay where it stands. The pulse is self-centred with no area radius stat of its own and uses the action's reach
(12) — reading the nova effect's absent radius drew a radius-1 circle nothing could see. A daemon action alone draws no
projectiles
— the ring is fired through the monster projectile runtime (the Rampage nova pattern, `MonsterProjectileUse` + team-2
`HostileProjectileOffense`, `SpreadRadians` = a full turn), from the **rare's live position** at the cast (a stale origin,
captured when the tick queued the spell, reads as the cast coming from somewhere the monster no longer is). `ProjectileFor`
returns null for all three nova effects (no `Metadata/Projectiles/<effect>` row), so the projectile is resolved by hand:
Hatred fires `Metadata/Projectiles/ArcticBreath` (Creeping Frost, speed 844), Anger `Metadata/Projectiles/FlameBreath` (the
fire arctic breath its `EssenceTriggeredFlameBreath` names, speed 844, flight `firearctic_loop` index 1 / impact
`firearctic_end`), and Wrath is a **lobbed lightning mortar** — the skill's `MonsterProjectile` row is the molten
`MortarMolten`, but its art read as fire, so the shell uses `Metadata/Projectiles/MortarLightning` (the lightning
molten-projectile rig). The row's speed 750 crossed the short landing in ~0.14 s (too fast to see), so the shell is slowed
(300) and its landing distance is 280 — the skill's own `projectile_minimum_range` is 125 and `projectile_spread_radius`
350. Each shell's impact art hooks the projectile's **Area** event (the landing), not `End` (the object's retirement, which
fired the explosion late), and plays for 1.4 s. A full-turn spread makes
the runtime land each shell its landing distance out in its own direction, a ring of explosions around the rare. Hatred's
Creeping Frost also leaves a trail of chilling-ground ice crystals (`ground_effects/ice/ice.ao`, scale 1.05) along each
projectile's radial path out to ~56 cells for the skill's 7.5 s chilling duration; a player walking over them is chilled (the
strongbox ice-nova ground pattern), refreshed while they stay on the ice.
The stats at level: resistance `base_<type>_damage_resistance_%` = -10 for 3000 ms; degen
`base_chaos_damage_to_deal_per_minute` (4989 at 83, ≈83 dps before resistance) for 2500 ms; grab cast 1000 ms, no damage. The
resistance debuff is a refreshing player stat source with `BuffDefinitions` 746 `essence_elemental_damage_taken` (its
`BuffVisualsKey` is 0 `invisible`, so the debuff is the stat alone, no icon); the chaos degen goes through
`TrapDegenPerSecond` after chaos resistance with buff 100 `monster_aura_essence_daemon_degen` (visual 102, `degeneration.dds`
/ `plague_degen.epk`); the grab teleports with the gate's `0x01b7` (`InstanceProtocol.Teleport` under `ProjectileMutation`).
The proximity shield (`EssenceDaemonProximityShield`, GE 2842) triggers with Misery's pulse as `BuffDefinitions` 227
(`proximity_shield_aura`, 5 s, radius 22). Remaining daemon/ground spells confirmed from `client.db`: Sorrow's Vortex
totems use `GroundEffects[164]` (`frost_bolt/sml_maelstrom.ao`, GE 5663 cold DoT + chill) and Spite's totem uses
`GroundEffects[47]` (`lightning_ground_01.ao`, GE 5664 shocked ground); Torment's `EssenceOrbOfStormsDaemon` carries
`SummonEffect` 460 (`lightning_strike_herald.ao`) and `EssenceDaemonBallLightning` (GE 2500, −80% speed); Insanity's
`EssenceLightningBeacon` (GE 4294) uses `storm_call/stormcall_{idle,impact}.ao` + `stormpulse.ao` (MA 296–298) and its
`SpectralSummonedSkull.ot` sets `base_cannot_be_damaged = 1`; Hysteria casts `EssenceDaemonVaalFlameblast` (MA 377/380),
`EssenceFireballBurst` (GE 31) and burning ground (`FireElementalMeteorEssence`, GE 5267); Horror casts `StormCascade`
(GE 6121, MA 302–303), Maelström chilled ground and `EssenceHorrorCharges1` (BT 233, BD 27–29); Delirium casts
`EssenceChaosProjectileWithGroundEffects` (GE 5195, piercing `OfferingOfJudgementProjectileChaos` + `caustic_02.ao`) and
summons `EssenceInsectWildExplosive` (`InsectSuicideExplosionLarge`, GE 2198); Desolation's boulder uses MA 7957/7958
(`fleshboulderEssence.ao` / `fleshboulder_explode.ao`, GE 12734/12735 with 100% poison); and Remnant of Corruption adds
`RemnantOfCorruptionMonsterAura` (BT 237, radius 30, BD 768, −30% move speed, 360%/min life + 180%/min ES physical DoT)
plus on-death `EssenceFleshmonsterSummonedOnDeath` (variation 4) alongside Suffering's `EssenceArcticGolemSummoned` (variation 9).

**How the replica stands (2026-11-20).** The monolith, its mini crystals, the three activations, the essence drops from
the released rare and the rare's periodic essence spells are implemented (`MapEssences.cs`, `EssenceCorruption.cs`,
`EssenceDaemons.cs`). The imprisoned monsters are also sent as paused, invulnerable, instance-owned monster entities at
the crystals so their models exist, created **asleep** with the client's `hide_mini_life_bar` stat
(`StatKeys.HideMiniLifeBar`, key 0x2a8b; the stat table's row 10890 is `hide_mini_life_bar`, keys are row + 1) so the
model renders without a life bar and takes no orders until the crystal breaks. The rare is hostile (the monolith's panel
shows its `ObjectMagicProperties`) with the rarity glow suppressed; the minis are the pack's own monsters, **friendly**
and carrying `virtual_is_hidden_monster` (key 0x59d0, Stat row 22991) besides `hide_mini_life_bar`, which takes the ally
bar, name and hover tooltip away. A friendly team alone still shows the hover (observed live); a monster type whose client
Targetable is false hides its model with the target, and the hideout's `HideoutMonsterStatue` task is hideout-only (its
create in a map answers `0x02fc "Buffer Underflow"`), so neither is used. The monolith carries the rare's entity id in
+0x104 and the gold panel with the name, mods, essences and helper text draws live. The encased look is the essence's own
effect: `BuffDefinitions` 1311 `frozen_in_time` with its visual overridden to `BuffVisuals` 658
`monster_essence_effect_buff` (3.30 661), never the row's default `legion_petrify`, plus one S→C `0x1b2` animation update
(kind 0, index 0, **speed 0**) that stops the actor's animation. Every spawn of the encounter (the encased previews and
the released monsters) uses `MonsterPlacement.Established`: the proposed placement's nearest-clear search pushes them
out of the crystal, because the monolith's own blocker (size 4) stays registered until the break is presented — observed
live as the monster standing offset from the monolith. The mini crystals take random bearings on a radius-10..22 ring
and keep only exact cells walkable for their size 2, clear of the area's bodies and objects and at least 6 cells from
every other crystal; a nearest-cell snap pulled them off the ring and onto each other. That combination is the closest
match found live; the remaining visual gap is the crystal model itself: the main monolith's state-1 `stage_01` animation
shows only its base ring in the replica, and the official look (a tall shard cluster and the shards around the minis) is
the open item.
