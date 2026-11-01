// Self-contained (Frida 17 gives each script its own scope). Every stat read made while FUNCTION runs (on its thread),
// with the value returned, and FUNCTION's return; plus the return of each call to ANIMATION from inside it. Each distinct
// (key, value) is sent once per call. Keys are Stats.dat row + 1.
// Shield Charge (3.29.3b): FUNCTION 0x141a6df00, its arrival hook (the final hit, then the end attack's animation
// through 0x141e9a3c0); 0x141a6bae0, its setup (path, range, the mover's speed). After FUNCTION returns, the action's
// mover speed (+0xe8 → +8 → +0x98 → float +0x240, what the moving-attack update walks at) is sent too.
const FUNCTION = '0x141a6bae0', ANIMATION = '0x141e9a3c0';
const m = Process.getModuleByName('Game.patched.exe');
const at = va => m.base.add(ptr(va).sub(ptr('0x140000000')));
const va = p => (p.compare(m.base) >= 0 && p.compare(m.base.add(m.size)) < 0) ? '0x' + p.sub(m.base).add(ptr('0x140000000')).toString(16) : p.toString();
let inside = 0, reads = null, anims = null;
Interceptor.attach(at(FUNCTION), {
  onEnter(args) {
    if (inside === 0) { reads = {}; anims = []; this.action = args[0]; this.flags = {f3e4: args[0].add(0x3e4).readU8(), f3e6: args[0].add(0x3e6).readU8(), dl: args[1].toUInt32() & 0xff}; }
    inside++;
  },
  onLeave(r) {
    if (--inside !== 0) return;
    let speed = null;
    try { speed = this.action.add(0xe8).readPointer().add(8).readPointer().add(0x98).readPointer().add(0x240).readFloat(); } catch (e) { }
    send({call: FUNCTION, flags: this.flags, ret: r.toString(), speed, anims, reads});
  }
});
Interceptor.attach(at(ANIMATION), {
  onEnter() { this.mine = inside > 0; },
  onLeave(r) { if (this.mine) anims.push(r.toString()); }
});
for (const [addr, getter] of [['0x1424fc1e0', 'stats'], ['0x1424cfa40', 'network']])
  Interceptor.attach(at(addr), {
    onEnter(args) { this.key = inside > 0 ? args[1].toUInt32() : null; this.caller = this.returnAddress; },
    onLeave(r) { if (this.key !== null) reads[getter + ':0x' + this.key.toString(16)] = r.toInt32(); }
  });
send('trace-call-stats ready');
