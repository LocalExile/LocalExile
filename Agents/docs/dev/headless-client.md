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

Defaults: `--renderer null`, `--frame-sleep-ms 50`, `--priority below-normal`, EcoQoS on, `--window minimized`,
`--nosound --nologo`; without `--cwd`, a fresh settings file from the preset under `logs\nullifier\`. `--renderer configured` keeps the
settings' DirectX/Vulkan device (tier 1/2 only); `--no-eco-qos`, `--priority normal`, `--window normal`,
`--frame-sleep-ms 0`, `--sound` turn the other tiers off; `--affinity 0x3`, `--trim-seconds 10`,
`--no-render-jobs`, `--no-texture-upload` add more; `--trace-files <regex>` logs matching `CreateFileW` paths.

Commands added to client_control's set: `waypoint:travel` (`area`: a WorldAreas HASH16; sends the World panel pin's
C→S 0x4d, [16](../protocol/16-waypoints.md) §3; the Gateway accepts it only for an activated waypoint, so use
`/waypoints all` first) and `nullifier:stats` (main-loop frames and frames per second since the previous call).
Everything else is client_control's `execute`, bound to the spawned pid instead of "the only Game.patched.exe running".

The client lives exactly as long as `nullifier.py`: the hooks exist only while it is attached, and an unhooked client
would save its settings over the user's `poeoffline_Config.ini`, so the launcher terminates its client when its stdin
closes or it is interrupted. It never looks a process up by name and kills nothing but its own child.

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
- Frame pacing: the main loop's per-frame state-stack update (`0x141526cbf`, the point client-control serves RPCs at)
  is where `nullifier.js` counts frames and sleeps `--frame-sleep-ms`; with the Null device there is no present or
  vsync to wait on, so this sleep is the frame-rate cap. Game logic and the network keep running at that rate (the
  server is authoritative; the client only predicts and reports).

### Process level

`nullifier.py` sets the priority class (`below-normal` by default), EcoQoS (`SetProcessInformation(
ProcessPowerThrottling, EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION)`: low clocks / efficient cores, and the client's
`timeBeginPeriod` no longer raises the system timer rate), optionally the CPU affinity and a periodic
`EmptyWorkingSet` (moves pages to the standby list; private commit is unchanged, so it helps physical RAM, not the
commit limit). The window is shown minimized (`SW_SHOWMINNOACTIVE`) or hidden, never activated, and
`SetForegroundWindow` is neutralised, so a launched client cannot take the keyboard focus from the user's window.
A hidden window also hides an error dialog; use `--window minimized` or `normal` while debugging.

## 3. Measurements

Scenario (`nullifier-bench.py`): fresh client, bridge login, `OfflineWitch` played into Lioneye's Watch, 10 s settle,
then 60 s sampled every 5 s by `nullifier-measure.ps1`; then `/waypoints all`, `waypoint:travel` to The Coast,
`/goto 1_1_town`, `/pos`, chat, `/tp`, and 20 s more. CPU is percent of one core (16 logical cores). Machine: RTX 5070
Ti + Radeon iGPU, 31 GB RAM.

| Tier | Settings | Private commit | Working set | CPU (% of a core) | GPU util | GPU dedicated | Bridge |
|---|---|---|---|---|---|---|---|
| reference: a normal client of another session (read-only counters, Vulkan, high textures, unfocused, state unknown) | user's | 7.49 GB | 4.27 GB | 37.5 | 9 % | 3.38 GB | — |
| 0 baseline | user's `poeoffline_Config.ini` copy, `--renderer configured --frame-sleep-ms 0 --priority normal --no-eco-qos --window normal --sound` | pending | pending | pending | pending | pending | pending |
| 1 config | preset, `--renderer configured --frame-sleep-ms 0 --priority normal --no-eco-qos --window normal` | pending | pending | pending | pending | pending | pending |
| 2 process | tier 1 + below-normal, EcoQoS, minimized, `--trim-seconds 10` | pending | pending | pending | pending | pending | pending |
| 3a WARP | preset + `adapter_name=Microsoft Basic Render Driver`, tier 2 | pending | pending | pending | pending | pending | pending |
| 3b Null device | defaults (`--renderer null --frame-sleep-ms 50`, tier 2) | pending | pending | pending | pending | pending | pending |
| 3c Null + less | 3b + `--no-render-jobs --no-texture-upload` | pending | pending | pending | pending | pending | pending |

Run each row with, for example:

```powershell
.venv\Scripts\python.exe scripts\dev\nullifier-bench.py --label null --patch-port <p> --login-port <a> --travel -- --renderer null
```

Results append to `logs\nullifier\bench.jsonl` (the measure JSON plus `nullifier:stats` frames and the options).

**Not measured yet (2026-10-30):** the machine's free commit stayed between 2 and 7 GB all session (other agents'
clients, test hosts and builds), below the 12 GB required before an extra client may start, so no client was launched
and every row above is pending. Static analysis and offline tests are all that back the tiers so far; in particular
it is unverified that the client reaches the login screen, enters an area and changes area with the Null device.

## 4. What is safe to default for agent sessions

Until the table is filled: tier 1 (the preset) and tier 2 (below-normal priority, EcoQoS, minimized never-activated
window, `--nosound`) only change settings and scheduling that a normal player can also choose, so they are safe to
default once one live run confirms the bridge login. The Null device is the target default for headless agents but
must first pass the bench with `--travel` (login, play, waypoint travel, `/goto`, chat, `/tp`); WARP is a fallback
when the Null device fails, at a CPU cost.
