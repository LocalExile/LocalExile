"""Where the client reads a stat: `mov edx, <key>` followed within a few instructions by a call (the stats getter is
mostly called through the stats object's vtable: `mov rax, [rcx]; call [rax]` for the int value, `call [rax+8]` for
the bool, or directly 0x1424d6510).

Usage: python -I scripts/re/statkeys.py <key> [key ...]
Prints, per key, each site, the call and the function it is in (nearest preceding int3 padding, approximate).
Keys are client stat keys (Stats.dat row + 1, Agents/docs/protocol/11-skills.md §5).
"""
import sys
import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

keys = {int(a, 0) for a in sys.argv[1:]}
pe = pefile.PE('Client/Game.exe', fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
text = next(s for s in pe.sections if s.Name.rstrip(b'\0') == b'.text')
code = text.get_data()
start = base + text.VirtualAddress
md = Cs(CS_ARCH_X86, CS_MODE_64)

for key in sorted(keys):
    pat = b'\xba' + key.to_bytes(4, 'little')    # mov edx, imm32
    off = code.find(pat)
    while off >= 0:
        site = start + off
        call = None
        for n, ins in enumerate(md.disasm(code[off:off + 40], site)):
            if n == 0 and ins.op_str != f'edx, 0x{key:x}' and ins.op_str != f'edx, {key}':
                break
            if ins.mnemonic == 'call':
                call = ins.op_str
                break
            if n > 6 or ins.mnemonic in ('ret', 'jmp'):
                break
        if call:
            f = off
            while f > 0 and not (code[f - 1] == 0xCC and code[f] != 0xCC):
                f -= 1
            print(f'0x{key:x} at 0x{site:x} call {call:<24} in ~0x{start + f:x}')
        off = code.find(pat, off + 1)
