"""Plays a scripted input scenario on a session client with real mouse input and records where the client has every
object (scripts/re/frida/trace-sync.js), to compare with the Gateway's Game_TRACE=sync estimates (sync_report.py).

    .venv/Scripts/python.exe scripts/dev/sync_drive.py --session sync --out logs/sync-client.jsonl walk-clicks

The client must be in game (client_control). The scenarios are at the end of this file; a line of the --commands file
runs one bridge request (JSON), so an area can be prepared without a scenario for it.
"""
import frida
import argparse
import ctypes
from ctypes import wintypes
import json
import math
from pathlib import Path
import random
import threading
import time

ROOT = Path(__file__).resolve().parents[2]


def client_window(pid):
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    found = []
    callback = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def each(hwnd, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            box = wintypes.RECT()
            user32.GetClientRect(hwnd, ctypes.byref(box))
            found.append((box.right * box.bottom, hwnd, box.right, box.bottom))
        return True

    user32.EnumWindows(callback(each), 0)
    if not found:
        raise SystemExit(f"no visible window for pid {pid}")
    _, hwnd, width, height = max(found)
    return hwnd, width, height


class Driver:
    def __init__(self, session, out):
        record = json.loads((ROOT / "data" / "clients" / f"{session}.json").read_text(encoding="utf-8"))
        self.pid = int(record["pid"])
        self.hwnd, self.width, self.height = client_window(self.pid)
        self.device = frida.get_local_device()
        self.session = self.device.attach(self.pid)
        self.log = open(out, "a", encoding="utf-8")
        self.lock = threading.Lock()
        self.scripts = []
        for name in ("trace-sync.js", "drive-input.js"):
            script = self.session.create_script((ROOT / "scripts" / "re" / "frida" / name).read_text(encoding="utf-8"))
            script.on("message", self.message)
            script.load()
            self.scripts.append(script)
        self.input = self.scripts[1].exports_sync
        self.input.attach(self.hwnd)
        self.cx, self.cy = self.width // 2, self.height // 2 - 12

    def message(self, message, _):
        payload = message.get("payload") if message["type"] == "send" else {"error": message.get("description")}
        with self.lock:
            self.log.write(json.dumps(payload) + "\n")

    def at(self, angle, radius):
        """The screen point `radius` pixels from the player, who stands about the middle of the screen."""
        return (max(4, min(self.width - 4, int(self.cx + radius * math.cos(angle)))),
                max(4, min(self.height - 40, int(self.cy + radius * math.sin(angle) * .75))))

    def mark(self, label):
        with self.lock:
            self.log.write(json.dumps({"mark": label, "at": time.time()}) + "\n")

    def click(self, angle, radius, seconds=.03):
        self.input.cursor(*self.at(angle, radius))
        time.sleep(.03)
        self.input.down()
        time.sleep(seconds)
        self.input.up()

    def close(self):
        self.input.up()
        for script in self.scripts:
            script.unload()
        self.session.detach()
        self.log.close()


def walk_clicks(d, seconds):
    """A click every ~0.8 s in a new direction; each walk ends on its target or is replaced."""
    end = time.time() + seconds
    while time.time() < end:
        d.click(random.uniform(0, math.tau), random.uniform(60, 140))
        time.sleep(random.uniform(.35, .9))


def walk_held(d, seconds):
    """The button held, the cursor turning slowly: the case that drifts most in 07 §5."""
    angle = random.uniform(0, math.tau)
    d.input.cursor(*d.at(angle, 240))
    time.sleep(.03)
    d.input.down()
    end = time.time() + seconds
    while time.time() < end:
        angle += random.uniform(-.35, .35)
        d.input.cursor(*d.at(angle, 240))
        time.sleep(.1)
    d.input.up()


def stand(d, seconds):
    time.sleep(seconds)


def skill_clicks(d, seconds):
    """The skill on the right mouse button at a new point every ~1.2 s, near and far, with a walk click between."""
    end = time.time() + seconds
    while time.time() < end:
        d.input.cursor(*d.at(random.uniform(0, math.tau), random.uniform(60, 330)))
        time.sleep(.05)
        if d.key:
            d.input.key(d.key)
        else:
            d.input.skill()
        time.sleep(random.uniform(.8, 1.4))
        d.click(random.uniform(0, math.tau), random.uniform(60, 140))
        time.sleep(.35)


SCENARIOS = {"walk-clicks": walk_clicks, "walk-held": walk_held, "stand": stand, "skill-clicks": skill_clicks}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("scenario", choices=SCENARIOS)
    parser.add_argument("--session", default="sync")
    parser.add_argument("--out", default=str(ROOT / "logs" / "sync-client.jsonl"))
    parser.add_argument("--seconds", type=float, default=30)
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--key", type=lambda v: int(v, 0), default=0, help="skill-clicks: this virtual key, not the right button")
    options = parser.parse_args()
    random.seed(options.seed)
    driver = Driver(options.session, options.out)
    driver.key = options.key
    try:
        driver.mark(f"start {options.scenario}")
        SCENARIOS[options.scenario](driver, options.seconds)
        time.sleep(1)
        driver.mark(f"end {options.scenario}")
    finally:
        driver.close()


if __name__ == "__main__":
    main()
