"""Boss wake probe: a boss created far from the player is created asleep (no 0x1a9 wake zone, added to
AsleepMonsters). Walking up to it must wake it with EnterWakeZone (0x1a9); if it never wakes the boss is
invisible and untargetable on the client while the server thinks it is alive.
"""

from __future__ import annotations

import json
import re
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(r"D:\大肥鱼工作区\LocalExile")


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


ENTRY = re.compile(r"0x([0-9a-f]+) (.+?) \((-?\d+), (-?\d+)\)( \((lurking|buried|chasing)\))?")


def main() -> int:
    bridge = Bridge()
    try:
        st = bridge.call({"command": "game:status"}).get("result") or {}
        say(f"status: running={st.get('running')} area={st.get('area')}")
        if not st.get("running"):
            return 1
        mobs = text(bridge.call({"command": "chat", "text": "/mobs"}, timeout=60))
        bosses = [m for m in ENTRY.finditer(mobs) if "@" in m.group(2) and "Boss" in m.group(2) or "Princess" in m.group(2)]
        if not bosses:
            say("/mobs has no boss-like entry; listing the first few")
            say(mobs[:300])
            return 0
        m = bosses[0]
        id_, x, y = m.group(1), int(m.group(3)), int(m.group(4))
        say(f"boss 0x{id_} {m.group(2)} at ({x}, {y}); teleporting 6 cells away")
        say("tp: " + text(bridge.call({"command": "chat", "text": f"/tp {x + 6} {y}"}, timeout=90))[:120])
        for wait in (1, 2, 4):
            time.sleep(wait)
            mobs = text(bridge.call({"command": "chat", "text": "/mobs"}, timeout=60))
            entry = next((e.group(0) for e in ENTRY.finditer(mobs) if e.group(1) == id_), "gone")
            say(f"+{wait}s: {entry}")
        # Attack it: a sleeping object gets no order, so a hit is the other way a monster proves reachable.
        say("hit: " + text(bridge.call({"command": "chat", "text": f"/kill {id_}"}, timeout=90))[:140])
        return 0
    finally:
        bridge.close()


if __name__ == "__main__":
    raise SystemExit(main())
