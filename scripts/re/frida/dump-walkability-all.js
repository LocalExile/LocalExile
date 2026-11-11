// Like dump-walkability.js, but dumps every generated area (load after common.js): Tools/re/out/walk-<n>-{a,b}.bin and
// walk-<n>-meta.json, n = 0, 1, … in generation order. Used to capture generated areas (e.g. The Coast) per seed.
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
  const n = dumps++;
  const r = this.context.rax;
  const w = r.readS32(), h = r.add(4).readS32();
  const a0 = r.add(0x08).readPointer(), a1 = r.add(0x10).readPointer();
  const b0 = r.add(0x20).readPointer(), b1 = r.add(0x28).readPointer();
  const meta = { n, w, h, aBytes: save(`walk-${n}-a.bin`, a0, a1.sub(a0).toInt32()), bBytes: save(`walk-${n}-b.bin`, b0, b1.sub(b0).toInt32()) };
  const f = new File(OUT + `walk-${n}-meta.json`, 'w');
  f.write(JSON.stringify(meta));
  f.close();
  send({ walkability: meta });
});
send({ ready: 'dump-walkability-all' });
