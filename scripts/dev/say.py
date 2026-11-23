"""One-off: send chat commands to the running client through the bridge.

Usage: python scripts/dev/say.py "/goto MapWorldsForbiddenWoods" "/pos" "/mobs"
"""
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


print("status:", call({"command": "game:status"})[:260])
for text in sys.argv[1:]:
    print(f"{text} ->", call({"command": "command", "text": text})[:600])
    time.sleep(4)
proc.stdin.close()
