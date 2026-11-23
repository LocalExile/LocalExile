"""Disassemble the map overlay function and look for the string ids it loads.

The 32-bit immediate scan found nothing, so this dumps the overlay function documented as 0x1406b25f0 and also scans
for 64-bit immediates of the same ids.
"""

import struct
import sys

import capstone
import pefile

EXE = r"D:\大肥鱼工作区\LocalExile\Client\Game.patched.exe"
OVERLAY = 0x1406B25F0

IDS = {
    "MonstersRemaining1": 3599597233,
    "MonstersRemainingN": 31342061,
    "MoreThan50": 4022577848,
    "MapObjectiveComplete": 3721160435,
    "MonsterLevel": 1532933650,
    "League": 4032306906,
}

md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
md.detail = False


def read_va(pe, image_base, va, size):
    rva = va - image_base
    for s in pe.sections:
        if s.VirtualAddress <= rva < s.VirtualAddress + max(s.Misc_VirtualSize, s.SizeOfRawData):
            off = rva - s.VirtualAddress
            data = s.get_data()
            return data[off:off + size]
    return None


def main():
    pe = pefile.PE(EXE, fast_load=True)
    image_base = pe.OPTIONAL_HEADER.ImageBase

    print("=== 64-bit immediate scan")
    for name, value in IDS.items():
        pattern = struct.pack("<Q", value)
        total = 0
        for s in pe.sections:
            if not s.Characteristics & 0x40000000:  # initialized data
                continue
            data = s.get_data()
            start = 0
            while True:
                i = data.find(pattern, start)
                if i < 0:
                    break
                va = image_base + s.VirtualAddress + i
                print(f"  {name}: va=0x{va:x} in {s.Name.decode(errors='replace').rstrip(chr(0))}")
                total += 1
                start = i + 1
                if total > 4:
                    break
    print("  done")

    print(f"\n=== overlay 0x{OVERLAY:x}")
    code = read_va(pe, image_base, OVERLAY, 0x300)
    if code is None:
        print("  not in a section")
        return 1
    for insn in md.disasm(code, OVERLAY):
        print(f"  0x{insn.address:x}: {insn.mnemonic} {insn.op_str}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
