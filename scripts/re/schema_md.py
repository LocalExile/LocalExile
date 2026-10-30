# usage: schema_md.py [readschema.json]   -- ReadSchema.java output -> Agents/docs/protocol/schema.md
# Regenerate the input with (about 10 minutes):
#   bash scripts/re/ghidra/g.sh ReadSchema.java 'C:\...\Tools\re\out\readschema.json' 'C:\...\Agents\docs\re\components.json'
import json, os, re, sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SRC = sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, "Tools", "re", "out", "readschema.json")
OUT = os.path.join(REPO, "Agents", "docs", "protocol", "schema.md")
COMPONENTS = os.path.join(REPO, "Agents", "docs", "re", "components.json")
EXE = os.path.join(REPO, "Client", "Game.exe")
# The S→C factories: the instance connection's two groups, the login connection, and the patch connection (u8 ids).
FACTORIES = {"0x1421b5c40": "instance", "0x1421c6150": "world", "0x14219c350": "login", "0x1428f74d0": "patch"}

# Bytes consumed per component, traced live (Agents/docs/protocol/04 section 3 and 08 section 2). "min" is what the summary
# must give: the bytes read outside every condition and loop.
TRACED = [
    ("Positioned", "0x141e2f0b0", 16, 16, "player and exit"),
    ("Stats (ClientInternalStats 0x1435dd4e8)", "0x1421c2f40", 10, 10, "n = 0"),
    ("Buffs", "0x141e114d0", 2, 2, "n = 0"),
    ("Life", "0x141e6c2f0", 55, 55, ""),
    ("Animated", "0x141e56a80", 1, 1, "flags 0"),
    ("Player", "0x141e7dc10", 61, 45, "61 for \"DevWitch\" = 45 + 8 UTF-16 units of the name"),
    ("Inventories (slot 20)", "0x141e1ee30", 2, 2, "n = 0, n2 = 0"),
    ("Actor (slot 17)", "0x142192a80", 15, 15, "+ 2 in its slot 20 `0x1421932b0`, read through the vcall"),
    ("AreaTransition (ClientAreaTransition slot 17 → base)", "0x14166bb80", 5, 2,
     "2 + u16 of variant 2 (through the variant table, a vcall) + the compact int behind the type flag"),
    ("ObjectMagicProperties (slot 20)", "0x141e77990", 4, 4, "base part 3 + own flags 1"),
    ("MinimapIcon (slot 20)", "0x141e6f5d0", 1, 1, ""),
    ("InteractionAction", "0x141e1e240", 1, 1, ""),
    ("S→C 0x2b9 read", "0x141fe0520", 18, 16, "+ the blob16 (u16 length + bytes): 16 + 2"),
]


# The repo is public: summaries keep only our own notation, never the decompiled C that ReadSchema.java attaches to
# each line (the `// ...` comments, the conditions, the `in: ...` contexts). Tools/re/out keeps the full output.
C_TOKENS = re.compile(r"FUN_|param_|local_|[pu]?[a-z]Var\d|PTR_|LAB_|DAT_|\(\*|ulonglong|longlong|undefined")


def conditions(c):
    """The flag bits a condition tests, in our notation; "…" when it tests something else."""
    bits = [f"bit {int(m, 0):#x} {'set' if op == '!' else 'clear'}"
            for m, op in re.findall(r"& (0x[0-9a-fA-F]+|\d+)\)? ([!=])= 0", c)]
    if re.search(r"\(char\)\w+(\[0\])? < '\\0'", c):
        bits.append("bit 0x80 set")
    keys = [f'key "{k}"' for k in re.findall(r'"([A-Za-z_][\w ]*)"', c)]
    return ", ".join(bits + keys) or "…"


def clean_line(line):
    ind = line[:len(line) - len(line.lstrip())]
    t = line.strip()
    if t.startswith("//"):
        word = next((w for w in ("return", "break", "continue", "goto") if re.search(rf"\b{w}\b", t)), None)
        return None if word is None else ind + word
    if "    //" in t:
        t = t.split("    //")[0].rstrip()
    if " in: " in t:
        op, ctx = t.split(" in: ", 1)
        t = f"{op} (when {conditions(ctx)})"
    if t.startswith(("if ", "if(", "else if")):
        return ind + ("else if " if t.startswith("else") else "if ") + conditions(t) + " {"
    if t.startswith(("for ", "for(", "do ", "while")):
        return ind + "loop {"
    return ind + t


def clean(d):
    # Ghidra's default names are only addresses.
    addr = lambda x: re.sub(r"\bFUN_([0-9a-f]+)", r"0x\1", x)
    for m in d["messages"]:
        m["via"] = addr(m["via"])
    for s in d["summaries"].values():
        s["name"] = addr(s["name"])
        s["lines"] = [x for x in map(clean_line, s["lines"]) if x is not None]
        bad = [x for x in s["lines"] if C_TOKENS.search(x)]
        if bad:
            raise SystemExit(f"{s['func']}: decompiled code left in {bad[0]!r}")


def dispatch_cases():
    try:
        import pefile
        pe = pefile.PE(EXE, fast_load=True)
        data = open(EXE, "rb").read()
        off = pe.get_offset_from_rva(0x142154e14 - pe.OPTIONAL_HEADER.ImageBase)
        table = data[off:off + 0x2ed]
        default = max(set(table), key=table.count)
        return {i + 0xd: c for i, c in enumerate(table) if c != default}
    except Exception:
        return {}


def main():
    d = json.load(open(SRC, encoding="utf-8"))
    S = d["summaries"]
    clean(d)
    comps = json.load(open(COMPONENTS))["registrations"]
    cases = dispatch_cases()
    printed, queue = set(), []
    L = []

    def anchor(k):
        return "f-" + k.replace("@", "-")

    def block(k, title=None):
        s = S[k]
        printed.add(k)
        head = title or f"`{s['func']}`" + (f" (reader = arg {s['readerArg']})" if s["readerArg"] != 1 else "")
        out = [f"<a id=\"{anchor(k)}\"></a>", "", f"**{head}** — min {s['min']}{'+' if s['minIsLowerBound'] else ''} bytes"
               + (" (no reads)" if not s["reads"] else "") + (" — summary failed" if s["failed"] else ""), ""]
        if s["reads"] or s["failed"]:
            out += ["```text"] + s["lines"] + ["```", ""]
        subs = [x for x in s["subs"] if x in S and (S[x]["reads"] or S[x]["failed"])]
        if subs:
            out.append("Calls: " + ", ".join(f"[`{x}`](#{anchor(x)})" for x in subs))
            out.append("")
            queue.extend(subs)
        return out

    L += ["# Read sequences: components and messages (generated)", "",
          "Generated by `scripts/re/ghidra/ReadSchema.java` and `python -I scripts/re/schema_md.py`. Do not edit by hand.",
          "Inputs: [../re/components.json](../re/components.json) (the component registry, [../re/components.md](../re/components.md))",
          "and the S→C factories `0x1421b5c40` (instance), `0x1421c6150` (world), `0x14219c350` (login) and `0x1428f74d0`",
          "(patch). Each factory's message list is complete: its ids are exactly the factory switch's explicit cases.", "",
          "## How to read this", "",
          "Each summary keeps the reads and the conditions and loops around them, in the notation below; everything else is",
          "dropped. The decompiled C itself is not published (it stays in `Tools/re/out/readschema.json`): a condition shows",
          "only the flag bits or string keys it tests, `…` for anything else. Callee parameters were committed before",
          "decompiling, so readers passed in a register that a stored prototype left out (ObjectMagicProperties' base part,",
          "the entity header of messages) are visible.", "",
          "| Notation | Meaning |", "|---|---|",
          "| `u8` `u16` `u32` `u64` `bytes[N]` | inline read from the component blob reader `{data, size, pos}` (little-endian): `pos += N` |",
          "| `→ +0xNN` | the value lands in that field of the object (`this`); `(flags)` = it is only tested |",
          "| `bytes[var]` | a variable-length read (`pos += n`) |",
          "| `rawN` | `0x140dfb630` stream read / `0x140e28cf0` blob memcpy of N bytes (messages: network order where the code `ntoh`s it) |",
          "| `raw.wN`, `cint.w`, `blob16.w` | the write side (`0x140dfb760`, `0x141fa3c50`, `0x141fa44f0`) |",
          "| `cint` | compact int (`0x141e61010` / `0x14089cad0` / `0x141fa3d60`): first byte < 0x80 is the value, `10xxxxxx` 2 bytes, `110xxxxx` 3, … |",
          "| `wstr` | `0x140e0cbe0` wide string; `str` = `0x1408402a0` narrow string; `blob16` = `0x141fa4610` u16 BE length + bytes |",
          "| `sub 0x…` | a call that passes the reader on (its own summary below; \"reader arg k\" when it is not the 2nd argument) |",
          "| `vcall [vt+0xNN]` | an indirect call with the reader (slot 20 is `[vt+0xa0]`) |",
          "| `if bit 0x4 set {`, `if key \"name\" {`, `if … {` | a condition: the flag bit tested (of the last flags read), the string key compared, or something else |",
          "| `loop {` | a `for`/`while`/`do` loop (its count is usually the last value read) |",
          "| `(when …)` | the read happens inside a condition expression |",
          "| bounds checks | `if (size < pos + N) throw` blocks are flattened away |", "",
          "**min** is the byte count of the reads outside every condition and loop, callees included; `+` means a",
          "variable or indirect read also happens unconditionally, so the real minimum is higher.", "",
          "Limits: a summary is only as good as the decompiler's structure. Reads hidden behind function pointers (variant",
          "tables, vtables) show as `vcall`. Field destinations (`→ +0x…`) are found for direct stores only.", "",
          "## Validation against the traced objects", "",
          "| Component / message | Function | Traced bytes | Expected min | Generated min | |", "|---|---|---|---|---|---|"]
    allok = True
    for name, fn, traced, exp, note in TRACED:
        s = S.get(fn)
        got = "—" if s is None else f"{s['min']}{'+' if s['minIsLowerBound'] else ''}"
        ok = s is not None and s["min"] == exp
        allok &= ok
        L.append(f"| {name} | [`{fn}`](#{anchor(fn)}) | {traced} | {exp} | {got} | {'✓' if ok else '✗'} {note} |")
    L += ["", "All match." if allok else "**Some do not match** (see ✗).", ""]

    # Components.
    L += ["## Component deserializers", "",
          "Primary vtables only (the factory's own vtable and its subclasses, see [../re/components.md](../re/components.md)).",
          "Slot 17 is what `0x141ebe190` calls; a slot 17 that is a bare `ret` reads nothing and is omitted. Object types and",
          "their component order: [../re/object-types.md](../re/object-types.md).", ""]
    by_fn = {}
    for c in d["components"]:
        by_fn.setdefault(c["summary"], []).append(c["label"])
    order = {}
    for r in comps:
        order.setdefault(r["name"], r["typeId"] if r["typeId"] >= 0 else 999)
    quiet = []
    for k in sorted(by_fn, key=lambda k: min((order.get(l.split()[0], 999), l) for l in by_fn[k])):
        labels = sorted(set(by_fn[k]))
        if not S[k]["reads"] and not S[k]["failed"]:
            quiet.append(f"`{S[k]['func']}` ({'; '.join(labels)})")
            continue
        L += block(k, f"`{S[k]['func']}` — " + "; ".join(labels))
    if quiet:
        L += ["Deserializers that read nothing (besides bare `ret` slots): " + ", ".join(quiet) + ".", ""]

    # Messages.
    for fac, kind in FACTORIES.items():
        msgs = [m for m in d["messages"] if m["factory"] == fac and m["vtable"]]
        skipped = [m for m in d["messages"] if m["factory"] == fac and not m["vtable"]]
        L += [f"## S→C messages: {kind} group (factory `{fac}`)", "",
              f"{len(msgs)} ids with a message class. {len(skipped)} switch labels create nothing (the default case)"
              + (", or a class this script could not resolve: " + ", ".join(f"{m['id']} ({m['via']})" for m in skipped if m["via"] != "no vtable (calls [])")
                 if any(m["via"] != "no vtable (calls [])" for m in skipped) else "") + ".",
              "Read = vtable slot 2 (what the client does on receive), write = slot 1." +
              (" Dispatcher case: `scripts/re/dispatch_ids.py`." if kind == "instance" else ""), ""]
        for m in sorted(msgs, key=lambda m: int(m["id"], 16)):
            idn = int(m["id"], 16)
            case = f", dispatcher case {cases[idn]:#x}" if kind == "instance" and idn in cases else ""
            L.append(f"### {m['id']}")
            L.append("")
            L.append(f"Class `{m['vtable']}` ({m['via']}{case}), read `{m['read']}`, write `{m['write']}`.")
            L.append("")
            if m["readSummary"] in S:
                L += block(m["readSummary"], f"read `{m['read']}`")
            if m["writeSummary"] in S and S[m["writeSummary"]]["reads"]:
                w = S[m["writeSummary"]]
                L += ["<details><summary>write " + f"<code>{m['write']}</code> (min {w['min']})</summary>", "", "```text"]
                L += w["lines"] + ["```", "", "</details>", ""]
                queue.extend(x for x in w["subs"] if x in S and S[x]["reads"])

    # Helpers.
    L += ["## Helpers (read functions called with the reader)", ""]
    while queue:
        k = queue.pop(0)
        if k not in printed:
            L += block(k)
    open(OUT, "w", encoding="utf-8", newline="\n").write("\n".join(L))
    json.dump(d, open(OUT[:-3] + ".json", "w", encoding="utf-8", newline="\n"), ensure_ascii=False, indent=0)
    print(f"{OUT}: {len(L)} lines, {len(printed)} summaries; validation {'all match' if allok else 'MISMATCH'}")
    return 0 if allok else 1


if __name__ == "__main__":
    sys.exit(main())
