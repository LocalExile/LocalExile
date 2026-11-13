"""Drive the live client into an Atlas boss arena and record what the fight looks like.

The fight is a client-side effect: the server picks an action, the client plays the clip. So the only
honest check is to look at the running client — a screenshot per action, next to the actions the
Gateway actually sent. This tool does both in one run:

    python scripts/dev/boss_test.py --boss shaper        # The Shaper's Realm
    python scripts/dev/boss_test.py --boss elder         # Absence of Value and Meaning
    python scripts/dev/boss_test.py --boss uber-elder    # both, in The Shaper's Realm (uber)
    python scripts/dev/boss_test.py --boss shaper --seconds 90 --interval 0.25 --keep-client

What it does, in order, and each step is in the report:

1. attaches to this checkout's client (``--pid``, else ``data/clients/default.json``, else the largest
   ``Game.patched.exe``), and gets it into the game: saved login, then a character;
2. ``/god`` (the run must not end in a corpse), ``/goto <area> <seed>`` and waits for the area;
3. reads the boss's cell from ``/mobs``, walks the character next to it with ``/tp``;
4. captures frames through Windows Graphics Capture (no focus, no z-order change, client area only)
   while it tails ``logs/live-gateway.log`` for the boss's actions (0x1ab), its life bar (0x02bc) and
   its life updates (0x01b1);
5. writes ``report.md`` — the action timeline with each action's skill, the life-bar packets, and the
   frames — plus ``frames/`` and a contact sheet ``sheet.png`` for a quick look.

The skill ids in the Gateway log are the actor skill ids the boss was given (0x4000-0x402c); the names
come from ``Modules/Core/Game/Monster/AtlasBosses.cs``, parsed here so the table cannot drift.

Needs ``windows-capture`` and Pillow in the interpreter that runs it (the contact sheet is skipped
without Pillow). Frames are the client's client area, so window chrome and any window on top are out.
"""

from __future__ import annotations

import argparse
from datetime import datetime
import ctypes
import ctypes.wintypes as wt
import json
import os
import re
import subprocess
import sys
import threading
import time
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent.parent
WALL_START = None  # when the capture started, to line the Gateway's stamps up with the frames
GATEWAY_LOG = REPO / "logs" / "live-gateway.log"
CONTROL = REPO / "scripts" / "dev" / "client_control.py"

# area id, seed, the boss the area holds, and how far from it the character stands
BOSSES = {
    "shaper": ("MapWorldsShapersRealm", "The Shaper (TheShaperBoss, level 84)"),
    "elder": ("MapWorldsElderArena", "The Elder (TheElder, level 83)"),
    "uber-elder": ("MapWorldsElderArenaUber", "Uber Elder (TheElderUber + TheShaperBossUberElder, level 84)"),
    "sirus": ("AtlasExilesBoss5", "Sirus (AtlasExile5, level 84)"),
}
SEED = "1720089000"
STAND_OFF = 14  # cells from the boss: inside its reach, outside its body


# --- the client -------------------------------------------------------------

def client_pid(explicit: int) -> int:
    if explicit:
        return explicit
    for record in [REPO / "data" / "clients" / "default.json", *sorted((REPO / "data" / "clients").glob("*.json"))]:
        try:
            data = json.loads(record.read_text())
        except (OSError, ValueError):
            continue
        pid = data.get("pid") or 0
        if pid and _alive(pid) and data.get("session") == "default":
            return pid
    best, area = 0, 0
    # Win32_Process was already filtered to the client, so the name adds nothing.
    for entry in _processes():
        if (entry["working"] or 0) > area:
            best, area = entry["pid"], entry["working"]
    return best


def _alive(pid: int) -> bool:
    out = subprocess.run(["powershell", "-NoProfile", "-Command", f"(Get-Process -Id {pid} -ErrorAction SilentlyContinue) -ne $null"],
                         capture_output=True, text=True)
    return out.stdout.strip().lower() == "true"


def _processes() -> list[dict]:
    out = subprocess.run(["powershell", "-NoProfile", "-Command",
                          "Get-CimInstance Win32_Process -Filter \"Name = 'Game.patched.exe'\" | "
                          "Select-Object ProcessId,WorkingSetSize,ExecutablePath | ConvertTo-Json -Compress"],
                         capture_output=True, text=True)
    try:
        rows = json.loads(out.stdout or "[]")
    except ValueError:
        return []
    rows = rows if isinstance(rows, list) else [rows]
    return [{"pid": r.get("ProcessId"), "working": r.get("WorkingSetSize") or 0,
             "path": r.get("ExecutablePath") or ""} for r in rows]


class Bridge:
    """One attached client-control process, so the Frida agent stays attached for the whole run."""

    def __init__(self) -> None:
        self.proc = subprocess.Popen([sys.executable, str(CONTROL), "--stdio"], cwd=str(REPO),
                                     stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, bufsize=1)

    def __call__(self, command: str, **kwargs) -> dict:
        request = {"command": command, **kwargs}
        self.proc.stdin.write(json.dumps(request) + "\n")
        self.proc.stdin.flush()
        line = self.proc.stdout.readline()
        if not line:
            raise RuntimeError(f"the bridge closed on {command}")
        answer = json.loads(line)
        if not answer.get("ok"):
            raise RuntimeError(f"{command}: {answer.get('error')}")
        return answer.get("result") or {}

    def close(self) -> None:
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=10)
        except Exception:
            self.proc.kill()


# --- frames ----------------------------------------------------------------

def windows_of(pid: int) -> list[int]:
    user32 = ctypes.windll.user32
    user32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
    found: list[tuple[int, int]] = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def visit(hwnd, _):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            rect = wt.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(rect))
            found.append(((rect.right - rect.left) * (rect.bottom - rect.top), hwnd))
        return True

    user32.EnumWindows(visit, 0)
    found.sort(reverse=True)
    return [hwnd for _, hwnd in found]


class Frames:
    """Saves the client's client area to PNG every ``interval`` seconds, in a WGC session of its own."""

    def __init__(self, pid: int, folder: Path, interval: float) -> None:
        from windows_capture import WindowsCapture

        handles = windows_of(pid)
        if not handles:
            raise RuntimeError(f"no visible window of pid {pid}")
        self.hwnd = handles[0]
        self.folder = folder
        self.folder.mkdir(parents=True, exist_ok=True)
        self.interval = interval
        self.saved: list[tuple[float, Path]] = []
        self._wall: list[float] = []
        self.started = time.monotonic()
        self._lock = threading.Lock()
        self._stop = False
        user32 = ctypes.windll.user32
        if user32.IsIconic(self.hwnd):  # WGC cannot capture an iconified window
            user32.ShowWindow(self.hwnd, 4)
            user32.SetWindowPos(self.hwnd, None, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0040)
        self.capture = WindowsCapture(cursor_capture=False, draw_border=False, window_hwnd=self.hwnd)

        @self.capture.event
        def on_frame_arrived(frame, control):
            with self._lock:
                if self._stop or time.monotonic() - self._last < self.interval:
                    return
                self._last = time.monotonic()
                index = len(self.saved) + 1
                name = self.folder / f"frame-{index:04d}-{int((self._last - self.started) * 1000):06d}ms.png"
                frame.save_as_image(str(name))
                self.saved.append((self._last - self.started, name))
                self._wall.append(time.time())
                if index >= 4_000:
                    control.stop()

        @self.capture.event
        def on_closed():
            pass

        self._last = 0.0
        self.thread = threading.Thread(target=self.capture.start, daemon=True)
        self.thread.start()

    def wall(self) -> list[float]:
        return self._wall

    def stop(self) -> list[tuple[float, Path]]:
        self._stop = True
        try:
            self.capture.stop()
        except Exception:
            pass
        self.thread.join(timeout=5)
        return self.saved


# --- the Gateway's side of the story ---------------------------------------

def skill_names() -> dict[int, str]:
    """The bosses' actor skill ids, from the constants the server gives them."""
    source = (REPO / "Modules" / "Core" / "Game" / "Monster" / "AtlasBosses.cs").read_text(encoding="utf-8")
    effects = {int(row): name for name, row in re.findall(r"internal const int (\w+)Effect = (\d+);", source)}
    names: dict[int, str] = {}
    for base, group in ((0x4000, "Shaper"), (0x4010, "Elder"), (0x4020, "Sirus")):
        order = re.findall(rf"{group}Effects = \[(.*?)\];", source, re.S)
        if not order:
            continue
        rows = [int(x) for x in re.findall(r"\d+", order[0])]
        for index, row in enumerate(rows):
            names[base + index] = effects.get(row, str(row))
    return names


class ActionLog:
    """Tails the Gateway log from now on and keeps what the bosses did."""

    ACTION = re.compile(r"^(\d\d:\d\d:\d\d\.\d+) .*?=> 0x01ab monster 0x([0-9a-f]+) \((.*?)\) (.*?): ([0-9A-F]+)$")
    AREA = re.compile(r"uses area skill 0x([0-9a-f]+), radius")
    BAR = re.compile(r"0x02bc monster 0x([0-9a-f]+) \((.*?)\) StateMachine")
    FIGHT = re.compile(r"0x([0-9a-f]+) (.*?): fight started")
    LIFE = re.compile(r"monster 0x([0-9a-f]+) \((.*?)\) life")

    def __init__(self) -> None:
        self.offset = GATEWAY_LOG.stat().st_size if GATEWAY_LOG.exists() else 0
        self.bosses: dict[str, str] = {}
        self.actions: list[dict] = []
        self.bars: list[str] = []
        self.started = time.monotonic()

    def poll(self) -> None:
        if not GATEWAY_LOG.exists():
            return
        size = GATEWAY_LOG.stat().st_size
        if size < self.offset:  # the log was rotated under us
            self.offset = 0
        if size == self.offset:
            return
        with GATEWAY_LOG.open("r", encoding="utf-8", errors="replace") as stream:
            stream.seek(self.offset)
            for line in stream:
                self._line(line.rstrip("\n"))
            self.offset = stream.tell()

    def _line(self, line: str) -> None:
        if match := self.FIGHT.search(line):
            self.bosses[match.group(1)] = match.group(2)
            return
        if match := self.BAR.search(line):
            self.bars.append(line.split("[gateway]")[-1].strip())
            return
        if match := self.ACTION.search(line):
            when, monster, label, what, body = match.groups()
            skill = None
            if area := self.AREA.search(what):
                skill = int(area.group(1), 16)
            else:
                # the action's own skill id sits in the last few bytes, before its order id
                ids = [int(body[i:i + 4], 16) for i in range(0, len(body) - 3, 2)]
                skill = next((i for i in ids if 0x4000 <= i <= 0x4030), None)
            self.actions.append({"at": when, "label": label, "monster": monster, "what": what, "skill": skill})



def player_cell(bridge: Bridge) -> tuple[int, int] | None:
    for line in chat(bridge, "/pos", wait=1.0):
        if match := re.search(r"\((\d+),\s*(\d+)\)", line):
            return int(match.group(1)), int(match.group(2))
    return None


def send_moves(bridge: Bridge, target: tuple[int, int], stop: int = 20, hops: int = 40,
               log=print) -> bool:
    """Walks the character to the boss with the client's own Move (C->S 0x25) in hops of at most 120
    cells. `act walkTo` would click like a player, but its clicks read the operating system's cursor,
    so they resolve to the wrong cell while no one is moving the mouse (measured here 2026-11-13:
    "aim misread ... the cursor is not reading the world"); a Move needs no cursor."""
    import math

    for hop in range(1, hops + 1):
        here = player_cell(bridge)
        if not here:
            log(f"  hop {hop}: no position")
            return False
        dx, dy = target[0] - here[0], target[1] - here[1]
        distance = math.hypot(dx, dy)
        if distance <= stop:
            log(f"  arrived at {here}, {distance:.0f} cells from the boss")
            return True
        step = min(distance, 120)
        want = (int(here[0] + dx / distance * step), int(here[1] + dy / distance * step))
        bridge("move", x=want[0], y=want[1])
        time.sleep(3.0)
        now = player_cell(bridge) or here
        moved = math.hypot(now[0] - here[0], now[1] - here[1])
        log(f"  hop {hop}: {here} -> {now} (asked {want}, moved {moved:.0f}, left {math.hypot(target[0]-now[0], target[1]-now[1]):.0f})")
        if moved < 2:
            log("  the character did not move: stopping here")
            return False
    return False


def walk_towards(bridge: Bridge, target: tuple[int, int], stop: int = 22, hops: int = 80,
                 log=print) -> bool:
    """Clicks the character towards the boss the way a player would: one `act` hop at a time, the cell
    re-read between hops. A click reaches about 16 cells, so a distant boss is a run of clicks."""
    import math

    for hop in range(1, hops + 1):
        here = player_cell(bridge)
        if not here:
            log(f"  hop {hop}: no position")
            return False
        dx, dy = target[0] - here[0], target[1] - here[1]
        distance = math.hypot(dx, dy)
        if distance <= stop:
            log(f"  arrived at {here}, {distance:.0f} cells from the boss")
            return True
        step = min(max(distance - stop + 4, 8), 60)
        want = (int(here[0] + dx / distance * step), int(here[1] + dy / distance * step))
        result = bridge("act", kind="walkTo", x=want[0], y=want[1])
        log(f"  hop {hop}: {here} -> {want} reached={result.get('reached')} cell={result.get('cell')} "
            f"clicks={result.get('clicks')} {result.get('reason', '')}".rstrip())
        if result.get("cell") and distance < 120 and not result.get("reached"):
            # the last few cells are not worth a retry loop here
            pass
    return False


# --- the run ---------------------------------------------------------------

def chat(bridge: Bridge, line: str, wait: float = 1.5) -> list[str]:
    answer = bridge("chat", text=line, wait=wait)
    return answer.get("replies") or []


def boss_cell(replies: list[str]) -> tuple[int, int] | None:
    for line in replies:
        if match := re.match(r"0x[0-9a-f]+ .*?\((\d+), (\d+)\)", line):
            return int(match.group(1)), int(match.group(2))
    return None


def montage(frames: list[tuple[float, Path]], out: Path, columns: int = 4, width: int = 640) -> Path | None:
    try:
        from PIL import Image, ImageDraw
    except ImportError:
        return None
    if not frames:
        return None
    thumbs = []
    for offset, path in frames:
        image = Image.open(path)
        height = int(image.height * width / image.width)
        image = image.resize((width, height), Image.LANCZOS).convert("RGB")
        draw = ImageDraw.Draw(image)
        draw.rectangle([0, 0, 210, 26], fill=(0, 0, 0))
        draw.text((6, 6), f"#{path.stem.split('-')[1]}  {offset:.1f}s", fill=(255, 235, 120))
        thumbs.append(image)
    rows = (len(thumbs) + columns - 1) // columns
    cell = thumbs[0].height
    sheet = Image.new("RGB", (columns * width, rows * cell), (18, 18, 18))
    for index, image in enumerate(thumbs):
        sheet.paste(image, ((index % columns) * width, (index // columns) * cell))
    sheet.save(out)
    return out


def closest(at: str | None, frames: list[tuple[float, Path]], window: float = 0.6) -> str:
    """The frame nearest a log stamp, as a markdown link, when it is within ``window`` seconds."""
    if not at or not frames:
        return ""
    from datetime import datetime, timedelta
    clock = datetime.combine(datetime.today(), datetime.strptime(at, "%H:%M:%S.%f").time())
    best, gap = None, window
    for offset, path in frames:
        started = WALL_START
        delta = abs((started + timedelta(seconds=offset) - clock).total_seconds())
        if delta < gap:
            best, gap = path, delta
    return f"[{best.stem.split('-')[1]}](frames/{best.name})" if best else ""


def report(folder: Path, boss: str, area: str, character: str, actions: list[dict], names: dict[int, str],
           bars: list[str], frames: list[tuple[float, Path]], notes: list[str]) -> Path:
    def name_of(action: dict) -> str:
        skill = action.get("skill")
        return f"{names.get(skill, '?')} (0x{skill:04x})" if skill else "?"

    lines = [f"# {BOSSES[boss][1]} — live run", "",
             f"- area: `{area}` (seed {SEED})",
             f"- character: {character}",
             f"- frames: {len(frames)} in `frames/`",
             f"- actions the Gateway sent: {len(actions)}",
             f"- boss life bar packets (0x02bc): {len(bars)}", ""]
    if notes:
        lines += ["## Steps", ""] + [f"- {note}" for note in notes] + [""]
    lines += ["## Actions", "", "| at | boss | what | skill | nearest frame |", "|---|---|---|---|---|"]
    for action in actions:
        frame = closest(action.get("at"), frames)
        lines.append(f"| {action.get('at', '')} | {action['label']} | {action['what']} | {name_of(action)} "
                     f"| {frame} |")
    lines += ["", "## Life bar", ""]
    lines += [f"- {line}" for line in bars] or ["- none: the 0x02bc boss_life_bar record never went out"]
    lines += ["", "## Frames", "", "| t (s) | file |", "|---|---|"]
    lines += [f"| {offset:.1f} | [{path.name}](frames/{path.name}) |" for offset, path in frames]
    path = folder / "report.md"
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--boss", default="shaper", choices=sorted(BOSSES))
    parser.add_argument("--character", help="the character to play (default: the first the client lists)")
    parser.add_argument("--pid", type=int, default=0, help="the client's pid (default: this checkout's default client)")
    parser.add_argument("--out", help="the folder for frames and the report")
    parser.add_argument("--seconds", type=float, default=60.0, help="how long to watch the fight")
    parser.add_argument("--interval", type=float, default=0.35, help="seconds between saved frames")
    parser.add_argument("--approach", choices=["tp", "move", "walk"], default="tp",
                        help="how to reach the boss: /tp (default), the client's Move packets (move), or "
                             "real clicks (walk, needs the operating system's cursor to move too)")
    parser.add_argument("--stay", action="store_true",
                        help="do not change area or walk: watch the fight where the character stands")
    parser.add_argument("--no-montage", action="store_true")
    parser.add_argument("--dry-run", action="store_true", help="print the plan and stop")
    args = parser.parse_args()

    area, title = BOSSES[args.boss]
    folder = Path(args.out) if args.out else REPO / "logs" / "boss-test" / f"{time.strftime('%Y%m%d-%H%M%S')}-{args.boss}"
    pid = client_pid(args.pid)
    print(f"{title}\n  area {area}, seed {SEED}\n  client pid {pid or '?'}\n  out {folder}")
    if not pid:
        print("no client: start one with scripts/dev/live.ps1 first", file=sys.stderr)
        return 2
    if args.dry_run:
        return 0

    folder.mkdir(parents=True, exist_ok=True)
    notes: list[str] = []
    bridge = Bridge()
    frames: Frames | None = None
    log = ActionLog()
    try:
        status = bridge("game:status")
        notes.append(f"client at `{status.get('screen')}`")
        if status.get("screen") == "login":
            bridge("login:saved")
            status = bridge("game:status")
            notes.append("logged in with the saved login")
        if status.get("screen") == "characters":
            characters = bridge("character:list").get("characters") or []
            pick = args.character or (characters[0]["name"] if characters else None)
            if not pick:
                raise RuntimeError("no character to play")
            bridge("character:play", name=pick)
            character = pick
            notes.append(f"played {pick}")
        else:
            character = status.get("character") or "?"
            notes.append(f"already in game as {character}")
        time.sleep(2)

        god = chat(bridge, "/god")
        notes.append(f"`/god` -> {god}")
        if not any("God mode on" in line for line in god):
            chat(bridge, "/god")
            notes.append("`/god` again (it is a toggle and was already on)")

        if not args.stay:
            bridge("chat", text=f"/goto {area} {SEED}", wait=2.0)
            try:
                bridge("area:wait", area=area, timeout=90)
            except RuntimeError:
                pass
            status = bridge("game:status")
            notes.append(f"area `{status.get('area')}`")
            time.sleep(3)
        else:
            notes.append("staying where the character stands")

        frames = Frames(pid, folder / "frames", args.interval)
        global WALL_START
        WALL_START = datetime.fromtimestamp(time.time())
        # The tail was started before /goto on purpose: the boss's life bar goes out as it spawns, a moment before the
        # run's own frames begin, and a tail started here would miss it (it reported "0 life-bar packets" with the
        # packet in the log).
        mobs = chat(bridge, "/mobs")
        notes.append(f"`/mobs` -> {mobs}")
        cell = boss_cell(mobs)
        if args.stay:
            reach = player_cell(bridge)
            if cell and reach:
                import math
                notes.append(f"character at {reach}, {math.hypot(cell[0] - reach[0], cell[1] - reach[1]):.0f} cells from the boss")
        elif not cell:
            notes.append("no boss cell in `/mobs`: the arena placed nothing")
        else:
            x, y = cell
            notes.append(f"boss at ({x}, {y})")
            if args.approach == "tp":
                bridge("chat", text=f"/tp {x - STAND_OFF} {y}", wait=2.0)
                notes.append(f"`/tp` to ({x - STAND_OFF}, {y})")
            else:
                walk = send_moves if args.approach == "move" else walk_towards
                reached = walk(bridge, cell, log=lambda line: (print(line), notes.append(line)))
                notes.append(f"reached the boss: {reached}")

        deadline = time.monotonic() + args.seconds
        while time.monotonic() < deadline:
            log.poll()
            time.sleep(0.5)
        log.poll()
    finally:
        saved = frames.stop() if frames else []
        bridge.close()

    names = skill_names()
    sheet = None if args.no_montage else montage(saved, folder / "sheet.png")
    path = report(folder, args.boss, area, locals().get("character", "?"), log.actions, names, log.bars, saved, notes)
    print(f"\n{len(saved)} frames, {len(log.actions)} actions, {len(log.bars)} life-bar packets")
    print(f"report {path}")
    if sheet:
        print(f"sheet  {sheet}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
