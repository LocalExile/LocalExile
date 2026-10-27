# PoB import and evryBuild

[evryBuild - POB for LocalExile](https://github.com/evrydayjunglist/evryBuild) adds Preview and Forge Character to PoB.
This server provides the importer and its optional authenticated HTTP receiver.
Ordinary Community Fork XML/export codes also work through the offline command.
Sharing URLs are not fetched.

## Supported builds

The importer reads the active tree, item set and skill set from a PoE 1 Community Fork build.
It supports unascended classes, levels 1 through 100 and the extracted 3.29 passive tree/masteries, equipment,
flasks, sockets, links and obtainable gems that the native item model can represent.
Item modifiers must resolve to exact native rows and values; unsupported properties are refused.
Armour bases keep their matched saved defence roll rather than receiving a random replacement.
Verified hidden modifiers are restored only for matching supported unique/base identities.

Ascendancies, tree jewels/overrides, unsupported special nodes, named bandit/Pantheon rewards, Ruthless imports,
item-granted skill sources, imbued supports and incompatible gem/link layouts are refused.
Disabled groups/gems are excluded with warnings.
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
