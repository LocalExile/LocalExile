# Getting Started

This guide takes a fresh clone to "in game in Lioneye's Watch, able to walk and take The Coast exit". Each step says
what success looks like and what to do when it fails. It is written to be followed by a person or by an AI agent.

## Rules (read first, especially if you are an AI agent)

1. **Never start the client any other way than `scripts/dev/live.ps1`.** `live.ps1` starts it with
   `--patching-server 127.0.0.1:12995`. Started any other way (double-click, Steam, launcher, a second instance), the
   client contacts the official patch servers. It may then update itself, which overwrites the patched executable and
   the game data.
2. **Run only one client at a time.** To bring a running client to the front, restore its window; do not launch it
   again.
3. **Run every command from the repository root.** The tools read and write `data/` and `Client/` relative to the
   current directory.
4. **Never commit `data/` or `Client/`.** They hold your keys, accounts and the game. Both are git-ignored; keep it
   that way. The one exception is `data/schema/`, the community table schema, which is tracked.
5. **Run `live.ps1` directly** (`.\scripts\dev\live.ps1` in a PowerShell session). Do not wrap it in a pipe or
   capture its output (for example `powershell -File live.ps1 | Out-String`). The servers it starts keep the pipe
   open, so the call never returns. Read `logs\` instead.
6. **The client must be version 3.29.3b.** The login patch and several protocol constants are specific to that build.
   Any other version fails at step 4 or 5, with a clear error.

## What you need

| Requirement | Why | Check |
|---|---|---|
| Windows 10/11 x64 | the client and the scripts are Windows-only | |
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | builds and runs the servers | `dotnet --version` prints `10.x` |
| PowerShell 5.1+ | runs `live.ps1` | `$PSVersionTable.PSVersion` |
| Path of Exile **3.29.3b**, standalone install | the client you play with | a folder with `PathOfExile.exe` and `Content.ggpk` |
| [Node.js](https://nodejs.org) (Node 24) + `npm install` in `Tools\ooz` (npm package `ooz-wasm` 2.x) | decompresses the game's bundles for the data extraction (step 7) | `node --version`; `Tools\ooz\node_modules\ooz-wasm` exists |
| Python 3.9+ | the data extraction (step 7); with `pip install frida`, walk maps (step 9, optional) | `python --version`; `python -c "import frida"` |
| ~same free space as the client | you work on a copy of it | |

**Steam installs do not work as is.** The Steam layout has no `Content.ggpk`, and its executables have other names.
Use the standalone layout.

**Getting 3.29.3b:** this repository cannot provide the client. The official launcher always updates to the newest
version, so you need a copy that is still at 3.29.3b. Never let a newer launcher touch your copy.

### Third-party tools (only to extend the server)

Playing needs nothing beyond the table above (step 7 extracts the game data with Node and Python). To work out more of the protocol, as
[docs/re/tooling.md](docs/re/tooling.md) describes, you also need these. Apart from the dat-schema file, none of them are in the repository; install
them yourself. Programs go under `Tools\` at the repository root (git-ignored), where the scripts look for them.

| Tool | Version we used | Where it goes | Used for |
|---|---|---|---|
| [Ghidra](https://github.com/NationalSecurityAgency/ghidra/releases) | 12.1.4 (`ghidra_12.1.4_PUBLIC_20260921`) | `Tools\ghidra_12.1.4_PUBLIC_20260921\ghidra_12.1.4_PUBLIC\` | static analysis: decompiling, references, the Java scripts in `scripts/re/ghidra/` |
| [Microsoft Build of OpenJDK](https://learn.microsoft.com/java/openjdk/download) | 21 (21.0.12.1) | `Tools\jdk-21\` (so `Tools\jdk-21\bin\java.exe` exists) | runs Ghidra |
| [Frida](https://frida.re) | Python package `frida` | `pip install frida` (or `.venv`, step 9) | live tracing of the client: `scripts/re/frida/*.js`, walk maps |
| REA (reverse-engineering MCP server), npm package `rea-agents` | 6.5.0 | `npm install -g rea-agents` (needs Node.js, below); check with `rea --version` | lets an AI agent drive Ghidra and Frida; see `.mcp.json` below |
| [Git for Windows](https://git-scm.com/download/win) (Git Bash) | any | `bash` on PATH | `scripts/re/ghidra/g.sh`. Without it, use the PowerShell runner `scripts/re/ghidra/run.ps1` |
| Python packages `pefile`, `capstone` | 2024.8.26, 5.0.7 | `pip install pefile capstone` | the helpers in `scripts/re/*.py` (disassembly, string and vtable search) |
| [Node.js](https://nodejs.org) + npm package `ooz-wasm` | Node 24, ooz-wasm 2.x | Node on PATH; then `cd Tools\ooz; npm install` (also needed to play, see What you need) | Oodle decompression of the game's bundles, for `scripts/re/ggdump`, `scripts/re/atlas` and `Tools/PoEDataExtractor` |
| [dat-schema](https://github.com/poe-tool-dev/dat-schema) `schema.min.json` | release matching 3.29 | already in the repository: `data\schema\schema.min.json` (MIT, see `data\schema\LICENSE`) | column layouts of the game's `.datc64` tables, for `Tools/PoEDataExtractor` |
| LibGGPK3 (NuGet) | 2.7.5 | restored by `dotnet build` | reads `Content.ggpk` and its bundles |

One-time Ghidra setup, after Ghidra, the JDK and `Client\Game.exe` are in place (takes hours; the analysis of the 79 MB
executable is large):

```powershell
.\scripts\re\ghidra\import.ps1
```

It creates `Tools\ghidra-projects\poe3293`, which `scripts/re/ghidra/g.sh` and `run.ps1` open read-only.

For an AI agent, register REA as an MCP server in `.mcp.json` at the repository root (git-ignored, because the paths are
specific to your PC):

```json
{
  "mcpServers": {
    "rea": {
      "type": "stdio",
      "command": "cmd",
      "args": ["/c", "rea", "--mcp"],
      "env": {
        "GHIDRA_INSTALL_DIR": "<repo>\\Tools\\ghidra_12.1.4_PUBLIC_20260921\\ghidra_12.1.4_PUBLIC",
        "JAVA_HOME": "<repo>\\Tools\\jdk-21",
        "REA_ANALYSIS_PROVIDER": "ghidra",
        "REA_GHIDRA_STARTUP_TIMEOUT_MS": "3600000"
      }
    }
  }
}
```

Never put these tools, their outputs (`Tools\re\out`, the Ghidra project) or game files in a commit. The `.gitignore`
already keeps them out.

## 1. Clone and build

```powershell
git clone <this repository URL> LocalExile
cd LocalExile
dotnet build GameServer.slnx
dotnet test tests/GameServer.Core.Tests
dotnet test tests/GameServer.Data.Tests
dotnet test tests/GameServer.Gateway.Tests
```

Success: `Build succeeded` with 0 warnings, and every test run ends with `Passed!`. The tests that need the real client
quietly pass when there is no `Client/` yet.

## 2. Allow the scripts to run

```powershell
Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
```

If you would rather not change the policy, run the script as
`powershell -ExecutionPolicy Bypass -File scripts\dev\live.ps1` wherever this guide says `.\scripts\dev\live.ps1`.

## 3. Copy the client into `Client/`

Copy the **whole** client folder into `Client\` at the repository root. Work on a copy, because steps 5 and 6 modify it.
Then give the main executable the name the tools use:

```powershell
robocopy "D:\Games\Path of Exile" Client /E      # your install path
Copy-Item Client\PathOfExile.exe Client\Game.exe
```

Success: `Client\Game.exe` (about 79 MB) and `Client\Content.ggpk` exist.

- `robocopy` exit codes below 8 mean success (1 = copied, 3 = copied plus extra files). Copying ~70 GB takes a few
  minutes.
- Close the client before copying. A running client locks `Content.ggpk`.

### Optional external client folder

The default remains `<repository>\Client`. To use another installation, create `client.local.json` in the repository
root (beside `GameServer.slnx`). This file is ignored by Git. For example:

```json
{
  "clientDirectory": "F:\\poe\\Client"
}
```

Move the whole client directory, including `config.json`, while the game and local servers are closed.
The configured directory must contain `Game.exe`, `Game.patched.exe` and `Content.ggpk`.
Relative paths resolve from the repository root. An optional `Game_CLIENT_DIR` environment variable takes precedence
over the JSON file. Without either setting, the tools continue to use the repository's `Client` folder.
Malformed local configuration stops the tools with an error rather than silently selecting another client.

`live.ps1`, `extract-data.ps1` and the native client-control bridge use this location. The launcher passes resolved
`Game_CLIENT_EXE` and `Game_GGPK` paths to its servers and restores the caller's environment when it exits.
Existing explicit `Game_CLIENT_EXE` and `Game_GGPK` overrides retain precedence for their individual files.
Extraction passes the same archive to .NET and Python generators.
Generated server data stays under repository `data/` unless a separate runtime folder is configured below.
Standalone server commands do not read `client.local.json`; use the existing file environment overrides for those.
Other manual RE commands in this guide still need explicit external paths where their examples say `Client/...`.

The client is launched with its own directory as its working directory, so its `config.json` continues to select
its existing Documents profile. Moving the game directory does not move or replace that profile.
Start it normally from a PowerShell terminal in the repository root:

```powershell
.\scripts\dev\live.ps1
```

### Optional separate build folder

Add `buildDirectory` to the same ignored `client.local.json` to keep compiled programs and intermediate files
outside the source checkout.
For example, this layout puts the client and build output beside the repository:

```json
{
  "clientDirectory": "../Client",
  "buildDirectory": "../build"
}
```

All folder settings are optional and resolve relative to the repository root, including paths containing spaces.
`Game_BUILD_DIR` overrides `buildDirectory`, just as `Game_CLIENT_DIR` overrides `clientDirectory`.
Without a build setting, the usual per-project `bin/Debug/net10.0` and `obj/` directories remain in use.
The launcher and extraction tools build Debug with .NET's `--artifacts-path` option when configured.
They execute the matching `bin/<project>/debug/<project>.dll`, including the gateway selector and Python's GGPK helper.
They never fall back to an older DLL in the source tree.

Run the launcher from the source repository as above.
Servers and extractors use the repository as their working directory unless `runtimeDirectory` is configured below.
The native client keeps its own working directory and Documents profile.
`-Only Gateway` rebuilds and restarts only Gateway; `-NoBuild` uses existing DLLs in the selected layout.
A missing DLL with `-NoBuild` stops startup before the launcher stops servers or clears their logs.
`-ClientOnly` requires an already-built Patch DLL in the same layout and running local servers.

Manual `dotnet` commands do not read the JSON file.
For a full build and tests using the example layout, open PowerShell in the repository root and run:

```powershell
dotnet build GameServer.slnx --artifacts-path ../build
dotnet test tests/GameServer.Core.Tests --artifacts-path ../build
dotnet test tests/GameServer.Data.Tests --artifacts-path ../build
dotnet test tests/GameServer.Gateway.Tests --artifacts-path ../build
```

Only `bin/` and `obj/` are disposable when the build folder also holds runtime data.

### Optional separate runtime folder

Use `runtimeDirectory` to keep the source checkout for code, documentation, and the tracked data schema.
For example:

```json
{
  "clientDirectory": "../Client",
  "runtimeDirectory": "../build"
}
```

The launcher builds into that runtime folder automatically unless an explicit `buildDirectory` or `Game_BUILD_DIR`
selects another build folder.
`Game_RUNTIME_DIR` overrides `runtimeDirectory`.
Without a runtime setting, the original repository-relative layout is unchanged.
The launcher creates runtime `data/`, `logs/` and extraction-cache folders on startup.
Extraction installs its pinned Node dependency there if missing, while its worker and schema stay in the source tree.
The first `control.ps1` or `live.ps1 -Walkmaps` call creates runtime `.venv/` and installs its pinned Frida package.
Later calls reuse that environment; package downloads happen only when a required dependency is missing.
Python helper processes suppress bytecode files in the source tree.
`scripts/dev/bugreport.ps1` and `scripts/re/levelgen/lg.ps1` use the same configured folders.
The runtime folder must be separate from the checkout and must not belong to another checkout.

An existing installation needs one migration.
Close the game window first, then stop this checkout's local services and migrate:

```powershell
.\scripts\dev\stop.ps1
.\scripts\dev\setup-runtime.ps1 -Migrate
.\scripts\dev\live.ps1
```

Migration refuses running client/server processes and preexisting destination files.
It copies and verifies every runtime file by SHA256 before moving originals into a separate local backup beside
the source checkout; the receipt records the backup and verification details.
Saves, keys, generated tables, walk maps, logs, extraction packages and the active metadata cache are preserved.
Obsolete per-project build folders are also preserved in the backup.
Tracked `data/schema/` stays in source, and historical RE installations/artifacts are not moved.
Later startup is idempotent and never repeats migration.
If runtime data reappears in the source checkout, startup stops so that two databases cannot silently diverge.

**Keep runtime `data/` when cleaning builds.**
It contains saves, accounts and login keys.
Delete or rebuild only `bin/` and `obj/`; do not delete the entire runtime folder.

Use `scripts/dev/run.ps1` for individual server or maintenance commands so they use the configured working directory:

```powershell
.\scripts\dev\run.ps1 Patch gateway
.\scripts\dev\run.ps1 Host
```

Direct `dotnet run` commands do not read the local path configuration.
For initial key generation and executable patching with a separate runtime, use the configured client paths:

```powershell
. .\scripts\dev\client-path.ps1
$client = Get-ClientPaths -RepositoryRoot (Get-Location).Path
.\scripts\dev\run.ps1 Auth keygen $client.Executable
.\scripts\dev\run.ps1 Auth patch $client.Executable $client.PatchedExecutable
```

Gateway tests use the same runtime selection through their shared test initializer.
Production catalogs continue to read relative `data/` paths from the selected working directory.
The native client's directory and Documents profile remain separate.

To inspect extraction freshness, or rerun just the read-only graph analysis:

```powershell
.\scripts\dev\extract-data.ps1 -List
.\scripts\dev\extract-data.ps1 -Only passive-graphs
```

## 4. Generate your own login keys

```powershell
dotnet run --project src/GameServer.Auth -- keygen Client/Game.exe
```

Success: `[auth] wrote data/login-keys.json`.

This creates a new server signing key over the client's own DSA parameters. It is yours alone: never share or commit
it.

| Problem | Meaning |
|---|---|
| `data/login-keys.json exists` | keys already made; keep them (the patched client from step 5 trusts exactly these keys) |
| `login verify key not found in client image` | `Client\Game.exe` is not the 3.29.3b `PathOfExile.exe` |

## 5. Patch the client to trust your keys

```powershell
dotnet run --project src/GameServer.Auth -- patch Client/Game.exe Client/Game.patched.exe
```

Success: `[auth] login key blobs at file offset 0x...` then `[auth] wrote Client/Game.patched.exe`.

- If you regenerate the keys (by deleting `data/login-keys.json` and running step 4 again), run this step again too.
- Antivirus software may flag or quarantine the patched executable, because it is a modified binary. If
  `Game.patched.exe` disappears, allow it in your antivirus.

## 6. Point the client's gateway list at your login server

```powershell
dotnet run --project src/GameServer.Patch -- gateway --login 127.0.0.1:20493
```

Success: `[patch] gateway_list.txt now points at 127.0.0.1:20493 (original saved to data/gateway_list.original.txt)`.
The lines printed after it all contain `127.0.0.1:20493`.

- The client must be closed (the GGPK is locked while it runs).
- To undo: `dotnet run --project src/GameServer.Patch -- gateway --restore`.
- `no gateway_list.txt in the GGPK` means `Client\Content.ggpk` is missing or not a standalone client's.

## 7. Extract the game data

The Gateway reads the game's tables, object types, hideouts and more from files extracted from `Client\Content.ggpk`
into `data\` (all git-ignored: they are game data). One script makes all of them:

```powershell
.\scripts\dev\extract-data.ps1
```

`live.ps1` (step 10) runs it on every start, so you may skip this and let `live.ps1` do it. The first run takes a few
minutes; later runs only check stamps and finish at once. It reads the GGPK and never writes it, and does not need the
client closed.

Success: one `extracting <name> ...` and `done in N s` per file, then `game data extracted: tables, object-types, ...`.
A second run prints `game data up to date`.

| File | Feature without it |
|---|---|
| `data\tables\*.json` (about 940, report in `data\extract-report.json`) | almost everything: stats, areas, waypoints, items, monsters, NPCs |
| `data\object-types.json` | monsters, NPCs, area transitions |
| `data\item-types.json` | items (characters start with none, `/item` is off) |
| `data\area-transitions.json` | the area transitions of fixed layouts |
| `data\room-points.json` | unique bosses |
| `data\hideouts.json` | hideouts |
| `data\animation-contacts.json` | each class's spell timing and where its arrows and wand bolts leave |
| `data\monster-contacts.json` | when and from where monster projectiles leave (the Sand Spitter's mortar) |
| `data\projectile-clips.json` | the flight and impact clips of projectile objects (monster mortars are invisible without them) |

How it decides: the manifest at the top of `scripts\dev\extract-data.ps1` gives each file its command and a version
number. The script runs every entry whose file is missing or whose stamp (`data\extract-stamps.json`) has another
version, and the entries that need one that ran (everything needs the tables). A pull that changes a file's format
bumps its version, and your next run regenerates it. Options: `-Force` (everything), `-Only hideouts,room-points`
(just these), `-List` (each entry's state; runs nothing). Each run's output is in `logs\extract-<name>.log`. It
builds each .NET tool once (`logs\extract-build-<project>.log`) and runs it with `dotnet run --no-build`.

| Problem | Meaning |
|---|---|
| `setup incomplete: Node.js not found` / `Tools\ooz\node_modules missing` | install Node, then `cd Tools\ooz; npm install` |
| `setup incomplete: Python 3.9+ ...` | install Python from python.org (see step 9 for the Store alias) |
| `extract-data: <name> failed` | read `logs\extract-<name>.log`; the tables report `schema mismatch: 0` when the client is 3.29.3b |
| `extract-data: building <project> failed` | read `logs\extract-build-<project>.log`; a build error there is a code problem in that tool, not in the game data |

## 8. Create an account and a character

Start the dev account service in one terminal:

```powershell
dotnet run --project src/GameServer.Host
```

Success: `[host] listening on 127.0.0.1:7000, db=data\saves.db`.

In a second terminal:

```powershell
dotnet run --project src/GameServer.DevClient
```

Then type, one line at a time:

```
register name=myaccount password=choose-a-password
login name=myaccount password=choose-a-password
create_character name=MyWitch class=witch
characters
```

Every reply should contain `"ok":true`; the last one lists your character.

- Account and character names: 3–32 letters, digits, `_` or `-`. Passwords: at least 4 characters.
- Piping the commands instead of typing them works too:
  `"register name=a password=b`n...`n" | dotnet run --project src/GameServer.DevClient`. The client ignores the
  byte-order mark PowerShell adds.
- The game's login screen may show a previously used account name in the Email field (the client remembers it per
  Windows user). Replace it with yours.
- Only the Witch is supported for now. Other classes can be created, but in game you appear as a Witch (with your
  character's name).
- Then close both: an empty line quits the dev client, and Ctrl+C stops the host. You can keep `GameServer.Host`
  running while you play: it and the login server share the accounts in `data/saves.db`, and neither caches them.

## 9. (Optional) Walk maps

The Gateway needs each area's layout: which cells are walkable, the area transitions, the rooms with monster points.
For every area its level generator port reproduces (the supported areas, [re/011](docs/re/011-level-generation.md)),
it generates the layout itself from the seed: nothing to capture, and nothing is written to disk. Any other area needs
a capture from your own client. This step is optional and only for those areas or for working on the generator; by
default nothing is recorded.

- **Without captures:** you can play every supported area. An area that is not supported tells you so in game; its
  layout is unknown to the server (you may spawn somewhere odd, and it has no area transitions).
- **With captures:** `live.ps1 -Walkmaps` attaches a small Frida script to the client, which saves each area's walk
  map, its area transitions (`<area>_seed<seed>.transitions.json`), the doodads and rooms it places (from which the
  Gateway finds the rooms with monster points) and its generation trace into `data/walkability/` the first time the
  client generates it. The Gateway picks them up during that same load. Only with `-Walkmaps` does the Gateway write
  anything per load (`current.json`, which tells the recorder the area) and accept `/tour`.

```powershell
python -m pip install frida
python -c "import frida; print(frida.__version__)"
```

Or keep Frida in a virtual environment in the repository; `live.ps1` uses `.venv` automatically when it exists
(`.venv` is git-ignored):

```powershell
python -m venv .venv
.venv\Scripts\python.exe -m pip install frida
```

If `python` opens the Microsoft Store, the Windows app alias is in the way. Install Python from python.org, or turn
the alias off under Settings → Apps → App execution aliases.

Captured maps are saved as `data/walkability/<area>_seed<seed>.bin` and `.json` (git-ignored).

## 10. Play

```powershell
.\scripts\dev\live.ps1            # add -Walkmaps if you did step 9
```

Success, in order:
1. The script prints `client pid ...`, then `servers up`. With `-Walkmaps`, it first prints
   `walk-map capture on (logs\walkmaps.log)`.
2. The client window opens and loads for about a minute.
3. The login screen appears. Type your account name in the **Email** field and your password. Click the password
   field before typing; it needs focus.
4. Click **Log In**, select your character, and click **Play**.
5. You arrive in Lioneye's Watch. Click the ground to walk.

**Set Network Mode to Predictive once** (Esc → Options → Game → Network Mode), then log out to the character screen
and click Play again. The server supports only Predictive. With the default "auto", the client picks lockstep on
localhost: your character is not drawn and does not move, and the server shows a message box saying so. The setting is
saved in `Documents\My Games\Path of Exile\production_Config.ini` (`networking_type=predictive`), which your normal
client shares.

**League modes:** the existing creation screen's Hardcore, SSF and Ruthless choices select server rules.
Selected modes change server rules; Hardcore Ruthless death is permanent.
Ruthless balance can be edited in `config/ruthless.json` without rebuilding or restarting the server.
That active file is local and git-ignored; `config/ruthless.example.json` holds the tracked defaults.
To create a missing local copy and edit it, run from the repository root:

```powershell
if (!(Test-Path .\config\ruthless.json)) { Copy-Item .\config\ruthless.example.json .\config\ruthless.json }
notepad .\config\ruthless.json
```

See [league modes](docs/game/league-modes.md) for exact behavior, limitations and editing instructions.

To check the result from the logs (useful for agents):
- `logs\live-gateway.log` has `create PlayerObject { Name = <your character>`.
- With `-Walkmaps`:
  - `logs\walkmaps.log` shows `saved 1_1_town_seed1.bin (690x414)`;
  - after taking The Coast exit, it also shows `saved 1_1_2_seed1.bin (1725x1725)`;
  - the Gateway log shows `walk map captured` for each area, and the player's `GridX`/`GridY` on The Coast is on land
    (for seed 1, about (1278, 1304)).
- A line `ignored a grid with unexpected size ... 'w': 0` in `walkmaps.log` is normal: it comes from the login
  screen.

In game:
- **The Coast:** click "The Coast" on the east side of town. Ctrl+click it to open the Instance Manager instead (pick
  an instance or create a new one).
- **Commands:** type in chat with Enter, for example `/commands` (or `/com`), `/set_speed 200`.

Logs are in `logs\` (`live-auth.log`, `live-gateway.log`, `live-patch.log`, and `walkmaps.log`), plus the client's own
`Client\logs\Client.txt`. Every line starts with the time; the previous run's logs are in `logs\archive\`, and each
connection's last packets in `logs\packets\`. `Game_TRACE` adds dev traces, and `.\scripts\dev\bugreport.ps1` zips it
all for an issue ([debugging](docs/dev/debugging.md)).

`live.ps1` options:
- `-NoClient` restarts the servers but keeps the running client.
- `-Only Gateway` (or `Auth`, `Patch`, comma-separated) rebuilds and restarts only those servers; the client and the
  other servers keep running. After a Gateway change this is all you need: the client drops to the login screen, log in
  and Play again.
- `-NoBuild` skips the builds.
- `-NoExtract` skips the game-data check (step 7).

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `setup incomplete: ...` from `live.ps1` | a one-time step is missing | do the step named in the message |
| A feature is off (no hideouts, no items, no unique boss); the Gateway log starts with `WARNING: ... game data file(s) missing` | a game data file was not extracted, or `live.ps1 -NoExtract` skipped it | run `.\scripts\dev\extract-data.ps1` (or `-List` to see what is stale), then `live.ps1 -Only Gateway` |
| `live.ps1` stops at `extracting <name> ...` | the entry is still working (area-transitions takes ~10 min), or, before #93, `Start-Process -Wait` waited on the MSBuild worker node `dotnet run` left behind | check that `logs\extract-<name>.log` still grows; on an older checkout, pull, or set `MSBUILDDISABLENODEREUSE=1` and `DOTNET_CLI_USE_MSBUILD_SERVER=0` before `live.ps1` |
| `port 8080 is used by <program>` | another program (often a web server) holds a dev port | close it, or set `Game_PATCH_URL=http://127.0.0.1:<port>/` |
| `patch server did not start` | `Client\Content.ggpk` missing, locked or unreadable | close every client, check the path |
| Client shows a patching/update screen or downloads | started without `live.ps1`, or the patch server is not running | close it immediately; start only through `live.ps1` |
| "The password is not correct for this account" | wrong password, or an account created after the login server started | check the password; restart with `live.ps1` (it reloads accounts) |
| "Account does not exist" | the name in the Email field does not match `register name=` | use the registered name |
| Login hangs, no "Connected to login server" | gateway list not patched (step 6), or the login server is not running | redo step 6; check `logs\live-auth.log` |
| A new character has no items, no weapon on the ground (`start kit is empty` in `live-gateway.log`) | it first logged in before `data/tables` and `data/item-types.json` existed, and that empty kit was saved | extract them (see "Third-party tools"), stop the Gateway, move `data\saves\<character>.json` away, run `live.ps1` again |
| Character select shows no characters | the character was created after the login server started | run `live.ps1` again |
| `dotnet build` / `dotnet test` fails with `MSB3027` ... `locked by: ".NET Host"` | the servers `live.ps1` started are running from the same `bin\` folders | let `live.ps1` rebuild them (`-Only Gateway` …), or build and test elsewhere with a short path: `dotnet test tests/GameServer.Gateway.Tests --artifacts-path C:\Temp\lx` |
| In town, but your character is invisible and clicks do nothing | Network Mode is lockstep ("auto" picks it on localhost); the Gateway log says `lockstep movement` | Options → Game → Network Mode → Predictive, then log out to the character screen and Play again |
| Stuck after Play (no area) | `Game_GATEWAY_ENTER` was set to something other than `1` | unset it; `live.ps1` sets it to `1` |
| `HNC mask not found` in `live-gateway.err` | `Client\Game.exe` is not 3.29.3b | use the right client version |
| Spawn in the sea or off the map | an area the server does not generate, with no walk map yet | use `-Walkmaps` (step 9) and enter the area again |
| `walkmaps: permission denied` | client and Python run at different privilege levels | run both as the same user, not one as administrator |
| Esc → "Exit to character selection" hangs | not implemented yet | run `.\scripts\dev\live.ps1 -NoClient` (the client drops to the login screen) |
| The client closes or crashes by itself | usually a message the server sent wrong or left out | read the `DISCONNECT` line and the `last … packet(s)` block above it in `live-gateway.log`; attach `scripts/re/frida/trace-crash.js` and reproduce; report it with `scripts\dev\bugreport.ps1` ([debugging](docs/dev/debugging.md)) |
| Windows Firewall prompt | should not happen (servers bind to 127.0.0.1) | if it does, deny: nothing needs outside access |

## Configuration

Environment variables, read when each server starts:

| Variable | Default | Meaning |
|---|---|---|
| `Game_GATEWAY_ENTER` | `1` (set by `live.ps1`) | `1` = enter the area after creating the player |
| `Game_BIND` | `127.0.0.1` | address Auth and Gateway listen on |
| `Game_LOGIN_PORT` | 20493 | login server port (must match step 6) |
| `Game_GATEWAY_PORT` | 6112 | instance server port |
| `Game_GATEWAY_HOST` | 127.0.0.1 | address sent to the client for area changes |
| `Game_AUTH_CONTROL_PORT` | 20495 | Auth ↔ Gateway control port (localhost only) |
| `Game_CLIENT_EXE` | `Client/Game.exe` | client executable the Gateway reads protocol constants from |
| `Game_RANDOM_SEEDS` | unset | new instances of the areas the Gateway generates itself (`gate-baseline.json`, [re/011](docs/re/011-level-generation.md)) get random seeds, every other area seed 1; `0` = seed 1 everywhere |
| `Game_WALKMAPS` | unset | set by `live.ps1 -Walkmaps`: the Gateway announces each area load to the walk-map recorder and runs capture tours (`/tour`); unset, it records nothing |
| `Game_SEED` | unset | a number = that seed for every new non-town, non-hideout instance (generation traces of one seed with `live.ps1 -Walkmaps`; overrides `Game_RANDOM_SEEDS`) |
| `Game_LEAGUE` | `standard` | `hardcore` shows the hardcore death screen (a single Exit button) |
| `Game_LEAGUES` | `Standard,Allflame` | base leagues; each offers its Hardcore/SSF/Ruthless combinations |
| `Game_RUTHLESS_RULES` | `config/ruthless.json` | Ruthless tuning file; valid edits apply during play |
| `Game_START_WEAPON` | unset | `ground` = a new character's class weapon waits on the ground near the spawn instead of being worn |
| `Game_FLASK_BUFFS` | unset | `0` = flasks recover without putting their flask_effect buff on the player |
| `Game_FLASK_CHARGES` | unset | unset = the game's flask charge rules (kills by rarity, charge mods, a flask put on the bar in a hostile area starts empty); `full` = every kill fills all flasks and equipped flasks keep their charges |
| `Game_PLAYER_ATTACK` | `PlayerMelee` | GrantedEffects `Id` of the player's default attack (level 1) |
| `Game_MOVE_MODE` | `echo` | `echo`, `move` or `teleport` |
| `Game_WALK_SPEED` | 37 | walk speed in cells per second, used for position estimates |
| `Game_DEV_DB` | `data/accounts.json` | an account store from before the save database, imported once |
| `Game_SAVES_DB` | `data/saves.db` | character saves and stashes (Gateway, Auth) |
| `Game_INSTANCE_SECRET` | `data/instance-secret.bin` | instance key secret (created on first start) |
| `Game_PATCH_URL` | `http://127.0.0.1:8080/` | patch web server address |
| `Game_TRACE` | unset | dev traces, a comma list: `move`, `latency`, `ticks`, `ai`, `packets`, or `all` ([debugging](docs/dev/debugging.md)) |
| `Game_PACKET_DUMPS` | `logs\packets` (given by `live.ps1` to the servers it starts) | folder for each connection's last packets, written when it closes; `0` = none |
| `Game_LOG_TIMESTAMPS` | unset | `0` = no time at the start of each log line |

## Files you will have afterwards

| Path | What | Share? |
|---|---|---|
| `Client\` | your client copy (gateway list patched) and `Game.patched.exe` | never |
| `data\login-keys.json` | your login server keys | never |
| `data\instance-secret.bin` | instance key secret | never |
| `data\accounts.json.imported` | the account store from before the save database, kept after its import | never |
| `data\saves.db` (with `-wal`, `-shm` while the servers run) | accounts, characters, their saves and stashes ([saves](docs/saves.md)) | never |
| `data\gateway_list.original.txt` | backup for `gateway --restore` | no need |
| `data\tables\`, `data\*-types.json`, `data\hideouts.json` … | extracted game data (step 7) and their stamps, `data\extract-stamps.json` | no (game data) |
| `data\walkability\` | captured walk maps | no (derived from game data) |
| `logs\` | server logs | only when reporting a bug, and read them first |

## Start over

1. Close the client.
2. Run `dotnet run --project src/GameServer.Patch -- gateway --restore`.
3. Delete `data\` and `Client\Game.patched.exe`.
4. Continue from step 4.
