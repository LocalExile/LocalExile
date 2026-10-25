# Roadmap: Hideouts, crafting stations and maps

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/hideouts-and-maps.md](../status/hideouts-and-maps.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 1. Quality of life

Harvest station placement and native recipe browsing are implemented.
Next: implement Harvest recipe execution and atomic lifeforce payment; see protocol/21-hideouts.md.

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
  The map device and Nessa's shop use the older area-named maps (Beach Map).
- **Start:** the Atlas panel's messages and how it stores progress; the map stash tab's layout (18 §7);
  maps from the generic bases on the layout picked on the Atlas; drops by tier.
- **Done when:** maps drop, are stored and run the 3.29 way, and the Atlas shows and saves progress.

### Hideouts: decorations, editing and the map device

- **Known:** the Lush Hideout works: travel, waypoint, stash, Nessa, the Edit button and the map device: slots,
  Activate and six portals into the map ([21](../docs/protocol/21-hideouts.md) §5).
  Its decoration (`data/hideouts.json`) lists every doodad with its type and model.
- **Start:** create the decorations (`HideoutObject`, each type's components from data/object-types.json);
  the Edit mode's client messages (move, place, remove, save);
  maps: the arrival point, a return portal, fragments and device recipes and map mods.
  Personal template selection and login to the selected home are implemented (see Status).
- **Done when:** the hideout looks like the real one, can be edited and saved, and maps can be run from its device.
