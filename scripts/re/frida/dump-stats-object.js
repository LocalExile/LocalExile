// Load after common.js. When the stat getter 0x1424d6510(stats, key) is asked for KEY, dumps that stats object's stored
// (key, value) pairs once per object: the sorted vector of 8-byte (u32 key, i32 value) entries at stats+0xf0/+0xf8
// (filled by 0x1424d65c0). Keys are client stat keys (Stats.dat row + 1).
const KEY = 0x126;   // main_hand_attack_speed_+%
const dumped = {};
Interceptor.attach(at('0x1424d6510'), {
  onEnter(args) {
    if (args[1].toInt32() !== KEY) return;
    const s = args[0], id = s.toString();
    if (dumped[id]) return;
    dumped[id] = 1;
    try {
      const b = s.add(0xf0).readPointer(), e = s.add(0xf8).readPointer();
      const n = Math.min(e.sub(b).toInt32() / 8, 400), pairs = [];
      for (let i = 0; i < n; i++) pairs.push(['0x' + b.add(i * 8).readU32().toString(16), b.add(i * 8 + 4).readS32()]);
      send({stats: id, caller: va(this.returnAddress), count: n, pairs});
    } catch (err) { send({stats: id, error: '' + err}); }
  }
});
send('dump-stats-object ready');
