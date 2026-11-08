// Load after common.js. What the client reads while it creates and starts a skill action (issue #167): every stat getter
// call 0x1424d6510(stats, key) that runs inside 0x1419473e0 (create the action for a skill id) or 0x141e937b0 (start the
// action on an Actor), with its value and caller. The first time one of Holy Strike's animation stats is read, the stats
// object it came from is dumped (the vector of 8-byte {u32 key, i32 value} at stats+0xf0/+0xf8, see dump-stats-object.js),
// so it shows whether the server sent a stat or the client fell back to a default.
// Keys are client keys (Stats.dat row + 1):
//   0x572c holy_strike_maximum_number_of_animated_weapons     (the limit the client uses)
//   0x572d base_holy_strike_maximum_number_of_animated_weapons
//   0x572e double_holy_strike_animated_weapons_limit
//   0x572f number_of_active_holy_strike_animated_weapons
//   0x5730 holy_strike_animates_owners_main_hand_weapon
const INTERESTING = new Set([0x572c, 0x572d, 0x572e, 0x572f, 0x5730]);
const depth = {};
for (const f of ['0x1419473e0', '0x141e937b0']) {
  Interceptor.attach(at(f), {
    onEnter() { depth[this.threadId] = (depth[this.threadId] || 0) + 1; send({enter: f, t: Date.now()}); },
    onLeave() { depth[this.threadId]--; }
  });
}
const dumped = {};
Interceptor.attach(at('0x1424d6510'), {
  onEnter(args) {
    this.on = (depth[this.threadId] || 0) > 0;
    if (!this.on) return;
    this.stats = args[0];
    this.key = args[1].toInt32();
    this.ret = va(this.returnAddress);
  },
  onLeave(r) {
    if (!this.on) return;
    send({stat: '0x' + this.key.toString(16), value: r.toInt32(), stats: this.stats.toString(), caller: this.ret});
    if (!INTERESTING.has(this.key) || dumped[this.stats.toString()]) return;
    dumped[this.stats.toString()] = 1;
    try {
      const b = this.stats.add(0xf0).readPointer(), e = this.stats.add(0xf8).readPointer();
      const n = Math.min(e.sub(b).toInt32() / 8, 500), pairs = [];
      for (let i = 0; i < n; i++) pairs.push(['0x' + b.add(i * 8).readU32().toString(16), b.add(i * 8 + 4).readS32()]);
      send({object: this.stats.toString(), count: n, pairs});
    } catch (err) { send({object: this.stats.toString(), error: '' + err}); }
  }
});
send('trace-holy-strike-stats ready');
