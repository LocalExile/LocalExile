"""Screenshots of the live Path of Exile client, for looking at what the server did.

The client draws nearly everything itself, so a server change is only proven by watching the real
client. This takes frames of its window into PNGs, builds a contact sheet, and (with --gold) measures
how much of each frame is effect-colored, which is how a beam or a projectile is found in a timeline
without opening thirty images.

    python scripts/dev/shot.py --seconds 20 --interval 0.1 --out logs/shots/beam
    python scripts/dev/shot.py --seconds 20 --out logs/shots/beam --gold --crop 0.2,0.1,0.8,0.8
    python scripts/dev/shot.py --from logs/shots/beam --strip 40,44,48,52    # a second look, no capture

Then read the sheet (or a strip of single frames) with the image reader; see the skill
`localexile-client-shots` for the whole loop and its traps.

Nothing here starts or stops a server or a client: start those with scripts/dev/live.ps1 first.
"""

from __future__ import annotations

import argparse
import ctypes
import json
import re
import subprocess
import sys
import threading
import time
from ctypes import wintypes as wt
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]


def client_pid(explicit: int | None) -> int:
    """The client's pid: the pid given, else the one visible Game.patched process."""
    if explicit:
        return explicit
    script = (
        "Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'Game*' } | "
        "ForEach-Object { \"$($_.Id)`t$($_.Path)\" }"
    )
    out = subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True)
    rows = []
    for line in (out.stdout or "").splitlines():
        pid, _, path = line.partition("	")
        if pid.strip().isdigit() and "game.patched" in path.lower():
            rows.append((int(pid), path.strip()))
    if not rows:
        raise SystemExit("no Game.patched process: start the client with scripts/dev/live.ps1 -ClientOnly")
    if len(rows) > 1:
        print("several clients; pass --pid", file=sys.stderr)
        for pid, path in rows:
            print(f"  {pid}  {path}", file=sys.stderr)
        raise SystemExit(2)
    return rows[0][0]


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
    """Saves the client's client area to PNG every `interval` seconds."""

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
        self._last = 0.0
        self.started = time.monotonic()
        self._stop = False
        user32 = ctypes.windll.user32
        if user32.IsIconic(self.hwnd):  # WGC cannot capture an iconified window
            user32.ShowWindow(self.hwnd, 4)
            user32.SetWindowPos(self.hwnd, None, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0040)
        self.capture = WindowsCapture(cursor_capture=False, draw_border=False, window_hwnd=self.hwnd)

        @self.capture.event
        def on_frame_arrived(frame, control):
            if self._stop or time.monotonic() - self._last < self.interval:
                return
            self._last = time.monotonic()
            index = len(self.saved) + 1
            name = self.folder / f"frame-{index:04d}-{int((self._last - self.started) * 1000):06d}ms.png"
            frame.save_as_image(str(name))
            self.saved.append((self._last - self.started, name))
            if index >= 4_000:
                control.stop()

        @self.capture.event
        def on_closed():
            pass

        self.thread = threading.Thread(target=self.capture.start, daemon=True)
        self.thread.start()

    def stop(self) -> list[tuple[float, Path]]:
        self._stop = True
        try:
            self.capture.stop()
        except Exception:
            pass
        self.thread.join(timeout=5)
        return self.saved


def load(folder: Path) -> list[tuple[float, Path]]:
    """Frames already on disk, as (milliseconds, path), for a second look without capturing again."""
    found: list[tuple[float, Path]] = []
    for path in sorted(folder.glob("frame-*.png")):
        match = re.search(r"-(\d+)ms$", path.stem)
        found.append(((int(match.group(1)) / 1000) if match else 0.0, path))
    return found


def gold_share(path: Path) -> int:
    """Effect-colored pixels: the golden art of the Shaper's skills (R high, G high, B low)."""
    from PIL import Image

    image = Image.open(path).convert("RGB").resize((320, 200), Image.LANCZOS)
    pixels = image.load()
    return sum(1 for y in range(200) for x in range(320)
               if pixels[x, y][0] > 190 and pixels[x, y][1] > 170 and pixels[x, y][2] < 130)


def sheet(frames: list[tuple[float, Path]], out: Path, columns: int = 4, width: int = 640,
          crop: tuple[float, float, float, float] | None = None, labels: list[str] | None = None) -> Path | None:
    from PIL import Image, ImageDraw

    if not frames:
        return None
    tiles = []
    for _, path in frames:
        image = Image.open(path).convert("RGB")
        if crop:
            w, h = image.size
            image = image.crop((int(w * crop[0]), int(h * crop[1]), int(w * crop[2]), int(h * crop[3])))
        height = int(image.size[1] * width / image.size[0])
        tiles.append(image.resize((width, height), Image.LANCZOS))
    out.parent.mkdir(parents=True, exist_ok=True)
    rows = (len(tiles) + columns - 1) // columns
    canvas = Image.new("RGB", (columns * width, rows * tiles[0].size[1]), (0, 0, 0))
    draw = ImageDraw.Draw(canvas)
    for index, tile in enumerate(tiles):
        x, y = (index % columns) * width, (index // columns) * tile.size[1]
        canvas.paste(tile, (x, y))
        # The number is the frame's own, so a strip of picked frames still names the frames you asked for.
        caption = labels[index] if labels and index < len(labels) else f"#{index + 1}"
        draw.text((x + 6, y + 4), caption, fill=(255, 255, 0))
    canvas.save(out, quality=88)
    return out


def strip(frames: list[tuple[float, Path]], out: Path, picks: list[int], width: int = 440,
          crop: tuple[float, float, float, float] | None = None, columns: int = 4) -> Path | None:
    """Just the frames named (1-based), in order, for a strip that follows one effect."""
    chosen = [(index, frames[index - 1]) for index in picks if 1 <= index <= len(frames)]
    return sheet([frame for _, frame in chosen], out, columns=columns, width=width, crop=crop,
                 labels=[str(index) for index, _ in chosen])


def main() -> int:
    parser = argparse.ArgumentParser(description="Screenshots of the live Path of Exile client.")
    parser.add_argument("--pid", type=int, default=None, help="the client's pid (default: find it)")
    parser.add_argument("--seconds", type=float, default=15.0)
    parser.add_argument("--interval", type=float, default=0.1, help="seconds between frames")
    parser.add_argument("--out", type=Path, default=None, help="folder for the frames (default logs/shots/<time>)")
    parser.add_argument("--from", dest="source", type=Path, default=None,
                        help="a folder already captured: sheet/strip/gold it again without capturing")
    parser.add_argument("--gold", action="store_true", help="report effect-colored pixels per frame")
    parser.add_argument("--crop", default=None, help="crop for the sheet, as x0,y0,x1,y1 in 0..1")
    parser.add_argument("--strip", default=None, help="frame numbers for a strip, e.g. 40,44,48,52")
    parser.add_argument("--columns", type=int, default=4)
    parser.add_argument("--width", type=int, default=640)
    args = parser.parse_args()

    crop = None
    if args.crop:
        crop = tuple(float(part) for part in args.crop.split(","))
        if len(crop) != 4:
            raise SystemExit("--crop wants four fractions")

    if args.source:
        out = args.source
        saved = load(out)
        if not saved:
            raise SystemExit(f"no frame-*.png in {out}")
        print(f"{len(saved)} frames in {out}")
    else:
        out = args.out or (REPO / "logs" / "shots" / time.strftime("%Y%m%d-%H%M%S"))
        frames = Frames(client_pid(args.pid), out, args.interval)
        print(f"capturing {args.seconds:g} s into {out} ...")
        time.sleep(args.seconds)
        saved = frames.stop()
        print(f"{len(saved)} frames")

    if args.gold and saved:
        counts = [(index, gold_share(path)) for index, (_, path) in enumerate(saved, 1)]
        hot = [index for index, count in counts if count > 300]
        runs: list[str] = []
        start = None
        for index, _ in enumerate(saved, 1):
            value = dict(counts)[index]
            if value > 300 and start is None:
                start = index
            elif value <= 300 and start is not None:
                runs.append(f"{start}-{index - 1}")
                start = None
        if start:
            runs.append(f"{start}-{len(saved)}")
        print(f"effect-colored runs (frames > 300 px): {', '.join(runs) or 'none'}")
        print(f"peak: {max(counts, key=lambda item: item[1]) if counts else '-'}")

    page = sheet(saved, out / "sheet.jpg", columns=args.columns, width=args.width, crop=crop)
    if page:
        print(f"sheet {page}")
    if args.strip:
        picks = [int(part) for part in args.strip.split(",") if part.strip()]
        page = strip(saved, out / "strip.jpg", picks, width=args.width, crop=crop, columns=args.columns)
        if page:
            print(f"strip {page}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
