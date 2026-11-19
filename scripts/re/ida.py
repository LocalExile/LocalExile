# usage: python -X utf8 scripts/re/ida.py
# Runs a list of read-only IDA (idalib) operations against the client database and prints the answers.
# The database is Client/Game.patched.exe.i64 (IDA's own analysis, already done), opened headless; nothing is saved.
# The operations come from logs/tmp/ida-ops.txt (or $IDA_OPS), one per line, '#' starts a comment:
#   fn <addr|name>                 the function containing an address, or the named function, with its range
#   decomp <addr|name>             Hex-Rays pseudocode of that function
#   strings <regex> [<max>]        strings matching the regex, with addresses
#   xrefs <addr|name>              code references to an address or a name
#   callers <addr|name>            functions that call it
#   callees <addr|name>            functions its body calls
#   name <pattern>                 names matching a pattern (substring, case-insensitive)
#   bytes <addr> <n>               raw bytes as hex
#   seg                            the loaded segments
import os
import re
import struct
import sys

# This folder holds scripts named like standard modules (dis.py), and running a script from here puts it first on
# sys.path, which breaks IDAPython's "import dis". Keep it out of the import path.
_here = os.path.dirname(os.path.abspath(__file__))
sys.path = [p for p in sys.path if os.path.abspath(p or os.getcwd()) != _here]

import idapro

DB = os.environ.get("IDA_DB", os.path.join("Client", "Game.patched.exe.i64"))

_open = idapro.open_database(DB, False, "")
if _open != 0:
    print(f"open_database failed ({_open})")
    sys.exit(1)

import ida_bytes
import ida_idaapi
import ida_funcs
import ida_hexrays
import ida_name
import ida_segment
import ida_ua
import ida_xref
import idautils

hexrays = ida_hexrays.init_hexrays_plugin()


def resolve(token):
    try:
        return int(token, 16)
    except ValueError:
        ea = ida_name.get_name_ea(ida_idaapi.BADADDR, token)
        return None if ea == ida_idaapi.BADADDR else ea


def func_of(token):
    ea = resolve(token)
    return None if ea is None else ida_funcs.get_func(ea)



def _slot_list(ea, n=420):
    out = []
    for i in range(n):
        ptr = ida_bytes.get_qword(ea + i * 8)
        if ptr == 0 or ptr < 0x140001000 or ptr >= 0x144767000:
            break
        out.append(ptr)
    return out

def _vt_at(col_va):
    """Find the vtable whose [-1] slot points at the complete object locator."""
    pat = struct.pack("<Q", col_va)
    for base, seg, data in each_chunk():
        start = 0
        while True:
            at = data.find(pat, start)
            if at < 0:
                break
            yield base + at
            start = at + 1


def _dump_vtable(ea, note=""):
    print(f"vtable at {ea:#x} {note}")
    for i in range(0, 400):
        ptr = ida_bytes.get_qword(ea + i * 8)
        if ptr == 0 or ptr < 0x140001000 or ptr >= 0x144767000:
            break
        f = ida_funcs.get_func(ptr)
        nm = ida_name.get_ea_name(f.start_ea) if f else "?"
        print(f"  slot {i:3d} (+{i * 8:#5x}) {ptr:#x} {nm}")


def run(op):
    parts = op.split()
    cmd = parts[0].lower()
    if cmd == "fn":
        f = func_of(parts[1])
        if f is None:
            print("not found")
        else:
            print(f"{ida_name.get_ea_name(f.start_ea)} {f.start_ea:#x}-{f.end_ea:#x} size={f.end_ea - f.start_ea}")
    elif cmd == "decomp":
        f = func_of(parts[1])
        if f is None:
            print("not found")
        elif not hexrays:
            print("no hexrays")
        else:
            try:
                cf = ida_hexrays.decompile(f.start_ea)
                print(str(cf) if cf else "decompile failed")
            except Exception as exc:  # noqa: BLE001
                print(f"decompile error: {exc}")
    elif cmd == "strings":
        rx = re.compile(parts[1], re.I)
        limit = int(parts[2]) if len(parts) > 2 else 200
        n = 0
        for s in idautils.Strings():
            if rx.search(str(s)):
                print(f"{s.ea:#x} {str(s)!r}")
                n += 1
                if n >= limit:
                    print("... truncated")
                    break
        print(f"({n} shown)")
    elif cmd == "xrefs":
        ea = resolve(parts[1])
        if ea is None:
            print("not found")
        else:
            n = 0
            for x in idautils.XrefsTo(ea, 0):
                f = ida_funcs.get_func(x.frm)
                where = ida_name.get_ea_name(f.start_ea) if f else "?"
                print(f"from {x.frm:#x} ({where}) type {x.type}")
                n += 1
                if n > 400:
                    print("... truncated")
                    break
            print(f"({n} refs)")
    elif cmd in ("callers", "callees"):
        f = func_of(parts[1])
        if f is None:
            print("not found")
            return
        seen = {}
        if cmd == "callers":
            for x in idautils.XrefsTo(f.start_ea, 0):
                g = ida_funcs.get_func(x.frm)
                if g is not None:
                    seen.setdefault(g.start_ea, ida_name.get_ea_name(g.start_ea))
        else:
            for item in idautils.FuncItems(f.start_ea):
                for x in idautils.XrefsFrom(item, 0):
                    if x.type in (ida_xref.fl_CN, ida_xref.fl_CF) and x.to != ida_idaapi.BADADDR:
                        g = ida_funcs.get_func(x.to)
                        if g is not None:
                            seen.setdefault(g.start_ea, ida_name.get_ea_name(g.start_ea))
        for a, n in sorted(seen.items()):
            print(f"{n} {a:#x}")
        print(f"({len(seen)})")
    elif cmd == "name":
        rx = re.compile(parts[1], re.I)
        n = 0
        for ea, nm in idautils.Names():
            if rx.search(nm):
                print(f"{ea:#x} {nm}")
                n += 1
                if n > 400:
                    print("... truncated")
                    break
        print(f"({n} shown)")
    elif cmd == "bytes":
        ea = resolve(parts[1])
        print(ida_bytes.get_bytes(ea, int(parts[2]) if len(parts) > 2 else 16).hex())
    elif cmd == "str":
        ea = resolve(parts[1])
        for width in (1, 2):
            raw = ida_bytes.get_strlit_contents(ea, -1, 0 if width == 1 else 1)
            if raw:
                print(f"{width}B: {raw.decode('utf-8', 'replace')!r}")
    elif cmd == "dis":
        ea = resolve(parts[1])
        for _ in range(int(parts[2]) if len(parts) > 2 else 20):
            try:
                import idc
                line = idc.GetDisasm(ea)
            except Exception:
                line = ida_ua.print_insn_mnem(ea) + " " + ida_ua.print_operand(ea, 0)
            print(f"{ea:#x}  {line}")
            try:
                import idc
                ea = idc.next_head(ea)
            except Exception:
                ea += 1
    elif cmd == "findptr":
        # every place holding a pointer to an address (8 bytes, image base 0x140000000)
        target = resolve(parts[1])
        pat = struct.pack("<Q", target)
        n = 0
        for base, seg, data in each_chunk():
            start = 0
            while True:
                at = data.find(pat, start)
                if at < 0:
                    break
                ea = base + at
                f = ida_funcs.get_func(ea)
                where = ida_name.get_ea_name(f.start_ea) if f else ""
                print(f"{ea:#x} ({ida_segment.get_segm_name(seg)}) {where}")
                n += 1
                start = at + 1
                if n > 60:
                    print("... truncated")
                    return
        print(f"({n} found)")
    elif cmd == "find":
        rx = re.compile(parts[1].encode())
        rx16 = re.compile(parts[1].encode("utf-16-le"))
        limit = int(parts[2]) if len(parts) > 2 else 50
        n = 0
        for base, seg, data in each_chunk():
            for one in (rx, rx16):
                for m in one.finditer(data):
                    print(f"{base + m.start():#x} ({ida_segment.get_segm_name(seg)}) {m.group()[:60]!r}")
                    n += 1
                    if n >= limit:
                        print("... truncated")
                        return
        print(f"({n} found)")
    elif cmd == "seg":
        for s in idautils.Segments():
            seg = ida_segment.getseg(s)
            print(f"{ida_segment.get_segm_name(seg)} {seg.start_ea:#x}-{seg.end_ea:#x} perm={seg.perm}")
    elif cmd == "rtti":
        # a type descriptor's name field is what "name ..." finds; the descriptor base is 16 bytes earlier,
        # and the complete object locator names it through a 32-bit RVA, so work backwards from there.
        name_ea = resolve(parts[1])
        if name_ea is None:
            print("not found")
            return
        td = name_ea - 16
        rva = (td - 0x140000000) & 0xFFFFFFFF
        print(f"type descriptor {td:#x} (name {name_ea:#x})")
        hits = 0
        for base, seg, data in each_chunk():
            needle = struct.pack("<I", rva)
            start = 0
            while True:
                at = data.find(needle, start)
                if at < 0:
                    break
                col = base + at - 12
                sig = ida_bytes.get_dword(col)
                if sig not in (0, 1):
                    print(f"  candidate {col:#x} sig={sig:#x} ctx={ida_bytes.get_dword(col + 4):#x} {ida_bytes.get_dword(col + 8):#x}")
                if sig in (0, 1) or os.environ.get("IDA_RTTI_LOOSE"):
                    print(f"  locator {col:#x} signature {sig}")
                    for vt in _vt_at(col):
                        _dump_vtable(vt + 8, f"[via locator {col:#x}]")
                        hits += 1
                start = at + 1
        print(f"({hits} vtables)")
    elif cmd == "vt":
        ea = resolve(parts[1])
        _dump_vtable(ea)
    elif cmd == "vtdiff":
        a = _slot_list(resolve(parts[1]))
        b = _slot_list(resolve(parts[2]))
        n = max(len(a), len(b))
        diff = 0
        for i in range(n):
            va = a[i] if i < len(a) else 0
            vb = b[i] if i < len(b) else 0
            if va != vb:
                fa = ida_funcs.get_func(va)
                print(f"slot {i:3d} (+{i * 8:#5x}) {va:#x} {ida_name.get_ea_name(fa.start_ea) if fa else '-'}   != {vb:#x}")
                diff += 1
        print(f"({diff} differing of {n})")
    elif cmd == "vtnear":
        a = _slot_list(resolve(parts[1]))
        lo = int(parts[2], 16)
        hi = int(parts[3], 16)
        n = 0
        for i, va in enumerate(a):
            if lo <= va < hi:
                f = ida_funcs.get_func(va)
                print(f"slot {i:3d} (+{i * 8:#5x}) {va:#x} {ida_name.get_ea_name(f.start_ea) if f else '-'}")
                n += 1
        print(f"({n} in {lo:#x}-{hi:#x})")
    else:
        print("unknown op")


CHUNK = 1 << 20


def each_chunk():
    """Yield (base address, segment, bytes) one megabyte at a time: get_bytes over a whole 100 MB .data returns nothing."""
    for s in idautils.Segments():
        seg = ida_segment.getseg(s)
        ea = seg.start_ea
        while ea < seg.end_ea:
            n = min(CHUNK, seg.end_ea - ea)
            data = ida_bytes.get_bytes(ea, n)
            if data:
                yield ea, seg, data
            ea += n


def main():
    ops_path = os.environ.get("IDA_OPS", os.path.join("logs", "tmp", "ida-ops.txt"))
    with open(ops_path, encoding="utf-8") as h:
        ops = [line.split("#", 1)[0].strip() for line in h]
    for op in ops:
        if not op:
            continue
        print(f"\n===== {op} =====")
        try:
            run(op)
        except Exception as exc:  # noqa: BLE001
            print(f"error: {type(exc).__name__}: {exc}")
    idapro.close_database(False)


if __name__ == "__main__":
    main()
