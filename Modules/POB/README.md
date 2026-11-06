# POB

Optional LocalExile module for the 3.29.3b client. It prepares a Path of Building import, asks for confirmation,
then replaces the current character's build and returns the player to character selection.
The converter's source and executable project are included in this folder; a PoB installation, neighbouring
checkout, external converter installer and client patches are not required.

## Enable and build

The module path in a league's `modules.json` is `POB`; Standard includes it. Removing that entry disables import
for that league without deleting saved characters. League module lists are read at Gateway startup.
The module depends on `Core/Game` through the `Game` module dependency.

`dotnet build GameServer.slnx` builds `bin/pob.dll` and `bin/converter/PobImport.dll` with its dependencies.
The normal module build and release packaging include both. The existing `stat-descriptions` extraction entry
provides `data/stat-descriptions.txt`; the converter also uses the ordinary extracted `data/tables` and passive graphs.
Generated game data, build output and downloaded snapshots do not belong in Git.

## Import

- `/pob <code>` prepares a short share code without changing the character.
- For longer codes, a server operator places UTF-8 text in `data/pob-import/inbox/build.txt`, then the player uses
  `/pob file build.txt`. This avoids the unmodified client's 512-character chat limit.
- `/pob file build.json` accepts a previously downloaded native build snapshot containing its `pob` code and `build`
  data. This retains modifier IDs and physical socket indexes omitted by some PoB exports.
- The summary and warnings are followed by a confirmation prompt. `Y` confirms within five minutes;
  `N` or `/pob cancel` cancels. Confirmation is single-use. A short countdown precedes character selection.
- The player re-enters the same character after application. An invalid code or expired/cancelled confirmation
  does not replace the character.

Import replaces the build's class, level, experience, passive tree/masteries/tattoos, ascendancy, equipped items,
weapon swap, flasks, socketed gems and passive jewels. Unrelated inventories, quest flags, waypoints, stash,
cosmetics and module progress remain from the latest save. A transactional backup of the previous character
record and save is stored under `pob-backup/<character id>/<backup id>` in the save database's metadata.
The old connection's writers are retired before the atomic replacement; admission is blocked during application.

## Scope

The module imports a build; it does not implement every gameplay mechanic described by PoB. Unsupported skills,
tree options and conversion losses are reported. Item/gem losses reject the default import rather than silently
committing a partial replacement.
Energy Blade snapshots can retain calculated damage and sockets; plain PoB weapon text cannot reconstruct damage
or the pre-transformation sword when those values are absent. A matching native snapshot is required in that case.
Unknown description handlers are left unresolved instead of guessing stat values.

No network downloads occur during import. Only simple `.txt`/`.json` names in the server's inbox are accepted;
file size, inline code size, converter concurrency and conversion time are bounded. The converter prepares data
using a read-only database connection; the Gateway owns the final transaction.
