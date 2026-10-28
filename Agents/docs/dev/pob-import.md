# PoB import and evryBuild

[evryBuild - POB for LocalExile](https://github.com/evrydayjunglist/evryBuild) adds Preview and Forge Character to PoB.
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
[LocalExile guide](https://github.com/evrydayjunglist/evryBuild/blob/master/docs/LocalExile.md).
Its source and the server remain separate repositories, typically in sibling `evryBuild` and `evryPath` folders.

The receiver runs separately from normal game services and needs an existing local account/save database.
In PowerShell at the server root, issue an account-bound import token:

```powershell
$accountName = Read-Host 'Existing LocalExile account name'
.\scripts\dev\pob-server.ps1 -IssueToken -Account $accountName
```

The first invocation creates local `config/pob-server.json` from its tracked example and builds the receiver.
Success prints `"ok":true` and a token; copy only its value into evryBuild, without quotation marks.
The token is a separate import credential, not the game login password.
Issuing another token replaces the previous one; only its digest is stored in the database.
Do not commit tokens, active configuration, accounts or saves.

Start the receiver in the same server-root PowerShell window:

```powershell
.\scripts\dev\pob-server.ps1 -NoBuild
```

Keep that window running; Ctrl+C stops the receiver.
It listens at `http://127.0.0.1:20496` by default and permits Standard imports.
To configure other offered league names or server paths, open `config/pob-server.json` in a text editor first.
Its relative paths resolve against `repositoryRoot`; defaults use this checkout's `data` and client archive.
Build submissions cannot supply filesystem paths or database locations.

In evryBuild, open the build, then select **Import/Export Build**, **Forge Character** below Build Sharing.
Enter the Server URL above, local account, Account API token, Standard league and a new character name.
Select **Preview**, read the result, then select **Forge Character** if the supported build is correct.
Preview and Forge report the omitted features in their warnings.
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
Requests, XML and decompression are bounded; XML DTDs/entities, duplicate JSON fields and unknown fields are refused.
The default listener is local; remotely managed receivers need administrator-configured HTTPS or a TLS proxy.

Parser, materializer, character-save, token and HTTP regression tests cover accepted and refused inputs.
Cross-project testing also covers the actual evryBuild Lua HTTP path and real zlib/base64 exports against an isolated
receiver: read-only preview, creation, duplicate refusal, account-bound tokens and token-free saved preferences.
Those checks use synthetic build fields and a temporary database; native GUI and in-game validation remain separate.
The issue #75 character-stat branch remains independent; this branch includes only item-state prerequisites.
