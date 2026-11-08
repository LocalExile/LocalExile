// Load after common.js. Server-driven moves of Positioned (S→C 0x1ba → 0x141e37460 → 0x141e37740, and the creation blob's
// f1 0x01 movement): per call the object's grid cell, rotation (+0x29c) and fixed height (+0x290), the move's duration
// (param_2 +8), height (+0xc) and flag bytes (+0x90..+0x95), and whether the actual move (Positioned vtable +0xd8) runs:
// it is skipped when the object's check (object +8 → +0x78, vtable +0x1b0) answers non-zero.
const hooked = {};
Interceptor.attach(at('0x141e37740'), {
  onEnter(args) {
    const p = args[0], m2 = args[1];
    const flags = [];
    for (let i = 0x90; i <= 0x95; i++) flags.push(m2.add(i).readU8());
    const mv = p.readPointer().add(0xd8).readPointer();
    if (!hooked[mv]) {
      hooked[mv] = 1;
      Interceptor.attach(mv, { onEnter(a) { send({d8: va(mv), pos: a[0].toString(), x: a[1].readS32(), y: a[1].add(4).readS32(), h: a[2].toInt32()}); } });
    }
    send({move: p.toString(), grid: [p.add(0x294).readS32(), p.add(0x298).readS32()], rot: p.add(0x29c).readFloat(),
          fixedHeight: p.add(0x290).readS32(), dest: [m2.readPointer().readS32(), m2.readPointer().add(4).readS32()],
          duration: m2.add(8).readFloat(), height: m2.add(0xc).readS32(), flags, caller: va(this.returnAddress)});
  }
});
send('trace-move ready');
