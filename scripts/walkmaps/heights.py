"""Terrain height grid from a placed-tile capture (scripts/re/frida/capture-terrain-tiles.js).

Usage: python scripts/walkmaps/heights.py <tiles_N.json> <area id> <seed>
Writes data/walkability/<area>_seed<seed>.heights.bin (int16 per cell, row by row) and .heights.json {w, h}, which
GameServer.Gateway's TerrainHeights reads. The lookup is the client's 0x141d84b00 / 0x141d7d100 / 0x141d8fc40
(Agents/docs/protocol/11-skills.md §10): the subtile's palette-packed 23 x 23 int8 heights at the tile-local cell
under the entry's code, + terrain multiplier x the entry's height offset.
"""

import json
import os
import runpy
import struct
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
RUNTIME = str(runpy.run_path(os.path.join(REPO, "scripts", "dev", "client_paths.py"))["runtime_directory"](REPO))
# Client tables (3.29.3b): 0x142fa04b0 (9 x 3 bytes, the code's axis choice) and 0x142fb7f98 (code byte -> row).
CODES = bytes.fromhex("000101010001000000010100000001010000000100010101000101")
CLAMP = bytes.fromhex("00030201040506070800000000000000")
UNKNOWN = -32768


def cell(vec, i):
    """0x141d8fc40: a subtile's height at index i of the 23 x 23 grid."""
    n = len(vec)
    if n == 0:
        return 0
    if n == 1:
        r = vec[0]
    elif n == 0x45:
        r = vec[vec[(i >> 3) + 2] >> (i & 7) & 1]
    elif n == 0x89:
        r = vec[vec[(i >> 2) + 4] >> ((i & 3) * 2) & 3]
    elif n == 0x119:
        r = vec[vec[(i >> 1) + 0x10] >> ((i & 1) << 2) & 0xF]
    else:
        r = vec[i]
    return r - 256 if r > 127 else r


def grid(capture):
    w, h, mult = capture["w"], capture["h"], capture["mult"]
    vecs = [bytes.fromhex(v) for v in capture["vecs"]]
    W, H = w * 23, h * 23
    out = [UNKNOWN] * (W * H)
    for i, (v, code, offset) in enumerate(capture["entries"]):
        if v < 0:
            continue
        tx, ty = i % w, i // w
        c = min(CLAMP[code] if code < len(CLAMP) else 8, 8)
        t = CODES[c * 3:c * 3 + 3]
        vec, base = vecs[v], mult * offset
        for y in range(23):
            for x in range(23):
                a = (22 - x, x, 22 - y, y)
                u1 = a[t[1] + 2 * t[0]]
                u2 = a[(2 if t[0] == 0 else 0) + t[2]]
                out[(ty * 23 + y) * W + tx * 23 + x] = cell(vec, u2 * 23 + u1) + base
    return W, H, out


def main():
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    with open(sys.argv[1], encoding="utf-8") as f:
        W, H, cells = grid(json.load(f))
    stem = os.path.join(RUNTIME, "data", "walkability", f"{sys.argv[2]}_seed{sys.argv[3]}")
    with open(stem + ".heights.bin", "wb") as f:
        f.write(struct.pack(f"<{len(cells)}h", *cells))
    with open(stem + ".heights.json", "w", encoding="utf-8") as f:
        json.dump({"w": W, "h": H}, f)
    known = [c for c in cells if c != UNKNOWN]
    print(f"{os.path.basename(stem)}.heights.bin: {W}x{H}, {len(known)} known cells, {min(known)}..{max(known)}")


if __name__ == "__main__":
    main()
