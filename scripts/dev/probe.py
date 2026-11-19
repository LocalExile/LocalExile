"""Talk to the running Path of Exile client through client_control.py, from the shell.

One bridge session, a list of commands, one JSON answer line each:

    python scripts/dev/probe.py '[{"command":"game:status"}]'
    python scripts/dev/probe.py '[{"command":"logout"},{"command":"login:exile","password":"x"}]'
    python scripts/dev/probe.py '[{"command":"chat","text":"/pos"}]'

The answers are client_control's own (screen, characters, area, chat replies). Commands live in
scripts/dev/client_control.py; this is only the one-shot form, for when you do not need the bridge to
stay attached. For a whole run (Boss fight, frames) use scripts/dev/boss_test.py instead.

Pass --stdio-compatible commands only: "command" plus that command's fields.
"""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
CONTROL = REPO / "scripts" / "dev" / "client_control.py"


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    commands = json.loads(sys.argv[1])
    if isinstance(commands, dict):
        commands = [commands]
    proc = subprocess.Popen([sys.executable, str(CONTROL), "--stdio"], cwd=str(REPO),
                            stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, bufsize=1)
    failed = 0
    try:
        for item in commands:
            proc.stdin.write(json.dumps(item) + "\n")
            proc.stdin.flush()
            line = proc.stdout.readline()
            if not line:
                print(json.dumps({"ok": False, "error": "the bridge closed"}))
                return 1
            print(line.strip())
            sys.stdout.flush()
            if not json.loads(line).get("ok"):
                failed = 1
    finally:
        try:
            proc.stdin.close()
            proc.wait(timeout=20)
        except Exception:
            proc.kill()
    return failed


if __name__ == "__main__":
    raise SystemExit(main())
