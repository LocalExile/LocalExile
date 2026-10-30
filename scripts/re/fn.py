# usage: python -I scripts/re/fn.py <address>… [--callers] [--callees] [--show]
#        python -I scripts/re/fn.py --rebuild
# Where an address of Client/Game.exe lives: the function that contains it (exact bounds from the PE's exception
# table, .pdata), its decompiled file under Tools/re/out/decomp-all, and with --callers / --callees the functions
# that name it / that it names in the decompile. --show prints the function's decompiled code.
# The index (Tools/re/out/fn-index.json, local like the decompile) is built on first use (~1 min) and rebuilt when
# Game.exe or the decompile is newer.
import bisect, json, os, re, struct, sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = os.path.join(ROOT, "Client", "Game.exe")
DECOMP = os.path.join(ROOT, "Tools", "re", "out", "decomp-all")
INDEX = os.path.join(ROOT, "Tools", "re", "out", "fn-index.json")
NAME = re.compile(r"\bFUN_([0-9a-f]{9})\b")


def build():
    import pefile
    pe = pefile.PE(EXE, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    # .pdata: RUNTIME_FUNCTION entries (begin RVA, end RVA, unwind info RVA), sorted by begin.
    d = pe.OPTIONAL_HEADER.DATA_DIRECTORY[3]
    raw = pe.get_data(d.VirtualAddress, d.Size)
    spans = {}
    for i in range(0, len(raw) - 11, 12):
        b, e, _ = struct.unpack_from("<III", raw, i)
        if b == 0: continue
        spans.setdefault(base + b, base + e)
    # Chained unwind entries split one function into several ranges; the decompile names the function's entry.
    files, callees = {}, {}
    for sub in os.listdir(DECOMP):
        folder = os.path.join(DECOMP, sub)
        if not os.path.isdir(folder): continue
        for f in os.listdir(folder):
            m = re.fullmatch(r"FUN_([0-9a-f]{9})\.c", f)
            if not m: continue
            addr = int(m.group(1), 16)
            files[addr] = os.path.relpath(os.path.join(folder, f), ROOT)
            with open(os.path.join(folder, f), encoding="utf-8", errors="replace") as h:
                names = {int(x, 16) for x in NAME.findall(h.read())}
            names.discard(addr)
            callees[addr] = sorted(names)
    pdata = sorted(spans)
    index = {
        "built": [os.path.getmtime(EXE), max(os.path.getmtime(os.path.join(DECOMP, s)) for s in os.listdir(DECOMP))],
        "pdata": pdata,
        "pdataEnds": [spans[s] for s in pdata],
        "entries": sorted(files),
        "files": {f"{a:x}": p for a, p in files.items()},
        "callees": {f"{a:x}": [f"{c:x}" for c in cs] for a, cs in callees.items() if cs},
    }
    with open(INDEX, "w", encoding="utf-8") as h:
        json.dump(index, h)
    return index


def load():
    if os.path.exists(INDEX):
        with open(INDEX, encoding="utf-8") as h:
            index = json.load(h)
        newest = [os.path.getmtime(EXE), max(os.path.getmtime(os.path.join(DECOMP, s)) for s in os.listdir(DECOMP))]
        if index.get("built") == newest: return index
    print("building the index (Tools/re/out/fn-index.json)…", file=sys.stderr)
    return build()


def containing(index, addr):
    """(function entry, end or None, note): the .pdata range holding the address; when no decompiled file starts
    there (a range split off a function, or a leaf function without unwind data), the nearest decompiled entry below."""
    pdata, ends = index["pdata"], index["pdataEnds"]
    i = bisect.bisect_right(pdata, addr) - 1
    if i >= 0 and addr < ends[i]:
        if f"{pdata[i]:x}" in index["files"]: return pdata[i], ends[i], ""
        note = f" (inside .pdata range 0x{pdata[i]:x}-0x{ends[i]:x}, which no decompiled file starts)"
    else:
        note = " (in no .pdata range)"
    entries = index["entries"]
    j = bisect.bisect_right(entries, addr) - 1
    return (entries[j], None, note + ", nearest decompiled entry below") if j >= 0 else (None, None, note)


def main(argv):
    if "--rebuild" in argv:
        build(); print("rebuilt"); return
    index = load()
    callers_of = None
    for a in [x for x in argv if not x.startswith("--")]:
        addr = int(a.removeprefix("FUN_"), 16)
        start, end, note = containing(index, addr)
        if start is None: print(f"0x{addr:x}: not in any function{note}"); continue
        key = f"{start:x}"
        path = index["files"].get(key)
        where = f"+0x{addr - start:x}" if addr != start else "entry"
        print(f"0x{addr:x}: FUN_{key} {where}" + (f" (ends 0x{end:x})" if end else "") + note + (f"  {path}" if path else ""))
        if "--callees" in argv:
            print("  calls: " + " ".join("FUN_" + c for c in index["callees"].get(key, [])))
        if "--callers" in argv:
            if callers_of is None:
                callers_of = {}
                for f, cs in index["callees"].items():
                    for c in cs: callers_of.setdefault(c, []).append(f)
            print("  named by: " + " ".join("FUN_" + c for c in sorted(callers_of.get(key, []))))
        if "--show" in argv and path:
            with open(os.path.join(ROOT, path), encoding="utf-8", errors="replace") as h:
                print(h.read())


if __name__ == "__main__":
    main(sys.argv[1:])
