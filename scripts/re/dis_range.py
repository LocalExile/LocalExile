import capstone, pefile, sys
EXE = r"Client\Game.patched.exe"
pe = pefile.PE(EXE, fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
def read_va(va, size):
    rva = va - base
    for s in pe.sections:
        size_sec = max(s.Misc_VirtualSize, s.SizeOfRawData)
        if s.VirtualAddress <= rva < s.VirtualAddress + size_sec:
            off = rva - s.VirtualAddress
            return s.get_data()[off:off+size]
    return None
va = int(sys.argv[1],16); n = int(sys.argv[2])
code = read_va(va, n)
for insn in md.disasm(code, va):
    print(f"0x{insn.address:x}: {insn.mnemonic} {insn.op_str}")
