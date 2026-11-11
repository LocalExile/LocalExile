"""Read the portal objects a Gateway session created out of its log, decoded.

The Gateway logs every S→C 0x02bb (create object) and 0x008f (map device portals) as hex, so a session's portals can be
recovered after the fact without a Frida session. This is where the portal's own fields come from: for a MultiplexPortal
the object blob carries its place index, portal type and open flag, and each 0x008f entry carries the area it leads to,
the portal variation (+0x26, 0 = the default visual) and the owner name.

    python scripts/re/portal_capture.py                          # logs/live-gateway.log -> Tools/re/out/portals.json
    python scripts/re/portal_capture.py logs/other.log --out out.json

Layouts: Agents/docs/protocol/02-instance-connection.md (object header, CreateObject),
Agents/docs/protocol/21-hideouts.md (MapPortalObject, 0x008f).

The Gateway caps each logged hex dump at 96 bytes, so a six-portal 0x008f list and a long .ao path arrive cut off; the
capture decodes what fits, sets `truncated`, and keeps the raw hex. For an effect object use its `loggedAo` name from
the log line, which is the complete path.
"""
import argparse
import json
import re
import struct
import sys
from pathlib import Path

HEADER = 2 + 4 + 4 + 2 + 4 + 2   # id, object, u32, u16, object type, blob length (u16 BE)

CREATE_PORTAL = re.compile(
    r"0x02bb create map portal (0x[0-9a-f]+) \(place (\d+), (\d+)/(\d+)\) to (\S+) at \((\d+), (\d+)\): ([0-9A-F]+)")
CREATE_EFFECT = re.compile(
    r"0x02bb create effect (0x[0-9a-f]+) (\S+) at \((\d+), (\d+)\) \((.*)\): ([0-9A-F]+)")
DEVICE_PORTALS = re.compile(
    r"0x008f map device (0x[0-9a-f]+) portals: (\d+) open to (\S+): ([0-9A-F]+)")


def compact(b, i):
    """The client's compact uint (InstanceProtocol.CompactUInt): <0x80 one byte, then 0x80/0xc0/0xe0 prefixes."""
    first = b[i]
    if first < 0x80:
        return first, i + 1
    if first < 0xC0:
        return ((first & 0x3F) << 8) | b[i + 1], i + 2
    if first < 0xE0:
        return ((first & 0x1F) << 16) | (b[i + 1] << 8) | b[i + 2], i + 3
    if first < 0xF0:
        return ((first & 0x0F) << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3], i + 4
    return int.from_bytes(b[i + 1:i + 5], "big"), i + 5


def object_header(b):
    """The 0x02bb header and the blob it carries (LoginWriter.Blob: u16 length, then the bytes)."""
    length = struct.unpack_from(">H", b, 16)[0]
    return {
        "objectId": f"0x{struct.unpack_from('>I', b, 2)[0]:x}",
        "objectType": b[12:16].hex().upper(),
        "blobLength": length,
        "blob": b[HEADER:HEADER + length],
        "trailing": b[HEADER + length:].hex().upper(),
    }


def decode_map_portal(blob):
    """MapPortalObject.Blob: Positioned, StateMachine (standard_portal, portal, map_device_index, map_device_large),
    Animated, Portal (the multiplex type), Transitionable (open), MinimapIcon, InteractionAction, tasks."""
    out = {"flags": blob[0], "baseDataEntries": blob[1]}
    out["x"], out["y"], out["rotation"] = struct.unpack_from("<iif", blob, 2)
    out["team"] = struct.unpack_from("<H", blob, 14)[0]      # 2 flags + x, y, rotation
    out["f0"], out["f1"], out["u18"], out["shown"] = blob[16:20]
    out["stateMachineDefines"], out["stateMachineFlags"] = blob[20], blob[21]
    i = 22
    states = []
    for _ in range(out["stateMachineDefines"]):
        value, i = compact(blob, i)
        states.append({"value": value, "flags": blob[i]})
        i += 1
    out["states"] = states
    out["statesNamed"] = dict(zip(["standard_portal", "portal", "map_device_index", "map_device_large"],
                                  (s["value"] for s in states)))
    out["animated"] = blob[i]
    out["portalType"] = blob[i + 1]
    out["transitionable"] = blob[i + 2]     # 1 = open
    out["minimapIcon"] = blob[i + 3]
    out["interaction"] = blob[i + 4]
    out["tasks"] = blob[i + 5]
    out["consumed"] = i + 6
    out["truncated"] = i + 6 > len(blob)
    return out


def decode_effect(blob):
    """EffectObject.Blob: Positioned, then Animated bit0 + the .ao path (WStr32: u32 units, UTF-16LE)."""
    out = {"flags": blob[0], "baseDataEntries": blob[1]}
    out["x"], out["y"], out["rotation"] = struct.unpack_from("<iif", blob, 2)
    unit_count = struct.unpack_from("<I", blob, 21)[0]       # 2 flags + Positioned (18) + u8, u8 + the Animated bit
    end = 25 + unit_count * 2
    # The log caps a hex dump at 96 bytes, so a long .ao path arrives cut off: report the cut, not half a path (the log
    # line's own `loggedAo` text is complete).
    out["truncated"] = end > len(blob)
    out["animatedObject"] = None if out["truncated"] else blob[25:end].decode("utf-16-le")
    return out


def decode_device_portals(b):
    """InstanceMapDevice.Portals: u8 flags, u8, u8, u32 device, u16, u8, u8, u8 count, then per portal
    (u32 object, u16 area HASH16, u16 variation (+0x26), u8, u8, u8 style, str owner).
    The Gateway log caps each hex dump at 96 bytes, so a six-portal list arrives cut off: decode the whole entries and
    report the rest as truncated (the raw hex is kept in the capture)."""
    device = struct.unpack_from(">I", b, 5)[0]
    i = 9 + 2 + 1 + 1          # u16, u8, u8 before the count
    count = b[i]
    i += 1
    portals = []
    for _ in range(count):
        if i + 6 > len(b):
            break
        object_id = struct.unpack_from(">I", b, i)[0]
        area = struct.unpack_from(">H", b, i + 4)[0]
        i += 6
        entry = {"objectId": f"0x{object_id:x}", "areaHash16": f"0x{area:04x}", "variation": 0, "style": 0, "owner": ""}
        if area != 0:
            if i + 7 > len(b):
                break
            entry["variation"] = struct.unpack_from(">H", b, i)[0]
            entry["style"] = b[i + 4]
            length = struct.unpack_from(">H", b, i + 5)[0]
            if i + 7 + length > len(b):
                break
            entry["owner"] = b[i + 7:i + 7 + length].decode("utf-8", "replace")
            i += 7 + length
        portals.append(entry)
    return {"device": f"0x{device:x}", "count": count, "decoded": len(portals),
            "truncated": len(portals) < count, "portals": portals}


CONNECTION = re.compile(r"\[gateway\] (\d+\.\d+\.\d+\.\d+:\d+)")


def capture(path):
    portals, effects, devices, connections = [], [], [], []
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        connection = m.group(1) if (m := CONNECTION.search(line)) else ""
        if connection and connection not in connections:
            connections.append(connection)
        if (m := CREATE_PORTAL.search(line)):
            obj = object_header(bytes.fromhex(m.group(8)))
            entry = {"connection": connection, "objectId": m.group(1), "place": int(m.group(2)), "ordinal": int(m.group(3)),
                     "of": int(m.group(4)), "area": m.group(5),
                     "loggedPosition": {"x": int(m.group(6)), "y": int(m.group(7))},
                     "objectType": obj["objectType"], "trailing": obj["trailing"], "hex": m.group(8)}
            entry.update(decode_map_portal(obj["blob"]))
            portals.append(entry)
        elif (m := CREATE_EFFECT.search(line)):
            obj = object_header(bytes.fromhex(m.group(6)))
            entry = {"connection": connection, "objectId": m.group(1), "loggedAo": m.group(2),
                     "loggedPosition": {"x": int(m.group(3)), "y": int(m.group(4))}, "name": m.group(5),
                     "objectType": obj["objectType"], "trailing": obj["trailing"], "hex": m.group(6)}
            entry.update(decode_effect(obj["blob"]))
            effects.append(entry)
        elif (m := DEVICE_PORTALS.search(line)):
            entry = decode_device_portals(bytes.fromhex(m.group(4)))
            entry.update({"connection": connection, "loggedDevice": m.group(1), "loggedCount": int(m.group(2)), "area": m.group(3),
                          "hex": m.group(4)})
            devices.append(entry)
    return {"log": str(path), "connections": connections, "mapPortals": portals, "effectObjects": effects,
            "devicePortalLists": devices}


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("log", nargs="?", default="logs/live-gateway.log")
    ap.add_argument("--out", default="Tools/re/out/portals.json")
    args = ap.parse_args()
    path = Path(args.log)
    if not path.exists():
        sys.exit(f"no log at {path} (run the Gateway with scripts/dev/live.ps1 first)")

    data = capture(path)
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(data, indent=2), encoding="utf-8")

    print(f"{len(data['mapPortals'])} map portal(s), {len(data['effectObjects'])} effect object(s), "
          f"{len(data['devicePortalLists'])} portal list(s) -> {out}")
    # The log spans every connection of this Gateway run; print the one that just made the portals.
    latest = data["connections"][-1] if data["connections"] else None
    if latest and len(data["connections"]) > 1:
        print(f"latest connection {latest} (earlier connections stay in the JSON)")
    data["mapPortals"] = [p for p in data["mapPortals"] if not latest or p["connection"] == latest]
    data["effectObjects"] = [e for e in data["effectObjects"] if not latest or e["connection"] == latest]
    data["devicePortalLists"] = [d for d in data["devicePortalLists"] if not latest or d["connection"] == latest]
    for p in data["mapPortals"]:
        print(f"  {p['objectId']:>8}  place {p['place']}  {p['area']:<16} ({p['x']}, {p['y']})  "
              f"type {p['portalType']}  open {p['transitionable']}")
    for e in data["effectObjects"]:
        print(f"  {e['objectId']:>8}  effect {e['name']} at ({e['x']}, {e['y']})")
    for d in data["devicePortalLists"]:
        print(f"  device {d['device']} lists {d['count']} portal(s): " +
              ", ".join(f"{p['objectId']}->{p['areaHash16']} variation {p['variation']}" for p in d["portals"]))


if __name__ == "__main__":
    main()
