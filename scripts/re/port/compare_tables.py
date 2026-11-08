"""Compare the game tables of two client versions, decoded by Tools/PoEDataExtractor into two folders.

Usage: python -I scripts/re/port/compare_tables.py <old tables> <new tables> <schema.min.json> <out.json>

For every table in both folders it reports:
- rows: the row count of each version;
- the key (the first unique string column) and, matched by key: rows added, rows removed, and the rows whose index moved
  (a row index is what the server hard-codes as a "row N" or a Stats key N+1), with the first moved index;
- columns whose values changed for many matched rows, which would mean the schema's column order is wrong for the new
  version. Foreign rows are compared through the referenced table's key, so a reference that moved with its target
  row is not a change.
Exit 1 when a column looks reordered (more than half of at least 20 matched rows differ).
"""
import json, os, sys


def load(folder, name):
    path = os.path.join(folder, name + ".json")
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def main():
    old_dir, new_dir, schema_path, out_path = sys.argv[1:5]
    schema = json.load(open(schema_path, encoding="utf-8"))
    # The schema lists Path of Exile 1 and 2 tables under the same names; validFor bit 1 is Path of Exile 1.
    tables = {t["name"]: t for t in schema["tables"] if t.get("validFor", 1) & 1}
    names = sorted(n[:-5] for n in os.listdir(old_dir) if n.endswith(".json"))
    names = [n for n in names if os.path.exists(os.path.join(new_dir, n + ".json"))]

    data = {n: (load(old_dir, n), load(new_dir, n)) for n in names}
    keys = {}
    for n in names:
        # The key is the first string column whose values are unique and non-empty in both versions (the schema's own
        # "unique" flag is missing on several Id columns, WorldAreas.Id among them).
        cols = tables.get(n, {}).get("columns", [])
        old, new = data[n]
        def unique(name):
            for rows in (old, new):
                values = [r.get(name) for r in rows]
                if not rows or any(v in (None, "") for v in values) or len(set(values)) != len(values):
                    return False
            return True
        candidates = [c["name"] for c in cols if c["type"] == "string" and not c.get("array") and c.get("name")]
        candidates = [c for c in candidates if c == "Id"] + [c for c in candidates if c != "Id"]
        keys[n] = next((c for c in candidates if unique(c)), None)

    def key_of(table, version, index):
        rows = data.get(table, (None, None))[version]
        k = keys.get(table)
        if rows is None or k is None or not isinstance(index, int) or not 0 <= index < len(rows):
            return ("raw", index)
        return ("key", rows[index].get(k))

    report, suspicious = {}, []
    for n in names:
        old, new = data[n]
        entry = {"rows": [len(old), len(new)], "key": keys[n]}
        k = keys[n]
        if k:
            oi = {r.get(k): i for i, r in enumerate(old)}
            ni = {r.get(k): i for i, r in enumerate(new)}
            if len(oi) != len(old) or len(ni) != len(new):
                entry["key"] = None
                k = None
        if k:
            entry["added"] = [x for x in ni if x not in oi]
            entry["removed"] = [x for x in oi if x not in ni]
            moved = sorted(oi[x] for x in oi if x in ni and oi[x] != ni[x])
            entry["moved"] = len(moved)
            entry["firstMoved"] = moved[0] if moved else None
            pairs = [(old[oi[x]], new[ni[x]]) for x in oi if x in ni]
        else:
            # No key: rows are only comparable by index. Report how many leading rows are unchanged (foreign rows
            # compared through their target's key), so an append (the whole old table is a prefix) shows as such.
            def same(a, b):
                for c in tables.get(n, {}).get("columns", []):
                    name = c["name"]
                    if not name or name not in a:   # a column the old version does not have
                        continue
                    ref = n if c["type"] == "row" else (c.get("references") or {}).get("table") if c["type"] == "foreignrow" else None
                    va, vb = a.get(name), b.get(name)
                    if ref:
                        va = [key_of(ref, 0, x) for x in va] if isinstance(va, list) else key_of(ref, 0, va)
                        vb = [key_of(ref, 1, x) for x in vb] if isinstance(vb, list) else key_of(ref, 1, vb)
                    if va != vb:
                        return False
                return True
            prefix = 0
            while prefix < min(len(old), len(new)) and same(old[prefix], new[prefix]):
                prefix += 1
            entry["unchangedPrefix"] = prefix
            entry["moved"] = None if prefix < len(old) and len(old) != len(new) else 0
            pairs = list(zip(old, new)) if len(old) == len(new) else list(zip(old[:prefix], new[:prefix]))
        cols = tables.get(n, {}).get("columns", [])
        changed = {}
        for c in cols:
            name = c["name"]
            if not name or name == k or (old and name not in old[0]):   # a column the old version does not have
                continue
            ref = (c.get("references") or {}).get("table") if c["type"] in ("foreignrow", "row") else None
            if c["type"] == "row":
                ref = n
            diff = 0
            for a, b in pairs:
                va, vb = a.get(name), b.get(name)
                if ref:
                    conv = lambda v, ver: [key_of(ref, ver, x) for x in v] if isinstance(v, list) else key_of(ref, ver, v)
                    va, vb = conv(va, 0), conv(vb, 1)
                if va != vb:
                    diff += 1
            if pairs and diff:
                changed[name] = round(diff / len(pairs), 3)
                if len(pairs) >= 20 and diff / len(pairs) > 0.5:
                    suspicious.append(f"{n}.{name}: {diff}/{len(pairs)} matched rows differ")
        entry["matched"] = len(pairs)
        entry["changedColumns"] = changed
        report[n] = entry

    json.dump({"tables": report, "suspicious": suspicious}, open(out_path, "w", encoding="utf-8"), indent=1)
    rows_changed = [n for n in names if report[n]["rows"][0] != report[n]["rows"][1]]
    moved = [n for n in names if report[n].get("moved")]
    print(f"tables compared: {len(names)}; row count changed: {len(rows_changed)}; rows moved: {len(moved)}")
    for s in suspicious:
        print("suspicious:", s)
    return 1 if suspicious else 0


if __name__ == "__main__":
    sys.exit(main())
