// Written for the 3.29.3b client and not ported: 3.29.3c changed this handler (virtual calls added and removed, UI
// vtable slots moved, e.g. 0x9b8 -> 0x9c8), so its call sites and slots need finding again before it runs on 3.29.3c.
// Ctrl + click on an inventory item (0x14030e3e0, reached from the grid click handler 0x1402ddbc0 through the grid's
// vtable +0x4d0): which "is this panel open" checks it makes on its UI state object (session +0x88 → +0x978) and what
// they return, so the branch it takes is visible (with a sell window open it should end in 0x142166660, 0xb2 with
// x = y = 0xff). Probes the virtual call sites only while the handler runs (Stalker hangs this client). Load after
// common.js.
const SITES = ('14030e5e1:468 14030e725:9b8 14030e790:cb0 14030e844:9b8 14030ed00:468 14030edf0:738 14030ee0d:dd0 ' +
  '14030f025:878 14030f03e:9e0 14030f47a:6b0 14030f497:648 14030f4b4:658 14030f50a:660 14030f567:698 14030f5f2:668 ' +
  '14030f648:d50 14030f68d:8c8 14030f77f:670 14030f7d5:6a0 14030f852:c90 14030f8b4:6c0 14030f90a:6c8 14030f960:6e0 ' +
  '14030f97d:6e8 14030f99a:6f0 14030fab4:6d0 14030fad1:d00 14030fb93:df8 14030fbc2:5d8 14030fc5a:7f0 14030fcf0:758 ' +
  '14030fd0d:748 14030fd2a:710 14030fd47:708 14030fd64:718 14030fdba:860 14030fe0d:688 14030fe26:c80 14030fe7d:c88 ' +
  '14030feca:838 14030ff7e:468 1403100b8:468 1403101a3:840 140310204:f90 14031023a:fe0 14031027b:ff8 1403102b7:728 ' +
  '1403102d0:998 140310380:870 1403103d1:878 140310427:880 140310467:9e8 1403104ac:888 1403104ff:890 140310552:898 ' +
  '140310593:8a0 1403105e8:988 14031077d:750 1403109d3:10b8').split(' ').map(s => s.split(':'));
let inside = false, seen = [];
const hooks = new Map();   // slot function → [callers]
Interceptor.attach(at('0x14030e3e0'), {
  onEnter(a) { inside = true; seen = []; send({ctrlClick: va(a[0]), item: a[1].toInt32()}); },
  onLeave() { inside = false; send({ctrlClickPath: seen}); },
});
for (const [site, slot] of SITES)
  Interceptor.attach(at('0x' + site), function () {
    if (!inside) return;
    const fn = this.context.rcx.readPointer().add(parseInt(slot, 16)).readPointer();
    seen.push(site + ' +0x' + slot + ' → ' + va(fn));
    if (!hooks.has(fn.toString())) {   // its return value, while the handler runs
      hooks.set(fn.toString(), true);
      Interceptor.attach(fn, { onLeave(rv) { if (inside) seen.push('   = 0x' + rv.toString(16)); } });
    }
  });
Interceptor.attach(at('0x142166660'), { onEnter(a) { send({sent0xb2: true, inventory: a[1].toInt32(), item: a[2].toInt32()}); } });
send('trace-ctrl-click loaded (' + SITES.length + ' sites)');
