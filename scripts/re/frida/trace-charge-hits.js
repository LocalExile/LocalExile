// Self-contained (Frida 17 gives each script its own scope). Where and how far a moving attack hits (Shield Charge,
// 11-skills.md "Shield Charge"): every call of the action's hit function (its vtable +0x550, Shield Charge 0x1413c79d0,
// args: action, &point (two i32 cells), kind: 0 along the path, 1 the final hit, 2 the end phase) with the time since
// the action's first call, and every range 0x141a6d760 (action, kind) returns for it.
const HIT = '0x1413c79d0', RANGE = '0x141a6d760';
const m = Process.getModuleByName('Game.patched.exe');
const at = va => m.base.add(ptr(va).sub(ptr('0x140000000')));
let first = null, lastAction = null;
const ms = () => { const now = Date.now(); return first === null ? 0 : now - first; };
Interceptor.attach(at(HIT), {
  onEnter(args) {
    if (!args[0].equals(lastAction ?? ptr(0))) { lastAction = args[0]; first = Date.now(); send({action: args[0].toString()}); }
    let point = null; try { point = [args[1].readS32(), args[1].add(4).readS32()]; } catch (e) { }
    send({t: ms(), hit: args[2].toUInt32() & 0xff, point});
  }
});
Interceptor.attach(at(RANGE), {
  onEnter(args) { this.kind = args[1].toUInt32() & 0xff; },
  onLeave(r) { send({t: ms(), range: r.toInt32(), kind: this.kind}); }
});
send('trace-charge-hits ready');
