"""Enter the Aqueduct and inspect its population through the existing native-client bridge.

    .venv/Scripts/python.exe scripts/dev/packpoint_probe.py --session agent

Requires a logged-in client started by live.ps1. The Gateway log records room-point placement and the remaining
scattered pack budget. This probe changes the validation character's area; it does not unlock its waypoints.
"""
from __future__ import annotations

import argparse
import time
from client_control import ClientControl

AQUEDUCT = "1_4_1"


def say(text: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {text}", flush=True)


def replies(result: dict) -> str:
    return " ".join(result.get("replies") or [])


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--session", help="The live.ps1 client session to inspect")
    options = parser.parse_args()
    try:
        with ClientControl(options.session) as bridge:
            state = bridge.execute({"command": "game:status"})
            say(f"status: running={state.get('running')} area={state.get('area')}")
            if not state.get("running") or state.get("screen") != "game":
                say("A logged-in validation character is required")
                return 1
            bridge.execute({"command": "command", "text": f"/goto {AQUEDUCT}"})
            bridge.execute({"command": "area:wait", "area": AQUEDUCT, "timeout": 180})
            position = replies(bridge.execute({"command": "command", "text": "/pos", "wait": 30}))
            if not position:
                raise TimeoutError("The Gateway did not finish entering the area")
            say("pos: " + position[:160])
            mobs = replies(bridge.execute({"command": "command", "text": "/mobs"}))
            say(f"/mobs: {mobs[:240]}")
            say("Population details: logs/live-gateway.log")
            return 0
    except Exception as error:
        say(f"probe failed: {error}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
