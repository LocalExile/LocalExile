// Why a lockstep client's tick lag byte reads 0 at first (07 §6, "Network mode"); load after common.js and run with
// scripts/re/frida/run.py. Per network update (0x14214f1d0, instance in rcx, frame ms in r8) in state 3: the frame
// time, the unanswered-tick queue (+0xc458..+0xc460), the world's queued frames (+0x9d8), carried time (+0x988),
// speed (+0x98c) and lag counter (+0xb98). Per frame runner call (0x14218a2a0) the same world fields before and after.
// Each 0x1c pop (0x14215c280: its index, the queue before), each send of 0x1b (with its lag byte), 0x16 and 0x01ef
// (0x1421752b0: message id at +8, the tick's lag at +0x14), each 0x21 (0x14215d6d0) and each activation (0x142189fe0).
// Records carry q (QPC ticks). Every update is kept for the first 1500 after each 0x21, later only the eventful ones.
const gm = Process.getModuleByName('Game.patched.exe');
const at = va => gm.base.add(ptr(va).sub(ptr('0x140000000')));
const k32 = Process.getModuleByName('kernel32.dll');
const qpcFn = new NativeFunction(k32.getExportByName('QueryPerformanceCounter'), 'int', ['pointer']);
const qpfFn = new NativeFunction(k32.getExportByName('QueryPerformanceFrequency'), 'int', ['pointer']);
const qb = Memory.alloc(8);
const q = () => { qpcFn(qb); return qb.readU64().toNumber(); };
qpfFn(qb); send({qpf: qb.readU64().toNumber()});
let out = [], fresh = 0;
const push = r => { out.push(r); if (out.length >= 256) { send({r: out}); out = []; } };
setInterval(() => { if (out.length) { send({r: out}); out = []; } }, 500);
const ackSize = inst => inst.add(0xc460).readPointer().sub(inst.add(0xc458).readPointer()).toInt32() / 8;
const world = w => ({m: w.add(0x984).readU8(), f: w.add(0x9d8).readU32(), c: w.add(0x988).readFloat(),
  s: w.add(0x98c).readFloat(), l: w.add(0xb98).readU32()});
Interceptor.attach(at(0x14214f1d0), {
  onEnter(a) {
    const inst = a[0];
    if (inst.add(0xc3a0).readU32() !== 3) return;
    const w = inst.add(0xc278).readPointer();
    if (w.isNull()) return;
    const r = {k: 'U', q: q(), dt: a[2].toUInt32(), a: ackSize(inst), ...world(w)};
    if (fresh > 0) { fresh--; push(r); }
    else if (r.f > 1 || r.l > 0 || r.a > 1) push(r);
  },
});
Interceptor.attach(at(0x14218a2a0), {
  onEnter(a) { this.w = a[0]; this.b = world(a[0]); },
  onLeave() {
    const after = world(this.w);
    if (fresh > 0 || this.b.f > 0 || after.l > 0) push({k: 'R', q: q(), b: this.b, e: after});
  },
});
Interceptor.attach(at(0x14215c280), {
  onEnter(a) {
    const inst = a[0].add(8).readPointer();
    push({k: 'P', q: q(), i: a[0].readPointer().readPointer().add(0x12).readU16(), a: ackSize(inst)});
  },
});
Interceptor.attach(at(0x1421752b0), {
  onEnter(a) {
    const id = a[1].add(8).readU16();
    if (id === 0x1b) push({k: 'T', q: q(), lag: a[1].add(0x14).readU8(), a: ackSize(a[0])});
    else if (id === 0x16 || id === 0x1ef) push({k: 'S', q: q(), id});
  },
});
Interceptor.attach(at(0x14215d6d0), { onEnter() { fresh = 1500; push({k: 'E', q: q()}); } });
Interceptor.attach(at(0x142189fe0), { onEnter(a) { push({k: 'A', q: q(), ...world(a[0])}); } });
send({ready: true});
