"""An AI driver for a live LocalExile client: one long-lived bridge session, one scripted run.

Why a driver instead of one-shot control.ps1 calls: every bridge call attaches and detaches Frida, and the
client exits on a detach cycle, so a run has to keep one session open from login to logout. This starts the
servers and the client (live.ps1), opens `client_control.py --stdio` once, and drives the client through it
with one JSON object per line.

Usage (from the repository root, on the portable toolchain PATH):
    python scripts/dev/ai_driver.py                 # servers + client + a scripted play-through
    python scripts/dev/ai_driver.py --no-start      # drive a stack and client that are already running

The run log goes to ai-test-run.log; every request and reply is written there.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
LOG = REPO / "ai-test-run.log"
CONTROL = REPO / "scripts" / "dev" / "client_control.py"
LIVE = REPO / "scripts" / "dev" / "live.ps1"


class Run:
    def __init__(self) -> None:
        self.lines: list[str] = []

    def say(self, text: str) -> None:
        line = f"[{time.strftime('%H:%M:%S')}] {text}"
        self.lines.append(line)
        print(line, flush=True)
        LOG.write_text("\n".join(self.lines) + "\n", encoding="utf-8")


class Bridge:
    """One `client_control.py --stdio` process: JSON lines in, JSON lines out."""

    def __init__(self, run: Run) -> None:
        self.run = run
        self.proc = subprocess.Popen(
            [sys.executable, str(CONTROL), "--stdio"],
            cwd=str(REPO), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1)

    def call(self, request: dict, timeout: float = 300.0) -> dict:
        assert self.proc.stdin and self.proc.stdout
        self.run.say("  -> " + json.dumps(request, ensure_ascii=False))
        self.proc.stdin.write(json.dumps(request) + "\n")
        self.proc.stdin.flush()
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            line = self.proc.stdout.readline()
            if not line:
                break
            line = line.strip()
            if not line.startswith("{"):
                continue
            reply = json.loads(line)
            shown = line if len(line) <= 400 else line[:400] + "..."
            self.run.say("  <- " + shown)
            return reply
        self.run.say("  <- no reply")
        return {"ok": False, "error": "no reply"}

    def close(self) -> None:
        try:
            if self.proc.stdin:
                self.proc.stdin.close()
        except OSError:
            pass
        try:
            self.proc.wait(timeout=20)
        except subprocess.TimeoutExpired:
            self.proc.kill()


def start_servers_and_client(run: Run) -> None:
    run.say("starting servers and client (live.ps1)")
    subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(LIVE),
                    "-NoBuild", "-NoExtract"], cwd=str(REPO), check=False)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--no-start", action="store_true", help="drive a stack and client that already run")
    parser.add_argument("--character", default="", help="character name (default: the highest level)")
    parser.add_argument("--account", default=os.environ.get("AI_TEST_ACCOUNT", "wwh"))
    parser.add_argument("--password", default=os.environ.get("AI_TEST_PASSWORD", "wwh1234"))
    args = parser.parse_args()

    run = Run()
    run.say("=== AI driver run ===")
    if not args.no_start:
        start_servers_and_client(run)

    bridge = Bridge(run)
    try:
        status = {}
        for _ in range(40):
            status = bridge.call({"command": "game:status"}, timeout=120)
            if status.get("ok") and (status.get("result") or {}).get("running"):
                break
            time.sleep(5)
        if not (status.get("ok") and (status.get("result") or {}).get("running")):
            run.say("the client never came up")
            return 1

        # The client reaches its login screen a few seconds after it is up; the saved login may or may not be there.
        for _ in range(30):
            state = (bridge.call({"command": "game:status"}, timeout=120).get("result") or {})
            if state.get("loginState") == 3 or state.get("screen") in ("characters", "game"):
                break
            if state.get("screen") == "login":
                break
            time.sleep(4)

        characters = bridge.call({"command": "character:list"})
        if not characters.get("ok"):
            run.say(f"logging in as {args.account}")
            bridge.call({"command": "login", "account": args.account, "password": args.password})
            characters = bridge.call({"command": "character:list"})
        entries = ((characters.get("result") or {}).get("characters") or [])
        if not entries:
            run.say("no characters")
            return 1
        name = args.character or max(entries, key=lambda c: c.get("level", 0))["name"]
        run.say(f"playing {name}")

        bridge.call({"command": f"character:play:{name}"})
        bridge.call({"command": "chat", "text": "/pos"})
        bridge.call({"command": "chat", "text": "/mobs"})

        # Towns disable attacks, so go to a combat area first: open every waypoint, then travel.
        run.say("travel: opening waypoints and going to 1_1_4_1 (The Coast)")
        bridge.call({"command": "chat", "text": "/waypoints all"})
        time.sleep(1)
        bridge.call({"command": "waypoint:travel", "area": "1_1_4_1", "wait": True}, timeout=180)
        bridge.call({"command": "chat", "text": "/pos"})

        # Aura test: activate each supported aura with the dev command (no socketed gem needed), then read /auras
        # for the reservation and buff state. Towns disable skills, so this runs after the travel above.
        AURAS = [("Determination", 20), ("Grace", 20), ("Haste", 20), ("Anger", 20), ("Clarity", 20), ("Vitality", 20)]
        for name, level in AURAS:
            reply = bridge.call({"command": "chat", "text": f"/aura_on {name} {level}"})
            text = " ".join(((reply.get("result") or {}).get("replies") or [])) or reply.get("error", "")
            run.say(f"aura: {name} -> {text[:160]}")
            time.sleep(1.0)
        run.say("aura: state after activation")
        auras = bridge.call({"command": "chat", "text": "/auras"})
        run.say("aura: " + " ".join(((auras.get("result") or {}).get("replies") or []))[:400])
        # The character's own panel stats with the auras on: /defences shows armour and evasion (Determination and
        # Grace should have raised them over their base values).
        bridge.call({"command": "chat", "text": "/defences 5"})
        report_aura_log(run)
        return 0
    finally:
        bridge.close()


def report_aura_log(run: Run) -> None:
    """The gateway's own record of the aura toggles: reservations, buffs, refusals."""
    log = REPO / "logs" / "live-gateway.log"
    if not log.exists():
        run.say("aura: no gateway log")
        return
    lines = log.read_text(encoding="utf-8", errors="replace").splitlines()[-300:]
    wanted = [l for l in lines
              if "aura" in l.lower() or "reservation" in l.lower() or "reserved" in l.lower()
              or "AddBuff" in l or "0x01c1" in l.lower()]
    run.say(f"aura: gateway log, {len(wanted)} relevant line(s)")
    for line in wanted[-12:]:
        run.say("  | " + line.strip()[:200])


def report_combat_log(run: Run, target: int) -> None:
    """The gateway's own record of what the attacks did: damage, hits, the monster's death."""
    log = REPO / "logs" / "live-gateway.log"
    if not log.exists():
        run.say("combat: no gateway log")
        return
    lines = log.read_text(encoding="utf-8", errors="replace").splitlines()[-400:]
    hex_id = f"0x{target:08x}".lower()
    wanted = [l for l in lines
              if hex_id in l.lower() or "damage" in l.lower() or "Splitting Steel" in l
              or "Leap Slam" in l or "killed" in l.lower() or "evaded" in l.lower()]
    run.say(f"combat: gateway log, {len(wanted)} relevant line(s)")
    for line in wanted[-12:]:
        run.say("  | " + line.strip()[:200])


if __name__ == "__main__":
    raise SystemExit(main())
