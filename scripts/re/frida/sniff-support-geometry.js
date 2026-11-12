// Support geometry sniffer (read-only): Spell Cascade's repeated areas and Arrow Nova's payload/volley.
//
// Load it with scripts/re/frida/run.py against the running client:
//
//   python scripts/re/frida/run.py <pid> --seconds 0 --log logs/support-geometry.jsonl `
//       scripts/re/frida/sniff-support-geometry.js
//
// Cast the supported spell (Spell Cascade) a few times aiming at a fixed spot, then the Arrow Nova bow attack,
// 3 s apart. Ctrl-C detaches without killing the client.
//
// The client predicts its own casts, so the server 0x1ab stream does not carry the repeats for the caster. This hooks
// the client-side path too:
//
//   srv     S->C 0x1ab Actor action (0x14219aa10): the cast echo. Target object +0x20, target cell +0x24/+0x28,
//           server cell +0x2c/+0x30, skill +0x34, sequence +0x36. The local player is the actor whose sequence
//           matches a local use.
//   use     C->S use (0x14216ccb0, a[2] = action): sequence +0x11a and aimed cell +0x130/+0x134. A cast marker.
//   action  0x141ef0050 (manager, &action, skill id) creates an action; *&action is the action after the call.
//           Logs the skill id, action type +0x118 (ActionTypes HASH16), sequence +0x11a and aimed cell.
//   obj     client-side object creation (0x141ec47e0): the metadata path. The object is arg 0; its Positioned
//           component is [[obj+8]+0x98].
//   place   0x141e351d0 (Positioned, int cell[2], ...): the cell a recently created effect was placed at. These are
//           Spell Cascade's extra areas.
//   create  S->C 0x2bb object create (0x141ec4a40): object id, metadata path and cell.
//   proj    Projectile component constructor (0x141e3ab90): the projectile entity.
//   path    Positioned movement update (0x141e35a50) of a projectile created in this window: cell, at most 24 per
//           entity.
//
// Reading the log:
//   Spell Cascade: one cast gives several place cells in a line; subtract them from the cast's aim cell. The middle
//   area is the aimed cell and the outer ones are offset along the caster -> aim direction.
//   Arrow Nova: the payload arrow's path ends at the aimed cell; the child arrows are the projectile entities created
//   just after it disappears. Their first cells give the ring radius, count and angles.

const image = Process.enumerateModules().find(m => /^(PathOfExileSteam|Game\.patched)\.exe$/i.test(m.name));
if (!image) throw new Error('client image not found');
const base = ptr('0x140000000');
// Data addresses here are the 3.29.3c release's (build 327984). Its 3.29.3.5 hotfix and 3.29.3d have the same code
// and move data (Agents/docs/re/tooling.md section 11): MOVED lists where each data address this script uses sits in
// them, so that the script and its output stay in release addresses on every build.
const MOVED = {
  '3.29.3.5': {
    '0x1435ab608':'0x1435ab5d8'},
  '3.29.3d': {
    '0x1435ab608':'0x1435ab568'}};
// The build by its link time (the PE header's TimeDateStamp): the release and its hotfix both call themselves 3.29.3c.
const BUILD = ({0x6af1219e: '3.29.3.5', 0x6af26d5e: '3.29.3d'})[image.base.add(image.base.add(0x3c).readU32() + 8).readU32()] || '';
const moved = MOVED[BUILD] || {};
const movedBack = Object.fromEntries(Object.entries(moved).map(([from, to]) => [to, from]));
const at = va => image.base.add(ptr(typeof va === 'string' && moved[va.toLowerCase()] || va).sub(base));

const EXPECTED = {
  // 0x14219aa10  Actor 0x1ab handler (trace-actions.js).
  '0x14219aa10': '48895c24085556574154415541564157',
  // 0x141ec4a40  0x2bb create handler (trace-projectile-create.js).
  '0x141ec4a40': '48895c241048894c2408565741564883',
  // 0x141ec47e0  client-side object create (sniff-bloodsoaked-vanguard.js).
  '0x141ec47e0': '48895c241848894c2408555657415641',
  // 0x141ef0050  action created by the Actor skill manager (trace-actions.js).
  '0x141ef0050': '48895c240848896c2418488954241056',
};

const u16 = (p, o) => { try { return p.add(o).readU16(); } catch (e) { return null; } };
const i32 = (p, o) => { try { return p.add(o).readS32(); } catch (e) { return null; } };
const ptrAt = (p, o) => { try { return p.add(o).readPointer(); } catch (e) { return null; } };
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

// The type record scan from trace-projectile-create.js: find the UTF-16 "Metadata/" path. Return the most specific
// (longest) of the paths the record and its child point at.
function typePath(rec) {
  const found = [];
  for (const base of [rec, (() => { try { return rec.add(0x28).readPointer(); } catch (e) { return null; } })()]) {
    if (base === null) continue;
    for (let off = 0; off < 0x100; off += 8) {
      try {
        const p = base.add(off).readPointer();
        if (p.readUtf16String(9) !== 'Metadata/') continue;
        const s = p.readUtf16String(160);
        if (s !== null && found.indexOf(s) < 0) found.push(s);
      } catch (e) { }
    }
  }
  found.sort((a, b) => b.length - a.length);
  return found.length ? found[0] : '?';
}

// A Positioned component is [[entity+8]+0x98]; +0x294/+0x298 are the integer cell.
function positionedOf(entity) {
  try { return entity.add(8).readPointer().add(0x98).readPointer(); } catch (e) { return null; }
}
function cellOfPositioned(p) {
  try { return [p.add(0x294).readS32(), p.add(0x298).readS32()]; } catch (e) { return null; }
}

const created = new Map();      // entity pointer -> { t, type } for objects created by 0x141ec47e0
const projectiles = new Map(); // projectile entity -> { t, type, cells }
let lastReap = 0;
let lastReapTarget = null;
const projectileTypes = new Map();
let windowUntil = 0;
const openWindow = () => { windowUntil = stamp() + 6000; };

// --- 0x1ab: S->C Actor actions (the cast echo) ------------------------------
if (check('0x14219aa10')) {
  try {
    Interceptor.attach(at('0x14219aa10'), {
      onEnter(args) {
        try {
          if (args[1].add(8).readU16() !== 0x1ab) return;
          send({ t: stamp(), kind: 'srv', actor: hex8(args[1].add(0x10).readU32()),
            skill: hex8(args[1].add(0x34).readU16()), seq: args[1].add(0x36).readU16(),
            targetId: hex8(args[1].add(0x20).readU32()),
            target: [i32(args[1], 0x24), i32(args[1], 0x28)], cell: [i32(args[1], 0x2c), i32(args[1], 0x30)] });
        } catch (e) { send({ t: stamp(), hookError: 'action', message: String(e) }); }
      }
    });
    send({ t: stamp(), attached: '0x14219aa10' });
  } catch (e) { send({ t: stamp(), hookError: 'actionAttach', message: String(e) }); }
}

// --- 0x14216ccb0: the client sends a use (C->S 0x24/0x25), action in a[2] ---
try {
  Interceptor.attach(at('0x14216ccb0'), {
    onEnter(args) {
      try {
        const a = args[2];
        send({ t: stamp(), kind: 'use', action: a.toString(), seq: u16(a, 0x11a),
          type: u16(a, 0x118), target: [i32(a, 0x130), i32(a, 0x134)] });
        if (u16(a, 0x118) === 18895) { lastReap = stamp(); lastReapTarget = [i32(a, 0x130), i32(a, 0x134)]; }
        openWindow();
      } catch (e) { send({ t: stamp(), hookError: 'use', message: String(e) }); }
    }
  });
  send({ t: stamp(), attached: '0x14216ccb0' });
} catch (e) { send({ t: stamp(), hookError: 'useAttach', message: String(e) }); }

// --- 0x141ef0050: create an action (manager, &action, skill id) -------------
if (check('0x141ef0050')) {
  try {
    Interceptor.attach(at('0x141ef0050'), {
      onEnter(args) { this.slot = args[1]; this.skill = args[2].toInt32() & 0xffff; },
      onLeave() {
        try {
          let a = null;
          try { a = this.slot.readPointer(); } catch (e) { }
          send({ t: stamp(), kind: 'action', skill: hex8(this.skill), action: a === null ? null : a.toString(),
            type: a === null ? null : u16(a, 0x118), seq: a === null ? null : u16(a, 0x11a),
            target: a === null ? null : [i32(a, 0x130), i32(a, 0x134)] });
          openWindow();
        } catch (e) { send({ t: stamp(), hookError: 'actionCreate', message: String(e) }); }
      }
    });
    send({ t: stamp(), attached: '0x141ef0050' });
  } catch (e) { send({ t: stamp(), hookError: 'actionCreateAttach', message: String(e) }); }
}

// --- 0x141ec47e0: client-side object creation -------------------------------
if (check('0x141ec47e0')) {
  try {
    Interceptor.attach(at('0x141ec47e0'), {
      onEnter(args) {
        try {
          this.type = typePath(args[1].readPointer());
          this.p3 = args[2].toString();
          this.p4 = args[3].toInt32();
          this.p5 = args[4].toInt32();
        } catch (e) { this.type = '?'; }
      },
      onLeave(retval) {
        try {
          const entity = retval;
          const type = this.type;
          if (!/Doodad|Terrain|MiscellaneousObjects|Characters|NPC|Hideout|Stash/i.test(type)) {
            created.set(entity.toString(), { t: stamp(), type });
            send({ t: stamp(), kind: 'obj', type, entity: entity.toString() });
            // Spell Cascade's areas are generic Effect objects with no Positioned component; sample the object's own
            // fields right after a Reap use and again 150 ms later, when its transform/child is built.
            if (type === 'Metadata/Effects/Effect' && stamp() - lastReap < 1500) {
              const meta = { p3: this.p3, p4: this.p4, p5: this.p5 };
              const sample = (tag, entity) => {
                const info = { t: stamp(), kind: 'fx', tag, entity: entity.toString(), p3: meta.p3, p4: meta.p4, p5: meta.p5 };
                const positionedVt = at('0x1435ab608');
                try {
                  const begin = entity.add(0x10).readPointer(), end = entity.add(0x18).readPointer();
                  const count = Math.min(24, end.sub(begin).toInt32() / 8);
                  info.comps = [];
                  for (let i = 0; i < count; i++) {
                    const comp = begin.add(i * 8).readPointer();
                    const vt = comp.readPointer();
                    const cell = vt.equals(positionedVt) ? [comp.add(0x294).readS32(), comp.add(0x298).readS32()] : null;
                    info.comps.push([vt.toString(), cell]);
                  }
                } catch (e) { info.compsError = String(e); }
                if (lastReapTarget !== null) {
                  info.hits = [];
                  try {
                    for (let off = 0; off < 0x300; off += 4) {
                      const x = entity.add(off).readS32(), y = entity.add(off + 4).readS32();
                      if (Math.abs(x - lastReapTarget[0]) < 80 && Math.abs(y - lastReapTarget[1]) < 80)
                        info.hits.push([off, [x, y]]);
                    }
                  } catch (e) { }
                }
                send(info);
              };
              sample('now', entity);
              const keep = ptr(entity.toString());
              setTimeout(() => { try { sample('later', keep); } catch (e) { } }, 150);
            }
          }
        } catch (e) { send({ t: stamp(), hookError: 'objectCreate', message: String(e) }); }
      }
    });
    send({ t: stamp(), attached: '0x141ec47e0' });
  } catch (e) { send({ t: stamp(), hookError: 'objectCreateAttach', message: String(e) }); }
}

// --- 0x141e351d0: place a Positioned at a cell ------------------------------
try {
  Interceptor.attach(at('0x141e351d0'), {
    onEnter(args) {
      try {
        // The Positioned's +8 is the entity (trace-projectile-path.js).
        const entity = args[0].add(8).readPointer();
        const key = entity.toString();
        const info = created.get(key);
        if (info === undefined) return;
        if (stamp() - info.t > 4000) { created.delete(key); return; }
        send({ t: stamp(), kind: 'place', type: info.type, cell: [args[1].readS32(), args[1].add(4).readS32()] });
      } catch (e) { send({ t: stamp(), hookError: 'place', message: String(e) }); }
    }
  });
  send({ t: stamp(), attached: '0x141e351d0' });
} catch (e) { send({ t: stamp(), hookError: 'placeAttach', message: String(e) }); }

// --- 0x2bb: S->C object creation -------------------------------------------
if (check('0x141ec4a40')) {
  try {
    Interceptor.attach(at('0x141ec4a40'), {
      onEnter(args) {
        try {
          if (stamp() > windowUntil) return;
          let type = '?';
          try { type = typePath(args[1].readPointer()); } catch (e) { }
          const id = hex8(args[2].toInt32() >>> 0);
          // The 0x2bb blob carries the object's own fields; an effect's AO path is in it as UTF-16. Dump it so the
          // Arrow Nova payload's effect (not just its generic ServerEffect type) can be named.
          let blob = null, paths = [];
          try {
            const reader = args[3];
            const data = reader.readPointer(), size = reader.add(8).readU64().toNumber();
            blob = hex(data, Math.min(size, 200));
            for (let off = 0; off + 18 <= size; off += 2) {
              try {
                const s = data.add(off).readUtf16String(Math.min(160, (size - off) / 2));
                if (s !== null && s.startsWith('Metadata/') && paths.indexOf(s) < 0) paths.push(s);
              } catch (e) { }
            }
          } catch (e) { }
          send({ t: stamp(), kind: 'create', id, type, blob, paths });
        } catch (e) { send({ t: stamp(), hookError: 'create', message: String(e) }); }
      }
    });
    send({ t: stamp(), attached: '0x141ec4a40' });
  } catch (e) { send({ t: stamp(), hookError: 'createAttach', message: String(e) }); }
}

// --- 0x141e3ab90: a Projectile component is built; a[1] is its entity ------
try {
  Interceptor.attach(at('0x141e3ab90'), {
    onEnter(args) {
      try {
        if (stamp() > windowUntil) return;
        const entity = args[1];
        projectiles.set(entity.toString(), { t: stamp(), cells: 0 });
        const pos = positionedOf(entity);
        send({ t: stamp(), kind: 'proj', entity: entity.toString(), cell: pos === null ? null : cellOfPositioned(pos) });
      } catch (e) { send({ t: stamp(), hookError: 'projectile', message: String(e) }); }
    }
  });
  send({ t: stamp(), attached: '0x141e3ab90' });
} catch (e) { send({ t: stamp(), hookError: 'projectileAttach', message: String(e) }); }

// --- 0x141e35a50: a tracked projectile's Positioned movement update --------
try {
  Interceptor.attach(at('0x141e35a50'), {
    onEnter(args) {
      try {
        const p = args[0];
        const entity = p.add(8).readPointer();
        const info = projectiles.get(entity.toString());
        if (info === undefined) return;
        if (info.cells >= 24) { this.p = null; return; }
        this.p = p; this.info = info; this.entity = entity.toString();
      } catch (e) { this.p = null; }
    },
    onLeave() {
      if (!this.p) return;
      try {
        const cell = cellOfPositioned(this.p);
        if (cell === null) return;
        this.info.cells++;
        send({ t: stamp(), kind: 'path', entity: this.entity, cell });
      } catch (e) { }
    }
  });
  send({ t: stamp(), attached: '0x141e35a50' });
} catch (e) { send({ t: stamp(), hookError: 'pathAttach', message: String(e) }); }

// --- 0x1b2: animated-object clip update (0x141e5d440) ----------------------
// The official server sends the rain rig its animation clip/index; without it the client plays the art's default
// (the landing pose or a T-pose).
try {
  Interceptor.attach(at('0x141e5d440'), {
    onEnter(args) {
      try {
        const m = args[1];
        if (m.add(8).readU16() !== 0x1b2) return;
        send({ t: stamp(), kind: 'anim', id: hex8(m.add(0x10).readU32()), kindByte: m.add(0x20).readU8(),
          index: m.add(0x24).readU32(), speed: m.add(0x28).readFloat() });
      } catch (e) { }
    }
  });
  send({ t: stamp(), attached: '0x141e5d440' });
} catch (e) { send({ t: stamp(), hookError: 'animAttach', message: String(e) }); }

send('sniff-support-geometry ready');
