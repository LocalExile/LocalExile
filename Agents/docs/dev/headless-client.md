# Headless ("nullified") client for agent sessions

Goal: many automated clients on one PC, each as cheap as possible in CPU, RAM and GPU, ideally with no GPU device,
no textures and no rendering, while the game logic, the network and the [client-control bridge](client-control.md)
keep working, and without getting in the way of the user's or other agents' clients. It is how `live.ps1` starts
every `-Session` client ([live testing](live-testing.md), "Several stacks and clients"); the default client (no
`-Session`) starts as before.

| File | What it is |
|---|---|
| `scripts/dev/nullifier.py` | the launcher `live.ps1` starts for a session client: spawns `Client/Game.patched.exe` suspended in the session folder, puts it in a kill-on-close job, loads the agent, applies the process limits, resumes it, records it, waits for it |
| `scripts/dev/nullifier.js` | its Frida agent, loaded before the client's first instruction: session-local two-copies mutexes and a window that never takes focus (always); with `-Nullifier` the engine's Null renderer (with the Null index-buffer fix); diagnostics on request |
| `scripts/dev/nullifier_Config.ini` | the settings preset (DirectX 11, 800×600 window, low textures, 10 fps, effects off, sound muted, no `[LOGIN]`) that seeds a new session's settings file |
| `scripts/dev/nullifier-measure.ps1` | read-only sampler of one pid: private commit, working set, CPU, GPU engine utilisation, dedicated/shared GPU memory |
| `scripts/dev/nullifier-bench.py` | the measurement scenario on a session client: start, log in, play, stand in town, measure, then waypoint travel, `/goto` back, chat, `/tp`, measure again; lock, memory gate, watchdog, call timeouts; `--survey` keeps a memory survey per measurement |
| `scripts/dev/nullifier-tour.py` | the coverage tour on a running session client: every act's town and a combat area, the Act 1 boss area, a map and the hideout, a fight with the default attack in each combat area, chat and `/tp`; client alive, frames and `/pos` checked per step (§7) |

## 1. Using it

```powershell
.\scripts\dev\live.ps1 -PortBase 21000 -NoClient                 # this checkout's servers on their own ports
.\scripts\dev\live.ps1 -ClientOnly -Session bot1 -Nullifier      # a GPU-less session client (or: control.ps1 game:open -Session bot1)
.\scripts\dev\control.ps1 login:offline -Session bot1 -PasswordStdin
.\scripts\dev\control.ps1 character:play:OfflineWitch -Session bot1
.\scripts\dev\control.ps1 command '/waypoints all' -Session bot1
.venv\Scripts\python.exe scripts\dev\client_control.py --session bot1 --stdio   # then, one JSON object per line:
{"command":"waypoint:travel","area":"0x7ad2"}
{"command":"game:frames"}
.\scripts\dev\live.ps1 -Stop -ClientOnly -Session bot1           # the launcher, and with its job the client
.\scripts\dev\live.ps1 -Agent                                  # servers + one -Nullifier client 'agent', ports picked if none recorded
```

A session client always gets its own folder and settings, the job object, below-normal priority and EcoQoS, a window
that is shown but never activated, no hold on the user's mouse and keyboard (§2, "The user's mouse and keyboard"), and
session-local two-copies mutexes. `-Nullifier` (`Game_CLIENT_NULLIFIER=1`) adds the Null renderer, trimming and the lean
memory options (`--renderer null --trim-seconds 10 --mimalloc purge_delay=0 --mimalloc arena_purge_mult=1
--lazy-null-buffers 8`, §6); without it the session's settings decide the
renderer, which for a new session is the preset's DirectX 11 (§4 explains the choice). The bridge commands are
client_control's; `waypoint:travel` (`area`: a WorldAreas HASH16; the World panel pin's C→S 0x4d,
[16](../protocol/16-waypoints.md) §3; the Gateway accepts it only for an activated waypoint, so `/waypoints all`
first) and `game:frames` (main-loop frames since the bridge attached, and frames per second since the previous call,
counted at client-control.js's own frame point) were added for these clients.

`nullifier.py` can also be run by hand on a prepared session folder, e.g. for diagnostics:
`nullifier.py --cwd data\clients\S --record data\clients\S.json [--renderer null] [--trim-seconds 10]
[--frame-sleep-ms N] [--crash-log <file>] [--trace-files <regex>] [--trace-movies] [--probe <js>] [--no-render-jobs]
[--no-texture-upload] [--window minimized|hidden|normal] [--priority …] [--no-eco-qos] [--affinity 0x3] [--sound]
[--mimalloc NAME=VALUE]… [--lazy-null-buffers KIB]` (ports default to the stack's, `data\stack.json`); through
`live.ps1`, `Game_NULLIFIER_ARGS` appends options to a session client's launcher (`nullifier-bench.py
--launcher-args`). `--no-render-jobs`/`--no-texture-upload` stalled the main loop
(§3).

**Settings and folder.** The client opens `config.json`, `Content.ggpk`, its asset folders and `logs\Client.txt` in
its working folder (§2). `live.ps1` prepares the session folder (`data\clients\S\`: a hard link to `Content.ggpk`,
junctions to `Client\`'s asset folders, and a `config.json` naming `localexile-<checkout>-S_Config.ini` /
`_login.credentials`, the settings seeded from the preset without `[LOGIN]`). The launcher refuses the shared
`Client\` folder, a folder without `config.json`, and a `GGG_CONFIG_FILE_NAME` starting with `poeoffline_` or
`production_`, so a session client never writes the user's settings. (An earlier version redirected the settings
file through `CreateFileW`/`MoveFileExW` hooks instead; it missed the client's real save path and the user's
`poeoffline_Config.ini` was rewritten once, harmlessly, at 22:55:47 on 2026-10-30. That redirect is gone.)

**Lifetime, records and other processes.** `live.ps1` writes the pending record (`pid` 0, the time before the start,
the session, folder and ports), starts the launcher detached and waits until the launcher has merged `pid`, `started`
(the client's creation time), `launcher` and `launcherStarted` into it (atomically: temporary file and replace); after
60 s, or when the launcher ends first, it stops the launcher and throws. The launcher puts the client in a job object
with `KILL_ON_JOB_CLOSE` while it is still suspended, so it never outlives the launcher however that ends, and it
terminates it when the Frida session detaches (unhooked it must not keep running). Priority, EcoQoS, affinity,
trimming, liveness and termination use the one process handle opened at the spawn; nothing looks a pid up again
(Windows hands an exited client's pid to the next new process; an earlier version once attached to such a process).
The bridge finds the client by the record (`ClientControl(client_session=S)`, PID and start time, started from this
checkout's `Client\` path). `-Stop` stops a launcher only when its PID, start time and command line match the record.

**The two-copies limit.** The client allows two copies per PC. Right at startup (`0x140101680`, at `0x140101d85`) it
calls `CreateMutexW(NULL, FALSE, "Global\PoERunMutexA")` and `WaitForSingleObject(handle, 0)`; if another process
owns it (`WAIT_TIMEOUT`) it does the same with `Global\PoERunMutexB`, and if that is owned too it throws "You may only
run up to two copies of Path of Exile at a time." (`0x140102587`), which shows as a dialog the client then waits on, so
a third copy hangs invisibly. The handles are never stored, released or looked at again: the mutexes exist only for
this count. A client of ours would therefore take one of the two machine-wide slots that the user's and the other
sessions' clients need. `nullifier.js` (always, renderer or not) rewrites exactly these two names in all six
name-taking variants, `CreateMutexW`/`CreateMutexExW`/`OpenMutexW` and their ANSI forms (the client imports
`CreateMutexW` for this check, and `CreateMutexA`/`OpenMutexA` in LMDB's code, `0x1411c0a40`), to
`Local\LocalExile-<checkout>-<folder>-PoERunMutexA`/`B`: a session-local name per client folder, so session clients
never take a Global slot and never block each other across folders (two clients in the same folder still share a
pair). The hooks are installed while the client is still suspended, before its first instruction, and each rename is
reported; the launcher waits up to 30 s for that report after resuming the client, and without it closes the client
and fails the launch, so a silent hook failure cannot leave a client holding a Global slot. Nothing else uses those
names. What the count does not guard, and what stays shared between all copies whatever their number:
`PathOfExileSingleInstance` (a session mutex for handing a `.build`/`.filter` file to a running client's window,
`0x140100780`), `Global\PoERunMutexCrashUpload` (crash-report upload), and LMDB's own `Global\MDB…` locks around the
shared shader and pipeline caches in `%LOCALAPPDATA%\Path of Exile` (multi-process safe by design, up to 126 readers);
these names are left alone. The startup "Wiping cache …tmp" of that folder runs in every copy, independent of the
mutexes. Settings, saved login and `Client.txt` are per folder; `Content.ggpk` is opened read-only and shared.
`live.ps1` checks the slots only for the default client, before it stops or starts anything: it refuses when two
clients hold Global slots. A client a launcher started, from any checkout, holds none, and is known by its parent (a
python running `nullifier.py`, started before it), by `--nologo` on its command line (only the launcher passes it),
or by this checkout's records. The renaming is checked offline (`scripts/dev/test-nullifier-mutex.cjs`, Frida's API stubbed);
live (§5) the session clients held `\Sessions\3\BaseNamedObjects\LocalExile-pr-nullifier-v1-PoERunMutexA` (and
`…-v2-…`) and no Global one, and while two of them ran a separate process could still take and release both
`Global\PoERunMutexA` and `B` with a zero wait.

A plain `client_control.py` from a checkout without sessions (`main` before the multi-instance work) finds its client
by process name: it attaches to any lone `Game.patched.exe`, a session client included, and refuses when two run. That
is why extra clients may only run next to checkouts that have the session-aware scripts ([live
testing](live-testing.md)).

## 2. What the client offers (3.29.3b, image base `0x140000000`)

### Settings file and environment

- The default settings file is `Documents\My Games\Path of Exile\poeoffline_Config.ini` (the copied client's default
  name, patched by the setup from `production_Config.ini`). The client builds that name in `0x140e2c340` and then
  reads `config.json` from its working folder if it exists, a JSON object whose keys `GGG_CONFIG_FILE_NAME`,
  `GGG_CREDENTIAL_FILE_NAME`, `GGG_LOGIN_SERVER_PORT`, `GGG_BASE_WEB_CDN_URL`, `CONFIGURATION_NAME`, ... override the
  built-in values (`0x140e2d200` reads a JSON string member; the client logs `Loading config file: config.json`).
  These are **not environment variables**: the strings exist only as JSON keys, and the scripts' `pop` of an
  environment variable `GGG_CONFIG_FILE_NAME` has no effect. Content.ggpk and `logs\Client.txt` are also opened in
  the working folder. The client logs to its standard output too, so the launcher spawns it with piped stdio.
- Command-line flags parsed in `0x1400f8450`: `--nosound`, `--softwareaudio`, `--nologo`, `--no-device-recovery`,
  `--no-nv` (no NVIDIA Streamline/DLSS), `--no-agility-sdk`, `--disable-dx12-pso-cache` / `--enable-dx12-pso-cache`,
  `--use-defaults`, `--allow-priority-boost`, `--disable-assert-box`, and the login/patching ones. None turns rendering
  off.
- `[DISPLAY]` keys read in `0x141908570`: `device_type` (`DirectX11`, `DirectX12`, `Vulkan`), `adapter_name` /
  `adapter_uid`, `resolution_width/height`, `fullscreen`, `borderless_windowed_fullscreen`, `maximize_window`,
  `texture_quality` (`TextureQualityLow` / `TextureQualityHigh`), `texture_filtering`, `vsync`, `triple_buffering`,
  `framerate_limit` (+0x50) / `framerate_limit_enabled` (+0x54), `background_framerate_limit` (+0x58) /
  `background_framerate_limit_enabled` (+0x5c), `shadow_type`, `light_quality`, `global_illumination_detail`,
  `water_detail`, `screenspace_effects(_resolution)`, `bloom_strength`, `depth_of_field`, `upscale*`,
  `use_dynamic_resolution` / `dynamic_resolution_fps`, `use_dynamic_particle_culling`. A client that is not the
  foreground window runs at `background_framerate_limit` (10 by default), which is the case of every agent client.

### The engine's Null device

The renderer has a sixth device type besides DirectX 11/12, Vulkan and the console ones: **Null**.

- `Engine::Init` (`0x1410048f0`, `(engine, options)`) copies the engine options' switches and logs them: options
  `+0x2d5` is `[RENDER] Render: ON`, `+0x2bf` `[TEXTURE] Upload`, `+0x2bc` `[TEXTURE] Streaming` (also
  `[RENDER] Async`), `+0x306` `[ENGINE] Test Disable Frame Render Jobs`, `+0x303`/`+0x304` the synchronous UI and
  simulation test switches. The Render switch goes to the renderer's init `0x140faf1e0` as its fourth argument and is
  stored at renderer `+0x14f3b8`.
- The device start `0x140fadb20` (and the second device start `0x140fb4340`) replace the settings' device type with **6**
  when that switch is off; otherwise only 0–3 are accepted. `0x140fd39e0` maps settings type 6 to device enum 0, which
  `[RENDER] Starting device: ` logs as `Null` and the device factory `0x140e6a0a0` builds as a 0x298-byte object with
  vtable `0x143202f10`: 36 methods, 30 of them `ret`, `return 0` or `return false`. There is a matching
  `ShaderCacheNull` cache name. No D3D or Vulkan device, swap chain, GPU memory or texture upload exists with it.
- Nothing in the shipped client turns the switch off (it is not a flag or a settings key), so `nullifier.js` hooks
  `0x1410048f0` at spawn and writes options `+0x2d5 = 0` before the engine reads it (`--renderer null`).
  Startup still enumerates the adapters (`0x140f0eca0` creates a throw-away D3D12 and D3D11 device on each adapter to
  log its feature level, `Enumerated device for adapter …`), which touches the GPU driver once.
- **The Null index buffer's missing "describe".** Live, the client first reached character selection, generated the
  town and then died 3–6 s later with an access violation at `0x142be6be4` (`movss xmm0, [rsi]`, `rsi` 3.4 GB past
  the vertex array). That is the triangle test of the mesh ray cast `0x142be7270` (`0x142be6b10`, called per
  triangle with three vertex pointers `vertices + index × stride`). It asks the index buffer to describe itself
  (vtable `+0x20`, out: u32 format, `0x65` = 16-bit, `0x66` = 32-bit; u32) and then Maps it (`+0x10`). The Null
  device's index buffer (vtable `0x14320e140`, a 0x150-byte object made in `0x140e939b0`) keeps a zero-filled byte
  vector of the buffer's size at `+0x130` (Map returns its start) and the index kind at `+0x148` (1: 16-bit, 2:
  32-bit; the DirectX 12 buffer keeps the same value at `+0x318`, and its describe `0x140ee7990` returns `0x65`/`0x66`),
  but its describe slot is the shared no-op `ret` `0x1400f0f00`. The caller then compared an uninitialised format,
  read a 16-bit buffer as 32-bit, ran past its end (a probe showed 378 bytes, 756 read) and turned heap garbage into
  indices. `nullifier.js` (`--renderer null`) points that one vtable slot at a small machine-code stub that answers like the
  DirectX 12 buffer from the Null buffer's own fields (format from `+0x148`, index count from the vector's size).
  The indices themselves stay zero (the Null device keeps no uploaded data), so these ray casts hit nothing, which
  the client tolerates; the server is authoritative for movement and position.
- With that, the Null client enters areas, stays 60 s in town, travels by waypoint and changes area by `/goto`, and
  the bridge's commands all work (§3). The engine still logs hundreds of `[ENTITY2] … Shader generation failed` CRIT
  lines per area (the Null shader "builds" produce nothing); they are harmless.

### Graphics API paths (for the other options)

- `d3d11!D3D11CreateDevice` is imported directly (IAT `0x142f43f88`), `dxgi!CreateDXGIFactory1` too (`0x142f43fb8`);
  `d3d12.dll` (ordinal 101) and `vulkan-1.dll` are delay-loaded. The DirectX 11 device (`0x140e94e10`, 0x458 bytes)
  calls `D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, …, 4 feature levels, SDK 7)` with the IDXGIAdapter the
  settings selected (`adapter_name`/`adapter_uid` among the enumerated ones, at `+0x238` of the adapter record).
- **WARP** (software rasteriser): the enumeration lists `Microsoft Basic Render Driver` (feature level 0xc100) next to
  the real GPUs, so `device_type=DirectX11` + `adapter_name=Microsoft Basic Render Driver` selects it with no hook.
  It moves all rendering to the CPU and textures to system memory: GPU-less, but expensive.
- `D3D_DRIVER_TYPE_NULL` needs the debug-layer `d3d11ref.dll`, which is not installed here; the engine's own Null
  device makes it unnecessary.
- Frame pacing: the preset's `framerate_limit=10` caps every renderer, the Null one included (9.2 frames per second
  measured with both); game logic, the network and the bridge keep working at that rate (the server is
  authoritative; the client only predicts and reports). `--frame-sleep-ms` adds a sleep at the main loop's
  per-frame state-stack update (`0x141526c40`; client-control serves its RPCs 0x7f bytes into it, at `0x141526cbf`,
  and checks those bytes, so the nullifier hooks the entry). `game:frames` counts frames at client-control.js's own
  point.

### Process level

`nullifier.py` sets the priority class (`below-normal` by default) and EcoQoS (`SetProcessInformation(
ProcessPowerThrottling, EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION)`, retried with `EXECUTION_SPEED` alone where
Windows 10 refuses the second flag: low clocks / efficient cores, and the client's `timeBeginPeriod` no longer raises
the system timer rate), optionally the CPU affinity and a periodic `EmptyWorkingSet` (`--trim-seconds`: pages move
to the standby list; private commit is unchanged, so it helps physical RAM, not the commit limit). The window is
created with `WS_EX_NOACTIVATE` (live: without it a new client's window took the keyboard focus at creation; with
it the foreground window stayed the user's and the clients' logs show no focus at all; it also keeps them off the
taskbar), shown without activation (`SW_SHOWNOACTIVATE`, default), minimized (`SW_SHOWMINNOACTIVE`) or hidden, and
`SetForegroundWindow` is neutralised, so a launched client cannot take the keyboard focus from the user's window.
**Minimizing does not stop it.** The engine pauses its main loop when its window procedure (`0x140e67380` →
`0x140e68220`) sees `WM_SIZE` / `SIZE_MINIMIZED` (it logs `[WINDOW] Minimized`): live, a DirectX 11 client started
minimized never reached the login screen, and a Null client someone minimized stopped answering the bridge. The agent
turns that one message into `WM_NULL` before the engine sees it; the window still minimizes, and the engine keeps its
last size and runs at its background frame rate (live: both renderers minimized at 9 fps, `/pos`, waypoint travel,
`/goto`, chat and `/tp` answered). A hidden window also hides an error dialog.

### The user's mouse and keyboard

A session client never holds the user's input (live 2026-10-31: a new session client locked the user's mouse inside
its window). The bridge drives the client through its own calls and packets, so none of this is needed by it. For every
session client, renderer or not, `nullifier.js` replaces these `user32` calls at spawn:

- `ClipCursor` (the window procedure `0x140e67df0` / `0x140e68220`, the `confine_mouse_to_window` setting): returns
  success and does nothing, not even `ClipCursor(NULL)` (which would free the user's own client's confinement). The
  preset also sets `confine_mouse_to_window=false`.
- `SetCursorPos` / `SetPhysicalCursorPos` (3.29.3b imports neither): do nothing. `SetCapture` (41 call sites): does
  nothing. `ShowCursor` keeps the display count the client expects without hiding anything; `SetCursor(NULL)` does
  nothing.
- `SetWindowsHookExW/A`: global input hooks are refused (`NULL`, which the client tolerates). 3.29.3b installs a
  low-level keyboard hook (`WH_KEYBOARD_LL`, procedure `0x140e68f40`, at `0x140e6905c`), which would see every key the
  user presses; `WH_MOUSE_LL` and thread-less `WH_KEYBOARD`/`WH_MOUSE` are refused too.
- `RegisterRawInputDevices` (`0x14103b998`; the client registers usage 1/4 and 1/5 with `RIDEV_INPUTSINK |
  RIDEV_DEVNOTIFY`): `RIDEV_INPUTSINK`, `RIDEV_EXINPUTSINK`, `RIDEV_CAPTUREMOUSE`/`RIDEV_NOHOTKEYS`, `RIDEV_APPKEYS` and
  `RIDEV_NOLEGACY` are removed, so the client never receives, captures or swallows the user's input in the background.
- `SystemParametersInfoW/A` with `SPI_SETFILTERKEYS` (0x33), `SPI_SETTOGGLEKEYS` (0x35) or `SPI_SETSTICKYKEYS` (0x3b):
  the client changes these accessibility keys for the whole Windows session (`0x140e68fb0`, `0x140e68939`, not saved)
  and puts them back only when it exits normally, which a client its job ends never does. They return success and
  change nothing; the GET calls pass.
- `ChangeDisplaySettingsExW` and the other `ChangeDisplaySettings` variants: return `DISP_CHANGE_SUCCESSFUL` and change
  nothing, so a session whose settings say `fullscreen=true` cannot change the user's display mode.
- No DirectInput: the client imports no `dinput8`.

Processes the client starts get the same isolation: the launcher turns on Frida's child gating, so a child (the
client starts `Game.patched.exe --crash-upload-startup` about a second after its own start) begins suspended, gets the
agent with the always-on parts only (mutex names, window, input; nothing that changes the engine), and then runs; it
stays in the client's job (live: `{"nullifierChild": …, "isolated": ["Game.patched.exe", "--crash-upload-startup"]}`).
The client is also started with `--disable-assert-box`, so a failed assertion cannot wait on a modal dialog nobody
answers.

Exports that share code are replaced once, and a failure is logged per API (an early version replaced two aliases,
threw, and the rest of the agent, the Null renderer included, never ran). Live (one client, a separate process
polling `GetClipCursor` every 100 ms while the client started, logged in, played, travelled and came back): Null 1494
samples and DirectX 11 1394 samples, none clipped (always the whole virtual screen), the foreground window never the
client's; `ClipCursor` was called and ignored; the client's own `GetRegisteredRawInputDevices` showed 1/4 and 1/5 with
flags 0. Offline: `test-nullifier-mutex.cjs`.

### Movies

The client plays Bink 2 movies through the delay-loaded `bink2w64.dll` (23 imports, `BinkOpen` … `BinkClose`). They
are UI video textures, decoded on the CPU and drawn like any texture; no game logic waits on them. The paths in the
executable: the startup logo `Art/Videos/GGG.bk2` (set up in `0x14152a5a0`), the loading screen `Loading.bk2`
(`0x141528d60`), and effect videos for UI panels (passive-tree clicks, Blight, Ultimatum/Ancestral trials, Sentinel,
Ritual, mystery boxes, tutorials). There are no act-ending or quest movies among them (those cinematics are in-engine).
`--trace-movies` logs each movie the client opens and closes. Live with the Null renderer through the whole coverage
tour (§7): `GGG.bk2` is never opened (`--nologo`, which the launcher always passes, skips it; the login screen is
reached 1–3 s after the start); the login screen's `POELogoCurseoftheAllflame.bk2` closes when the screen is left
(0.5–0.7 s, 9–11 frames); `Loading.bk2` opens for each area load and the engine closes it when the load ends (3.1–4.3
s, 59–81 frames), at the same 9 frames per second as the game; `VaalDarkness.bk2` is opened once and stays open as a
preloaded effect texture. None waits for a key press or for focus, so nothing needs skipping.

## 3. Measurements

Scenario (`nullifier-bench.py`): fresh client in its own session folder, bridge login, `OfflineWitch` played into
Lioneye's Watch, 10 s settle, then 60 s sampled every 5 s by `nullifier-measure.ps1`; then `/waypoints all`,
`waypoint:travel` to The Coast, `/goto 1_1_town`, `/pos`, chat, `/tp`, and 20 s more. CPU is percent of one core (16
logical cores). Machine: RTX 5070 Ti + Radeon iGPU, 31 GB RAM.

Measured 2026-10-30/31 with the extra-client lock held, a launch gate (free RAM ≥ 8–9 GB, free commit ≥ 3–6 GB) and a
watchdog that killed only the run's own client when free commit fell below 1.5 GB or free RAM below 4 GB. Other
agents' test hosts (up to 13 GB each) and client restarts made the machine's free commit swing between 0.3 and 11 GB:
the watchdog fired 9 times, and other sessions' old `live.ps1` killed the measurement servers five times and one
measured client (it stops every `Game.patched` and `GameServer.*` process by name). The full-quality baseline and
WARP were skipped on purpose (the reference row stands in for the baseline).

| Tier | Settings | Private commit | Working set | CPU (% of a core) | GPU util | GPU dedicated | Bridge |
|---|---|---|---|---|---|---|---|
| reference: a normal client of another session (read-only counters, Vulkan, high textures, unfocused, state unknown) | user's | 7.49 GB | 4.27 GB | 37.5 | 9 % | 3.38 GB | — |
| preset + process, DX11, in town (60 s) | preset, below-normal, EcoQoS, `--window normal`, `--nosound` | 4.33 GB | 2.46 GB | 50.7 (311 in the first 5 s while shaders compiled; ~11 after) | 7 % | 1.66 GB | login, list, play ✓ |
| same, after waypoint travel and `/goto` (20 s) | same | 4.59 GB | 2.74 GB | 10.7 | 4.2 % | 1.78 GB | all ✓ (below) |
| preset + process + `--trim-seconds 10`, DX11, in town (shader cache warm) | same | 4.32 GB | 0.23 GB (max 0.69) | 18.9 | 5.9 % | 1.64 GB | login, play ✓ |
| **Null renderer** (`--renderer null --frame-sleep-ms 50`), in town | preset, tier 2 | 1.90 GB | 1.76 GB | 14.9 | 0 | 0 | login, list, play ✓ |
| same, after waypoint travel and `/goto` (20 s) | same | 1.96 GB | 1.91 GB | 4.6 | 0 | 0 | all ✓ |
| **Null renderer + `--trim-seconds 10`** (no frame sleep), in town | preset, tier 2 | 1.89 GB | 0.12 GB (max 0.23) | 17.0 | 0 | 0 | login, list, play ✓ |
| same, after waypoint travel and `/goto` (20 s) | same | 1.97 GB | 0.05 GB | 6.3 | 0 | 0 | waypoint travel, `/goto`, `/pos`, `/tp` ✓ |
| Null + `--no-render-jobs --no-texture-upload --window minimized` | same | 1.79 GB | 1.76 GB | 26.5 | 0 | 0 | in town ✓ at 6.7 fps; the main loop then stopped (no frame for the bridge within 5 s) |
| Null before the index-buffer fix (3 runs) | same | 1.8 GB | 1.65 GB | — | 0 | 0 | login, list ✓; crash 3–6 s after entering the town |
| DX11 with `--window minimized` | preset | — | — | — | — | — | no frames while minimized: never reached login |

"All ✓": `/waypoints all` (reply listing every waypoint), `waypoint:travel` to The Coast (`/pos` then answered
`(1362, 626) in 1_1_2`), `/goto 1_1_town` (`Area generated at Seed: 1`), `/pos` (`(384, 224) in 1_1_town`), chat,
`/tp +5 +0` (`Teleported to (389, 224) in 1_1_town.`). Every client but the `--no-render-jobs` one ran at 9.2 frames
per second and entered the town 6–8 s after launch (menu 1–3 s, login 0.8 s, play 2.5–4.3 s). The Null client runs 41–43 threads against 107 for
DirectX 11. The session settings file was written by the client (`localexile-pr-nullifier-bench_Config.ini`), the
user's `poeoffline_Config.ini` not (it was written once afterwards by another session's default-folder client, which
uses that file by design).

Against the reference client, the Null renderer saves 75 % of private commit (1.9 against 7.5 GB), all GPU memory
and GPU time, and runs at about 15 % of a core in town (5–6 % standing after travel); with `--trim-seconds 10` its
resident memory drops to 50–230 MB. Against the DirectX 11 preset it saves 2.4 GB of commit and 1.7 GB of VRAM per
client.

Run a row with, for example:

```powershell
.venv\Scripts\python.exe scripts\dev\nullifier-bench.py --label null --session bench --nullifier --lock D:\path\.extra-client.lock --travel
```

It needs this checkout's servers on their own ports (`live.ps1 -PortBase`), starts the client with `live.ps1
-ClientOnly -Session bench [-Nullifier]`, drives it with `ClientControl(client_session="bench")` and stops it with
`live.ps1 -Stop -ClientOnly -Session bench`. Results append to `logs\nullifier\bench.jsonl` (the measure JSON plus
`game:frames` and the mode; a watchdog kill is recorded too). The measurements above were taken with the earlier
stand-alone launcher (the same spawn, agent, job and limits; it drove the client itself), before the run-mutex
renaming.

## 4. What is safe to default for agent sessions

- **Every session client: the launcher's safety and process tier** (own folder and settings, kill-on-close job,
  session-local two-copies mutexes, a never-activated window, no hold on the user's mouse and keyboard, below-normal
  priority, EcoQoS). None of it changes what the game does.
- **Default renderer for a new session: the preset's DirectX 11** (no `-Nullifier`). Live: login, play, slash
  commands, chat, `/tp`, waypoint travel and `/goto`; 4.3 GB commit, 1.7 GB VRAM, ~11 % of a core at 9 fps once its
  shaders are compiled. It is a real renderer with no reverse-engineered patch in the way, so it is the safe choice for
  an agent whose session may reach any area or screen.
- **`-Nullifier` for many clients or no spare GPU:** the Null renderer with trimming and the lean memory options
  (§6). Live the same way, ~1.6 GB commit, no GPU device or memory, ~20 % of a core in town and 5–9 % standing after
  travel, 50–230 MB resident. The coverage tour (§7: every act, a boss area, a map, the hideout, fights) passed with
  it. If a session still breaks under it, drop `-Nullifier` for that session and report the crash (`nullifier.py
  --crash-log`). `live.ps1 -Agent` starts the servers and one such client.
- **Not a default:** `--trim-seconds 60` (about 4 points of a core less in town, but 0.9 GB resident on average and
  1.5 GB at peak instead of ~0.1 GB, §6), `--mimalloc eager_commit=0` (no effect, §6).
- **Not a default:** `--no-render-jobs`/`--no-texture-upload` (more CPU, and the main loop stalled),
  `--frame-sleep-ms` (the preset's cap already gives 9.2 fps).
- **Untried:** WARP (`adapter_name=Microsoft Basic Render Driver` in the preset, no hook), which trades the GPU for
  CPU and is not needed now that the Null device holds.

## 5. Live verification of the live.ps1 integration (2026-10-31)

Run from this checkout on its own ports (`live.ps1 -PortBase 22000 -NoClient`), the extra-client lock held, a
watchdog on memory and on foreign clients or default-port servers (it never fired), no default client started.

| Step | Check | Result |
|---|---|---|
| 1 | `live.ps1 -ClientOnly -Session v1` (DirectX 11 preset) | ✓ in 2.1 s; record with `pid`, `started`, `launcher`, `launcherStarted`; `run mutex -> Local\LocalExile-pr-nullifier-v1-PoERunMutexA` in `logs\nullifier-v1.log`; handles: the Local name, no Global one |
| 1 | bridge on v1 | ✓ login, list, play (in game 5.9 s after the bridge started), `/pos`, `game:frames` 9 fps, `/waypoints all`, waypoint travel (`(626, 488) in 1_1_2`), `/goto 1_1_town`, chat, `/tp +5 +0` |
| 1 | cost (30 s in town) | 4.27 GB private, 2.90 GB working set, 10.4 % of a core, GPU 8 %, 1.66 GB VRAM |
| 1 | settings | the session's `localexile-pr-nullifier-v1_Config.ini` rewritten by the client at login (922 → 8349 bytes) |
| 1 | `-Stop -ClientOnly -Session v1` | ✓ launcher and client gone, record removed |
| 2 | `-Session n1 -Nullifier`, same bridge sequence | ✓ all; 1.89 GB private, 0.18 GB working set (trimmed), 23 % of a core, no GPU or VRAM |
| 3 | kill the launcher | ✓ its job ended the client at once |
| 3 | `-ClientOnly -Session f2` with this stack's Patch server stopped | ✓ refused before anything was written or started (`missing port 22000`); no record, launcher or client |
| 4 | v1 (DirectX 11) and v2 (`-Nullifier`) at once | ✓ both logged in and played the same character on the one Gateway; each held only its Local name; `Global\PoERunMutexA` and `B` were free (taken and released with a zero wait by a separate process) |
| 4 | focus | ✗ first run: each new window took the focus at creation; fixed (`WS_EX_NOACTIVATE`), rerun ✓: the foreground window unchanged, no focus in the clients' logs |
| 4 | minimized | ✗ first run: v2, minimized, stopped answering; fixed (the minimize message is withheld from the engine), rerun ✓: both minimized at 9 fps; v2 minimized did waypoint travel, `/goto`, chat, `/tp` |
| 4 | cost of the pair (20 s) | v1 4.28 GB / 2.72 GB / 11.6 % / 1.66 GB VRAM; v2 1.82 GB / 0.04 GB / 7.4 % / none |
| 4 | `-Stop -ClientOnly -Session v1` | ✓ v1 and its launcher gone, v2, its launcher and record untouched, v2 still in game (`/pos`) |
| — | user's `poeoffline_Config.ini`, `production_Config.ini` | ✓ mtime and SHA-256 unchanged |

Not covered live: another checkout's default client beside the sessions (the Global-slot count is tested with mocks),
`--window hidden`. (Areas beyond Lioneye's Watch and The Coast under the Null renderer: the tour, §7.)

## 6. Lean clients: where a Null client's commit goes, and what can go

A Null client in Lioneye's Watch commits ~1.9 GB of private memory. Most of it lives in two 1 GiB mimalloc arenas
(~790 and ~480–650 MB committed) and a 72 MB block, plus ~200 MB of image sections and ~300 MB of small heaps and stacks.
Mapped files (the `data.mdb` shader cache, 6.7 GB, and `Content.ggpk`) are read-only views and cost no commit. Survey:
a read-only per-allocation scan (committed, reserved, zero-page share, sample strings) of the client;
`nullifier-bench.py --survey <script>` stores one per measurement. The client's allocator is **mimalloc 2.2** (built
Aug 15 2025, linked into the executable): the engine's aligned allocation `0x142c839f0` is `mi_heap_malloc_aligned`
behind a 16-byte header (size, alignment), and the global counter `0x1445c8528` adds up what goes through it.

### 6.1 The game database cannot be shared between clients

The plan was to serve the loaded `Data/*.datc64` tables from one shared read-only section. The client's tables cannot
be shared that way:

- **How a table loads.** Each table type has a getter (BaseItemTypes: `0x14012c580`, the name `Data/BaseItemTypes.dat`)
  that asks the resource manager (`0x1421cfbd0`, 16 hash buckets by name) for it (`0x14014f380`); a new one is made by a
  per-type factory (`0x140156020`) on the generic resource (`0x1421d0f20`: name, load and free callbacks, an object of
  0xb8 bytes) and loaded by `0x1421d14c0`, which allocates the object and calls the type's loader (BaseItemTypes:
  `0x14239c9b0`). The loader reads the file into a temporary buffer, checks the 0xbb×8 marker after the rows, copies
  the rows into a vector (file row size: 0x176 bytes for BaseItemTypes) at object `+0x0` and the variable data (from the
  marker on) into its own buffer at `+0x18`, then builds the type's indexes (hash maps by Id and by other keys, a
  sorted vector).
- **The copy is rewritten in place** (`0x14239d3c0` for BaseItemTypes): each string offset becomes an absolute
  pointer into the variable data; each foreign key becomes (pointer to the target row, pointer to the target table's
  resource object), and that object's reference count is raised; lists of keys inside the variable data are rewritten
  the same way. The tables a table refers to are loaded first. So a loaded table is full of this process's addresses:
  heap pointers to its own data, to other tables' rows and to their (mutable: reference counts, last-use times)
  resource objects.
- **Measured** (`--probe` on `0x1421d14c0`, 2026-10-31): 469 tables are loaded by the time the client is in town,
  rows 97 MB and variable data 62 MB: 159 MB, not the 788 MB first estimated (that figure was the whole first arena).
- **Read-only after load:** with every page of all 469 tables made read-only after its load and access violations
  logged, the only writes came 34–47 s after load from the table destructor (`0x1401349b0` → `0x1421d1140`), when the
  login screen's tables were released. Nothing writes a table while it is loaded.
- **But different in every process:** two clients at the login screen, every table both had loaded compared byte for
  byte: 5 of 287 tables identical; of 162 MB, 5.99 M qwords differ (2.58 M of them pointer-like at 8-byte alignment;
  the rest are pointers at the odd offsets of rows like 0x176 bytes).
- **Why not map it at the same address everywhere:** the tables point at each other's resource objects, which are
  ordinary heap objects that change at run time, and tables load on several threads in whatever order they finish, so
  no two clients lay them out alike. Only the strings in the variable data are the same everywhere (at most ~60 MB per
  client), and serving those would mean taking buffers away from the allocator that frees them. Not done.

### 6.2 The allocator returns freed memory at once (`--mimalloc purge_delay=0 --mimalloc arena_purge_mult=1`)

mimalloc reads each option from `MIMALLOC_<NAME>` in its environment the first time it needs it; its option table
(`0x143a5ab90`, 37 entries of 0x20 bytes) holds the stock defaults (`eager_commit=1`, `eager_commit_delay=1`,
`arena_eager_commit=2`, `purge_decommits=1`, `purge_delay=10` ms, `arena_purge_mult=10`, `arena_reserve=1 GiB`).
`nullifier.py --mimalloc NAME=VALUE` sets one for the client only, and the agent logs the value the allocator then
uses (read back from the table 20 s after the start).

- `eager_commit=0` (commit segments on demand): no effect (town 1836 MB against 1840, after travel 1938 against 1927).
  The all-zero pages are not uncommitted segment space.
- `purge_delay=0` with `arena_purge_mult=1`: memory the game frees goes back to the system at once instead of after
  10 ms (100 ms in the arenas), which matters because area changes free and allocate in bursts: −174 MB in town and
  −220 MB after travel (two runs each, §6.5), CPU within the runs' spread. A default of `-Nullifier`.

### 6.3 The Null buffers' CPU copies are committed when first touched (`--lazy-null-buffers 8`)

Who owns the all-zero committed pages (a read-only scan of every pointer into each zero run, the holder rebased to the
image): mostly **Null device buffers**. Each Null buffer (the index buffer `0x14320e140`, the buffers `0x143212bc0`,
`0x143212be0`, `0x143212c00` and `0x14369c088`, made in `0x140e939b0`, `0x142bb4730`, `0x142bb4a00`, `0x142bb4cd0`,
`0x142bb5110`) keeps a byte vector of its full size, zero-filled by `vector<u8>::resize` (`0x140898aa0`) right after the
object is made, which `Map` returns. Nothing uploads to the Null device, so they stay zero: the GPU particle ring
buffers alone ("Particle bone buffer", "Particle instance buffer", "Particle Index Buffer") are tens of MB each (the
"72 MB pool, 98 % zero" is one of them), and with the mesh buffers they came to ~440 MB of all-zero committed pages.
They cannot simply be left out: `Map` callers read and write them (the mesh ray test reads the index buffers, §2).

With `--lazy-null-buffers KIB` (only with the Null renderer; all of it native code compiled by Frida's CModule, no
JavaScript per call) the agent:

- filters that resize by its return address: only its five construction calls (`0x140e93cc1`, `0x142bb496a`,
  `0x142bb4c34`, `0x142bb4f58`, `0x142bb547b`, each checked to be the call), and only a vector that was empty before
  the resize (so all of it is the zero fill) and is at least KIB KiB. Its whole pages are recorded, keyed to the
  vector, and then decommitted;
- handles, in a vectored exception handler, only read or write faults inside a recorded range: a reserved page there
  is committed (zero-filled, what the zero fill had left) and the access continues; a page another thread committed
  meanwhile also continues; anything else (other faults, other addresses, execute faults) goes on to the next handler;
- hooks the five destructors (`0x140ee8150`, `0x140f74000`, `0x140f740c0`, `0x140f74060`, `0x142bbb6b0`; the vector at
  `+0x130`, `+0x130`, `+0x140`, `+0x130`, `+0x1a0`), which commit the vector's range again and drop it before the vector
  is freed: the allocator gets back exactly the committed memory it handed out, and no range outlives its buffer.

Nothing else of these classes touches the vector (their other methods are `Map`, which returns it, and no-ops), so
every access during a buffer's life sees what it saw before (zeros, then its own data), and commit is charged only for
the pages that are touched. A system call reading into or writing from an untouched page of such a buffer would fail
instead of faulting; nothing passes a Null buffer's memory to the system (only `Map` exposes it, to the engine's own
copies and the mesh ray test). Up to 65,536 live ranges; past that, new buffers are left as they are. Live, in town:
1,229 live ranges, 182 MB decommitted, 4 MB touched again; after travel 165 ranges had been dropped with their
buffers. An earlier version (JavaScript filter, ranges never dropped) ran the whole coverage tour (24 areas: 8,196
vectors, 619 MB decommitted, 90 MB touched again) without a crash; the current one passed it as well (§7). A default
of `-Nullifier` (8 KiB).

What stays all-zero afterwards (~300 MB in town): 24 MB held by the shader source builder (`ShaderSourceBuilder`,
`0x140fdfa20`), smaller Null buffers below 8 KiB or grown later, ~13 MB of allocator page slack, and ~170 MB whose
holders the scan could not tie to a class.

### 6.4 Smaller items

- **Threads:** 46–48 in a Null client (107 with DirectX 11); none was removed.
- **Shader cache:** `data.mdb` (6.7 GB) is a mapped view of the LMDB file shared by every client: no commit, and with
  trimming it is not resident. Unchanged.
- **Trim interval:** `--trim-seconds 10` (the default) against 60, two clients at once with the lean options: town CPU
  20.0 against 15.8 % of a core, but resident memory 98 MB against 863 MB on average (1.5 GB at peak). Kept at 10 s.
- **Frame cap:** the preset's 10 fps (9.2 measured) is kept: a lower rate changes how often the client runs its game
  logic and sends, which these clients must not change.

### 6.5 Results

Same scenario every time (`nullifier-bench.py`, or the same steps with two clients: start, bridge login, play
OfflineWitch (the second client CampAccept, sent to Lioneye's Watch), 10 s, 60 s measured in town, `/waypoints all`,
waypoint travel to The Coast, `/goto 1_1_town`, `/pos`, chat, `/tp`, 20 s measured), after merging origin/main,
2026-10-31; private commit and working set averaged over the samples; CPU in percent of one core.

| One client | Private commit, town | after travel | Working set, town / after | CPU, town / after |
|---|---|---|---|---|
| Null, before (2 runs) | 1,931 / 1,926 MB | 1,982 / 2,046 MB | 82–146 / 47 MB | 15.4–17.0 / 6.9–7.3 |
| + `purge_delay=0`, `arena_purge_mult=1` (2 runs) | 1,748 / 1,760 MB | 1,814 / 1,774 MB | 89–93 / 46 MB | 17.9–18.6 / 5.4–5.8 |
| + lazy Null buffers ≥ 64 KiB | 1,615 MB | 1,628 MB | 87 / 47 MB | 19.3 / 6.7 |
| `-Nullifier` (+ lazy ≥ 8 KiB, JavaScript filter) | 1,579 MB | 1,580 MB | 104 / 47 MB | 21.0 / 9.4 |
| **`-Nullifier` now** (native filter, ranges dropped with their buffers) | **1,589 MB** | **1,584 MB** | 109 / 47 MB | 19.1 / 6.9 |

| Two clients at once, per client | Private commit, town | after travel | CPU, town / after |
|---|---|---|---|
| Null, before | 1,847 / 1,891 MB | 1,944 / 1,978 MB | 18.4, 18.6 / 4.4, 5.7 |
| **`-Nullifier` now** | **1,592 / 1,551 MB** | **1,584 / 1,603 MB** | 20.1, 20.0 / 4.2, 6.8 |

Per client that is about −0.30 GB in town and −0.37 GB after travel (−16 to −19 %); CPU is 1–3 points of a core higher
in town (the page faults and the immediate purges) and about the same after travel. **Projection for 8 clients** (one
per worktree): ~15.5 GB of commit before (1.93–2.01 GB each), ~12.7 GB now (1.57–1.60 GB each): ~2.8 GB less. Login,
character list, play, `/pos`, chat, `/tp`, waypoint travel and `/goto` passed in every run above.

## 7. Coverage: the tour

`nullifier-tour.py --session S` (the client started by `live.ps1 -ClientOnly -Session S [-Nullifier]`) logs in, plays
OfflineWitch, turns `/god` on (off again at the end), `/waypoints all`, and goes with `/goto` through Lioneye's Watch,
The Coast, the Act 1 boss area 1_1_7_2 (Brutus), every act's town and a combat area (acts 2–10), a map
(`MapWorldsBeach`) and the default hideout (`HideoutForest`); combat areas get a seed of their own. In each combat area
it spawns a Hungry Corpse (`/spawn_mob ZombieBite@13`), teleports next to it and attacks it with the default attack
(`object:interact` with skill `0x0102`) until the Gateway's log shows its life at 0, then moves (the Move skill). After
every step it checks that the client is alive, runs frames, and that `/pos` names the area; a client-side level
generation failure is reported as such. Results append to `logs\nullifier\tour.jsonl`.

Live with the Null renderer (2026-10-31, crash log on): 27/27 steps without the lean options and 27/27 with them (twice,
once through the plain `-Nullifier` defaults); all 12 fights killed their monster (the wand's hits at 8 a time, the
monster hitting back); 9.0–9.7 frames per second in every area; no crash. Before merging origin/main the client could
not build 2_6_1 at seed 1 ("Could not tilise edge BeachCliff", a known server-side issue fixed upstream by #413, with any
renderer) and stayed loading; after the merge it plays.
