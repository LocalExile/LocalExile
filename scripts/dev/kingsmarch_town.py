"""Kingsmarch's town objects, for the Game module's Settlers hub (KingsmarchTown.cs).

The client draws a generated area's own level but not the hub's furniture: every Kingsmarch object inherits
`DecoUpgradeNoRender.ot`'s `BaseEvents { server_only = true }`, so the client's doodad pass drops it and the town renders
as floating furniture over grass. The objects the server has to create instead are the room's own `room_doodads` rows, so
this writes them out of client.db with their room-local cell and angle.

Writes data/kingsmarch-town.json:
    {"room": "<the .arm path>", "roomX": 138, "roomY": 138,
     "objects": [{"type": "<object type path>", "model": "<.ao path>", "x": 123, "y": 456, "angle": 0.0}, ...]}

`roomX`/`roomY` are where the room sits in the town's grid (the area capture `KalguuranSettlersLeague` puts
`kingsmarch.arm` at (138,138), 621x1035 cells), so a row's cell is `roomX + x`, `roomY + y`.

Rows without a `type` are plain client doodads the client already draws (bar stools, a cat, crates); they are left out.
Only the rows of this one room matter: the town is `Metadata/Terrain/Leagues/Settlers/HubTown/Rooms/kingsmarch.arm`.
"""

import json
import os
import sqlite3
import sys

ROOM = "Metadata/Terrain/Leagues/Settlers/HubTown/Rooms/kingsmarch.arm"
ROOM_X, ROOM_Y = 138, 138
OUT = os.path.join("data", "kingsmarch-town.json")


def main(argv):
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    db = os.path.join(root, "data", "client.db")
    if not os.path.exists(db):
        sys.exit(f"no {db}: run scripts/dev/client-sql.ps1 first (it builds client.db from sql/)")
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    try:
        row = con.execute("select _row from room_point_rooms where room = ?", (ROOM,)).fetchone()
        if row is None:
            sys.exit(f"{ROOM} is not in client.db's room_point_rooms: is the client's room data extracted?")
        rows = con.execute(
            "select type, model, x, y, angle from room_doodads where room = ? and type is not null and type <> '' "
            "order by _row", (row[0],)).fetchall()
    finally:
        con.close()
    if not rows:
        sys.exit(f"room_doodads has no typed rows for {ROOM}")
    objects = [{"type": t, "model": m, "x": int(x), "y": int(y), "angle": round(float(a or 0.0), 5)}
               for (t, m, x, y, a) in rows]
    out = os.path.join(root, OUT)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump({"room": ROOM, "roomX": ROOM_X, "roomY": ROOM_Y, "objects": objects}, f, ensure_ascii=False, indent=1)
        f.write("\n")
    print(f"kingsmarch-town: {len(objects)} object(s) of {ROOM} -> {OUT}")


if __name__ == "__main__":
    main(sys.argv[1:])
