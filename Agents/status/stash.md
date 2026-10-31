# Status: Stash

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/stash.md](../roadmap/stash.md).

## Works (verified with the real client)

- Delve stash correction: publishes the native 41 × 4 inventory bounds;
  all fossil/resonator slots retain the 5000 limit.
  Empty legacy resonators migrate to stackable types with 1–4 independent yellow/orange Delve sockets.
  Automated layout/merge/reload coverage; confirmed working by the user in the real client.
  See protocol/18-stash.md.
- **Currency withdrawal (#249).** Ctrl + left click takes at most one normal inventory stack from the clicked slot;
  Ctrl + right click uses the separate `0xe3` bulk boolean to take as much of that stack as fits.
  Real-client/Gateway logs confirm bulk 0 takes 40 from 218, leaving 178;
  bulk 1 takes 178 as four stacks of 40 and one of 18.
  Automated tests cover quantity limits, existing-stack top-ups, partial/full inventory, rollback and save reloads.
- Stash: the Stash in Lioneye's Watch at (319, 272) opens the stash panel (S→C 0x7b kind 1) with the account's 11 tabs
  of the character's league (normal, premium, quad, currency, essence, fragment, map, flask, gem) from the tab list in
  S→C 0x21; each tab's contents come on the client's request (C→S 0xc7 → 0xc0 row 26 + 0xc8);
  items go into and out of tabs by click (0x28 / 0x2a) and Ctrl + click (C→S 0xe0 in, 0xe3 out, stacks onto stacks);
  special tabs hold only their own items (the map tab nothing yet), the currency, essence and fragment tabs each item
  in its own slot, from the game data's stash tables (18 §7);
  currency-tab limits come from `CurrencyItems.CurrencyTab_StackSize` (generally 10 000; lifeforce 50 000).
  Earlier real-client verification and Gateway logs show yellow lifeforce merging into one 50 000 stack in slot 99.
  Automated tests cover manual and quick merges, Harvest types, overflow and save reloads;
  Ctrl + click on currency goes to its affinity tab's slot (C→S 0xe1, live 2026-10-20) and a plain click on a currency
  slot picks up the stack; the tab settings' affinities (0xd3) are saved;
  the stash reopens on the account's last tab after a relog (0x7b affinity on the first open per connection) and keeps
  the panel's own tab while the connection lasts; tabs and items are account-wide (the account's row in
  `data/saves.db`).
  Verified live with normal, premium and the currency lifeforce slot; other special-tab slots need further checks.
  Guild stash: a second object in every hideout opens the guild stash panel (0x7b kind 2) with its own tabs (manager
  2), one per account until there are guilds (verified live).
  Code: `AccountStash.cs`, `StashCatalog.cs`, `InstanceStash.cs`, Gateway (`OpenStash`, `SendStashTab`).
  Docs: [18](../docs/protocol/18-stash.md).
- **Fast stash opens.** The client keeps only the stash tab it shows loaded: picking a tab unloads the others (S→C 0xc1)
  and resends the tab list with them marked not loaded. Area entry preloads only the tabs with shop currency.
  Before, each open got slower with every loaded tab (about 1.5 s with full currency, essence, fragment, gem and
  divination tabs). The user confirmed in the real client (2026-10-29) that opens became much faster, with no crash.
  Gateway tests cover the unload, the price tab that stays, the first open's two requests and the 0xc1 bytes.
  See protocol/18-stash.md §3.
- **Dev tab fill (`/item_tab <tab name> <count>`).** On a currency, essence, fragment, delve, blight, delirium or divination tab it
  adds up to `count` more, at most 1000 per command, of every item type that has its own slot, as far as each slot holds.
  Divination cards are filled to complete sets, which is what that tab shows.
  On a gem tab it adds one of every creatable gem variant not already there, as far as its 500 cells go.
  The flask tab adds one missing standard flask or tincture base and each creatable unique flask/tincture identity.
  Ordinary flasks include 12 life, 12 mana, 6 hybrid and 18 utility bases; Royale duplicates are excluded.
  Existing normal or magic items count by base; uniques count by base, name and visual identity, independently.
  Repeat filling adds no duplicates and preserves existing items, including different uniques on the same base.
  Other tabs without a supported fill mode are refused with the reason.
  Blight, Delirium, Divination, Ultimatum and Breach tabs now publish their StashType sizes; the 12 × 12 default made the
  oils draw over the blight tab.
  Verified in the real client (2026-10-28): blight oils, all divination cards in both rows of the tab, and the gem tab's
  colour sub-tabs. Automated tests cover caps, in-place growth, reloads, refused tabs, tab lookup, cell fill and every
  real layout against its published size. The regular map tab remains open (roadmap).
  Unique collections use 22 hidden category inventories and the native collection cells; `/item_tab Уники 1` fills
  each available collection cell once, preserving existing items. Verified in the real client: 1309 items, including
  unique maps and inactive tinctures. Repeat filling does not duplicate occupied cells. Seven old catalogue names
  have no matching client collection cell and are reported as unavailable; unique Heist contracts are not generated.
  Legacy items can make the native collected count exceed its current-league denominator.

## Partly working

- Personal stash and gold are shared only within the same account and full league, including mode combinations.
  Guild stash storage is also league-scoped; Standard retains the legacy bank and other leagues use separate banks.
  SSF refuses guild opening, tab requests and inventory moves.
  Automated checks cover SSF guild-stash exclusion and ordinary guild-stash access.
  Stash sharing, forged requests and dropped-item isolation are covered by automated checks.
  Shared SSF town player presence remains separate from private dropped items.
  See [league modes](../docs/game/league-modes.md).
- **Crafting cells.** Delve crafting cells accept equipment, support swaps and retain their contents after reload.
  Lioneye's Watch now creates a guild stash beside its personal stash.
  Persistence checks pass; the real client opened the city guild stash and loaded its guild tab.

## Not working yet

- Stash: a switch to an already loaded tab sends nothing, so it is missed if the player logs out without reopening the
  stash or moving an item; the special tabs' slot rules, tab folders (the tab order, names and colours are kept,
  2026-10-22), real guilds for the guild stash and stashes in other towns
  ([18](../docs/protocol/18-stash.md) "Open").
