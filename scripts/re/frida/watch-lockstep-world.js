// Self-contained. In lockstep (network mode 2) the world (0x14218a2a0, vtable 0x1435e4db8) runs its world messages one
// server frame at a time from a queue of frame lengths (+0x9c0 deque, count +0x9d8) and a message list (+0x9e0/+0x9e8).
// This finds who fills them: hardware watchpoints on +0x9d8 and +0x9e8 report the writing instruction and a backtrace.
//   python scripts/re/frida/run.py Game.patched.exe --log logs/lockstep-world.jsonl scripts/re/frida/watch-lockstep-world.js
const m = Process.getModuleByName('Game.patched.exe');
const at = va => m.base.add(ptr(va).sub(ptr('0x140000000')));
const va = p => { try { return '0x' + p.sub(m.base).add(ptr('0x140000000')).toString(16); } catch (e) { return p.toString(); } };
let world = null, armed = false;
const seen = {};

Process.setExceptionHandler(d => {
  if (d.type !== 'single-step' && d.type !== 'breakpoint') return false;
  const key = va(d.address);
  if (!seen[key]) {
    seen[key] = 1;
    send({writer: key, bt: Thread.backtrace(d.context, Backtracer.ACCURATE).slice(0, 12).map(va),
          frames: world.add(0x9d8).readU64().toString(), messages: world.add(0x9e8).readPointer().sub(world.add(0x9e0).readPointer()).toInt32() / 8});
  }
  return true;
});

function arm() {
  if (armed || world === null) return;
  armed = true;
  for (const t of Process.enumerateThreads()) {
    try {
      t.setHardwareWatchpoint(0, world.add(0x9d8), 8, 'w');
      t.setHardwareWatchpoint(1, world.add(0x9e8), 8, 'w');
    } catch (e) { send({armError: e.message, thread: t.id}); }
  }
  send({armed: world.toString(), frames: world.add(0x9d8).readU64().toString()});
}

Interceptor.attach(at('0x14218a2a0'), {
  onEnter(a) {
    if (world === null) { world = a[0]; setTimeout(arm, 0); }
  }
});
send('watch-lockstep-world ready');
