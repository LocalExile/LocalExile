"""Scan every section (not just .text) for the overlay's string ids as 4-byte values.

If the client keeps them in a table, the ids sit next to each other in .rdata; locating that table tells us which code
formats the map overlay's text, and from there which field it counts.
"""

import struct

import pefile

EXE = r"D:\大肥鱼工作区\LocalExile\Client\Game.patched.exe"

IDS = {
    "MonstersRemaining1": 3599597233,
    "MonstersRemainingN": 31342061,
    "MoreThan50": 4022577848,
    "MapObjectiveComplete": 3721160435,
    "MonsterLevel": 1532933650,
    "League": 4032306906,
}

pe = pefile.PE(EXE, fast_load=True)
image_base = pe.OPTIONAL_HEADER.ImageBase

for name, value in IDS.items():
    pattern = struct.pack("<I", value)
    hits = []
    for s in pe.sections:
        sec = s.Name.decode(errors="replace").rstrip("\x00")
        data = s.get_data()
        start = 0
        while True:
            i = data.find(pattern, start)
            if i < 0:
                break
            hits.append((sec, image_base + s.VirtualAddress + i, i))
            start = i + 1
    print(f"{name} = {value} (0x{value:08x}): {len(hits)} hit(s)")
    for sec, va, off in hits[:8]:
        print(f"    {sec} va=0x{va:x}")
