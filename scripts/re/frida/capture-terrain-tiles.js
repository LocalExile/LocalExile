// usage: load into the running client with REA's Frida (start_frida_session attach, load_frida_script), walk into
// an area, then: python scripts/walkmaps/heights.py <OUT>tiles_N.json <area id> <seed>. One file per generated area.
// Live 2026-10-18 (town, The Coast): the client stayed up; the town capture reproduces the known grid exactly.
// Read-only capture of the terrain's placed subtiles when 0x142047a50 starts (it loops over the finished terrain).
// terrain = *(arg0 + 0x28): +0x18 / +0x20 size in tiles, +0x28 0x38-byte entries, +0x104 i16 height multiplier.
// entry: +0 subtile record (vector<u8> heights at +0 / +8), +0x30 i16 height offset, +0x36 u8 code.
// Output folder (absolute, with a trailing slash): edit before loading.
const OUT = 'C:/Temp/terrain-tiles/';
const exe = Process.enumerateModules().find(m => m.name.toLowerCase().endsWith('.exe'));
const at = va => exe.base.add(ptr(va).sub(ptr('0x140000000')));
const SIZES = [0, 1, 0x45, 0x89, 0x119, 0x211];
const hex = buf => { const a = new Uint8Array(buf); let s = ''; for (let i = 0; i < a.length; i++) s += (a[i] < 16 ? '0' : '') + a[i].toString(16); return s; };
let calls = 0;
const hook = at('0x142047a50');
if (hex(hook.readByteArray(16)) !== '48895c24085556574154415541564157') send({ refused: true });
else Interceptor.attach(hook, {
  onEnter(args) {
    const n = ++calls;
    try {
      const t = args[0].add(0x28).readPointer();
      const w = t.add(0x18).readS32(), h = t.add(0x20).readS32();
      if (w <= 0 || h <= 0 || w > 400 || h > 400) { send({ call: n, skip: 'size', w, h }); return; }
      const mult = t.add(0x104).readS16(), base = t.add(0x28).readPointer();
      const index = new Map(), vecs = [], entries = [];
      let bad = 0;
      for (let i = 0; i < w * h; i++) {
        const e = base.add(i * 0x38);
        const sub = e.readPointer();
        let v = -1;
        if (!sub.isNull()) {
          const key = sub.toString();
          if (index.has(key)) v = index.get(key);
          else {
            const b = sub.readPointer(), len = sub.add(8).readPointer().sub(b).toInt32();
            if (SIZES.indexOf(len) < 0) { bad++; v = -2; }
            else { v = vecs.length; vecs.push(len ? hex(b.readByteArray(len)) : ''); }
            index.set(key, v);
          }
        }
        entries.push([v, e.add(0x36).readU8(), e.add(0x30).readS16()]);
      }
      const f = new File(OUT + 'tiles_' + n + '.json', 'w');
      f.write(JSON.stringify({ w, h, mult, entries, vecs }));
      f.close();
      send({ call: n, w, h, mult, unique: vecs.length, bad });
    } catch (e) {
      send({ call: n, error: String(e) });
    }
  },
});
send({ ready: true });
