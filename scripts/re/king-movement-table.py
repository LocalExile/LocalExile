#!/usr/bin/env python3
"""Build the King in the Mists movement table from a Gateway log and a Frida position trace.

Usage:
    python scripts/re/king-movement-table.py \
        logs/live-gateway.log logs/frida-king-position-2026-11-01c.jsonl

Each row pairs one `0x01ab King in the Mists strafes/dashes backward` action with the next
`0x01ba ... settle at (x, y)` within 1.6 s and the client cells between them from the Frida trace.
`server start` is the boss cell carried by the action, `settle target` the 0x1ba cell, `client clip`
the first/last non-settle cell (`total != 0.1`), `correction` the distance the settle moved the model.

This is the extractor used for the King's movement table in the encounter pull request. It is read-only: the logs are
not modified. Rows without Frida cells are omitted (the trace can lose the actor's Positioned pointer
when a handler passes a temporary message object; see the doc).
"""
import datetime
import json
import math
import re
import sys


def seconds(stamp: str) -> float:
    hour, minute, rest = stamp.split(":")
    return int(hour) * 3600 + int(minute) * 60 + float(rest)


def clock(value: float) -> str:
    return (datetime.datetime.min + datetime.timedelta(seconds=value)).strftime("%H:%M:%S.%f")[:-3]


def compact(data: bytes, index: int):
    """The 0x1ab compact integer: <0x80 one byte, 0x80+14 bits, 0xc0+21 bits, 0xe0+28 bits, 0xf0+u32."""
    first = data[index]
    if first < 0x80:
        return first, index + 1
    if first < 0xC0:
        return ((first & 0x3F) << 8) | data[index + 1], index + 2
    if first < 0xE0:
        return ((first & 0x1F) << 16) | (data[index + 1] << 8) | data[index + 2], index + 3
    if first < 0xF0:
        return ((first & 0x0F) << 24) | (data[index + 1] << 16) | (data[index + 2] << 8) | data[index + 3], index + 4
    return int.from_bytes(bytes(data[index + 1:index + 5]), "big"), index + 5


def cells_from(path: str):
    cells = []
    with open(path, encoding="utf-8", errors="ignore") as handle:
        for line in handle:
            try:
                message = json.loads(line)
            except ValueError:
                continue
            payload = message.get("payload") or {}
            if payload.get("kind") != "cell":
                continue
            when = datetime.datetime.fromtimestamp(payload["ts"] / 1000)
            cells.append((when.hour * 3600 + when.minute * 60 + when.second + when.microsecond / 1e6,
                          tuple(payload["cell"]), payload.get("total", 0)))
    cells.sort()
    return cells


def main(gateway: str, trace: str) -> int:
    cells = cells_from(trace)
    actions, settles = [], []
    with open(gateway, encoding="utf-8", errors="ignore") as handle:
        for line in handle:
            if "King in the Mists" not in line:
                continue
            match = re.match(r"(\d\d:\d\d:\d\d\.\d+)", line)
            if not match:
                continue
            when = seconds(match.group(1))
            text = line.rstrip()
            dash = ("0x01ab King in the Mists strafes" in text
                    or "0x01ab King in the Mists dashes backward" in text)
            if dash:
                blob = text.rsplit(": ", 1)[-1].strip()
                try:
                    data = bytes.fromhex(blob)
                except ValueError:
                    continue
                flags = int.from_bytes(data[12:14], "big")
                index = 14
                if flags & 0x80:
                    index += 4
                player_x, index = compact(data, index)
                player_y, index = compact(data, index)
                boss_x, index = compact(data, index)
                boss_y, index = compact(data, index)
                skill = int.from_bytes(data[index:index + 2], "big")
                label = ("strafe-right" if "strafes right" in text
                         else "strafe-left" if "strafes left" in text else "dash-back")
                actions.append((when, label, skill, (player_x, player_y), (boss_x, boss_y)))
            elif "0x01ba King in the Mists" in text and "settle at" in text:
                settle = re.search(r"settle at \((\d+), (\d+)\)", text)
                if settle:
                    settles.append((when, (int(settle.group(1)), int(settle.group(2)))))

    print("| action | skill | server start | settle target | client clip | moved | correction |")
    print("|---|---|---|---|---|---|---|")
    for when, label, skill, player, boss in actions:
        settle = next((s for s in settles if s[0] >= when and s[0] - when < 1.6), None)
        if settle is None:
            continue
        clip = [c for c in cells if when - 0.05 <= c[0] < settle[0] and abs(c[2] - 0.1) > 0.01]
        if not clip:
            continue
        start, end = clip[0][1], clip[-1][1]
        moved = math.dist(start, end)
        correction = math.dist(end, settle[1])
        print("| %s (%s) | `0x%04x` | %s | %s | %s -> %s | %.1f | **%.1f** |" % (
            clock(when), label, skill, boss, settle[1], start, end, moved, correction))
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__)
        raise SystemExit(2)
    raise SystemExit(main(sys.argv[1], sys.argv[2]))
