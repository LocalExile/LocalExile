import capstone, pefile
EXE = r"Client\Game.exe"
pe = pefile.PE(EXE, fast_load=True); base = pe.OPTIONAL_HEADER.ImageBase
data = open(EXE,'rb').read()
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
def read_va(va, n):
    off = pe.get_offset_from_rva(va - base); return data[off:off+n]
for va in range(0x140207890, 0x1402078b0):
    ins = next(md.disasm(read_va(va, 16), va), None)
    if ins: print(hex(va), ins.mnemonic, ins.op_str)
