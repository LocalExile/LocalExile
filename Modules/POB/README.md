# POB

Optional server-side import bridge for the 3.29.3b client. The module contains no PoB parser, item-text converter,
compatibility database or separately maintained conversion project. Conversion belongs to an external tool
(for example a future Waypoint integration); this repository owns validation and character-save application.

## Configuration

Enable `POB` in the league's `modules.json`. Removing it disables import for that league.
League module lists are read at Gateway startup. Build the module through the normal solution build.

Set `ConverterExecutable` and `ConverterArguments` in the module's `settings.json` to an operator-installed tool.
The executable is an explicit filesystem path; arguments are a fixed JSON string array, never a shell command.
The default executable is empty: code conversion reports that no external converter is configured.
The server neither downloads nor installs a converter. Release packaging includes only the server module.

## External contract, version 1

The server starts the configured executable without a shell, from the repository root. It writes one UTF-8 JSON
request to stdin, then closes stdin. Request fields are `Version` (1), `Code`, `Name`, `Data` (absolute catalogue
directory), `Record` (the current `Character` record) and nullable `SourceFile` (an operator-provided snapshot).
No account credentials or writable database connection are passed to the converter.

The external tool returns one JSON document on stdout and exits with code zero:

```json
{"Version":1,"Record":{},"Save":{},"Summary":"Prepared build","Warnings":[]}
```

`Record` uses the server's `Character` serialization and must retain the requested character/account IDs, name and
league. Only its class and level are imported. `Save` uses `CharacterSaves.Save`; only build-owned inventory rows
1-11, 14, 15, 23 and 102 are accepted. The server materializes item payloads against its own catalogs before staging.
The remaining save fields follow the server's build merge contract in `CharacterBuildImports.Merge`.
Invalid identity, unsupported version, invalid item data and oversized results fail before character mutation.

Only one conversion runs at a time. Conversion has a 90-second deadline and is cancelled when the module stops.
Stdout is limited to 8 Mi characters, stderr to 8 Ki characters; exceeding either limit terminates the process.
The external executable is trusted operator software and is not sandboxed by this module.

## Commands and save lifecycle

- `/pob <code>` sends a short share code to the external converter.
- `/pob file build.txt` reads a longer code from `data/pob-import/inbox`, avoiding the client's 512-character chat limit.
- `/pob file build.json` accepts either a native snapshot for the external converter or its prepared version-1 result.
  A prepared result can be imported without installing a converter on the server.
- Inbox files must be simple `.txt`/`.json` filenames, at most 2 MiB, and not file links.
- The summary asks for `Y` within five minutes. `N`, `/pob cancel`, a replacement request or expiry cancels the pending build.
- A confirmed import ends the old session after a short countdown. The Gateway applies the build atomically after
  old-session writers finish; the player re-enters the same character from character selection.

The replacement covers class, level, experience, passives/masteries/tattoos, ascendancy, equipment, weapon swap,
flasks, socketed gems and passive jewels. Quests, waypoints, other inventories, cosmetics and unrelated module state
come from the latest save. Old aura/stance/golem intent is cleared and derived equipment/gem caches are invalidated.
A transactional backup is stored in database metadata under `pob-backup/<character id>/<backup id>`.

Conversion fidelity depends on the separately maintained tool. This bridge does not implement gameplay mechanics
described by a build and does not certify arbitrary external converters or every imported skill/item interaction.
