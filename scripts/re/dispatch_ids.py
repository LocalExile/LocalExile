# usage: dispatch_ids.py [id ...]   -- instance S→C dispatcher 0x142150ae0: switch index = byte table at 0x14215b764
# indexed by (id - 0x0d), 0x2ef entries. Prints id → case for the given ids, or every non-default case.
import sys, pefile
import os
EXE = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "Client", "Game.exe")
pe = pefile.PE(EXE, fast_load=True); base = pe.OPTIONAL_HEADER.ImageBase
data = open(EXE, 'rb').read()
off = pe.get_offset_from_rva(0x14215b764 - base)
table = data[off:off + 0x2ef]
default = max(set(table), key=table.count)
ids = [int(a, 0) for a in sys.argv[1:]] or [i + 0xd for i, c in enumerate(table) if c != default]
for i in ids:
    c = table[i - 0xd] if 0xd <= i < 0xd + len(table) else None
    print(f'id {i:#06x} -> ' + ('default (no case)' if c in (None, default) else f'case {c:#x}'))
