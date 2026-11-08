// Self-contained. Traces the client's own pathfinding for the local player's walks (Agents/docs/protocol/07 §5):
//   python scripts/re/frida/run.py Game.patched.exe --log logs/pathfind.jsonl scripts/re/frida/trace-pathfind.js
// - moveTo: Pathfinding component MoveTo 0x141e81c20 (component, target, min, max, precision, tolerance, layer flag).
// - path: the pathfinder 0x141e50f90 (target, entity, 13 more args) with the start cell (Positioned +0x294), the mover's
//   size (+0x1e3), its result, the waypoint list it wrote (goal first, start last) and the reached / out flags.
// - step: each straight move along the path (Positioned 0x141e37740): from, to, the float at +8 (speed for path walks).
// - layers: once, the pathfinder's walk map (0x141d888f0's map: +0x18/+0x20 size in 23-cell tiles, +0x100 row stride,
//   3 layers of 4-bit cells at +0xd0 + 0x18 i) to Tools/re/out/pathmap-<i>.bin + pathmap.json.
const m = Process.getModuleByName('Game.patched.exe');
const at = va => m.base.add(ptr(va).sub(ptr('0x140000000')));
const va = p => '0x' + p.sub(m.base).add(ptr('0x140000000')).toString(16);
const OUT = m.path.replace(/[\\/][^\\/]+[\\/][^\\/]+$/, '') + '/Tools/re/out/';
const t0 = Date.now();
const now = () => ((Date.now() - t0) / 1000).toFixed(3);
const xy = p => [p.readS32(), p.add(4).readS32()];
let depth = 0, dumped = false;

Interceptor.attach(at('0x141e81c20'), {
  onEnter(a) {
    send({t: now(), moveTo: va(this.returnAddress), target: xy(a[1].readPointer()), min: a[2].toInt32(), max: a[3].toInt32(),
          precision: a[4].toInt32(), tolerance: a[5].toInt32() << 24 >> 24, layerFlag: a[6].toInt32() & 0xff,
          flags54c: a[0].add(0x54c).readU8(), phase52e: a[0].add(0x52e).readU8()});
  }
});

Interceptor.attach(at('0x141e50f90'), {
  onEnter(a) {
    this.d = depth++;
    const ent = a[1], pos = ent.add(0x98).readPointer();
    this.out = a[8]; this.count = a[9]; this.reached = a[10]; this.flag = a[11];
    this.info = {t: now(), path: this.d, target: xy(a[0]), start: xy(pos.add(0x294)), size: pos.add(0x1e3).readS8(),
      layer2c0: pos.add(0x2c0).readU16(), f1e7: pos.add(0x1e7).readU8(),
      args: [2, 3, 4, 5, 6, 7, 12, 13, 14].map(i => a[i].toInt32()), caller: va(this.returnAddress)};
  },
  onLeave(r) {
    depth--;
    const n = this.count.readU64().toNumber();
    const wps = [];
    for (let i = 0; i < Math.min(n, 0x88); i++) wps.push(xy(this.out.add(8 * i)));
    Object.assign(this.info, {ret: r.toInt32() & 0xff, n, wps, reached: this.reached.readU8(), outFlag: this.flag.readU8()});
    send(this.info);
  }
});

const mapHook = Interceptor.attach(at('0x141d888f0'), {
  onEnter(a) {
    if (dumped) return;
    dumped = true;
    setTimeout(() => mapHook.detach(), 0);
    const map = a[0];
    const meta = {tilesW: map.add(0x18).readS32(), tilesH: map.add(0x20).readS32(), stride: map.add(0x100).readS32(), layers: []};
    for (let i = 0; i < 3; i++) {
      const b = map.add(0xd0 + 0x18 * i).readPointer(), e = map.add(0xd8 + 0x18 * i).readPointer();
      const n = e.sub(b).toInt32();
      meta.layers.push(n);
      if (n > 0) { const f = new File(OUT + `pathmap-${i}.bin`, 'wb'); f.write(b.readByteArray(n)); f.close(); }
    }
    const f = new File(OUT + 'pathmap.json', 'w'); f.write(JSON.stringify(meta)); f.close();
    send({layers: meta, map: map.toString()});
  }
});

Interceptor.attach(at('0x141e37740'), {
  onEnter(a) {
    const p = a[0], mv = a[1];
    send({t: now(), step: xy(p.add(0x294)), to: xy(mv.readPointer()), speed: mv.add(8).readFloat(), caller: va(this.returnAddress)});
  }
});

send('trace-pathfind ready');
