// Load after common.js. Which objects the client creates itself (0x141ebdf30: a client-side object, id from the world)
// and which come from the server (0x141ebe190, the 0x2b9 create), with the object type's metadata path, and every
// Projectile component (0x141e37850 writes vtable 0x1435a0d10) with an image-relative backtrace. Live 2026-10-29: one
// Fireball cast built two Projectile components, one per path, so the client predicts the local player's projectile.
// The type record's path is found by scanning it for a pointer to a UTF-16 "Metadata/" string.
function typePath(rec) {
  for (const base of [rec, (() => { try { return rec.add(0x28).readPointer(); } catch (e) { return null; } })()]) {
    if (base === null) continue;
    for (let off = 0; off < 0x100; off += 8) {
      try {
        const p = base.add(off).readPointer();
        const s = p.readUtf16String(9);
        if (s === 'Metadata/') return p.readUtf16String(120);
      } catch (e) { }
    }
  }
  return '?';
}
Interceptor.attach(at('0x141ebdf30'), {
  onEnter(args) { let t = '?'; try { t = typePath(args[1].readPointer()); } catch (e) { } send({client: Date.now(), type: t}); }
});
Interceptor.attach(at('0x141ebe190'), {
  onEnter(args) { let t = '?'; try { t = typePath(args[1].readPointer()); } catch (e) { } send({server: Date.now(), id: args[2].toInt32() >>> 0, type: t}); }
});
Interceptor.attach(at('0x141e37850'), {
  onEnter() { send({projectile: Date.now(), bt: Thread.backtrace(this.context, Backtracer.ACCURATE).slice(0, 3).map(va)}); }
});
send('trace-projectile-create ready');
