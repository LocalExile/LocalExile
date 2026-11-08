"""Message ids of one client build against the next, by message class (Agents/docs/re/tooling.md section 11).

Usage: python -I scripts/re/port/compare_messages.py <map dir> <old dir> <new dir> <out.json>

<map dir> is scripts/re/ghidra/PortMap.java's output (functions.tsv, propagated.tsv, refs.tsv). <old dir> and <new dir>
each hold readschema.json (scripts/re/ghidra/ReadSchema.java: every case of the four S->C factories) and sendsites.json
(scripts/re/ghidra/SendSites.java: every C->S send with its (vtable, id) pairs), made on the matching build.

S->C: an old factory case with a class pairs with the new case whose class is the old vtable mapped (or, for a vtable
the map lacks, whose read and write functions are the old ones mapped); the id may change, the class may not. A class
several cases share pairs its old and new ids in order when they are as many. Every pair must keep the id order
(a later old id never gets an earlier new id). A case with no class pairs only through its neighbours: it is reported with the shift of the
classed cases around it when both agree, else as unpaired. C->S: the (vtable, id) pairs sent by each function of the old
build compare with those of its mapped function by mapped vtable; a vtable sent with several ids pairs them in order.
Writes every pair, the shifts (runs of equal id difference) and everything unpaired; exit 1 if a class pairs with no
case, an old id with two new ids, or the order breaks.
"""
import json, os, sys


def load_map(d):
    m = {}
    for line in open(os.path.join(d, "refs.tsv")):
        a, b, _ = line.split("\t")
        m[int(a, 16)] = int(b, 16)
    for line in open(os.path.join(d, "functions.tsv")):
        a, b = line.split("\t")[:2]
        m[int(a, 16)] = int(b, 16)
    for line in open(os.path.join(d, "propagated.tsv")):
        p = line.rstrip("\n").split("\t")
        if p[6] == "taken":
            m[int(p[0], 16)] = int(p[1], 16)
    return m


def h(v):
    return None if v is None else int(v, 16)


def runs(pairs):
    """[(old, new)] sorted by old -> [(first old, last old, delta)] of equal delta."""
    out = []
    for o, n in sorted(pairs):
        if out and out[-1][2] == n - o:
            out[-1][1] = o
        else:
            out.append([o, o, n - o])
    return [{"from": f"{a:#x}", "to": f"{b:#x}", "delta": d} for a, b, d in out]


def main():
    mapdir, olddir, newdir, outpath = sys.argv[1:5]
    m = load_map(mapdir)
    bad = []
    result = {"s2c": {}, "c2s": {}}

    old = json.load(open(os.path.join(olddir, "readschema.json"), encoding="utf-8"))["messages"]
    new = json.load(open(os.path.join(newdir, "readschema.json"), encoding="utf-8"))["messages"]
    for factory in sorted({x["factory"] for x in old}):
        nf = m.get(h(factory))
        os_ = [x for x in old if x["factory"] == factory]
        ns = [x for x in new if h(x["factory"]) == nf]
        def key(x, mapped):
            if not x["vtable"]:
                return None
            if mapped:
                nv = m.get(h(x["vtable"]))
                if nv is not None:
                    return ("vt", nv)
                if x["read"] and x["write"] and h(x["read"]) in m and h(x["write"]) in m:
                    return ("rw", m[h(x["read"])], m[h(x["write"])])
                return None
            return ("vt", h(x["vtable"]))
        groups_new = {}
        for x in ns:
            if x["vtable"]:
                groups_new.setdefault(("vt", h(x["vtable"])), []).append(x)
                if x["read"] and x["write"]:
                    groups_new.setdefault(("rw", h(x["read"]), h(x["write"])), []).append(x)
        groups_old = {}
        for x in os_:
            if x["vtable"]:
                groups_old.setdefault(key(x, True), []).append(x)
        pairs, unpaired_old, used = [], [], set()
        for k, xs in groups_old.items():
            ys = groups_new.get(k, []) if k else []
            xs = sorted(xs, key=lambda x: h(x["id"]))
            ys = sorted(ys, key=lambda x: h(x["id"]))
            if k and len(xs) == len(ys):
                for x, y in zip(xs, ys):
                    pairs.append((h(x["id"]), h(y["id"]), x["vtable"], y["vtable"]))
                    used.add(h(y["id"]))
            else:
                for x in xs:
                    unpaired_old.append({"id": x["id"], "vtable": x["vtable"],
                                         "why": "class not mapped" if not k else f"{len(xs)} old and {len(ys)} new cases"})
                    bad.append(f"S->C {factory} {x['id']}")
        ordered = sorted(pairs)
        for (o1, n1, *_), (o2, n2, *_) in zip(ordered, ordered[1:]):
            if n2 <= n1:
                bad.append(f"S->C {factory} order {o1:#x}->{n1:#x}, {o2:#x}->{n2:#x}")
        # Classless cases: the shift of the nearest classed pairs on both sides, when they agree.
        classed = sorted((o, n) for o, n, _, _ in pairs)
        classless = []
        new_classless = {h(x["id"]) for x in ns if not x["vtable"]}
        for x in os_:
            if x["vtable"]:
                continue
            o = h(x["id"])
            lo = max((p for p in classed if p[0] < o), default=None)
            hi = min((p for p in classed if p[0] > o), default=None)
            dl = None if lo is None else lo[1] - lo[0]
            dh = None if hi is None else hi[1] - hi[0]
            if dl is not None and dl == dh and o + dl in new_classless:
                classless.append({"old": f"{o:#x}", "new": f"{o + dl:#x}"})
            else:
                classless.append({"old": f"{o:#x}", "new": None, "between": [dl, dh]})
        unpaired_new = [x["id"] for x in ns if x["vtable"] and h(x["id"]) not in used]
        result["s2c"][factory] = {
            "newFactory": None if nf is None else f"{nf:#x}",
            "cases": [len(os_), len(ns)],
            "shifts": runs([(o, n) for o, n, _, _ in pairs]),
            "pairs": [{"old": f"{o:#x}", "new": f"{n:#x}", "vtable": v, "newVtable": nv} for o, n, v, nv in sorted(pairs)],
            "unpairedOld": unpaired_old,
            "unpairedNew": unpaired_new,
            "classless": classless,
        }

    olds = json.load(open(os.path.join(olddir, "sendsites.json"), encoding="utf-8"))["sites"]
    news = json.load(open(os.path.join(newdir, "sendsites.json"), encoding="utf-8"))["sites"]
    def sent(sites):
        out = {}
        for st in sites:
            for p in st["pairs"]:
                if p.get("id"):
                    out.setdefault(h(st["func"]), {}).setdefault(h(p["vtable"]) if p["vtable"] else None, set()).add(h(p["id"]))
        return out
    so, sn = sent(olds), sent(news)
    pairs, unmatched = set(), []
    for f, vts in so.items():
        nf = m.get(f)
        if nf not in sn:
            unmatched.append({"func": f"{f:#x}", "why": "function not mapped" if nf is None else f"no send in {nf:#x}"})
            continue
        for vt, ids in vts.items():
            nvt = m.get(vt) if vt is not None else None
            if vt is not None and nvt is None:
                unmatched.append({"func": f"{f:#x}", "vtable": f"{vt:#x}", "why": "vtable not mapped"})
                continue
            nids = sn[nf].get(nvt, set())
            if len(nids) == len(ids):
                pairs.update(zip(sorted(ids), sorted(nids)))
            else:
                unmatched.append({"func": f"{f:#x}", "vtable": None if vt is None else f"{vt:#x}",
                                  "ids": sorted(hex(i) for i in ids), "new": sorted(hex(i) for i in nids)})
    by_old = {}
    for o, n in pairs:
        by_old.setdefault(o, set()).add(n)
    for o, ns_ in by_old.items():
        if len(ns_) > 1:
            bad.append(f"C->S {o:#x} -> {sorted(hex(n) for n in ns_)}")
    flat = sorted((o, min(n)) for o, n in by_old.items())
    for (o1, n1), (o2, n2) in zip(flat, flat[1:]):
        if n2 <= n1:
            bad.append(f"C->S order {o1:#x}->{n1:#x}, {o2:#x}->{n2:#x}")
    result["c2s"] = {
        "sites": [len(olds), len(news)],
        "shifts": runs(flat),
        "ids": {f"{o:#x}": sorted(f"{n:#x}" for n in ns_) for o, ns_ in sorted(by_old.items())},
        "unmatched": unmatched,
    }
    result["problems"] = bad
    json.dump(result, open(outpath, "w", encoding="utf-8"), indent=1)
    for f, r in result["s2c"].items():
        print(f"S->C {f} -> {r['newFactory']}: cases {r['cases']}, pairs {len(r['pairs'])}, unpaired old "
              f"{len(r['unpairedOld'])} new {len(r['unpairedNew'])}, classless unpaired "
              f"{sum(1 for c in r['classless'] if c['new'] is None)}; shifts {r['shifts']}")
    c = result["c2s"]
    print(f"C->S sites {c['sites']}, ids {len(c['ids'])}, unmatched {len(c['unmatched'])}; shifts {c['shifts']}")
    print(f"problems {len(bad)}")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
