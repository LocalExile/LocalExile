"""The headless-client measurement scenario (Agents/docs/dev/headless-client.md): one client through nullifier.py,
log in, play a character, stand in town, measure it with nullifier-measure.ps1, then prove the bridge (slash commands,
chat, /tp, waypoint travel, /goto back) and measure again. The client and the launcher are closed at the end.

    .venv/Scripts/python.exe scripts/dev/nullifier-bench.py --label null --patch-port 22095 --login-port 22093 \
        --travel -- --renderer configured

Everything after `--` goes to nullifier.py. The servers must run on the given ports with their logs in this checkout's
logs/ (client_control reads slash-command replies from logs/live-gateway.log). It refuses to launch when free commit is
below --min-free-gb, or while --lock names a live process (the lock is created with this pid and removed at the end).
The password comes from Game_BENCH_PASSWORD or the setup's data/offline-login.txt; it is never printed.
"""
import argparse
import ctypes
import datetime
import json
import os
from pathlib import Path
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
MEASURE = ROOT / "scripts" / "dev" / "nullifier-measure.ps1"
COAST = 0x7ad2  # WorldAreas 1_1_2 HASH16, a waypoint next to Lioneye's Watch


class MemoryStatus(ctypes.Structure):
    _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong)] + \
               [(n, ctypes.c_ulonglong) for n in ("total", "avail", "totalCommit", "availCommit", "totalVirt",
                                                  "availVirt", "availExt")]


def free_commit_gb():
    status = MemoryStatus(); status.dwLength = ctypes.sizeof(status)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status))
    return status.availCommit / 2**30


def alive(pid):
    kernel = ctypes.windll.kernel32
    handle = kernel.OpenProcess(0x1000, False, pid)
    if not handle:
        return False
    code = ctypes.c_ulong()
    kernel.GetExitCodeProcess(handle, ctypes.byref(code)); kernel.CloseHandle(handle)
    return code.value == 259


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
    parser.add_argument("--account", default="offline")
    parser.add_argument("--character", default="OfflineWitch")
    parser.add_argument("--seconds", type=int, default=60, help="town measurement length")
    parser.add_argument("--travel", action="store_true", help="also waypoint to The Coast and /goto back")
    parser.add_argument("--min-free-gb", type=float, default=12)
    parser.add_argument("--lock", type=Path, help="lock file shared by agents that launch extra clients")
    parser.add_argument("--out", type=Path, default=ROOT / "logs" / "nullifier" / "bench.jsonl")
    options = parser.parse_args(sys.argv[1:split])
    passthrough = sys.argv[split + 1:]

    if options.lock and options.lock.exists():
        words = options.lock.read_text(encoding="utf-8", errors="replace").split()
        if len(words) > 1 and words[1].isdigit() and alive(int(words[1])):
            raise SystemExit(f"lock held: {' '.join(words)}")
    free = free_commit_gb()
    if free < options.min_free_gb:
        raise SystemExit(f"free commit {free:.1f} GB < {options.min_free_gb} GB: not launching")
    options.out.parent.mkdir(parents=True, exist_ok=True)
    if options.lock:
        options.lock.write_text(f"nullifier-bench {os.getpid()} {datetime.datetime.now().isoformat()}\n", encoding="utf-8")
    log = lambda *a: print(datetime.datetime.now().strftime("%H:%M:%S"), *a, flush=True)  # noqa: E731
    record = lambda value: options.out.open("a", encoding="utf-8").write(json.dumps(value) + "\n")  # noqa: E731
    launcher = pid = None
    try:
        launcher = subprocess.Popen(
            [sys.executable, str(ROOT / "scripts" / "dev" / "nullifier.py"), "--patch-port", str(options.patch_port),
             "--login-port", str(options.login_port), "--stdio", *passthrough],
            cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, bufsize=1)
        started = json.loads(launcher.stdout.readline())
        pid = started["result"]["pid"]
        log("launched", json.dumps(started["result"]))

        def rpc(request, show=True):
            launcher.stdin.write(json.dumps(request) + "\n"); launcher.stdin.flush()
            result = json.loads(launcher.stdout.readline())
            if show:
                log(request["command"], request.get("text", request.get("area", "")), "->", json.dumps(result)[:400])
            return result

        def measure(label, seconds):
            text = subprocess.check_output(["powershell", "-NoProfile", "-File", str(MEASURE), "-ProcessId", str(pid),
                                            "-Seconds", str(seconds), "-Label", label], text=True).strip()
            stats = rpc({"command": "nullifier:stats"})["result"]
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
                frames = rpc({"command": "nullifier:stats"}, show=False).get("result")
                status = rpc({"command": "game:status"}, show=False)
                log("waiting:", json.dumps(frames), json.dumps(status)[:200])
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
        if launcher:
            launcher.stdin.close()  # nullifier.py closes its client when its input ends
            try:
                launcher.wait(30)
            except subprocess.TimeoutExpired:
                launcher.kill()
            log("launcher exited", launcher.returncode)
        if pid and alive(pid):  # a launcher that did not get to close it
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
        if options.lock and options.lock.exists() and str(os.getpid()) in options.lock.read_text(encoding="utf-8"):
            options.lock.unlink()
    return 0


if __name__ == "__main__":
    sys.exit(main())
