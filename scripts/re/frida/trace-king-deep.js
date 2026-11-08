// Deep read-only trace of the King's start: every world message that touches the King, the 0x2bb create blobs (the
// Animated/StateMachine/Inventories bytes), skill lookups, action begins, the body controller's clip timeline, and
// memory snapshots of the actor and its animation controller at creation and at the intro.
//   python scripts/re/frida/run.py Game.patched.exe --log logs/frida-king-deep.jsonl scripts/re/frida/trace-king-deep.js
// Then enter the Crux and stand for ~10 s. No packet is sent and no function is called.
const image = Process.enumerateModules().find(m => /^(PathOfExileSteam|Game\.patched)\.exe$/i.test(m.name));
if (!image) throw new Error('client image not found');
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
const i32 = (p, o) => { try { return p.add(o).readS32(); } catch (e) { return null; } };
const f32 = (p, o) => { try { return p.add(o).readFloat(); } catch (e) { return null; } };
const ptrAt = (p, o) => { try { return p.add(o).readPointer(); } catch (e) { return null; } };
const hex = (p, n) => { try { return Array.from(new Uint8Array(p.readByteArray(n)), b => b.toString(16).padStart(2, '0')).join(''); } catch (e) { return null; } };
const hexAt = (p, n) => p === null ? null : hex(p, n);
const stamp = () => Date.now();

let kingId = null, kingActor = null, kingController = null, lastClip = null, lastSample = 0, spawnDumped = false;

// The first UTF-16 "Metadata/" string reachable from a record (the trace scripts' typePath).
function typePath(rec) {
  for (const b of [rec, ptrAt(rec, 0x28)]) {
    if (b === null) continue;
    for (let off = 0; off < 0x100; off += 8) {
      const p = ptrAt(b, off);
      if (p === null) continue;
      try { if (p.readUtf16String(9) === 'Metadata/') return p.readUtf16String(120); } catch (e) { }
    }
  }
  return '?';
}

// Pointer-sized words in a record whose target is a short UTF-16 string.
function stringsIn(rec, from, to) {
  const out = [];
  if (rec === null) return out;
  for (let off = from; off < to; off += 8) {
    const p = ptrAt(rec, off);
    if (p === null) continue;
    try {
      const s = p.readUtf16String(80);
      if (s && /^(stance|Metadata\/|Voodoo|Art\/)/.test(s)) out.push(['0x' + off.toString(16), s]);
    } catch (e) { }
  }
  return out;
}

function controllerState(actor) {
  const c = ptrAt(actor, 0x1a0);
  if (c === null || c.isNull()) return null;
  return { ptr: c.toString(), clip: i32(c, 0x190), elapsed: f32(c, 0x1a4), speed: f32(c, 0x1b0),
    from: f32(c, 0x1b8), to: f32(c, 0x1bc), anim: u32(c, 0x1a8), flags218: u32(actor, 0x218),
    actor21a: u16(actor, 0x21a), actor21c: u16(actor, 0x21c), actor21e: u16(actor, 0x21e),
    actor220: u16(actor, 0x220), actor274: u32(actor, 0x274), actor26c: f32(actor, 0x26c) };
}

function dumpActor(tag) {
  if (kingActor === null) return;
  const ent = ptrAt(kingActor, 8), type = ent ? ptrAt(ent, 0x78) : null;
  send({ts: stamp(), kind: tag, actor: kingActor.toString(), entity: ent ? ent.toString() : null,
    typeData: type ? type.toString() : null, typePath: type ? typePath(type) : null,
    typeStrings: type ? stringsIn(type, 0, 0x300) : [],
    state: controllerState(kingActor),
    actorBytes: hexAt(kingActor, 0x300),
    controllerBytes: kingController ? hexAt(kingController, 0x340) : null});
}

function sample(reason) {
  if (kingActor === null) return;
  const s = controllerState(kingActor);
  if (s === null) return;
  const now = Date.now();
  if (reason !== 'action' && reason !== 'spawn' && s.clip === lastClip && now - lastSample < 250) return;
  lastClip = s.clip;
  lastSample = now;
  send({ts: now, kind: 'clip', reason, ...s});
}

// World messages: 0x1ab actions, 0x2bb create blobs, and the King's animation/state/stats/buff/position messages.
Interceptor.attach(at('0x14218ddf0'), {
  onEnter(args) {
    const m = args[1];
    const op = u16(m, 8);
    if (op === null) return;
    const id = u32(m, 0x10);
    try {
      if (op === 0x2bb) {
        const type = u32(m, 0x20);
        const begin = ptrAt(m, 0x28), end = ptrAt(m, 0x30);
        let len = null;
        if (begin !== null && end !== null && end.compare(begin) >= 0) len = end.sub(begin).toInt32();
        const data = len !== null && len > 0 && len < 0x4000 ? hex(begin, len) : null;
        if (type === 0xc1dcf14b) kingId = id;
        send({ts: stamp(), kind: 'create', id, type: type === null ? null : '0x' + type.toString(16), len, data});
        return;
      }
      if (op === 0x1ab) {
        if (id !== kingId && id !== 0x1000) return;
        send({ts: stamp(), kind: 'action-msg', id, actor: u32(m, 0x10), target: u32(m, 0x20),
          skill: u16(m, 0x34), seq: u16(m, 0x36), flags: u16(m, 0x38), header18: u16(m, 0x18),
          raw: [u16(m, 0x18), u16(m, 0x1a), u16(m, 0x1c), u16(m, 0x1e), u16(m, 0x20), u16(m, 0x22),
            u16(m, 0x24), u16(m, 0x26), u16(m, 0x28), u16(m, 0x2a), u16(m, 0x2c), u16(m, 0x2e),
            u16(m, 0x30), u16(m, 0x32), u16(m, 0x34), u16(m, 0x36), u16(m, 0x38), u16(m, 0x3a),
            u16(m, 0x3c), u16(m, 0x3e), u16(m, 0x40)]});
        return;
      }
      if (id !== kingId) return;
      if (op === 0x1b2) send({ts: stamp(), kind: 'anim-msg', id, kind2: u8(m, 0x20), index: u32(m, 0x24),
        speed: f32(m, 0x28), raw: [u16(m, 0x18), u16(m, 0x1a), u16(m, 0x1c), u16(m, 0x1e), u16(m, 0x20),
          u16(m, 0x22), u16(m, 0x24), u16(m, 0x26), u16(m, 0x28), u16(m, 0x2a)]});
      else if (op === 0x2bc) send({ts: stamp(), kind: 'component', id, raw: [u16(m, 0x18), u16(m, 0x1a),
        u16(m, 0x1c), u16(m, 0x1e), u16(m, 0x20), u16(m, 0x22), u16(m, 0x24), u16(m, 0x26),
        u16(m, 0x28), u16(m, 0x2a), u16(m, 0x2c), u16(m, 0x2e), u16(m, 0x30), u16(m, 0x32),
        u16(m, 0x34), u16(m, 0x36)]});
      else if (op === 0x1b3) send({ts: stamp(), kind: 'stats', id, raw: [u16(m, 0x18), u16(m, 0x1a),
        u16(m, 0x1c), u16(m, 0x1e), u16(m, 0x20), u16(m, 0x22), u16(m, 0x24), u16(m, 0x26)]});
      else if (op === 0x1c1) send({ts: stamp(), kind: 'buff', id, target: u16(m, 0x10),
        raw: [u16(m, 0x10), u16(m, 0x12), u16(m, 0x14), u16(m, 0x16), u16(m, 0x18), u16(m, 0x1a),
          u16(m, 0x1c), u16(m, 0x1e), u16(m, 0x20), u16(m, 0x22), u16(m, 0x24), u16(m, 0x26),
          u16(m, 0x28), u16(m, 0x2a), u16(m, 0x2c), u16(m, 0x2e), u16(m, 0x30), u16(m, 0x32),
          u16(m, 0x34), u16(m, 0x36), u16(m, 0x38), u16(m, 0x3a), u16(m, 0x3c), u16(m, 0x3e)]});
      else if (op === 0x1ac || op === 0x1b1 || op === 0x1b7 || op === 0x1a9 || op === 0x1aa)
        send({ts: stamp(), kind: 'msg', op, id, raw: [u16(m, 0x18), u16(m, 0x1a), u16(m, 0x1c), u16(m, 0x1e),
          u16(m, 0x20), u16(m, 0x22), u16(m, 0x24), u16(m, 0x26), u16(m, 0x28), u16(m, 0x2a)]});
    } catch (e) { send({ts: stamp(), kind: 'msg-error', error: String(e)}); }
  }
});

// Object creation: the type of every server create (the King's attached weapon/effects included).
Interceptor.attach(at('0x141ec4a40'), {
  onEnter(args) { send({ts: stamp(), kind: 'server-create', id: args[2].toInt32() >>> 0, type: typePath(args[1].readPointer())}); }
});

// Actor setup: the King's actor and its type data (MonsterVarieties row / ACT / stance strings) at creation.
Interceptor.attach(at('0x141ea32d0'), {
  onEnter(args) {
    const actor = args[0];
    const ent = ptrAt(actor, 8), type = ent ? ptrAt(ent, 0x78) : null;
    const path = type ? typePath(type) : '?';
    if (!/VoodooKingBoss2/i.test(path)) return;
    kingActor = actor;
    kingController = ptrAt(actor, 0x1a0);
    send({ts: stamp(), kind: 'king-actor', actor: actor.toString(), entity: ent ? ent.toString() : null,
      typeData: type ? type.toString() : null, typePath: path, typeStrings: type ? stringsIn(type, 0, 0x300) : [],
      state: controllerState(actor)});
    if (!spawnDumped) { spawnDumped = true; dumpActor('king-spawn'); }
  }
});

// The Animated component reader: the server's Animated flags and, for bit 3, the initial clip fields.
Interceptor.attach(at('0x141e5afd0'), {
  onEnter(args) {
    const flags = u8(args[1], 0);
    if (flags === null || flags === 0) return;
    const bit3 = (flags & 8) !== 0 ? [u32(args[1], 1), f32(args[1], 5), f32(args[1], 9)] : null;
    send({ts: stamp(), kind: 'animated', flags, bit3});
  }
});

// Skill lookups behind S->C 0x1ab: manager, id and the action pointer the client found (null = no such skill).
Interceptor.attach(at('0x141ef0050'), {
  onEnter(args) { this.mgr = args[0].toString(); this.id = args[2].toUInt32() & 0xffff; this.out = args[1]; },
  onLeave() {
    if (this.id < 0x4000 || this.id > 0x4014) return;
    let action = null;
    try { action = va(this.out.readPointer()); } catch (e) { }
    send({ts: stamp(), kind: 'skill-lookup', mgr: this.mgr, id: this.id, action});
  }
});

// Action begin: the actor, the action type/skill and the result.
Interceptor.attach(at('0x141e937b0'), {
  onEnter(args) { this.actor = args[0].toString(); this.action = args[1].readPointer(); },
  onLeave(ret) {
    const type = u16(this.action, 0x118), skill = u16(this.action, 0x11a);
    const king = kingActor !== null && this.actor === kingActor.toString();
    if (!king && type !== 0x16c7 && !(skill >= 0x4000 && skill <= 0x4014)) return;
    send({ts: stamp(), kind: 'action-begin', actor: this.actor, king, type, skill,
      result: ret.toInt32() & 0xff, state: king ? controllerState(kingActor) : null});
  }
});

// The body controller (actor +0x1a0): the clip the client actually plays.
Interceptor.attach(at('0x141e94900'), {
  onEnter(args) {
    if (kingActor === null) return;
    try { if (!args[0].equals(kingActor)) return; } catch (e) { return; }
    sample('tick');
  }
});

// Any animation played on the King's controller: the descriptor the client passes in.
Interceptor.attach(at('0x141dbdbc0'), {
  onEnter(args) {
    if (kingController === null) return;
    try { if (!args[0].equals(kingController)) return; } catch (e) { return; }
    send({ts: stamp(), kind: 'play-anim', args: [args[2].toUInt32() >>> 0, args[3].toUInt32() >>> 0,
      args[4].toUInt32() >>> 0, args[5].toUInt32() >>> 0, args[6].toUInt32() >>> 0,
      args[7].toUInt32() >>> 0], state: controllerState(kingActor)});
  }
});

// First-chance exceptions, with the last messages for the crash case.
const lastMessages = [];
Interceptor.attach(at('0x1429a7660'), {
  onEnter(args) { this.id = args[1].toUInt32() & 0xffff; },
  onLeave(accepted) {
    if ((accepted.toUInt32() & 0xff) === 0) return;
    lastMessages.push('0x' + this.id.toString(16));
    if (lastMessages.length > 64) lastMessages.shift();
  }
});
Process.setExceptionHandler(d => {
  send({ts: stamp(), kind: 'exception', type: d.type, at: va(d.address),
    mem: d.memory ? va(d.memory.address) : null, lastMessages,
    bt: Thread.backtrace(d.context, Backtracer.FUZZY).slice(0, 20).map(va)});
  return false;
});

send({ts: stamp(), kind: 'ready', module: image.name});
