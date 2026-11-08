// Self-contained. When the client takes, starts and stops other actors' actions, on the machine's QPC clock, to see
// whether a monster's order became a swing:
//   python scripts/re/frida/run.py <pid> --log logs/monster-actions.jsonl scripts/re/frida/trace-monster-actions.js
// Records (q = QPC ticks; id = the server object id; the local player is left out):
// - order: an S→C 0x1ab, before the handler ran: skill, sequence, flags, target object, the position pair.
// - ack: an S→C 0x1ac, with the pending (+0x1b8) and current (+0x1b0) action's skill.
// - start: the action became current (0x141e9b500), with its skill and the actor's cell.
// - stop: the current action stopped (0x141e96020).
const m = Process.getModuleByName('Game.patched.exe');
const at = va => m.base.add(ptr(va).sub(ptr('0x140000000')));
const k32 = Process.getModuleByName('kernel32.dll');
const qpcFn = new NativeFunction(k32.getExportByName('QueryPerformanceCounter'), 'int', ['pointer']);
const qbuf = Memory.alloc(8);
const q = () => { qpcFn(qbuf); return qbuf.readU64().toString(); };

const local = actor => actor.add(0x1c8).readU8() !== 0;
const idOf = actor => actor.add(8).readPointer().add(0x88).readU32();
const cell = actor => {
  const pos = actor.add(8).readPointer().add(0x98).readPointer();
  return [pos.add(0x294).readS32(), pos.add(0x298).readS32()];
};
const skill = (actor, offset) => {
  const action = actor.add(offset).readPointer();
  return action.isNull() ? 0 : action.add(0x118).readU16();
};

Interceptor.attach(at('0x14219aa10'), {
  onEnter(a) {
    const actor = a[0], msg = a[1], type = msg.add(8).readU16();
    if (local(actor)) return;
    if (type === 0x1ab)
      send({q: q(), order: idOf(actor), skill: msg.add(0x34).readU16(), seq: msg.add(0x36).readU16(),
            flags: msg.add(0x38).readU16(), from: [msg.add(0x2c).readS32(), msg.add(0x30).readS32()],
            to: [msg.add(0x24).readS32(), msg.add(0x28).readS32()], at: cell(actor)});
    else if (type === 0x1ac)
      send({q: q(), ack: idOf(actor), pending: skill(actor, 0x1b8), current: skill(actor, 0x1b0), at: cell(actor)});
  }
});
Interceptor.attach(at('0x141e9b500'), {
  onEnter(a) {
    if (!local(a[0])) send({q: q(), start: idOf(a[0]), skill: skill(a[0], 0x1b0), at: cell(a[0])});
  }
});
Interceptor.attach(at('0x141e96020'), {
  onEnter(a) {
    if (!local(a[0]) && !a[0].add(0x1b0).readPointer().isNull())
      send({q: q(), stop: idOf(a[0]), skill: skill(a[0], 0x1b0), at: cell(a[0])});
  }
});
send({ready: true});
