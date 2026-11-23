import pefile, struct, sys
EXE = r"Client\Game.exe"
pe = pefile.PE(EXE, fast_load=True); base = pe.OPTIONAL_HEADER.ImageBase
data = open(EXE,'rb').read()
def read_va(va, n):
    off = pe.get_offset_from_rva(va - base)
    return data[off:off+n]
table = read_va(0x14215b764, 0x2ef)
default = max(set(table), key=table.count)
for arg in sys.argv[1:]:
    i = int(arg, 0)
    c = table[i-0xd]
    tgt = struct.unpack('<i', read_va(0x14215b36c + 4*c, 4))[0]
    print(f"id {i:#06x}: case {c:#x} target {0x14215b36c + tgt:#x}" if c != default else f"id {i:#06x}: default")
