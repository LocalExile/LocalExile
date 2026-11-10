// Passive tree screen mode: 0x14098bec0(view, mode) sets the graph view's +0x17b8 (0 normal, 1 Atlas-like, 2 gold
// respec at Bestel). Reports every call that changes it, with an image-relative backtrace, so the code that leaves the
// respec mode can be found. Also reports the refund sends (0x1403cc9a0). Load after common.js.
Interceptor.attach(at('0x14098bec0'), {
  onEnter(a) {
    const now = a[0].add(0x17b8).readU8(), mode = a[1].toInt32() & 0xff;
    if (now === mode) return;
    send({treeMode: `${now} -> ${mode}`, view: a[0].toString(),
          bt: Thread.backtrace(this.context, Backtracer.ACCURATE).slice(0, 12).map(va)});
  },
});
Interceptor.attach(at('0x1403cc9a0'), { onEnter() { send('tree click send (0x34/0x36)'); } });
send('trace-tree-mode loaded');
