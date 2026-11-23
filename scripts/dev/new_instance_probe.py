"""New-instance probe: a fully cleared area should generate a fresh instance (new monsters) when the
client re-enters it through a logout/relogin, because the old instance holds only corpses.
"""

from __future__ import annotations

import json
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
        say("exit to character selection and log back in (a fresh instance for the area)")
        bridge.call({"command": "chat", "text": "/exit"}, timeout=60)
        time.sleep(6)
        bridge.call({"command": "login:saved"}, timeout=120)
        time.sleep(5)
        chars = bridge.call({"command": "character:list"}, timeout=120)
        say("characters: " + json.dumps(chars)[:160])
        play = bridge.call({"command": "character:play", "name": "wda"}, timeout=180)
        say("play wda: " + json.dumps(play)[:160])
        time.sleep(6)
        say("travel to the Coast")
        bridge.call({"command": "chat", "text": "/waypoints all"}, timeout=60)
        time.sleep(1)
        bridge.call({"command": "waypoint:travel", "area": COAST, "wait": True}, timeout=180)
        time.sleep(2)
        say("pos: " + text(bridge.call({"command": "chat", "text": "/pos"}, timeout=60)))
        mobs = text(bridge.call({"command": "chat", "text": "/mobs"}, timeout=60))
        say(f"/mobs after relogin: {mobs[:300]}")
        say(f"/kill all: {text(bridge.call({'command': 'chat', 'text': '/kill all'}, timeout=60))[:200]}")
        return 0
    finally:
        bridge.close()


if __name__ == "__main__":
    raise SystemExit(main())
