// Read-only position trace for the King in the Mists. Compare the client's Positioned cell with the Gateway's
// movement/teleport packets:
//   python scripts/re/frida/run.py Game.patched.exe --log logs/frida-king-position.jsonl \
//       scripts/re/frida/trace-king-position.js
// Then reproduce the mismatch. It logs the King's actor cell (+0x294), every 0x1b7/0x1ba/0x1b9 Positioned message,
// every 0x1ab Actor action, and the movement tick's cell/step fields. No packet is sent and no function is called.
const image = Process.enumerateModules().find(m => /^(PathOfExileSteam|Game\.patched)\.exe$/i.test(m.name));
if (!image) throw new Error('client image not found');
const base = ptr('0x140000000');
const at = va => image.base.add(ptr(va).sub(base));
const u8 = (p, o) => { try { return p.add(o).readU8(); } catch (e) { return null; } };
const u16 = (p, o) => { try { return p.add(o).readU16(); } catch (e) { return null; } };
const u32 = (p, o) => { try { return p.add(o).readU32(); } catch (e) { return null; } };
const i32 = (p, o) => { try { return p.add(o).readS32(); } catch (e) { return null; } };
const f32 = (p, o) => { try { return p.add(o).readFloat(); } catch (e) { return null; } };
const ptrAt = (p, o) => { try { return p.add(o).readPointer(); } catch (e) { return null; } };
const xy = p => [i32(p, 0x294), i32(p, 0x298)];
const hex = (p, n) => { try { return Array.from(new Uint8Array(p.readByteArray(n)), b => b.toString(16).padStart(2, '0')).join(''); } catch (e) { return null; } };
const stamp = () => Date.now();

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

let kingPos = null, kingActor = null, lastCell = null;

function noteActor(actor, id) {
  const ent = ptrAt(actor, 8), pos = ent ? ptrAt(ent, 0x98) : null;
  if (pos !== null && (id === 0x3000 || (kingActor !== null && actor.equals(kingActor)))) {
    kingPos = pos; kingActor = actor;
  }
}

// Actor messages: the 0x1ab action (skill/seq/from/to) and any other message for the tracked actor id.
Interceptor.attach(at('0x1421940a0'), {
  onEnter(args) {
    const actor = args[0], m = args[1];
    const op = u16(m, 8), id = u32(m, 0x10);
    noteActor(actor, id);
    if (id !== 0x3000 && !(kingActor !== null && actor.equals(kingActor))) return;
    const fields = {};
    for (let off = 0x18; off <= 0x40; off += 2) fields['u16@' + off.toString(16)] = u16(m, off);
    send({ts: stamp(), kind: 'actor-msg', op, id, actor: actor.toString(),
      skill: op === 0x1ab ? u16(m, 0x34) : null, seq: op === 0x1ab ? u16(m, 0x36) : null,
      target: op === 0x1ab ? u32(m, 0x20) : null, fields});
  }
});

// Positioned messages: 0x1b7 teleport, 0x1ba move-to and 0x1b9 orientation. Read the Positioned object's cell.
Interceptor.attach(at('0x141e30c90'), {
  onEnter(args) {
    const pos = args[0], m = args[1];
    const op = u16(m, 8), id = u32(m, 0x10);
    if (id !== 0x3000 && !(kingPos !== null && pos.equals(kingPos))) return;
    kingPos = pos;
    send({ts: stamp(), kind: 'positioned-msg', op, id, cell: xy(pos), hex: hex(m, 0x48), raw: {
      x: u32(m, 0x18), y: u32(m, 0x1c), u28: u32(m, 0x28), u2c: u32(m, 0x2c), u30: u8(m, 0x30)
    }});
  }
});

// The position tick: report a cell change (and the step timer) as the client actually applies it.
Interceptor.attach(at('0x141e32710'), {
  onEnter(args) {
    if (kingPos === null || !args[0].equals(kingPos)) return;
    const cell = xy(args[0]);
    if (lastCell !== null && cell[0] === lastCell[0] && cell[1] === lastCell[1]) return;
    lastCell = cell;
    send({ts: stamp(), kind: 'cell', cell, step: f32(args[0], 0x240), elapsed: f32(args[0], 0x238),
      total: f32(args[0], 0x23c), speed: f32(args[0], 0x23c)});
  }
});

// Actor setup: identify VoodooKingBoss2 before its first action, so the trace starts at spawn.
Interceptor.attach(at('0x141e9ca20'), {
  onEnter(args) {
    const actor = args[0];
    const ent = ptrAt(actor, 8), type = ent ? ptrAt(ent, 0x78) : null;
    const path = type ? typePath(type) : '?';
    if (!/VoodooKingBoss2/i.test(path)) return;
    kingActor = actor; kingPos = ent ? ptrAt(ent, 0x98) : null;
    send({ts: stamp(), kind: 'king-actor', actor: actor.toString(), position: kingPos ? kingPos.toString() : null,
      typePath: path, cell: kingPos ? xy(kingPos) : null});
  }
});

send({ts: stamp(), kind: 'ready', module: image.name, base: image.base.toString()});
