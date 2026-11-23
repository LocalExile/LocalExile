// Dump the client structures the map overlay reads, so the "monsters remain" counter can be located by diffing two
// dumps around a kill. Loaded by scripts/re/frida/run.py; everything is read-only.
'use strict';
const m = Process.getModuleByName('Game.patched.exe');
const va = v => m.base.add(v - 0x140000000);
const out = { module: m.base.toString(), base: '0x' + m.base.toString(16) };

function q(p) {
  try { return p.readPointer(); } catch (e) { return null; }
}
function u32(p) {
  try { return p.readU32(); } catch (e) { return null; }
}
function wstr(p, max) {
  try { return p.readUtf16String(Math.min(max || 48, 120)); } catch (e) { return null; }
}
function dumpInts(p, count) {
  const a = [];
  try {
    const buf = p.readByteArray(count * 4);
    const dv = new DataView(buf);
    for (let i = 0; i < count; i++) a.push(dv.getInt32(i * 4, true));
  } catch (e) { return null; }
  return a;
}

// Documented: 0x1445d6fd8 / 0x1445d6fb0 are the app and state-stack singletons; app +0x348 is the instance.
const app = q(va(0x1445d6fd8));
out.app = app ? app.toString() : null;
if (app) {
  out.app_qwords = {};
  for (const off of [0x300, 0x308, 0x310, 0x318, 0x320, 0x328, 0x330, 0x338, 0x340, 0x348, 0x350, 0x358, 0x360, 0x368]) {
    const p = q(app.add(off));
    out.app_qwords['0x' + off.toString(16)] = p ? p.toString() : null;
  }
  const instA = q(app.add(0x348));
  out.instanceFrom348 = instA ? instA.toString() : null;
  const via328 = q(app.add(0x328));
  out.via328 = via328 ? via328.toString() : null;
  if (via328) {
    const instB = q(via328.add(0xc278));
    out.instanceFrom328c278 = instB ? instB.toString() : null;
    if (instB) {
      const row = q(instB.add(0xb0));
      out.row = row ? row.toString() : null;
      if (row) {
        const idp = q(row);
        out.rowId = idp ? wstr(idp, 64) : null;
        out.rowHash16 = (() => { try { return row.add(0x2a).readU16(); } catch (e) { return null; } })();
        out.row_ints = dumpInts(row, 64);
      }
      out.instance_ints_0 = dumpInts(instB, 64);
      // The overlay's own window: what sits around +0xc278.
      out.instance_ints_c200 = dumpInts(instB.add(0xc200), 128);
      out.instance_ints_c400 = dumpInts(instB.add(0xc400), 128);
    }
  }
  if (instA) {
    out.instA_ints_0 = dumpInts(instA, 64);
  }
}
send(out);
