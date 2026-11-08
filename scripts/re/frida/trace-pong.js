// Traces the client's handling of S→C 0x1c (load after common.js): the read (0x141fa7150), the ack-queue pop
// (0x14215c280) and calls into the logger (0x140defbc0) from the network thread, with backtraces on the first few.
let logs = 0, armed = false;
Interceptor.attach(at('0x141fa7150'), {
  onEnter(a) { this.m = a[0]; send({ read1c: 'enter', stream_d0: a[1].add(0xd0).readPointer().toString() }); },
  onLeave() { armed = true; send({ read1c: 'leave', f10: this.m.add(0x10).readU16(), f12: this.m.add(0x12).readU16(), f14: this.m.add(0x14).readU32() }); }
});
Interceptor.attach(at('0x14215c280'), {
  onEnter(a) { const inst = a[0].add(8).readPointer(); this.inst = inst;
    send({ pop: 'enter', queued: inst.add(0xc460).readPointer().sub(inst.add(0xc458).readPointer()).toInt32() / 8 }); },
  onLeave(r) { send({ pop: 'leave', sentAt: r.toString() }); }
});
Interceptor.attach(at('0x140defbc0'), function (a) {
  if (armed && logs++ < 5) send({ log: '0x' + a[0].toUInt32().toString(16), bt: Thread.backtrace(this.context, Backtracer.ACCURATE).slice(0, 10).map(va) });
});
send({ ready: 'trace-pong' });
