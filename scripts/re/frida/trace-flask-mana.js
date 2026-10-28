// Read-only probe: client mana pool vs. the packets the client really sends.
// No native calls, no memory writes. Load with scripts/re/frida/run.py.
const module = Process.getModuleByName('Game.patched.exe');
const base = ptr('0x140000000');
const at = va => module.base.add(ptr(va).sub(base));
const hex = (p, n) => Array.from(new Uint8Array(p.readByteArray(n))).map(x => x.toString(16).padStart(2, '0')).join('');
const emit = data => send({time: Date.now(), ...data});

// Outgoing messages: the buffered socket writer (0x140dfb760 -> writer arg 0 = buffer).
Interceptor.attach(at('0x140dfb760'), {
  onEnter(args) {
    try {
      const n = args[2].toInt32();
      if (n < 2 || n > 32) return;
      const id = (args[1].readU8() << 8) | args[1].add(1).readU8();
      if (![0x24, 0x25, 0x70].includes(id)) return;
      emit({event: 'client-sends', id: '0x' + id.toString(16), hex: hex(args[1], n)});
    } catch (_) {}
  },
});

// The server's item-use answers (0x263).
Interceptor.attach(at('0x1421aded0'), {
  onEnter(args) { this.m = args[0]; },
  onLeave() {
    emit({event: 'server-item-used', typeHash: this.m.add(0x10).readU32(),
      inventoryId: this.m.add(0x14).readU32(), itemId: this.m.add(0x18).readU32(),
      result: this.m.add(0x40).readU16()});
  },
});

// The local player's mana pool, read once per client Life tick, logged only when it changes.
const mana = life => life.add(0x1c8);
const local = life => !life.isNull() && life.add(8).readPointer().add(0x88).readU32() === 0x1000;
let last = null;
Interceptor.attach(at('0x141e6a190'), {
  onEnter(args) {
    try {
      if (!local(args[0])) return;
      const pool = mana(args[0]), value = pool.add(0x30).readS32(), fraction = pool.add(0x28).readFloat();
      if (last !== null && Math.abs(value - last) < 1e-6) return;
      last = value;
      emit({event: 'client-mana', current: value, fraction: Math.round(fraction * 1000) / 1000});
    } catch (_) {}
  },
});
emit({event: 'probe-ready', pid: Process.id, mode: 'read-only'});
