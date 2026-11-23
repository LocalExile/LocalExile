"""One-off: log in, play a character and drive it, keeping one bridge session open.

Usage: python scripts/dev/enter.py "<chat command>" ["<chat command>" ...]
"""
import json
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
CHARACTER = "wda"
proc = subprocess.Popen(
    [sys.executable, str(REPO / "scripts" / "dev" / "client_control.py"), "--stdio"],
    cwd=str(REPO), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
    stderr=subprocess.STDOUT, text=True, encoding="utf-8", bufsize=1)


def call(request, timeout=200):
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
    return call({"command": "game:status"})


def in_game():
    return '"area": "' in status()


if not in_game():
    print("login:", call({"command": "login:saved"}, timeout=120)[:120])
    for _ in range(20):
        time.sleep(3)
        st = call({"command": "character:list"}, timeout=60)
        if '"characters"' in st:
            break
    for attempt in range(6):
        got = call({"command": "character:play", "name": CHARACTER}, timeout=120)
        print("play:", got[:160])
        if '"ok": true' in got:
            break
        call({"command": "character:select"}, timeout=60)
        time.sleep(4)
for _ in range(40):
    if in_game():
        break
    time.sleep(3)
print("status:", status()[:300])
for text in sys.argv[1:]:
    print(f"{text} ->", call({"command": "command", "text": text})[:700])
    time.sleep(6)
time.sleep(3)
print("status:", status()[:300])
proc.stdin.close()
