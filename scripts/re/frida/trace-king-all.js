// One whole official King-fight capture: every 0x1ab action field, every skill the client adds, the channel start,
// buff adds, server/client object creates and projectiles, plus the last message ids and a first-chance exception
// report. Self-contained: load it alone (it finds the running client's main module itself).
//   python scripts/re/frida/run.py PathOfExileSteam.exe --follow --log logs/frida-king-all.jsonl ^
//       scripts/re/frida/trace-king-all.js
// Then do the whole Crux fight. No need to make it crash: the point is to search the resulting JSONL afterwards.
// Process.mainModule is not the game executable when Frida attaches to the Steam client (it reports a helper DLL), so
// find the client image by name. The same script works for LocalExile's Game.patched.exe.
const image = Process.enumerateModules().find(m => /^(PathOfExileSteam|Game\.patched)\.exe$/i.test(m.name));
if (!image) throw new Error('client image not found: ' + Process.enumerateModules().map(m => m.name).join(', '));
const base = ptr('0x140000000');
const at = va => image.base.add(ptr(va).sub(base));
const va = p => {
  if (p === null || p === undefined) return null;
  if (p.compare(image.base) >= 0 && p.compare(image.base.add(image.size)) < 0)
    return '0x' + p.sub(image.base).add(base).toString(16);
  const mod = Process.findModuleByAddress(p);
  return mod ? `${mod.name}+0x${p.sub(mod.base).toString(16)}` : p.toString();
};
const u8 = (p, o) => { try { return p.add(o).readU8(); } catch (e) { return null; } };
const u16 = (p, o) => { try { return p.add(o).readU16(); } catch (e) { return null; } };
const u32 = (p, o) => { try { return p.add(o).readU32(); } catch (e) { return null; } };
const ptrAt = (p, o) => { try { return p.add(o).readPointer(); } catch (e) { return null; } };
const stamp = () => Date.now();

// The last accepted S->C message ids, for the exception report (the clock messages would push everything out).
const lastMessages = [];
const skipped = new Set([0, 0x1c, 0x2b5]);
Interceptor.attach(at('0x14299e300'), {
  onEnter(args) { this.id = args[1].toUInt32() & 0xffff; },
  onLeave(accepted) {
    if (skipped.has(this.id) || (accepted.toUInt32() & 0xff) === 0) return;
    lastMessages.push('0x' + this.id.toString(16));
    if (lastMessages.length > 64) lastMessages.shift();
  }
});

// Skills added to an ActiveSkillManager: the id at +0x40 is what the 0x1ab action carries.
const skillName = s => { try { return s.add(0x48).readPointer().readPointer().readPointer().readUtf16String(); } catch (e) { return '?'; } };
Interceptor.attach(at('0x141eea6a0'), {
  onEnter(args) {
    const s = args[1];
    send({ts: stamp(), kind: 'skill-added', mgr: args[0].toString(), id: u16(s, 0x40), w42: u16(s, 0x42),
      name: skillName(s), bt: Thread.backtrace(this.context, Backtracer.FUZZY).slice(0, 10).map(va)});
  }
});

// The action lookup behind S->C 0x1ab: 0x141ee97f0(manager, id). It returns the actor's action for that skill id,
// or null when the manager has no such skill, which is why an unknown 0x4000+ id plays nothing. Log only the King's
// ids (0x0102 default attack and 0x4000-0x4014) to keep the rest of the client's lookups out of the trace.
Interceptor.attach(at('0x141ee97f0'), {
  onEnter(args) {
    this.mgr = args[0].toString();
    this.id = args[2].toUInt32() & 0xffff;
    this.out = args[1];
  },
  onLeave(ret) {
    if (!(this.id >= 0x4000 && this.id <= 0x4014) && this.id !== 0x0102) return;
    let action = null;
    try { action = va(this.out.readPointer()); } catch (e) { }
    send({ts: stamp(), kind: 'skill-lookup', mgr: this.mgr, id: this.id, action});
  }
});

// S->C 0x1ab: actor, target, flags, start/target coordinates, skill and sequence. The raw field window is
// deliberately wide; the JSONL is searched afterwards, so extra values cost nothing. The King's actor pointer from
// the humanoid's actions feeds the body-controller hook below: the clip index tells which stance animation the client
// actually plays (the .act's stance2 Idle is call_loop_01 = 3, stance1 Idle is idle_01 = 17, ChangeToStance1 is
// call_end_01 = 2).
let kingActor = null, kingUntil = 0;
Interceptor.attach(at('0x1421940a0'), {
  onEnter(args) {
    const m = args[1];
    if (u16(m, 8) !== 0x1ab) return;
    const fields = {};
    for (let off = 0x18; off <= 0x40; off += 2) fields['u16@' + off.toString(16)] = u16(m, off);
    const actor = u32(m, 0x10), skill = u16(m, 0x34);
    send({ts: stamp(), kind: 'action-start', actor, target: u32(m, 0x20), skill, seq: u16(m, 0x36), fields});
    if ((skill === 0x4000 || (skill >= 0x4002 && skill <= 0x4004) || skill === 0x4009
        || (skill >= 0x400a && skill <= 0x400d)) && actor !== 0x1000) {
      kingActor = args[0];
      kingUntil = Date.now() + 12000;
      send({ts: stamp(), kind: 'king-actor', actor, skill, pointer: kingActor.toString(),
        clip: kingClip(kingActor)});
    }
  }
});

// The body controller of the King's actor (actor +0x1a0, state at +0x190). Every frame while the King is tracked;
// clip 2 is call_end_01 (ChangeToStance1 from stance2), 3 call_loop_01 (stance2 Idle), 17 idle_01 (stance1 Idle).
function kingClip(actor) {
  try {
    const c = actor.add(0x1a0).readPointer();
    if (c.isNull()) return null;
    return {clip: c.add(0x190).readS32(), elapsed: c.add(0x1a4).readFloat(), speed: c.add(0x1b0).readFloat(),
      from: c.add(0x1b8).readFloat(), to: c.add(0x1bc).readFloat()};
  } catch (e) { return null; }
}
Interceptor.attach(at('0x141e8e060'), {
  onEnter(args) {
    if (kingActor === null || Date.now() > kingUntil) return;
    try { if (!args[0].equals(kingActor)) return; } catch (e) { return; }
    send({ts: stamp(), kind: 'king-clip', ...kingClip(kingActor)});
  }
});

// Action begin (0x141e8cf10): the type at +0x118 and the skill at +0x11a of the started action. Log the stance
// change (type 0x16c7) and the King's own skills only, so the rest of the client's actions stay out of the trace.
Interceptor.attach(at('0x141e8cf10'), {
  onEnter(args) {
    const p = args[1].readPointer();
    const type = u16(p, 0x118), skill = u16(p, 0x11a);
    if (type !== 0x16c7 && !(skill >= 0x4000 && skill <= 0x4014)) return;
    send({ts: stamp(), kind: 'action-begin', type, skill, from: va(this.returnAddress)});
  }
});

// GeometryChannelBase::StartChannelling: the crash location. Log the channel object, its action pointer (normally set
// by the client from the action that starts the channel), the action fields and the caller.
Interceptor.attach(at('0x1419e0c70'), {
  onEnter(args) {
    const self = args[0], action = ptrAt(self, 8);
    send({ts: stamp(), kind: 'start-channelling', self: self.toString(), action: action ? action.toString() : null,
      actionFields: action ? {
        u32_0: u32(action, 0), u32_8: u32(action, 8), u32_10: u32(action, 0x10), u32_20: u32(action, 0x20),
        u32_28: u32(action, 0x28), u32_30: u32(action, 0x30), u16_34: u16(action, 0x34), u16_36: u16(action, 0x36),
      } : null,
      ret: va(this.returnAddress), bt: Thread.backtrace(this.context, Backtracer.FUZZY).slice(0, 14).map(va)});
  }
});

// The Buffs component's S->C 0x1c1 add: the buff row, source, skill and duration fields live in the message.
Interceptor.attach(at('0x141e11720'), {
  onEnter(args) {
    const m = args[1], raw = {};
    for (let off = 0x10; off <= 0x50; off += 2) raw['u16@' + off.toString(16)] = u16(m, off);
    send({ts: stamp(), kind: 'buff-add', self: args[0].toString(), msg: m.toString(), raw});
  }
});

// Object creation, with the type's metadata path when it can be found. Server creates are S->C 0x2b9; client ones are
// the predictor's own objects (which is how a local Kinetic Blast shows up).
function typePath(rec) {
  for (const b of [rec, ptrAt(rec, 0x28)]) {
    if (b === null) continue;
    for (let off = 0; off < 0x100; off += 8) {
      try {
        const p = ptrAt(b, off);
        if (p !== null && p.readUtf16String(9) === 'Metadata/') return p.readUtf16String(120);
      } catch (e) { }
    }
  }
  return '?';
}
Interceptor.attach(at('0x141ebe190'), {
  onEnter(args) { send({ts: stamp(), kind: 'server-create', id: args[2].toInt32() >>> 0, type: typePath(args[1].readPointer())}); }
});
Interceptor.attach(at('0x141ebdf30'), {
  onEnter(args) { send({ts: stamp(), kind: 'client-create', type: typePath(args[1].readPointer())}); }
});
Interceptor.attach(at('0x141e37850'), {
  onEnter() { send({ts: stamp(), kind: 'projectile', bt: Thread.backtrace(this.context, Backtracer.ACCURATE).slice(0, 4).map(va)}); }
});

// Area milestones, so object ids and skill managers can be grouped by the fight that owns them.
for (const [addr, name] of [['0x142186fa0', 'SetLocalPlayerId'], ['0x142156d80', 'EnterArea'],
                            ['0x142183670', 'WorldActivate']])
  Interceptor.attach(at(addr), { onEnter() { send({ts: stamp(), kind: 'area', event: name}); } });

// A first-chance exception is usually the crash that follows; the report includes the last message ids, registers and
// an image-relative backtrace so it can be lined up with the JSONL above.
Process.setExceptionHandler(d => {
  send({ts: stamp(), kind: 'exception', type: d.type, at: va(d.address),
    mem: d.memory ? va(d.memory.address) : null, lastMessages,
    bt: Thread.backtrace(d.context, Backtracer.FUZZY).slice(0, 20).map(va)});
  return false;
});

send({ts: stamp(), kind: 'ready', module: image.name, base: image.base.toString()});
