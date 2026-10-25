"""Walk-map generator: captures the terrain walkability grid of every area your own client generates.

Usage (started for you by `scripts/dev/live.ps1 -Walkmaps`):
    python scripts/walkmaps/walkmaps.py [--process Game.patched.exe] [--out data/walkability]

Needs: pip install frida  (Python 3.9+). Attaches to the running client, waits for it if it is not running yet,
and writes data/walkability/<area>_seed<seed>.bin plus <area>_seed<seed>.json ({"w":..,"h":..}), and <area>_seed<seed>.projectile.bin (what stops projectiles), and the layout's area
transitions and waypoints (grid position, rotation, height, model) to <area>_seed<seed>.transitions.json, and every placed
doodad (model, grid cell, angle) to <area>_seed<seed>.doodads.json (for the rooms' monster points).
Rhoa Nests/Glyph Wall (research only) and tutorial tile objects are recorded in <area>_seed<seed>.objects.json.
Placed .arm rooms are recorded in <area>_seed<seed>.rooms.json; the Strand support-gem chest uses its named room point. The area and
seed come from data/walkability/current.json, which GameServer.Gateway writes on every connection. Existing walk maps are
kept.
Nothing is sent anywhere; the files stay on your machine (data/ is git-ignored).
"""

import argparse
import json
import os
import sys
import threading
import time

try:
    import frida
except ImportError:
    sys.exit("walkmaps: the frida package is missing. Install it with:  python -m pip install frida")

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))


def log(text):
    print(f"[walkmaps] {text}", flush=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--process", default="Game.patched.exe", help="client process name")
    ap.add_argument("--out", default=os.path.join(REPO, "data", "walkability"), help="output folder")
    ap.add_argument("--wait", type=int, default=600, help="seconds to wait for the client to start")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    session = attach(args.process, args.wait)
    with open(os.path.join(HERE, "walkmaps.js"), encoding="utf-8") as f:
        script = session.create_script(f.read())

    done = threading.Event()
    script.on("message", lambda message, data: on_message(message, data, args.out))
    session.on("detached", lambda reason, *_: (log(f"client gone ({reason})"), done.set()))
    script.load()
    log(f"attached to {args.process}; writing to {args.out}")
    try:
        while not done.wait(1):
            pass
    except KeyboardInterrupt:
        pass


def attach(name, wait):
    deadline = time.time() + wait
    announced = False
    while True:
        try:
            return frida.attach(name)
        except frida.ProcessNotFoundError:
            if time.time() > deadline:
                sys.exit(f"walkmaps: {name} did not start within {wait} s")
            if not announced:
                log(f"waiting for {name} ...")
                announced = True
            time.sleep(1)
        except frida.PermissionDeniedError:
            sys.exit("walkmaps: permission denied attaching to the client (run both at the same privilege level)")


def on_message(message, data, out):
    if message.get("type") == "error":
        log(f"script error: {message.get('description') or message}")
        return
    payload = message.get("payload") or {}
    kind = payload.get("type")
    if kind == "ready":
        log(f"hook installed in {payload.get('module')}")
    elif kind == "refused":
        log(f"not hooking: {payload.get('reason')}")
    elif kind == "skip":
        log(f"ignored a grid with unexpected size {payload}")
    elif kind == "error":
        log(f"hook error: {payload.get('message')}")
    elif kind == "grid" and data is not None:
        save(out, payload["w"], payload["h"], data)
    elif kind == "projectile" and data is not None:
        save_projectile(out, payload["w"], payload["h"], data)
    elif kind == "object":
        save_object(out, payload)
    elif kind == "quest-object":
        save_quest_object(out, payload)
    elif kind == "doodads":
        save_doodads(out, payload.get("doodads") or [])
    elif kind == "room":
        save_room(out, payload)


USED = set()  # current.json nonces that already got their grid
FRESH_SECONDS = 120


def save(out, w, h, cells):
    area = current_area(out)
    if area is None:
        log(f"got a {w}x{h} grid, but no data/walkability/current.json from the Gateway; not saved")
        return
    # One grid per area load: the first grid after the Gateway announced the area, and only while that is recent.
    if area.get("nonce") in USED or time.time() - float(area.get("written", 0)) > FRESH_SECONDS:
        log(f"got a {w}x{h} grid that does not belong to a fresh area load; not saved")
        return
    USED.add(area.get("nonce"))
    stem = os.path.join(out, f"{area['id']}_seed{area['seed']}")
    if os.path.exists(stem + ".bin"):
        log(f"{os.path.basename(stem)}.bin already exists; kept")
        return
    with open(stem + ".bin.tmp", "wb") as f:
        f.write(cells)
    os.replace(stem + ".bin.tmp", stem + ".bin")
    with open(stem + ".json", "w", encoding="utf-8") as f:
        json.dump({"w": w, "h": h}, f)
    log(f"saved {os.path.basename(stem)}.bin ({w}x{h})")


def save_projectile(out, w, h, cells):
    """The projectile grid sent right after the walk map of the same load: <area>_seed<seed>.projectile.bin."""
    area = current_area(out)
    if area is None or time.time() - float(area.get("written", 0)) > FRESH_SECONDS:
        log(f"got a {w}x{h} projectile grid outside a fresh area load; not saved")
        return
    stem = os.path.join(out, f"{area['id']}_seed{area['seed']}")
    if os.path.exists(stem + ".projectile.bin"):
        return
    with open(stem + ".projectile.bin.tmp", "wb") as f:
        f.write(cells)
    os.replace(stem + ".projectile.bin.tmp", stem + ".projectile.bin")
    log(f"saved {os.path.basename(stem)}.projectile.bin ({w}x{h})")


OBJECTS = {}  # current.json nonce -> the tile objects reported for that area load


def save_object(out, obj):
    """Adds one area transition / waypoint of the area being generated to <area>_seed<seed>.transitions.json."""
    area = current_area(out)
    if area is None or time.time() - float(area.get("written", 0)) > FRESH_SECONDS:
        log(f"got a tile object ({obj.get('objectType')}) outside a fresh area load; not saved")
        return
    found = OBJECTS.setdefault(area.get("nonce"), [])
    found.append({k: obj.get(k) for k in ("objectType", "model", "x", "y", "rotation", "z")})
    stem = os.path.join(out, f"{area['id']}_seed{area['seed']}")
    with open(stem + ".transitions.json.tmp", "w", encoding="utf-8") as f:
        json.dump({"area": area["id"], "seed": area["seed"], "objects": found}, f, indent=1)
    os.replace(stem + ".transitions.json.tmp", stem + ".transitions.json")
    log(f"{os.path.basename(stem)}: {obj.get('objectType', '').rsplit('/', 1)[-1]} at ({obj.get('x')}, {obj.get('y')}) "
        f"z {obj.get('z')} (raw {obj.get('rawZ')}, height offset {obj.get('heightOffset')})")


QUEST_OBJECTS = {}  # Only the latest fresh (area, seed, nonce), separate from navigable transitions.
QUEST_TYPES = {"Metadata/Chests/QuestChests/RhoaChest" + suffix for suffix in ("", "2", "3")} | {
    "Metadata/QuestObjects/WaterCave/GlyphWall", "Metadata/Chests/TutorialSupportGemChest"}


def save_quest_object(out, obj):
    """Optional a1q4 tile placements; never add a chest/wall to the area's transition list."""
    if obj.get("objectType") not in QUEST_TYPES:
        return
    area = current_area(out)
    if area is None or area.get("nonce") is None or time.time() - float(area.get("written", 0)) > FRESH_SECONDS:
        return
    key = (area["id"], area["seed"], area["nonce"])
    if key not in QUEST_OBJECTS:
        QUEST_OBJECTS.clear()
        QUEST_OBJECTS[key] = []
    found = QUEST_OBJECTS[key]
    found.append({k: obj.get(k) for k in ("objectType", "model", "x", "y", "rotation", "z", "rawZ", "heightOffset", "code")})
    stem = os.path.join(out, f"{area['id']}_seed{area['seed']}")
    with open(stem + ".objects.json.tmp", "w", encoding="utf-8") as f:
        json.dump({"area": area["id"], "seed": area["seed"], "objects": found}, f, indent=1)
    os.replace(stem + ".objects.json.tmp", stem + ".objects.json")
    log(f"{os.path.basename(stem)}: quest object {obj['objectType'].rsplit('/', 1)[-1]} at ({obj.get('x')}, {obj.get('y')})")


DOODADS = {}  # current.json nonce -> [model, x, y, angle] of every doodad placed in that area load
ROOMS = {}


def save_room(out, obj):
    area = current_area(out)
    if area is None or time.time()-float(area.get("written",0))>FRESH_SECONDS:
        return
    if not str(obj.get("path", "")).lower().endswith(".arm") or not 0<=obj.get("code",-1)<=7:
        return
    key=(area["id"],area["seed"],area.get("nonce"))
    if key not in ROOMS:
        ROOMS.clear()
        ROOMS[key]=[]
    found=ROOMS[key]
    value={k:obj[k] for k in ("path","x","y","width","height","code")}
    if value not in found:
        found.append(value)
    stem=os.path.join(out,f"{area['id']}_seed{area['seed']}")
    with open(stem+".rooms.json.tmp","w",encoding="utf-8") as f:
        json.dump({"area":area["id"],"seed":area["seed"],"rooms":found},f)
    os.replace(stem+".rooms.json.tmp",stem+".rooms.json")


def save_doodads(out, batch):
    """Adds placed doodads of the area being generated to <area>_seed<seed>.doodads.json (models listed once)."""
    area = current_area(out)
    if area is None or time.time() - float(area.get("written", 0)) > FRESH_SECONDS:
        return
    found = DOODADS.setdefault(area.get("nonce"), [])
    found.extend(batch)
    models = sorted({d[0] for d in found})
    index = {m: i for i, m in enumerate(models)}
    stem = os.path.join(out, f"{area['id']}_seed{area['seed']}")
    with open(stem + ".doodads.json.tmp", "w", encoding="utf-8") as f:
        json.dump({"area": area["id"], "seed": area["seed"], "models": models,
                   "doodads": [[index[d[0]], d[1], d[2], round(d[3], 4)] for d in found]}, f)
    os.replace(stem + ".doodads.json.tmp", stem + ".doodads.json")
    if len(found) == len(batch) or len(found) // 1000 != (len(found) - len(batch)) // 1000:
        log(f"{os.path.basename(stem)}: {len(found)} doodads")


def current_area(out):
    path = os.path.join(out, "current.json")
    try:
        with open(path, encoding="utf-8") as f:
            area = json.load(f)
        return area if "id" in area and "seed" in area else None
    except (OSError, ValueError):
        return None


if __name__ == "__main__":
    main()
