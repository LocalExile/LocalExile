# Getting Started

This guide takes a fresh clone to "in game in Lioneye's Watch, able to walk and take The Coast exit". Each step says
what success looks like and what to do when it fails. It is written to be followed by a person or by an AI agent.
To play without developing, a [release](https://github.com/LocalExile/LocalExile/releases) needs none of this: run its
`LocalExile.exe` ([releases](docs/dev/releases.md)).

## Rules (read first, especially if you are an AI agent)

1. **Never start the client any other way than `scripts/dev/live.ps1`.** `live.ps1` starts it with
   `--patching-server 127.0.0.1:12995`. Started any other way (double-click, Steam, launcher, a second instance), the
   client contacts the official patch servers. It may then update itself, which overwrites the patched executable and
   the game data.
2. **Run only the clients `live.ps1` starts: one, or more as named sessions.** To bring a running client to the
   front, restore its window; do not launch it again. Several clients (or several checkouts' servers) at once need
   `live.ps1 -Session` and `-PortBase` ([live testing](docs/dev/live-testing.md#several-stacks-and-clients)).
3. **Run every command from the repository root.** The tools read and write `data/` and `Client/` relative to the
   current directory.
4. **Never commit `data/` or `Client/`.** They hold your keys, accounts and the game. Both are git-ignored; keep it
   that way. The one exception is `data/schema/`, the community table schema, which is tracked.
5. **Run `live.ps1` directly** (`.\scripts\dev\live.ps1` in a PowerShell session). Do not wrap it in a pipe or
   capture its output (for example `powershell -File live.ps1 | Out-String`). The servers it starts keep the pipe
   open, so the call never returns. Read `logs\` instead.
6. **The client must be an allowed build: 3.29.3c or 3.29.3d.** `src/GameServer.Core/Protocol/ClientBuilds.cs` lists
   them by the hash of the executable; the login patch and several protocol constants are specific to them.
   Any other version fails at step 4 or 5, with an error that names the builds that are allowed.

## What you need

| Requirement | Why | Check |
|---|---|---|
| Windows 10/11 x64, or Linux ([below](#linux)) | the steps below are written for Windows | |
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | builds and runs the servers | `dotnet --version` prints `10.x` |
| PowerShell 5.1+ | runs `live.ps1` | `$PSVersionTable.PSVersion` |
| Path of Exile **3.29.3c** or **3.29.3d**, standalone or Steam install | the client you play with | standalone: a folder with `PathOfExile.exe` and `Content.ggpk`; Steam: `PathOfExileSteam.exe` and `Bundles2\` |
| [Node.js](https://nodejs.org) (Node 24) + `npm install` in `Tools\ooz` (npm package `ooz-wasm` 2.x) | decompresses the game's bundles for the data extraction (step 7) | `node --version`; `Tools\ooz\node_modules\ooz-wasm` exists |
| Python 3.9+ | the full data extraction (maintainers, step 7); with `pip install frida`, walk maps (step 9, optional) | `python --version`; `python -c "import frida"` |
| ~same free space as the client | you work on a copy of it | |

**Steam installs:** Steam's 3.29.3c (the release it served from 2026-11-06, and the 3.29.3.5 hotfix of 2026-11-09) and
Steam's 3.29.3d work; LocalExile checks
the install's `Bundles2\_.index.bin`. The Steam layout keeps the game files loose in `Bundles2\` instead of a `Content.ggpk`, and
`PathOfExileSteam.exe` is the same executable as the standalone `PathOfExile.exe`. The client still runs in standalone
mode: step 5 patches it to read the loose files, and Steam is never started or needed.

**Getting the client:** this repository cannot provide it. The official launcher and Steam always update to the newest
version, so a fresh install works only while that version is an allowed build (3.29.3d on 2026-11-11). Otherwise you
need a copy that is still at one. Never let a launcher touch your copy.

### Linux

Linux runs the same steps with the same scripts. `live.ps1` and `extract-data.ps1` run under PowerShell 7 and the client
runs under Proton, through [umu-launcher](https://github.com/Open-Wine-Components/umu-launcher).

| Requirement | Why | Check |
|---|---|---|
| .NET 10 SDK, with the ASP.NET Core targeting pack | on Arch it is a separate package, `aspnet-targeting-pack`; without it builds fail with `NETSDK1226` | `dotnet build GameServer.slnx` |
| PowerShell 7: `dotnet tool install --global PowerShell` (`~/.dotnet/tools` on PATH) | runs the scripts | `pwsh --version` |
| umu-launcher | runs the client; the first start creates `data/wine-prefix` and may download Proton | `umu-run --help` |
| Node.js, `npm install` in `Tools/ooz`, Python 3.9+ | as on Windows | `node --version`; `python3 --version` |

- Run the scripts as `pwsh scripts/dev/live.ps1`, with `/` in paths, and every other command as this guide gives it.
- Step 2 is not needed. For step 3, `cp -r --reflink=auto "<your install>" Client` copies the client, and on a
  copy-on-write filesystem (btrfs, XFS) takes no extra space.
- The client's settings are in `data/wine-prefix/pfx/drive_c/users/steamuser/Documents/My Games/Path of Exile`.
- These settings go through to the client: `WINEPREFIX` (default `data/wine-prefix`), `PROTONPATH` (default `GE-Proton`),
  `GAMEID` (default `umu-238960`), `Game_CLIENT_RUNNER` (default `umu-run`), and anything else in the shell. On a laptop
  with two GPUs, `__NV_PRIME_RENDER_OFFLOAD=1 __VK_LAYER_NV_optimus=NVIDIA_only __GLX_VENDOR_LIBRARY_NAME=nvidia` picks
  an NVIDIA GPU.
- `-Walkmaps` on Linux x86_64 needs `g++` on its first run. It downloads the pinned official Frida GumJS development
  kit (SHA-256 checked) and builds a helper in ignored `Tools/frida/`. No Python Frida package is needed on Linux.
  Recording must start with the client; to enable it for an existing client, use `-Stop -ClientOnly`, then
  `-ClientOnly -Walkmaps`. Restart the Gateway with `-Only Gateway -Walkmaps` if it was started without recording.
- Not on Linux yet: named `-Session`, `-Nullifier` and `-Agent` clients. They stop with a message;
  with `-NoClient` the servers start as usual.

### Third-party tools (only to extend the server)

Playing needs nothing beyond the table above (step 7 extracts the game data with Node). To work out more of the protocol, as
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

New tests go in `tests/GameServer.Gateway.Tests` only: modules have no tests, a PR adds at most 2, and a test never sleeps or waits on real time.

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
# a Steam install instead: Copy-Item Client\PathOfExileSteam.exe Client\Game.exe
```

Success: `Client\Game.exe` (about 79 MB) and `Client\Content.ggpk` (Steam: `Client\Bundles2\_.index.bin`) exist.

- `robocopy` exit codes below 8 mean success (1 = copied, 3 = copied plus extra files). Copying ~70 GB takes a few
  minutes.
- Close the client before copying. A running client locks `Content.ggpk`.

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
| `this is not a Path of Exile executable LocalExile runs` | `Client\Game.exe` is another build, or a patched copy; the message names its hash and the allowed builds |
| `login verify key not found in client image` | `Client\Game.exe` is not a Path of Exile `PathOfExile.exe` |

## 5. Patch the client to trust your keys

```powershell
dotnet run --project src/GameServer.Auth -- patch Client/Game.exe Client/Game.patched.exe
```

Success: `[auth] login key blobs at file offset 0x...` then `[auth] wrote Client/Game.patched.exe`.

- In a Steam install it also prints `Steam install: the copy reads the loose game files instead of Content.ggpk`.
  A `Game.patched.exe` made without that line creates an empty `Content.ggpk` and asks to download everything: delete
  both and run this step again.
- If you regenerate the keys (by deleting `data/login-keys.json` and running step 4 again), run this step again too.
- Antivirus software may flag or quarantine the patched executable, because it is a modified binary. If
  `Game.patched.exe` disappears, allow it in your antivirus.

## 6. Point the client's gateway list at your login server

```powershell
dotnet run --project src/GameServer.Patch -- gateway --login 127.0.0.1:20493
```

Success: `[patch] gateway_list.txt now points at 127.0.0.1:20493 (original saved to data/gateway_list.original.txt)`.
The lines printed after it all contain `127.0.0.1:20493`.

- The client must be closed (it locks its files while it runs). In a Steam install the list is the loose
  `Client\gateway_list.txt`.
- To undo: `dotnet run --project src/GameServer.Patch -- gateway --restore`.
- `no gateway_list.txt in the client's files` means `Client\` holds neither a standalone nor a Steam client.

## 7. Extract the game data

The servers read the client's tables and the files generated from them (object types, area transitions, hideouts, the
stat descriptions, ...) from `data\client.db`, which they build from `sql\` in git (step "The databases" below). From
your own `Client\Content.ggpk` they need two files, extracted into `data\` (git-ignored: game data):

```powershell
.\scripts\dev\extract-data.ps1 -Servers
```

`live.ps1` (step 10) runs it on every start, so you may skip this and let `live.ps1` do it. The first run takes a few
minutes; later runs only check stamps and finish at once. It reads the GGPK and never writes it, and does not need the
client closed. A second run prints `game data up to date`.

| File | Feature without it |
|---|---|
| `data\game-files.zip` (about 121 MB) | the levels the Gateway generates itself, the Phrecia classes' passive graph check |
| `data\client-constants.json` | the root hash of your GGPK: the Patch server closes each client's connection |

After extraction the servers never open `Client\` or need Node: `data\` is enough to run them (`live.ps1 -NoClient`).
Only the client, its setup steps and this extraction use `Client\`. To run servers on another machine, copy `data\`
(`data\auth.db` and `data\characters.db` hold the accounts and characters). The capture tour's debug tooling also reads the client's log.

Without `-Servers` the script runs the whole manifest: `data\tables\*.json` and every generator's file. That is the
maintainer's input to `scripts\dev\client-sql.ps1 diff` after a client patch or a generator change, which writes the SQL
for what changed ([databases](docs/dev/databases.md)); it needs Python as well.

How it decides: the manifest at the top of `scripts\dev\extract-data.ps1` gives each file its command and a version
number. The script runs every entry whose file is missing or whose stamp (`data\extract-stamps.json`) has another
version, and the entries that need one that ran (everything needs the tables). A pull that changes a file's format
bumps its version, and your next run regenerates it. Options: `-Servers` (only what the servers read), `-Force`
(everything), `-Only hideouts,room-points` (just these), `-List` (each entry's state; runs nothing). Each run's output
is in `logs\extract-<name>.log`. It builds each .NET tool once (`logs\extract-build-<project>.log`) and runs it with
`dotnet run --no-build`.

| Problem | Meaning |
|---|---|
| `setup incomplete: Node.js not found` / `Tools\ooz\node_modules missing` | install Node, then `cd Tools\ooz; npm install` |
| `setup incomplete: Python 3.9+ ...` | install Python from python.org (see step 9 for the Store alias) |
| `extract-data: <name> failed` | read `logs\extract-<name>.log`; the tables report `schema mismatch: 0` for an allowed client build |
| `extract-data: building <project> failed` | read `logs\extract-build-<project>.log`; a build error there is a code problem in that tool, not in the game data |

### The databases

`extract-data.ps1` writes files under `data\`; it creates no database. Each server builds its SQLite files under `data\`
from the SQL in `sql\` when it starts ([databases](docs/dev/databases.md)): `auth.db` and `characters.db` (accounts and
characters), `client.db` (the client's tables, about 16 s the first time) and `world.db` (the game content: settings,
unique weights, the item and jewel catalogues, drop tables). A pull that brings new update files applies them on the
next start. The Gateway log shows the files applied (`[sql] world: <n> file(s) applied`) and the content it loaded
(`[gateway] game data: revision 1, ... 1299 unique item(s), ...`).

To change the content, add an update file in `sql\updates\pending_world\`; `/reload` in game reads world.db again without
a restart. See [editable game data](docs/dev/editable-game-data.md) for what can be content.

## 8. Create an account and a character

Start the dev account service in one terminal:

```powershell
dotnet run --project src/GameServer.Host
```

Success: `[host] listening on 127.0.0.1:7000, db=data\characters.db`.

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
- Then close both: an empty line quits the dev client, and Ctrl+C stops the host. You can keep `GameServer.Host`
  running while you play: it and the login server share the accounts in `data/auth.db`, and neither caches them.

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

**Network Mode** (Esc → Options → Game → Network Mode) is the client's choice, and the server follows it. Predictive is
the preferred mode; lockstep is served but partly working ([07](docs/protocol/07-latency-visibility-movement.md) §6).
With the default "auto", the client picks lockstep on localhost, and the Gateway log says once that Predictive is
preferred. To switch, change the option, log out to the character screen and click Play again. The setting is saved in
`Documents\My Games\Path of Exile\production_Config.ini` (`networking_type=`), which your normal client shares.

**League modes:** the existing creation screen's Hardcore, SSF and Ruthless choices select server rules.
Selected modes change server rules; Hardcore Ruthless death is permanent.
Ruthless balance is the Ruthless module's settings, changed without rebuilding or restarting the server: write the numbers
you change in `data/modules/Ruthless.json` (the defaults are in `Modules/Core/Ruthless/settings.json`). From the repository root:

```powershell
New-Item -ItemType Directory -Force data\modules | Out-Null
notepad .\data\modules\Ruthless.json
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
- **Commands:** type in chat with Enter, for example `/commands` (or `/com`). The development commands (`/goto`,
  `/item`, `/set_speed`, …) are the `gm-commands` mod of [LocalExile-Mods](https://github.com/LocalExile/LocalExile-Mods).
- **Mods:** `/mod` lists the mods the Gateway found in `Mods\Binaries\` (`live.ps1` builds the ones in `Mods\Source\` there), `/mod_enable <id>` and `/mod_disable <id>` turn one on or off;
  a mod rebuilt while you play reloads by itself ([modding](docs/dev/modding.md)).

Logs are in `logs\` (`live-auth.log`, `live-gateway.log`, `live-patch.log`, and `walkmaps.log`), plus the client's own
`Client\logs\Client.txt`. Every line starts with the time; the previous run's logs are in `logs\archive\`, and each
connection's last packets in `logs\packets\`. `Game_TRACE` adds dev traces, and `.\scripts\dev\bugreport.ps1` zips it
all for an issue ([debugging](docs/dev/debugging.md)).

`live.ps1` options:
- `-Stop` stops this checkout's clients, walk-map recorder and servers, a watched Gateway included, then waits until
  their ports are free (with `-Only Gateway` only that server, with `-NoClient` everything but the clients, with
  `-ClientOnly -Session S` only that session's client). Another checkout's processes are never stopped
  ([live testing](docs/dev/live-testing.md), "Several stacks and clients").
- `-NoClient` restarts the servers but keeps the running client.
- `-ClientOnly` starts only the client, against servers that already run: nothing is built, extracted or restarted, and
  a running client stays. With `-Walkmaps` (default ports and client) it also starts this checkout's walk-map recorder
  unless it runs (the recorder ends with its client, so each new client needs one); the Gateway announces area loads
  to it only if it was started with `-Walkmaps` too.
- `-Only Gateway` (or `Auth`, `Patch`, comma-separated) rebuilds and restarts only those servers; the client and the
  other servers keep running. After a Gateway change hot reload cannot apply this is all you need: the client drops to
  the login screen, log in and Play again.
- Hot reload is on by default: the Gateway runs under `dotnet watch`, and a saved edit to its code (or to Core, Data
  and Storage) applies to the running Gateway with the client still in game (`[gateway] hot reload #n applied` in the
  log).
  Players in game read "Starting Hot-Reload" in chat when a file under `src\` is saved, and "Hot-reload is applied in
  x.yz seconds." when it is (about 15 s for a Gateway file). A save that does not compile changes nothing; they read
  "Hot-reload failed" with the compile errors instead.
  A method's new body runs from its next call; per-connection setup (the chat command table) runs again at the next
  area change; a loop already running keeps the old body until it returns; and static fields (the catalogs) keep the
  values they were built with.
  An edit hot reload cannot apply (a changed signature, a removed member) rebuilds and restarts the Gateway on its own,
  which drops the client to the login screen as `-Only Gateway` does.
  Auth and Patch are not watched.
- `-NoHotReload` starts the built Gateway without `dotnet watch`; `-Only Gateway -NoHotReload` switches a running
  setup over, and a later `-Only Gateway` switches back.
- `-Release` builds and runs the servers' Release builds, without `dotnet watch`: the monster AI runs 1.6-2.3x faster
  and no watcher (~220 MB) runs beside the Gateway, for a stack that needs no hot reload
  ([performance](docs/dev/performance.md)). `-Debug` goes back to the default. A run without either uses the
  configuration of the last full run (`data\live-configuration.txt`), so `-Only`, `-ClientOnly` and `-NoBuild` keep to
  the builds that are running.
- `-NoBuild` skips the builds.
- `-NoExtract` skips the game-data check (step 7).

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `setup incomplete: ...` from `live.ps1` | a one-time step is missing | do the step named in the message |
| A feature is off (no hideouts, no items, no unique boss); the Gateway log starts with `WARNING: ... game data file(s) missing` | a game data file was not extracted, or `live.ps1 -NoExtract` skipped it | run `.\scripts\dev\extract-data.ps1 -Servers` (or `-List` to see what is stale), then `live.ps1 -Only Gateway` |
| `live.ps1` stops at `extracting <name> ...` | the entry is still working (area-transitions takes ~10 min), or, before #93, `Start-Process -Wait` waited on the MSBuild worker node `dotnet run` left behind | check that `logs\extract-<name>.log` still grows; on an older checkout, pull, or set `MSBUILDDISABLENODEREUSE=1` and `DOTNET_CLI_USE_MSBUILD_SERVER=0` before `live.ps1` |
| `port 8080 is used by <program>` | another program (often a web server) holds a dev port | close it, or set `Game_PATCH_URL=http://127.0.0.1:<port>/` |
| `patch server did not start` | `Client\Content.ggpk` missing, locked or unreadable | close every client, check the path |
| Client shows a patching/update screen or downloads | started without `live.ps1`, or the patch server is not running | close it immediately; start only through `live.ps1` |
| "Checking resources", then "Disconnected from patching server"; `live-patch.log` shows `root hash 0000…` | `Content.ggpk` has a zero root hash (left by the official patcher or a GGPK tool) | start again: `live.ps1` and `LocalExile.exe` repair it while the client is closed |
| "The password is not correct for this account" | wrong password, or an account created after the login server started | check the password; restart with `live.ps1` (it reloads accounts) |
| "Account does not exist" | the name in the Email field does not match `register name=` | use the registered name |
| Login hangs, no "Connected to login server" | gateway list not patched (step 6), or the login server is not running | redo step 6; check `logs\live-auth.log` |
| A new character has no items, no weapon on the ground (`start kit is empty` in `live-gateway.log`) | it first logged in before `data\client.db` had the tables and item types, and that empty kit was saved in `data\characters.db` | extract them, stop the Gateway, delete that character's row from `data\characters.db` (or move the whole DB aside to start over), run `live.ps1` again |
| Character select shows no characters | the character was created after the login server started | run `live.ps1` again |
| `dotnet build` / `dotnet test` fails with `MSB3027` ... `locked by: ".NET Host"` | the servers `live.ps1` started are running from the same `bin\` folders | let `live.ps1` rebuild them (`-Only Gateway` …), or build and test elsewhere with a short path: `dotnet test tests/GameServer.Gateway.Tests --artifacts-path C:\Temp\lx` |
| Stuck after Play (no area) | `Game_GATEWAY_ENTER` was set to something other than `1` | unset it; `live.ps1` sets it to `1` |
| `HNC mask not found` in `live-gateway.err` | the extracted client constants are not an allowed build's | use an allowed client build (rule 6) and let `live.ps1` extract again |
| Spawn in the sea or off the map | an area the server does not generate, with no walk map yet | use `-Walkmaps` (step 9) and enter the area again |
| `walkmaps: permission denied` | client and Python run at different privilege levels | run both as the same user, not one as administrator |
| The client closes or crashes by itself | usually a message the server sent wrong or left out | read the `DISCONNECT` line and the `last … packet(s)` block above it in `live-gateway.log`; attach `scripts/re/frida/trace-crash.js` and reproduce; report it with `scripts\dev\bugreport.ps1` ([debugging](docs/dev/debugging.md)) |
| Windows Firewall prompt | should not happen (servers bind to 127.0.0.1) | if it does, deny: nothing needs outside access |

## Configuration

The servers' addresses and ports, and the client folder, are in `Config\localexile.conf` under the repository root (or
the release folder). The servers write it with every key at its default, and what each does, on their first start; change
what you need. The file is yours (git-ignored); delete it to get the current defaults back. To accept players from other machines, set `Login.BindAddress` and
`Gateway.BindAddress` to `0.0.0.0` and `Gateway.PublicAddress` to the address they reach you at. Each key's environment
variable (named in the file) wins over it; `Game_CONF` names another file.

Environment variables, read when each server starts:

| Variable | Default | Meaning |
|---|---|---|
| `Game_GATEWAY_ENTER` | `1` (set by `live.ps1`) | `1` = enter the area after creating the player |
| `Game_BIND` | `127.0.0.1` | address Auth and Gateway listen on (`Login.BindAddress`, `Gateway.BindAddress`) |
| `Game_LOGIN_PORT` | 20493 | login server port, must match step 6 (`Login.Port`) |
| `Game_GATEWAY_PORT` | 6112 | instance server port (`Gateway.Port`) |
| `Game_GATEWAY_HOST` | 127.0.0.1 | address (IP) Auth and the Gateway send the client for its first area and every area change (`Gateway.PublicAddress`) |
| `Game_AUTH_CONTROL_PORT` | 20495 | Auth ↔ Gateway control port, localhost only (`AuthControl.Port`) |
| `Game_CONF` | `Config\localexile.conf` | the server configuration file |
| `Game_CLIENT_FOLDER` | `Client` | the client copy the Patch server and `LocalExile.exe` use (`Client.Folder`) |
| `Game_PORT_BASE` | unset | `live.ps1`: this checkout's servers on ports base..base+4 (Patch, Patch web, Auth login, Auth control, Gateway); remembered in `data/stack.json` like `-PortBase` ([live testing](docs/dev/live-testing.md#several-stacks-and-clients)) |
| `Game_CLIENT_SESSION` | unset | `live.ps1 -ClientOnly` and `client_control.py`: the named client session, like `-Session` / `--session` |
| `Game_RANDOM_SEEDS` | unset | new instances of the areas the Gateway generates itself (world.db's `server_level_areas`, [re/011](docs/re/011-level-generation.md)) get random seeds, every other area seed 1; `0` = seed 1 everywhere; an area the client cannot build at seed 1 gets a seed it built instead |
| `Game_WALKMAPS` | unset | set by `live.ps1 -Walkmaps`: the Gateway announces each area load to the walk-map recorder and runs capture tours (`/tour`); unset, it records nothing |
| `Game_SEED` | unset | a number = that seed for every new non-town, non-hideout instance (generation traces of one seed with `live.ps1 -Walkmaps`; overrides `Game_RANDOM_SEEDS`) |
| `Game_LEAGUE` | `standard` | `hardcore` shows the hardcore death screen (a single Exit button) |
| `Game_LEAGUES_FOLDER` | `Leagues` | where the leagues are: one folder per league |
| `Game_MODULES_FOLDER` | `Modules` | where the Gateway loads modules from |
| `Game_LEAGUE_MODULES` | `data/league-modules.json` | the user's modules added to or removed from leagues ([modules](docs/dev/modules.md)) |
| `Game_RUTHLESS_RULES` | unset | a file of all eight Ruthless numbers, in place of the module's settings; valid edits apply during play |
| `Game_NPCS` | unset | `0` = no NPCs are placed in towns |
| `Game_STASH` | unset | `0` = no stash is placed in towns and hideouts |
| `Game_FLASK_BUFFS` | unset | `0` = flasks recover without putting their flask_effect buff on the player |
| `Game_FLASK_CHARGES` | unset | unset = the game's flask charge rules (kills by rarity, charge mods, a flask put on the bar in a hostile area starts empty); `full` = every kill fills all flasks and equipped flasks keep their charges |
| `Game_PLAYER_ATTACK` | `PlayerMelee` | GrantedEffects `Id` of the player's default attack (level 1) |
| `Game_MOVE_MODE` | `echo` | `echo`, `move` or `teleport` |
| `Game_WALK_SPEED` | 37 | walk speed in cells per second, used for position estimates |
| `Game_DEV_DB` | `data/accounts.json` | an account store from before the save database, imported once |
| `Game_CHARACTERS_DB` | `data/characters.db` | character saves and stashes (Gateway, Auth) |
| `Game_AUTH_DB` | `data/auth.db` (beside a characters database elsewhere: `<name>-auth.db`) | accounts (Auth, Gateway, Host) |
| `Game_INSTANCE_SECRET` | `data/instance-secret.bin` | instance key secret (created on first start) |
| `Game_PATCH_URL` | `http://127.0.0.1:8080/` | patch web server address |
| `Game_TLS_PFX`, `Game_TLS_PFX_PASSWORD` | unset | a PKCS#12 certificate file (and its password): the patch web server then defaults to `https://0.0.0.0:443` |
| `Game_HOT_RELOAD_LOG` | set by `live.ps1` with hot reload | the `dotnet watch` output file the Gateway reads to report hot reload results (compile errors, nothing to apply) in game |
| `Game_TRACE` | unset | dev traces, a comma list: `move`, `latency`, `ticks`, `ai`, `packets`, or `all` ([debugging](docs/dev/debugging.md)) |
| `Game_PACKET_DUMPS` | `logs\packets` (given by `live.ps1` to the servers it starts) | folder for each connection's last packets, written when it closes; `0` = none |
| `Game_LOG_TIMESTAMPS` | unset | `0` = no time at the start of each log line |
| `Game_MODS` | unset | `0` = the Gateway loads no mods ([modding](docs/dev/modding.md)) |
| `Game_MODS_FOLDER` | `Mods/Binaries` | the folder the Gateway loads mods from and watches |

## Files you will have afterwards

| Path | What | Share? |
|---|---|---|
| `Client\` | your client copy (gateway list patched) and `Game.patched.exe` | never |
| `data\login-keys.json` | your login server keys | never |
| `data\instance-secret.bin` | instance key secret | never |
| `data\accounts.json.imported` | the account store from before the save database, kept after its import | never |
| `data\auth.db`, `data\characters.db` (with `-wal`, `-shm` while the servers run) | accounts; characters, their saves and stashes ([saves](docs/saves.md), [databases](docs/dev/databases.md)) | never |
| `data\gateway_list.original.txt` | backup for `gateway --restore` | no need |
| `data\game-files.zip`, `data\client-constants.json` | extracted game data (step 7) and their stamps, `data\extract-stamps.json`; a full extraction (no `-Servers`) also writes `data\tables\` and the generators' files | no (game data) |
| `data\client.db`, `data\world.db` | the client tables and generated files, and the game content, built from `sql\` | no (game data) |
| `data\walkability\` | captured walk maps | no (derived from game data) |
| `logs\` | server logs | only when reporting a bug, and read them first |
| `Mods\Binaries\` | built mods: `Mods\Source\`'s, and any you add ([modding](docs/dev/modding.md)) | yours, if you like |

## Start over

1. Close the client.
2. Run `dotnet run --project src/GameServer.Patch -- gateway --restore`.
3. Delete `data\` and `Client\Game.patched.exe`.
4. Continue from step 4.
