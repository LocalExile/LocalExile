"""Does a new client executable hold the same code as one LocalExile already runs?

Usage: python -I scripts/re/port/compare_builds.py <allowed PathOfExile.exe> <new PathOfExile.exe> <mask VA of the allowed one>
       python -I scripts/re/port/compare_builds.py <3.29.3c release exe> <new exe> --data-map <script.js>...

Needs capstone (pip install capstone). Read-only. A build earns a row in ClientBuilds (src/GameServer.Core/Protocol)
only when this ends in "same code" and compare_tables.py finds no difference (Agents/docs/re/tooling.md section 11).

It checks, in this order:
- the sections and every .pdata function start and end are equal (no function moved, grew or shrank);
- every changed instruction of those functions is a reference to data that moved: a RIP-relative operand, or an
  image-relative displacement into .rdata/.data (table[base + index*8 + rva]). Anything else is listed: a changed
  immediate or opcode is a changed rule, message id or structure, and needs a person;
- the changed bytes outside .pdata functions (leaf functions have no unwind data) read as moved references too;
- what LocalExile takes from the executable: the login key header found once, the two LooseFilesPatch sites
  unchanged, and where the HNC mask is now (the new row's HncMaskVa).

--data-map prints, for each script, the rows of its MOVED table for the new build: where every data address the script
uses in code sits there, also the ones that stay, so that an address missing from the table is one nobody checked.
The scripts' addresses are the 3.29.3c release's, so that executable is the first argument.
An address is settled by, in this order: every instruction that refers to it naming one new address; its bytes being
found once in the new build's data; the nearest referenced data below and above having moved by the same amount, with
equal bytes there. What none of these settles is listed and left out.
"""
import bisect
import hashlib
import re
import struct
import sys
from collections import Counter

import capstone

# The same values as LoginKeys.cs (DerPrefix), Hnc.cs (MaskSha256) and LooseFilesPatch.cs (Sites).
DER_PREFIX = bytes.fromhex("308203423082023506072a8648ce380401")
MASK_SHA256 = "0e88f29acc9cab88ee400ca78bbfeeeaebc333afb11f1ea9be3fd37aeb7cbc31"
LOOSE_SITES = [(0x1400FC426, bytes.fromhex("0f852b010000")), (0x1428F3221, bytes.fromhex("32c0"))]


class Image:
    def __init__(self, path):
        self.bytes = open(path, "rb").read()
        b = self.bytes
        pe = struct.unpack_from("<I", b, 0x3C)[0]
        count = struct.unpack_from("<H", b, pe + 6)[0]
        optional = pe + 24
        self.base = struct.unpack_from("<Q", b, optional + 24)[0]
        at = optional + struct.unpack_from("<H", b, pe + 20)[0]
        self.sections = []
        for _ in range(count):
            name = b[at:at + 8].rstrip(b"\0").decode("ascii", "replace")
            vsize, rva, rsize, raw = struct.unpack_from("<IIII", b, at + 8)
            self.sections.append((name, rva, vsize, raw, rsize))
            at += 40
        exc_rva, exc_size = struct.unpack_from("<II", b, optional + 112 + 3 * 8)
        start = self.offset(exc_rva)
        self.functions = sorted(struct.unpack_from("<II", b, start + i * 12) for i in range(exc_size // 12))
        self.sha256 = hashlib.sha256(b).hexdigest()

    def offset(self, rva):
        for _, start, vsize, raw, rsize in self.sections:
            if start <= rva < start + max(vsize, rsize):
                return raw + rva - start
        raise ValueError(f"RVA {rva:#x} is not in the file")

    def at_va(self, va, length):
        o = self.offset(va - self.base)
        return self.bytes[o:o + length]

    def va_of(self, offset):
        for _, start, _, raw, rsize in self.sections:
            if raw <= offset < raw + rsize:
                return self.base + start + offset - raw
        return None


def data_map(old, new, scripts):
    """The MOVED rows of each script for the new build (see the module text)."""
    _, t_rva, _, t_raw, t_size = old.sections[0]
    a, b = old.bytes[t_raw:t_raw + t_size], new.bytes[t_raw:t_raw + t_size]
    data_lo, data_hi = old.base + old.sections[1][1], old.base + old.sections[2][1] + old.sections[2][2]
    literal = re.compile(r"0x14[0-9a-fA-F]{7}\b")
    wanted = {}
    for path in scripts:
        found, in_table = set(), False
        for line in open(path, encoding="utf-8"):
            # The script's own MOVED table holds the answers of an earlier run, not addresses it uses.
            in_table = in_table or line.startswith("const MOVED")
            if in_table:
                in_table = not line.rstrip().endswith("};")
                continue
            if line.lstrip().startswith(("//", "*")):
                continue
            cut = line.find("//")
            found |= {int(m.group(), 16) for m in literal.finditer(line) if not 0 <= cut < m.start()}
        wanted[path] = sorted(x for x in found if data_lo <= x < data_hi)

    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    rip = re.compile(r"rip ([+-]) (0x[0-9a-f]+)")
    refs = {}
    for begin, end in old.functions:
        lo, hi = begin - t_rva, end - t_rva
        for addr, size, _, ops in md.disasm_lite(a[lo:hi], old.base + begin):
            m = rip.search(ops)
            if not m:
                continue
            target = addr + size + (int(m.group(2), 16) if m.group(1) == "+" else -int(m.group(2), 16))
            if not data_lo <= target < data_hi:
                continue
            s = addr - old.base - t_rva
            at = a[s:s + size].find(struct.pack("<I", (target - addr - size) & 0xFFFFFFFF))
            if at < 0 or a[s:s + at] != b[s:s + at] or a[s + at + 4:s + size] != b[s + at + 4:s + size]:
                continue
            refs.setdefault(target, set()).add(addr + size + struct.unpack_from("<i", b, s + at)[0])
    anchors = sorted((t, next(iter(v))) for t, v in refs.items() if len(v) == 1)
    keys = [t for t, _ in anchors]

    def read(image, va, n):
        try:
            return image.at_va(va, n)
        except ValueError:
            return b""

    def once(needle):
        hits = []
        for _, rva, _, raw_at, rsize in new.sections[1:3]:
            blob = new.bytes[raw_at:raw_at + rsize]
            i = blob.find(needle)
            while i >= 0 and len(hits) < 2:
                hits.append(new.base + rva + i)
                i = blob.find(needle, i + 1)
        return hits[0] if len(hits) == 1 else None

    def settle(va):
        if len(refs.get(va, ())) == 1:
            return next(iter(refs[va])), "an instruction"
        for n in (64, 48, 32):
            chunk = read(old, va, n)
            if len(chunk) == n and any(chunk) and once(chunk) is not None:
                return once(chunk), "its bytes"
        i = bisect.bisect_left(keys, va)
        if 0 < i < len(anchors) and anchors[i - 1][1] - anchors[i - 1][0] == anchors[i][1] - anchors[i][0]:
            there = va + anchors[i][1] - anchors[i][0]
            if read(old, va, 8) == read(new, there, 8):
                return there, "its neighbours"
        return None, None

    how, unsettled = Counter(), 0
    for path, addresses in wanted.items():
        pairs, open_ = [], []
        for va in addresses:
            there, why = settle(va)
            if there is None:
                open_.append(va)
                continue
            how[why] += 1
            pairs.append(f"'{va:#x}':'{there:#x}'")
        moved = sum(1 for pair in pairs if pair[1:12] != pair[15:26])
        print(f"// {path}: {len(addresses)} data addresses in code, {moved} moved")
        line = "    "
        for pair in pairs:
            if len(line) + len(pair) + 1 > 118:
                print(line.rstrip())
                line = "    "
            line += pair + ","
        print(line.rstrip(","))
        for va in open_:
            print(f"// not settled: {va:#x}")
        unsettled += len(open_)
    print("// settled by:", dict(how), "not settled:", unsettled)
    return 1 if unsettled else 0


def main():
    if "--data-map" in sys.argv:
        at = sys.argv.index("--data-map")
        return data_map(Image(sys.argv[1]), Image(sys.argv[2]), sys.argv[at + 1:])
    old, new, mask_va = Image(sys.argv[1]), Image(sys.argv[2]), int(sys.argv[3], 16)
    problems = []
    print(f"allowed  {old.sha256}  {len(old.bytes)} bytes")
    print(f"new      {new.sha256}  {len(new.bytes)} bytes")

    layout = [(s[0], s[1], s[3], s[4]) for s in old.sections] == [(s[0], s[1], s[3], s[4]) for s in new.sections]
    print("sections at the same places:", layout)
    same_functions = old.functions == new.functions
    print(f".pdata functions: {len(old.functions)} and {len(new.functions)}; all starts and ends equal: {same_functions}")
    if not layout or not same_functions:
        print("RESULT: different code. This build needs a port (tooling.md section 11), not a row.")
        return 1

    _, t_rva, _, t_raw, t_size = old.sections[0]
    a, b = old.bytes[t_raw:t_raw + t_size], new.bytes[t_raw:t_raw + t_size]
    data_lo, data_hi = old.sections[1][1], old.sections[2][1] + old.sections[2][2]
    image_hi = max(s[1] + max(s[2], s[4]) for s in old.sections)
    diff = [i for i in range(t_size) if a[i] != b[i]]
    begins = [f[0] for f in old.functions]
    touched, outside = set(), []
    for i in diff:
        k = bisect.bisect_right(begins, t_rva + i) - 1
        if k >= 0 and t_rva + i < old.functions[k][1]:
            touched.add(k)
        else:
            outside.append(i)
    print(f".text: {len(diff)} bytes differ, in {len(touched)} functions, {len(outside)} outside any .pdata function")

    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    number = re.compile(r"0x[0-9a-f]+")
    kinds, listed = Counter(), []
    for k in sorted(touched):
        begin, end = old.functions[k]
        lo, hi = begin - t_rva, end - t_rva
        ia = list(md.disasm_lite(a[lo:hi], old.base + begin))
        ib = list(md.disasm_lite(b[lo:hi], old.base + begin))
        if [(x[0], x[1]) for x in ia] != [(x[0], x[1]) for x in ib]:
            kinds["other"] += 1
            listed.append(f"{old.base + begin:#x}  the instructions of this function do not line up")
            continue
        for (addr, size, m1, o1), (_, _, m2, o2) in zip(ia, ib):
            s = addr - old.base - t_rva
            if a[s:s + size] == b[s:s + size]:
                continue
            n1, n2 = number.findall(o1), number.findall(o2)
            changed = [(x, y) for x, y in zip(n1, n2) if x != y]
            same_shape = m1 == m2 and number.sub("N", o1) == number.sub("N", o2) and len(changed) == 1
            rip = re.search(r"rip [+-] (0x[0-9a-f]+)", o1)
            if same_shape and rip and changed[0][0] == rip.group(1):
                kinds["RIP-relative reference"] += 1
            elif same_shape and "[" in o1 and all(data_lo <= int(v, 16) < data_hi for v in changed[0]):
                kinds["image-relative reference"] += 1
            else:
                kinds["other"] += 1
                listed.append(f"{addr:#x}  {m1} {o1}  ->  {m2} {o2}")
        decoded = sum(x[1] for x in ia)
        if a[lo + decoded:hi] != b[lo + decoded:hi]:
            kinds["other"] += 1
            listed.append(f"{old.base + begin + decoded:#x}  bytes after the decoded instructions differ")
    print("changed instructions:", dict(kinds))
    for line in listed[:50]:
        print("   ", line)
    if kinds["other"]:
        problems.append(f"{kinds['other']} changed instructions are not moved data references (listed above)")

    # Outside .pdata: each run of differing bytes must sit in one 32-bit field that points into the image in both builds.
    runs, current = [], [outside[0]] if outside else []
    for i in outside[1:]:
        if i - current[0] < 4:
            current.append(i)
        else:
            runs.append(current)
            current = [i]
    if current:
        runs.append(current)
    unread = 0
    for run in runs:
        lo, hi = run[0], run[-1]
        for w in range(lo, max(lo - 4, hi - 4, -1), -1):
            if w + 4 <= hi:
                continue
            x, y = struct.unpack_from("<i", a, w)[0], struct.unpack_from("<i", b, w)[0]
            relative = all(data_lo <= t_rva + w + 4 + v < image_hi for v in (x, y))
            absolute = all(data_lo <= v < data_hi for v in (x, y))
            if relative or absolute:
                break
        else:
            unread += 1
            if unread <= 20:
                print(f"    {old.base + t_rva + lo:#x}  {a[lo - 4:lo + 8].hex()}  ->  {b[lo - 4:lo + 8].hex()}")
    print(f"changed fields outside .pdata functions: {len(runs)}; not readable as a moved reference: {unread}")
    if unread:
        problems.append(f"{unread} changed fields outside .pdata functions are not moved data references (listed above)")

    for label, image in (("allowed", old), ("new", new)):
        at = image.bytes.find(DER_PREFIX)
        unique = at >= 0 and image.bytes.find(DER_PREFIX, at + 1) < 0
        sites = [image.at_va(va, len(original)) == original for va, original in LOOSE_SITES]
        print(f"{label}: login key header at file offset {at:#x}, found once: {unique}; LooseFilesPatch sites unchanged: {all(sites)}")
        if not unique or not all(sites):
            problems.append(f"the {label} executable lacks the login key header or a LooseFilesPatch site")

    mask = old.at_va(mask_va, 64)
    if hashlib.sha256(mask).hexdigest() != MASK_SHA256:
        problems.append(f"the allowed executable has no HNC mask at {mask_va:#x}")
    else:
        at = new.bytes.find(mask)
        if at < 0 or new.bytes.find(mask, at + 1) >= 0:
            problems.append("the HNC mask is not in the new executable exactly once")
        else:
            print(f"HNC mask: {mask_va:#x} in the allowed build, {new.va_of(at):#x} in the new one (its HncMaskVa)")

    if problems:
        print("RESULT: not proven equal. A person has to look at:")
        for problem in problems:
            print("  -", problem)
        return 1
    print("RESULT: same code. With equal tables (compare_tables.py) this build can be a row in ClientBuilds.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
