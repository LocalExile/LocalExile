"""Start a cheap ("nullified") copy of the local 3.29.3b client for automated agent sessions, and drive it.

    .venv/Scripts/python.exe scripts/dev/nullifier.py --session bot1 --patch-port 12995 --login-port 20493 --stdio
    {"command":"login","account":"offline","password":"..."}
    {"command":"character:play","name":"OfflineWitch"}
    {"command":"command","text":"/goto 1_1_2"}

The client runs in its own folder (--session NAME prepares data/clients/NAME/ as live.ps1's -Session clients have it:
a hard link to Content.ggpk, junctions to the client's asset folders, and a config.json naming the session's own
settings file, seeded from config/nullifier_Config.ini; --cwd DIR uses a folder prepared elsewhere). It refuses a
folder whose config.json is missing or names the default poeoffline_/production_ settings file, so the user's
settings are never written.

It spawns Client/Game.patched.exe suspended through Frida with scripts/dev/nullifier.js (a window that never takes
focus, session-local names for the client's two-copies mutexes so it never takes one of the user's two Global
slots; optionally the engine's own Null renderer, a frame-sleep cap, frame statistics, a crash log), puts it in a
job object that kills it when this script ends, applies the process limits (priority, EcoQoS, affinity, working set
trimming) through the one process handle it keeps from the spawn, resumes it, and then serves client_control.py's
JSON-lines commands for THIS process only (--stdio), or just waits. The client lives exactly as long as this script
and its Frida session: it is terminated when either ends. It never touches another process: nothing is found by name
and the only process handle it uses is its own child's. Agents/docs/dev/headless-client.md has the measurements
behind the defaults.

--record merges {pid, started (FILETIME UTC), launcher, launcherStarted, ...} into a JSON file (atomically), for a
launcher such as live.ps1 that starts this script detached and tracks its clients by pid.
"""
import argparse
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import threading
import time

import frida

sys.path.insert(0, str(Path(__file__).resolve().parent))
import client_control  # noqa: E402  (same directory)

ROOT = client_control.ROOT
CLIENT = client_control.CLIENT
AGENT = Path(__file__).with_name("nullifier.js")
PRESET = ROOT / "config" / "nullifier_Config.ini"
SESSIONS = ROOT / "data" / "clients"
SETTINGS = Path(os.path.expanduser("~")) / "Documents" / "My Games" / "Path of Exile"
DEFAULT_SETTINGS = re.compile(r"^(poeoffline|production)_", re.IGNORECASE)

kernel = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)
HANDLE, DWORD, BOOL = wintypes.HANDLE, wintypes.DWORD, wintypes.BOOL
for name, args, result in [
        ("OpenProcess", [DWORD, BOOL, DWORD], HANDLE),
        ("CloseHandle", [HANDLE], BOOL),
        ("SetPriorityClass", [HANDLE, DWORD], BOOL),
        ("SetProcessAffinityMask", [HANDLE, ctypes.c_size_t], BOOL),
        ("SetProcessInformation", [HANDLE, ctypes.c_int, ctypes.c_void_p, DWORD], BOOL),
        ("TerminateProcess", [HANDLE, wintypes.UINT], BOOL),
        ("WaitForSingleObject", [HANDLE, DWORD], DWORD),
        ("GetProcessTimes", [HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4, BOOL),
        ("CreateJobObjectW", [ctypes.c_void_p, wintypes.LPCWSTR], HANDLE),
        ("SetInformationJobObject", [HANDLE, ctypes.c_int, ctypes.c_void_p, DWORD], BOOL),
        ("AssignProcessToJobObject", [HANDLE, HANDLE], BOOL)]:
    getattr(kernel, name).argtypes, getattr(kernel, name).restype = args, result
psapi.EmptyWorkingSet.argtypes, psapi.EmptyWorkingSet.restype = [HANDLE], BOOL
PRIORITIES = {"idle": 0x40, "below-normal": 0x4000, "normal": 0x20}
WAIT_TIMEOUT = 0x102


class PowerThrottling(ctypes.Structure):
    _fields_ = [("Version", wintypes.ULONG), ("ControlMask", wintypes.ULONG), ("StateMask", wintypes.ULONG)]


class IoCounters(ctypes.Structure):
    _fields_ = [(n, ctypes.c_ulonglong) for n in ("r", "w", "o", "rb", "wb", "ob")]


class BasicLimits(ctypes.Structure):
    _fields_ = [("PerProcessUserTimeLimit", ctypes.c_longlong), ("PerJobUserTimeLimit", ctypes.c_longlong),
                ("LimitFlags", DWORD), ("MinimumWorkingSetSize", ctypes.c_size_t),
                ("MaximumWorkingSetSize", ctypes.c_size_t), ("ActiveProcessLimit", DWORD),
                ("Affinity", ctypes.c_size_t), ("PriorityClass", DWORD), ("SchedulingClass", DWORD)]


class ExtendedLimits(ctypes.Structure):
    _fields_ = [("BasicLimitInformation", BasicLimits), ("IoInfo", IoCounters),
                ("ProcessMemoryLimit", ctypes.c_size_t), ("JobMemoryLimit", ctypes.c_size_t),
                ("PeakProcessMemoryUsed", ctypes.c_size_t), ("PeakJobMemoryUsed", ctypes.c_size_t)]


def check(ok, what):
    if not ok:
        raise OSError(ctypes.get_last_error(), what)


class Child:
    """The spawned client, through the one handle opened right after the spawn: limits, liveness and termination
    never look the pid up again, so a pid Windows reuses after the client exits cannot be mistaken for it. The job
    object kills the client when this process (and so the job handle) goes away, however it ends."""

    def __init__(self, pid):
        self.pid = pid
        # PROCESS_TERMINATE | SET_QUOTA | SET_INFORMATION | QUERY_INFORMATION | SYNCHRONIZE
        self.handle = kernel.OpenProcess(0x0001 | 0x0100 | 0x0200 | 0x0400 | 0x00100000, False, pid)
        check(self.handle, f"OpenProcess({pid})")
        self.job = kernel.CreateJobObjectW(None, None)
        check(self.job, "CreateJobObjectW")
        limits = ExtendedLimits()
        limits.BasicLimitInformation.LimitFlags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        check(kernel.SetInformationJobObject(self.job, 9, ctypes.byref(limits), ctypes.sizeof(limits)),
              "SetInformationJobObject")
        check(kernel.AssignProcessToJobObject(self.job, self.handle), "AssignProcessToJobObject")
        times = [wintypes.FILETIME() for _ in range(4)]
        check(kernel.GetProcessTimes(self.handle, *(ctypes.byref(t) for t in times)), "GetProcessTimes")
        self.started = times[0].dwHighDateTime << 32 | times[0].dwLowDateTime

    def alive(self):
        return self.handle is not None and kernel.WaitForSingleObject(self.handle, 0) == WAIT_TIMEOUT

    def limit(self, priority, eco_qos, affinity):
        """Scheduling priority, EcoQoS (execution-speed throttling: low clocks / efficient cores; plus ignoring the
        client's timer-resolution requests where Windows supports it) and CPU affinity."""
        if priority:
            check(kernel.SetPriorityClass(self.handle, PRIORITIES[priority]), "SetPriorityClass")
        if eco_qos:
            for mask in (0x1 | 0x4, 0x1):  # EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION; the latter needs Windows 11
                state = PowerThrottling(1, mask, mask)
                if kernel.SetProcessInformation(self.handle, 4, ctypes.byref(state), ctypes.sizeof(state)):
                    break
            else:
                raise OSError(ctypes.get_last_error(), "SetProcessInformation(ProcessPowerThrottling)")
        if affinity:
            check(kernel.SetProcessAffinityMask(self.handle, affinity), "SetProcessAffinityMask")

    def trim(self):
        if self.alive():
            psapi.EmptyWorkingSet(self.handle)

    def terminate(self):
        if self.alive():
            kernel.TerminateProcess(self.handle, 1)

    def close(self):
        self.terminate()
        for handle in (self.handle, self.job):
            if handle:
                kernel.CloseHandle(handle)
        self.handle = self.job = None


def settings_name(folder):
    """The settings file the client in `folder` uses, from its config.json; refuses the default ones."""
    config = Path(folder) / "config.json"
    if not config.is_file():
        raise SystemExit(f"{config} is missing: the client would use the user's poeoffline_Config.ini")
    name = json.loads(config.read_text(encoding="utf-8-sig")).get("GGG_CONFIG_FILE_NAME")
    if not isinstance(name, str) or not name or DEFAULT_SETTINGS.match(Path(name).name):
        raise SystemExit(f"{config} must name the client's own settings file in GGG_CONFIG_FILE_NAME, not {name!r}")
    return name


def prepare_session(session, preset):
    """data/clients/<session>/ as live.ps1 Start-Client -Session builds it: the client opens Content.ggpk, config.json,
    its asset folders and logs/ in its working folder."""
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,32}", session):
        raise SystemExit("session names use letters, digits, - and _ (at most 32)")
    folder = SESSIONS / session
    folder.mkdir(parents=True, exist_ok=True)
    shared, link = CLIENT.parent / "Content.ggpk", folder / "Content.ggpk"
    if link.exists():
        a, b = link.stat(), shared.stat()
        if a.st_size != b.st_size or a.st_mtime != b.st_mtime:  # the GGPK was replaced since: only this link goes
            link.unlink()
    if not link.exists():
        os.link(shared, link)
    for assets in CLIENT.parent.iterdir():
        target = folder / assets.name
        if assets.is_dir() and assets.name not in ("logs", "CachedHLSLShaders") and not os.path.lexists(target):
            subprocess.run(["cmd", "/c", "mklink", "/J", str(target), str(assets)], check=True, capture_output=True)
    prefix = f"localexile-{ROOT.name}-{session}"
    (folder / "config.json").write_text(json.dumps(
        {"GGG_CONFIG_FILE_NAME": f"{prefix}_Config.ini", "GGG_CREDENTIAL_FILE_NAME": f"{prefix}_login.credentials"},
        indent=2), encoding="ascii")
    ini = SETTINGS / f"{prefix}_Config.ini"
    if not ini.exists():
        SETTINGS.mkdir(parents=True, exist_ok=True)
        # No [LOGIN] section: no saved login to send to whichever login server the shared gateway list names.
        lines, section = [], None
        for line in Path(preset).read_text(encoding="utf-8-sig").splitlines():
            if m := re.fullmatch(r"\[(.+)\]\s*", line):
                section = m.group(1)
            if section != "LOGIN":
                lines.append(line)
        ini.write_text("\n".join(lines) + "\n", encoding="utf-8-sig")
    return folder


def write_record(path, values):
    """Merge `values` into the JSON at `path` (a pending live.ps1 record keeps its other fields), atomically."""
    path = Path(path)
    record = {}
    if path.exists():
        try:
            record = json.loads(path.read_text(encoding="utf-8-sig"))
        except ValueError:
            record = {}
    record.update(values)
    path.parent.mkdir(parents=True, exist_ok=True)
    handle, temporary = tempfile.mkstemp(dir=path.parent, prefix=path.name, suffix=".tmp")
    with os.fdopen(handle, "w", encoding="ascii") as out:
        json.dump(record, out)
    os.replace(temporary, path)


def run_mutex_prefix(folder):
    r"""The session-local namespace for the client's two-copies mutexes (nullifier.js renames Global\PoERunMutexA/B to
    <prefix>PoERunMutexA/B), so clients we launch never take one of the two Global slots the user's and other
    sessions' clients need. Unique per checkout and client folder."""
    tag = re.sub(r"[^A-Za-z0-9_-]", "_", f"{ROOT.name}-{Path(folder).name}")[:80]
    return "Local\\LocalExile-" + tag + "-"


class PidClientControl(client_control.ClientControl):
    """client_control's commands, bound to the spawned client instead of "the only Game.patched.exe running"."""

    def __init__(self, child, nullifier=None):
        super().__init__()
        self.child = child
        self.nullifier = nullifier  # the nullifier.js script, for nullifier:stats

    def _find_client(self):
        return self.child.pid if self.child.alive() else None

    def open(self):
        return {"alreadyRunning": True, **self.ready()}

    def execute(self, request):
        request = client_control.normalize(request)
        if request["command"] == "nullifier:stats":
            # Main-loop frames counted by nullifier.js (--frame-stats), and the frame rate since the previous call.
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
    where = parser.add_mutually_exclusive_group(required=True)
    where.add_argument("--session", help="prepare and use data/clients/<SESSION>/ as the client's folder")
    where.add_argument("--cwd", type=Path, help="an already prepared client folder (its config.json names the "
                                               "settings file)")
    parser.add_argument("--preset", type=Path, default=PRESET, help="seed for a new session settings file")
    parser.add_argument("--renderer", choices=["null", "configured"], default="null",
                        help="null: the engine's Null device, no GPU (default); configured: the settings' device_type "
                             "(the preset: DirectX 11), the fallback if an area breaks under the Null device")
    parser.add_argument("--frame-sleep-ms", type=int, default=0,
                        help="sleep per main-loop frame (0: none; the preset's framerate_limit caps a real renderer)")
    parser.add_argument("--frame-stats", action="store_true", help="count main-loop frames for nullifier:stats")
    parser.add_argument("--no-render-jobs", action="store_true", help="also disable the frame render jobs")
    parser.add_argument("--no-texture-upload", action="store_true", help="texture manager Upload off")
    parser.add_argument("--priority", choices=sorted(PRIORITIES), default="below-normal")
    parser.add_argument("--no-eco-qos", action="store_true", help="do not mark the process EcoQoS")
    parser.add_argument("--affinity", type=lambda v: int(v, 0), default=0, help="CPU affinity mask (0: all)")
    parser.add_argument("--trim-seconds", type=float, default=0, help="trim the working set this often (0: never)")
    parser.add_argument("--window", choices=["minimized", "hidden", "normal"], default="normal",
                        help="how the client window is shown; it is never activated or brought to the foreground. "
                             "A minimized client with a real renderer stops its main loop (no login)")
    parser.add_argument("--sound", action="store_true", help="keep sound (default: --nosound)")
    parser.add_argument("--trace-files", help="log CreateFileW paths matching this regex (diagnostics)")
    parser.add_argument("--crash-log", type=Path, help="append a client crash's registers and backtrace here")
    parser.add_argument("--probe", type=Path, action="append", default=[],
                        help="append this Frida script to the agent (diagnostics; it sees OPTIONS, at(), checked(), log())")
    parser.add_argument("--stdio", action="store_true", help="serve client_control JSON-lines commands on stdin")
    parser.add_argument("--record", type=Path, help="merge {pid, started, launcher, launcherStarted, ...} into this JSON")
    options = parser.parse_args()

    folder = prepare_session(options.session, options.preset) if options.session else Path(os.path.abspath(options.cwd))
    if os.path.normcase(os.path.abspath(folder)) == os.path.normcase(os.path.abspath(CLIENT.parent)):
        raise SystemExit("the shared Client folder has no config.json of its own: use --session or a session --cwd")
    settings = settings_name(folder)
    agent_options = {
        "nullRenderer": options.renderer == "null", "noRenderJobs": options.no_render_jobs,
        "noTextureUpload": options.no_texture_upload, "frameSleepMs": max(0, options.frame_sleep_ms),
        "frameStats": options.frame_stats, "window": options.window, "traceFiles": options.trace_files,
        "runMutexPrefix": run_mutex_prefix(folder),
        "crashLog": str(Path(os.path.abspath(options.crash_log))) if options.crash_log else None}
    argv = [str(CLIENT), "--patching-server", f"127.0.0.1:{options.patch_port}", "--nologo"]
    if not options.sound:
        argv.append("--nosound")
    # As Start-Offline.ps1: optional web requests (news, shop) fail locally instead of reaching the internet.
    env = {"HTTP_PROXY": "http://127.0.0.1:1", "HTTPS_PROXY": "http://127.0.0.1:1", "ALL_PROXY": "http://127.0.0.1:1",
           "NO_PROXY": "localhost,127.0.0.1,::1"}
    os.environ["Game_LOGIN_PORT"] = str(options.login_port)  # client_control's login connects here

    device = frida.get_local_device()
    # stdio "pipe": the client also logs to its standard output, which must not reach this script's JSON lines; Frida
    # reads it (the "output" events, dropped here) so it never blocks.
    pid = device.spawn(argv, cwd=str(folder), env=env, stdio="pipe")
    child = session = None
    try:
        child = Child(pid)  # in its job before it runs a single instruction
        session = device.attach(pid)
        # The hooks exist only while the session does: a client that loses it is not left running unhooked.
        session.on("detached", lambda *unused: child.terminate())
        script = session.create_script(f"const OPTIONS = {json.dumps(agent_options)};\n"
                                       + AGENT.read_text(encoding="utf-8")
                                       + "".join("\n" + p.read_text(encoding="utf-8") for p in options.probe),
                                       name="localexile-nullifier")
        script.on("message", lambda message, data: print(json.dumps(
            {"nullifier": message.get("payload", {}).get("message") if message.get("type") == "send"
             else message.get("description", message)}), file=sys.stderr, flush=True))
        script.load()
        child.limit(options.priority, not options.no_eco_qos, options.affinity)
        device.resume(pid)
        launched = {"pid": pid, "started": child.started, "launcher": os.getpid(),
                    "launcherStarted": launcher_started(), "cwd": str(folder),
                    "settings": settings, "options": agent_options}
        if options.record:
            write_record(options.record, launched)
        print(json.dumps({"ok": True, "result": launched}), flush=True)
        if options.trim_seconds > 0:
            def trimmer():
                while child.alive():
                    time.sleep(options.trim_seconds)
                    child.trim()
            threading.Thread(target=trimmer, daemon=True).start()
        if options.stdio:
            with PidClientControl(child, script) as control:
                for line in sys.stdin:
                    try:
                        result = client_control.run_request(control, json.loads(line))
                    except (ValueError, TypeError) as error:
                        result = {"ok": False, "error": str(error)}
                    print(json.dumps(result, ensure_ascii=True), flush=True)
        else:
            while child.alive():
                time.sleep(1)
    except KeyboardInterrupt:
        pass
    finally:
        if child is not None:
            child.close()
        else:  # failed before the handle: the spawned process is still suspended and certainly ours
            try:
                device.kill(pid)
            except frida.InvalidOperationError:
                pass
        if session is not None:
            try:
                session.detach()
            except frida.InvalidOperationError:
                pass
    return 0


def launcher_started():
    """This launcher's own creation time (FILETIME UTC), so a record names the launcher unambiguously too."""
    handle = kernel.OpenProcess(0x1000, False, os.getpid())
    try:
        times = [wintypes.FILETIME() for _ in range(4)]
        check(kernel.GetProcessTimes(handle, *(ctypes.byref(t) for t in times)), "GetProcessTimes")
        return times[0].dwHighDateTime << 32 | times[0].dwLowDateTime
    finally:
        kernel.CloseHandle(handle)


if __name__ == "__main__":
    sys.exit(main())
