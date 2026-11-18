// Load after common.js. Dumps every stats object that holds the client's virtual skill-effect lifetime
// (skill_effect_duration, key 0x168) or the beam skill's own numbers, with the (key, value) pairs the client
// actually stored. The monster's skill record is built when the boss is created, so enter the area after this is
// attached.
const KEYS = new Set([0x168, 0x157, 0x126]);   // skill_effect_duration, base_skill_effect_duration, main_hand_attack_speed
const dumped = {};
Interceptor.attach(at('0x1424d6510'), {
  onEnter(args) {
    const key = args[1].toInt32();
    const s = args[0], id = s.toString();
    if (dumped[id]) return;
    try {
      const b = s.add(0xf0).readPointer(), e = s.add(0xf8).readPointer();
      const n = Math.min(e.sub(b).toInt32() / 8, 400);
      const pairs = [];
      let has = false;
      for (let i = 0; i < n; i++) {
        const k = b.add(i * 8).readU32(), v = b.add(i * 8 + 4).readS32();
        pairs.push(['0x' + k.toString(16), v]);
        if (k === 0x168) has = true;
      }
      if (!has && !(KEYS.has(key))) return;
      dumped[id] = 1;
      send({ stats: id, asked: '0x' + key.toString(16), count: n, pairs });
    } catch (err) { send({ stats: id, error: '' + err }); }
  }
});
send('dump-effect-duration ready');
