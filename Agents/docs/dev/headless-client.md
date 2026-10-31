# Headless ("nullified") client for agent sessions

Goal: many automated clients on one PC, each as cheap as possible in CPU, RAM and GPU, ideally with no GPU device,
no textures and no rendering, while the game logic, the network and the [client-control bridge](client-control.md)
keep working. The tools are opt-in and do not change `live.ps1`'s normal client.

| File | What it is |
|---|---|
| `scripts/dev/nullifier.js` | Frida agent loaded while the client is still suspended at spawn: the engine's Null renderer, per-client settings file, frame-rate cap, a window that never takes focus, a frame counter |
| `scripts/dev/nullifier.py` | launcher: spawns `Client/Game.patched.exe` with the agent, applies the process limits, resumes it, then serves client_control's JSON-lines commands for **that pid only** (`--stdio`); closes the client when it ends |
| `config/nullifier_Config.ini` | settings preset (DirectX 11, 800×600 window, low textures, 10 fps, effects off, sound muted) copied into each client's own settings file |
| `scripts/dev/nullifier-measure.ps1` | read-only sampler of one pid: private commit, working set, CPU, GPU engine utilisation, dedicated/shared GPU memory |
| `scripts/dev/nullifier-bench.py` | the measurement scenario: launch, log in, play, stand in town, measure, then waypoint travel, `/goto` back, chat, `/tp`, measure again |

## 1. Using it

The local servers must already run (Auth, Patch, Gateway, on any ports) with their logs in this checkout's `logs\`
(client_control reads slash-command replies from `logs\live-gateway.log`). Then:

```powershell
.venv\Scripts\python.exe scripts\dev\nullifier.py --patch-port 12995 --login-port 20493 --stdio
# first line: {"ok": true, "result": {"pid": 1234, "config": "...\\logs\\nullifier\\client-5678_Config.ini", ...}}
# then client_control's requests, one JSON object per line, for this client only:
{"command":"login","account":"offline","password":"..."}
{"command":"character:play","name":"OfflineWitch"}
{"command":"command","text":"/waypoints all"}
{"command":"waypoint:travel","area":"0x7ad2"}
{"command":"nullifier:stats"}
```

Defaults (the measured agent default, §3, §4): `--renderer configured` (the preset's DirectX 11 at 10 fps),
`--frame-sleep-ms 0`, `--priority below-normal`, EcoQoS on, `--window normal` (shown, never activated),
`--nosound --nologo`; without `--cwd`, a fresh settings file from the preset under `logs\nullifier\`.
`--no-eco-qos`, `--priority normal`, `--sound` turn the process tier off; `--affinity 0x3`, `--trim-seconds 10` add
more. Experimental (§2: the client crashes on entering an area): `--renderer null`, with `--frame-sleep-ms 50`,
`--no-render-jobs`, `--no-texture-upload`. A client that crashes appends its registers and a backtrace to
`logs\nullifier\crashes.log` (`--crash-log`); `--trace-files <regex>` logs matching `CreateFileW` paths.

Commands added to client_control's set: `waypoint:travel` (`area`: a WorldAreas HASH16; sends the World panel pin's
C→S 0x4d, [16](../protocol/16-waypoints.md) §3; the Gateway accepts it only for an activated waypoint, so use
`/waypoints all` first) and `nullifier:stats` (main-loop frames and frames per second since the previous call).
Everything else is client_control's `execute`, bound to the spawned pid instead of "the only Game.patched.exe running".

The client lives exactly as long as `nullifier.py`: the hooks exist only while it is attached, and an unhooked client
would save its settings over the user's `poeoffline_Config.ini`, so the launcher terminates its client when its stdin
closes or it is interrupted. It never looks a process up by name and kills nothing but its own child; the pid is
always checked together with its creation time, because Windows hands an exited client's pid to the next new process
(seen live: before this check the bridge attached to such a process once and failed at its module check).

### With several clients (live.ps1 sessions)

The multi-instance work (`contrib/multi-instance`) gives each checkout its own server ports and each named client
session its own working folder, `data\clients\<session>\`, with a hard link to the shared `Content.ggpk` and a
`config.json` naming the session's settings file and saved login (§2). The nullifier is a mode such a session opts
into rather than a second launcher:

- seed the session's settings file from `config/nullifier_Config.ini` instead of `poeoffline_Config.ini` (it has no
  `[LOGIN]` section, as the sessions require);
- start `nullifier.py --cwd <session folder> --patch-port <stack patch port> --login-port <stack login port>
  --record data\clients\<session>.json` detached instead of `Game.patched.exe`: with `--cwd` it redirects no settings
  file (the session's `config.json` decides), and `--record` writes `{pid, started (FILETIME UTC), launcher, ...}` so
  the session's client is tracked, found by the bridge and stopped by its pid as before. Stopping the client ends the
  launcher; ending the launcher closes the client.

On `main` Auth's instance info always names port 6112, so a client logging in through a private Auth on other ports
would still be sent to whatever Gateway listens on 6112; the multi-instance branch sends it to
`Game_GATEWAY_HOST:Game_GATEWAY_PORT`. Use the nullifier on private ports only on top of that change.

## 2. What the client offers (3.29.3b, image base `0x140000000`)

### Settings file and environment

- The settings file is `Documents\My Games\Path of Exile\poeoffline_Config.ini` (the copied client's default name,
  patched by the setup from `production_Config.ini`). The client builds that name in `0x140e2c340` and then reads
  `config.json` relative to the working directory (`Client\`) if it exists, a JSON object whose keys `GGG_CONFIG_FILE_NAME`, `GGG_CREDENTIAL_FILE_NAME`,
  `GGG_LOGIN_SERVER_PORT`, `GGG_BASE_WEB_CDN_URL`, `CONFIGURATION_NAME`, ... override the built-in values
  (`0x140e2d200` reads a JSON string member; the `"Loading config file: "` log line names `config.json`). These are
  **not environment variables**: the strings exist only as JSON keys, and the scripts' `pop` of an environment variable
  `GGG_CONFIG_FILE_NAME` has no effect. The client also opens `Content.ggpk` and writes `logs\Client.txt` in its
  working folder, which is what live.ps1's session folders use (§1). A client started in the shared `Client\` folder,
  where no `config.json` may be added, gets its own settings from `nullifier.js` instead: it rewrites the path of every
  `CreateFileW`, `GetFileAttributes(Ex)W`, `DeleteFileW` and `MoveFileExW` (destination) that ends in
  `\poeoffline_Config.ini` to the client's own file.
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
- **Live (2026-10-30, three runs, one with `--no-render-jobs --no-texture-upload`): not usable yet.** The client
  starts the Null device, reaches the login screen in 1.2–3.5 s, logs in through the bridge and lists the characters.
  Play generates the town (`Generating level 13 area "1_1_town"`; the client connects to the Gateway and receives
  the area) with hundreds of `[ENTITY2] … Shader generation failed` CRIT lines (the Null shader "builds" produce
  nothing), then the main thread dies with an access violation at `0x142be6be4` (`movss xmm0, [rsi]` right after the
  4×4 transform `0x140e43630`; `rsi` points 3.4 GB past the array in `r14`, an index computed from data the Null
  device does not provide) 3–6 s after entering the area, every time. It is the area/scene code on the main thread,
  not the frame render jobs (disabling them changes nothing). Private commit was 1.8 GB at that moment, against
  3.3 GB for the DirectX 11 preset client. Making it usable means patching that path and whatever fails next; the
  crash log (`--crash-log`) has the registers and the backtrace.

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
- Frame pacing: the main loop's per-frame state-stack update (`0x141526c40`; client-control serves its RPCs 0x7f bytes
  into it, at `0x141526cbf`, and checks those bytes, so the nullifier hooks the entry) is where `nullifier.js` counts
  frames and sleeps `--frame-sleep-ms`; with the Null device there is no present or vsync to wait on. With a real
  renderer the preset's `framerate_limit=10` does it: 9.2 frames per second measured, and game logic, the network and
  the bridge keep working at that rate (the server is authoritative; the client only predicts and reports).

### Process level

`nullifier.py` sets the priority class (`below-normal` by default), EcoQoS (`SetProcessInformation(
ProcessPowerThrottling, EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION)`: low clocks / efficient cores, and the client's
`timeBeginPeriod` no longer raises the system timer rate), optionally the CPU affinity and a periodic
`EmptyWorkingSet` (moves pages to the standby list; private commit is unchanged, so it helps physical RAM, not the
commit limit). The window is shown without activation (`SW_SHOWNOACTIVATE`, default), minimized
(`SW_SHOWMINNOACTIVE`) or hidden, and `SetForegroundWindow` is neutralised, so a launched client cannot take the
keyboard focus from the user's window. **A minimized client with a real renderer stops its main loop**
(`[WINDOW] Minimized`, no frames: it never reached the login screen in 60 s), so `--window minimized` (and, untested,
`hidden`) only suits the Null renderer, which ran minimized. A hidden window also hides an error dialog.

## 3. Measurements

Scenario (`nullifier-bench.py`): fresh client, bridge login, `OfflineWitch` played into Lioneye's Watch, 10 s settle,
then 60 s sampled every 5 s by `nullifier-measure.ps1`; then `/waypoints all`, `waypoint:travel` to The Coast,
`/goto 1_1_town`, `/pos`, chat, `/tp`, and 20 s more. CPU is percent of one core (16 logical cores). Machine: RTX 5070
Ti + Radeon iGPU, 31 GB RAM.

Measured 2026-10-30/31 under a reduced gate (free physical RAM ≥ 8 GB and free commit ≥ 3 GB at launch, the shared
extra-client lock held, and a watchdog that killed only this run's client when free commit fell below 1.5 GB or free
RAM below 4 GB). Other agents' test hosts (up to 13 GB each) and client restarts made the machine's free commit swing
between 0.3 and 9 GB: the watchdog fired 8 times on its own client, and another session's `live.ps1` killed the
measurement servers twice and one measured client (it stops every `Game.patched` and `GameServer.*` process by
name). The full-quality baseline and WARP were skipped on purpose (the reference row stands in for the baseline).

| Tier | Settings | Private commit | Working set | CPU (% of a core) | GPU util | GPU dedicated | Bridge |
|---|---|---|---|---|---|---|---|
| reference: a normal client of another session (read-only counters, Vulkan, high textures, unfocused, state unknown) | user's | 7.49 GB | 4.27 GB | 37.5 | 9 % | 3.38 GB | — |
| 1+2 preset + process, in town (60 s) | `config/nullifier_Config.ini` (DX11, 800×600, low textures, 10 fps), below-normal, EcoQoS, `--window normal`, `--nosound` | 4.33 GB (max 4.37) | 2.46 GB | 50.7 avg (311 in the first 5 s, shader compiles; ~11 after) | 7 % | 1.66 GB | login, list, play ✓ |
| 1+2 after waypoint travel and `/goto` back (20 s) | same | 4.59 GB | 2.74 GB | 10.7 | 4.2 % | 1.78 GB | `/waypoints all`, `waypoint:travel` to The Coast (`(1431, 626) in 1_1_2`), `/goto 1_1_town`, `/pos`, chat, `/tp +5 +0` ✓ |
| 3b Null device (`--renderer null --frame-sleep-ms 50`) | preset, tier 2 | 1.8 GB at the crash | 1.65 GB | — | 0 | 0 | login, list ✓; play: crash 3–6 s after entering the town (§2) |
| 3c Null + `--no-render-jobs --no-texture-upload` | same | — | — | — | — | — | same crash |
| 2 with `--window minimized`, DX11 | preset | — | — | — | — | — | no frames while minimized: never reached login |

The preset client ran at 9.2 frames per second (`nullifier:stats`), entered the town 5.8 s after launch (menu 2.5 s,
login 0.8 s, play 2.5 s) and kept 107 threads. Against the reference client: private commit −42 %, working set −42 %,
dedicated GPU memory −51 %, GPU load roughly halved, and CPU at the 10-fps cap about 11 % of a core, under a third
of the reference's 37 %, once the first shader compiles are done (the DirectX 11 shader cache is shared, so later
clients compile less).

Run a row with, for example:

```powershell
.venv\Scripts\python.exe scripts\dev\nullifier-bench.py --label preset --patch-port <p> --login-port <a> --travel -- --renderer configured
```

Results append to `logs\nullifier\bench.jsonl` (the measure JSON plus `nullifier:stats` frames and the options).

## 4. What is safe to default for agent sessions

- **Default now (measured): the preset + process tier**, i.e. `nullifier.py` with no renderer options: DirectX 11 at
  800×600, low textures, 10 fps, effects and sound off, below-normal priority, EcoQoS, a window shown but never
  activated. Every bridge command used by agents worked (login, character list, play, slash commands with replies,
  chat, `/tp`, waypoint travel, `/goto`). It still needs a GPU (1.7 GB of VRAM per client) and about 4.5 GB of
  commit, so a 31 GB machine fits a handful of them next to the servers, not dozens.
- **Not a default:** `--window minimized`/`hidden` with a real renderer (the client stops), `--renderer null`
  (crashes on entering an area), `--trim-seconds` (unmeasured; it cannot lower commit).
- **Next steps for a GPU-less client:** fix the Null device's crash at `0x142be6be4` (find which scene object the
  index comes from and give it a valid size, or skip that update when the device is Null), then rerun the bench with
  `--renderer null --travel`; if that path keeps failing, try WARP (`adapter_name=Microsoft Basic Render Driver` in
  the preset, no hook), which trades the GPU for CPU.
