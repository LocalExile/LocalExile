"""Disassemble around a machine address in the client.

Takes VAs like 0x14156702f and prints the nearby instructions so a captured access site can be read.
"""

import sys

import capstone
import pefile

EXE = r"D:\大肥鱼工作区\LocalExile\Client\Game.patched.exe"
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
md.detail = False

pe = pefile.PE(EXE, fast_load=True)
image_base = pe.OPTIONAL_HEADER.ImageBase


def read_va(va, size):
    rva = va - image_base
    for s in pe.sections:
        size_sec = max(s.Misc_VirtualSize, s.SizeOfRawData)
        if s.VirtualAddress <= rva < s.VirtualAddress + size_sec:
            off = rva - s.VirtualAddress
            return s.get_data()[off:off + size]
    return None


def show(va, back=48, fwd=96):
    code = read_va(va - back, back + fwd)
    if code is None:
        print(f"0x{va:x}: not mapped")
        return
    print(f"\n=== 0x{va:x}")
    for insn in md.disasm(code, va - back):
        mark = " <== " if insn.address <= va < insn.address + insn.size else "     "
        print(f" {mark}0x{insn.address:x}: {insn.mnemonic} {insn.op_str}")


for arg in sys.argv[1:]:
    show(int(arg, 16))
