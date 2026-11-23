"""Port a whole-client decompile to another build whose functions sit at the same addresses (a hotfix, a letter patch).

Usage: python -I scripts/re/port/port_build.py <old exe> <new exe> <old game dir> <new game dir> [--index-only]

<old game dir> is overnight.ps1's Tools/re/out/full/game for <old exe>. The new build must pass the first two checks of
compare_builds.py (same sections, every .pdata function at the same start and end); anything else needs a port
(tooling.md section 11). Code addresses then stay; only data (strings, tables, vtables, globals) moves, and every data
address in the index, the decompile, the disassembly and the tables is rewritten through a map proven from the bytes:

1. an instruction: the same instruction in both builds whose RIP-relative or image-relative operand is the only change,
   for every function of the index (leaf functions with no .pdata entry included), each old address naming one new one;
2. its neighbours: the nearest proven addresses below and above moved by the same amount, and its 8 bytes are equal there;
3. its bytes: its first 64, 48 or 32 bytes (not all zero) found once in the new build's data.

An address none of these settles keeps its old value and is listed in <new game dir>/port-unresolved.tsv (and the
index's port_unresolved table). A function whose code changed in another way keeps the old build's C, marked on its first
line, and is listed in port-changed.tsv. Nothing is guessed. Needs capstone. Read-only on its inputs.
"""
import bisect
import collections
import os
import re
import shutil
import sqlite3
import struct
import sys
import time

import capstone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from compare_builds import Image  # noqa: E402

# A 14xxxxxxx address token: bare, after 0x, or glued after an underscore (DAT_, PTR_FUN_, s_text_).
TOKEN = re.compile(r"(?:(?<=_)|(?<=0x)|(?<=0X)|(?<![0-9A-Za-z_]))(14[0-9a-fA-F]{7})(?![0-9A-Za-z])")


def log(*parts):
    print(time.strftime("%H:%M:%S"), *parts, flush=True)


class DataMap:
    def __init__(self, old, new, functions):
        self.old, self.new = old, new
        _, t_rva, _, t_raw, t_size = old.sections[0]
        self.t_rva = t_rva
        a, b = old.bytes[t_raw:t_raw + t_size], new.bytes[t_raw:t_raw + t_size]
        self.lo = old.base + old.sections[1][1]
        self.hi = old.base + old.sections[2][1] + old.sections[2][2]
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        md.detail = False
        number = re.compile(r"0x[0-9a-f]+")
        rip = re.compile(r"rip [+-] (0x[0-9a-f]+)")
        pairs = collections.defaultdict(set)
        self.changed = []
        for begin, size in functions:
            lo, hi = begin - old.base - t_rva, begin - old.base - t_rva + size
            if lo < 0 or hi > len(a):
                continue
            if a[lo:hi] == b[lo:hi]:
                # Unchanged bytes: its RIP-relative targets moved only if... they cannot: same bytes, same place.
                for addr, sz, _, ops in md.disasm_lite(a[lo:hi], begin):
                    m = rip.search(ops)
                    if m:
                        t = self._rip_target(addr, sz, ops)
                        if self.lo <= t < self.hi:
                            pairs[t].add(t)
                continue
            ia = list(md.disasm_lite(a[lo:hi], begin))
            ib = list(md.disasm_lite(b[lo:hi], begin))
            if [(x[0], x[1]) for x in ia] != [(x[0], x[1]) for x in ib]:
                self.changed.append((begin, "the instructions do not line up"))
                continue
            other = []
            for (addr, sz, m1, o1), (_, _, m2, o2) in zip(ia, ib):
                if rip.search(o1):
                    t1, t2 = self._rip_target(addr, sz, o1), self._rip_target(addr, sz, o2)
                    if self.lo <= t1 < self.hi and m1 == m2 and number.sub("N", o1) == number.sub("N", o2):
                        rest1 = [x for x in number.findall(o1) if x != rip.search(o1).group(1)]
                        rest2 = [x for x in number.findall(o2) if x != rip.search(o2).group(1)]
                        if rest1 == rest2:
                            pairs[t1].add(t2)
                            continue
                if o1 == o2 and m1 == m2:
                    continue
                n1, n2 = number.findall(o1), number.findall(o2)
                diff = [(x, y) for x, y in zip(n1, n2) if x != y]
                if (m1 == m2 and number.sub("N", o1) == number.sub("N", o2) and len(diff) == 1 and "[" in o1
                        and all(self.lo <= old.base + int(v, 16) < self.hi for v in diff[0])):
                    pairs[old.base + int(diff[0][0], 16)].add(old.base + int(diff[0][1], 16))
                    continue
                other.append(f"{addr:#x} {m1} {o1} -> {m2} {o2}")
            if other:
                self.changed.append((begin, "; ".join(other[:4])))
        self.exact = {k: next(iter(v)) for k, v in pairs.items() if len(v) == 1}
        # One old address the new build's instructions split over several (a constant the old link folded): no single
        # answer, so no fallback either. A reference from one instruction still gets its own (port_index, datarefs).
        self.split = {k for k, v in pairs.items() if len(v) > 1}
        self.anchors = sorted(self.exact.items())
        self.keys = [k for k, _ in self.anchors]
        self.cache, self.how = {}, collections.Counter()

    def _rip_target(self, addr, size, ops):
        m = re.search(r"rip ([+-]) (0x[0-9a-f]+)", ops)
        d = int(m.group(2), 16)
        return addr + size + (d if m.group(1) == "+" else -d)

    def _read(self, image, va, n):
        try:
            return image.at_va(va, n)
        except ValueError:
            return b""

    def _once(self, needle):
        """Where the needle sits in the new build's data, when it sits there exactly once. Every 8-byte word at every
        offset is indexed once (sorted), so a lookup checks only the offsets whose word equals the needle's rarest one."""
        if not hasattr(self, "_words"):
            import numpy as np
            self._np = np
            self._words = []
            for _, rva, _, raw, rsize in self.new.sections[1:3]:
                blob = self.new.bytes[raw:raw + rsize]
                arr = np.frombuffer(blob, dtype=np.uint8)
                view = np.lib.stride_tricks.sliding_window_view(arr, 8)
                words = np.ascontiguousarray(view).view("<u8").ravel()
                order = np.argsort(words, kind="stable")
                self._words.append((blob, self.new.base + rva, words[order], order))
        np = self._np
        hits = []
        for blob, va0, keys, order in self._words:
            best = None
            for k in range(0, len(needle) - 7):
                w = np.frombuffer(needle[k:k + 8], dtype="<u8")[0]
                lo, hi = np.searchsorted(keys, w, "left"), np.searchsorted(keys, w, "right")
                if best is None or hi - lo < best[2] - best[1]:
                    best = (k, lo, hi)
                if hi - lo <= 2:
                    break
            k, lo, hi = best
            for p in order[lo:hi]:
                start = int(p) - k
                if start >= 0 and blob[start:start + len(needle)] == needle:
                    hits.append(va0 + start)
                    if len(hits) > 1:
                        return None
        return hits[0] if len(hits) == 1 else None

    def get(self, va):
        """The new address of an old data address, or None when nothing proves one."""
        if va in self.cache:
            return self.cache[va]
        there, why = self.exact.get(va), "an instruction"
        if va in self.split:
            self.cache[va] = None
            self.how["split by the new build"] += 1
            return None
        if there is None:
            i = bisect.bisect_left(self.keys, va)
            if 0 < i < len(self.anchors):
                (k0, v0), (k1, v1) = self.anchors[i - 1], self.anchors[i]
                if v0 - k0 == v1 - k1 and self._read(self.old, va, 8) == self._read(self.new, va + v0 - k0, 8):
                    there, why = va + v0 - k0, "its neighbours"
        if there is None:
            for n in (64, 48, 32):
                chunk = self._read(self.old, va, n)
                if len(chunk) == n and any(chunk):
                    hit = self._once(chunk)
                    if hit is not None:
                        there, why = hit, "its bytes"
                        break
        self.cache[va] = there
        self.how[why if there is not None else "not settled"] += 1
        return there

    def is_data(self, va):
        return self.lo <= va < self.hi


def rewrite_text(text, dm, unresolved, where):
    def sub(m):
        token = m.group(1)
        va = int(token, 16)
        if not dm.is_data(va):
            return token
        there = dm.get(va)
        if there is None:
            unresolved[(where, f"{va:x}")] += 1
            return token
        out = f"{there:x}"
        return out.upper() if token.isupper() and any(c.isalpha() for c in token) else out
    return TOKEN.sub(sub, text)


def port_index(src, dst, dm, unresolved, build_note):
    shutil.copyfile(src, dst)
    c = sqlite3.connect(dst)
    tables = [r[0] for r in c.execute("select name from sqlite_master where type='table'")]
    for t in tables:
        cols = [r[1] for r in c.execute(f'pragma table_info("{t}")')]
        rows = c.execute(f'select rowid, {", ".join(chr(34) + x + chr(34) for x in cols)} from "{t}"').fetchall()
        updates = []
        for row in rows:
            new = [rewrite_text(v, dm, unresolved, f"index.{t}.{cols[i]}") if isinstance(v, str) else v
                   for i, v in enumerate(row[1:])]
            if new != list(row[1:]):
                updates.append((*new, row[0]))
        if updates:
            c.executemany(f'update "{t}" set {", ".join(chr(34) + x + chr(34) + "=?" for x in cols)} where rowid=?', updates)
        log(f"index {t}: {len(rows)} rows, {len(updates)} rewritten")
    # A reference from an instruction: that instruction in the new build names its target exactly, which also settles
    # an address the new build split. Only where the old instruction named the old target, so Ghidra's other kinds stay.
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)

    def target(image, va):
        code = dm._read(image, va, 16)
        ins = next(md.disasm_lite(code, va), None) if code else None
        m = re.search(r"rip ([+-]) (0x[0-9a-f]+)", ins[3]) if ins else None
        return dm._rip_target(ins[0], ins[1], ins[3]) if m else None

    src_refs = dict(((r, (f, t)) for r, f, t in sqlite3.connect(f"file:{src}?mode=ro", uri=True)
                     .execute("select rowid, from_addr, to_addr from datarefs")))
    fixes = []
    for r, (f, t) in src_refs.items():
        if f.startswith("EXT") or t.startswith("EXT") or not dm.is_data(int(t, 16)):
            continue
        if target(dm.old, int(f, 16)) != int(t, 16):
            continue
        there = target(dm.new, int(f, 16))
        if there is not None:
            fixes.append((f"{there:x}", r))
            unresolved.pop(("index.datarefs.to_addr", t), None)
    c.executemany("update datarefs set to_addr=? where rowid=?", fixes)
    log(f"index datarefs: {len(fixes)} targets read from the new build's own instructions")
    # A string's text can change at the same place: the build number, and the per-compile hash in anonymous-namespace
    # names (?A0x...). Where the old text is exactly the old build's bytes, the new build's bytes there replace it.
    old_rows = dict(((r, (a, ty, v)) for r, a, ty, v in sqlite3.connect(f"file:{src}?mode=ro", uri=True)
                     .execute("select rowid, addr, type, value from strings")))
    texts = []
    for r, a, ty, v in c.execute("select rowid, addr, type, value from strings").fetchall():
        oa, _, ov = old_rows[r]
        if dm.is_data(int(oa, 16)) and dm.get(int(oa, 16)) is None:
            continue  # unresolved: kept, and listed
        wide = bool(ty) and "unicode" in ty
        want = ov.encode("utf-16-le" if wide else "utf-8", "surrogatepass" if wide else "surrogateescape")
        if not want or dm._read(dm.old, int(oa, 16), len(want)) != want:
            continue  # Ghidra's text is not the raw bytes (escapes): leave it
        if dm._read(dm.new, int(a, 16), len(want)) == want:
            continue
        raw, step = dm._read(dm.new, int(a, 16), 4096), 2 if wide else 1
        end = next((i for i in range(0, len(raw) - step + 1, step) if raw[i:i + step] == b"\0" * step), len(raw))
        texts.append((raw[:end].decode("utf-16-le" if wide else "utf-8", "replace"), r))
    c.executemany("update strings set value=? where rowid=?", texts)
    log(f"index strings: {len(texts)} texts changed in the new build, re-read from it")
    c.execute("create table port_unresolved (place, old_address, count)")
    c.executemany("insert into port_unresolved values (?,?,?)", [(w, a, n) for (w, a), n in sorted(unresolved.items())])
    c.execute("create table port_changed (function, change)")
    c.executemany("insert into port_changed values (?,?)", [(f"{f:x}", why) for f, why in dm.changed])
    c.execute("create table build_info (key, value)")
    c.executemany("insert into build_info values (?,?)", build_note)
    c.commit()
    c.execute("vacuum")
    c.close()


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    index_only = "--index-only" in sys.argv
    old_p, new_p, src, dst = args
    old, new = Image(old_p), Image(new_p)
    if [(s[0], s[1], s[3], s[4]) for s in old.sections] != [(s[0], s[1], s[3], s[4]) for s in new.sections] \
            or old.functions != new.functions:
        print("different code layout: this build needs a port (tooling.md section 11), not port_build.py")
        return 1
    os.makedirs(dst, exist_ok=True)
    idx = sqlite3.connect(f"file:{os.path.join(src, 'index.sqlite')}?mode=ro", uri=True)
    functions = [(int(a, 16), int(s or 0)) for a, s in idx.execute("select addr,size from functions") if int(s or 0) > 0]
    idx.close()
    log(f"mapping data from {len(functions)} functions")
    dm = DataMap(old, new, functions)
    moved = sum(1 for k, v in dm.exact.items() if k != v)
    log(f"{len(dm.exact)} data addresses proven by an instruction, {moved} moved; {len(dm.changed)} functions changed otherwise")

    unresolved = collections.Counter()
    note = [("old_sha256", old.sha256), ("new_sha256", new.sha256), ("source", "port_build.py from the old build's index"),
            ("rule", "code addresses unchanged; data addresses rewritten where proven, else kept and listed in port_unresolved")]
    port_index(os.path.join(src, "index.sqlite"), os.path.join(dst, "index.sqlite"), dm, unresolved, note)

    if not index_only:
        changed = {f for f, _ in dm.changed}
        for root, _, names in os.walk(src):
            rel = os.path.relpath(root, src)
            os.makedirs(os.path.join(dst, rel), exist_ok=True)
            for n in names:
                if n == "index.sqlite":
                    continue
                s, d = os.path.join(root, n), os.path.join(dst, rel, n)
                if not n.endswith((".c", ".asm", ".tsv", ".h", ".txt")):
                    shutil.copyfile(s, d)
                    continue
                text = open(s, encoding="utf-8", errors="surrogateescape", newline="").read()
                out = rewrite_text(text, dm, unresolved, n.rsplit(".", 1)[-1])
                if n.endswith(".c") and n.startswith("FUN_"):
                    try:
                        if int(n[4:-2], 16) in changed:
                            out = f"// port_build.py: this function's code changed in {new.sha256[:12]}; this C is {old.sha256[:12]}'s\n" + out
                    except ValueError:
                        pass
                open(d, "w", encoding="utf-8", errors="surrogateescape", newline="").write(out)
            log(f"{rel}: {len(names)} files")

    with open(os.path.join(dst, "port-unresolved.tsv"), "w", encoding="utf-8") as f:
        for (w, a), n in sorted(unresolved.items()):
            f.write(f"{w}\t{a}\t{n}\n")
    with open(os.path.join(dst, "port-changed.tsv"), "w", encoding="utf-8") as f:
        for fn, why in dm.changed:
            f.write(f"{fn:x}\t{why}\n")
    log(f"settled: {dict(dm.how)}; unresolved distinct addresses: {len({a for _, a in unresolved})}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
