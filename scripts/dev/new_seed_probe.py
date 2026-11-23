"""New-instance probe 2: the cleared Coast with a new seed must generate a full fresh set of monsters,
and the saved-restore path (gateway restart, saved living monsters) must bring them back alive.
"""

from __future__ import annotations

import json
import random
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(r"D:\大肥鱼工作区\LocalExile")
COAST = "1_1_4_1"


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
        seed = random.randint(10_000_000, 2_000_000_000)
        say(f"/goto 1_1_4_1 {seed} (a brand-new instance)")
        bridge.call({"command": "chat", "text": f"/goto 1_1_4_1 {seed}"}, timeout=120)
        time.sleep(4)
        say("pos: " + text(bridge.call({"command": "chat", "text": "/pos"}, timeout=60)))
        mobs = text(bridge.call({"command": "chat", "text": "/mobs"}, timeout=60))
        say(f"/mobs in the new instance: {mobs[:280]}")
        say(f"/kill all: {text(bridge.call({'command': 'chat', 'text': '/kill all'}, timeout=60))[:120]}")
        # Leave to town, then come back inside the lifetime: the same new instance keeps its corpses.
        bridge.call({"command": "chat", "text": "/goto 1_1_town"}, timeout=120)
        time.sleep(4)
        bridge.call({"command": "chat", "text": "/waypoints all"}, timeout=60)
        time.sleep(1)
        bridge.call({"command": "waypoint:travel", "area": COAST, "wait": True}, timeout=180)
        time.sleep(2)
        mobs = text(bridge.call({"command": "chat", "text": "/mobs"}, timeout=60))
        say(f"/mobs back in the same new instance: {mobs[:200]}")
        say(f"/kill all: {text(bridge.call({'command': 'chat', 'text': '/kill all'}, timeout=60))[:120]}")
        return 0
    finally:
        bridge.close()


if __name__ == "__main__":
    raise SystemExit(main())
