# Status: Hideouts, crafting stations and maps

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/hideouts-and-maps.md](../roadmap/hideouts-and-maps.md).

## Works (verified with the real client)

- Idols (Legacy of Phrecia): they drop in maps, the 41 unique ones too, can be gambled at Faustus, lie in the Atlas screen's side panel and add their modifiers to the maps opened.
  The 3.29.3 clients have no Idol panel, so the Idol inventory is the map device's storage grid: 4 by 5 cells, one loadout.
  Docs: [Idols](../docs/game/idols.md).
- Heist Locker is placed in each hideout and opens its native window (0x7b kind 0x37).
  The native window handler and visible state were verified through the real client.
  Its placement supports the hideout editor; storage transfers were not added by this change.
  See [21](../docs/protocol/21-hideouts.md), Heist Locker.
- Horticrafting Station (2026-10-27): placed beside the ordinary bench in every selected hideout; interaction opens
  the native recipe browser (0x7b kind 0x39).
  C→S 0x274 carries the selected HarvestCraftOptions HASH16 (captured live: ReforgeFire 0xa3e5); S→C 0x276 carries the
  crafted item id and clears the panel's pending state, so Craft can be clicked repeatedly without reopening.
  Targeted/affinity reforges, augments and modifier conversions execute on the item in HarvestCraftingItem; the exact
  Wild/Vivid/Primal/Sacred lifeforce cost is split across the main inventory and personal stash and commits with the
  item atomically.
  Refusals spend nothing; unsupported influence, exchange, divination-card, gem, corrupted-map and quality-enchant
  recipes are reported in chat and wait for their prerequisite systems.
  Reforges need an identified rare, augments a non-influenced item.
  Uniques, unmodifiable items and a recipe of the other game mode (GameMode 1 not in Ruthless, 2 Ruthless only) are
  refused, and RancourCost charges Crystallised Rancour (these rules not yet tried in the client).
  Automated tests cover raw rows/costs, every supported operation family, payment, insufficient funds and save
  rollback; station, window and the request were confirmed live.
  See [21](../docs/protocol/21-hideouts.md), Horticrafting Station.
- Generic 3.29 maps, first slice (2026-10-27): `MapKeyTierN` items serialize their MapKey component and `/item Map
  (Tier N)` creates one; the map device accepts them and uses `MapKeys.dat` for the tier. The native hideout Atlas
  window (S?C 0x7b kind 0x2b) shows the four starting layouts; a node click (C?S 0x00e7) selects one and Activate opens
  it. Every character has the epilogue quest flag `EndGameAvailable`, without which the client refuses Atlas activation
  (until Kitava earns it, only in a hideout and the maps, so the Epilogue's gates stay shut; checked offline).
  Live 2026-10-29: selecting Estuary opened Estuary; Cells is the default.
  Basic monster map drops use a local approximation.
  Atlas passive tree foundation (2026-10-30): the three saved presets are sent with their node lists and point budget
  (S→C 0x3d); C→S 0x3e/0x3f allocate/refund nodes and save them; `/atlas_tree` opens the tree and
  `/atlas_points <n>` sets the dev budget. Live: with 30 points, three "Map Drop Chance" nodes allocated and the
  client updated the tree; the presets are saved. The budget now comes from the Atlas's completed maps: one point per
  map first completed, up to 100 (#382), derived at login/area entry from the saved completed set and awarded on a real
  completion. A fresh save earns its first map point on its first completion. The server
  holds the Atlas graph (client.db `passive_graph*`, graph `atlas`) and refuses an allocation off the graph, of an
  icon-only node (the masteries), or not touching the start or an allocated node, and a refund that cuts allocated nodes
  off from the start (`AtlasPassives.CanAllocate/CanRefund`; not yet tried live). A save's old nodes are kept
  as they are. The active preset is saved (module data `atlas.active`), picked with a tab (C→S 0x2a8) and sent last
  so the client ends on it; the lists are sorted; the budget adds node-granted points (Unwavering Vision). A refund
  spends one respec point (`atlas.respec`, starting at 0) and an Orb of Unmaking (Kirac's shop) grants one
  ([15](../docs/protocol/15-progression.md) §2; not yet tried live). Trees II and III unlock at 50 and 100 completed
  maps (the stand-in for bonus objectives) through league flags 329-332, per character; a locked tab is refused. A map's area stats take the active preset's
  nodes (HardMode vectors in a HardMode league) composed by the tree's own rules (`AtlasStats`: Wandering Path, Grand
  Design, Unfettered Concentration, mod effect per mod and per fragment), with rarity-gated packs and Meticulous
  Appraiser resolved in the list; the vital/deadly keystones' monster life and damage, the map's card and scarab
  drop factors and the scarab-type weights (ScarabTypes: the Carapaces' `MoreLikely`, `DisableDrops`) act ([maps](../docs/game/maps.md); offline only).
  The selected layout is kept in the character save (an AtlasNode Id, `CharacterSaves.SelectedAtlasLayout`, chosen with
  `/atlas <layout>`), and a restarted Gateway reads it back instead of resetting to the starting node (#382). Generic
  maps use their tier as the area level (tier + 67). Layouts marked for a specific item require the named map.
  Atlas progress (2026-11-01): killing a map's WorldAreas boss (or any unique) in it completes its AtlasNode, saved per
  character; S→C 0x1a4 and one chat line mark it the first time only. Area entry sends one 0x1a3 (both masks still include all on-Atlas rows) with the 0x3d
  presets, before 0x21, so login does not announce every saved map. A completion re-sends that 0x1a3 right after the
  0x1a4, so both panels (the Atlas and the map device's portals) draw the new mark without an area change.
  `/atlas_complete <map | all>` and `/atlas_reset` (dev).
  Live: 0x1a4 for Chateau showed it completed and opened Springs and Arachnid Nest; a native Beach boss kill also sent
  the completion and refreshed the Atlas state. Bonus objectives, Voidstones, map stash placement and official drop pools remain open.
  Code: `AtlasCatalog.cs`, `AtlasPassives.cs`, `AtlasProgress.cs`, `MapDevice.cs`, `ItemDrops.cs`, `InstanceItems.cs`.
  Docs: [21](../docs/protocol/21-hideouts.md), [re/013](../docs/re/013-atlas-progress.md), [maps](../docs/game/maps.md), [05](../docs/protocol/05-inventories-and-ui.md).
- Portal Scroll and the Portal skill (its 2.5 s cast): outside towns and hideouts a portal opens beside the player,
  replacing the previous one; from a map it leads to the hideout, anywhere else to the last town.
  That town or hideout gets a portal back, which brings the player to the portal's cell of the same instance;
  the town-side position uses the fixed layout anchor, independently of the latest arrival entrance (automated check);
  the town side checks that Auth still has the destination instance; there the portal stands again (`OpenTownPortal`, `SpawnTownPortals`, `OpenPortals`;
  a map's portal gets no portal back: an entry through that map's device portals arrives at it, also after a death, and it
  replaces the map's own portal back there, #1002;
  while the server runs, being tested 2026-10-23).
  Code: Gateway (`UsePortalScroll`, `UseMapReturnPortal`).
  Docs: [21](../docs/protocol/21-hideouts.md) §5.
- Hideouts: every character has the hideout unlocked (quest flags HideoutUnlocked, CanEditHideout …;
  league quest flags UnlockedHideouts, HaveHideout, UnlockedMapDevice in the Player component, sent as bit id − 1) and
  owns the Lush Hideout; `/hideout` (C→S 0x16 command 45) and the World panel's Hideout button (C→S 0x17a) take the
  player there (its own instance, seed 1) next to its waypoint;
  its Waypoint (with HideoutDoodad data), Stash, Nessa and map device come from its decoration (`scripts/re/atlas
  hideouts` → `data/hideouts.json`); the HUD shows the Edit button;
  the map device can be clicked and opens its window (S→C 0x7b kind 5);
  maps go into its slots (dragged, or Ctrl + clicked: C→S 0x00e4 in and 0x00e5 out, live 2026-11-15), Activate
  (C→S 0x00d8) checks them (maps and map fragments only, one map), consumes them and
  opens six portals (MultiplexPortal + S→C 0x008f) into one instance of the map's area, each used once (the one taken
  stays gone); a click on a run whose instance has expired (six minutes after its last player left) closes the run and
  its remaining portals and says so in chat, instead of spending a portal on a transfer that cannot arrive (2026-11-18);
  guests share that portal pool, map rules and return destination, and see the owner's saved furniture.
  Visiting another hideout preserves the visitor's own home; only the owner activates its device or edits its furniture.
  The instance keeps the map's level and mods for its whole life (after its last portal or another
  activation, and for guests); 0x1d sends the map's area stats (its mods, tier and atlas) and the Gateway generates
  the level and applies the mods with them, the tier's more monster life and damage included;
  `An Audience With The King` uses its native recipe to open the Crux of Nothingness by itself,
  rejecting extra inputs before consumption (verified in the 3.29.3d client with both inputs preserved); in the map, a
  portal back to the hideout, the map's own monster packs (Maps.dat) and its boss arena
  gates (teleports); Nessa sells Beach Maps; `/league_flag`, `/hideout_keys` (dev, answered in chat).
  A run whose device instance is gone (a hideout's instance expires six minutes after its last player leaves) moves to
  the instance the character enters, so its portals left stand at the new device (2026-11-18).
  A click on a portal back into a hideout or town whose instance expired enters a new one of that area, by its
  device, instead of a transfer that was refused (2026-11-18).
  "Resurrect at Checkpoint" from a map whose hideout instance is gone enters a new instance of it by its device, not
  a refused transfer that left the dead character in the map (2026-11-18).
  Code: `HideoutCatalog.cs`, `InstanceHideouts.cs`, `WorldAreaHideouts.cs`, Gateway (`WarpToHideout`,
  `SpawnHideoutDoodads`, `UseMapDevice`, `ActivateMapDevice`, `UseMapPortal`), `MapDevice.cs`.
  Docs: [21](../docs/protocol/21-hideouts.md).
- Map device portal labels (2026-11-14, live): a portal's floating label names its area and appends "(Completed)" once
  that run's map boss is dead (S→C 0x008f flags bit 1, which the Portal component's nametag reads). The mark follows the
  exact shared map run across area changes and guest kills. Every declared terminal boss must die; unrelated uniques
  do not count. A new run of the same map starts unmarked, and existing visitors receive updated labels.
  Live in a Chinese client: after the boss died the standing portals read "<area name> (已完成)", and the logged
  message began `008F02`. See [21](../docs/protocol/21-hideouts.md) §5.

## Partly working

- Map and Atlas modifiers govern scarab drops, monster combat and player recovery, skill area, defences, accuracy and aura effect.

- Named scarabs: Domination/Cloister, Ambush boxes, Essence release/Ascent/Calcification/Stability and Terrors bosses;
  Essence: corruption/transfers, Toughness, enrage, Decay, Envy, Karui and death insects; other skills/scaling partial (#505).
- Tormented Spirits in maps and the campaign: touch, possession, right-click Possession cancellation (`C→S 0x004b`),
  rewards, the Torment scarabs, the Atlas tree's stats and the Idols' once they reach maps (#1091) (name plate update:
  [14](../docs/protocol/14-monster-packs.md) §5).
  Not yet: the spirits' and Grips' own skills, possessed Rogue Exiles, scarab weights, Refiner's Bargain (#1129).
- Beyond in maps (3.29 Scourge rework, #1156): kill-triggered blood orbs (`BeyondPortal1`) merge into pack
  (`BeyondPortal2`) and boss (`BeyondPortal3`) portals for the rolled `BeyondFactions` faction (Demon, Flesh, Pale),
  spawning packs and bosses (K'Tash, Ghorr, Beidat) and shutting down further portals once a boss spawns.
  Tainted currency drops, all five Beyond scarabs (Resurgence, Invasion, Haemophilia, Corruption, base), Atlas passives
  (Endless Tide, Demonic Power map-instance boss buffs, faction notables, Torn Veil, Fiendish Opulence) and Idols read from
  `Game.MapModsOf`.
  Not yet: specialised Beyond boss spell kits and Ghorr's Debilitate on hit.

- Harbingers in maps (#1157): CoreLeagues spawning, invulnerable avatars with repeat-wave summoning
  (`skill_repeat_count`) and permyriad life loss on wave kills, Harbinger/King-helper/Empowered-Obelisk skills and
  auras (mortars, ball lightning, cold beam, warp flame, minion buffs, curse auras, obelisk damage-reduction and haste
  auras), King Harbinger bosses with helper avatars, Empowered Obelisks, the 11 active 3.29 currency shards
  (full-stack conversion and full-orb upgrades), all four Harbinger scarabs, and composed Atlas/Idol map stats.
  Not yet: the sky beam, flameblast and upheaval land once instead of as their trigger cascade, the ball lightning
  hits once instead of zapping as it flies, The Beachhead unique map portal encounter and Harbinger unique piece
  assembly.

- Sacred Grove (Harvest): the map rolls it from the composed area stats (Atlas, scarabs, sextants, Idols), on `.dgr`
  and `.tgr` maps; its `link` rooms are a gate each way inside the map instance where the league runs Harvest, and map
  packs stay out of the grove.
  The Harvest module fills 3-4 pairs of crops (Extractor, Irrigator, Pole, Storage Tank, 23 seeds by colour and tier);
  one click hatches the seed monsters and withers the other crop of the pair; they drop Wild/Vivid/Primal lifeforce.
  The `atlas_harvest_*` passives (including Crop Rotation via `atlas_keystone_crop_rotation`: all Tier 1 on spawn and
  per-seed tier upgrades on idle crops of other colours at crop completion),
  the three Harvest scarabs and the Idols' same stats apply, all read from
  `Game.MapModsOf`, except Nature's Favour (lucky Horticrafting outcomes), the Harvest-scarab weighting
  (`map_harvest_scarab_more_likely_%`) and Refiner's Bargain's crops (its monsters drop no lifeforce and nothing else).
  The memory-line grove (`HarvestLeagueMemoryLine`, or its room in a map) has five single crops of 45-47 mixed-colour
  seeds; Corpse-Grown (Faridun) seeds replace Tier 3 seeds wherever `map_harvest_%_chance_for_faridun_seed` is granted.
  Tier 4 bosses drop the Sacred Blossom, which opens `HarvestLeagueBoss`: the soul tree starts Oshabi's four colour
  phases (Sacred first) and their adds, the Root Wall cage held by the walls' bodies (sent unblocked); her death sets
  `HarvestOshabiDefeated`, opens the Groveseed Altar (Oshabi Bloodline) and drops her world.db rewards. The opt-in Legacy module `HarvestHeartOfTheGrove` (3.13) rarely puts her in a map's grove.
  Live 2026-11-18 (tier 16 Cage): the gate labelled "The Sacred Grove" leads in and back; the grove needs the client's
  HarvestRules ruleset (S→C 0x1f), without which an Extractor crashes the client once targetable; a Collector shows its
  seed list, one click hatched 23 Vivid monsters and withered the pair, and their lifeforce reached the Horticrafting
  Station. Oshabi's fight ran in the client from the soul tree to the altar (four colours, adds, rewards, Bloodline
  selector). Not compared with a retail capture: Oshabi's state sequence (soul tree progression, interactable, emerge, energy and colour stages, death; her emerge,
  travelling and repeat_fight states).

- Simulacrum: five arenas and a 15-wave controller; opening and the wave-start prompt checked in the client, wave
  combat and the reward display not yet. Starting a wave destroys every item on the ground, as the client warns.
  Its arenas use their encounter rules independently of the owner's Atlas preset.
  Scheduled bosses spawn before a scaled wave can award its rewards.

- **Waypoint arrival.** Hideout entry uses the exact saved waypoint cell for spawn and movement origin,
  including login and returns from other areas.
  Live player and waypoint records used the same saved cell.

- Hideout editing moves and rotates installed furniture, the waypoint and NPCs through native C→S 0x5b.
  Selection, placements and Monster Statues persist per account/template; legacy homes migrate from the first
  character with a selected hideout (an explicit migration source can be configured). Cross-character client verification pending.
  Runtime interactions and path blockers follow the moved objects.
  Every template of the selected hideout is editable: one without a captured walk map leaves cell checks to the client.
  Furniture moves send 0x161 to rebuild native hideout model collision without changing object ids.
  Server blockers are registered again at saved positions on entry.
  NPCs and all functional furniture share inherited OT collision footprints and interaction clearance.
  Arrival correction runs after the full scene is registered; waypoints and ground loot remain passable.
  Live overlap escape and released-floor checks passed for all nine functional hideout objects.
  A player overlapping terrain or stationary objects is moved to a nearby clear cell after edits and during ticks.
  The map-device underfoot escape and walking through its released old position were verified in the client.
  Extracted AO hull radii cover small and large map devices; persistence and clearance have automated coverage.
  Map-device, Nessa and stash movement/rotation and reconnect persistence were verified in the real client.
  Adding/removing arbitrary decorations and changing their appearance remain unimplemented; the dev `/statue`
  command places a saved stone Monster Statue of a chosen MonsterVarieties row (2026-11-01, live client verified:
  no life bar or name, idle animation, survives leaving and re-entering). See
  [21](../docs/protocol/21-hideouts.md), Monster Statue.
  See [21](../docs/protocol/21-hideouts.md), Moving installed furniture and NPCs.
- Ordinary crafting bench (2026-10-25, automated coverage; live client verification pending): all 765 active recipes
  and their 130 recipe flags are available.
  Exact modifiers, class masks, socket/link recipes, flask enchantments, replacement/removal and rare-affix rerolls
  are served.
  Payment combines main inventory and personal stash, including multiple tabs/currencies, and commits with the item
  atomically.
  Replacing an existing crafted mod (the Craft button relabelled "Replace", an ordinary request) costs only the
  recipe; with the multimod only the conflicting craft is replaced.
  Ctrl + click (C→S 0xe9) moves an inventory item onto the empty bench slot and the bench's item back.
  Tests cover each recipe/class, replacement with and without the multimod, and failed-payment/save cases.
  See [21](../docs/protocol/21-hideouts.md), Ordinary bench recipes and payment.
- Bench affordability correction: native bench counters exclude ordinary stash inventories.
  A separate, nonspendable currency-count view now publishes current personal stash totals on opening and refreshes
  after crafting, while actual main-inventory currency is counted once.
  Payment still uses only real items; large totals retain their full value.
  Automated checks pass; corrected live UI verification is pending.
  See [21](../docs/protocol/21-hideouts.md), Bench affordability.
- Helena placement and hideout picker entry point (2026-10-25, awaiting client confirmation): Helena stands
  screen-right of Niko and Select Hideout opens the native window.
  All exported personal templates are owned; selection, transfer and persistence are implemented.
  See [21](../docs/protocol/21-hideouts.md).
- Hideout picker crash fix (2026-10-25, awaiting client confirmation): initialize the campaign unlock bitset with S→C
  0x1f6 on area entry, before Helena can open the picker.
  Missing data caused the confirmed client access violation at 0x142174267.
  See [21](../docs/protocol/21-hideouts.md).
- **Niko and Kirac in the hideout (verified live, 2026-10-21):** Niko to Nessa's right, Kirac to her left.
  Faustus uses his native hideout NPC alongside them in every template, with the existing dialogue and saved editor placement.
  Kirac's hideout row has no dialogue of its own and uses the main Kirac's (`NpcCatalog.TalkSource`);
  Purchase Items sells maps. Kirac marks a conversation heard when chosen (NPCTalk column 15 league flags), which opens his next ones.
  Niko's Delve options need quest flag ThisIsMyHideout (sent in hideouts and the Mine Encampment) and league quest
  flag 68 DelveEncounterComplete (every character): NPCTalk columns 15-18 are league quest flag ids.
  See [21](../docs/protocol/21-hideouts.md).
- **Delve, first steps (verified live, 2026-10-21):** View Subterranean Chart opens the chart (S→C 0x7b kind 0x1c,
  from the client's screen table); its data request C→S 0x23f is answered with S→C 0x240, and one entry draws the
  first mine section (the other fields are being worked out with `/delve_chart`).
  Visit Mine Encampment goes to `Delve_Hub` (generated by the server level generator via `server_level_areas`), which
  has its tile's waypoint (still drawn inactive: no 0x4c bit), `DelveMinerHub` Niko (with `DelveOwner` and clean
  `NpcClose` transitions from Act 4), the stash and the guild stash; reward trips award Azurite and progress `delve2`.
  The World panel's special pins are all shown (league quest flags for every character, the Labyrinth's waypoint
  always activated). The Hideout, Menagerie, Mine Encampment, Forbidden Sanctum, Kingsmarch, Rogue Harbour and
  Monastery of the Keepers pins lead to their hubs, which have a clickable waypoint back, a stash, a guild stash and
  their NPCs (Einhar, Divinia, the Kingsmarch villagers, the Heist crew, Ailith; Einhar also in every hideout).
  The hub waypoints stay unlit (client area-type rule).
  The upper hub entrance now reaches an initial Delve_Main mine section and its return gate leads back to the hub.
  Native entry, ordinary walking and return were verified; see the initial terrain block below.
  Account checkpoint progression is implemented; physical terrain and encounter limitations are described below.
  See [16](../docs/protocol/16-waypoints.md) §4.
  See [21](../docs/protocol/21-hideouts.md).
- **The Sovereign (Allflame leagues):** after Valerie's introduction on the Deepwater Coast (`NPCTalk` 3932 sets league
  flag 1834 `DeepwaterBoatUnlocked`), its rowboat, the Deepwater World-panel pin and `/boat` reach The Sovereign.
  The hub is generated, with its waypoint, `Boat_AreaTransition_Animate` deck ↔ below-deck stairs (serialized with
  trailing `StateMachine` so `no_highlight` applies), deck and below-deck stashes and guild stashes, below-deck crafting
  bench, dynamic Valerie variants (789–794) on `ValeriePlacerOne/Two/Three` and below deck, `CorpseBattery` and
  `VesperCrafting` (795) ([08](../docs/protocol/08-area-transitions.md) §2, [16](../docs/protocol/16-waypoints.md) §4,
  [17](../docs/protocol/17-npcs.md)).
  Charts (rarity, affixes, level, room including Kishara's Rest `#29` at area level ≥ 81), the Chart Locker, Valerie's
  chart device and Voyage planner, and the deck's Bathysphere start Chart and Voyage runs whose grid, level and stats
  are the chart's or the board's, with the Maiden Voyage, Spirit Flame and Turning the Tide trackers (Quests 209–211,
  [03](../docs/protocol/03-area-and-world.md) §8, [05](../docs/protocol/05-inventories-and-ui.md) §3,
  [12](../docs/protocol/12-quests.md) §5, [13](../docs/protocol/13-items.md) §2).
  Guesses (G in the code): the chart inventories are per character while the tutorial's league flags are per account
  and league; the deck's map owner; the drop, room, implicit and border pools; when charts are consumed.
  Chart and border stats apply per cell on Charts and Voyages: pack size, Sulphur, experience, magic extra mods,
  combat affixes, Soul Eater, free lanterns, Strongboxes, Essence monoliths, Torment cages, Izaro altars, Treasure
  Anchors, minibosses, rare-monster drops and post-roll drop conversions.
  Not applied: Wildwood Wisps, Pantheon rare mods, rare fracture-on-death, Friendly Jellyfish AI, wealthy barrel
  clusters, highly fishy, and the Active Voyage window (0x72).
  A new instance of an encounter area (`/goto DeepwaterEncounter`, `/goto DeepwaterEncounterVoyage`) gets a random grid
  of the Sandy, Coral Forest and Coral Reef rooms (one room, or a 3 × 3 Voyage) with The Sovereign in it, sent in
  0x1d's extra data, and ruleset 171, which the client keeps (verified live, 3.29.3d, 2026-11-18;
  [03](../docs/protocol/03-area-and-world.md) §8).
  The player arrives in the bottom-left chart (cell 0's `default` point; the board's layout and orientation are from
  the client's code).
  Their level generation is in the [world status](world.md) ("Level generator port").
  The Chart and Voyage runs (lanterns placed where the client's snap puts them, server-side Auto-Lanterns via league
  flag 1841, drowning, the league's packs, Sulphur and its posed corpses, chests, collapse, capsules with a Voyage's
  entry capsule, the Bathysphere's staged descent, extraction to the deck's CollectionChest, Dredge) are in
  `Modules/Core/Content/Deepwater`, with its guesses marked G there ([03](../docs/protocol/03-area-and-world.md) §8,
  [12](../docs/protocol/12-quests.md) §5).
  Verified live (3.29.3d, 2026-11-19): a Chart's six lanterns, its collapse and ride up, a Voyage's Bathysphere called
  from a capsule and its three-stage descent, the haul on the deck and in the inventory on The Sovereign after a relog,
  the corpses without a GEAL error.
  Picked-up Dredge items carry the client's contraband item flag (header flags 0x0006,
  [13](../docs/protocol/13-items.md) §2), so they can be dragged (C→S 0x2a) or Ctrl+clicked (C→S 0x0112) into and out
  of a capsule's grid (inventory 113) and only there besides the main inventory; Send to Surface moves them, unmarked,
  to the deck's collection.
  A Voyage's start Bathysphere is sent `landed` 0 once one is called, so it stays unusable.
  Verified live 2026-11-19: the drag, the Ctrl+click both ways, the client refusing an unmarked item, Send to Surface
  from a cell capsule (the entry one refuses), the unlabelled start Bathysphere.
  An encounter area also gets ruleset 172, before 0x21, so the deck's Loot window (0x7b kind 0x6f) shows the
  collection; its Ctrl+click (C→S 0x0113) and Take All (ruleset 172 op 9) move it to the main inventory (verified live
  2026-11-19).
  What is still in a capsule's grid at a ride up goes, unmarked, to the collection or the inventory (G).
  Dead Man's Sulphur counts and pays across the Chart Locker, carried bags and personal stash.
  Vesper's Allflame Crafting (`0x7b` kind `0x69`, C→S `0x010f`, ruleset 172 op 2), all 11 crafting Ducats,
  Intangibility and the four `GhostflameCraftingOutput` display-case vats with hover mod-diff and relog persistence
  work ([03](../docs/protocol/03-area-and-world.md) §8, [05](../docs/protocol/05-inventories-and-ui.md) §3).
  Cursed Ducats (inventories 115/116, C→S `0x0115`, area stats on Charts and Voyages, all six drops including Velka's
  `Ducat of Tsoatha's Gift` and Valerie's reclaimable "TAKE ALL" offer via C→S `0x008b`) and Messages in a Bottle
  (`0x0058` → `Cursor1` + `0x7b` kind `0x6d`) work ([05](../docs/protocol/05-inventories-and-ui.md) §3).
  Verified live 2026-11-19: Chaos and Changeling's Ducat crafts, vat crack/claim and relog reconstruction, Cursed
  Ducat socketing and reclaim, and Message in a Bottle reveal.
  Ship upgrades (`DeepwaterUpgrades` rows 0–4: `+1` lantern, Sulphur scaling, extra scattered objects, final-lantern
  Adrenaline, all five upgrade pieces with Vesper turn-ins `#3920–3924` unlocking copy-cap tiers and
  `GhostflameCraftingBenchProps_Stage1..6`), Act 1 Deepwater Fairgraves (`1_1_9` with `FlameBarrier1` and
  `AllFlameLanternDeepwater`), and Kishara's Rest (`#29` barrier → `#30` imbued level ≥ 83 boss arena with two-phase
  Cressie and Velka, guaranteed unique + 8% Coursing Current Support + first-kill quest drops, and
  `VelkaDescensionObject` opening Velka Bloodline `Descendancy#15`) work
  ([03](../docs/protocol/03-area-and-world.md) §8, [12](../docs/protocol/12-quests.md) §5,
  [17](../docs/protocol/17-npcs.md)).
  Verified live (3.29.3d, 2026-11-20): Sovereign stairs with no white hover glow, below-deck stashes and crafting
  bench, `LQF 1834` boat unlock, Ship Graveyard Fairgraves and Vesper awakening, all five upgrade turn-ins with
  `Stage2..6` props, Auto-Lanterns (`LQF 1841`) and final-lantern Adrenaline, Kishara's Rest `#29 → #30` with Cressie
  and Velka, Velka Bloodline (`Descendancy#15`) reload preserving Dredge loot and extraction, Vesper's revive scene,
  rotated charts, free-lantern cells, Strongboxes, Essence monoliths, Torment cages, Izaro altars, Treasure Anchors,
  and drop conversions (Stacked Decks, Gold, Corrupted).
  Deferred crafting orbs: Resonators, Veiled, Conflict/Dominance, Tempering/Tailoring, Memory orbs.
- **Allflame encounters in ordinary areas:** an Allflame character meets the Coral Nest, Cursed Treasure and Brinerot
  Plunder rooms the level generator places: their guards (golems and crabs, Drowned Crawlers, Brinerots), a chest that
  opens once they are dead for one Chart and ordinary chest loot, Ducats from Brinerot guards, and the opener's
  Craiceann's Call, Possession or Sailor's Lantern (`Modules/Core/Content/Deepwater`, `DeepwaterEncounters.cs`,
  `DeepwaterBuffs.cs`).
  Verified live 2026-11-19 in 1_2_1 and 1_1_4_1; a Standard character gets none.
  The trigger distance, guard counts, Ducat and buff chances are guesses in world.db (`deepwater_encounter_*`,
  `deepwater_*_chance`).
  Charts drop only from these chests ("The Sovereign" for their rolls).
  Possession can be cancelled by right-clicking its buff icon (`C→S 0x004b`, verified live 2026-11-20).
  Not built: Possession monster touches outside torment maps; the Drowned Crawlers' possessed rares; the Brinerot
  barrels' own monsters and `free` state (plain Brinerots stand in); dungeon areas such as The Submerged Passage, where
  the port rolls the room but does not place it.
- **Delve sulphite and the Crawler (verified live, 2026-10-29).**
  - The character's Voltaxic Sulphite is saved, sent on entering the Mine Encampment or a Delve area (S→C 0x228), and
    shown by the Subterranean Chart's gauge. `/sulphite <n>` sets it.
    Sulphite scarabs guarantee three shared, single-use map deposits with Niko. Natural encounters roll the area's
    composed Delve chance using the map's seed. Native area-level rewards scale with
    map quantity and the owner's legacy scarab bonus, cap at equipment capacity, and survive reentry/module reload.
    Fumes adds mine guards and a native enrage visual with +50% item quantity on affected monsters; its radius,
    guard placement and exact official combat tuning remain server approximations.
    Entry sends a full snapshot without a zero-gain event; chart opening refreshes its gauge with 0x226.
    Two unchanged-value updates settle the paused chart and invalidate its label position; the number follows the fill edge.
    A third update suppresses the native invalidation. Both the fill and label were verified without client changes.
  - The native HUD gauge is permanent in mine areas (type 27). Niko maps now keep its transient display visible
    with resource refreshes at the real saved balance, without gain popups. Native checks confirmed visibility
    across waiting and a deposit credit up to capacity. Wild Niko follows terrain height instead of a fixed Z.
    The encampment (type 15) still shows sulphite in its chart.
  - `/delve_crawler [x y degrees]` creates the stationary Crawler in the encampment or initial mine section,
    or moves and turns it.
    While parked it uses the native DelveMapViewer interaction type with the Crawler model and robot map icon,
    highlights on hover and opens the chart on a real mouse click. Moving carts use DelveRobot with native turning
    wheels; separate stationary chart pads carry the circular start/destination markers and original floor model.
    Repeated creation is idempotent; client/server blockers follow relocation and chart use checks walk-up range.
    Hub wheel meshes and the powered torch use the original body bones; explicit looping idle runs the engine.
    The user confirmed the engine animation after restarting the client.
  - The Voltaxic Generator console and its cables occupy their native hub-room positions.
    Walking up and clicking opens the native Delve upgrades tab with resources, upgrade prices and purchase buttons.
    Its shared window requires permanent achievement set 2 in AreaInstance; an empty list hides all its tabs.
    Azurite, upgrade levels and flare/dynamite counts persist per account/league with atomic costs, caps and rollback.
    Server transactions have automated coverage; live purchases remain unverified because the current balance is zero.
    Equipment stats are sent in Delve areas; darkness damage and consumable use remain unimplemented.
  - Delve chart routes now persist completed points/edges and the last successful checkpoint per account/league.
    The entrance layout is used only for the first trip; later edges connect two mine checkpoint rooms in the selected
    direction. Later trips generate fresh native Mines windows with new seeds, varied rooms and authored rail shapes;
    account history rejects repeated geometry/routes and prevents minimap reuse. Other biomes remain unfinished. Successful destinations expand
    chart blocks down and sideways. A complete neighbour halo preserves the native five-cell fog fade across block corners.
    Each confirmation reaches its selected node, then the Crawler parks.
    Intermediate corridor cells are part of that journey and never become separate checkpoint stops.
    Arrival starts three defence waves over five unpaused seconds, using the route's monster pool. Only after every
    wave has spawned and all destination defenders are dead does the point save and refresh the chart.
    Tunnel stragglers do not block completion; the next direction requires a new selection.
    Death, leaving or module reload discards the unfinished trip, retains spent sulphite and restores the saved point.
    The Crawler follows authored rails, waits at 70 cells (resumes below 60), and speeds up along the rails when led.
    Departure uses the visible native circle boundary, with no travel debit while waiting.
    Completion keeps a permanent endpoint light and plays the native checkpoint activation animation with its success sound.
    The Crawler's own forward axis leads each rail segment; rounded cells do not change its heading, and only clear
    sleeve connections are smoothed; each generated route stays on player-walkable cells. Lamps cover the exact start and finish.
    Wheels, engine, torch, checkpoint pads, 30-cell lamps and cables remain in place.
    Supported Weta/Spider packs
    scale with depth (up to 100 per authored pack); levels rise from 34 at depth 1 to 86 at depth 150, then stay 86.
    Kill rewards and 15–20 base completion items gain depth IIQ/IIR; the existing configurable item cap remains.
    Newly opened destinations receive one completion reward; revisiting opened points cannot farm free rewards.
    Native walking/Kinetic Blast completed a route with exact 24 sulphite debit and one reward. Native checks also
    confirmed cross-character checkpoint sharing and unfinished-trip death reset.
    DelveOwner enables chart continuity; Niko and Crawler center it on the last saved account checkpoint.
    Mine windows follow chart-cell origins so the underground position arrow follows the occupied checkpoint.
    General biome/encounter parity, darkness damage and consumable use remain partial.
  - The Crawler stands on the rails by the mine entrance at (408, 310), facing 90°, on every entry; the stash and the
    guild stash are at (355, 295) and (344, 295), facing 180°. Lioneye's Watch has them at (327, 270) and (317, 270).
    The Sarn Encampment has them at (295, 423) and (285, 418), facing 28° (2026-11-04).
    The user placed all of these live (2026-10-30).
  - `/stash_place` and `/guild_stash_place <x> <y> <degrees>` move the stash and the guild stash of any area until the
    player leaves it, to find their places.
  - See [21](../docs/protocol/21-hideouts.md) "Delve".
- **Hideout crafting bench placement (2026-10-25).** Personal hideout selection now handles the native 0x254 request,
  saves the selected WorldAreas Id per character and transfers immediately.
  Auth uses the saved choice on login; world-panel travel, `/hideout` and map return portals use the same home.
  Functional furniture and all four NPCs are placed on the selected template's floor.
  Selection decoding, persistence and placement have automated coverage;
  the new transfer and reconnect path awaits user verification in the client.
- The standard bench spawns to the left of the guild stash in hideouts, using the existing decoration's model and a
  walkable placement (placement confirmed in the real client).
  Clicking it now walks into interaction range and sends 0x7b kind 0x33, type 0 to open the ordinary crafting window.
  Automated checks cover the wire format; a live window-opening check is pending.
  Recipe unlocks and applying crafts are now implemented; see protocol/21-hideouts.md.

## Not working yet

- Hideouts: adding/removing decorative trees/tents and changing decoration appearance are not implemented.
  Personal template selection, saved home login and functional furniture placement are implemented
  ([21](../docs/protocol/21-hideouts.md)).
- Map device: a map without a walk-map capture puts the player at an arbitrary cell (Beach is captured);
  such a map now gets a random seed instead of seed 1, which is the seed captures are dumped at — pinning it there handed
  the client a layout it could not build and it exited (`MapWorldsColosseum`: "no suitable room found … cage_turn");
  the arena gates land next to their pair, not on the telepads;
  the portals look like player portals; the open map is lost on a Gateway restart
  ([21](../docs/protocol/21-hideouts.md) §5, "Open").
