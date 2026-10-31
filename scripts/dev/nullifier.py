"""Start a cheap ("nullified") copy of the local 3.29.3b client for automated agent sessions, and drive it.

    .venv/Scripts/python.exe scripts/dev/nullifier.py --patch-port 12995 --login-port 20493 --stdio
    {"command":"login","account":"offline","password":"..."}
    {"command":"character:play","name":"OfflineWitch"}
    {"command":"command","text":"/goto 1_1_2"}

It spawns Client/Game.patched.exe suspended through Frida with scripts/dev/nullifier.js (a per-client settings file from
the low-cost preset, optionally the engine's own Null renderer and a frame-sleep cap), applies the process-level
limits (priority, EcoQoS, affinity, working set trimming, a window that never takes focus), resumes it, and then
serves client_control.py's JSON-lines commands for THIS process only (--stdio), or just waits. The client lives
exactly as long as this script: the hooks are only in place while it is attached, so it closes the client when it
ends (stdin closed, Ctrl+C) rather than leave it running unhooked. The pid is checked together with its creation
time everywhere, so a pid Windows reuses after the client exits is never attached to, trimmed or terminated. Agents/docs/dev/headless-client.md has the measurements behind the defaults.

It never touches another client or server: no process is found by name, nothing is killed but its own child, and it
writes no file except the settings file and the --record file. The servers (Auth, Patch, Gateway) must already run on
the given ports.

Settings: with --cwd (a client session folder whose config.json names the session's settings file, as live.ps1's
-Session clients have) the client's own config.json decides and nothing is redirected; otherwise the settings file is
redirected to --config (default a fresh copy of config/nullifier_Config.ini under logs/nullifier/), so the user's
poeoffline_Config.ini is never used. --record writes the client's pid and creation time for a launcher such as
live.ps1 that starts this script detached and tracks its clients by pid.
"""
import argparse
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import shutil
import sys
import threading
import time

import frida

sys.path.insert(0, str(Path(__file__).resolve().parent))
import client_control  # noqa: E402  (same directory)

ROOT = client_control.ROOT
CLIENT = client_control.CLIENT
AGENT = Path(__file__).with_name("nullifier.js")
PRESET = ROOT / "config" / "nullifier_Config.ini"

kernel = ctypes.WinDLL("kernel32", use_last_error=True)
user = ctypes.WinDLL("user32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)
kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel.OpenProcess.restype = wintypes.HANDLE
kernel.SetPriorityClass.argtypes = [wintypes.HANDLE, wintypes.DWORD]
kernel.SetProcessAffinityMask.argtypes = [wintypes.HANDLE, ctypes.c_size_t]
kernel.SetProcessInformation.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
kernel.TerminateProcess.argtypes = [wintypes.HANDLE, wintypes.UINT]
kernel.CloseHandle.argtypes = [wintypes.HANDLE]
kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
psapi.EmptyWorkingSet.argtypes = [wintypes.HANDLE]
PRIORITIES = {"idle": 0x40, "below-normal": 0x4000, "normal": 0x20}


class PowerThrottling(ctypes.Structure):
    _fields_ = [("Version", wintypes.ULONG), ("ControlMask", wintypes.ULONG), ("StateMask", wintypes.ULONG)]


def process_handle(pid):
    # PROCESS_SET_INFORMATION | PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA | PROCESS_TERMINATE
    handle = kernel.OpenProcess(0x0200 | 0x0400 | 0x0100 | 0x0001, False, pid)
    if not handle:
        raise OSError(ctypes.get_last_error(), f"cannot open process {pid}")
    return handle


def apply_process_limits(pid, priority, eco_qos, affinity):
    """Process tier: scheduling priority, EcoQoS (execution-speed throttling, which also lets Windows put the
    process on efficient cores at low clocks, and ignore its timer-resolution requests) and CPU affinity."""
    handle = process_handle(pid)
    try:
        if priority and not kernel.SetPriorityClass(handle, PRIORITIES[priority]):
            raise OSError(ctypes.get_last_error(), "SetPriorityClass")
        if eco_qos:
            # PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION
            state = PowerThrottling(1, 0x1 | 0x4, 0x1 | 0x4)
            if not kernel.SetProcessInformation(handle, 4, ctypes.byref(state), ctypes.sizeof(state)):
                raise OSError(ctypes.get_last_error(), "SetProcessInformation(ProcessPowerThrottling)")
        if affinity and not kernel.SetProcessAffinityMask(handle, affinity):
            raise OSError(ctypes.get_last_error(), "SetProcessAffinityMask")
    finally:
        kernel.CloseHandle(handle)


def trim_working_set(pid):
    handle = process_handle(pid)
    try:
        psapi.EmptyWorkingSet(handle)
    finally:
        kernel.CloseHandle(handle)


def creation_time(pid):
    """The process creation time as a UTC FILETIME (what PowerShell's StartTime.ToFileTimeUtc() gives)."""
    handle = kernel.OpenProcess(0x1000, False, pid)
    if not handle:
        return None
    try:
        times = [wintypes.FILETIME() for _ in range(4)]
        if not kernel.GetProcessTimes(handle, *(ctypes.byref(t) for t in times)):
            return None
        return times[0].dwHighDateTime << 32 | times[0].dwLowDateTime
    finally:
        kernel.CloseHandle(handle)


def ours(pid, started):
    """The spawned client is still running: same pid AND creation time (a pid is reused once its process exits)."""
    return started is not None and client_control.live_process(pid) and creation_time(pid) == started


def terminate(pid):
    handle = kernel.OpenProcess(0x0001, False, pid)
    if handle:
        kernel.TerminateProcess(handle, 0)
        kernel.CloseHandle(handle)


class PidClientControl(client_control.ClientControl):
    """client_control's commands, bound to one pid instead of the only Game.patched.exe running."""

    def __init__(self, pid, nullifier=None, started=None):
        super().__init__()
        self.client_pid = pid
        # Its creation time: once the client has exited, Windows can give its pid to an unrelated process, which
        # must never be attached to.
        self.client_started = creation_time(pid) if started is None else started
        self.nullifier = nullifier  # the nullifier.js script, for nullifier:stats

    def _find_client(self):
        alive = client_control.live_process(self.client_pid) and self.client_started is not None
        return self.client_pid if alive and creation_time(self.client_pid) == self.client_started else None

    def open(self):
        return {"alreadyRunning": True, **self.ready()}

    def execute(self, request):
        request = client_control.normalize(request)
        if request["command"] == "nullifier:stats":
            # Main-loop frames counted by nullifier.js, and the frame rate since the previous stats request.
            if self.nullifier is None:
                raise RuntimeError("No nullifier agent loaded")
            return self.nullifier.exports_sync.stats()
        if request["command"] == "waypoint:travel":
            # The World panel pin's C→S 0x4d (Agents/docs/protocol/16-waypoints.md §3): u16 destination HASH16, u32
            # (the panel's waypoint id; the Gateway logs it only), u8 Ctrl. The Gateway checks the waypoint is
            # activated (/waypoints all activates every one) and changes area; wait for the new area with game:status.
            area = int(request["area"], 0) if isinstance(request.get("area"), str) else int(request["area"])
            if not 0 < area <= 0xffff:
                raise ValueError("area must be a WorldAreas HASH16")
            packet = [0x00, 0x4d, area >> 8, area & 0xff, 0, 0, 0x2f, 0x00, 0]
            return {**self.rpc("chat", bytes=packet), "area": area}
        return super().execute(request)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--patch-port", type=int, required=True, help="local patching server (TCP) port")
    parser.add_argument("--login-port", type=int, required=True, help="local Auth login port (bridge login)")
    parser.add_argument("--cwd", type=Path, default=CLIENT.parent,
                        help="the client's working folder (Content.ggpk, config.json and logs/ are opened there)")
    parser.add_argument("--config", help="redirect the settings file here (created from the preset if missing); "
                                         "'none': no redirect (default with --cwd)")
    parser.add_argument("--preset", type=Path, default=PRESET, help="settings copied into --config when it is missing")
    parser.add_argument("--renderer", choices=["null", "configured"], default="configured",
                        help="configured: the settings' device_type (the preset: DirectX 11); null: the engine's Null "
                             "device, experimental: reaches character selection but crashes on entering an area")
    parser.add_argument("--frame-sleep-ms", type=int, default=0,
                        help="sleep per main-loop frame (0: none; the preset's framerate_limit caps a real renderer)")
    parser.add_argument("--no-render-jobs", action="store_true", help="also disable the frame render jobs")
    parser.add_argument("--no-texture-upload", action="store_true", help="texture manager Upload off")
    parser.add_argument("--priority", choices=sorted(PRIORITIES), default="below-normal")
    parser.add_argument("--no-eco-qos", action="store_true", help="do not mark the process EcoQoS")
    parser.add_argument("--affinity", type=lambda v: int(v, 0), default=0, help="CPU affinity mask (0: all)")
    parser.add_argument("--trim-seconds", type=float, default=0, help="trim the working set this often (0: never)")
    parser.add_argument("--window", choices=["minimized", "hidden", "normal"], default="normal",
                        help="how the client window is shown; it is never activated or brought to the foreground. "
                             "A minimized client with a real renderer stops its main loop (no login); only the Null "
                             "renderer runs minimized")
    parser.add_argument("--sound", action="store_true", help="keep sound (default: --nosound)")
    parser.add_argument("--trace-files", help="log CreateFileW paths matching this regex (diagnostics)")
    parser.add_argument("--crash-log", type=Path, default=ROOT / "logs" / "nullifier" / "crashes.log",
                        help="append a client crash's registers and backtrace here")
    parser.add_argument("--stdio", action="store_true", help="serve client_control JSON-lines commands on stdin")
    parser.add_argument("--record", type=Path, help="write {pid, started (FILETIME UTC), launcher} here once running")
    options = parser.parse_args()

    # abspath, not resolve(): a worktree's Client\ is a junction, and the client should see this checkout's path.
    cwd = Path(os.path.abspath(options.cwd))
    config = options.config
    if config is None and os.path.normcase(cwd) == os.path.normcase(os.path.abspath(CLIENT.parent)):
        config = str(ROOT / "logs" / "nullifier" / f"client-{os.getpid()}_Config.ini")
    if config in (None, "none"):
        config = None
    else:
        config = Path(os.path.abspath(config))
        if not config.exists():
            config.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(options.preset, config)
    agent_options = {
        "nullRenderer": options.renderer == "null", "noRenderJobs": options.no_render_jobs,
        "noTextureUpload": options.no_texture_upload, "frameSleepMs": max(0, options.frame_sleep_ms),
        "configPath": str(config) if config else None, "window": options.window, "traceFiles": options.trace_files,
        "crashLog": str(Path(os.path.abspath(options.crash_log)))}
    argv = [str(CLIENT), "--patching-server", f"127.0.0.1:{options.patch_port}", "--nologo"]
    if not options.sound:
        argv.append("--nosound")
    # As Start-Offline.ps1: optional web requests (news, shop) fail locally instead of reaching the internet.
    env = {"HTTP_PROXY": "http://127.0.0.1:1", "HTTPS_PROXY": "http://127.0.0.1:1", "ALL_PROXY": "http://127.0.0.1:1",
           "NO_PROXY": "localhost,127.0.0.1,::1"}
    os.environ["Game_LOGIN_PORT"] = str(options.login_port)  # client_control's login connects here

    device = frida.get_local_device()
    pid = device.spawn(argv, cwd=str(cwd), env=env)
    session = launched = None
    try:
        session = device.attach(pid)
        script = session.create_script(f"const OPTIONS = {json.dumps(agent_options)};\n"
                                       + AGENT.read_text(encoding="utf-8"), name="localexile-nullifier")
        script.on("message", lambda message, data: print(json.dumps(
            {"nullifier": message.get("payload", {}).get("message") if message.get("type") == "send"
             else message.get("description", message)}), file=sys.stderr, flush=True))
        script.load()
        apply_process_limits(pid, options.priority, not options.no_eco_qos, options.affinity)
        device.resume(pid)
        launched = {"pid": pid, "started": creation_time(pid), "launcher": os.getpid(), "cwd": str(cwd),
                    "config": str(config) if config else None, "options": agent_options}
        if options.record:
            options.record.parent.mkdir(parents=True, exist_ok=True)
            options.record.write_text(json.dumps(launched), encoding="ascii")
        print(json.dumps({"ok": True, "result": launched}), flush=True)
        if options.trim_seconds > 0:
            def trimmer():
                while True:
                    time.sleep(options.trim_seconds)
                    if not ours(pid, launched["started"]):
                        return
                    try:
                        trim_working_set(pid)
                    except OSError:
                        return
            threading.Thread(target=trimmer, daemon=True).start()
        if options.stdio:
            with PidClientControl(pid, script, launched["started"]) as control:
                for line in sys.stdin:
                    try:
                        result = client_control.run_request(control, json.loads(line))
                    except (ValueError, TypeError) as error:
                        result = {"ok": False, "error": str(error)}
                    print(json.dumps(result, ensure_ascii=True), flush=True)
        else:
            while ours(pid, launched["started"]):
                time.sleep(1)
    except KeyboardInterrupt:
        pass
    finally:
        # Unhooked, the client would save its settings over the user's poeoffline_Config.ini: close it with us.
        if ours(pid, creation_time(pid) if launched is None else launched["started"]):
            terminate(pid)
        if session is not None:
            try:
                session.detach()
            except frida.InvalidOperationError:
                pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
