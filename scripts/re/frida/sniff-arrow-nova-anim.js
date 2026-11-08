// Arrow Nova rain-rig animation probe (read-only). Hooks the Animated component deserializer (0x141e5afd0) and dumps
// the component's fields at creation, so the official server's animation selection (cast-up vs landing) can be read.
// Run: python scripts/re/frida/run.py <pid> --seconds 0 --log logs/arrow-nova-anim-detail.jsonl ^
//      scripts/re/frida/sniff-arrow-nova-anim.js
'use strict';
const image = Process.enumerateModules().find(m => /^(PathOfExileSteam|Game\.patched)\.exe$/i.test(m.name));
if (!image) throw new Error('client image not found');
const base = ptr('0x140000000');
const at = va => image.base.add(ptr(va).sub(base));
const u32 = (p, o) => { try { return p.add(o).readU32(); } catch (e) { return null; } };
const i32 = (p, o) => { try { return p.add(o).readS32(); } catch (e) { return null; } };
const f32 = (p, o) => { try { return p.add(o).readFloat(); } catch (e) { return null; } };
const stamp = () => Date.now();
let until = 0;

// A use of Split Arrow (ActionTypes 13732) marks a 4 s window.
Interceptor.attach(at('0x14216ccb0'), {
  onEnter(args) {
    try {
      const a = args[2];
      if (a.add(0x118).readU16() !== 13732) return;
      until = stamp() + 4000;
      send({ t: stamp(), kind: 'use', target: [i32(a, 0x130), i32(a, 0x134)] });
    } catch (e) { }
  }
});

// The Animated component's deserializer: args[0] is the component, args[1] the blob reader.
Interceptor.attach(at('0x141e5afd0'), {
  onEnter(args) { this.c = args[0]; this.r = args[1]; },
  onLeave() {
    if (stamp() > until) return;
    const c = this.c;
    const dump = {};
    for (let o = 0; o < 0x140; o += 4) dump['i32@' + o.toString(16)] = i32(c, o);
    // Pointers to a UTF-16 "Metadata/" path (the .ao this component plays).
    const paths = [];
    for (let o = 0; o < 0x140; o += 4) {
      try {
        const q = c.add(o).readPointer();
        if (q.readUtf16String(9) !== 'Metadata/') continue;
        paths.push([o, q.readUtf16String(160)]);
      } catch (e) { }
    }
    send({ t: stamp(), kind: 'anim', comp: c.toString(), paths, dump });
    // Sample the same component later: the animation phase/clip evolve if the client plays a rig.
    const keep = ptr(c.toString());
    setTimeout(() => {
      try {
        const later = {};
        for (let o = 0; o < 0x140; o += 4) later['i32@' + o.toString(16)] = i32(keep, o);
        send({ t: stamp(), kind: 'animLater', comp: keep.toString(), paths, dump: later });
      } catch (e) { }
    }, 180);
  }
});

send('sniff-arrow-nova-anim ready');
