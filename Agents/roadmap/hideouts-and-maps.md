# Roadmap: Hideouts, crafting stations and maps

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/hideouts-and-maps.md](../status/hideouts-and-maps.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 1. Quality of life

Harvest, what is left: Refiner's Bargain's crops, Nature's Favour's lucky crafts and the Harvest-scarab weighting; the
client questions (the Extractor/Irrigator state sequence after a crop starts, where seeds sit and what clears a hatched seed's plant; the crop label's button sends C→S 0x0275 with the Irrigator's id, which has no handler), and Oshabi's soul tree and boss state sequence captured live (the order of the three colours after
Sacred, random here, and whether a Root Wall's TriggerableBlockage is sent blocked); AtlasNode 158 `OshabiBoss`'s
completion (`HarvestLeagueBoss` has no `Bosses_MonsterVarietiesKeys`, so AtlasProgress never completes it); opening
the Atlas RandomContent nodes (MapDeviceRecipes 74-89, whose RecipeItems hold the placeholder 3920) to reach the
Overgrown Grove; a 3.29 source for `map_harvest_%_chance_for_faridun_seed` (Mirage areas); Oshabi's Sacred Suffering
ground and vine teleports; the recipes for influences, exchanges, divination cards, gem lenses, corrupted maps and
enchants.

### Switching hideout templates

- **Known:** Helena now stands to Niko's screen-right and opens HideoutSelectionWindow (0x7b kind 0x22).
  The owned list contains the default Lush Hideout.
  [21](../docs/protocol/21-hideouts.md).
- **Start:** map the selection request, persist the chosen template, and use it for /hideout, player keys and portals.

The ordinary crafting bench's active recipes, unlocks and inventory/personal-stash payment are implemented (Status and
protocol/21-hideouts.md).
Disabled legacy recipes, map-device recipes and other specialised crafting stations are separate from the ordinary
bench.

## 2. The world

### Maps and the Atlas (3.29)

- **Known:** the 3.29 rules and the 20 generic map bases (`MapKeyTier1` … `MapKeyTier16` and four special ones) are in
  [game/maps.md](../docs/game/maps.md).
  Generic `MapKeyTierN` items, `MapKeys.dat` tiers, the MapKey component, the default Atlas layout and basic map drops
  are implemented; see Status. The native Atlas panel/progress and the map stash are not.
- **Start:** Atlas saved progress (completed nodes, adjacent unlocks, point awards);
  Voidstones; the map stash tab's layout (18 A5); the official drop pools.
- **Done when:** maps drop, are stored and run the 3.29 way, and the Atlas shows and saves progress.

### Idols: what the side panel leaves out

- **Known:** the `Idols` module works with the map device's storage grid as the Idol inventory ([Idols](../docs/game/idols.md)).
  The client's own Idol inventories (three loadouts of up to 32 cells) have no panel in the 3.29.3 clients.
- **Start:** the slots unlocked by Atlas objectives (`MapRelicInventoryLayout`, league quest flags `AtlasIdolSlotsUnlocked1` to `6`);
  the Ruthless versions of two unique Idols;
  refusing Atlas passive allocations on the server while the tree is hidden; drop rates from recorded play.
- **Done when:** a later client build with an Idol panel shows the three loadouts, or the items above work in the side panel.

### Hideouts: decorations, editing and the map device

- **Known:** the Lush Hideout works: travel, waypoint, stash, Nessa, the Edit button and the map device: slots,
  Activate and six portals into the map ([21](../docs/protocol/21-hideouts.md) §5).
  Gateway checks cover standalone Sacrifice and Mortal fragment refusal. Client presentation remains to be checked.
  Its decoration (`data/hideouts.json`) lists every doodad with its type and model.
  The Heist Locker is placed and opens its native storage window; contract/blueprint transfers need separate work.
  Moving/rotating installed furniture and NPCs now saves per character/template through C→S 0x5b.
  The map device, Nessa and stash were checked live, including restored positions after reconnect.
  Other installed objects use the same handler; their individual editor interactions need manual verification.
  Device edits rebuild native walkability through 0x161 and replace the server footprint at the saved position.
  Small-device underfoot escape and walking across its former position are verified in the real client.
  NPCs and functional furniture now use inherited OT footprints and static-model hulls through one registry.
  Waypoints and loot remain passable. Check large MTX models and wall-adjacent layouts separately.
  Save/reentry persistence and collision escape for all supported NPC types have automated coverage.
  The dev `/statue` command creates a stone Monster Statue of any MonsterVarieties row at the player and
  saves it per character/template; C→S 0x0209 replaces a targeted hideout monster the same way. The
  statues play their idle and are recreated on entry. See [21](../docs/protocol/21-hideouts.md), Monster Statue.
- **Start:** create the rest of the decorations (`HideoutObject`, each type's components from
  data/object-types.json); the Edit mode's placement/removal and appearance messages;
  maps: remaining scarab engines, Essence skills/enhancement scaling and Evolution/Apparitions (#505);
  Torment spirits' and Grips' own granted skills (#1129), including the Arsonist's first Firestorm cast, its target and repeat timing.
  Observe the Martyr's death explosion, animated weapon contacts and
  the party's death and corpse removal displays in the client (#1182);
  verify possessed monsters' action timing (#1177) and
  the host's life bar and size when leaving, claiming or re-entering a Massive shrine's radius (#1178, #1179) in the client;
  Harbinger follow-up: verify the avatar's life bar inside a Massive shrine after a wave kill (#1180) in the client;
  The Beachhead unique map (`HarbingerPortal` boss encounter, Harbinger unique piece drops,
  puzzle-grid assembly and Harbinger Scroll upgrades);
  `An Audience With The King` opens the Crux; Simulacrum has arena selection and a wave controller.
  Simulacrum's native reward/fog presentation, specialised boss behaviour and full clear need verification.
  Personal template selection and login to the selected home are implemented (see Status).
- **Done when:** the hideout looks like the real one, can be edited and saved, and maps can be run from its device.
