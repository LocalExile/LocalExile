"""One-off: log in as a character, walk into an area, and report what the gateway logs.

Usage: python scripts/dev/map_probe.py <areaId> [seed] [character]
"""
import json
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
AREA = sys.argv[1] if len(sys.argv) > 1 else "MapWorldsForbiddenWoods"
SEED = sys.argv[2] if len(sys.argv) > 2 else ""
CHARACTER = sys.argv[3] if len(sys.argv) > 3 else "wda"
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


def status():
    return call({"command": "game:status"})[:300]


def chat(text, timeout=90):
    print(f"chat {text}:", call({"command": "command", "text": text}, timeout=timeout)[:300])


print("status:", status())
print("login:", call({"command": "login:saved"}, timeout=120)[:160])
time.sleep(6)
print("status:", status())
print("list:", call({"command": "character:list"}, timeout=90)[:200])
print("play:", call({"command": "character:play", "name": CHARACTER}, timeout=180)[:200])
for _ in range(30):
    time.sleep(3)
    st = status()
    if '"area": "' in st:
        print("in area:", st)
        break
else:
    print("never reached an area:", st)
time.sleep(3)
chat("/pos")
chat("/goto " + AREA + (f" {SEED}" if SEED else ""), timeout=240)
time.sleep(15)
print("status in area:", status())
chat("/pos")
chat("/mobs", timeout=180)
proc.stdin.close()
