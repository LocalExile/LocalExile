import sys, struct, pefile
EXE = r"Client\Game.exe"
pe = pefile.PE(EXE, fast_load=True); base = pe.OPTIONAL_HEADER.ImageBase
data = open(EXE,'rb').read()
text = [s for s in pe.sections if s.Name.startswith(b'.text')][0]
tb = data[text.PointerToRawData:text.PointerToRawData+text.SizeOfRawData]; trva = text.VirtualAddress
targets = {int(a,16) for a in sys.argv[1:]}
hits = {}
for i in range(len(tb)-5):
    if tb[i] == 0xE8:
        rel = struct.unpack_from('<i', tb, i+1)[0]
        tgt = base + trva + i + 5 + rel
        if tgt in targets:
            hits.setdefault(hex(tgt), []).append(hex(base+trva+i))
for k,v in hits.items(): print(k, 'callers:', ' '.join(v[:12]))
