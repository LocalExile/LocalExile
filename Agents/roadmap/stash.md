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
  Switching to a loaded tab sends nothing to the server.
- **Start:** check live that the affinity window closes on S→C 0x136 and that currency Ctrl + click (0xe1) lands in
  its slot; tab rename / colour (0xd2), order (0xcc) and folders (0xd1); the unique and divination tabs.
- **Done when:** tab settings are kept and every special tab takes its items as the real one does.

### Special-tab crafting follow-up

Lifeforce limits and Delve equipment cells are implemented ([18](../docs/protocol/18-stash.md)).
Further stack-limit increases require verifying a protocol representation beyond the current u16 Stack component.
