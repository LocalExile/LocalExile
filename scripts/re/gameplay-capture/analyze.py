"""Offline, bounded gameplay-capture study. Raw payloads never appear in the report."""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import statistics
import struct


def compact(data, offset):
    first = data[offset]
    offset += 1
    extra, bits, mask = ((0, 7, 127) if first < 128 else (1, 14, 63) if first < 192
                         else (2, 21, 31) if first < 224 else (3, 28, 15) if first < 240 else (4, 32, 0))
    value = first & mask
    for _ in range(extra):
        value = (value << 8) | data[offset]
        offset += 1
    if value & (1 << (bits - 1)):
        value -= 1 << bits
    return value, offset


def action(data):
    actor, key, header, flags = struct.unpack_from(">IIHH", data, 2)
    offset = 14
    target = None
    if flags & 128:
        target = struct.unpack_from(">I", data, offset)[0]
        offset += 4
    coordinates = []
    for _ in range(4):
        value, offset = compact(data, offset)
        coordinates.append(value)
    skill, sequence = struct.unpack_from(">HH", data, offset)
    if offset + 4 != len(data):
        raise ValueError("Unconsumed action bytes")
    return dict(actor=actor, key=key, header=header, flags=flags, skill=skill,
                sequence=sequence, target_object=target, coordinates=coordinates)


def study(folder):
    blob = (folder / "payloads.bin").read_bytes()
    counts = Counter()
    lengths = defaultdict(Counter)
    signatures = Counter()
    actors = Counter()
    actions = []
    hit_layouts = Counter()
    zero_life = set()
    life_updates = 0
    clicks = Counter()
    frames = []
    transitions = []
    invalid = []
    errors = []
    times = []
    first_time = None
    expected_offset = 0
    fixed = {0x18e:15, 0x18f:11, 0x192:10, 0x195:2, 0x1b:7, 0x7d:10, 0x25:16, 0x24:12}
    for number, line in enumerate((folder / "events.jsonl").read_text(encoding="utf-8").splitlines(), 1):
        try:
            event = json.loads(line)
            message = event["message"]
            payload = message.get("payload", {})
            if message.get("type") == "error" or payload.get("kind") == "capture-error":
                errors.append(number)
            if "offset" not in event:
                continue
            offset, length = event["offset"], event["length"]
            if offset != expected_offset or offset + length > len(blob):
                raise ValueError("Payload index gap or truncation")
            expected_offset = offset + length
            data = blob[offset:expected_offset]
            if length != payload["length"]:
                raise ValueError("Length metadata mismatch")
            # Failed pump attempts can include retry bytes; never treat them as accepted messages.
            if payload.get("kind") != "bytes" or not payload["accepted"]:
                continue
            direction, opcode = payload["direction"], payload["opcode"]
            if len(data) < 2 or int.from_bytes(data[:2], "big") != opcode:
                raise ValueError("Opcode metadata mismatch")
            if direction == "C2S" and opcode in fixed and len(data) != fixed[opcode]:
                raise ValueError("Known request length mismatch")
            timestamp = payload["started"]
            if first_time is None:
                first_time = timestamp
            times.append(timestamp)
            counts[(direction, opcode)] += 1
            lengths[(direction, opcode)][len(data)] += 1
            if direction == "S2C" and opcode == 0x1ab:
                decoded = action(data)
                actions.append(decoded)
                actors[decoded["actor"]] += 1
                signatures[(decoded["actor"], decoded["key"], decoded["header"], decoded["skill"])] += 1
            if direction == "S2C" and opcode == 0x1af:
                flags = int.from_bytes(data[20:22], "big")
                kind = data[22]
                expected = 23
                if kind & 2:
                    expected += 2 + (2 if flags & 0x400 else 0) + (2 if kind & 4 else 0) + (2 if kind & 8 else 0)
                if len(data) != expected:
                    raise ValueError("Unsupported action-hit layout")
                hit_layouts[(flags, kind, expected)] += 1
            if direction == "S2C" and opcode == 0x1b1:
                if len(data) != 24:
                    raise ValueError("Unsupported life-update layout")
                life_updates += 1
                if data[20] == 0 and int.from_bytes(data[12:16], "big") == 0:
                    zero_life.add(int.from_bytes(data[2:6], "big"))
            if direction == "S2C" and opcode == 0x2b5:
                frames.append(timestamp)
            if direction == "C2S" and opcode in (0x18e, 0x18f, 0x25, 0x24):
                index = 10 if opcode in (0x18e, 0x25) else 6
                skill = int.from_bytes(data[index:index+2], "big")
                flags_index = index + (4 if opcode in (0x25, 0x24) else 2)
                flags = int.from_bytes(data[flags_index:flags_index+2], "big")
                extra = data[-1] if opcode in (0x18e, 0x18f) else None
                clicks[(opcode, skill, flags, extra)] += 1
            if (direction, opcode) in (("S2C", 0x27), ("S2C", 0x11), ("S2C", 0x1d),
                                       ("C2S", 0x7d), ("S2C", 0x22), ("S2C", 0x21)):
                entry = dict(direction=direction, opcode=hex(opcode), ms_from_first=timestamp-first_time)
                if opcode == 0x22:
                    entry["local_player_id"] = int.from_bytes(data[2:6], "big")
                transitions.append(entry)
        except (ValueError, KeyError, IndexError, struct.error):
            invalid.append(number)
    gaps = sorted(b-a for a,b in zip(frames, frames[1:]))
    frame_report = dict(count=len(frames))
    if gaps and frames[-1] > frames[0]:
        frame_report.update(observed_hz=round(1000*(len(frames)-1)/(frames[-1]-frames[0]), 3),
                            median_gap_ms=statistics.median(gaps), p95_gap_ms=gaps[int(.95*len(gaps))],
                            maximum_gap_ms=max(gaps), zero_gaps=gaps.count(0))
    return dict(binary_sha256=hashlib.sha256(blob).hexdigest(), binary_bytes=len(blob),
                accepted_records=sum(counts.values()), invalid_records=invalid, capture_error_records=errors,
                duration_ms=max(times)-min(times) if times else 0, frames=frame_report,
                actions_fully_decoded=len(actions), actors=actors.most_common(), transitions=transitions,
                combat=dict(life_updates=life_updates, zero_life_objects=sorted(zero_life),
                            hit_layouts=[dict(flags=hex(f),kind=hex(k),length=l,count=n)
                                         for (f,k,l),n in hit_layouts.most_common()]),
                action_signatures=[dict(actor=a,key=hex(k),header=hex(h),skill=hex(s),count=n)
                                   for (a,k,h,s),n in signatures.most_common()],
                requests=[dict(opcode=hex(o),skill=hex(s),flags=hex(f),extra=e,count=n)
                          for (o,s,f,e),n in clicks.most_common()],
                messages=[dict(direction=d,opcode=hex(o),count=n,lengths=dict(lengths[(d,o)]))
                          for (d,o),n in sorted(counts.items())],
                limitations=["Instance serialization timestamps are not wire RTT or simulation time.",
                             "Initial area entry is absent. A final truncated record is excluded.",
                             "Actor identity before the town transition is inferred, not established by a captured 0x22.",
                             "Opcode presence alone does not establish gameplay parity or damage/RNG agreement."])


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("folder", type=Path)
    args = ap.parse_args()
    report = study(args.folder)
    output = args.folder / "study.json"
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({k:report[k] for k in ("accepted_records", "invalid_records", "capture_error_records",
                                          "actions_fully_decoded", "frames", "transitions")}, indent=2))
    print("Saved " + str(output))

if __name__ == "__main__":
    main()
