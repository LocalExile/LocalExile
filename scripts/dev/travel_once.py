"""One-off: travel to 1_1_3_1 through the bridge and print the full reply."""
import json
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
proc = subprocess.Popen(
    [sys.executable, str(REPO / "scripts" / "dev" / "client_control.py"), "--stdio"],
    cwd=str(REPO), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
    stderr=subprocess.STDOUT, text=True, encoding="utf-8", bufsize=1)


def call(request, timeout=180):
    proc.stdin.write(json.dumps(request) + "\n")
    proc.stdin.flush()
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        line = proc.stdout.readline()
        if not line:
            break
        if line.strip().startswith("{"):
            return line.strip()
    return "no reply"


print("status:", call({"command": "game:status"})[:200])
print("travel:", call({"command": "waypoint:travel", "area": "1_1_3_1", "wait": True}, timeout=60)[:400])
time.sleep(2)
print("pos:", call({"command": "chat", "text": "/pos"})[:300])
proc.stdin.close()
