"""Print the MtxTypes row remap between two client versions as C# ranges (old first row, old last row, shift).

Usage: python -I scripts/re/port/mtx_remap.py <old tables> <new tables>

Rows are matched by MtxTypes.Id. A row in no range was removed in the new version. The output is pasted into
src/GameServer.Gateway/Saves/CosmeticMigration.cs; the script checks that the ranges cover exactly the kept rows.
"""
import json, os, sys


def main():
    old_dir, new_dir = sys.argv[1:3]
    old = json.load(open(os.path.join(old_dir, "MtxTypes.json"), encoding="utf-8"))
    new = json.load(open(os.path.join(new_dir, "MtxTypes.json"), encoding="utf-8"))
    new_index = {r["Id"]: i for i, r in enumerate(new)}
    assert len(new_index) == len(new), "MtxTypes.Id is not unique in the new version"
    ranges, removed = [], 0
    for i, r in enumerate(old):
        j = new_index.get(r["Id"])
        if j is None:
            removed += 1
            continue
        if old[i]["HASH32"] != new[j]["HASH32"]:
            raise SystemExit(f"row {i} {r['Id']}: HASH32 changed")
        shift = j - i
        if ranges and ranges[-1][2] == shift and ranges[-1][1] == i - 1:
            ranges[-1][1] = i
        else:
            ranges.append([i, i, shift])
    kept = sum(b - a + 1 for a, b, _ in ranges)
    assert kept + removed == len(old)
    print(f"// {len(old)} rows -> {len(new)} rows: {kept} kept in {len(ranges)} ranges, {removed} removed")
    for a, b, s in ranges:
        print(f"        ({a}, {b}, {s}),")


if __name__ == "__main__":
    main()
