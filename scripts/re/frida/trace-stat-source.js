// Self-contained (Frida 17 gives each script its own scope). Which network stat set answers one stat read: while
// FUNCTION runs, each read of KEY through the network getter 0x1424cfa40 (provider: sorted (u32 key, i32 value) pairs
// at +0xf0..+0xf8, falling back to the provider at +0xe0; 04-player-entity.md §4.1a) sends the provider, how many pairs it
// holds, whether KEY is among them, the value of each MARKERS key it holds (a skill's own stats mark its record), and the
// same for its fallback.
// Shield Charge (3.29.3b): FUNCTION 0x141a6bae0 (its setup), KEY movement_velocity_+% (0xf5).
const FUNCTION = '0x141a6bae0', KEY = 0xf5;
const MARKERS = {level: 0x1, base_movement_velocity: 0xb4, shield_charge_damage_maximum: 0x1db};
const m = Process.getModuleByName('Game.patched.exe');
const at = va => m.base.add(ptr(va).sub(ptr('0x140000000')));
const va = p => (p.compare(m.base) >= 0 && p.compare(m.base.add(m.size)) < 0) ? '0x' + p.sub(m.base).add(ptr('0x140000000')).toString(16) : p.toString();
function describe(provider) {
  try {
    const begin = provider.add(0xf0).readPointer(), end = provider.add(0xf8).readPointer();
    const n = end.sub(begin).toInt32() / 8, pairs = {};
    for (let i = 0; i < n; i++) pairs[begin.add(i * 8).readU32()] = begin.add(i * 8 + 4).readS32();
    const marks = {};
    for (const [name, k] of Object.entries(MARKERS)) if (k in pairs) marks[name] = pairs[k];
    return {provider: provider.toString(), vtable: va(provider.readPointer()), pairs: n, key: KEY in pairs ? pairs[KEY] : 'absent', marks};
  } catch (e) { return {provider: provider.toString(), error: e.message}; }
}
let inside = 0;
const seen = new Set();
Interceptor.attach(at(FUNCTION), { onEnter() { inside++; }, onLeave() { inside--; } });
Interceptor.attach(at('0x1424cfa40'), {
  onEnter(args) {
    if (inside === 0 || (args[1].toUInt32() >>> 0) !== KEY) return;
    const p = args[0];
    if (seen.has(p.toString())) return;
    seen.add(p.toString());
    let fallback = null; try { const f = p.add(0xe0).readPointer(); if (!f.isNull()) fallback = describe(f); } catch (e) { }
    send({read: '0x' + KEY.toString(16), caller: va(this.returnAddress), set: describe(p), fallback});
  }
});
send('trace-stat-source ready');
