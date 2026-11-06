"""Correlate captured hidden-buff removals, Emerge actions and nearby player-action origins offline."""
import argparse
from bisect import bisect_left
from collections import defaultdict
import json
from math import dist
from pathlib import Path

from analyze import action
from semantics import records, study_semantics


def neighbours(samples, record, timestamp, position):
    """Bracket by record order, never interpolate a movement destination as the player's actual position."""
    index = bisect_left([sample["record"] for sample in samples], record)
    result = {}
    for name, sample in (("before", samples[index-1] if index else None),
                         ("after", samples[index] if index < len(samples) else None)):
        result[name] = (dict(record=sample["record"], time_delta_ms=sample["time"]-timestamp,
                             position=sample["position"], distance=dist(position, sample["position"]))
                        if sample else None)
    return result


def study(folder):
    semantic = study_semantics(folder)
    births = {m["record"]: m for m in semantic["monsters"] if "prefix" in m}
    hidden_actors = {(m["stream"], m["entity"]) for m in births.values()
                     if any(buff["id"] == "hidden_monster" for buff in m["prefix"].get("initial_buffs", []))}
    known, pending = {}, {}
    players, inferred, origins = {}, defaultdict(set), defaultdict(list)
    removals, events, unpaired = [], [], []
    for number, value, packet in records(folder):
        if value["direction"] != "S2C":
            continue
        stream, opcode, timestamp = value["stream"], value["opcode"], value["started"]
        entity = int.from_bytes(packet[2:6], "big")
        key = stream, entity
        if opcode == 0x22:
            players[stream] = entity
        elif opcode == 0x2b9:
            pending.pop(key, None)
            known.pop(key, None)
            if (birth := births.get(number)) is not None:
                hidden = [buff for buff in birth["prefix"].get("initial_buffs", []) if buff["id"] == "hidden_monster"]
                if len(hidden) == 1:
                    known[key] = birth, hidden[0]["instance"]
        elif opcode == 0x1c2 and len(packet) == 14:
            if key in known and int.from_bytes(packet[12:14], "big") == known[key][1]:
                birth, buff = known.pop(key)
                entry = dict(stream=stream, entity=entity, variety=birth["path"], buff_instance=buff,
                             creation_record=birth["record"], birth_position=birth["prefix"]["position"],
                             removal_record=number, removal_time=timestamp)
                pending[key] = entry
                removals.append(entry)
        elif opcode == 0x1ab:
            decoded = action(packet)
            if decoded["header"] in (0x9001, 0x5001) and decoded["key"] == entity:
                inferred[stream].add(entity)
            origins[stream].append(dict(record=number, time=timestamp, entity=entity,
                                        position=decoded["coordinates"][:2]))
            if decoded["skill"] == 0x101:
                if key in pending:
                    entry = pending.pop(key)
                    entry.update(emerge_record=number, emerge_delay_ms=timestamp-entry["removal_time"],
                                 wake_position=decoded["coordinates"][:2])
                    events.append(entry)
                else:
                    unpaired.append(dict(stream=stream, entity=entity, record=number))
    identities = {}
    for stream in origins:
        candidates = sorted(inferred[stream])
        player = players.get(stream)
        source = "local_object_header" if player is not None else "ambiguous_or_absent"
        if player is None and len(candidates) == 1:
            player, source = candidates[0], "inferred_self_action_headers"
        identities[stream] = dict(entity=player, source=source, self_header_candidates=candidates)
        samples = [sample for sample in origins[stream] if sample["entity"] == player]
        for event in events:
            if event["stream"] == stream:
                event.update(neighbours(samples, event["removal_record"], event["removal_time"], event["wake_position"]))
                event["unchanged_since_creation"] = event["birth_position"] == event["wake_position"]
    removed_actors = {(r["stream"], r["entity"]) for r in removals}
    return dict(player_identities=identities, initial_hidden_actors=len(hidden_actors), events=events,
                initial_hidden_actors_without_removal=[dict(stream=s, entity=e) for s, e in sorted(hidden_actors-removed_actors)],
                removals_without_emerge=[r for r in removals if "emerge_record" not in r],
                emerges_without_initial_hidden_removal=unpaired, prefix_failures=semantic["prefix_failures"],
                limitations=["Only an initially decoded hidden_monster instance can be paired with its removal.",
                             "Stream and entity identify an actor; recreated objects reset pending hidden state.",
                             "Player-action origins bracket observations, not exact positions at the wake time.",
                             "Self-action headers infer identity only when one candidate exists in that stream.",
                             "Hook times are not server game times; no universal wake radius or cadence is inferred.",
                             "Nearby actors do not establish pack membership or a shared pack wake rule."])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path)
    args = parser.parse_args()
    report = study(args.folder)
    (args.folder / "wake.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(dict(paired_events=len(report["events"]),
                          initial_hidden_actors=report["initial_hidden_actors"],
                          initial_hidden_actors_without_removal=len(report["initial_hidden_actors_without_removal"]),
                          unchanged_since_creation=sum(e["unchanged_since_creation"] for e in report["events"]),
                          removals_without_emerge=len(report["removals_without_emerge"]),
                          emerges_without_initial_hidden_removal=len(report["emerges_without_initial_hidden_removal"]),
                          prefix_failures=len(report["prefix_failures"])), indent=2))
