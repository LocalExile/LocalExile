// Load after common.js. How long an action stays an Actor's current action (+0x1b0): logs every change of the current
// action's ids (+0x118 action type HASH16 / +0x11a skill id) per actor, with a millisecond clock, from the per-frame
// actor update 0x141e94900; and the values the stat getter 0x1424d6510 returns for the attack-time stats.
const ids = a => { try { return a.isNull() ? null : '0x' + a.add(0x118).readU16().toString(16) + '/0x' + a.add(0x11a).readU16().toString(16); } catch (e) { return '?'; } };
const last = {}, t0 = Date.now();
Interceptor.attach(at('0x141e94900'), {
  onEnter(args) {
    const k = args[0].toString(), cur = ids(args[0].add(0x1b0).readPointer());
    if (last[k] !== cur) { last[k] = cur; send({t: Date.now() - t0, actor: k, current: cur}); }
  }
});
const keys = new Set([0x142, 0x221a, 0x126, 0x2a6, 0x47]), seen = {};
Interceptor.attach(at('0x1424d6510'), {
  onEnter(args) { this.k = args[1].toInt32(); this.s = args[0].toString(); },
  onLeave(r) {
    if (!keys.has(this.k)) return;
    const id = this.s + ':' + this.k + ':' + r.toInt32();
    if (seen[id]) return;
    seen[id] = 1;
    send({t: Date.now() - t0, stat: '0x' + this.k.toString(16), value: r.toInt32(), stats: this.s, caller: va(this.returnAddress)});
  }
});
send('trace-action-life ready');
