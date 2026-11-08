// Trade/sell window clicks: the grid click handler (0x1402de150) with the mode byte that picks its branch
// (FUN_1402dfbc0(grid) + 0x588: 0 or 8..11 → the Ctrl/Shift/Alt branch; else the trade branch, where only Shift and
// Ctrl+Shift send 0xb3 / 0xb5), the Ctrl-click handler (0x14030e970) and the senders of 0xb2 / 0xb3 / 0xb5.
// Load after common.js.
const modeOf = new NativeFunction(at('0x1402dfbc0'), 'pointer', ['pointer']);
Interceptor.attach(at('0x1402de150'), {
  onEnter(a) {
    let mode = '?';
    try { mode = modeOf(a[0]).add(0x588).readU8(); } catch (e) {}
    let kind = '?';
    try { kind = a[2].add(0x14).readU32() + '/' + a[2].readU8(); } catch (e) {}
    send({grid: va(a[0]), mode, flags523: a[0].add(0x523).readU8(), event: kind, global: at('0x1445998ce').readU8()});
  },
});
Interceptor.attach(at('0x14030e970'), { onEnter(a) { send({ctrlClick: va(a[0]), item: a[1].toInt32()}); } });
for (const [addr, name] of [['0x14216d010', '0xb2'], ['0x14216d100', '0xb3'], ['0x14216d1e0', '0xb5']])
  Interceptor.attach(at(addr), { onEnter(a) { send({send: name, a1: a[1].toInt32(), a2: a[2].toInt32()}); } });
// After a trade closes (0x140dcac00) the UI recomputes whether the world takes clicks (0x140dd3760 → 0x140232fa0 with
// a bool): only with no left panel in the panel manager (UI +0x608) and the trade windows hidden.
const slot = (ui, off) => { const p = ui.add(off).readPointer(); return p.isNull() ? 'none' : va(p) + ' vt ' + va(p.readPointer()); };
const shown = p => (p.add(0x1e8).readU32() >> 11) & 1;
Interceptor.attach(at('0x140dd3760'), {
  onEnter(a) {
    const ui = a[0];
    try { send({refresh: true, left: slot(ui, 0x608), right: slot(ui, 0x610), sell: shown(ui.add(0x7c8).readPointer()),
                trade: shown(ui.add(0x7c0).readPointer()), a00: slot(ui, 0xa00)}); } catch (e) { send({refreshErr: '' + e}); }
  },
});
Interceptor.attach(at('0x140232fa0'), { onEnter(a) { send({worldInput: a[1].toInt32() & 0xff}); } });
Interceptor.attach(at('0x140dc2640'), {
  onEnter(a) { try { send({panelOpen: va(a[1]), vt: va(a[1].readPointer()), side: a[1].add(0x318).readU32(), left: slot(a[0], 0x608)}); } catch (e) {} },
});
Interceptor.attach(at('0x140dcac00'), { onEnter(a) { send({tradeClose: a[1].toInt32(), accepted: a[2].toInt32() & 0xff}); } });
send('trace-trade-clicks loaded');
