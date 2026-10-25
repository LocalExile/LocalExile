# Status: Setup, tooling and tests

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/setup-and-tooling.md](../roadmap/setup-and-tooling.md).

## Works (verified with the real client)

- Game data extracted from the GGPK in one step: `scripts/dev/extract-data.ps1` (run by `live.ps1` on every start,
  `-NoExtract` to skip) regenerates each data file that is missing or whose manifest version changed (stamps in
  `data/extract-stamps.json`), and the Gateway warns once at startup about any missing file.
  Code: `scripts/dev/extract-data.ps1`, `GameDataFiles.cs`.
  Docs: [GettingStarted](../GettingStarted.md) step 7.
- Local patching server: the client never contacts the official patch servers.
  Code: `GameServer.Patch`.
  Docs: [01](../docs/protocol/01-login.md) §1, [GettingStarted](../GettingStarted.md) step 6.
- Dev account service: register, log in, create characters (JSON-lines over TCP).
  Code: `GameServer.Host`, `GameServer.DevClient`.
  Docs: [dev-server.md](../docs/dev-server.md).

## Partly working

Nothing yet.

## Not working yet

Nothing yet.

## Tests

`dotnet test tests/GameServer.Core.Tests` (378 tests), `tests/GameServer.Data.Tests` (15) and
`tests/GameServer.Gateway.Tests` (376: currency on items (from the inventory or a stash tab), the currency tab's
crafting slot, gem levels, gem requirements (level and attributes) and the level stall, the level-up stopped between
two levels, the gem level-up and lock messages and the saved lock flag, quality and variants, stable gem skill ids,
the save database and its transactions, stash tab rules, Ctrl + click moves, item moves with failing saves, inventory
moves, stacks, the start weapon on the ground, flasks, arrival points, instance ground items and monsters, quests,
passives, spell suppression, attack speed, critical strikes, accuracy, armour/evasion, item mods, auras, damage
conversion and the account stash) pass.
Tests that need the real client pass quietly without `Client/`.

## Tooling

Everything used to work out the protocol is in the repository: the Ghidra, Frida and Python scripts in `scripts/re/`,
our own tools in `Tools/`, and how to install the third-party ones in [GettingStarted](../GettingStarted.md)
("Third-party tools").
The full reverse-engineering setup has not yet been rebuilt from a fresh clone;
if something is missing, fix the guide.

Optional [native client control](../docs/dev/client-control.md) provides CLI/JSON RPC for development without repeated
UI input.
It targets the copied standalone 3.29.3b client and local services only.

## Doc caveats

`docs/re/003`–`004` are early notes with known errors (for example "login is HTTPS"; it is custom TCP, see `re/007`).
Treat them as history, not reference.
