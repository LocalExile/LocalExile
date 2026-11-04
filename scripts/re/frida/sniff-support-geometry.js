// Support geometry sniffer (read-only): Spell Cascade's repeated areas and Arrow Nova's payload/volley.
//
// Load it with scripts/re/frida/run.py against the running client:
//
//   python scripts/re/frida/run.py Game.patched.exe --seconds 0 --log Tools/re/out/support-geometry.jsonl `
//       scripts/re/frida/sniff-support-geometry.js
//
// Then stand in a pack and cast the supported spell (Spell Cascade) or the bow attack (Arrow Nova) a few times,
// Ctrl-C detaches without killing the client. Every hook checks the 3.29.3b prologue first and logs {skip:...} instead
// of attaching at a wrong address.
//
// What it logs (JSON lines):
//   action   0x1ab Actor action: actor, skill id, sequence, target id and the raw u16 fields. A cast marker.
//   create   0x2b9 object create within 2 s of a cast: object id and metadata path (projectiles).
//   position 0x1b7/0x1ba/0x1b9 Positioned messages for those projectile ids: op and cell.
//
// Reading the log:
//   Spell Cascade: compare the cast actions' target cells and the projectile spawn cells; the repeat offsets are the
//   per-repeat distance along the aim direction (world cells, 10 per metre).
//   Arrow Nova: the payload arrow lands at the targeted cell; the child arrows are the projectiles created after it,
//   and their initial cells/orientations give the ring's spacing and count.
//   The client timestamps are Date.now() ms; group lines by the action's seq.

const image = Process.enumerateModules().find(m => /^(PathOfExileSteam|Game\.patched)\.exe$/i.test(m.name));
if (!image) throw new Error('client image not found');
const base = ptr('0x140000000');
const at = va => image.base.add(ptr(va).sub(base));

const EXPECTED = {
  // 0x1421940a0  Actor 0x1ab handler (trace-actions.js).
  '0x1421940a0': '48895c24085556574154415541564157',
  // 0x141ebe190  0x2b9 create handler (trace-projectile-create.js).
  '0x141ebe190': '48895c241048894c2408565741564883',
};

const u16 = (p, o) => { try { return p.add(o).readU16(); } catch (e) { return null; } };
const u8 = (p, o) => { try { return p.add(o).readU8(); } catch (e) { return null; } };
const u32 = (p, o) => { try { return p.add(o).readU32(); } catch (e) { return null; } };
const hex = (p, n) => {
  try { return Array.from(new Uint8Array(p.readByteArray(n))).map(b => b.toString(16).padStart(2, '0')).join(''); }
  catch (e) { return null; }
};
const hex8 = v => '0x' + (v >>> 0).toString(16);
const stamp = () => Date.now();

function check(addr) {
  const want = EXPECTED[addr];
  if (!want) return true;
  const got = hex(at(addr), want.length / 2);
  if (got !== want) {
    send({ t: stamp(), skip: addr, reason: 'prologue', got, want, module: image.name, base: image.base.toString() });
    return false;
  }
  return true;
}

// The type record scan from trace-projectile-create.js: find the UTF-16 "Metadata/" path.
function typePath(rec) {
  for (const base of [rec, (() => { try { return rec.add(0x28).readPointer(); } catch (e) { return null; } })()]) {
    if (base === null) continue;
    for (let off = 0; off < 0x100; off += 8) {
      try {
        const p = base.add(off).readPointer();
        if (p.readUtf16String(9) === 'Metadata/') return p.readUtf16String(120);
      } catch (e) { }
    }
  }
  return '?';
}

const tracked = new Set();   // projectile object ids created near a cast
const types = new Map();     // object id -> metadata path
let lastCast = 0;
let lastSeq = null;
let lastSkill = null;

// --- 0x1ab: Actor actions (the cast marker) ---------------------------------
if (check('0x1421940a0')) {
  try {
    Interceptor.attach(at('0x1421940a0'), {
      onEnter(args) {
        try {
          if (args[1].add(8).readU16() !== 0x1ab) return;
          const actor = args[1].add(0x10).readU32();
          const skill = args[1].add(0x34).readU16();
          const seq = args[1].add(0x36).readU16();
          const target = args[1].add(0x20).readU32();
          lastCast = stamp();
          lastSeq = seq;
          lastSkill = skill;
          const fields = {};
          for (let off = 0x18; off <= 0x40; off += 2) fields['u16@' + off.toString(16)] = u16(args[1], off);
          send({ t: lastCast, kind: 'action', actor: hex8(actor), skill: hex8(skill), seq, target: hex8(target), fields });
        } catch (e) { send({ t: stamp(), hookError: 'action', message: String(e) }); }
      }
    });
  } catch (e) { send({ t: stamp(), hookError: 'actionAttach', message: String(e) }); }
}

// --- 0x2b9: object creation (the projectiles) -------------------------------
if (check('0x141ebe190')) {
  try {
    Interceptor.attach(at('0x141ebe190'), {
      onEnter(args) {
        try {
          let type = '?';
          try { type = typePath(args[1].readPointer()); } catch (e) { }
          if (!type.startsWith('Metadata/Projectiles/')) return;
          const id = args[2].toInt32() >>> 0;
          const nearCast = stamp() - lastCast < 2000;
          if (nearCast) { tracked.add(id); types.set(id, type); }
          if (!nearCast) return;
          send({ t: stamp(), kind: 'create', id: hex8(id), type, skill: lastSkill === null ? null : hex8(lastSkill),
            seq: lastSeq, nearCast: true });
        } catch (e) { send({ t: stamp(), hookError: 'create', message: String(e) }); }
      }
    });
  } catch (e) { send({ t: stamp(), hookError: 'createAttach', message: String(e) }); }
}

// --- Positioned messages: 0x1b7 teleport, 0x1ba move-to, 0x1b9 orientation --
try {
  Interceptor.attach(at('0x141e30c90'), {
    onEnter(args) {
      try {
        const pos = args[0], msg = args[1];
        const op = u16(msg, 8), id = u32(msg, 0x10);
        if (!tracked.has(id)) return;
        send({ t: stamp(), kind: 'position', op, id: hex8(id), type: types.get(id) ?? '?',
          cell: [u32(msg, 0x18), u32(msg, 0x1c)], u28: u32(msg, 0x28), u2c: u32(msg, 0x2c), u30: u8(msg, 0x30) });
      } catch (e) { send({ t: stamp(), hookError: 'position', message: String(e) }); }
    }
  });
  send({ t: stamp(), positionHook: '0x141e30c90' });
} catch (e) { send({ t: stamp(), hookError: 'positionAttach', message: String(e) }); }

send('sniff-support-geometry ready');
