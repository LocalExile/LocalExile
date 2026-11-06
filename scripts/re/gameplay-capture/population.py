"""Count distinct opening-area monsters; keep missing-creation inferences separate from decoded identities."""
import argparse
from collections import Counter, defaultdict
import json
from pathlib import Path

from analyze import action
from semantics import Reader, records, study_semantics, table


def population(folder):
    semantic = study_semantics(folder)
    rows = list(records(folder))
    town = next((n for n,v,b in rows if v["direction"] == "S2C" and v["opcode"] == 0x1d
                 and int.from_bytes(b[6:8], "big") == 0x235c), float("inf"))
    unique = {}
    for monster in semantic["monsters"]:
        if monster["record"] < town:
            unique.setdefault((monster["stream"], monster["entity"]), monster)
    created, players, stats, actions = set(), set(), defaultdict(dict), defaultdict(list)
    stat_rows = table("Stats")
    for n,v,b in rows:
        if n >= town or v["direction"] != "S2C":
            continue
        key = (v["stream"], int.from_bytes(b[2:6], "big"))
        if v["opcode"] == 0x2b9:
            created.add(key)
        elif v["opcode"] == 0x22:
            players.add(key)
        elif v["opcode"] == 0x1b3:
            reader = Reader(b[12:])
            for _ in range(reader.unsigned()):
                stat, value = reader.unsigned(), reader.signed()
                if not 1 <= stat <= len(stat_rows):
                    raise ValueError("Invalid update stat row")
                stats[key][stat_rows[stat-1]["Id"]] = value
        elif v["opcode"] == 0x1ab:
            decoded = action(b)
            actions[key].append(decoded)
            if decoded["header"] in (0x9001, 0x5001) and decoded["key"] == decoded["actor"]:
                players.add(key)
    missing = []
    for key, values in sorted(stats.items()):
        if key in created or key in players:
            continue
        hidden = values.get("virtual_is_hidden_monster") == 1
        # This is corroborating evidence, not a recovered type or rarity. Do not count player-style order headers.
        combat_actor = values.get("main_hand_total_base_weapon_attack_duration_ms", 0) > 0 and any(
            a["header"] == 0xa000 for a in actions[key])
        if hidden or combat_actor:
            missing.append(dict(stream=key[0], entity=key[1], evidence="hidden_monster_stat" if hidden
                                else "untyped_actor_with_combat_stats", stats=values,
                                action_count=len(actions[key]), rarity=None))
    rarity = Counter(str(m["prefix"]["rarity"]) if "prefix" in m else "undecoded" for m in unique.values())
    return dict(creation_records=sum(m["record"] < town for m in semantic["monsters"]),
                distinct_created_monsters=len(unique), created_rarities=dict(rarity),
                missing_creation_actors=missing, provisional_reconstructed_total=len(unique)+len(missing),
                exact_area_total_verified=False,
                prefix_failures=[f for f in semantic["prefix_failures"] if f["record"] < town],
                limitations=["An unseen monster can be absent from both creation and update records.",
                             "Untyped combat actors are an inference; their exact variety and rarity remain unknown.",
                             "Count includes the tutorial zombie and Hillock; recreation messages are deduplicated."])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path)
    args = parser.parse_args()
    report = population(args.folder)
    (args.folder / "population.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({k:v for k,v in report.items() if k != "missing_creation_actors"}, indent=2))
