# usage: python -I scripts/re/action_animations.py [Client/Game.exe] [ActionType ...]
#
# Which Animation.dat row a skill's action asks the actor's .act for, read from the client's code (the 3.29.3c release;
# code addresses hold for every allowed build). Without names: every ActionTypes row a Rogue Exile's granted effect uses.
#
# The action classes register by name at 0x141947300 (name in rcx, factory in rdx). A factory allocates
# (0x142c8cb50), runs the class's constructor, and the constructor (or the factory after it) installs the vtable. The
# action's start (vtable +0xf8) asks for its animation one of these ways, followed two calls deep:
#   proj    0x141ba58f0 (projectile start), the row in edx
#   attack  0x141b58830 (attack start), the row in edx
#   melee   0x141c9b060 (the melee family's start), the row in edx
#   dataOr  0x141b720e0 (bow and thrown attacks): GrantedEffects.Animation, else the row in edx
#   spell   0x1419e9840 (the spell base): its virtual +0x4e0 returns the row (+0x4d0 when +0x210 returns 1)
#   attack  0x141c9af80 too (the dual-wield attacks: Dual Strike, Viper Strike dual wielding), the row in edx
#   warcry  0x141abf4b0 (the war cries' start): virtual +0x4d8
#   direct  0x141ea0c60 / 0x141ea2420: plays a clip by its own logic (a channel)
# Each line: the ActionType, then the (kind, row) pairs found; "no vtable" when the factory could not be followed, [] when
# none of the routines is reached within two calls. A cmov between two rows prints both ("Melee|Backstab").
# Needs capstone and pefile.
import bisect, json, os, re, sqlite3, struct, sys

import pefile
from capstone import CS_ARCH_X86, CS_MODE_64, Cs

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = next((a for a in sys.argv[1:] if a.lower().endswith(".exe")), os.path.join(REPO, "Client", "Game.exe"))
NAMES = [a for a in sys.argv[1:] if not a.lower().endswith(".exe")]

pe = pefile.PE(EXE, fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_EXCEPTION"]])
data = open(EXE, "rb").read()
text = [s for s in pe.sections if s.Name.startswith(b".text")][0]
seg = data[text.PointerToRawData:text.PointerToRawData + text.SizeOfRawData]
tlo, thi = base + text.VirtualAddress, base + text.VirtualAddress + text.SizeOfRawData
md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True

REGISTER, ALLOC = 0x141947300, 0x142c8cb50
EDX = {0x141ba58f0: "proj", 0x141b58830: "attack", 0x141b720e0: "dataOr", 0x141c9b060: "melee", 0x141c9af80: "attack"}
# The war cries' start (EnduringCry, AbyssalCry): the row is virtual +0x4d8, played with 0x141ea0c60.
WARCRY = 0x141abf4b0
SPELL = {0x1419e9840: "spell", 0x141af3260: "v2", 0x141c8b950: "v3"}
DIRECT = (0x141ea0c60, 0x141ea2420)


def rd(va, n):
    return data[pe.get_offset_from_rva(va - base):][:n]


def q(va):
    return struct.unpack("<Q", rd(va, 8))[0]


def cstr(va):
    try:
        return rd(va, 80).split(b"\0")[0].decode("ascii")
    except Exception:
        return None


starts = sorted((base + e.struct.BeginAddress, base + e.struct.EndAddress) for e in pe.DIRECTORY_ENTRY_EXCEPTION)
start_list = [s for s, _ in starts]


def bounds(va):
    i = bisect.bisect_right(start_list, va) - 1
    return starts[i] if i >= 0 else (va, va + 0x100)


def insns(fn):
    lo, hi = bounds(fn)
    return list(md.disasm(rd(lo, hi - lo), lo))


def rip(i):
    return i.address + i.size + i.operands[1].mem.disp


def registry():
    reg = {}
    for m in re.finditer(rb"\xe8", seg):
        va = tlo + m.start()
        if va + 5 + struct.unpack_from("<i", seg, m.start() + 1)[0] != REGISTER:
            continue
        name = factory = None
        for i in md.disasm(rd(va - 0x20, 0x20), va - 0x20):
            if i.mnemonic == "lea" and "[rip" in i.op_str:
                if i.op_str.startswith("rcx"):
                    name = cstr(rip(i))
                if i.op_str.startswith("rdx"):
                    factory = rip(i)
        if name and factory:
            reg[name] = factory
    return reg


def is_vtable(t):
    try:
        return not (tlo <= t < thi) and tlo <= q(t) < thi and tlo <= q(t + 8) < thi
    except Exception:
        return False


def stores_to_self(ins, k, selves):
    """Whether the vtable lea'd at ins[k] is stored at the object's start ([self], no offset) before its register is
    overwritten (within 12 instructions)."""
    reg = ins[k].op_str.split(",")[0]
    for j in ins[k + 1:k + 13]:
        dest = j.op_str.split(",")[0]
        if j.mnemonic == "mov" and dest.startswith("qword ptr [") and j.op_str.endswith(", " + reg)                 and dest[len("qword ptr ["):-1] in selves:
            return True
        if dest == reg:
            return False
    return False


def vtables_stored(ins, selves):
    """The vtables lea'd in these instructions and stored at the object's start, in order; a register copied from one
    of `selves` (mov rbx, rcx) joins them."""
    found = []
    for k, i in enumerate(ins):
        parts = i.op_str.split(", ")
        if i.mnemonic == "mov" and len(parts) == 2 and parts[1] in selves and not parts[0].startswith("qword"):
            selves = selves | {parts[0]}
        elif len(parts) == 2 and parts[0] in selves and i.mnemonic in ("mov", "lea"):
            selves = selves - {parts[0]}   # now another object (a member's address)
        if i.mnemonic == "lea" and "[rip" in i.op_str and is_vtable(rip(i)) and stores_to_self(ins, k, selves):
            found.append(rip(i))
    return found


def constructor(factory):
    """(the constructor the factory calls after allocating, a vtable the factory installs after it, if any)."""
    first = list(md.disasm(rd(factory, 8), factory))[:1]
    if first and first[0].mnemonic == "jmp" and first[0].operands[0].type == 2:
        factory = first[0].operands[0].imm
    ins = insns(factory)
    for k, i in enumerate(ins):
        if i.mnemonic == "call" and i.operands[0].type == 2 and i.operands[0].imm == ALLOC:
            for n, j in enumerate(ins[k + 1:k + 14]):
                if j.mnemonic == "call" and j.operands[0].type == 2:
                    # The allocation is in rax; the factory keeps it in a register of its own (rbx) for the
                    # constructor's argument and for a vtable of its own stored after it.
                    selves = {"rax"} | {x.op_str.split(", ")[0] for x in ins[k + 1:k + 1 + n]
                                        if x.mnemonic == "mov" and x.op_str.endswith(", rax")}
                    post = vtables_stored(ins[k + 2 + n:k + 2 + n + 30], selves)
                    return j.operands[0].imm, (post[-1] if post else None)
    return None


def vtable_of_constructor(ct):
    """The last vtable the constructor stores at the object's start."""
    found = vtables_stored(insns(ct), {"rcx"})
    return found[-1] if found else None


def vtable(reg, name):
    if (factory := reg.get(name)) is None or (found := constructor(factory)) is None:
        return None
    ct, post = found
    return post or vtable_of_constructor(ct)


def const_ret(fn):
    ins = list(md.disasm(rd(fn, 0x10), fn))[:2]
    if len(ins) == 2 and ins[1].mnemonic == "ret":
        a = ins[0]
        if a.mnemonic == "mov" and (a.op_str.startswith("eax, ") or a.op_str.startswith("al, ")):
            return int(a.op_str.split(", ")[1], 0)
        if a.mnemonic == "xor" and a.op_str in ("eax, eax", "al, al"):
            return 0
    return None


def end_of(fn):
    """How far to read a function: its .pdata entry often covers only the prologue (a chained unwind entry follows), so
    at least 0x800 bytes; the scan stops at the last ret or int3 past every branch."""
    lo, hi = bounds(fn)
    return max(hi, fn + 0x800) if lo == fn else fn + 0x400


def spell_row(vt, anim):
    slot = 0x4d0 if const_ret(q(vt + 0x210)) == 1 else 0x4e0
    row = const_ret(q(vt + slot))
    return anim.get(row, hex(q(vt + slot))) if row is not None else hex(q(vt + slot))


def scan(fn, vt, depth, seen, out, anim):
    if fn in seen or depth < 0:
        return
    seen.add(fn)
    if fn in SPELL:
        out.append((SPELL[fn], spell_row(vt, anim)))
        return
    if fn == WARCRY:
        row = const_ret(q(vt + 0x4d8))
        out.append(("warcry", anim.get(row, hex(q(vt + 0x4d8)))))
        return
    end = end_of(fn)
    ins, far = [], fn
    for i in md.disasm(rd(fn, end - fn), fn):
        ins.append(i)
        imm = i.operands[0].imm if i.operands and i.operands[0].type == 2 else None
        if i.mnemonic.startswith("j") and i.mnemonic != "jmp" and imm is not None:
            far = max(far, imm)
        if i.mnemonic == "jmp" and imm is not None and fn < imm < end:
            far = max(far, imm)
        if i.mnemonic in ("ret", "int3") or (i.mnemonic == "jmp" and not (imm is not None and fn < imm < end)):
            if i.address >= far:
                break
    edx = ecx = None
    for i in ins:
        if i.mnemonic == "mov" and i.op_str.startswith("edx, ") and i.op_str[5:6].isdigit():
            edx = int(i.op_str.split(", ")[1], 0)
        elif i.mnemonic == "xor" and i.op_str == "edx, edx":
            edx = 0
        if i.mnemonic == "mov" and i.op_str.startswith("ecx, ") and i.op_str[5:6].isdigit():
            ecx = int(i.op_str.split(", ")[1], 0)
        if i.mnemonic.startswith("cmov") and i.op_str == "edx, ecx" and edx is not None:
            edx = (edx, ecx)
        if i.mnemonic in ("call", "jmp") and i.operands and i.operands[0].type == 2:
            t = i.operands[0].imm
            if t in EDX:
                rows = edx if isinstance(edx, tuple) else (edx,)
                out.append((EDX[t], "|".join(str(anim.get(r, r)) for r in rows)))
            elif t in SPELL:
                out.append((SPELL[t], spell_row(vt, anim)))
            elif t in DIRECT:
                out.append(("direct", hex(fn)))
            elif tlo <= t < thi:
                scan(t, vt, depth - 1, seen, out, anim)
            edx = None
        if i.mnemonic == "call" and not (i.operands and i.operands[0].type == 2):
            edx = None


def exile_action_types(db):
    act = {r[0]: r[1] for r in db.execute("SELECT _row, Id FROM ActionTypes")}
    names = set()
    for (keys,) in db.execute("""SELECT m.GrantedEffectsKeys FROM RogueExiles r JOIN MonsterVarieties m ON m._row = r.MonsterVarietiesKey
            WHERE r.__col1 = 1 OR m.Id LIKE '%Faridun%' OR m._row IN (SELECT Id FROM RogueExileGiants)"""):
        for e in json.loads(keys or "[]"):
            (skill,) = db.execute("SELECT ActiveSkill FROM GrantedEffects WHERE _row = ?", (e,)).fetchone()
            (action,) = db.execute("SELECT ActionType FROM ActiveSkills WHERE _row = ?", (skill,)).fetchone()
            if action in act:
                names.add(act[action])
    return sorted(names)


def main():
    db = sqlite3.connect(f"file:{os.path.join(REPO, 'data', 'client.db')}?mode=ro", uri=True)
    anim = {r[0]: r[1] for r in db.execute("SELECT _row, Id FROM Animation")}
    reg = registry()
    for name in NAMES or exile_action_types(db):
        vt = vtable(reg, name)
        if not vt:
            print(f"{name:28} no vtable")
            continue
        out = []
        scan(q(vt + 0xf8), vt, 2, set(), out, anim)
        print(f"{name:28} {hex(vt)} {sorted(set((a, str(b)) for a, b in out))}")


if __name__ == "__main__":
    main()
