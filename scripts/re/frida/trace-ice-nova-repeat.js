// Load after common.js. Ice Nova's repeats (Spell Echo): the IceNova action (vtable 0x143545408) rings again from its
// update 0x141c02c10 after a 0.3 s pause (+0x31c) while its ring count +0x320 is under spell_repeat_count and bytes
// +0x2b8 and +0x2b9 are equal, and its slot 0x460 (0x1412e1ad0) jumps to the spell base's replay 0x1419e6370, which
// takes one off +0x2d0 and plays the animation again. This logs each ring start (0x141c02940), each victim the ring
// reaches (0x141c02f50), each ring end (0x141c02bc0), each replay, and every change of those fields in the update.
// Ring helpers can receive a copy of the original action with a different vtable. Observe the actual
// ring helper and capture its vtable there so updates and shared spell replays use a matching receiver.
const novaTables = new Set();
const isIceNova = a => { try { return novaTables.has(a.readPointer().toString()); } catch (e) { return false; } };
const fields = a => ({ b2b8: a.add(0x2b8).readU8(), b2b9: a.add(0x2b9).readU8(), rings: a.add(0x320).readU32(),
  left: a.add(0x2d0).readU32(), total: a.add(0x2d4).readU32(), pause: a.add(0x31c).readFloat(),
  elapsed: a.add(0x32c).readFloat(), active: a.add(0x650).readU8() });
const last = new Map();
const log = (what, a, extra) => send(Object.assign({ [what]: a.toString(), t: Date.now() }, fields(a), extra || {}));
Interceptor.attach(at('0x141c02c10'), {
  onEnter(args) {
    if (!isIceNova(args[0])) return;
    const a = args[0], f = fields(a);
    // The ring's own timers change every frame; only the counters and flags are worth a line.
    const key = [f.b2b8, f.b2b9, f.rings, f.left, f.total, f.active, f.pause > 0].join();
    if (last.get(a.toString()) !== key) { last.set(a.toString(), key); log('update', a); }
  }
});
Interceptor.attach(at('0x141c02940'), { onEnter(args) { novaTables.add(args[0].readPointer().toString()); log('ringStart', args[0]); } });
Interceptor.attach(at('0x141c02f50'), {
  onEnter(args) { if (isIceNova(args[0])) log('reach', args[0], { target: args[1].toUInt32() }); }
});
Interceptor.attach(at('0x141c02bc0'), { onEnter(args) { if (isIceNova(args[0])) log('ringEnd', args[0]); } });
Interceptor.attach(at('0x1419e6370'), { onEnter(args) { if (isIceNova(args[0])) log('replay', args[0]); } });
send('trace-ice-nova-repeat ready');
