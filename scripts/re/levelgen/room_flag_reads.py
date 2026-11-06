"""Find candidate placed-room flag tests in the pinned client's terrain functions.

Usage: python scripts/re/levelgen/room_flag_reads.py > Tools/re/out/room-flag-reads.json
These are offset matches, not established room identities. Tracks straight-line register copies only;
indirect calls, copied records and cross-block propagation are deliberately left unresolved.
"""
import json
from pathlib import Path
import struct

import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86 import X86_OP_IMM, X86_OP_MEM, X86_OP_REG


def family(name):
    aliases = {"al": "rax", "ah": "rax", "ax": "rax", "eax": "rax",
               "bl": "rbx", "bh": "rbx", "bx": "rbx", "ebx": "rbx",
               "cl": "rcx", "ch": "rcx", "cx": "rcx", "ecx": "rcx",
               "dl": "rdx", "dh": "rdx", "dx": "rdx", "edx": "rdx",
               "sil": "rsi", "si": "rsi", "esi": "rsi",
               "dil": "rdi", "di": "rdi", "edi": "rdi",
               "bpl": "rbp", "bp": "rbp", "ebp": "rbp",
               "spl": "rsp", "sp": "rsp", "esp": "rsp"}
    return aliases.get(name, name.rstrip("dwb") if name.startswith("r") and name[1:2].isdigit() else name)


def scan(exe):
    pe = pefile.PE(exe, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    directory = pe.OPTIONAL_HEADER.DATA_DIRECTORY[3]
    raw = pe.get_data(directory.VirtualAddress, directory.Size)
    spans = sorted({(base+b, base+e) for b,e,_ in struct.iter_unpack("<III", raw)
                    if 0x141fe0000 <= base+b < 0x142060000})
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    md.detail = True
    found = []
    for begin, end in spans:
        tracked = {}
        for ins in md.disasm(pe.get_data(begin-base, end-begin), begin):
            ops = ins.operands
            candidate = None
            if ins.mnemonic in ("test", "and", "bt") and len(ops) == 2 and ops[1].type == X86_OP_IMM:
                operand = ops[0]
                if operand.type == X86_OP_MEM and operand.mem.disp == 0x1c and not operand.mem.index:
                    candidate = dict(origin=hex(ins.address), mode="direct")
                elif operand.type == X86_OP_REG:
                    candidate = tracked.get(family(ins.reg_name(operand.reg)))
                if candidate is not None:
                    found.append(dict(function=hex(begin), instruction=hex(ins.address),
                                      operation=ins.mnemonic, operands=ins.op_str,
                                      immediate=ops[1].imm, **candidate))
            # Preserve only an unmodified low byte. Register copies retain the original load address.
            copied = None
            if ins.mnemonic in ("mov", "movzx") and len(ops) == 2 and ops[0].type == X86_OP_REG:
                src = ops[1]
                if src.type == X86_OP_MEM and src.mem.disp == 0x1c and not src.mem.index and src.size <= 4:
                    copied = dict(origin=hex(ins.address), mode="loaded-register")
                elif src.type == X86_OP_REG:
                    copied = tracked.get(family(ins.reg_name(src.reg)))
            _, written = ins.regs_access()
            for reg in written:
                tracked.pop(family(ins.reg_name(reg)), None)
            if copied is not None:
                tracked[family(ins.reg_name(ops[0].reg))] = copied
            if ins.mnemonic.startswith("j") or ins.mnemonic in ("call", "ret"):
                tracked.clear()
    return dict(scope=["0x141fe0000", "0x142060000"], functions=len(spans), candidates=found,
                limitations="Offset matches only; no object provenance, cross-block flow, shifts or server code.")


if __name__ == "__main__":
    root = Path(__file__).resolve().parents[3]
    print(json.dumps(scan(str(root / "Client" / "Game.exe")), indent=2))
