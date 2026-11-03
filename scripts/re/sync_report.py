"""How far the server's position estimates are from where the client really has each object.

Reads the client's samples (scripts/re/frida/trace-sync.js, JSON lines from run.py or sync_drive.py) and the Gateway's
[sync] lines (Game_TRACE=sync, logs/live-gateway.log), both on the machine's QPC clock, and prints the error per object
class, in grid cells, plus the events that move the player on the client:

    python scripts/re/sync_report.py logs/sync-client.jsonl logs/live-gateway.log [--from Q] [--to Q] [--json]
"""
import argparse
import json
import math
import re
import sys

PLAYER = 0x1000
SYNC = re.compile(r"\[sync\] qpc=(\d+) peer=(\S+) speed=(\S+) moving=(\d) p=(-?\d+),(-?\d+) m=(\S*)")


def percentile(values, share):
    if not values:
        return None
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int(share * len(ordered)))]


def summary(values):
    return {"n": len(values), "p50": percentile(values, .5), "p90": percentile(values, .9), "p99": percentile(values, .99),
            "max": max(values) if values else None}


def read_server(path, start, end):
    samples = {}   # peer: [(qpc, {id: (x, y)}, moving)]
    with open(path, encoding="utf-8", errors="replace") as lines:
        for line in lines:
            match = SYNC.search(line)
            if not match:
                continue
            q = int(match[1])
            if start and q < start or end and q > end:
                continue
            cells = {PLAYER: (int(match[5]), int(match[6]))}
            for item in match[7].split(";"):
                if item:
                    key, pair = item.split(":")
                    x, y = pair.split(",")
                    cells[int(key, 16)] = (int(x), int(y))
            samples.setdefault(match[2], []).append((q, cells, int(match[4])))
    # An area change is a new connection: the one with the most samples in the window is the run's.
    return max(samples.values(), key=len, default=[])


def read_client(path, start, end):
    frequency, samples, events = None, [], []
    with open(path, encoding="utf-8", errors="replace") as lines:
        for line in lines:
            line = line.strip()
            if not line.startswith("{"):
                continue
            record = json.loads(line)
            if "qpf" in record:
                frequency = int(record["qpf"])
            if "q" not in record:
                continue
            q = int(record["q"])
            if start and q < start or end and q > end:
                continue
            (samples if "s" in record else events).append((q, record))
    return frequency, samples, events


def nearest(samples, q, hint):
    while hint + 1 < len(samples) and abs(samples[hint + 1][0] - q) <= abs(samples[hint][0] - q):
        hint += 1
    return hint


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("client")
    parser.add_argument("gateway")
    parser.add_argument("--from", dest="start", type=int, default=0)
    parser.add_argument("--to", dest="end", type=int, default=0)
    parser.add_argument("--json", action="store_true")
    options = parser.parse_args()

    frequency, client, events = read_client(options.client, options.start, options.end)
    if client:
        server = read_server(options.gateway, client[0][0], client[-1][0])
    else:
        server = []
    if not server or not client or not frequency:
        sys.exit(f"nothing to compare: {len(server)} server samples, {len(client)} client samples, qpf {frequency}")

    player, monsters, moving_monsters, standing_monsters = [], [], [], []
    worst = {}
    hint = 0
    limit = frequency * 0.06
    for q, record in client:
        hint = nearest(server, q, hint)
        at, cells, _ = server[hint]
        if abs(at - q) > limit:
            continue
        here = None
        for object_id, x, y, moving in record["s"]:
            if object_id == PLAYER:
                here = (x, y)
            if object_id not in cells:
                continue
            sx, sy = cells[object_id]
            error = math.hypot(x - sx, y - sy)
            if error > 500:   # a stale pointer in the client's samples, not a position
                continue
            if object_id == PLAYER:
                player.append(error)
            else:
                monsters.append(error)
                (moving_monsters if moving else standing_monsters).append(error)
                worst[object_id] = max(worst.get(object_id, 0), error)

    echoes = [record for _, record in events if "echo" in record]
    teleports = [record for _, record in events if "tp" in record]
    snaps = [record for record in echoes if record["d"] >= 92]
    result = {
        "seconds": (client[-1][0] - client[0][0]) / frequency,
        "player": summary(player),
        "monsters": summary(monsters),
        "monsters_moving": summary(moving_monsters),
        "monsters_standing": summary(standing_monsters),
        "monsters_seen": len(worst),
        "uses": sum(1 for _, record in events if "use" in record),
        "echoes": len(echoes),
        "echo_error": summary([record["d"] for record in echoes]),
        "echoes_over_92_cells": len(snaps),
        "player_teleports": len(teleports),
        "teleport_jump": summary([math.hypot(r["tp"][0] - r["client"][0], r["tp"][1] - r["client"][1]) for r in teleports]),
    }
    if options.json:
        print(json.dumps(result))
        return
    for key, value in result.items():
        if isinstance(value, dict):
            cells = "  ".join(f"{k} {v:.2f}" if isinstance(v, float) else f"{k} {v}" for k, v in value.items())
            print(f"{key:22} {cells}")
        else:
            print(f"{key:22} {value:.1f}" if isinstance(value, float) else f"{key:22} {value}")


if __name__ == "__main__":
    main()
