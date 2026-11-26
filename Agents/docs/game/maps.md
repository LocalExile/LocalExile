# Maps and the Atlas (3.29 rules)

How maps work in 3.29, the version this server targets. Build maps, the map device, map drops and the map stash tab
from these rules. The rules come from the project owner (2026-10-20, from the 3.29 game and its wiki); the data names
come from the extracted tables.

## Map items: 20 generic base types

3.29 maps are not named after an area. There are 20 map base types, and any of them can be used on any layout of the
Atlas: the player picks the layout on the Atlas, the map item sets the tier.

| Base type (BaseItemTypes Id) | Name | Map tier | Area level |
|---|---|---|---|
| `Metadata/Items/Maps/MapKeyTier1` … `MapKeyTier16` | Map (Tier 1) … Map (Tier 16) | 1 … 16 | 68 … 83 (tier + 67) |
| `Metadata/Items/Maps/MapKeyNightmareBoss` | Nightmare Map | 16 | 83 |
| `Metadata/Items/Maps/MapKeyShaperGuardian` | Shaper Guardian Map | 16 | 83 |
| `Metadata/Items/Maps/MapKeyVaalTemple` | Vaal Temple Map | 16 | 83 |
| `Metadata/Items/Maps/MapKeyReliquary` | Valdo Map | 17 | 84 |

Their item class is `MapKey` (ItemClassCategory 63, which StashtabAffinityByItemClassCategory gives the Maps affinity).

- **Nightmare Map:** modifiable only with Chaos Orbs, Vaal Orbs, Delirium Orbs and chisels. Tier 16 maps have a chance
  to drop as a Nightmare Map when Voidstones are socketed into the Atlas. It is the tier-17 map item.
- **Originator influence:** the `MapZanaInfluenced` implicit (`map_zana_influence`) makes the area "Influenced by the
  Originator's Memories"; the client shows that line on the map item. The map device does not read this implicit yet,
  so Nessa's stock only carries the item identity for now.
- **Shaper Guardian Map:** the area is influenced by the Shaper.
- **Vaal Temple Map:** the area contains 10 additional guarded Vaal Vessels; always corrupted. Obtained by corrupting
  tier 16 maps.
- **Valdo Map:** acquired from a Valdo's Puzzle Box.

The tables also hold about 490 older map bases named after areas (`MapTier1_1` "Cursed Crypt Map", `MapAtlasBeach` and
`MapWorldsBeach` "Beach Map" …, class `Map`, and Maps.dat with each one's `Regular_WorldAreasKey`). They are left over
from earlier versions: 3.29 doesn't drop them. Don't build new features on them.

## The Atlas

- The player's progress shows on the Atlas. Completing a map on the chosen layout reveals the adjacent connected areas,
  which opens new regions to enter.
- The player starts in the centre of the Atlas, where the first maps (area level 68) are.
- 16 standard tiers, each tier one area level (table above).

### The Atlas tree's stats in a map

The client shows, and its level generator reads, only the area stats of 0x1d, so the server composes the tree's own rules
into that list (`AtlasStats`, `Game.MapAreaStats`) from the active preset:
- **Small** nodes are the ones that are not Notable, Keystone or Gateway (`IsAtlasWormhole`), the client's own
  definition (ReminderTextSmallAtlasPassives). Gateways are never scaled or dropped.
- `small_atlas_passive_effect_+%` (Wandering Path, 100) scales every Small node's values, truncated per value;
  `map_atlas_extra_content_small_node_effect_+%` (Unfettered Concentration) adds for **Extra Content** nodes: the id
  families (`atlas_<family>_`, matching the AtlasPassiveSkillTreeGroupType of their group's mastery) of the leagues
  ReminderTextAtlasExtraContent lists. `delve`, `jun`, `alva`, `bestiary` and `masters` count as the missions,
  `domination` as Shrines, `harvest` as the Sacred Grove, `heist` as Smuggler's Caches, `anarchy` as Rogue Exiles,
  `settlers` as Ore Deposits. Tempests and Wildwood Wisps have no nodes.
- `display_notable_…_grant_nothing` and `display_small_…_grant_nothing` drop those nodes' stats; they still count as
  allocated, so `map_pack_size_+%_per_atlas_notable_passive_allocated` (Grand Design) adds pack size per allocated Notable.
- The explicit mods' effect is `map_mod_effect_+%_for_non_unique_maps` (not on unique maps) + `_per_mod` × explicit mods
  + `_per_fragment_used` × fragments; only the first id names unique maps.
- `map_contains_x_additional_<r>_packs_if_<r>` (no area text) becomes the plain count on a map of that rarity and is
  dropped otherwise. Meticulous Appraiser moves the three quantity stats into `map_item_drop_rarity_+%` at 300%.
- The vital and deadly keystones' `map_monster_{life,damage}_+%_final_from_{vital,deadly}_atlas` stay in the list as
  they are; the Game module copies each to the monster stat without `map_`, a more modifier on the monster's life or hit
  (`MonsterModEffects.HandOverMore`).
- A HardMode league uses the HardMode vectors: Wandering Path keeps only its first stat, Grand Design only its first.
- `atlas_keystone_no_extra_content` (Back to Basics) needs nothing more: the level generator already rolls no league
  content with it. Its 0–80% more mod effect, Growing Hordes, Eye of the Djinn's per-empty-slot scarab effect and the
  device gates (Unwavering Vision) are not applied.

## Drops

- The lowest tier drops during the campaign; inside a map, higher tiers start dropping.
- Magic and rare monsters can drop maps one tier above the map the character is in; map bosses two tiers above.
- Moving from one group of tiers to the next is a challenge: higher tiers are harder to sustain. Mid and top tiers add
  higher values for the mods they roll, new harder mods, and mod combinations low tiers don't allow together.
- Many Atlas passive skills improve map drops.

## Voidstones

When a Voidstone is socketed into the Atlas:

- every map in that quadrant has its minimum tier raised to 16;
- found maps drop one tier higher, and maps below tier 16 are converted to gold;
- every map on the Atlas gets a global effect, including the chance for a tier 16 map to drop as a Nightmare Map.

## In this server today

- **Implemented first slice (2026-10-27):** generic `MapKeyTierN` items (MapKey component) carry their map tier and can
  be created with `/item Map (Tier N)`; the map device accepts them, the native Atlas panel selects a starting layout
  (C→S 0x00e7), and the device opens that layout at the map tier. The selected layout is saved with the character.
  The area level follows the tier (`tier + 67`), and the area instance/monsters use it. Every character has the epilogue
  flag `EndGameAvailable` that the client requires for Atlas activation and map areas (before Kitava only in a hideout
  and the maps: `Quests.EndgameOnly`). Basic map drops from monsters use a local
  approximation.
- **Atlas passive tree, first slice:** the three presets are saved and sent (S→C 0x3d); node allocation/refund
  (C→S 0x3e/0x3f) is saved, with a dev point budget (`/atlas_points`).   The Atlas graph, point awards from completion,
  map stash placement and the official drop pools are not implemented. Voidstones drop from the endgame bosses
  `AtlasVoidstoneSlots` names; the first kill sockets them (quest flags GrantAtlasUpgrade1–4) and found maps
  then drop one tier higher, with maps below tier 16 converted to gold. The Atlas panel click (C→S 0xf6) moves
  a Voidstone into or out of Inventories row 39. Each occupied socket sets that quadrant's S→C 0x1a3 `+0x70` bit
  (four stones also set bit 4) so those Atlas nodes draw as T16; put / take resends 0x1a3.
- **The map device** ([21](../protocol/21-hideouts.md) §5) opens a map's area from Maps.dat's `Regular_WorldAreasKey`,
  which only the old area-named maps have. Kirac sells generic `MapKey` tiers that open the Atlas layout. With those
  bases, the area comes from the layout picked on the Atlas instead. Generic maps now use `MapKeys.dat` for their tier
  and the selected Atlas layout for the area; their area level follows the tier rather than the layout's WorldAreas row.
  The map item's affixes and quality, and scarabs/fragments' `MapFragmentMods`, apply to that instance (drops, pack
  size, monster life/damage). A recipe with no map (`MapDeviceRecipes`: Writ, invitations, four Sacrifice fragments)
  opens that boss area (Atziri, Shaper, Elder, Maven, Sirus, Eater, Exarch and the rest of `MapDeviceRecipes`).
  Those areas place their WorldAreas bosses without a captured room point; a multi-boss recipe arena holds the last
  one back until the others die (Maven's Nucleus, Sirus, Atziri). A Maven invitation stands its `MavenFights`
  witness bosses in MavenHub. Killing the last WorldAreas boss completes the Atlas node. The first Maven / Uber
  Elder / Eater+Exarch / Incarnation of Dread kill drops that `AtlasVoidstoneSlots` Voidstone, sets the
  GrantAtlasUpgrade quest flag, and socketed stones raise found map tiers (below 16 become gold).
  League extra content is not spawned yet.
- **The map stash tab** takes nothing yet: the client lists its maps by tier, and how it places them is not known
  ([18](../protocol/18-stash.md) §7).

## Monster hit modifiers

Equalising deducts a percentage of the victim's maximum Life, Mana and Energy Shield on rare and unique monster hits.
The legacy nearby-ally modifier counts live peers and owned allies around the player at the instant of a monster hit.
The server estimates that radius at 40 cells.

Simulacrum projects the native early, middle and late flask-drain affixes into each wave's monsters.
A successful roll on a hit removes one charge from an equipped flask that has a charge.
