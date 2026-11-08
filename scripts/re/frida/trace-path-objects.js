// Self-contained. The objects the pathfinder's collision test 0x141e65ae0 looks at (once per object and mover), with
// the Positioned fields that decide whether they block (Agents/docs/protocol/07 §5):
//   python scripts/re/frida/run.py Game.patched.exe --log logs/path-objects.jsonl scripts/re/frida/trace-path-objects.js
// ctx (arg 0): [0] cell*, [1] threshold* (mover size + 1), [2] layer flag*, [3] {mover Positioned, team*, Player*, phase*}.
const m = Process.getModuleByName('Game.patched.exe');
const at = va => m.base.add(ptr(va).sub(ptr('0x140000000')));
const seen = new Set();
const fields = p => ({cell: [p.add(0x294).readS32(), p.add(0x298).readS32()], size: p.add(0x1e3).readS8(),
  size1e4: p.add(0x1e4).readS8(), f1e0: p.add(0x1e0).readU16(), f1e5: p.add(0x1e5).readU8(), f1e6: p.add(0x1e6).readU8(),
  f1e7: p.add(0x1e7).readU8(), l2c0: p.add(0x2c0).readU8(), l2c1: p.add(0x2c1).readU8(),
  team: p.add(0x158).readPointer().add(0x58).readS32()});
Interceptor.attach(at('0x141e65ae0'), {
  onEnter(a) {
    const ctx = a[0], obj = a[1], mc = ctx.add(0x18).readPointer();
    const mover = mc.readPointer();
    const key = obj.toString() + mover.toString();
    this.key = key;
    if (seen.has(key)) return;
    this.info = {obj: fields(obj), mover: fields(mover), threshold: ctx.add(8).readPointer().readS32(),
      layerFlag: ctx.add(0x10).readPointer().readU8(), moverTeam: mc.add(8).readPointer().readS32(),
      player: mc.add(0x10).readPointer().readPointer().toString(), phase: mc.add(0x18).readPointer().readU8()};
  },
  onLeave(r) {
    if (!this.info) return;
    seen.add(this.key);
    this.info.blocks = r.toInt32() & 0xff;
    send(this.info);
  }
});
send('trace-path-objects ready');
