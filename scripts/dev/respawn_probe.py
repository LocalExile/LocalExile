"""Respawn probe: kill everything, leave the area, come back, and see what the area holds.
The bridge stays open for the whole run (closing it ends the client).
"""

from __future__ import annotations

import json
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
COAST = "1_1_4_1"


def say(text: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {text}", flush=True)


class Bridge:
    def __init__(self) -> None:
        self.proc = subprocess.Popen(
            [sys.executable, str(REPO / "scripts" / "dev" / "client_control.py"), "--stdio"],
            cwd=str(REPO), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1)

    def call(self, request: dict, timeout: float = 180.0) -> dict:
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


def living(reply: dict) -> list[str]:
    # /mobs lines without a corpse marker; the command lists corpses too, so count only the
    # ones the server says are alive is not possible from the label — use /kill all's "Killed N".
    return []


def main() -> int:
    bridge = Bridge()
    try:
        r = bridge.call({"command": "game:status"})
        st = r.get("result") or {}
        say(f"status: running={st.get('running')} char={st.get('character')} area={st.get('area')}")
        if not st.get("running"):
            return 1

        # 1) baseline in the Coast
        say("re-enter the Coast through the waypoint (a fresh visit of the saved instance)")
        bridge.call({"command": "waypoint:travel", "area": COAST, "wait": True}, timeout=180)
        say("pos: " + text(bridge.call({"command": "chat", "text": "/pos"})))
        mobs = text(bridge.call({"command": "chat", "text": "/mobs"}))
        say(f"/mobs on arrival: {mobs[:260]}")
        killed = text(bridge.call({"command": "chat", "text": "/kill all"}))
        say(f"/kill all: {killed[:160]}")

        # 2) leave to town and come back: the same instance keeps its state.
        bridge.call({"command": "chat", "text": "/hideout"}, timeout=60)
        time.sleep(4)
        say("back to the Coast")
        bridge.call({"command": "waypoint:travel", "area": COAST, "wait": True}, timeout=180)
        time.sleep(2)
        mobs = text(bridge.call({"command": "chat", "text": "/mobs"}))
        say(f"/mobs after re-enter (same instance): {mobs[:260]}")
        say(f"/pos: {text(bridge.call({'command': 'chat', 'text': '/pos'}))}")
        say(f"/kill all again: {text(bridge.call({'command': 'chat', 'text': '/kill all'}))[:160]}")
        return 0
    finally:
        bridge.close()


if __name__ == "__main__":
    raise SystemExit(main())
