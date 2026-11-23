"""Monster respawn / refresh probe: walks the character around an area, kills monsters, and watches what
the server does — spawn counts, buried/lurking state, the wake zone, respawn after a clear.

Run while the stack is up (live.ps1 / ai_driver.py has started it):
    python scripts/dev/spawn_probe.py
Every step and reply goes to stdout; the gateway log has the server's own lines.
"""

from __future__ import annotations

import json
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]


class Bridge:
    """One client_control.py --stdio session for the whole probe."""

    def __init__(self) -> None:
        self.proc = subprocess.Popen(
            [sys.executable, str(REPO / "scripts" / "dev" / "client_control.py"), "--stdio"],
            cwd=str(REPO), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1)

    def call(self, request: dict, timeout: float = 180.0) -> dict:
        assert self.proc.stdin and self.proc.stdout
        self.proc.stdin.write(json.dumps(request) + "\n")
        self.proc.stdin.flush()
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            line = self.proc.stdout.readline()
            if not line:
                break
            line = line.strip()
            if line.startswith("{"):
                return json.loads(line)
        return {"ok": False, "error": "no reply"}

    def close(self) -> None:
        try:
            if self.proc.stdin:
                self.proc.stdin.close()
        except OSError:
            pass
        try:
            self.proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.proc.kill()


def say(text: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {text}", flush=True)


def replies(reply: dict) -> str:
    return " ".join(((reply.get("result") or {}).get("replies") or []))


def mob_count(text: str) -> int:
    return 0 if "No monsters" in text else text.count("0x")


def main() -> int:
    bridge = Bridge()
    try:
        status = bridge.call({"command": "game:status"})
        if not ((status.get("result") or {}).get("running")):
            say("the client is not running; start the stack first")
            return 1
        say("travel: to 1_1_2a (The Tidal Island), a small area good for spawn checks")
        bridge.call({"command": "chat", "text": "/waypoints all"})
        time.sleep(1)
        bridge.call({"command": "waypoint:travel", "area": "1_1_2a", "wait": True}, timeout=180)
        say("pos: " + replies(bridge.call({"command": "chat", "text": "/pos"})))

        say("baseline /mobs")
        baseline = replies(bridge.call({"command": "chat", "text": "/mobs"}))
        say(f"mobs ({mob_count(baseline)}): {baseline[:300]}")

        # Walk in a few directions to wake buried/lurking monsters: the rise path needs player proximity.
        pos = baseline  # keep the log short; the walk uses fixed offsets around the arrival point
        for dx, dy in ((30, 0), (0, 30), (-30, 0), (0, -30), (30, 30)):
            bridge.call({"command": "move", "x": 400 + dx, "y": 1300 + dy})
            time.sleep(1.0)
        time.sleep(3)
        after = replies(bridge.call({"command": "chat", "text": "/mobs"}))
        say(f"after walking ({mob_count(after)}): {after[:300]}")

        # Kill everything through the normal death path (experience, drops, quest progress) and see what remains.
        bridge.call({"command": "chat", "text": "/kill all"})
        time.sleep(3)
        cleared = replies(bridge.call({"command": "chat", "text": "/mobs"}))
        say(f"after /kill all ({mob_count(cleared)}): {cleared[:200]}")

        # Respawn check: wait and see whether anything comes back (most PoE areas do not respawn within a visit).
        for wait in (10, 20):
            time.sleep(wait)
            later = replies(bridge.call({"command": "chat", "text": "/mobs"}))
            say(f"after +{wait}s ({mob_count(later)}): {later[:200]}")

        # Spawn a known pack at a distance and check it appears and goes to sleep when far (the far-boss path).
        bridge.call({"command": "chat", "text": "/spawn_mob Zombie@5"})
        time.sleep(2)
        spawned = replies(bridge.call({"command": "chat", "text": "/mobs"}))
        say(f"after /spawn_mob Zombie ({mob_count(spawned)}): {spawned[:250]}")
        return 0
    finally:
        bridge.close()


if __name__ == "__main__":
    raise SystemExit(main())
