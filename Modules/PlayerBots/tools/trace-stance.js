// Which animation set (stance) the client picks per actor: FUN_141e97190 decides, FUN_141e96a90 applies the name
// ("Town", "default", weapon sets). Load on its own (scripts/re/frida/run.py). PlayerBots research, local only.
const m = Process.getModuleByName('Game.patched.exe');
const at = v => m.base.add(ptr(v).sub(ptr('0x140000000')));
const pick = at('0x141e97190'), apply = at('0x141e96a90');
const wstr = s => {
  try {
    const size = s.add(0x10).readU64().toNumber(), cap = s.add(0x18).readU64().toNumber();
    const p = cap > 7 ? s.readPointer() : s;
    return p.readUtf16String(size);
  } catch (e) { return '?'; }
};
const seen = {};
let current = null;
Interceptor.attach(pick, {
  onEnter(args) {
    const self = args[0];
    current = self.toString();
    let area = null, town = null;
    try {
      area = self.add(8).readPointer().add(0x78).readPointer().add(0xb0).readPointer();
      town = area.isNull() ? null : [area.add(0x14).readU8(), area.add(0x14e).readU8(), area.add(0x2a).readS16()];
    } catch (e) { }
    send({ pick: current, early: self.add(0x219).readU8() & 1, town, entity: self.add(8).readPointer().toString() });
  },
  onLeave() { current = null; },
});
Interceptor.attach(apply, {
  onEnter(args) {
    const name = wstr(args[1]);
    const key = (current || args[0].toString()) + ':' + name;
    seen[key] = (seen[key] || 0) + 1;
    if (seen[key] <= 3) send({ apply: args[0].toString(), from: current, name });
  },
});
send({ ready: true });
