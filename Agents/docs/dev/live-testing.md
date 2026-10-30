# Live testing against the real client

How to run the patched 3.29.3b client against our servers safely and repeatably.

## Rules

1. **Never let the client update.** An update replaces the patched client and data. Start it only through
   `scripts/dev/live.ps1`, which passes `--patching-server 127.0.0.1:12995`. The patch server advertises only
   `http://127.0.0.1:8080/` and answers 404 for everything except `gateway_list.txt`. Check
   `Tools/re/out/live-patch.log` for `cdn passthrough off`.
2. **Never start a second client** while one runs (that includes computer-use `open_application("Game.patched")`). A
   second instance started without the patching-server argument tried to download `PathOfExile.exe` from the real CDN on
   2026-10-16. To bring the running client to the front, restore its window:
   ```powershell
   Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int c); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);'
   $h = (Get-Process -Name Game.patched).MainWindowHandle; [W.U]::ShowWindow($h, 9); [W.U]::SetForegroundWindow($h)
   If the window stays minimized (`ShowWindow` from a background process is ignored), post a restore command instead:
   ```powershell
   Add-Type -Namespace W3 -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);'
   [W3.U]::PostMessage((Get-Process -Name Game.patched).MainWindowHandle, 0x0112, [IntPtr]0xF120, [IntPtr]0)
   ```
   ```
3. With "Save Login Details" ticked the client auto-submits the saved login at startup. If it fails, Auth now closes
   the connection and Log In works again. In the client the password field needs a click before typing; Enter does not
   submit, click Log In.
4. The dev account is `devtester` (password in `data/dev-login.txt`, untracked). Its characters live in
   `data/saves.db` ([saves](../saves.md)).

## One-time setup

```bash
dotnet run --project src/GameServer.Auth -- keygen Client/Game.exe
dotnet run --project src/GameServer.Auth -- patch Client/Game.exe Client/Game.patched.exe
```

Also point the GGPK gateway list at the local login server (`GameServer.Patch gateway --login 127.0.0.1:20493`; the
original is backed up to `data/gateway_list.original.txt`), and register the dev account with `GameServer.DevClient`.

## A run

```powershell
$env:Game_GATEWAY_ENTER = '1'      # also send 0x21 (enter area). Leave unset to stop after the player object
& .\scripts\dev\live.ps1           # -NoClient keeps a running client, -NoBuild skips the builds
```

Gateway env: `Game_MOVE_MODE` = `echo` (default: only the 0x1ab echo), `move` (also 0x1ba, `Game_MOVE_SPEED`) or
`teleport` (0x1b7); `Game_PLAYER_AO` overrides the Animated path; `Game_HIT_OFFSET_MS` (default 150) delays the
default attack's hit past its contact keyframe ([11](../protocol/11-skills.md) §5).

`live.ps1` stops anything on ports 20493, 12995, 8080 and 6112 and the client, builds Auth, Patch and Gateway, starts all
three (logs in `Tools/re/out/live-*.log`), waits for the patch server and launches the client. Then:

1. wait for "Loading in" in `Client/logs/Client.txt` (startup takes about a minute; the file check is local)
2. log in as `devtester` and select DevWitch
3. Play

To restart only the Gateway: `.\scripts\dev\live.ps1 -Only Gateway`. The client drops to the login screen and you
log in again; the login server keeps running.

## Changing the Gateway while it runs (hot reload)

`live.ps1` runs the Gateway under `dotnet watch` unless `-NoHotReload` is given
([GettingStarted](../../GettingStarted.md) step 10).
These are suggestions that have worked well, for people and agents alike; use your judgment.

- **Try saving before restarting.** Most behaviour edits (packet handlers, damage, drops, AI, chat commands) apply to
  the running Gateway in about 15 s, and the player stays in game. A restart costs the player a login and the walk
  back to what they were testing.
- **Check that it applied.** `logs\live-gateway.log` gets `[gateway] hot reload: <file> saved` and then
  `[gateway] hot reload #n applied in x.yz seconds`; players see the same two lines in chat.
  `dotnet watch`'s own lines (`File updated`, `changes applied`, compile errors) are in `logs\live-gateway.err`.
- **A compile error leaves the old code running.** `dotnet watch` reports it in `live-gateway.err` and waits for the
  next save; nothing restarts. Players read "Hot-reload failed: n compile error(s)" in chat with the first three
  errors (file, line, code). Fixing it and saving again is usually all that is needed; when the fix brings the code
  back to what is running, `dotnet watch` applies nothing and players read "no code changes to apply".
- **Group a multi-file change.** Each save that compiles starts a reload, so a half-done change across several files
  can apply in between. Writing the files in quick succession (or in one tool call) keeps it to one reload.
- **Know what needs more than a save.** Static fields and catalogs (`Items`, `Skills`, `Lazy<>` caches) keep the values
  they were built with; new static fields start empty, not with their initializer. Per-connection setup (the chat
  command table, character preparation) runs again on the next area change, which is a new connection. A loop that is
  already running keeps its old body until it returns. For these, an area change or `live.ps1 -Only Gateway` helps.
- **Some edits restart on their own.** A changed method signature or a removed member cannot be applied;
  `dotnet watch` prints `Restart is needed to apply the changes`, rebuilds and restarts the Gateway, and the player
  is back at the login screen (the Gateway tries to say so in chat first). It can be kind to tell the person playing
  before saving such an edit.
- **Tests while it runs.** The watched Gateway holds its `bin\` folder; build and test elsewhere, for example
  `dotnet test tests/GameServer.Gateway.Tests --artifacts-path C:\Temp\lx` (Troubleshooting in GettingStarted).
- **Auth and Patch are not watched.** Changes there still need `live.ps1 -Only Auth` or `-Only Patch`.

## What a good run looks like

`live-gateway.log`:
```
[gateway] CONNECT 127.0.0.1:…
[gateway] … opening: ticket=0x… digest=… address=0x7f000001 u32=0 realm=0x40 flag=1
[gateway] … => 0x0011 (start encryption)
[gateway] … => 0x001d AreaInstance { InstanceId = 1, AreaHash = 9052, Seed = 1, … }
[gateway] … <= 10 bytes: 007D43E6886D8EA8DD43
[gateway] … => 0x0022 local player 0x1000: 002200001000
[gateway] … => 0x02b9 create PlayerObject { Name = DevWitch, … } type=0x9c41993e: 02B9…
[gateway] … => 0x00c0 inventory 1 row 0: …            (× 117)
[gateway] … => 0x003d passive slots 0-2: 003D00000000 1C0000
[gateway] … => 0x0021 enter area: 0021…
[gateway] … <= 7 bytes: 001B00…                       (repeating: in game)
```

`Client.txt`: "Got Instance Details from login server" → "Connecting to instance server at 127.0.0.1:6112" →
"Client-Safe Instance ID = 1" → "Generating level 13 area "1_1_town" with seed 1" → tile/doodad hash → "[SCENE] Set
Source [Lioneye's Watch]". Then the HUD appears.

## Troubleshooting

| Symptom | Meaning |
|---|---|
| "Checking resources..." for a long time, with "Queue file to download" in `Client.txt` | a client not started by `live.ps1`, or a patch server advertising a real CDN. **Stop the client** |
| "Buffer Underflow" dialog | the 0x2b9 blob is shorter than what the client reads. Closing the dialog ends the client. Trace with `trace-player-blob.js` |
| `WerFault.exe` appears / window masked in screenshots | client crash. Event log id 1000 has the fault offset (VA − 0x140000000). Attach Frida before Play next time |
| "Area reported by instance does not exist" | the 0x14 area hash is not a WorldAreas.HASH16 |
| client back at the login screen, "An unexpected disconnection occurred" | the instance connection closed (Gateway restarted or crashed) |
| nothing after 0x7d | 0x21 not sent (`Game_GATEWAY_ENTER` unset). The world queues 0x2b9 and waits |
| extra map icons with no label (e.g. three area transition icons at the town gate) | the client's **minimap cache** remembers icons of objects it has seen, per area, in `%LOCALAPPDATA%\Path of Exile\Minimap\` (hash-named, encrypted files). Moving an object around (dev commands, changed defaults) leaves its old icons there, across restarts. Close the client and move the files away; seen 2026-10-17 |

Window position and size change between launches; take a screenshot before clicking. At half scale, the password
field, character row and Play button were near (245, 217), (375, 61) and (455, 263) in one layout and moved in others.

## Env vars

| Var | Default | Used by |
|---|---|---|
| `Game_LOGIN_PORT` | 20493 | Auth |
| `Game_GATEWAY_PORT` | 6112 | Gateway |
| `Game_GATEWAY_ENTER` | unset | Gateway: `1` sends 0x21 |
| `Game_INSTANCE_SECRET` | `data/instance-secret.bin` | Auth and Gateway (must match) |
| `Game_SAVES_DB` | `data/saves.db` | Gateway, Auth, Host |
| `Game_PATCH_TCP_PORT`, `Game_PATCH_URL`, `Game_PATCH_WEB_ROOT` | 12995, http://0.0.0.0:8080, http://127.0.0.1:8080/ | Patch |
| `Game_CDN_PASSTHROUGH` | unset | Patch: `1` proxies to the real CDN (only to repair data on purpose, with a backup of `Client/`) |
