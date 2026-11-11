# usage: scanvt.py <func_va>   -- every .rdata vtable whose slot 1 is <func_va> (e.g. 0x1429a7660 lists the network
# message handler groups); prints slots 0-3
import struct, pefile, re, sys
import os
EXE = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "Client", "Game.exe")
pe = pefile.PE(EXE, fast_load=True); base = pe.OPTIONAL_HEADER.ImageBase
data = open(EXE,'rb').read()
needle = struct.pack('<Q', int(sys.argv[1],16))
for m in re.finditer(re.escape(needle), data):
    off=m.start()
    try: va=base+pe.get_rva_from_offset(off)
    except: continue
    vt=va-8
    slots=[struct.unpack_from('<Q',data,off-8+8*i)[0] for i in range(4)]
    print(hex(vt), [hex(x) for x in slots])
