"""Compare two captures structurally; different input modes/seeds are reported, not normalized away."""
import argparse
from collections import Counter
import json
from pathlib import Path
from analyze import study


def details(folder):
    report = study(folder)
    data = (folder / "payloads.bin").read_bytes()
    duplicate_life = 0
    last_pool = {}
    areas = []
    phase = "pre-town"
    phases = {phase:Counter(), "town":Counter()}
    for line in (folder / "events.jsonl").read_text(encoding="utf-8").splitlines():
        try:
            event = json.loads(line)
            value = event["message"].get("payload", {})
            if value.get("kind") != "bytes" or not value["accepted"]:
                continue
            offset, length = event["offset"], event["length"]
            if offset + length > len(data):
                continue
            packet = data[offset:offset+length]
            direction, opcode = value["direction"], value["opcode"]
            if direction == "S2C" and opcode == 0x1d:
                area = int.from_bytes(packet[6:8], "big")
                string_length = int.from_bytes(packet[8:10], "big")
                seed = int.from_bytes(packet[10+string_length:14+string_length], "big")
                areas.append(dict(area_hash=hex(area), seed=seed))
                if area == 0x235c:
                    phase = "town"
            phases[phase][direction + " " + hex(opcode)] += 1
            if direction == "S2C" and opcode == 0x1b1 and len(packet) == 24:
                key = (value["stream"], int.from_bytes(packet[2:6], "big"), packet[20])
                state = packet[12:]
                duplicate_life += last_pool.get(key) == state
                last_pool[key] = state
        except (KeyError, ValueError, IndexError):
            continue
    requests = Counter({m["opcode"]:m["count"] for m in report["messages"] if m["direction"] == "C2S"})
    predictive = sum(requests[o] for o in ("0x24", "0x25"))
    lockstep = sum(requests[o] for o in ("0x18e", "0x18f"))
    report.update(network_mode="mixed" if predictive and lockstep else "lockstep" if lockstep else "predictive" if predictive else "unknown",
                  predictive_action_requests=predictive, lockstep_action_requests=lockstep,
                  repeated_pool_states=duplicate_life, captured_areas=areas,
                  phase_counts={p:dict(sorted(c.items())) for p,c in phases.items()})
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("official", type=Path)
    parser.add_argument("local", type=Path)
    args = parser.parse_args()
    official, local = details(args.official), details(args.local)
    result = dict(official=official, local=local, limitations=[
        "Different network modes: do not attribute movement packet differences to a server defect.",
        "Official start is missing; local includes start. Runs have different seeds, durations and actions.",
        "Message counts, health changes and hit flags alone cannot establish balance or performance parity."])
    path = args.local / "comparison.json"
    path.write_text(json.dumps(result, indent=2), encoding="utf-8")
    for label, report in (("official", official), ("local", local)):
        print(label, json.dumps({k:report[k] for k in ("network_mode", "accepted_records", "captured_areas", "repeated_pool_states")}))
    print("Saved " + str(path))

if __name__ == "__main__":
    main()
