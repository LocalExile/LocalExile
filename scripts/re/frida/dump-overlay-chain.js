// Hook the map overlay function (0x1406b25f0) and dump what it reads: the chain [rcx+0x328] -> [+0xc278] -> [+0xb0]
// is the current WorldAreas row (documented in Agents/docs/dev/client-control.md). Dumping the same windows twice, once
// before and once after monsters die, shows which integer the overlay counts with.
'use strict';
const m = Process.getModuleByName('Game.patched.exe');
const va = v => m.base.add(v - 0x140000000);
const OVERLAY = va(0x1406b25f0);

function q(p) { try { return p.readPointer(); } catch (e) { return null; } }
function wstr(p) { try { return p.readUtf16String(64); } catch (e) { return null; } }
function ints(p, count) {
  try {
    const buf = p.readByteArray(count * 4);
    const dv = new DataView(buf);
    const a = [];
    for (let i = 0; i < count; i++) a.push(dv.getInt32(i * 4, true));
    return a;
  } catch (e) { return null; }
}

let seq = 0, lastMs = 0;
Interceptor.attach(OVERLAY, {
  onEnter(args) {
    const now = Date.now(); if (now - lastMs < 300) return; lastMs = now;
    const self = this.context.rcx;
    const out = { self: self.toString(), chain: {} };
    const x = q(self.add(0x328));
    out.chain.x_328 = x ? x.toString() : null;
    if (x) {
      const y = q(x.add(0xc278));
      out.chain.y_c278 = y ? y.toString() : null;
      if (y) {
        const row = q(y.add(0xb0));
        out.chain.row = row ? row.toString() : null;
        if (row) {
          const idp = q(row);
          out.rowId = idp ? wstr(idp) : null;
          out.rowHash16 = (() => { try { return row.add(0x2a).readU16(); } catch (e) { return null; } })();
          out.rowInts = ints(row, 96);
        }
        // Everything the overlay's instance object holds nearby.
        out.yInts_0 = ints(y, 128);
        out.yInts_c000 = ints(y.add(0xc000), 256);
        out.yInts_c400 = ints(y.add(0xc400), 256);
      }
      out.xInts_0 = ints(x, 128);
    }
    out.seq = ++seq; send(out);
  }
});
send({ hooked: OVERLAY.toString() });

