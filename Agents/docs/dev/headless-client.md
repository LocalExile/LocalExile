# Headless ("nullified") client for agent sessions

Goal: many automated clients on one PC, each as cheap as possible in CPU, RAM and GPU, ideally with no GPU device,
no textures and no rendering, while the game logic, the network and the [client-control bridge](client-control.md)
keep working. The tools are opt-in and do not change `live.ps1`'s normal client.

| File | What it is |
|---|---|
| `scripts/dev/nullifier.js` | Frida agent loaded while the client is still suspended at spawn: a window that never takes focus; on request the engine's Null renderer (with the Null index-buffer fix), a frame-sleep cap, a frame counter, a crash log |
| `scripts/dev/nullifier.py` | launcher: prepares or checks the client's own folder, spawns `Client/Game.patched.exe` there with the agent, puts it in a kill-on-close job, applies the process limits, resumes it, then serves client_control's JSON-lines commands for **that client only** (`--stdio`) |
| `config/nullifier_Config.ini` | settings preset (DirectX 11, 800×600 window, low textures, 10 fps, effects off, sound muted, no `[LOGIN]`) that seeds each client's own settings file |
| `scripts/dev/nullifier-measure.ps1` | read-only sampler of one pid: private commit, working set, CPU, GPU engine utilisation, dedicated/shared GPU memory |
| `scripts/dev/nullifier-bench.py` | the measurement scenario: launch, log in, play, stand in town, measure, then waypoint travel, `/goto` back, chat, `/tp`, measure again; lock, memory gate, watchdog, call timeouts |

## 1. Using it

The local servers must already run (Auth, Patch, Gateway, on any ports) with their logs in this checkout's `logs\`
(client_control reads slash-command replies from `logs\live-gateway.log`). Then:

```powershell
.venv\Scripts\python.exe scripts\dev\nullifier.py --session bot1 --patch-port 12995 --login-port 20493 --stdio
# first line: {"ok": true, "result": {"pid": 1234, "started": …, "cwd": "…\\data\\clients\\bot1", "settings": "localexile-<checkout>-bot1_Config.ini", …}}
# then client_control's requests, one JSON object per line, for this client only:
{"command":"login","account":"offline","password":"..."}
{"command":"character:play","name":"OfflineWitch"}
{"command":"command","text":"/waypoints all"}
{"command":"waypoint:travel","area":"0x7ad2"}
{"command":"nullifier:stats"}
```

Defaults (§3, §4): `--renderer null` (the engine's Null device: no GPU device, no GPU memory), the preset's 10 fps,
`--priority below-normal`, EcoQoS on, `--window normal` (shown, never activated), `--nosound --nologo`.
`--renderer configured` keeps the preset's DirectX 11 (the fallback if an area breaks under the Null device);
`--trim-seconds 10` trims the working set (measured below; it cannot lower commit); `--no-eco-qos`,
`--priority normal`, `--sound`, `--affinity 0x3`, `--frame-sleep-ms N` (a per-frame sleep, not needed with the
preset's cap), `--frame-stats` (frame counter for `nullifier:stats`), `--crash-log <file>` (registers and backtrace of
a crash), `--trace-files <regex>` (log matching `CreateFileW` paths) and `--probe <js>` (append a diagnostic script to
the agent) are the other switches. `--no-render-jobs`/`--no-texture-upload` stalled the main loop (§3).

**Settings and folder.** The client opens `config.json`, `Content.ggpk`, its asset folders and `logs\Client.txt` in
its working folder (§2). `--session NAME` prepares `data\clients\NAME\` as live.ps1's `-Session` clients have it: a
hard link to `Content.ggpk` (the same file; nothing is copied or written), junctions to `Client\`'s asset folders
(`FMOD`, `D3D12`, `Streamline`, ...; not `logs`), and a `config.json` naming its own settings file and saved login
(`localexile-<checkout>-NAME_Config.ini` / `_login.credentials` in `Documents\My Games\Path of Exile`), the settings
seeded from the preset without `[LOGIN]`. `--cwd DIR` uses a folder prepared elsewhere. The launcher refuses the
shared `Client\` folder, a folder without `config.json`, and a `GGG_CONFIG_FILE_NAME` starting with `poeoffline_` or
`production_`, so a nullified client never writes the user's settings. (An earlier version redirected the settings
file through `CreateFileW`/`MoveFileExW` hooks instead; it missed the client's real save path and the user's
`poeoffline_Config.ini` was rewritten once, harmlessly, at 22:55:47 on 2026-10-30. That redirect is gone.)

**Lifetime and other processes.** Right after the spawn, while the client is still suspended, it goes into a job
object with `KILL_ON_JOB_CLOSE`, so it never outlives the launcher however that ends, and it is terminated when the
Frida session detaches (unhooked it must not keep running). Priority, EcoQoS, affinity, trimming, liveness and
termination all use the one process handle opened at the spawn; nothing looks a pid up again (Windows hands an exited
client's pid to the next new process; before this an earlier version once attached to such a process). The launcher
refuses to start a third client: the client allows two copies per PC (it takes `Global\PoERunMutexA` or `B`,
`0x140101d85`; a third waits invisibly on its "only two copies" dialog). In default mode the agent installs only the
focus guard; the engine hook, frame counter, exception handler and file hooks only with the options that need them.

Commands added to client_control's set: `waypoint:travel` (`area`: a WorldAreas HASH16; sends the World panel pin's
C→S 0x4d, [16](../protocol/16-waypoints.md) §3; the Gateway accepts it only for an activated waypoint, so use
`/waypoints all` first) and `nullifier:stats` (frames and frames per second since the previous call, with
`--frame-stats`). Everything else is client_control's `execute`, bound to the launched client.

A plain `client_control.py` on `main` finds its client by process name: it attaches to a nullified client if that is
the only `Game.patched.exe` running (and refuses when two run). Use the launcher's `--stdio` (or, after the
multi-instance merge, the session's bridge) to address one client among several.

### With several clients (live.ps1 sessions)

The multi-instance work (`contrib/multi-instance`) gives each checkout its own server ports and each named client
session its own working folder; the nullifier becomes a launch mode of such a session: `live.ps1 Start-Client
-Session S` writes the pending record and the session folder, then starts `nullifier.py --cwd data\clients\S
--record data\clients\S.json` detached. `--record` merges `{pid, started (FILETIME UTC), launcher, launcherStarted,
cwd, settings, options}` into the session's JSON atomically (temporary file + replace), so the session's client is
tracked, found by the bridge and stopped by its pid as before; stopping the client ends the launcher, and ending the
launcher ends the client.

On `main` Auth's instance info always names port 6112, so a client logging in through a private Auth on other ports
would still be sent to whatever Gateway listens on 6112; the multi-instance branch sends it to
`Game_GATEWAY_HOST:Game_GATEWAY_PORT`. Use the nullifier on private ports only on top of that change.

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
  and checks those bytes, so the nullifier hooks the entry), which is also where `--frame-stats` counts frames.

### Process level

`nullifier.py` sets the priority class (`below-normal` by default) and EcoQoS (`SetProcessInformation(
ProcessPowerThrottling, EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION)`, retried with `EXECUTION_SPEED` alone where
Windows 10 refuses the second flag: low clocks / efficient cores, and the client's `timeBeginPeriod` no longer raises
the system timer rate), optionally the CPU affinity and a periodic `EmptyWorkingSet` (`--trim-seconds`: pages move
to the standby list; private commit is unchanged, so it helps physical RAM, not the commit limit). The window is
shown without activation (`SW_SHOWNOACTIVATE`, default), minimized (`SW_SHOWMINNOACTIVE`) or hidden, and
`SetForegroundWindow` is neutralised, so a launched client cannot take the keyboard focus from the user's window.
**A minimized client with a real renderer stops its main loop** (`[WINDOW] Minimized`, no frames: it never reached
the login screen in 60 s). A hidden window also hides an error dialog.

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
.venv\Scripts\python.exe scripts\dev\nullifier-bench.py --label null --patch-port <p> --login-port <a> --lock D:\path\.extra-client.lock --travel -- --renderer null
```

Results append to `logs\nullifier\bench.jsonl` (the measure JSON plus `nullifier:stats` frames and the options; a
watchdog kill is recorded too).

## 4. What is safe to default for agent sessions

- **Default: the Null renderer with the preset and the process tier** (`nullifier.py` with no renderer options): no
  GPU device, ~1.9 GB commit and ~15 % of a core per client at 10 fps, every bridge command agents use verified in
  town, after waypoint travel and after `/goto`. Add `--trim-seconds 10` when many clients share the machine's RAM
  (resident memory 50–230 MB; commit unchanged). Only Lioneye's Watch and The Coast were entered; another area or
  combat may still reach a Null-device gap. Then use `--renderer configured` (DirectX 11 preset, 4.3 GB commit,
  1.7 GB VRAM, verified the same way) and report the crash (`--crash-log`).
- **Not a default:** `--window minimized`/`hidden` with a real renderer (the client stops),
  `--no-render-jobs`/`--no-texture-upload` (more CPU, and the main loop stalled), `--frame-sleep-ms` (the preset's
  cap already gives 9.2 fps).
- **Untried:** WARP (`adapter_name=Microsoft Basic Render Driver` in the preset, no hook), which trades the GPU for
  CPU and is not needed now that the Null device holds.
