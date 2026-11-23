"""Diff two instance snapshots taken before and after the monsters died.

Prints the integers that changed, with the before/after pair, so a counter (one that drops as monsters die) stands out.
"""

import json
import sys

A = r"D:\大肥鱼工作区\inst-A.jsonl"
B = r"D:\大肥鱼工作区\inst-B.jsonl"


def load(path):
    with open(path, encoding="utf-8") as f:
        for line in f:
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                continue
            if rec.get("type") == "send" and "instance" in rec.get("payload", {}):
                return rec["payload"]
    raise SystemExit(f"no dump in {path}")


a, b = load(A), load(B)
print(f"A instance={a['instance']} row={a.get('rowId')}")
print(f"B instance={b['instance']} row={b.get('rowId')}")

for key in ("inst_0", "inst_c000"):
    xs, ys = a.get(key), b.get(key)
    if xs is None or ys is None:
        print(f"{key}: missing")
        continue
    changed = [(i, x, y) for i, (x, y) in enumerate(zip(xs, ys)) if x != y]
    drops = [(i, x, y) for i, x, y in changed if y < x]
    print(f"\n{key}: {len(changed)} of {len(xs)} int(s) changed, {len(drops)} decreased")
    for i, x, y in changed[:40]:
        print(f"  [{i:5d}] 0x{i*4:06x}  {x:>12} -> {y:>12}   (delta {y - x})")
