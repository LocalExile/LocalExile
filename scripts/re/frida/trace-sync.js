// Self-contained. Where the client really has every object, sampled on the machine's QPC clock, to compare with the
// server's estimate (Game_TRACE=sync on the Gateway; compare both with scripts/re/sync_report.py):
//   python scripts/re/frida/run.py <pid> --log logs/sync-client.jsonl scripts/re/frida/trace-sync.js
// Records (q = QPC ticks; ids are server object ids):
// - qpf: the QPC frequency, once.
// - s: a sample every 50 ms: [[id, x, y, moving], ...], the local player first (id 0x1000), then every object that
//   got an Actor or Positioned message, each from Positioned +0x294/+0x298. Moving is the step time +0x240 != 0.
// - echo: an S→C 0x1ab for the local player (sequence >= 0), with the pair the server reports, the target and the
//   client's own cell, before the handler ran, and the distance. Whether it snapped shows in the next sample.
// - tp: an S→C 0x1b7 for the local player, with the cell and the client's own cell before it.
// - end: an S→C 0x1ac for the local player.
// - use: the client sent a C→S 0x24/0x25 (0x142166300): the action's sequence and target, and the client's cell.
// - click: the client asked for an action (0x14215f700). In lockstep nothing starts until the server answers.
// - gone: an object was removed (0x2bd): its pointer is no longer read.
const m = Process.getModuleByName('Game.patched.exe');
const at = va => m.base.add(ptr(va).sub(ptr('0x140000000')));
const k32 = Process.getModuleByName('kernel32.dll');
const qpcFn = new NativeFunction(k32.getExportByName('QueryPerformanceCounter'), 'int', ['pointer']);
const qpfFn = new NativeFunction(k32.getExportByName('QueryPerformanceFrequency'), 'int', ['pointer']);
const qbuf = Memory.alloc(8);
const q = () => { qpcFn(qbuf); return qbuf.readU64().toString(); };
qpfFn(qbuf);
send({qpf: qbuf.readU64().toString()});

const PLAYER = 0x1000;
const cell = p => [p.add(0x294).readS32(), p.add(0x298).readS32()];
const objects = new Map();   // server object id → Positioned pointer
let player = null;

const learn = (id, positioned) => {
  objects.set(id, positioned);
};

Interceptor.attach(at('0x1421940a0'), {
  onEnter(a) {
    const actor = a[0], msg = a[1], type = msg.add(8).readU16(), id = msg.add(0x10).readU32();
    const pos = actor.add(8).readPointer().add(0x98).readPointer();
    learn(id, pos);
    // A lockstep client never sets the flag, so the player is known by its id there.
    if (actor.add(0x1c8).readU8() === 0 && id !== PLAYER) return;
    player = pos;
    if (type === 0x1ab) {
      const seq = msg.add(0x36).readS16();
      if (seq < 0) return;
      const server = [msg.add(0x2c).readS32(), msg.add(0x30).readS32()], client = cell(pos);
      const dx = server[0] - client[0], dy = server[1] - client[1];
      send({q: q(), echo: seq & 0xffff, server, client, target: [msg.add(0x24).readS32(), msg.add(0x28).readS32()],
            d: Math.sqrt(dx * dx + dy * dy)});
    } else if (type === 0x1ac) send({q: q(), end: cell(pos)});
  }
});
Interceptor.attach(at('0x141e30c90'), {
  onEnter(a) {
    const pos = a[0], msg = a[1], type = msg.add(8).readU16(), id = msg.add(0x10).readU32();
    learn(id, pos);
    if (pos.equals(player ?? ptr(0)) && type === 0x1b7) send({q: q(), tp: [msg.add(0x20).readS32(), msg.add(0x24).readS32()], client: cell(pos)});
  }
});
Interceptor.attach(at('0x14215f700'), {onEnter(a) { send({q: q(), click: a[3].isNull() ? null : [a[3].readS32(), a[3].add(4).readS32()]}); }});

Interceptor.attach(at('0x142166300'), {
  onEnter(a) {
    if (player === null) return;
    const action = a[2];
    send({q: q(), use: action.add(0x11a).readS16() & 0xffff, target: [action.add(0x130).readS32(), action.add(0x134).readS32()],
          client: cell(player)});
  }
});

// The world handler routes 0x2bd to the object's removal; the id is at message +0x10 (07 §4).
Interceptor.attach(at('0x142187480'), {
  onEnter(a) {
    for (const arg of [a[1], a[2]]) {
      try {
        if (arg.add(8).readU16() !== 0x2bd) continue;
        const id = arg.add(0x10).readU32();
        if (objects.delete(id)) send({q: q(), gone: id});
        return;
      } catch (e) { /* not the message argument */ }
    }
  }
});

setInterval(() => {
  const samples = [];
  if (player !== null) {
    try { const c = cell(player); samples.push([PLAYER, c[0], c[1], player.add(0x240).readFloat() !== 0 ? 1 : 0]); }
    catch (e) { player = null; }
  }
  for (const [id, p] of objects) {
    if (id === PLAYER) continue;
    try { const c = cell(p); samples.push([id, c[0], c[1], p.add(0x240).readFloat() !== 0 ? 1 : 0]); }
    catch (e) { objects.delete(id); }
  }
  if (samples.length) send({q: q(), s: samples});
}, 50);

send('trace-sync ready');
