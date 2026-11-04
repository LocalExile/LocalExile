// The lockstep client's object tick, per frame (run with scripts/re/frida/run.py --finish-rpc finish; 07 §5): the order the objects update in (0x142183ce0 → 0x142184680/0x142184870
// per object → each component's slot +0x40; Positioned's is 0x141e32710), each walker's tick (accumulator +0x244, step
// time +0x240, elapsed +0x238, cell before and after, the body checks 0x141e47a40 made inside it), every segment setup
// (0x141e4d180: the accumulator before and after, old and new step time) and every forced position set (0x141e31800),
// plus each 0x1ab's from cell against the actor's cell. Records carry q (QPC ticks) and n (the frame counter).
const m = Process.getModuleByName('Game.patched.exe');
const at = va => m.base.add(ptr(va).sub(ptr('0x140000000')));
const k = Process.getModuleByName('kernel32.dll');
const qpc = new NativeFunction(k.getExportByName('QueryPerformanceCounter'), 'int', ['pointer']);
const qpf = new NativeFunction(k.getExportByName('QueryPerformanceFrequency'), 'int', ['pointer']);
const qbuf = Memory.alloc(8);
const now = () => { qpc(qbuf); return qbuf.readU64().toNumber(); };
qpf(qbuf); send({qpf: qbuf.readU64().toNumber()});
let buffer = [], frame = 0, seq = 0, fullOrderFrames = 0, mode = -1;
const flush = () => { if (buffer.length >= 512) { send({r: buffer}); buffer = []; } };
const idOf = obj => obj.add(0x88).readU32();
const cell = p => [p.add(0x294).readS32(), p.add(0x298).readS32()];
// The world's frame step: one call per server frame in lockstep (dt in XMM1, not readable here).
Interceptor.attach(at('0x142183ce0'), {
  onEnter(a) {
    frame++; seq = 0;
    mode = a[0].add(0x984).readU8();
    if (frame <= 400 || frame % 300 === 0) fullOrderFrames = frame;
    buffer.push({k: 'F', q: now(), n: frame, mode});
    flush();
  },
});
// The per-object updates: the ordinary helper and the high-id one. Every object is logged for the first frames and
// every 300th; otherwise only objects whose Positioned has a step time (walkers under way).
function objectUpdate(helper) {
  return {
    onEnter(a) {
      const obj = a[1];
      seq++;
      let moving = 0;
      try {
        const p = obj.add(0x98).readPointer();
        if (!p.isNull()) moving = p.add(0x240).readFloat() !== 0 ? 1 : 0;
      } catch (e) { moving = -1; }
      if (fullOrderFrames === frame || moving === 1) {
        buffer.push({k: 'O', n: frame, s: seq, id: idOf(obj), h: helper, mv: moving});
        flush();
      }
    },
  };
}
Interceptor.attach(at('0x142184680'), objectUpdate(0));
Interceptor.attach(at('0x142184870'), objectUpdate(1));
// The Positioned tick of a walker: accumulator, step time, elapsed and cell before and after, and the body checks inside.
let tick = null;
Interceptor.attach(at('0x141e32710'), {
  onEnter(a) {
    const p = a[0];
    const step = p.add(0x240).readFloat();
    if (step === 0) { this.skip = true; return; }
    this.skip = false;
    this.p = p;
    this.id = idOf(p.add(8).readPointer());
    this.acc0 = p.add(0x244).readFloat();
    this.step0 = step;
    this.el0 = p.add(0x238).readFloat();
    this.c0 = cell(p);
    this.f5 = p.add(0x1e5).readU8();
    this.checks = [];
    tick = this;
  },
  onLeave() {
    if (this.skip) return;
    tick = null;
    const p = this.p;
    buffer.push({k: 'T', q: now(), n: frame, s: seq, id: this.id, a0: this.acc0, a1: p.add(0x244).readFloat(), st0: this.step0,
      st1: p.add(0x240).readFloat(), e0: this.el0, e1: p.add(0x238).readFloat(), c0: this.c0, c1: cell(p), f5: this.f5, b: this.checks});
    flush();
  },
});
// The step's body check (map, &cell, threshold, layer) → blocked.
Interceptor.attach(at('0x141e47a40'), {
  onEnter(a) {
    this.t = tick;
    if (this.t === null) return;
    this.cell = [a[1].readS32(), a[1].add(4).readS32()];
    this.thr = a[2].toInt32();
  },
  onLeave(r) {
    if (this.t === null) return;
    this.t.checks.push([this.cell[0], this.cell[1], this.thr, r.toInt32() & 0xff]);
  },
});
// A segment's setup (walker struct = Positioned + 0x1e8): the accumulator is rescaled by new/old when the old step time
// is above zero.
Interceptor.attach(at('0x141e4d180'), {
  onEnter(a) {
    const w = a[0];
    this.w = w;
    this.id = idOf(w.sub(0x1e8).add(8).readPointer());
    this.acc0 = w.add(0x5c).readFloat();
    this.old = w.add(0x58).readFloat();
    this.from = [a[1].toInt32(), a[1].shr(32).toInt32()];
    this.to = [a[3].toInt32(), a[3].shr(32).toInt32()];
  },
  onLeave() {
    buffer.push({k: 'S', q: now(), n: frame, s: seq, id: this.id, a0: this.acc0, a1: this.w.add(0x5c).readFloat(), old: this.old,
      nw: this.w.add(0x58).readFloat(), from: this.from, to: this.to, inTick: tick !== null ? 1 : 0});
    flush();
  },
});
// A forced position set (an action start whose pair differs from the actor's cell, 0x141e8e7e0 in lockstep).
Interceptor.attach(at('0x141e31800'), {
  onEnter(a) {
    const p = a[0];
    this.p = p;
    this.id = idOf(p.add(8).readPointer());
    this.acc0 = p.add(0x244).readFloat();
    this.step0 = p.add(0x240).readFloat();
    this.c0 = cell(p);
    this.target = [a[1].readS32(), a[1].add(4).readS32()];
  },
  onLeave() {
    const p = this.p;
    buffer.push({k: 'P', q: now(), n: frame, s: seq, id: this.id, a0: this.acc0, a1: p.add(0x244).readFloat(), st0: this.step0,
      st1: p.add(0x240).readFloat(), c0: this.c0, tgt: this.target, c1: cell(p)});
    flush();
  },
});
// Every 0x1ab: the server's from cell against the actor's cell as the message is applied.
Interceptor.attach(at('0x1421940a0'), {
  onEnter(a) {
    const actor = a[0], msg = a[1], type = msg.add(8).readU16();
    if (type !== 0x1ab) return;
    const p = actor.add(8).readPointer().add(0x98).readPointer();
    buffer.push({k: 'A', q: now(), n: frame, s: seq, id: msg.add(0x10).readU32(), from: [msg.add(0x2c).readS32(), msg.add(0x30).readS32()],
      c: cell(p), skill: msg.add(0x34).readU16(), flags: msg.add(0x38).readU16(), acc: p.add(0x244).readFloat(), st: p.add(0x240).readFloat()});
    flush();
  },
});
rpc.exports = { finish() { if (buffer.length) { send({r: buffer}); buffer = []; } } };
send({ready: true});
