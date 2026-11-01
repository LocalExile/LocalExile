# Roadmap: Stash

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/stash.md](../status/stash.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 1. Quality of life

Delve slot bounds, empty resonator socket layouts and stackable migration are implemented;
verify the corrected fossil/resonator presentation in the real client (18-stash.md).

### Stash: tab settings and the other special tabs

- **Known:** the stash opens on the last tab, tabs load and items move by click and Ctrl + click
  ([18](../docs/protocol/18-stash.md)); special tabs hold only their own items, the currency, essence and fragment
  tabs each item in its slot, and affinities (0xd3) are saved (18 §7).
  Currency stack limits follow the extracted table; lifeforce merging to 50 000 is verified in the real client.
  Switching to a loaded tab sends nothing to the server.
- **Start:** check live that the affinity window closes on S→C 0x136;
  tab rename / colour (0xd2), order (0xcc) and folders (0xd1); the regular map tab
  (MapStashSpecialSubStashGroup / MapStashUniqueMapInfo). Unique collection categories and `/item_tab` filling
  are implemented; unique Heist contract generation and catalogue entries absent from the native collection remain
  unsupported. The divination and gem tabs show items (18 §7).
- **Done when:** tab settings are kept and every special tab takes its items as the real one does.

### Special-tab crafting follow-up

Lifeforce limits and Delve equipment cells are implemented ([18](../docs/protocol/18-stash.md)).
Currency-tab limits now follow the extracted item data instead of the u16 wire maximum.
