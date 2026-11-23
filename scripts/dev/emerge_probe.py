"""Emerge probe: take the character next to a lurking/buried monster and check it rises.

A monster waiting to rise (MonsterEmerge) stays frozen until the player comes within Range (24 cells) of it, or
a lurking one is hit. Walking up to it must produce "0x02bc ... Transitionable" and the Emerge action in the
gateway log, and /mobs must stop calling it "lurking"/"buried".
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


ENTRY = re.compile(r"0x([0-9a-f]+) (.+?) \((-?\d+), (-?\d+)\)( \((lurking|buried|chasing)\))?(?:, |$)")


def waiting(mobs: str, kind: str) -> list[tuple[str, int, int]]:
    out = []
    for m in ENTRY.finditer(mobs):
        if m.group(6) == kind:
            out.append((m.group(1), int(m.group(3)), int(m.group(4))))
    return out


def main() -> int:
    bridge = Bridge()
    try:
        st = bridge.call({"command": "game:status"}).get("result") or {}
        say(f"status: running={st.get('running')} area={st.get('area')}")
        if not st.get("running"):
            return 1
        say("pos: " + text(bridge.call({"command": "chat", "text": "/pos"}, timeout=60)))
        if st.get("area") != "1_1_4_1":
            say("travel to the Coast (emerge varieties: Cavern Strider, Broodling)")
            bridge.call({"command": "chat", "text": "/waypoints all"}, timeout=60)
            time.sleep(1)
            bridge.call({"command": "waypoint:travel", "area": "1_1_4_1", "wait": True}, timeout=180)
            time.sleep(2)
            say("pos: " + text(bridge.call({"command": "chat", "text": "/pos"}, timeout=60)))
        mobs = text(bridge.call({"command": "chat", "text": "/mobs"}, timeout=60))
        lurking = waiting(mobs, "lurking")
        buried = waiting(mobs, "buried")
        say(f"waiting: {len(lurking)} lurking, {len(buried)} buried")
        if not lurking and not buried:
            say("no waiting monster in this area; nothing to test here")
            return 0
        target = (lurking or buried)[0]
        id_, x, y = target
        say(f"target 0x{id_} at ({x}, {y}); teleport 12 cells away")
        say("tp: " + text(bridge.call({"command": "chat", "text": f"/tp {x + 12} {y}"}, timeout=90))[:160])
        time.sleep(0.5)
        say("pos: " + text(bridge.call({"command": "chat", "text": "/pos"}, timeout=60)))
        for wait in (1, 2, 4):
            time.sleep(wait)
            mobs = text(bridge.call({"command": "chat", "text": "/mobs"}, timeout=60))
            entry = next((m.group(0) for m in ENTRY.finditer(mobs) if m.group(1) == id_), "gone")
            say(f"+{wait}s: {entry}")
        return 0
    finally:
        bridge.close()


if __name__ == "__main__":
    raise SystemExit(main())
