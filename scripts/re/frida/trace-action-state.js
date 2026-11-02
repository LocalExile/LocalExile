// Self-contained (Frida 17 gives each script its own scope). What an action does while it runs. When 0x141e8cf10
// (actor, &action, pos) starts an action of class VTABLE (its +0x118 is the action type HASH16, +0x11a the use's
// sequence; the skill id is not stored there), it snapshots the action's first SNAPSHOT_BYTES every 100 ms for
// SNAPSHOT_MS, sending the u32 words that changed and whether it is still the actor's current action (+0x1b0); and
// for the same window counts the calls to the class's own virtual functions (FUNCTIONS) with their last return value.
// Shield Charge (3.29.3b): VTABLE 0x143324da8, type 0x2bc0; FUNCTIONS are the entries it overrides from Move's
// 0x1434b2128.
const VTABLE = '0x143324da8';
const FUNCTIONS = ['0x1413c6f50', '0x1413c7150', '0x141b56ac0', '0x141b56db0', '0x141a6bae0',
  '0x141a6c060', '0x1413c7080', '0x141a6ba70', '0x141b56820', '0x141a6cba0', '0x141a6cbd0', '0x1413c6c40',
  '0x141246380', '0x1419fd360', '0x141a6d340', '0x141a6e410'];
const SNAPSHOT_BYTES = 0x300, SNAPSHOT_MS = 3000;
const m = Process.getModuleByName('Game.patched.exe');
const at = va => m.base.add(ptr(va).sub(ptr('0x140000000')));
const va = p => (p.compare(m.base) >= 0 && p.compare(m.base.add(m.size)) < 0) ? '0x' + p.sub(m.base).add(ptr('0x140000000')).toString(16) : p.toString();
const words = p => { try { return Array.from(new Uint32Array(p.readByteArray(SNAPSHOT_BYTES))); } catch (e) { return null; } };
let watching = null, counts = {};
for (const f of FUNCTIONS)
  try {
    Interceptor.attach(at(f), {
      onEnter(args) { this.mine = watching !== null && args[0].equals(watching); },
      onLeave(r) { if (this.mine) { const c = counts[f] ??= {n: 0, ret: null}; c.n++; c.ret = '0x' + r.toString(16); } }
    });
  } catch (e) { send({unhooked: f, why: e.message}); }  // too short for a hook (a few bytes)
Interceptor.attach(at('0x141e8cf10'), {
  onEnter(args) { this.actor = args[0]; this.action = args[1].readPointer(); },
  onLeave(r) {
    const a = this.action, actor = this.actor;
    let vt; try { vt = va(a.readPointer()); } catch (e) { return; }
    if (vt !== VTABLE || watching !== null) return;
    watching = a; counts = {};
    send({started: VTABLE, result: r.toInt32() & 0xff, type: '0x' + a.add(0x118).readU16().toString(16)});
    let last = words(a), t = 0;
    const timer = setInterval(() => {
      t += 100;
      const now = words(a);
      let current = '?'; try { current = actor.add(0x1b0).readPointer().equals(a); } catch (e) { }
      const changed = {};
      if (now && last) now.forEach((w, i) => { if (w !== last[i]) changed['+0x' + (i * 4).toString(16)] = '0x' + w.toString(16); });
      // The moving-attack update 0x141a6c060's own fields: phase flags, the scheduled events (count +0x3fc, next +0x400,
      // elapsed +0x404 against duration +0x420), +0x122's flags, and the mover's speed (Positioned? +0x240) it walks at.
      let fields = null;
      try {
        const mover = a.add(0xe8).readPointer().add(8).readPointer().add(0x98).readPointer();
        fields = {f3e5: a.add(0x3e5).readU8(), f3e6: a.add(0x3e6).readU8(), f3e8: a.add(0x3e8).readU8(),
          f3f8: a.add(0x3f8).readU8(), f122: '0x' + a.add(0x122).readU16().toString(16),
          events: a.add(0x3fc).readU32(), next: a.add(0x400).readU32(), elapsed: a.add(0x404).readFloat(),
          duration: a.add(0x420).readFloat(), f3f4: a.add(0x3f4).readFloat(), f3e0: a.add(0x3e0).readFloat(),
          speed: mover.add(0x240).readFloat(), mover1e6: mover.add(0x1e6).readS8()};
      } catch (e) { fields = {error: e.message}; }
      send({t, current, changed, calls: counts, fields});
      counts = {};
      last = now;
      if (t >= SNAPSHOT_MS) { clearInterval(timer); watching = null; }
    }, 100);
  }
});
send('trace-action-state ready');
