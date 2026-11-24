"""Bakes the Kingsmarch room's doodad hulls into blocked walk cells for the server.

The town's floor and buildings are client doodads: the client drops them in this area (their type inherits
BaseEvents{server_only=true}), so the walk grid captured from the client has no hulls in it and every wall, barrel,
bench and cart can be walked through. The client does its own walking, so the server's job is to clip a click's
destination: this writes the cells the module refuses to walk into, as [y, x0, x1) runs in area cells.

Each row's .ao is read through the GGPK (scripts/re/ot_components.py's cache, Tools/re/out/ot) or, for a quick rerun,
from --ao, a directory of dumped .ao texts named with their slashes replaced by underscores. A Hull's walk_tri /
walk_circle, turned by the row's angle, is the same shape the client stamps into its own walk grid. A row whose .ao has
no Hull but whose .ot is blocking with an object_size gets that square instead.

    python scripts/dev/kingsmarch_collision.py
    python scripts/dev/kingsmarch_collision.py --ao D:/scratch/ao-out
"""

import argparse
import json
import math
import os
import re
import sys

# Rows whose .ao carries no Hull, but whose type is blocking with this object_size (see data/object-types.json).
SQUARES = {"Stash": 3, "GuildStash": 3, "MapDeviceButton": 4, "FarmingPlot": 2, "FarmingPlotInteractable": 2}

TOWN_NPCS = os.path.join("src", "GameServer.Gateway", "NPC", "TownNpcs.cs")
ROOM = "Metadata/Terrain/Leagues/Settlers/HubTown/Rooms/kingsmarch.arm"
DEPTH = 17


def cache_name(directory, path):
    name = os.path.join(directory, path.replace("/", "_"))
    return "\\\\?\\" + os.path.abspath(name) if os.name == "nt" else name


def plain_read(path):
    with open(path, "rb") as f:
        raw = f.read()
    for encoding in ("utf-16", "utf-8", "latin-1"):
        try:
            text = raw.decode(encoding)
        except UnicodeDecodeError:
            continue
        if "walk_" in text or "Hull" in text or "extends" in text:
            return text
    return raw.decode("latin-1")


def parse_hull(text):
    """The walk triangles and circles of the first Hull block, or None when the text has none."""
    lines = [l.strip() for l in text.splitlines()]
    try:
        at = lines.index("Hull")
    except ValueError:
        return None
    tris, circles, depth = [], [], 0
    for line in lines[at + 1:]:
        if line == "{":
            depth += 1
            continue
        if line == "}":
            depth -= 1
            if depth <= 0:
                break
            continue
        if "=" not in line:
            continue
        key, _, value = line.partition("=")
        key = key.strip()
        value = value.strip().strip('"')
        try:
            n = [float(v) for v in value.split()]
        except ValueError:
            continue
        if key == "walk_tri" and len(n) >= 6:
            tris.append(n[:6])
        elif key == "walk_circle" and len(n) >= 3:
            circles.append(n[:3])
    return tris, circles


def parent_of(text):
    """The .ao a file extends, or None (DoodadHull.Load follows these while a file has no Hull)."""
    for line in text.splitlines():
        line = line.strip()
        if line.startswith("extends "):
            parent = line.split(None, 1)[1].strip().strip('"')
            if not parent or parent == "nothing":
                return None
            return parent if parent.endswith(".ao") else parent + ".ao"
    return None


def hulls_of(models, read_text, fetch):
    """The Hull of each model, following `extends` (batched: one GGPK dump per level of the chain)."""
    texts, wanted = {}, list(dict.fromkeys(models))
    for _ in range(DEPTH):
        if not wanted:
            break
        if fetch is not None:
            fetch(wanted)
        nxt = []
        for path in wanted:
            text = read_text(path)
            texts[path] = text
            parent = parent_of(text) if text else None
            if parent and parent not in texts:
                nxt.append(parent)
        wanted = nxt

    out = {}
    for model in models:
        path, hops = model, 0
        while path and path not in out and hops < DEPTH:
            text = texts.get(path)
            hull = parse_hull(text) if text else None
            if hull is not None:
                out[model] = hull
                break
            path = parent_of(text) if text else None
            hops += 1
        out.setdefault(model, None)
    return out


def inside(p, x, y):
    def side(a, b):
        return (b[0] - a[0]) * (y - a[1]) - (b[1] - a[1]) * (x - a[0])

    d1, d2, d3 = side(p[0], p[1]), side(p[1], p[2]), side(p[2], p[0])
    neg = d1 < 0 or d2 < 0 or d3 < 0
    pos = d1 > 0 or d2 > 0 or d3 > 0
    return not (neg and pos)


def cells(tris, circles, angle):
    """The cells the walk shapes cover, relative to the doodad's own cell."""
    c, s = math.cos(angle), math.sin(angle)

    def turn(x, y):
        return (x * c - y * s, x * s + y * c)

    out = set()
    # A cell counts as blocked when the hull covers any part of it, not just its centre: the town's lamp posts, benches
    # and carts have hulls thinner than a cell, and sampling only the centre let them fall between the samples, so the
    # grid came out with holes a player could walk through (live 2026-11-21).
    probes = [(ox * 0.3, oy * 0.3) for ox in (-1, 0, 1) for oy in (-1, 0, 1)]
    for t in tris:
        p = [turn(t[0], t[1]), turn(t[2], t[3]), turn(t[4], t[5])]
        xs = [q[0] for q in p]
        ys = [q[1] for q in p]
        for y in range(int(math.floor(min(ys))), int(math.ceil(max(ys))) + 1):
            for x in range(int(math.floor(min(xs))), int(math.ceil(max(xs))) + 1):
                if any(inside(p, x + px, y + py) for (px, py) in probes):
                    out.add((x, y))
    for (cx, cy, r) in circles:
        px, py = turn(cx, cy)
        ox, oy = int(math.floor(px + 0.5)), int(math.floor(py + 0.5))
        extent = int(math.floor(r + 0.5))
        squared = int(math.floor(r * r + 0.5))
        for dy in range(-extent, extent + 1):
            for dx in range(-extent, extent + 1):
                if dx * dx + dy * dy < squared:
                    out.add((ox + dx, oy + dy))
    return out


def runs(cell_set):
    """Row runs of a cell set: [[y, x0, x1], ...], x1 exclusive."""
    out = []
    for y in sorted({c[1] for c in cell_set}):
        xs = sorted(x for (x, yy) in cell_set if yy == y)
        start = prev = xs[0]
        for x in xs[1:]:
            if x == prev + 1:
                prev = x
                continue
            out.append([y, start, prev + 1])
            start = prev = x
        out.append([y, start, prev + 1])
    return out


def town_npcs(table):
    """The hub's villagers and their cells in the area's grid, from the Gateway's own room table.

    TownNpcs.cs gives room-local cells (the same ones kingsmarch.arm names), so the room's origin is added here; a
    villager the room has no line for is not in the table and so gets no footprint (the room does not place her).
    """
    try:
        text = open(table, encoding="utf-8-sig").read()
    except OSError as error:
        print(f"no villager footprints: {table}: {error}", file=sys.stderr)
        return []
    origin = re.search(r'new\("' + re.escape(ROOM) + r'",\s*-?\d+,\s*-?\d+,\s*-?[\d.]+f?,\s*(-?\d+),\s*(-?\d+)\)', text)
    if not origin:
        print(f"no villager footprints: {table} has no line for {ROOM}", file=sys.stderr)
        return []
    ox, oy = int(origin.group(1)), int(origin.group(2))
    found = re.findall(r'new\(Npc \+ "League/Kalguur/(Village\w+)",\s*(-?\d+),\s*(-?\d+)', text)
    return [(name, ox + int(x), oy + int(y)) for name, x, y in found]


def main():
    repo = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
    ap = argparse.ArgumentParser()
    ap.add_argument("--town", default=os.path.join(repo, "data", "kingsmarch-town.json"))
    ap.add_argument("--ao", default=None,
                    help="a directory of dumped .ao texts; without it the .ao files are read through the GGPK cache")
    ap.add_argument("--npcs", default=os.path.join(repo, TOWN_NPCS))
    ap.add_argument("--out", default=os.path.join(repo, "data", "doodad-collision", "KalguuranSettlersLeague.json"))
    args = ap.parse_args()

    if args.ao:
        directory = os.path.abspath(args.ao)
        if not os.path.isdir(directory):
            print(f"{directory} is not a directory of dumped .ao texts", file=sys.stderr)
            return 2

        def read_text(path):
            file = cache_name(directory, path)
            return plain_read(file) if os.path.exists(file) else None

        fetch = None
    else:
        sys.path.insert(0, os.path.join(repo, "scripts", "re"))
        import ot_components  # noqa: E402  (scripts/re/ot_components.py: the GGPK dump and its Tools/re/out/ot cache)

        # ggdump opens its first argument as a file, so it wants the .ggpk itself and not the client folder.
        ggpk = os.path.join(repo, "Client", "Content.ggpk")
        if os.path.exists(ggpk):
            ot_components.GGPK = ggpk

        def read_text(path):
            return ot_components.read_text(path)

        def fetch(paths):
            missing = [p for p in paths if ot_components.read_text(p) is None
                       and not os.path.exists(ot_components.cache_name(p) + ".missing")]
            if missing:
                ot_components.fetch(missing)

    with open(args.town, encoding="utf-8-sig") as f:
        town = json.load(f)
    rows = town["objects"]
    ox, oy = int(town.get("roomX") or 0), int(town.get("roomY") or 0)
    hulls = hulls_of([row.get("model") or "" for row in rows], read_text, fetch)
    objects, total, cells_total = [], 0, 0
    for index, row in enumerate(rows):
        model = row.get("model") or ""
        hull = hulls.get(model)
        name = (row.get("type") or "").split("/")[-1]
        if not hull or (not hull[0] and not hull[1]):
            side = SQUARES.get(name)
            if not side:
                continue
            half = max(1, side // 2)
            shape = [(cx, cy) for cy in range(-half, half + 1) for cx in range(-half, half + 1)]
        else:
            shape = cells(hull[0], hull[1], float(row.get("angle") or 0.0))
        if not shape:
            continue
        cells_total += len(shape)
        # The room_doodads rows are room-local; the room itself sits at (roomX, roomY) in the area's grid.
        rx, ry = ox + int(row["x"]), oy + int(row["y"])
        spaced = [[ry + dy, rx + a, rx + b] for (dy, a, b) in runs(shape)]
        total += len(spaced)
        objects.append({"i": index, "model": model, "runs": spaced})

    # The villagers are server objects, not room doodads, so they are not in the rows above. The client's own collision
    # stops a walker at them, but the server grid does not know them, so a click past one is answered with a move the
    # client cannot finish and the character bumps in place.
    npcs = town_npcs(args.npcs)
    for n, (name, nx, ny) in enumerate(npcs):
        objects.append({"i": -1 - n, "model": f"npc:{name}", "runs": [[ny + dy, nx - 2, nx + 3] for dy in range(-2, 3)]})

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump({"note": "blocked walk cells of the town's room doodads and villagers, as [y, x0, x1) runs in area cells "
                           "(scripts/dev/kingsmarch_collision.py)", "objects": objects}, f)
    with_hull = sum(1 for h in hulls.values() if h and (h[0] or h[1]))
    print(f"{len(rows)} rows, {len(hulls)} models ({with_hull} with a walk hull), {len(npcs)} villagers, "
          f"{len(objects)} objects, {cells_total} cells in {total} runs -> {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())