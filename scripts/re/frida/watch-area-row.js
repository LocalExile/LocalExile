// Watch who reads the current WorldAreas row: the map overlay must read it to draw the area's name and level, so the
// instruction pointer reported for a page access, taken while the overlay is redrawn, is the overlay's code.
// Arm, press Tab in the game, read the reported addresses, then disassemble them.
'use strict';
const m = Process.getModuleByName('Game.patched.exe');
const va = v => m.base.add(v - 0x140000000);
const toVa = p => '0x' + p.sub(m.base).add(0x140000000).toString(16);

function q(p) { try { return p.readPointer(); } catch (e) { return null; } }

const app = q(va(0x1445d6fd8));
const inst = app ? q(app.add(0x348)) : null;
const holder = inst ? q(inst.add(0xc278)) : null;
const row = holder ? q(holder.add(0xb0)) : null;
send({ app: app ? app.toString() : null, row: row ? row.toString() : null });

if (!row) { send({ error: 'no row: the overlay page is unknown' }); }
else {
  const page = row.and(ptr('0xfffffffffffff000'));
  const pageEnd = page.add(0x1000);
  send({ page: page.toString(), rowPageOffset: row.sub(page).toString() });

  const seen = {};
  const limit = 40;
  let n = 0;
  MemoryAccessMonitor.enable([{ base: page, size: 0x1000 }], {
    onAccess(details) {
      const from = details && details.from ? details.from : null;
      const key = from ? from.toString() : 'null';
      if (!from || seen[key]) return;
      seen[key] = true;
      n++;
      send({ access: details.operation, from: key, fromVa: from ? toVa(from) : null,
             address: details.address ? details.address.toString() : null });
      if (n >= limit) { try { MemoryAccessMonitor.disable(); } catch (e) {} }
    }
  });
  send({ armed: true });
  setTimeout(() => { try { MemoryAccessMonitor.disable(); } catch (e) {} send({ finished: n }); }, 60000);
}
