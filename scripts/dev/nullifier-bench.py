"""The headless-client measurement scenario (Agents/docs/dev/headless-client.md): one client through nullifier.py,
log in, play a character, stand in town, measure it with nullifier-measure.ps1, then prove the bridge (slash commands,
chat, /tp, waypoint travel, /goto back) and measure again. The client and the launcher are closed at the end.

    .venv/Scripts/python.exe scripts/dev/nullifier-bench.py --label preset --patch-port 22095 --login-port 22093 \
        --travel -- --renderer configured

Everything after `--` goes to nullifier.py (the client runs in data/clients/<--session>/, default "bench"). The
servers must run on the given ports with their logs in this checkout's logs/ (client_control reads slash-command
replies from logs/live-gateway.log).

Safety: it refuses to launch when free commit is below --min-free-gb or free RAM below --min-free-ram-gb, or while
--lock exists (created atomically with this pid; a lock in any other format counts as held). A watchdog kills this
run's client, and only it (through the handle opened when it started, checked against its creation time), when free
commit falls below --watch-commit-gb or free RAM below --watch-ram-gb. Every bridge call times out. The user's
Documents/My Games/Path of Exile/poeoffline_Config.ini is checked unchanged afterwards.
The password comes from Game_BENCH_PASSWORD or the setup's data/offline-login.txt; it is never printed.
"""
import argparse
import ctypes
from ctypes import wintypes
import datetime
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time

ROOT = Path(__file__).resolve().parents[2]
MEASURE = ROOT / "scripts" / "dev" / "nullifier-measure.ps1"
USER_SETTINGS = Path(os.path.expanduser("~")) / "Documents" / "My Games" / "Path of Exile" / "poeoffline_Config.ini"
COAST = 0x7ad2  # WorldAreas 1_1_2 HASH16, a waypoint next to Lioneye's Watch

kernel = ctypes.WinDLL("kernel32", use_last_error=True)
HANDLE, DWORD, BOOL = wintypes.HANDLE, wintypes.DWORD, wintypes.BOOL
for name, args, result in [("OpenProcess", [DWORD, BOOL, DWORD], HANDLE), ("CloseHandle", [HANDLE], BOOL),
                           ("TerminateProcess", [HANDLE, wintypes.UINT], BOOL),
                           ("WaitForSingleObject", [HANDLE, DWORD], DWORD),
                           ("GetProcessTimes", [HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4, BOOL)]:
    getattr(kernel, name).argtypes, getattr(kernel, name).restype = args, result


class MemoryStatus(ctypes.Structure):
    _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong)] + \
               [(n, ctypes.c_ulonglong) for n in ("total", "avail", "totalCommit", "availCommit", "totalVirt",
                                                  "availVirt", "availExt")]


def free_gb():
    """(free commit, free physical RAM) in GB."""
    status = MemoryStatus(); status.dwLength = ctypes.sizeof(status)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status))
    return status.availCommit / 2**30, status.avail / 2**30


def alive_pid(pid):
    handle = kernel.OpenProcess(0x1000 | 0x00100000, False, pid)  # QUERY_LIMITED_INFORMATION | SYNCHRONIZE
    if not handle:
        return False
    try:
        return kernel.WaitForSingleObject(handle, 0) == 0x102
    finally:
        kernel.CloseHandle(handle)


class Client:
    """A handle to the launched client, opened once and checked against the creation time nullifier.py reported."""

    def __init__(self, pid, started):
        self.handle = kernel.OpenProcess(0x0001 | 0x1000 | 0x00100000, False, pid)
        if not self.handle:
            raise RuntimeError(f"cannot open client {pid}")
        times = [wintypes.FILETIME() for _ in range(4)]
        kernel.GetProcessTimes(self.handle, *(ctypes.byref(t) for t in times))
        if (times[0].dwHighDateTime << 32 | times[0].dwLowDateTime) != started:
            kernel.CloseHandle(self.handle); self.handle = None
            raise RuntimeError(f"pid {pid} is not the launched client")
        self.pid = pid

    def alive(self):
        return self.handle is not None and kernel.WaitForSingleObject(self.handle, 0) == 0x102

    def kill(self):
        if self.alive():
            kernel.TerminateProcess(self.handle, 1)


def take_lock(path):
    """Create the lock atomically; an existing lock is held unless it is ours-format with a dead pid."""
    path = Path(path)
    for _ in range(2):
        try:
            fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        except FileExistsError:
            words = path.read_text(encoding="utf-8", errors="replace").split()
            if len(words) >= 2 and words[0] == "nullifier-bench" and words[1].isdigit() and not alive_pid(int(words[1])):
                path.unlink()  # a dead run of this script left it
                continue
            raise SystemExit(f"lock held: {' '.join(words) or '(empty)'}")
        with os.fdopen(fd, "w", encoding="utf-8") as out:
            out.write(f"nullifier-bench {os.getpid()} {datetime.datetime.now().isoformat()}\n")
        return
    raise SystemExit("could not take the lock")


def release_lock(path):
    path = Path(path)
    try:
        words = path.read_text(encoding="utf-8", errors="replace").split()
    except OSError:
        return
    if words[:2] == ["nullifier-bench", str(os.getpid())]:
        path.unlink()


def password():
    if os.environ.get("Game_BENCH_PASSWORD"):
        return os.environ["Game_BENCH_PASSWORD"]
    for line in (ROOT / "data" / "offline-login.txt").read_text(encoding="utf-8").splitlines():
        if line.startswith("Password: "):
            return line[len("Password: "):].strip()
    raise SystemExit("no password: set Game_BENCH_PASSWORD")


def main():
    split = sys.argv.index("--") if "--" in sys.argv else len(sys.argv)
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--label", required=True)
    parser.add_argument("--patch-port", type=int, required=True)
    parser.add_argument("--login-port", type=int, required=True)
    parser.add_argument("--session", default="bench", help="the client's folder data/clients/<SESSION>/")
    parser.add_argument("--account", default="offline")
    parser.add_argument("--character", default="OfflineWitch")
    parser.add_argument("--seconds", type=int, default=60, help="town measurement length")
    parser.add_argument("--travel", action="store_true", help="also waypoint to The Coast and /goto back")
    parser.add_argument("--min-free-gb", type=float, default=3, help="free commit needed to launch")
    parser.add_argument("--min-free-ram-gb", type=float, default=8, help="free physical RAM needed to launch")
    parser.add_argument("--watch-commit-gb", type=float, default=1.5, help="kill the client below this free commit")
    parser.add_argument("--watch-ram-gb", type=float, default=4, help="kill the client below this free RAM")
    parser.add_argument("--rpc-timeout", type=float, default=120, help="seconds a bridge call may take")
    parser.add_argument("--lock", type=Path, help="lock file shared by agents that launch extra clients")
    parser.add_argument("--out", type=Path, default=ROOT / "logs" / "nullifier" / "bench.jsonl")
    options = parser.parse_args(sys.argv[1:split])
    passthrough = sys.argv[split + 1:]

    commit, ram = free_gb()
    if commit < options.min_free_gb or ram < options.min_free_ram_gb:
        raise SystemExit(f"free commit {commit:.1f} GB / RAM {ram:.1f} GB below {options.min_free_gb} / "
                         f"{options.min_free_ram_gb} GB: not launching")
    if options.lock:
        take_lock(options.lock)
    options.out.parent.mkdir(parents=True, exist_ok=True)
    settings_before = USER_SETTINGS.stat().st_mtime_ns if USER_SETTINGS.exists() else None
    log = lambda *a: print(datetime.datetime.now().strftime("%H:%M:%S"), *a, flush=True)  # noqa: E731
    record = lambda value: options.out.open("a", encoding="utf-8").write(json.dumps(value) + "\n")  # noqa: E731
    launcher = client = None
    stop = threading.Event()
    try:
        launcher = subprocess.Popen(
            [sys.executable, str(ROOT / "scripts" / "dev" / "nullifier.py"), "--session", options.session,
             "--patch-port", str(options.patch_port), "--login-port", str(options.login_port), "--stdio",
             "--frame-stats", *passthrough],
            cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, bufsize=1)
        lines = queue.Queue()
        def reader():
            for line in launcher.stdout:
                lines.put(line)
            lines.put(None)
        threading.Thread(target=reader, daemon=True).start()

        def read():
            line = lines.get(timeout=options.rpc_timeout)
            if line is None:
                raise RuntimeError("the launcher ended")
            try:
                return json.loads(line)
            except ValueError:
                raise RuntimeError(f"unexpected launcher output: {line.strip()[:300]}") from None

        started = read()
        if not started.get("ok"):
            raise RuntimeError(f"launch failed: {started}")
        client = Client(started["result"]["pid"], started["result"]["started"])
        log("launched", json.dumps(started["result"]))

        def watchdog():
            while not stop.wait(2):
                commit, ram = free_gb()
                if (commit < options.watch_commit_gb or ram < options.watch_ram_gb) and client.alive():
                    client.kill()
                    log(f"WATCHDOG: free commit {commit:.1f} GB, RAM {ram:.1f} GB: killed this run's client")
                    record({"label": options.label, "watchdog": {"commitGB": round(commit, 2), "ramGB": round(ram, 2)}})
        threading.Thread(target=watchdog, daemon=True).start()

        def rpc(request, show=True):
            launcher.stdin.write(json.dumps(request) + "\n"); launcher.stdin.flush()
            result = read()
            if show:
                log(request["command"], request.get("text", request.get("area", "")), "->", json.dumps(result)[:400])
            return result

        def measure(label, seconds):
            text = subprocess.check_output(["powershell", "-NoProfile", "-File", str(MEASURE), "-ProcessId",
                                            str(client.pid), "-Seconds", str(seconds), "-Label", label],
                                           text=True, timeout=seconds + 60).strip()
            stats = rpc({"command": "nullifier:stats"}, show=False)["result"]
            value = {**json.loads(text), "frames": stats, "options": started["result"]["options"]}
            record(value); log("measured", json.dumps(value))
            return value

        clock = time.monotonic()
        rpc({"command": "game:open"})
        log(f"menu after {time.monotonic() - clock:.1f} s")
        result = rpc({"command": "login", "account": options.account, "password": password()}, show=False)
        log("login ->", json.dumps(result)[:300])
        rpc({"command": "character:list"})
        if not rpc({"command": "character:play", "name": options.character})["ok"]:
            # The bridge gives up when no frame comes for 5 s (a long area load); keep watching for 90 s.
            for _ in range(90):
                time.sleep(1)
                if not client.alive():
                    raise SystemExit("play failed: the client exited")
                status = rpc({"command": "game:status"}, show=False)
                if status["ok"] and status["result"].get("screen") == "game" and status["result"].get("instanceState") == 3:
                    break
            else:
                raise SystemExit("play failed")
        log(f"in game after {time.monotonic() - clock:.1f} s")
        rpc({"command": "command", "text": "/pos"})
        time.sleep(10)
        rpc({"command": "nullifier:stats"}, show=False)
        measure(options.label, options.seconds)
        if options.travel:
            rpc({"command": "command", "text": "/waypoints all"})
            rpc({"command": "waypoint:travel", "area": COAST})
            time.sleep(12)
            rpc({"command": "game:status"})
            rpc({"command": "command", "text": "/pos"})
            rpc({"command": "command", "text": "/goto 1_1_town"})
            time.sleep(10)
            rpc({"command": "game:status"})
            rpc({"command": "command", "text": "/pos"})
            rpc({"command": "chat", "text": "nullifier bench"})
            rpc({"command": "tp", "dx": 5, "dy": 0})
            measure(options.label + "-after-travel", 20)
    finally:
        stop.set()
        if launcher:
            try:
                launcher.stdin.close()  # nullifier.py closes its client when its input ends
                launcher.wait(30)
            except (OSError, subprocess.TimeoutExpired):
                launcher.kill()  # its job object then ends the client
            log("launcher exited", launcher.returncode)
        if client is not None:
            client.kill()
            kernel.CloseHandle(client.handle)
        if options.lock:
            release_lock(options.lock)
        after = USER_SETTINGS.stat().st_mtime_ns if USER_SETTINGS.exists() else None
        if after != settings_before:
            log(f"WARNING: {USER_SETTINGS} changed during the run")
    return 0


if __name__ == "__main__":
    sys.exit(main())
