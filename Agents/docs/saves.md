# Saves

How the servers keep player progress: what is saved, where, and how a change is written so that nothing is lost.

## What is saved, and where

Everything is in one SQLite file, `data/saves.db` (`Game_SAVES_DB` to put it elsewhere; git-ignored with the rest of
`data/`), shared by the Gateway, Auth and the dev host:

| What | Table | Code |
|---|---|---|
| A character's items (every inventory row and the cursor), skill bar, experience, passives and mastery effects, refund and extra passive points, waypoints, visited areas, quest flags, the start weapon still on the ground | `characters`, one row per character | `CharacterSaves`, `PlayerInventories` (Gateway) |
| An account stash's header: its tabs (name, type, colour), last tab, next stash item id, gold | `stashes`, one row per account | `AccountStash` (Gateway) |
| The items of one stash tab | `stash_tabs`, one row per account and tab | `AccountStash` (Gateway) |
| Accounts | `accounts` | `SqliteAccountStore` (Auth, Host) |
| Characters of an account: class, league, level (a copy for the character list), last town | `account_characters` | `SqliteAccountStore` (Auth, Host) |
| Deleted characters' saves, for recovery by hand | `deleted_characters` | `SqliteAccountStore.DeleteCharacter` |

Each row holds its record as JSON, readable with any SQLite browser. A save field this build doesn't know (a newer
build wrote it) is kept as it is by the next write. Nothing is cached across processes: Auth and the host read and write
the database directly, and a write waits for another process's transaction to end.

Not saved, on purpose, as in the real game: monsters, dead monsters, items on the ground and an opened map's portals
belong to their area instance, in memory. They last while the instance does (across logouts and disconnects) and are gone
when it expires or the Gateway restarts.

## How a change is saved

Every change to items, the stash, gold, passives, quest flags or waypoints is saved at once, in a transaction
(`Saves.Atomically`, over `SaveDatabase.Atomically`). The database runs with a write-ahead log and `synchronous=FULL`: a
committed transaction survives a crash or a power cut, and one that did not commit leaves nothing behind. A commit costs
about 0.04 ms for a 20 KB save and 0.5 ms for 400 KB on an NVMe drive (2026-10-20); these changes come at the speed of
the player's clicks.

A change that touches more than one save, or one save more than once, runs in one transaction, so all of it is saved or
none of it is. An item is never lost between its two places, nor counted in both:

| Change | In one transaction (`ItemMoves`, Program.Items.cs) |
|---|---|
| Stash → cursor (C→S 0x28), stash → inventory (0xe3, a stack onto the inventory's stacks) | out of the tab (or what doesn't fit stays), the last tab, onto the cursor / into the inventory |
| Inventory → stash (0xe0 into the shown tab, 0xe1 into the item's affinity tab) | out of the inventory (or what doesn't fit stays), into the tab (a stack onto its stacks, a slotted tab into the item's slot), the last tab |
| Held item into a tab (0x2a): merge, Shift-place one, swap | the tab's stack or item, the cursor |
| Split a tab's stack (0x7c) | the tab's stack, the split-off part on the cursor |
| Buy from an NPC (0x88, Ctrl + click 0x8a) | the price off the inventory's stacks and then the stash's, the item in |
| Sell to an NPC (Accept) | the offered items out, the reward in |
| Scroll of Wisdom on an item (0x71) | one scroll off its stack, the item identified |
| Orb of Regret (0x58) | one orb off its stack, the refund point |
| Gold respec, passive changes | the gold, the passives, their mastery effects, the refund points |
| Old per-character gold → the account (login) | the stash's gold, the character save letting go of it |
| Map device Activate | every slot's item |
| Delete a character (Auth) | the character out, its save set aside |

A stash change writes the stash's header and only the tab it changed: a click in one tab doesn't rewrite every tab.

When a save fails (a full disk, a database another program holds too long), the database rolls the whole transaction
back, the objects it changed in memory are dropped and loaded again from the database on their next use (a dropped copy
refuses any further change), and the error closes the connection. Memory is never ahead of the disk, and the next login
finds everything as last saved. A pickup from the ground whose save fails puts the item back on the ground.

The Gateway is a single-player server: one player plays at a time, and each connection's messages are handled in order.
A transaction holds the database's lock from its first change to its commit, so no other save lands in between.

## Experience and flask charges: saved every few seconds

Kills come by the hundred per second, and a committed write per kill would queue the Gateway behind the disk. So
experience and the flask charges kills refill are kept in memory and saved within 5 s (`CharacterSaves.ExperienceSaveInterval`),
at a level-up (at once, before the account hears of it), when the connection ends (a logout, an area change, a client
crash), when the Gateway stops, and with any other write of the character's save (an item move carries them along). A
crash or a power cut loses at most those few seconds of them; items are never kept this way.

The experience total in memory is the character's, shared by its connections, not a connection's copy: kills from
projectiles or Righteous Fire and an older connection still finishing during an area change all add to it, and none
writes an older total over a newer one. Quest flags, waypoints and visited areas are saved at once as changes to the
saved ones (added or taken away), for the same reason. A deleted character's unsaved experience is dropped, never given to
a new character of its name.

The character's level comes from its saved experience (`CharacterSaves.LevelOf`). The account's level is a copy for the
character list; when a level-up's update to Auth fails, the next login corrects it (`set_level`). Only Auth writes the
last town.

## The sell window

As in 3.29, an item offered to an NPC moves to the sell window but keeps its inventory cell: the client shows a greyed
imprint with the item's size and collision, and nothing else can take that cell or move or use the item
(`PlayerInventories.Offer`). An item offered from the cursor waits in the cell it was picked up from (or the first free
one), saved there. The client is not told the item left its inventory (its imprint is the client's to draw). Cancel, closing the window, a disconnect or a crash end the sale with every item where it was; the
offer itself is only in memory, so the save already has every item in its cell. Accept takes the offered items and puts
the reward in, in one save; the offered items' cells free up first, so the reward nearly always fits. When it doesn't,
nothing changes and the window stays open ("Not enough room in your inventory.").

## Loads that stop instead of losing

- A row that isn't a save (broken JSON, `null`) throws `UnreadableSaveException`: the login or the change stops and the
  row stays as it is. Starting the character or stash over would write over it.
- A saved item, socketed gem or mod the game data doesn't know (tables missing or not extracted) throws
  `UnknownSavedItemException`, and so do saved passives without the passive tree: the login stops and the save stays.
  Dropping them would lose them at the next save. Extract the game data (`scripts/dev/extract-data.ps1`) and log in
  again.
- Loading a character writes nothing; only a new character's start kit is saved at once.
- A ticket Auth can't resolve is refused, instead of playing the session under another save.

## Saves from before the database

On its first start with the database, the Gateway imports `data/saves/*.json` and `data/stashes/*.json`, and Auth (or the
host) imports `data/accounts.json` (`Game_DEV_DB`), each in one transaction; the files are renamed to `*.imported` (kept,
never read again). A file that can't be read stops the import and the server, so no account or character starts over;
fix or remove that file and start again. `data/saves/deleted/` is not imported. An imported stash keeps its items in its
header until its first save, which gives every tab its row.

## Tests

`SaveDatabaseTests` (commit, rollback, nested transactions, another process writing, set-aside, the import),
`ItemMovesTests` (every change above, each with a write made to fail part-way, then both saves loaded again; a control
test shows the same failure without a transaction loses the item), `ProgressSaveTests` (quest flags, waypoints, level,
unknown fields and items), `UnsavedProgressTests` (experience and flask charges kept in memory and saved),
`StashRowsTests` (one tab per write, imported stashes), `SaleImprintTests` (the sell window), `StashRulesTests` (what
each tab holds, slots, the repair at load), `CtrlClickTests` (Ctrl + click to and from the stash and a shop) and, in the
Core tests,
`SqliteAccountStoreTests` (accounts, deletion with its save, the import).
