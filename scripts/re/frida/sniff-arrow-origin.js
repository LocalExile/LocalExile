// Arrow Nova origin probe (read-only). Tags the rain rig and split-arrow entities from their Animated paths, then
// samples the attached position (Positioned +0x2b8) and the local-to-world output (0x141e361b0) so the caster-side
// arrow origin can be compared between the official and local clients.
// Run: python scripts/re/frida/run.py <pid> --seconds 0 --log logs/arrow-origin.jsonl scripts/re/frida/sniff-arrow-origin.js
'use strict';
const image = Process.enumerateModules().find(m => /^(PathOfExileSteam|Game\.patched)\.exe$/i.test(m.name));
if (!image) throw new Error('client image not found');
const base = ptr('0x140000000');
const at = va => image.base.add(ptr(va).sub(base));
const stamp = () => Date.now();
const positioned = new Map(); // entity -> Positioned component
const tagged = new Map();     // entity -> { label, path, pos, comp }

// Positioned deserializer: remember each entity's Positioned component.
Interceptor.attach(at('0x141e2f0b0'), {
  onEnter(args) { this.c = args[0]; },
  onLeave() {
    try {
      const entity = this.c.add(8).readPointer().toString();
      positioned.set(entity, this.c);
    } catch (e) { }
  }
});

// Animated deserializer: tag the rain rig and split-arrow entities by their .ao path.
Interceptor.attach(at('0x141e56a80'), {
  onEnter(args) {
    this.c = args[0];
    try {
      const r = args[1];
      this.base = r.readPointer();
      this.pos = r.add(0x10).readU64().toNumber();
    } catch (e) { this.base = null; }
  },
  onLeave() {
    if (!this.base) return;
    try {
      const b = this.base.add(this.pos);
      const flags = b.readU8();
      if ((flags & 1) === 0 || (flags & 2) !== 0) return;
      const n = b.add(1).readU32();
      if (n === 0 || n > 300) return;
      const path = b.add(5).readUtf16String(n);
      if (!/split_arrow|rain_rig/i.test(path)) return;
      const entity = this.c.add(8).readPointer().toString();
      const label = /rain_rig/i.test(path) ? 'rig' : 'arrow';
      const pos = positioned.get(entity);
      const rec = { label, path, pos, comp: this.c };
      tagged.set(entity, rec);
      send({ t: stamp(), kind: 'tag', label, path, entity, comp: this.c.toString(),
        positioned: pos ? pos.toString() : null });
      // Sample the Positioned attachment override (+0x2b8, two floats) while the rig plays.
      let samples = 0;
      const timer = setInterval(() => {
        try {
          if (!pos || samples++ > 150) { clearInterval(timer); return; }
          const grid = [pos.add(0x294).readS32(), pos.add(0x298).readS32()];
          const attach = [pos.add(0x2b8).readFloat(), pos.add(0x2bc).readFloat()];
          send({ t: stamp(), kind: 'sample', label, entity, grid, attach });
        } catch (e) { clearInterval(timer); }
      }, 16);
    } catch (e) { }
  }
});

// Local-to-world: the output at r8 is the object's rendered world position (two ints).
Interceptor.attach(at('0x141e361b0'), {
  onEnter(args) {
    try {
      const entity = args[0].add(8).readPointer().toString();
      const rec = tagged.get(entity);
      if (!rec) return;
      this.rec = rec;
      this.out = args[2];
      // param_8 (the "attached" switch) is the 8th argument, at [rsp+0x40] on Windows x64.
      this.flag = this.context.rsp.add(0x40).readU8();
    } catch (e) { }
  },
  onLeave() {
    if (!this.rec) return;
    try {
      const world = [this.out.readS32(), this.out.add(4).readS32()];
      send({ t: stamp(), kind: 'world', label: this.rec.label, world, attachedFlag: this.flag });
    } catch (e) { }
  }
});

send('sniff-arrow-origin ready');
