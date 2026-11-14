"""Pack-point probe: enter an area whose layout names its pack points (The Aqueduct, 1_4_1) and read the gateway's
population note, which should list a pack per room point and fill only the rest of the budget by scattering.

    .venv/Scripts/python.exe scripts/dev/packpoint_probe.py

Needs the client started by live.ps1 and logged in; it sends only chat through the bridge.
"""

from __future__ import annotations

import json
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
AQUEDUCT = "1_4_1"


def say(text: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {text}", flush=True)


class Bridge:
    def __init__(self) -> None:
        self.proc = subprocess.Popen(
            [sys.executable, str(REPO / "scripts" / "dev" / "client_control.py"), "--stdio"],
            cwd=str(REPO), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1)

    def call(self, request: dict, timeout: float = 240.0) -> dict:
        self.proc.stdin.write(json.dumps(request) + "\n")
        self.proc.stdin.flush()
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            line = self.proc.stdout.readline()
            if not line:
                break
            if line.strip().startswith("{"):
                return json.loads(line)
        return {"ok": False, "error": "no reply"}

    def close(self) -> None:
        try:
            self.proc.stdin.close()
        except OSError:
            pass


def text(reply: dict) -> str:
    return " ".join(((reply.get("result") or {}).get("replies") or []))


def main() -> int:
    bridge = Bridge()
    try:
        st = bridge.call({"command": "game:status"}).get("result") or {}
        say(f"status: running={st.get('running')} area={st.get('area')}")
        if not st.get("running"):
            return 1
        say("unlock waypoints, then /goto the Aqueduct")
        bridge.call({"command": "chat", "text": "/waypoints all"}, timeout=60)
        time.sleep(1)
        say("/goto: " + text(bridge.call({"command": "chat", "text": f"/goto {AQUEDUCT}"}, timeout=180))[:160])
        time.sleep(4)
        say("pos: " + text(bridge.call({"command": "chat", "text": "/pos"}, timeout=60)))
        mobs = text(bridge.call({"command": "chat", "text": "/mobs"}, timeout=60))
        say(f"/mobs: {mobs[:240]}")
        say(f"monster count: {mobs.count('0x')}")
        return 0
    finally:
        bridge.close()


if __name__ == "__main__":
    raise SystemExit(main())
