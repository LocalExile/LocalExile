# usage: python -I scripts/re/overnight/fragments.py <binary> <outdir>
# Entry points inside the executable bytes no function covers (<outdir>/undecompiled.tsv from NightDump), for
# NightFragments.java: a linear disassembly of each fragment, an entry at its first instruction and after each padding,
# ret or jmp. A fragment that does not disassemble to its end is data and gives none. Writes <outdir>/fragment-entries.txt.
# Needs capstone and pefile (pip install capstone pefile).
import os
import sys

import capstone
import pefile

binary, d = sys.argv[1], sys.argv[2]
pe = pefile.PE(binary, fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
image = pe.get_memory_mapped_image()
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
out, data, total = [], 0, 0
for line in open(os.path.join(d, 'undecompiled.tsv'), encoding='utf-8').read().splitlines()[1:]:
    start, _, size, _ = line.split('\t')
    a, size = int(start, 16), int(size)
    total += size
    ins = list(md.disasm(bytes(image[a - base:a - base + size]), a))
    if sum(x.size for x in ins) != size:
        data += 1
        continue
    fresh = True
    for x in ins:
        if x.mnemonic in ('int3', 'nop') or x.mnemonic == 'xchg' and x.op_str == 'ax, ax':
            fresh = True
            continue
        if fresh:
            out.append(x.address)
            fresh = False
        if x.mnemonic.startswith('ret') or x.mnemonic == 'jmp':
            fresh = True
with open(os.path.join(d, 'fragment-entries.txt'), 'w') as f:
    f.write(''.join('%x\n' % x for x in out))
print(f'{os.path.basename(binary)}: {total} uncovered bytes, {len(out)} entries, {data} fragments not code')
