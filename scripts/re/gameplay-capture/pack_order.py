"""Study entry-table order in nearby opening monsters; inferred groups are not verified packs."""
import argparse
from collections import Counter
import json
from math import factorial
from pathlib import Path

from semantics import records, study_semantics, table


def group_nearby(monsters, diameter):
    """Partition consecutive identities using an all-pairs distance bound, independent of variety rank."""
    groups = []
    for monster in monsters:
        previous = groups[-1] if groups else []
        x, y = monster["position"]
        if (not previous or monster["stream"] != previous[-1]["stream"]
                or monster["entity"] != previous[-1]["entity"] + 1
                or any((x-m["position"][0])**2 + (y-m["position"][1])**2 > diameter**2
                       for m in previous)):
            groups.append([])
        groups[-1].append(monster)
    return groups


def describe(group):
    ranks = [m["entry_rank"] for m in group]
    permutations = factorial(len(group))
    for count in Counter(ranks).values():
        permutations //= factorial(count)
    return dict(entities=[m["entity"] for m in group], entry_ranks=ranks,
                positions=[m["position"] for m in group],
                table_ordered=all(a <= b for a, b in zip(ranks, ranks[1:])),
                distinct_orderings_of_same_members=permutations)


def study(folder, pack_id, diameters):
    packs, entries = table("MonsterPacks"), table("MonsterPackEntries")
    pack_row = next(i for i, p in enumerate(packs) if p["Id"] == pack_id)
    members = [(i, e) for i, e in enumerate(entries) if e["MonsterPacksKey"] == pack_row]
    ranks = {}
    for rank, (_, entry) in enumerate(members):
        variety = entry["MonsterVarietiesKey"]
        if variety in ranks:
            raise ValueError("Pack has duplicate variety rows; rank would be ambiguous")
        ranks[variety] = rank
    town = next((n for n, v, b in records(folder) if v["direction"] == "S2C"
                 and v["opcode"] == 0x1d and int.from_bytes(b[6:8], "big") == 0x235c), float("inf"))
    unique = {}
    for m in study_semantics(folder)["monsters"]:
        if m["record"] < town:
            unique.setdefault((m["stream"], m["entity"]), m)
    monsters = [dict(stream=m["stream"], entity=m["entity"], entry_rank=ranks[m["row"]],
                     position=m["prefix"]["position"])
                for _, m in sorted(unique.items()) if m["row"] in ranks and "prefix" in m]
    trials = []
    for diameter in diameters:
        groups = [describe(g) for g in group_nearby(monsters, diameter)]
        multi = [g for g in groups if len(g["entities"]) > 1]
        trials.append(dict(diameter=diameter, groups=groups, multi_member_groups=len(multi),
                           ordered_multi_member_groups=sum(g["table_ordered"] for g in multi),
                           largest_group=max((len(g["entities"]) for g in groups), default=0)))
    return dict(pack=pack_id, pack_row=pack_row, min_count=packs[pack_row]["MinCount"],
                max_count=packs[pack_row]["MaxCount"],
                entries=[dict(row=i, rank=rank, variety=e["MonsterVarietiesKey"], flag=e["Flag"],
                              weight=e["Weight"]) for rank, (i, e) in enumerate(members)],
                observed_monsters=len(monsters), trials=trials,
                limitations=["Consecutive identities and nearby first positions do not prove pack membership.",
                             "Positions can include movement before capture; missing identities break groups.",
                             "Order can arise during allocation or entity creation, independently of selection.",
                             "Permutation counts are descriptive, not significance tests or proof of flag semantics."])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path)
    parser.add_argument("--pack", default="Act1-ZombieDrownedStrand")
    parser.add_argument("--diameters", nargs="+", type=int, default=[24, 32, 48, 64])
    args = parser.parse_args()
    if any(d <= 0 for d in args.diameters):
        parser.error("diameters must be positive")
    report = study(args.folder, args.pack, args.diameters)
    (args.folder / "pack-order.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(dict(pack=report["pack"], observed_monsters=report["observed_monsters"],
                          trials=[{k: v for k, v in t.items() if k != "groups"} for t in report["trials"]]),
                     indent=2))
