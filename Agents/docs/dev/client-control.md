# Native client control

`scripts/dev/client_control.py` provides CLI and persistent JSON-lines RPC for the copied standalone **3.29.3c**
client. It calls existing client functions through [Frida RPC](https://frida.re/docs/javascript-api/#rpc), on the
game thread. It uses the normal local authentication, character list, Play, instance connection, and Gateway command
handlers. No screenshots, keyboard/mouse events, HTTP listener, save editing, or server authentication bypass are used.

Start the local servers normally through `scripts/dev/live.ps1`. Python needs `frida` (the walk-map recorder's `.venv`
already has it). Run from the repository root:

```powershell
.\scripts\dev\control.ps1 game:open
.\scripts\dev\control.ps1 login:offline         # private password prompt; use the local account's existing password
.\scripts\dev\control.ps1 character:list
.\scripts\dev\control.ps1 character:play:OfflineWitch
.\scripts\dev\control.ps1 game:status
.\scripts\dev\control.ps1 command '/hit physical 100'
.\scripts\dev\control.ps1 chat '#hello'           # any chat line: local, #global, $trade, or a slash command
.\scripts\dev\control.ps1 tp 'RhoaChest 2'        # /tp: also tp '120 340' (a cell) or tp '+10 -5' (relative)
.\scripts\dev\control.ps1 character:select
.\scripts\dev\control.ps1 logout
```

`game:open` attaches to an existing client, or calls `live.ps1 -ClientOnly`. That switch starts only the copied client
with the local patching-server argument and requires the local servers already running; it does not restart services,
rebuild, extract data, or close an existing client. With `-Walkmaps` (default ports and client only) it also starts
the walk-map recorder beside the client unless this checkout's recorder already runs. It waits up to 60 seconds for a native menu, so login can immediately
follow its successful result. Before opening a closed client on the default ports, it points the cached GGPK gateway
list at the local Auth when the list does not already (a stack on its own ports never writes the shared GGPK: the
bridge's login connects to its Auth by port); an existing client returns before that.

Several clients: `--session NAME` (`control.ps1 -Session NAME`, or `Game_CLIENT_SESSION`) drives one of several clients
of this checkout's stack, each opened by `game:open` with that session as `live.ps1 -ClientOnly -Session NAME` does
([live testing](live-testing.md#several-stacks-and-clients)). The bridge attaches to the PID `live.ps1` recorded for the
session in `data/clients/NAME.json` (checked with the process's start time and this checkout's client path), never to
another checkout's or session's client; without a session it takes this checkout's one unrecorded or default-recorded
client, as before. A client counts as this checkout's by the path it was started by (its command line: the image name
Windows reports resolves the `Client` junction to the shared folder, the same for every checkout). Logins go to this
stack's Auth: the ports `live.ps1 -PortBase` wrote to `data/stack.json`, else `Game_LOGIN_PORT`, else 20493. Startup asset loading still takes the
client's normal time. After merging upstream's gateway reset, a fresh client opened against the already-running
Patch/Auth/Gateway services in 9.5 seconds, then logged in, listed characters and entered an area successfully
(2026-10-25, isolated validation database). Mocked startup checks also cover existing clients and failed preflights.

Every command returns one JSON object: `ok`, `result` or `error`, and `elapsedMs`. `character:list` reads the list
actually received by the client; entries have `index`, `name`, `league`, `level`, and `classId`. Play resolves the name
against that list and waits for the client to enter the area. Replaying the active character and opening an already
running game are idempotent. Login reauthenticates, returning to selection first if necessary. A wrong password returns
`ok:false` with login result 6; retrying works without restarting. `command` sends a slash command, `chat` any chat
line (the client's own 0x15, as typed in the chat box), and `tp` the `/tp` dev command built from `x`/`y`, `dx`/`dy`,
`id`, `target` (+ `index`) or `list`. For slash commands the result also has `replies`: the chat lines the Gateway
answered with, read from `logs/live-gateway.log` (its `=> 0x0017 chat line` entries) until 0.25 s pass without a new
one or `wait` seconds (default 1.5) in total. `replies` is null when the servers were not started by this checkout's
`live.ps1` (no log); a command without an answer (e.g. `/goto`) returns an empty list after `wait`. Only the lines sent
to this client's own connection count (the log names each connection by the client's local port, which the bridge
reads from the TCP table for the client's PID; none while it has no connection to the Gateway), so other sessions on
the same Gateway do not mix in; replies of
commands typed in this client's chat box meanwhile still do. The bridge does not capture the HUD.

Explicit password login replaces both the outgoing account/digest and the native message's saved-credential flag.
The ordinary handshake can submit a remembered form before the explicit login RPC runs.
`AccountLogin +0x95` must therefore be cleared when replacing that form with a password digest.
The Remember Password preference at `+0x94` is retained; `login:saved` has no pending replacement and stays unchanged.
The server still validates saved credentials and password digests through their separate normal paths.
Native verification used a new synthetic account and the unchanged strict Auth server on `bbb658a9`.
A remembered form first produced result 6 with `Flag95=true`; the corrected script reached selection with result 0.
The tested combined source was `9736945d`, whose controller script is identical to this standalone correction.
Saved-login preservation is covered by the offline hook control, not claimed as new native acceptance.
Offline checks run with `python scripts/dev/test_client_control.py` and
`node scripts/dev/test-client-login-writer.cjs`.

## Persistent integration

Spawn `.venv/Scripts/python.exe scripts/dev/client_control.py --stdio` with stdin/stdout pipes. Write one JSON object
per line, flush stdin, and read one JSON result line. This retains the Frida attachment between calls:

```json
{"command":"login","account":"offline","password":"your-local-password"}
{"command":"character:list"}
{"command":"character:play","name":"OfflineWitch"}
{"command":"command","text":"/npc Tarkleigh"}
{"command":"command","text":"/pos","wait":0.5}
{"command":"tp","target":"RhoaChest","index":2}
{"command":"tp","x":120,"y":340}
{"command":"act","kind":"walkTo","x":512,"y":740}
{"command":"chat","text":"#hello"}
{"command":"game:status"}
```

These lines illustrate the format; consume each result before submitting the next command. Credentials are neither
echoed nor persisted. CLI login also accepts `--password-stdin` for a password supplied through a pipe. Do not put real
credentials in tracked scripts or shell arguments. The Python `ClientControl` context manager exposes `execute(request)`
for scripts in the same process. Closing stdin or leaving the context detaches; the game keeps running.

## Client implementation and verification

The bridge refuses a different executable path or code signatures. Addresses below are image VAs, rebased at attach:

| Address | Purpose |
|---|---|
| `0x141528fbf` | State-stack update after its `+0x38` transition guard is entered. Calling transitions before this guard can destroy live UI state. One pending RPC is serviced here, with a five-second cancellation if no frame arrives. |
| `0x1445d6fd8` / `0x1445d6fb0` | App and state-stack singleton pointers in 3.29.3c (0x10 higher in 3.29.3d). App `+0x358` is login; `+0x348` is instance. The bridge reads both addresses from the instructions `0x1400f3b30` and `0x1400f29e3` (`globalAt`), so it follows every allowed build. |
| `0x142144340` | Connect using UTF-16 host/port strings; bridge forces `127.0.0.1`. |
| `0x142142fb0` | Native account-login writer. The handshake listener can auto-submit its saved form before a second RPC arrives; supply the requested account/digest to this **one** submission. The native string assignment owns its allocation; the digest is cleared from bridge memory after copying. |
| `0x142145360` | Login sender, used only if the handshake listener has not already submitted. |
| `0x142145ae0` | Capture the actual login result (`0x04`, result at `+0x10`), the delete result (`0x0d`, `+0x10`) and the create result (`0x13`: reader `0x1421a2240` stores the u16 at `+0x12`, the bool at `+0x10`). |
| `0x142146de0` | Native C→S 0x12 create-character sender: (login, name view, league view, unused r9, password view, start-state view). Its caller in 009, the create screen's `0x140cf5e60`, pushes state 11 and sets the class first; the bridge does neither, so no Play follows. An empty password view sends 32 zero bytes. |
| `0x141f9baf0` | C→S 0x0c message writer, slot 1 of class `0x1435ca710`: (message, socket) writes u16 `+8`, wstr `+0x10`, u8 `+0x30`; the bridge flushes after it like the delete screen's sender `0x140cfaf60`, which itself needs that screen. |
| instance `+0xc278` → `+0xb0` | Current WorldAreas row: Id string pointer at `+0`, HASH16 at `+0x2a`. The map overlay `0x1406b25f0` titles the area with this row's `+0x8` (Name) and tests `+0x14`/`+0x1e4`; the delve gauge (`0x140430780`) tests `+0x1e4`. |
| login `+0xe68` | Received character vector, 0x50-byte entries. |
| `0x142147040` | Play with the chosen name view and list index. Set in-game state `+0xa48` to its class; push **state 11 (loading)**, which installs the assignment listener. Directly pushing state 4 skips that listener. |
| `0x140d0f770` | Existing root login state's logout routine; preserves the correct login form lifecycle. |
| `0x140dfa000` / `0x140dfa160` | Existing buffered socket writer/flush, for `0x153` return to selection and `0x15` slash commands. Encryption remains the client's. |
| `0x1421752b0` | Signature-checked reference for socket write coordination: mark writing, acquire the socket lock while threaded, write, release and unmark before flushing. |

The bridge coordinates buffered writes with the network thread's flush through the socket's own lock.
An unsynchronized write could desynchronize encryption permanently; author-reported native quest runs verified the fix.

Live verified 2026-10-25: fresh `game:open`, login, actual level-100 OfflineWitch list, Play, return to selection, logout,
wrong-password rejection, successful retry, Play again, and `/hit physical 1` received by the Gateway. In one persistent
run: login 0.7–0.9 s, list 94 ms, Play 1.4–2.2 s, command submission 94 ms; timings include game frames and loading,
and are observations rather than guarantees. Only the Witch class was exercised live.
`tp` checked live 2026-10-25 in The Mud Flats (seed 1): after `/tp RhoaChest 2`, `/tp GlyphWall` and `/tp +10 +0` the
client's own cell (Positioned +0x294/+0x298, as `scripts/re/frida/trace-player-drift.js` reads it) equalled the
Gateway's `/pos` every time, and the `/pos`, `/tp list` and refusal replies came back in `replies`. An attached bridge is for the
local development client; it has no official-service support.

Checks: `node --check scripts/dev/client-control.js` and
`.venv/Scripts/python.exe -m unittest discover -s scripts/dev -p test_client_control.py`.
Mocked RPC tests exercise the production login/status waits for success 0 before authenticated state 3,
immediate rejection 6, and the existing timeouts for pending or incomplete success; no new native verification is claimed.
`powershell -NoProfile -File scripts/dev/test_live_client_only.ps1` checks closed/existing client startup,
local gateway selection, and failed preflights with mocked processes and no GGPK writes.

## Campaign helpers

From character selection, `{"command":"character:create","name":"CampaignWitch","class":"Witch","league":"Standard"}`
calls the client's own 0x12 sender (`league` defaults to Standard; `startState`, e.g. `IntDefault`, replaces `class`).
The class's default start state (set 0, level 1) comes from the `Characters` and
`CharacterStartStates` client tables (`data/client.db`). Names follow Auth's rule: 3–23 letters, digits, `_` or `-`. The call waits until the new
name is in the received list and returns `{"character":{index,name,league,level,classId},"startState",…}`. A non-zero
0x13 result raises its meaning, e.g. 21 name taken or 49 no slots. After a successful create the client itself moves
to the loading state (11) and waits for the Play its create screen would send; `character:play` sends that Play from
there without pushing state 11 again (live: create 0.6 s, Play into The Twilight Strand 4.6 s).
`{"command":"character:delete","name":"X"}` writes 0x0c with the class writer and waits until the name leaves the
list; with `"missingOk":true` an unlisted name returns `{"deleted":false}`. A non-zero 0x0d result raises, e.g. 16.

`game:status` has `build`, the version the executable names itself (`3.29.3d`), and `area` (WorldAreas Id) and
`areaHash` (HASH16) while instanceState is 3; they are null when the row
cannot be read. `{"command":"waypoint:travel","area":"1_1_2"}` (or `"hash":31442`, or the HASH16 as `area`) sends the World panel's 9-byte 0x4d
(u32 0x2f00, Ctrl 0) on the instance socket. With `"wait":true` the response adds a `status` once the area is entered.
`{"command":"area:wait","area":"1_1_2","timeout":60}` waits for that area. `{"command":"area:wait","from":<status>}`
waits for a change of area or instance: the instance leaves state 3 and returns, or the Id changes. With both, the
change and the Id are required (travel into the current area). `{"command":"item:pickup","id":<ground item id>}`
sends the `object:interact` 0x24, which puts the item into the first free main-inventory cell.

Every message the bridge writes on a socket goes out as the client's own instance send (`0x1421752b0`) writes one:
the socket is marked as being written (+0x240) and, while another thread services it (+0x242), the buffered writer
runs under the socket's SRW lock (+0x248), which the flush takes too. Written without the lock, a command raced the
network thread's flush and the client's cipher fell out of step with the Gateway's for good (twice in about six
campaign runs of hundreds of commands; the Gateway now closes such a connection, see 07 / `ClientInbox`).

Live verified 2026-10-30 with the [campaign runner](campaign-automation.md): character creation and Play of fresh
Witches, `area` right after every transition and waypoint travel, `area:wait`, `waypoint:travel` back to Lioneye's
Watch from The Tidal Island and The Mud Flats, `item:pickup` of weapons, gems, glyphs and the Medicine Chest, and
`--password-stdin` (it drops the leading BOM Windows PowerShell 5.1 pipes). `character:delete` and the 0x0d result
are covered by the mocked tests only.

## Build provenance for native verification

Switching branches does not rebuild `bin/`.
Before a `-NoBuild` pass, build the final source checkout and compare `git rev-parse HEAD` with the embedded
informational Git version of Gateway, Core and Host; record their hashes in the evidence metadata.
Verify the listening Gateway's DLL and working directory as well as the source tree.
A clean repository at the intended commit does not prove that a previously built DLL contains that commit.
Keep source and build outputs frozen during a native window, then restore the agreed normal runtime.

```powershell
git rev-parse HEAD
dotnet build
(Get-Item src/GameServer.Gateway/bin/Debug/net10.0/GameServer.Gateway.dll).VersionInfo.ProductVersion
Get-FileHash src/GameServer.Gateway/bin/Debug/net10.0/GameServer.Gateway.dll -Algorithm SHA256
```

## Walking with real input (act)

`act` moves the character the way a player moves it, for an operator outside the client
([ai-operator](ai-operator.md)); it is not a test shortcut:

```json
{"command":"act","kind":"walkTo","x":512,"y":740}
```

It clicks inside the client's own window through the no-focus route of `scripts/re/frida/drive-input.js`, so the
client's own action code decides what the server is sent, and it reports what the client itself resolved. That is the
opposite of `move`, which writes the C→S 0x25 itself so the client predicts nothing; `move` stays for tests that need
a fixed packet.

The result is a verdict, not an error: `{"reached":true,"cell":[512,740],"target":[512,740],"clicks":3,
"predicted":[508,742],"order":[508,742],"elapsedMs":…}`; when it does not get there, `reached` is false and `reason`
says why in a sentence an operator can act on (`blocked: 4 cells short of [512, 740] after 3 clicks`, `gave up …`),
with the same fields. `clicks` counts the clicks it took, `predicted` is the cell the client resolved while aiming,
`order` is the cell it resolved when the button went down (null if the client never asked for an action). Optional
`tolerance` (cells, default 1), `timeout` (seconds, default 25) and `clicks` (budget, default 30).

Nobody is teleported and no walk packet is written: the client's own order is the one that reaches the server, and
`order` is the bridge's record of it.

### What a click resolves to

The client converts its cursor into a world point every frame and publishes it at `instance + 0xc7f4` (x) and
`instance + 0xc7f8` (y) in world units; that pair is what its click asks for. The bridge reads it rather than guessing:

- `0x140df5100 int *f(int out_cell[2], const float world_xy[2])` is the client's own world→cell conversion,
  `out[i] = (int)rintf(world[i] * DAT_1436adb60)`, `DAT_1436adb60 = 0.092f` (1 cell = 10.8696 units, 23 cells = 250, the
  tile size). It is a pure function — no entity, no map, no per-area state — so a probe calls it directly.
- The per-frame pump `0x14011a070` calls `0x14011a610` unconditionally and that publishes the pair; there is no
  left-button test in it, so moving the cursor without clicking already updates what a click would walk to. That is
  what makes aiming free, and why `GetForegroundWindow` must answer the session window (the block producer upstream is
  gated on it; `scripts/re/frida/drive-input.js` keeps that hook for the same reason).
- `0x1421660b0` is where the client asks for the action, and its fourth argument points at the cell it resolved — the
  bridge's own record that the client aimed.
- One click saturates: the target is `player_world + R(yaw)·v̂·(min(|v|,1)·150 + 30)` world units, so it reaches about
  16.6 cells. `walkTo` therefore aims, clicks, waits for that walk to end and aims again — aiming straight at a far cell
  only saturates, so it aims at a waypoint one click away and repeats.

### Acting on objects: useSkill, loot, interact

```json
{"command":"act","kind":"useSkill","x":352,"y":1327,"target":134217965}
{"command":"act","kind":"loot","x":351,"y":1327,"object":1048576}
{"command":"act","kind":"interact","x":388,"y":231,"object":8192}
```

All three name the **cell** the thing stands on; `target`/`object` is the server's id, echoed back for the caller's
record only, because the client does not name its objects (its entity ids are not the server's). The bridge aims the
cursor at that cell (the same free readback as `walkTo`, so no camera maths and no calibration constant), presses, and
retries a few pixels higher each time — labels sit above the body — until the client asks for an action. `slot` picks
what presses: `0` is the left button (move and the default attack, which is the game's own layout), `1`-`5` are the
bar's Q/W/E/R/T; `frames` (default 3) is how many frames the press is held across, because the client reads the button
per frame.

The answer says what the client's own pick named and which order it produced, so the caller can tell a real action from
a walk:

```json
{"ok":true,"kind":"loot","cell":[351,1327],"resolved":[350,1327],"target":1048576,"pressed":[345,284],
 "attempts":1,"how":"left","sent":true,"order":{"cell":[0,0],"object":1048576,"walk":false,"from":"0x7ff6d13a8a8b"}}
```

`order.object` is the id the client put in the packet, `order.cell` is the cell it named (0,0 for an object), `order.walk`
says whether the press came from the walk sender, and `order.from` is the caller's return address (the walk path is
`0x14012461e`; the others are the skill paths). `loot` and `interact` fail with a sentence instead of a silent retry
when the order came back aimed at the ground (`the client walked instead of acting on an object`), and every kind says
`object <id> is not in the client's streamed set` when the label pass has not seen it yet — get closer, or look again.

Live, on the 3.29.3c release build: a left click on a ground item sent `<= 0x0024` with the item's id and the ground
lost it; a left click on the town's area transition sent its id and the character changed area; a left click on a
monster under a wand sent `0x0102` (the default attack) on the monster's id and the server's projectile recorded
`damage=7 life=16/23`. None of those writes a packet itself.

## Targeted development interactions

JSON RPC also accepts `game:resources` (read-only Life component pools and recent 0x1b1 corrections),
`object:interact` with an object `id` (and an optional `skill`, e.g. `258` = 0x0102, the default attack, to attack it; `ctrl` / `alt` true for an NPC's shortcuts), `move` with grid `x`/`y`, `npc:option` with row `index`, `npc:close`,
`reward:claim` with choice `id` and main-inventory `x`/`y`, and `inventory:pickup`/`inventory:place` with `inventoryId`
and item `id` or destination `x`/`y`, `item:use` with an item `id` (and `inventoryId`, default 1: the main inventory;
CLI `item:use 0x403`), which sends the right-click C→S 0x58 (a quest book's use, an Orb of Regret), and `flask:use` with
a flask-bar `slot` (0-4) or `key` (1-5; CLI
`flask:use 1`), and `pantheon:select` with a PantheonPanelLayout `row` (0-based; CLI `pantheon:select 11`), which sends
the Pantheon panel's C→S 0x1ee with row + 1 (for the later acts' Pantheon steps), and `stash:deposit` with an inventory item `id`
and a stash `tab` (default 0), which sends the Ctrl + click C→S 0xe0 that puts the item into that tab's first free cell
([18](../protocol/18-stash.md) §5; open the Stash first, as a player would). `flask:use` sends the flask key's own C→S 0x70
(u32 cell, [13](../protocol/13-items.md) §8), so a flask is used without keyboard input or window focus. These use the existing C?S interaction messages. Their result acknowledges
submission; wait for the resulting state or logs. Movement uses the client's Move action (0x2909). Interaction from
far away can fail to find a path; `tp` next to the object first (`{"command":"tp","target":"GlyphWall"}`), then interact. Resource updates are bounded to 128 entries and drained on
read, so persistent RPC is needed to observe a recovery interval. This measures client state, not HUD pixels.

`waypoint:travel` with an `area` (a WorldAreas HASH16, number or `"0x7ad2"` string; a WorldAreas Id or `hash` as in
"Campaign helpers" above) sends the World panel pin's C→S 0x4d ([16](../protocol/16-waypoints.md) §3): the Gateway changes area when that waypoint is activated
(`/waypoints all` activates every one). Live 2026-10-31 to The Coast and back with `/goto 1_1_town`.
`game:frames` returns the main-loop frames counted at the bridge's own frame point since it attached, and the frames
per second since the previous call (a session client with the cheap preset runs at about 9.2;
[headless client](headless-client.md)).

Live interaction checks included Tarkleigh's reward claim, starter weapon pickup/equip, the biting zombie's death and
gem pickup, walking to the support chest and opening it. The client stayed attached through these tests. RPC completion
is deferred until its native callback has returned, so a one-shot process can detach without unloading a hook while
it is still unwinding. Prefer loading additional probes into the same Frida session when extending this tool.

The campaign runner built on this bridge is described in [campaign automation](campaign-automation.md).
