# usage: c2s_md.py [sendsites.json]   -- every client -> server message -> Agents/docs/protocol/c2s.md (+ c2s.json)
# Regenerate the input with (about 15 s):
#   bash scripts/re/ghidra/g.sh SendSites.java 'C:\...\Tools\re\out\sendsites.json'
# Also reads Tools/re/out/decomp-all (writer lengths), Tools/re/out/readschema.json (shared S->C classes) and
# Client/Game.exe (vtable slots). Exits 1 if an exact length disagrees with the Gateway's KnownClientLength.
import collections, json, os, re, struct, sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUTDIR = os.path.join(REPO, "Tools", "re", "out")
SRC = sys.argv[1] if len(sys.argv) > 1 else os.path.join(OUTDIR, "sendsites.json")
OUT = os.path.join(REPO, "Agents", "docs", "protocol", "c2s.md")
EXE = os.path.join(REPO, "Client", "Game.exe")
D = os.path.join(OUTDIR, "decomp-all")

# Senders that SendSites.java does not cover: no 0x14216e940 call, or a site it cannot resolve. Each was read in the
# decompile. (protocol, id, class vtable or None for an inline write, sender function, note)
HAND = [
    ("instance", 0x85, None, "0x1403a8d30", "inline id only, from about 21 functions (htons(0x85) to instance+0xc288)"),
    ("instance", 0xcc, "0x1435c0438", "0x1421670f0", "calls the class writer directly"),
    ("instance", 0xcf, "0x1435c0298", "0x1421676c0", "calls the class writer directly"),
    ("instance", 0x26e, "0x1435bfdf8", "0x14216ca20", "calls the class writer directly"),
    ("instance", 0x20a, "0x14345ef10", "0x140a92540", "site 0x140a925a7: rdx merges 0 with a heap message"),
    ("instance", 0x20a, "0x14345ef10", "0x140d49f20", "site 0x140d4a1ce, in a .pdata chunk outside any Ghidra function"),
    # Login: writes to login+0xdd0. Fields: ../re/009-login-messages.md.
    ("login", 0x1, None, "0x14213e210", "keepalive, id only, every 10 s (its this is the login object's +8 base, so the stream is +0xdc8)"),
    ("login", 0x2, "0x1435c0038", "0x14213e610", ""),
    ("login", 0x3, "0x1435d8b88", "0x14213ea10", "also 0x140d05f70"),
    ("login", 0x5, "0x1435d8b30", "0x14213eda0", ""),
    ("login", 0x6, "0x1435d8ae8", "0x14213eba0", ""),
    ("login", 0x7, "0x1435d8ae8", "0x14213ef90", ""),
    ("login", 0x8, None, "0x140d60410", "u32, raw64; through *(global+0x350)+0xdd0, not the login object *(global+0x358)"),
    ("login", 0xa, "0x1435d8aa0", "0x140cf3150", ""),
    ("login", 0xc, "0x1435bfcd8", "0x140cfafa0", ""),
    ("login", 0xe, "0x1435c0898", "0x1421406f0", ""),
    ("login", 0x12, "0x1435d8b10", "0x142140490", ""),
    ("login", 0x18, None, "0x140d13060", ""),
    ("login", 0x19, None, "0x140d166a0", ""),
    ("login", 0x1c, None, "0x142140a80", "u16, u16"),
    ("login", 0x21, None, "0x140d285d0", ""),
    ("login", 0x22, "0x1435d8ac8", "0x142140b40", ""),
    ("login", 0x23, None, "0x140d268b0", ""),
    ("login", 0x2a, None, "0x140d26780", "wstr, u8"),
    ("login", 0x2b, None, "0x140d26650", "wstr, u8"),
    ("login", 0x2c, "0x1435d8a80", "0x140d01a60", ""),
    ("login", 0x39, None, "0x140d264b0", ""),
    ("login", 0x3a, None, "0x140d08c80", "u8; also 0x140d08de0"),
    ("login", 0x3d, "0x1435d8b68", "0x142140890", ""),
    ("login", 0x3f, None, "0x142140d70", "wstr"),
    ("login", 0x40, None, "0x142140e60", "wstr"),
    ("login", 0x42, None, "0x142140810", "u8"),
    ("login", 0x45, None, "0x1400f50f0", "blob16 string; the same function sends instance 0x2fa when in an instance"),
    ("login", 0x46, None, "0x140d14690", "u32 (0)"),
    # Patch: u8 id at +8, stream at patch+0x60. Every flush in the three patch senders is one of these.
    ("patch", 0x1, "0x14363ea68", "0x1428e9f40", "u8 id, u8 (7)"),
    ("patch", 0x3, "0x14363eae8", "0x1428ea9d0", "u8 id, wstr; also 0x1428eade0 (ctor 0x1428e8320). The class is S→C 0x6's"),
    ("patch", 0x5, "0x14363eb08", "0x1428eade0", "u8 id only, sent from two places"),
    ("patch", 0x8, "0x14363e9e0", "0x1428eade0", "u8 id, u16 length, bytes. The class is S→C 0xa's"),
]

import pefile
pe = pefile.PE(EXE, fast_load=True)
data = open(EXE, "rb").read()
def slot(vt, i):
    return struct.unpack_from("<Q", data, pe.get_offset_from_rva(int(vt, 16) + 8 * i - pe.OPTIONAL_HEADER.ImageBase))[0]

def src(a):
    p = os.path.join(D, a[2:5], "FUN_%s.c" % a)
    return open(p, encoding="utf-8", errors="replace").read() if os.path.exists(p) else None

def writer(fa):
    """Bytes a writer writes: the sum of its fixed-size stream writes; exact when it has no variable write, no call
    that takes the stream and no branch or loop."""
    t = src("%09x" % fa)
    if not t or "{" not in t: return None
    body = t.split("{", 1)[1]
    fixed = sum(int(n, 0) for n in re.findall(r"FUN_140dfb760\([^,]+,[^,]+,(0x[0-9a-f]+|\d+)\)", body))
    var = re.search(r"FUN_140dfb760\([^,]+,[^,]+,[^)0-9][^)]*\)", body)
    calls = set(re.findall(r"(FUN_1[0-9a-f]{8})\([^;]*param_2", body)) - {"FUN_140dfb760"}
    branch = re.search(r"\b(if|for|while|do)\b", body)
    return fixed, not var and not calls and not branch

def gateway_lengths():
    """KnownClientLength: id -> fixed length (the variable-length cases of SplitClientMessages are left out)."""
    consts = {}
    for root, _, fs in os.walk(os.path.join(REPO, "src")):
        for f in fs:
            if not f.endswith(".cs"): continue
            t = open(os.path.join(root, f), encoding="utf-8").read()
            cls = re.search(r"\b(?:class|struct)\s+(\w+)", t)
            for m in re.finditer(r"const\s+(?:ushort|int|byte)\s+([^;]+);", t):
                for part in m.group(1).split(","):
                    kv = re.match(r"\s*(\w+)\s*=\s*(0x[0-9a-fA-F]+|\d+)\s*$", part)
                    if kv:
                        consts[kv.group(1)] = int(kv.group(2), 0)
                        if cls: consts[cls.group(1) + "." + kv.group(1)] = int(kv.group(2), 0)
    val = lambda x: int(x, 0) if re.fullmatch(r"0x[0-9a-fA-F]+|\d+", x) else consts.get(x, consts.get(x.split(".")[-1]))
    t = open(os.path.join(REPO, "src", "GameServer.Core", "Protocol", "Instance", "InstanceProtocol.cs"), encoding="utf-8").read()
    table = t[t.index("KnownClientLength(ushort id) => id switch"):]
    table = table[:table.index("};")]
    out = {}
    for k, l in re.findall(r"([\w.]+)\s*=>\s*([\w.]+)", table):
        if k != "_" and val(k) is not None and val(l) is not None: out[val(k)] = val(l)
    return out


def main():
    sites = json.load(open(SRC, encoding="utf-8"))["sites"]
    s2c = collections.defaultdict(list)
    names = {"0x1421b5c40": "instance", "0x1421c6150": "world", "0x14219c350": "login", "0x1428f74d0": "patch"}
    for m in json.load(open(os.path.join(OUTDIR, "readschema.json"), encoding="utf-8"))["messages"]:
        if m["vtable"]: s2c[m["vtable"]].append(f"{names[m['factory']]} {m['id']}")

    msgs = collections.defaultdict(lambda: {"classes": set(), "senders": set(), "notes": []})
    unresolved = []
    for s in sites:
        good = [p for p in s["pairs"] if p["id"] and p["vtable"]]
        for p in good:
            e = msgs[("instance", int(p["id"], 16))]
            e["classes"].add(p["vtable"])
            e["senders"].add(s["func"] or s["site"])
        if s["notes"] or not good: unresolved.append(s)
    for p, i, vt, fn, note in HAND:
        e = msgs[(p, i)]
        if vt: e["classes"].add(vt)
        else: e["inline"] = True
        e["senders"].add(fn)
        if note: e["notes"].append(note)

    rows = []
    for (p, i), e in sorted(msgs.items()):
        r = {"protocol": p, "id": i, "classes": [], "senders": sorted(e["senders"]), "notes": e["notes"]}
        for vt in sorted(e["classes"]):
            w = slot(vt, 1)
            c = {"vtable": vt, "write": hex(w), "s2c": s2c.get(vt, [])}
            wl = writer(w)
            if wl: c["fixed"], c["exact"] = wl
            r["classes"].append(c)
        if e.get("inline"): r["inline"] = True
        lens = {(c.get("fixed"), c.get("exact")) for c in r["classes"]}
        if p == "instance" and r.get("inline") and not r["classes"]: lens = {(2, True)}  # 0x85: the id alone
        r["length"] = next(iter(lens))[0] if len(lens) == 1 and next(iter(lens))[1] else None
        rows.append(r)

    # Validation against the Gateway's hand table.
    gw = gateway_lengths()
    inst = {r["id"]: r for r in rows if r["protocol"] == "instance"}
    agree, bad, missing, nolen = 0, [], [], []
    for i, n in sorted(gw.items()):
        r = inst.get(i)
        if r is None: missing.append(i); continue
        if r["length"] is None: nolen.append(i); continue
        if r["length"] == n: agree += 1
        else: bad.append((i, n, r["length"]))

    L = ["# Client → server messages (generated)", "",
         "Generated by `scripts/re/ghidra/SendSites.java` and `python -I scripts/re/c2s_md.py`. Do not edit by hand.",
         "The S→C side is in [schema.md](schema.md).", "",
         "## How the list is complete", "",
         "Every message the client sends goes through the stream flush `0x140dfb8c0` (Winsock `send`). Its callers are the",
         "senders: instance messages go through `0x14216e940` (it calls the message's vtable slot 1, the writer, on",
         "`instance+0xc288`); a few senders write the id inline or call a writer directly. `SendSites.java` resolves every",
         "`0x14216e940` call over the control-flow graph (Ghidra's resolved jump tables included): from the call it walks",
         "backwards path by path to the stores of the message's vtable (`[slot]`) and u16 id (`[slot+8]`), so ids set in",
         "different branches or switch cases before one shared send each keep their own path's vtable. Constructor-built",
         "messages are read from the constructor's stores. The remaining senders (no `0x14216e940` call, or a site the",
         "script cannot resolve) were read by hand and are listed in `HAND` in `scripts/re/c2s_md.py`.", "",
         f"`0x14216e940` call sites: {len(sites)}, resolved {len(sites) - len(unresolved)}. Unresolved, covered by `HAND` or noted:", ""]
    for s in unresolved:
        L.append(f"- `{s['site']}` in " + (f"`{s['func']}`" if s["func"] else "no Ghidra function") + ": "
                 + f"{'; '.join(s['notes']) or 'no pair'}"
                 + (f" (also resolved: {', '.join(p['id'] for p in s['pairs'] if p['id'] and p['vtable'])})" if any(p['id'] and p['vtable'] for p in s['pairs']) else ""))
    L += ["", "A class shared with an S→C message (same vtable) is named in the S→C column: the client also builds some",
          "S→C classes locally (S→C instance 0xc0 is fed to the client's own handler through `0x141d625f0`; that is not a",
          "send and is not listed here).", "",
          "**Length** is the whole message in bytes, id included, when the writer is exact (only fixed-size stream writes,",
          "no branch, no call that takes the stream). `≥ N` = N fixed bytes plus variable or conditional parts.", "",
          "## Validation against the Gateway", "",
          f"`InstanceProtocol.KnownClientLength` has {len(gw)} fixed lengths: {agree} agree with an exact extracted length, "
          f"{len(bad)} disagree" + (": " + ", ".join(f"{i:#x} (Gateway {n}, extracted {x})" for i, n, x in bad) if bad else "")
          + (f"; {len(missing)} not found: " + ", ".join(f"{i:#x}" for i in missing) if missing else "")
          + (f"; {len(nolen)} have no exact extracted length (branching or variable writers): " + ", ".join(f"{i:#x}" for i in nolen) if nolen else "") + ".", ""]
    for p, title, idw in (("instance", "Instance connection (u16 id, `instance+0xc288`)", 4),
                          ("login", "Login connection (u16 id, `login+0xdd0`; fields in [../re/009-login-messages.md](../re/009-login-messages.md))", 2),
                          ("patch", "Patch connection (u8 id, `patch+0x60`)", 2)):
        rs = [r for r in rows if r["protocol"] == p]
        L += [f"## {title}", "", f"{len(rs)} ids.", "", "| Id | Class / writer | Length | S→C class | Senders | Notes |", "|---|---|---|---|---|---|"]
        for r in rs:
            cls = "<br>".join(f"`{c['vtable']}` / `{c['write']}`" for c in r["classes"]) or "inline"
            if r["length"] is not None: ln = str(r["length"])
            else:
                fx = [c["fixed"] for c in r["classes"] if "fixed" in c]
                ln = f"≥ {min(fx)}" if fx else ""
            # a generic class (one u16, one u32, ...) is shared by many S→C ids: give the count only
            sc = "<br>".join(", ".join(c["s2c"]) if len(c["s2c"]) <= 3 else f"generic ({len(c['s2c'])} S→C ids)"
                             for c in r["classes"] if c["s2c"])
            snd = ", ".join(f"`{x}`" for x in r["senders"][:3]) + (f" +{len(r['senders']) - 3}" if len(r["senders"]) > 3 else "")
            L.append(f"| {r['id']:#0{idw + 2}x} | {cls} | {ln} | {sc} | {snd} | {'; '.join(r['notes'])} |")
        L.append("")
    open(OUT, "w", encoding="utf-8", newline="\n").write("\n".join(L))
    for r in rows: r["id"] = hex(r["id"])
    json.dump(rows, open(OUT[:-3] + ".json", "w", encoding="utf-8", newline="\n"), ensure_ascii=False, indent=0)
    cnt = collections.Counter(r["protocol"] for r in rows)
    print(f"{OUT}: {dict(cnt)}; Gateway lengths {agree} agree, {len(bad)} disagree, {len(missing)} missing")
    return 1 if bad or missing else 0


if __name__ == "__main__":
    sys.exit(main())
