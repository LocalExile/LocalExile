// Dumps the terrain walkability the client builds its "Static Walkability Texture" from (load after common.js).
// Hook: right after the provider call `call r10` (vtable +0x50) in 0x1410dff50, rax points to
//   { i32 w, i32 h; vector<T> a [+0x08 begin, +0x10 end]; vector<T> b [+0x20 begin, +0x28 end] }.
// Files: Tools/re/out/walk-{meta.json,a.bin,b.bin}. The texture itself is (w+2)/3 x (h+2)/3.
// <repo>/Tools/re/out/, from the client's own path (<repo>/Client/Game.patched.exe).
const OUT = Process.enumerateModules()[0].path.replace(/[\\/][^\\/]+[\\/][^\\/]+$/, '') + '/Tools/re/out/';
let dumps = 0;
function save(name, p, n) {
  if (p.isNull() || n <= 0) return 0;
  const f = new File(OUT + name, 'wb');
  f.write(p.readByteArray(n));
  f.close();
  return n;
}
Interceptor.attach(at('0x1410e00bd'), function () {
  if (dumps++ > 0) return;
  const r = this.context.rax;
  const w = r.readS32(), h = r.add(4).readS32();
  const a0 = r.add(0x08).readPointer(), a1 = r.add(0x10).readPointer();
  const b0 = r.add(0x20).readPointer(), b1 = r.add(0x28).readPointer();
  const na = save('walk-a.bin', a0, a1.sub(a0).toInt32());
  const nb = save('walk-b.bin', b0, b1.sub(b0).toInt32());
  const meta = { w, h, aBytes: na, bBytes: nb };
  const f = new File(OUT + 'walk-meta.json', 'w');
  f.write(JSON.stringify(meta));
  f.close();
  send({ walkability: meta });
});
send({ ready: 'dump-walkability' });
