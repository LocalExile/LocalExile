// Dump the game-state object the overlay would read, so two dumps around a kill can be diffed for a counter.
// Instance = [app + 0x348] (documented); the current WorldAreas row follows [instance + 0xc278] -> [+0xb0].
'use strict';
const m = Process.getModuleByName('Game.patched.exe');
const va = v => m.base.add(v - 0x140000000);

function q(p) { try { return p.readPointer(); } catch (e) { return null; } }
function wstr(p) { try { return p.readUtf16String(48); } catch (e) { return null; } }
function ints(p, count) {
  try {
    const buf = p.readByteArray(count * 4);
    const dv = new DataView(buf);
    const a = [];
    for (let i = 0; i < count; i++) a.push(dv.getInt32(i * 4, true));
    return a;
  } catch (e) { return null; }
}

const app = q(va(0x1445d6fd8));
const out = { app: app ? app.toString() : null };
const inst = app ? q(app.add(0x348)) : null;
out.instance = inst ? inst.toString() : null;
if (inst) {
  const holder = q(inst.add(0xc278));
  out.holder = holder ? holder.toString() : null;
  if (holder) {
    const row = q(holder.add(0xb0));
    out.row = row ? row.toString() : null;
    if (row) {
      const idp = q(row);
      out.rowId = idp ? wstr(idp) : null;
    }
  }
  out.inst_0 = ints(inst, 4096);          // 16 KB
  out.inst_c000 = ints(inst.add(0xc000), 4096);
}
send(out);
