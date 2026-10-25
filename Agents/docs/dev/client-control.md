# Native client control

`scripts/dev/client_control.py` provides CLI and persistent JSON-lines RPC for the copied standalone **3.29.3b**
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
.\scripts\dev\control.ps1 character:select
.\scripts\dev\control.ps1 logout
```

`game:open` attaches to an existing client, or calls `live.ps1 -ClientOnly`. That switch starts only the copied client
with the local patching-server argument and requires the local servers already running; it does not restart services,
rebuild, extract data, or close an existing client. It waits up to 60 seconds for a native menu, so login can immediately
follow its successful result. Before opening a closed client, it resets the cached GGPK gateway list to local Auth,
as a normal startup does; an existing client returns before that write. Startup asset loading still takes the
client's normal time. After merging upstream's gateway reset, a fresh client opened against the already-running
Patch/Auth/Gateway services in 9.5 seconds, then logged in, listed characters and entered an area successfully
(2026-10-25, isolated validation database). Mocked startup checks also cover existing clients and failed preflights.

Every command returns one JSON object: `ok`, `result` or `error`, and `elapsedMs`. `character:list` reads the list
actually received by the client; entries have `index`, `name`, `league`, `level`, and `classId`. Play resolves the name
against that list and waits for the client to enter the area. Replaying the active character and opening an already
running game are idempotent. Login reauthenticates, returning to selection first if necessary. A wrong password returns
`ok:false` with login result 6; retrying works without restarting. `command` acknowledges **submission** of a slash
command; inspect Gateway replies/logs or resulting state to establish completion. The bridge does not capture the HUD.

## Persistent integration

Spawn `.venv/Scripts/python.exe scripts/dev/client_control.py --stdio` with stdin/stdout pipes. Write one JSON object
per line, flush stdin, and read one JSON result line. This retains the Frida attachment between calls:

```json
{"command":"login","account":"offline","password":"your-local-password"}
{"command":"character:list"}
{"command":"character:play","name":"OfflineWitch"}
{"command":"command","text":"/npc Tarkleigh"}
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
| `0x141526cbf` | State-stack update after its `+0x38` transition guard is entered. Calling transitions before this guard can destroy live UI state. One pending RPC is serviced here, with a five-second cancellation if no frame arrives. |
| `0x1445cbe68` / `0x1445cbe40` | App and state-stack singleton pointers. App `+0x358` is login; `+0x348` is instance. |
| `0x14213d9f0` | Connect using UTF-16 host/port strings; bridge forces `127.0.0.1`. |
| `0x14213c660` | Native account-login writer. The handshake listener can auto-submit its saved form before a second RPC arrives; supply the requested account/digest to this **one** submission. The native string assignment owns its allocation; the digest is cleared from bridge memory after copying. |
| `0x14213ea10` | Login sender, used only if the handshake listener has not already submitted. |
| `0x14213f190` | Capture the actual login result (`0x04`, result at `+0x10`). |
| login `+0xe68` | Received character vector, 0x50-byte entries. |
| `0x1421406f0` | Play with the chosen name view and list index. Set in-game state `+0xa48` to its class; push **state 11 (loading)**, which installs the assignment listener. Directly pushing state 4 skips that listener. |
| `0x140d0f740` | Existing root login state's logout routine; preserves the correct login form lifecycle. |
| `0x140dfb760` / `0x140dfb8c0` | Existing buffered socket writer/flush, for `0x153` return to selection and `0x15` slash commands. Encryption remains the client's. |

Live verified 2026-10-25: fresh `game:open`, login, actual level-100 OfflineWitch list, Play, return to selection, logout,
wrong-password rejection, successful retry, Play again, and `/hit physical 1` received by the Gateway. In one persistent
run: login 0.7–0.9 s, list 94 ms, Play 1.4–2.2 s, command submission 94 ms; timings include game frames and loading,
and are observations rather than guarantees. Only the Witch class was exercised live. An attached bridge is for the
local development client; it has no official-service support.

Checks: `node --check scripts/dev/client-control.js` and
`.venv/Scripts/python.exe -m unittest discover -s scripts/dev -p test_client_control.py`.
`powershell -NoProfile -File scripts/dev/test_live_client_only.ps1` checks closed/existing client startup,
local gateway selection, and failed preflights with mocked processes and no GGPK writes.


## Targeted development interactions

JSON RPC also accepts `game:resources` (read-only Life component pools and recent 0x1b1 corrections),
`object:interact` with an object `id`, `move` with grid `x`/`y`, `npc:option` with row `index`, `npc:close`,
`reward:claim` with choice `id` and main-inventory `x`/`y`, and `inventory:pickup`/`inventory:place` with `inventoryId`
and item `id` or destination `x`/`y`. These use the existing C?S interaction messages. Their result acknowledges
submission; wait for the resulting state or logs. Movement uses the client's Move action (0x2909). Interaction from
far away can fail to find a path; move closer and retry. Resource updates are bounded to 128 entries and drained on
read, so persistent RPC is needed to observe a recovery interval. This measures client state, not HUD pixels.

Live interaction checks included Tarkleigh's reward claim, starter weapon pickup/equip, the biting zombie's death and
gem pickup, walking to the support chest and opening it. The client stayed attached through these tests. RPC completion
is deferred until its native callback has returned, so a one-shot process can detach without unloading a hook while
it is still unwinding. Prefer loading additional probes into the same Frida session when extending this tool.
