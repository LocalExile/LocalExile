// Load after common.js. Why the per-frame runner 0x141e94900 drops a queued action (actor +0x1c0). It starts the queued
// action only when the action's vtable +0x130 and 0x1419400c0 return true and the actor flag +0x218 bit 0x40 (dead) and
// the entity flag +0x8c bit 0 are clear. For queued actions of action type ACTION (+0x118) this logs, per frame, those
// flags on entry and the actor's current (+0x1b0) / queued (+0x1c0) action on exit; and the two checks' results.
const ACTION = 0xb188;   // Melee
const ids = a => { try { return a.isNull() ? null : '0x' + a.add(0x118).readU16().toString(16) + '/0x' + a.add(0x11a).readU16().toString(16); } catch (e) { return '?'; } };
let hooked = false, cur = null;
const report = (what, r) => { if (cur) send({check: what, result: r.toInt32() & 0xff}); };
Interceptor.attach(at('0x141e94900'), {
  onEnter(args) {
    const a = args[0], q = a.add(0x1c0).readPointer();
    cur = null;
    if (q.isNull() || q.add(0x118).readU16() !== ACTION) return;
    cur = q;
    this.a = a;
    if (!hooked) {
      hooked = true;
      Interceptor.attach(q.readPointer().add(0x130).readPointer(), { onLeave(r) { report('+0x130', r); } });
    }
    send({enter: a.toString(), f218: a.add(0x218).readU8(), e8c: a.add(8).readPointer().add(0x8c).readU8(),
      current: ids(a.add(0x1b0).readPointer()), tick: a.add(0x510).readU32()});
  },
  onLeave() {
    if (this.a) send({leave: this.a.toString(), current: ids(this.a.add(0x1b0).readPointer()), queued: ids(this.a.add(0x1c0).readPointer())});
    cur = null;
  }
});
Interceptor.attach(at('0x1419400c0'), { onLeave(r) { report('0x1419400c0', r); } });
send('trace-queued-check ready');
