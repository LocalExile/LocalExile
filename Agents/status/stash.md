# Status: Stash

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/stash.md](../roadmap/stash.md).

## Works (verified with the real client)

- Delve stash correction: publishes the native 41 × 4 inventory bounds;
  all fossil/resonator slots retain the 5000 limit.
  Empty legacy resonators migrate to stackable types with 1–4 independent yellow/orange Delve sockets.
  Automated layout/merge/reload coverage; confirmed working by the user in the real client.
  See protocol/18-stash.md.
- Stash: the Stash in Lioneye's Watch at (319, 272) opens the stash panel (S→C 0x7b kind 1) with the account's 11 tabs
  of the character's league (normal, premium, quad, currency, essence, fragment, map, flask, gem) from the tab list in
  S→C 0x21; each tab's contents come on the client's request (C→S 0xc7 → 0xc0 row 26 + 0xc8);
  items go into and out of tabs by click (0x28 / 0x2a) and Ctrl + click (C→S 0xe0 in, 0xe3 out, stacks onto stacks);
  special tabs hold only their own items (the map tab nothing yet), the currency, essence and fragment tabs each item
  in its own slot (currency up to 10 000), from the game data's stash tables (18 §7);
  Ctrl + click on currency goes to its affinity tab's slot (C→S 0xe1, live 2026-10-20) and a plain click on a currency
  slot picks up the stack; the tab settings' affinities (0xd3) are saved;
  the stash reopens on the account's last tab after a relog (0x7b affinity on the first open per connection) and keeps
  the panel's own tab while the connection lasts; tabs and items are account-wide (the account's row in
  `data/saves.db`).
  Verified live with normal and premium tabs; the special tabs load but their slots are not checked.
  Guild stash: a second object in every hideout opens the guild stash panel (0x7b kind 2) with its own tabs (manager
  2), one per account until there are guilds (verified live).
  Code: `AccountStash.cs`, `StashCatalog.cs`, `InstanceStash.cs`, Gateway (`OpenStash`, `SendStashTab`).
  Docs: [18](../docs/protocol/18-stash.md).

## Partly working

- **Lifeforce and crafting cells.** Currency-tab lifeforce slots hold 65,535 each without wrapping.
  Delve crafting cells accept equipment, support swaps and retain their contents after reload.
  Lioneye's Watch now creates a guild stash beside its personal stash.
  Persistence checks pass; the real client opened the city guild stash and loaded its guild tab.

Nothing yet.

## Not working yet

- Stash: a switch to an already loaded tab sends nothing, so it is missed if the player logs out without reopening the
  stash or moving an item; the special tabs' slot rules, tab folders (the tab order, names and colours are kept,
  2026-10-22), real guilds for the guild stash, per-league stashes and stashes in other towns
  ([18](../docs/protocol/18-stash.md) "Open").
