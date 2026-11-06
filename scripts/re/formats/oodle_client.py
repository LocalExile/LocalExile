"""OodleLZ_Decompress (0x142c93fa0) of Client/Game.exe, run in the Unicorn emulator: the client's own decoder, for blocks
ooz-wasm rejects and to check what it unpacks. Slow, so it is only for those. Needs `pip install unicorn pefile`."""
import os
import struct

import pefile
from unicorn import UC_ARCH_X86, UC_HOOK_MEM_FETCH_UNMAPPED, UC_HOOK_MEM_READ_UNMAPPED, UC_HOOK_MEM_WRITE_UNMAPPED, UC_MODE_64, Uc, UcError
from unicorn.x86_const import (UC_X86_REG_GS_BASE, UC_X86_REG_R8, UC_X86_REG_R9, UC_X86_REG_RAX, UC_X86_REG_RCX, UC_X86_REG_RDX,
                               UC_X86_REG_RIP, UC_X86_REG_RSP)

DECOMPRESS = 0x142c93fa0
STACK, STACK_SIZE = 0x7000_0000, 0x10_0000
HEAP, HEAP_SIZE = 0x8000_0000, 0x1000_0000
TEB = 0x6000_0000
RETURN = 0x5000_0000
STUBS = 0x5100_0000  # every import resolves here: the decoder takes its memory from the caller and calls nothing it needs


def _page(n):
    return n + 0xfff & ~0xfff


class ClientOodle:
    def __init__(self, exe=None):
        repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
        pe = pefile.PE(exe or os.path.join(repo, "Client", "Game.exe"), fast_load=True)
        pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"]])
        base = pe.OPTIONAL_HEADER.ImageBase
        image = bytearray(pe.get_memory_mapped_image())
        for entry in getattr(pe, "DIRECTORY_ENTRY_IMPORT", []):
            for imp in entry.imports:
                struct.pack_into("<Q", image, imp.address - base, STUBS)
        self.uc = uc = Uc(UC_ARCH_X86, UC_MODE_64)
        uc.mem_map(base, _page(len(image)))
        uc.mem_write(base, bytes(image))
        uc.mem_map(STACK, STACK_SIZE)
        uc.mem_map(HEAP, HEAP_SIZE)
        uc.mem_map(TEB, 0x10000)
        uc.mem_map(RETURN, 0x1000)
        uc.mem_map(STUBS, 0x1000)
        uc.mem_write(RETURN, b"\xf4")
        uc.mem_write(STUBS, b"\x31\xc0\xc3")  # xor eax, eax; ret
        uc.reg_write(UC_X86_REG_GS_BASE, TEB)
        self.fault = None

        def unmapped(_uc, _access, address, _size, _value, _data):
            self.fault = address
            return False
        uc.hook_add(UC_HOOK_MEM_READ_UNMAPPED | UC_HOOK_MEM_WRITE_UNMAPPED | UC_HOOK_MEM_FETCH_UNMAPPED, unmapped)

    def decompress(self, compressed, size):
        """The unpacked bytes, or None if the client's decoder refuses the block."""
        uc = self.uc
        source, target, scratch = HEAP, HEAP + _page(len(compressed)) + 0x1000, HEAP + 0x800_0000
        if target + size + 0x1000 > scratch:
            raise ValueError("block too large")
        uc.mem_write(source, bytes(compressed))
        uc.mem_write(target, b"\0" * size)
        rsp = STACK + STACK_SIZE - 0x1000
        # (comp, compLen, raw, rawLen, fuzz=1, crc=0, verbose=0, dstBase=0, dstSize=0, cb=0, ud=0, mem, memSize, threadPhase=3)
        stack = [RETURN, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, scratch, 0x700_0000, 3]
        uc.mem_write(rsp, struct.pack(f"<{len(stack)}Q", *stack))
        uc.reg_write(UC_X86_REG_RSP, rsp)
        uc.reg_write(UC_X86_REG_RCX, source)
        uc.reg_write(UC_X86_REG_RDX, len(compressed))
        uc.reg_write(UC_X86_REG_R8, target)
        uc.reg_write(UC_X86_REG_R9, size)
        self.fault = None
        try:
            uc.emu_start(DECOMPRESS, RETURN)
        except UcError as e:
            rip = uc.reg_read(UC_X86_REG_RIP)
            if rip != RETURN:
                raise RuntimeError(f"{e} at {rip:#x}, fault {self.fault}") from None
        got = uc.reg_read(UC_X86_REG_RAX)
        if got != size:
            return None
        return bytes(uc.mem_read(target, size))
