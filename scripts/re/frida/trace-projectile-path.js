// Self-contained, observation-only. A client-drawn projectile's flight: the projectile actions' row publishes
// (0x141bb9130), each new Projectile component's entity (0x141e3ab90, rdx), and that entity's Positioned movement
// update (0x141e35a50, rcx = Positioned, +8 its entity): cell, float XY, the path's raw time (+0x238), duration (+0x23c),
// step time (+0x240) and the progress-curve inputs (+0x2fc, +0x300), on each cell change.
'use strict';
const image = Process.enumerateModules().find(m => /^(Game|Game\.patched)\.exe$/i.test(m.name));
const at = address => image.base.add(ptr(address).sub(ptr('0x140000000')));
const va = address => '0x' + address.sub(image.base).add(ptr('0x140000000')).toString(16);
const t0 = Date.now();
const entities = new Map();
Interceptor.attach(at('0x141bb9130'), {
  onEnter(args) { this.a = args[0]; },
  onLeave() {
    let row = null; try { row = this.a.add(0x358).readPointer().readPointer().readUtf16String(64); } catch (e) { }
    send({ t: Date.now() - t0, release: this.a.toString(), vtable: va(this.a.readPointer()), row });
  }
});
Interceptor.attach(at('0x141e3ab90'), {
  onEnter(args) {
    entities.set(args[1].toString(), { since: Date.now(), last: null });
    send({ t: Date.now() - t0, projectile: args[1].toString() });
  }
});
Interceptor.attach(at('0x141e35a50'), {
  onEnter(args) {
    const p = args[0];
    let key; try { key = p.add(8).readPointer().toString(); } catch (e) { return; }
    const entry = entities.get(key);
    if (entry === undefined) return;
    if (Date.now() - entry.since > 5000) { entities.delete(key); return; }
    this.p = p; this.entry = entry; this.key = key;
  },
  onLeave() {
    if (!this.p) return;
    const p = this.p;
    const cell = [p.add(0x294).readS32(), p.add(0x298).readS32()];
    const sig = cell.join(',');
    if (sig === this.entry.last) return;
    this.entry.last = sig;
    send({ t: Date.now() - t0, entity: this.key, cell, xy: [p.add(0x2b8).readFloat(), p.add(0x2bc).readFloat()],
      raw: p.add(0x238).readFloat(), full: p.add(0x23c).readFloat(), step: p.add(0x240).readFloat(),
      c1: p.add(0x2fc).readFloat(), c2: p.add(0x300).readFloat() });
  }
});
send('trace-projectile-path ready');
