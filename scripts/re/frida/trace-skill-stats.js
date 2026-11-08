// Load after common.js. Which stats the client reads while it creates and starts a skill action, and from where:
// the stat getter 0x1424d6510(stats, key) is logged (key, value, caller) only while 0x1419473e0 (create the action for
// a skill, called by 0x141ef0050) or 0x141e937b0 (start an action on the Actor) runs on the same thread.
const depth = {};
for (const f of ['0x1419473e0', '0x141e937b0']) {
  Interceptor.attach(at(f), {
    onEnter() { depth[this.threadId] = (depth[this.threadId] || 0) + 1; send({enter: f}); },
    onLeave() { depth[this.threadId]--; }
  });
}
const seen = {};
Interceptor.attach(at('0x1424d6510'), {
  onEnter(args) { this.on = depth[this.threadId] > 0; if (this.on) { this.stats = args[0]; this.key = args[1].toInt32(); this.ret = va(this.returnAddress); } },
  onLeave(r) {
    if (!this.on) return;
    const k = this.key + '@' + this.ret;
    if (seen[k]) return;
    seen[k] = 1;
    send({stat: '0x' + this.key.toString(16), value: r.toInt32(), stats: this.stats.toString(), caller: this.ret});
  }
});
send('trace-skill-stats ready');
