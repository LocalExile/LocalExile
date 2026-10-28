# PoB import and evryBuild

[evryBuild - POB for LocalExile](https://github.com/evrydayjunglist/evryBuild/tree/feature/localexile-integration) adds Preview and Forge Character to PoB.
This server provides the importer and its optional authenticated HTTP receiver.
Ordinary Community Fork XML/export codes also work through the offline command.
Sharing URLs are not fetched.

## Supported builds

The importer reads the active tree, item set and skill set from a PoE 1 Community Fork build.
It creates unascended characters at levels 1 through 100, using compatible extracted 3.29 passives/masteries,
equipment, flasks, sockets, links and obtainable gems that the native item model can represent.
Retained item modifiers resolve to exact native rows and values; unsupported modifiers are omitted with warnings.
Normal saved `ModRange` records are accepted, including redundant records on fixed-value modifiers.
Saved rolls follow PoB's modifier-list ordering and inactive variant offsets.
Utility-flask rolls use their explicit range tags; unresolved legacy utility-flask rolls omit only that modifier.
Items with unsupported bases, properties, variants or native unique identities are omitted as a whole.
Influence and catalyst metadata can be omitted while retaining the base item and representable modifier values.
Their skill groups are also omitted; no substitute equipment is generated.
Armour bases keep their matched saved defence roll rather than receiving a random replacement.
When PoB's independent defence percentiles require different native rolls, their average selects the one native roll,
and the importer warns that the resulting defences may differ.
Special sockets on accessories/quivers that the server cannot represent omit that item and its skill groups.
Equipped armour uses that roll and its local modifiers before body-armour conversion into Ward.
Equipment requirement exemptions also apply to current gem checks without publishing inactive-item state.
Verified hidden modifiers are restored only for matching supported unique/base identities.

Ascendancy selections and nodes, tree jewels/overrides, unsupported special nodes and named bandit/Pantheon rewards
are skipped with warnings.
The character keeps its base class and has no ascendancy allocation.
Passive nodes without a retained path to that class's start are omitted, along with their mastery selections.
Unsupported mastery effects omit their mastery node; extra passive points are computed from the retained tree.
Unavailable gems leave their sockets empty without shifting other gems or changing links.
Item-granted skill sources and imbued-support groups are omitted with warnings.
Disabled groups/gems are excluded with warnings.
Malformed exports, class/tree mismatches, incompatible tree versions, invalid socket/link layouts, invalid native
item or gem values, excessive passive point budgets and Ruthless imports still fail before creation.
Missing required server data, authentication failures and existing character names also remain errors.
If modifiers were omitted, exported defence/level totals are not forced onto the native item;
its remaining modifiers and saved base roll determine its native values, with a warning when totals differ.
PoB configuration, calculations and campaign progress are not imported.
Characters start in Lioneye's Watch; required supported extra passive points are granted with a warning.
Importing a native modifier does not implement missing gameplay mechanics, including special ES conversion systems.
The importer does not promise that LocalExile's current character calculations match every PoB result.

## Offline preview and creation

Open PowerShell in the server checkout's root, containing `scripts`, `src` and the extracted `data` folder.
Follow [Getting Started](../../GettingStarted.md) first if the server or extracted data is not set up.
In PoB, save the build XML or copy its build-sharing export code into a local text file, for example `build.txt`.
Preview it without opening or writing the save database:

```powershell
.\scripts\dev\import-pob.ps1 -File '.\build.txt' -Preview
```

A successful result has `"ok": true` and `"preview": true`; read its warnings before creating anything.
To create a new character, use an existing local account and an unused character name:

```powershell
$accountName = Read-Host 'Existing LocalExile account name'
$characterName = Read-Host 'New character name'
.\scripts\dev\import-pob.ps1 -File '.\build.txt' -Account $accountName -Name $characterName -League 'Standard'
```

The command builds an isolated Gateway copy under `artifacts/pob-import` and uses the normal configured save database.
It refuses existing database or legacy characters rather than replacing them.
Character metadata and the native save commit together or roll back together.
After success, log out to the login screen and log in again to refresh the character list.

## Connect evryBuild

Use the evryBuild source/package instructions in its
[LocalExile guide](https://github.com/evrydayjunglist/evryBuild/blob/feature/localexile-integration/docs/LocalExile.md).
Its source and the server remain separate repositories, typically in sibling `evryBuild` and `evryPath` folders.

Gateway hosts the optional HTTP listener on its own port, `20496`; game traffic stays on `6112`.
It builds, starts, stops and restarts with Gateway. No fourth game service or extra receiver window is needed.
First complete the normal [Getting Started](../../GettingStarted.md) setup and create a local account.
Open PowerShell in the server checkout's root (the folder containing `src`, `scripts`, `Client` and `data`), then run:

```powershell
if (-not (Test-Path -LiteralPath 'config\pob-server.json')) {
    Copy-Item -LiteralPath 'config\pob-server.example.json' -Destination 'config\pob-server.json'
}
.\scripts\dev\live.ps1 -NoClient
```

The example enables imports on `http://127.0.0.1:20496`.
Success appears in `logs/live-gateway.log` as `[pob] listening on http://127.0.0.1:20496`.
A missing config file, or `"enabled": false`, leaves imports disabled.
Gateway fails startup if an enabled listener cannot bind; check `logs/live-gateway.err` and stop any old standalone receiver.
After changing the config, rebuild/restart Gateway with `.\scripts\dev\live.ps1 -Only Gateway`.

The listener uses `data/` and the same `Game_SAVES_DB` and `Game_LEAGUES` environment settings as the game services.
The defaults are `data/saves.db` and the Standard/Allflame league families.
`Game_GGPK` selects the copied client archive; otherwise it is beside `Game_CLIENT_EXE`, or `Client/Content.ggpk`.
Node.js and `Tools/ooz` use the normal extraction setup from Getting Started; no extra data directory is required.
Set any `Game_*` overrides in the PowerShell window **before** starting the services and issuing tokens.
They must point to the same installation for every service; the receiver cannot select a separate database in JSON.

Only listener policy belongs in `config/pob-server.json`:

| Setting | Meaning |
|---|---|
| `enabled` | Opt into the listener during normal Gateway startup; default false |
| `listenUrl` | Dedicated HTTP(S) origin; default `http://127.0.0.1:20496`, independent of `Game_BIND` |
| `allowedLeagues` | Optional array of full offered league names, such as `["Standard", "SSF Standard"]`; omitted permits all active non-Ruthless leagues |
| `certificatePath` | Certificate for direct HTTPS, relative to the repository root or absolute |
| `certificatePasswordEnvironment` | Environment variable holding the certificate password |

Ruthless imports remain unsupported even when that league is active in the game.
For a remotely managed server, use HTTPS with a valid certificate or a TLS proxy forwarding to loopback.
The proxy must remove any public path prefix before forwarding `/api/pob/...`.
`Game_BIND=0.0.0.0` alone does not expose the import API.
Build submissions cannot select filesystem paths or database locations.

**Migrating the old separate receiver:** stop its PowerShell window with Ctrl+C.
Replace its old config with the small example above, then enable any desired listener policy.
The obsolete `repositoryRoot`, `dataFolder`, `databasePath`, `clientGgpk`, `oozWorker`, `nodeExecutable` and `leagues`
JSON fields are rejected instead of silently importing into a different installation.
Move database/client/league overrides into the normal `Game_*` environment before starting all services.
Existing account-bound tokens keep working when the same database is used.

Issue an import token in that same PowerShell window:

```powershell
$accountName = Read-Host 'Existing LocalExile account name'
.\scripts\dev\pob-server.ps1 -IssueToken -Account $accountName -NoBuild
```

Success prints `"ok":true` and a token; copy only its value into evryBuild, without quotation marks.
The token is a separate import credential, not the game login password.
Issuing another token replaces the previous one; only its digest is stored in the database.
Do not commit tokens, active configuration, accounts or saves.
Token commands work without a running Gateway and reuse its standard build output when present; a missing build is compiled automatically.
For advanced administration only, `pob-server.ps1 -NoBuild` still runs the HTTP listener alone.
Do not run that mode alongside an enabled Gateway listener on the same address.

In evryBuild, open the build, then select **Import/Export Build**, **Forge Character** below Build Sharing.
Enter the Server URL above, local account, Account API token, Standard league and a new character name.
Select **Preview**, read the result, then select **Forge Character** if the supported build is correct.
Preview checks the entered name, offered league and account capacity without writing or reserving a character slot.
It reports active equipment separately from the inactive weapon set, plus omitted item/enabled-gem counts and warnings.
“Ready with omissions” means a partial import: inspect what will be missing before forging.
Changing the build, name or connection details requires another preview in evryBuild.
Warning details are shortened when necessary, with the full distinct-warning count and a truncation flag;
summary counts still cover the entire import. Success JSON is capped below 64 KiB and serialized before any commit.
An ascended PoB build can therefore pass Preview while producing an unascended character with fewer effects or items.
The dialog clears its token after creation or closing; saved connection preferences contain no token.
Log out to the game's login screen and log in again to see the new character.

To revoke the account's import token, use another PowerShell window at the same server root:

```powershell
$accountName = Read-Host 'Existing LocalExile account name'
.\scripts\dev\pob-server.ps1 -RevokeToken -Account $accountName -NoBuild
```

## Receiver contract and validation

`POST /api/pob/preview` validates without creating a character.
`POST /api/pob/characters` creates a new character atomically.
Both accept JSON `{ "code": "...", "account": "...", "name": "...", "league": "Standard" }`
with `Authorization: Bearer TOKEN`; the token must belong to the requested existing account.
There is no browser CORS interface, arbitrary URL fetching or account registration in this receiver.
Unexpected server errors return a log reference; Gateway records the phase, exception type, stack methods and SQLite codes,
without submitted builds, exception messages or tokens.
Requests, XML and decompression are bounded; XML DTDs/entities, duplicate JSON fields and unknown fields are refused.
The default listener is local; remotely managed receivers need administrator-configured HTTPS or a TLS proxy.

Parser, materializer, character-save, token and HTTP regression tests cover accepted and refused inputs.
Cross-project testing also covers the actual evryBuild Lua HTTP path and real zlib/base64 exports against an isolated
receiver: read-only preview, creation, duplicate refusal, account-bound tokens and token-free saved preferences.
Automated checks use temporary databases. A full saved Community build also passed the actual evryBuild Lua/curl path
into Gateway, restoring level 99, 16 items, 30 gems, 100 passives and 9 masteries.
The owner confirmed the imported equipment/gems appear in the native client and movement works.
The issue #75 character-stat branch remains independent; this branch includes only item-state prerequisites.
