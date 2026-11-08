// Self-contained. Everything the client does to move objects, on the machine's QPC clock (the Gateway's
// Game_MOVE_TRACE=1 log uses the same clock), to check the server's copy of client movement step for step
// (Agents/docs/protocol/07-latency-visibility-movement.md §5):
//   python scripts/re/frida/run.py Game.patched.exe --log logs/movement.jsonl scripts/re/frida/trace-movement.js
// Records (field q = QPC ticks; ids are server object ids, learned from the Actor and Positioned message handlers):
// - qpf: the QPC frequency, once.
// - msg: an Actor (0x14219aa10) or Positioned (0x141e33fd0) message as handled: id, message id, raw first 0x40 bytes.
// - frame: the player's Positioned tick (0x141e35a50) started: once per frame (the frame's time).
// - cell: an object's grid cell changed during its tick (a Bresenham step), with its step time (+0x240).
// - stop: an object's walk ended in its tick (step time went to 0) and where.
// - path: a pathfinder call (0x141e50f90): mover, size, start, target, the 13 other arguments, result, waypoints, and
//   every object its collision test (0x141e65ae0) looked at, with cell, size, flags and whether it blocked.
// - seg: a straight move (0x141e37740): mover, from, to, the float at +8 (speed), the int at +0xc (height).
// - moveTo / inRange: Pathfinding MoveTo 0x141e81c20 and its range check 0x141e81950, with their arguments.
// - use: the C→S 0x24/0x25 sender 0x14216ccb0 ran (the click).
// - tick: an object's movement state before and after its tick (the player every frame; others while moving).
// - speedRoll: an Actor's run speed multiplier and the TinyMT32 inputs it came from.
const m = Process.getModuleByName('Game.patched.exe');
const at = va => m.base.add(ptr(va).sub(ptr('0x140000000')));
const va = p => { try { return '0x' + p.sub(m.base).add(ptr('0x140000000')).toString(16); } catch (e) { return p.toString(); } };
const k32 = Process.getModuleByName('kernel32.dll');
const qpcFn = new NativeFunction(k32.getExportByName('QueryPerformanceCounter'), 'int', ['pointer']);
const qpfFn = new NativeFunction(k32.getExportByName('QueryPerformanceFrequency'), 'int', ['pointer']);
const qbuf = Memory.alloc(8);
const q = () => { qpcFn(qbuf); return qbuf.readU64().toString(); };
qpfFn(qbuf);
send({qpf: qbuf.readU64().toString()});

const xy = p => [p.readS32(), p.add(4).readS32()];
const hex = (p, n) => Array.from(new Uint8Array(p.readByteArray(n))).map(b => b.toString(16).padStart(2, '0')).join('');
const idOf = new Map();   // Positioned pointer → server object id
let playerPos = null;
const name = p => idOf.has(p.toString()) ? idOf.get(p.toString()) : p.toString();

Interceptor.attach(at('0x14219aa10'), {
  onEnter(a) {
    const actor = a[0], msg = a[1], id = msg.add(0x10).readU32();
    const pos = actor.add(8).readPointer().add(0x98).readPointer();
    idOf.set(pos.toString(), id);
    if (actor.add(0x1c8).readU8() !== 0) playerPos = pos;
    send({q: q(), msg: msg.add(8).readU16(), id, hex: hex(msg, 0x40)});
  }
});
Interceptor.attach(at('0x141e33fd0'), {
  onEnter(a) {
    const pos = a[0], msg = a[1], id = msg.add(0x10).readU32();
    idOf.set(pos.toString(), id);
    send({q: q(), msg: msg.add(8).readU16(), id, positioned: 1,
          hex: hex(msg, 0x40)});
  }
});

// The tick's movement state: accumulator +0x244, step time +0x240, elapsed +0x238 (+= the tick's dt), total +0x23c,
// the segment's finish flag +0x1f0, the time curve +0x308 and its length +0x2f0.
const tickState = p => [p.add(0x244).readFloat(), p.add(0x240).readFloat(), p.add(0x238).readFloat(), p.add(0x23c).readFloat(),
  p.add(0x1f0).readU8(), p.add(0x308).readPointer().isNull() ? 0 : 1, p.add(0x2f0).readFloat()];
Interceptor.attach(at('0x141e35a50'), {
  onEnter(a) {
    this.p = a[0];
    this.cell = xy(a[0].add(0x294));
    this.step = a[0].add(0x240).readFloat();
    this.before = tickState(a[0]);
    this.player = playerPos !== null && a[0].equals(playerPos);
    if (this.player) send({q: q(), frame: 1});
  },
  onLeave() {
    const c = xy(this.p.add(0x294)), s = this.p.add(0x240).readFloat();
    const moved = c[0] !== this.cell[0] || c[1] !== this.cell[1];
    if (moved) send({q: q(), cell: c, id: name(this.p), from: this.cell, step: this.step});
    if (this.player || moved || this.before[1] !== 0) send({q: q(), tick: name(this.p), before: this.before, after: tickState(this.p)});
    if (this.step !== 0 && s === 0) send({q: q(), stop: c, id: name(this.p)});
  }
});

// Actor setup 0x141ea32d0: the run speed multiplier +0x1a8 = 1 + v / 100, v drawn by TinyMT32 in [range +0xa0, +0xa4]
// of the actor data (+0x188), seeded with (entity → +8 → +0x28 → +8, entity +0x88).
Interceptor.attach(at('0x141ea32d0'), {
  onEnter(a) { this.actor = a[0]; },
  onLeave() {
    try {
      const actor = this.actor, ent = actor.add(8).readPointer(), data = actor.add(0x188).readPointer();
      const pos = ent.add(0x98).readPointer();
      send({q: q(), speedRoll: name(pos), positioned: pos.toString(), seedA: ent.add(8).readPointer().add(0x28).readPointer().add(8).readS32(),
            seedB: ent.add(0x88).readS32(), min: data.add(0xa0).readS32(), max: data.add(0xa4).readS32(),
            mult: actor.add(0x1a8).readFloat()});
    } catch (e) { send({speedRollError: e.message}); }
  }
});

let inPath = null;
Interceptor.attach(at('0x141e50f90'), {
  onEnter(a) {
    const pos = a[1].add(0x98).readPointer();
    this.out = a[8]; this.count = a[9]; this.reached = a[10]; this.flag = a[11];
    this.info = {q: q(), path: name(pos), target: xy(a[0]), start: xy(pos.add(0x294)), size: pos.add(0x1e3).readS8(),
      args: [2, 3, 4, 5, 6, 7, 12, 13, 14].map(i => a[i].toInt32()), caller: va(this.returnAddress), objects: {}};
    this.outer = inPath;
    inPath = this.info;
  },
  onLeave(r) {
    inPath = this.outer;
    const n = this.count.readU64().toNumber();
    const wps = [];
    for (let i = 0; i < Math.min(n, 0x88); i++) wps.push(xy(this.out.add(8 * i)));
    Object.assign(this.info, {ret: r.toInt32() & 0xff, n, wps, reached: this.reached.readU8(), outFlag: this.flag.readU8(),
      depth: this.outer === null ? 0 : 1});
    send(this.info);
  }
});
Interceptor.attach(at('0x141e65ae0'), {
  onEnter(a) {
    if (inPath === null) return;
    this.o = a[1];
    this.key = a[1].toString();
  },
  onLeave(r) {
    if (inPath === null || this.o === undefined || inPath.objects[this.key]) return;
    const o = this.o;
    inPath.objects[this.key] = {id: name(o), cell: xy(o.add(0x294)), size: o.add(0x1e3).readS8(), f1e5: o.add(0x1e5).readU8(),
      f1e6: o.add(0x1e6).readU8(), f1e0: o.add(0x1e0).readU16(), team: o.add(0x158).readPointer().add(0x58).readS32(),
      blocks: r.toInt32() & 0xff};
  }
});

Interceptor.attach(at('0x141e37740'), {
  onEnter(a) {
    const p = a[0], mv = a[1];
    send({q: q(), seg: name(p), from: xy(p.add(0x294)), to: xy(mv.readPointer()), speed: mv.add(8).readFloat(),
          height: mv.add(0xc).readS32(), caller: va(this.returnAddress)});
  }
});
Interceptor.attach(at('0x141e81c20'), {
  onEnter(a) {
    const pos = a[0].add(8).readPointer().add(0x98).readPointer();
    send({q: q(), moveTo: name(pos), target: xy(a[1].readPointer()), min: a[2].toInt32(), max: a[3].toInt32(),
          precision: a[4].toInt32(), tolerance: a[5].toInt32() << 24 >> 24, layer: a[6].toInt32() & 0xff,
          flags54c: a[0].add(0x54c).readU8(), caller: va(this.returnAddress)});
  }
});
Interceptor.attach(at('0x141e81950'), {
  onEnter(a) {
    const pos = a[0].add(8).readPointer().add(0x98).readPointer();
    send({q: q(), inRange: name(pos), target: [a[1].toInt32(), a[1].shr(32).toInt32()], min: a[2].toInt32(),
          max: a[3].toInt32(), precision: a[4].toInt32(), tolerance: a[5].toInt32(), caller: va(this.returnAddress)});
  }
});
Interceptor.attach(at('0x14216ccb0'), { onEnter() { send({q: q(), use: 1, caller: va(this.returnAddress)}); } });

send('trace-movement ready');
