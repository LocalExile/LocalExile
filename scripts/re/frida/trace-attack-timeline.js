// Load after common.js. One attack's timeline with a millisecond clock: the action start (0x141e8cf10, its type HASH16
// and skill id), the body clip per frame, the server's object creates (0x141ebe190) and the client's own (0x141ebdf30),
// with the metadata path. A bow attack's order shows when the clip's contact falls against the arrow's arrival, and at
// what speed the clip really plays.
function typePath(rec) {
  for (const base of [rec, (() => { try { return rec.add(0x28).readPointer(); } catch (e) { return null; } })()]) {
    if (base === null) continue;
    for (let off = 0; off < 0x100; off += 8) {
      try {
        const p = base.add(off).readPointer();
        if (p.readUtf16String(9) === 'Metadata/') return p.readUtf16String(120);
      } catch (e) { }
    }
  }
  return '?';
}
const ids = a => { try { return a.isNull() ? null : '0x' + a.add(0x118).readU16().toString(16) + '/0x' + a.add(0x11a).readU16().toString(16); } catch (e) { return '?'; } };
let attackUntil = 0;
Interceptor.attach(at('0x141e8cf10'), {
  onEnter(args) {
    this.action = ids(args[1].readPointer());
    this.from = va(this.returnAddress);
  },
  onLeave(r) {
    const type = (this.action || '').split('/')[0];
    if (type === '0x415' || type === '0xb188') attackUntil = Date.now() + 1500;
    send({t: Date.now(), start: this.action, from: this.from, result: r.toInt32() & 0xff});
  }
});
// The body controller (actor +0x1a0) each frame of the attack: clip index +0x190, elapsed +0x1a4, speed +0x1b0, clip
// start and end +0x1b8/+0x1bc (seconds). A frame's elapsed and clock give the speed the clip really plays at.
Interceptor.attach(at('0x141e8e060'), {
  onEnter(args) {
    if (Date.now() > attackUntil) return;
    try {
      const c = args[0].add(0x1a0).readPointer();
      if (c.isNull()) return;
      send({t: Date.now(), actor: args[0].toString(), clip: c.add(0x190).readS32(), elapsed: c.add(0x1a4).readFloat(),
        speed: c.add(0x1b0).readFloat(), from: c.add(0x1b8).readFloat(), to: c.add(0x1bc).readFloat()});
    } catch (e) { send({frameError: String(e)}); }
  }
});
Interceptor.attach(at('0x141ebe190'), {
  onEnter(args) {
    if (Date.now() > attackUntil) return;
    let t = '?'; try { t = typePath(args[1].readPointer()); } catch (e) { }
    send({t: Date.now(), server: t});
  }
});
Interceptor.attach(at('0x141ebdf30'), {
  onEnter(args) {
    if (Date.now() > attackUntil) return;
    let t = '?'; try { t = typePath(args[1].readPointer()); } catch (e) { }
    send({t: Date.now(), client: t});
  }
});
send('trace-attack-timeline ready');
