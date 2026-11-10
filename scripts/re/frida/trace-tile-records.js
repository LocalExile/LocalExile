// Every tile object record (.tmd) of every placed tile while the client generates an area, with its world placement
// computed the way 0x142035a10 does it, for all types: also the server-side ones (AreaTransition, Waypoint …) that the
// client skips. Load after common.js.
//
// 0x141d82d90 walks a placed tile's records (0x58 bytes each) and calls 0x142047e30(ctx, record) for each:
//   ctx[1]: the placed tile: +0x30 i16 height offset (steps of 7.8125), +0x36 u8 rotation/flip code 0..7
//   ctx[2]: → i32 x, i32 y: the tile's grid origin
//   ctx[3]: → handle; *(handle)+0x28 → +0x60 u8 width, +0x61 u8 height (tiles of 23 cells)
//   record: i32 x, i32 y (tile-local cell), f32[2] offset, f32 z (+0x10), f32 angle (+0x14), f32 scale (+0x18),
//           u8 flip (+0x1c), …, +0x30 type handle, +0x38 .ao handle (handle+8 → object, +8 → std::wstring path)
// Placement (0x142035a10): a = [w*23 - x - 1, x, h*23 - y - 1, y]; T = the 3-byte rows at 0x142faa4d8 per code;
//   gx = a[T1 + 2*T0] + ox, gy = a[(T0 == 0 ? 2 : 0) + T2] + oy; angle → 2π - angle when code >= 4, then
//   + (code & 3) * 0x1436ae110, wrapped to [0, 2π]; z = record z - height offset * 7.8125 (unless the "none" value).
const WANT = typeof WANT_OVERRIDE !== 'undefined' ? WANT_OVERRIDE : /AreaTransition|Waypoint/;
const T = Array.from(new Uint8Array(at('0x142faa4d8').readByteArray(24)));
const QUARTER = at('0x1436ae110').readFloat(), TWO_PI = at('0x1436ae46c').readFloat(), NO_Z = at('0x1436af6cc').readFloat();
function wstr(p) {
  const len = p.add(0x10).readU64().toNumber(), cap = p.add(0x18).readU64().toNumber();
  return (cap > 7 ? p.readPointer() : p).readUtf16String(len);
}
// A handle's path: found once by probing (a word of the handle, or of what it points to, that is a std::wstring or a
// pointer to UTF-16 text starting with an upper-case letter and containing '/'), then reused.
let layout = null;
function textAt(p) {
  try { const s = p.readUtf16String(120); if (s && s.length > 6 && /^[A-Z][A-Za-z]+\//.test(s)) return s; } catch (e) {}
  return null;
}
function probePath(h) {
  for (let i = 0; i < 12; i++) {
    let w; try { w = h.add(i * 8).readPointer(); } catch (e) { return null; }
    for (const [how, f] of [['ptr', () => textAt(w)], ['wstr', () => textAt(w.add(8)) || textAt(w.add(8).readPointer())],
                            ['deref', () => textAt(w.readPointer())]]) {
      let s = null; try { s = f(); } catch (e) {}
      if (s) return { s, at: [i, how] };
    }
  }
  return null;
}
function handlePath(h) {
  if (h.isNull()) return null;
  if (layout) {
    try {
      const w = h.add(layout[0] * 8).readPointer();
      return layout[1] === 'ptr' ? textAt(w) : layout[1] === 'deref' ? textAt(w.readPointer())
        : (textAt(w.add(8)) || textAt(w.add(8).readPointer()));
    } catch (e) { return null; }
  }
  const r = probePath(h);
  if (r) { layout = r.at; send({ handleLayout: layout, sample: r.s }); return r.s; }
  return null;
}
let tiles = 0, dumps = 0;
Interceptor.attach(at('0x142047e30'), {
  onEnter(a) {
    try {
      const ctx = a[0], rec = a[1];
      if (dumps < 3) {
        dumps++;
        const hex = Array.from(new Uint8Array(rec.readByteArray(0x58))).map(b => b.toString(16).padStart(2, '0')).join('');
        const h30 = rec.add(0x30).readPointer(), h38 = rec.add(0x38).readPointer();
        send({ dump: hex, p30: probePath(h30), p38: probePath(h38) });
      }
      const type = handlePath(rec.add(0x30).readPointer());
      if (!type || !WANT.test(type)) return;
      const tile = ctx.add(8).readPointer();
      const code = Math.min(tile.add(0x36).readU8(), 8), hoff = tile.add(0x30).readS16();
      const origin = ctx.add(0x10).readPointer();
      const ox = origin.readS32(), oy = origin.add(4).readS32();
      const info = ctx.add(0x18).readPointer().readPointer().add(0x28).readPointer();
      const w = info.add(0x60).readU8(), h = info.add(0x61).readU8();
      const x = rec.readS32(), y = rec.add(4).readS32();
      const arr = [w * 23 - x - 1, x, h * 23 - y - 1, y], t = T.slice(code * 3, code * 3 + 3);
      const gx = arr[t[1] + 2 * t[0]] + ox, gy = arr[(t[0] === 0 ? 2 : 0) + t[2]] + oy;
      let ang = rec.add(0x14).readFloat();
      if (code >= 4 && code < 8) ang = TWO_PI - ang;
      ang += (code & 3) * QUARTER;
      while (ang < 0) ang += TWO_PI;
      while (ang > TWO_PI) ang -= TWO_PI;
      const z0 = rec.add(0x10).readFloat();
      send({
        type, ao: handlePath(rec.add(0x38).readPointer()), grid: [gx, gy], rot: ang,
        z: z0 === NO_Z ? null : z0 - hoff * 7.8125, local: [x, y], offset: [rec.add(8).readFloat(), rec.add(12).readFloat()],
        tile: { origin: [ox, oy], size: [w, h], code, hoff },
      });
    } catch (e) { send({ err: String(e) }); }
  },
});
send({ ready: 'trace-tile-records', T, QUARTER });
