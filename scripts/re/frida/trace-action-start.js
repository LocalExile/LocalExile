// Load after common.js. Whether an action actually starts on an Actor: 0x141e937b0(actor, &action, pos) returns 0 when
// it refuses (busy, queued, …). Logs the new action's ids (+0x118 action type HASH16, +0x11a skill id), the actor's
// current action (+0x1b0) and queued action (+0x1c0) the same way, and the result; and whether 0x141e94900 (start the
// queued action) runs.
const ids = a => { try { return a.isNull() ? null : '0x' + a.add(0x118).readU16().toString(16) + '/0x' + a.add(0x11a).readU16().toString(16); } catch (e) { return '?'; } };
Interceptor.attach(at('0x141e937b0'), {
  onEnter(args) {
    this.actor = args[0];
    this.info = {actor: args[0].toString(), action: ids(args[1].readPointer()),
      current: ids(args[0].add(0x1b0).readPointer()), queued: ids(args[0].add(0x1c0).readPointer())};
  },
  onLeave(r) { this.info.result = r.toInt32() & 0xff; send({start: this.info}); }
});
Interceptor.attach(at('0x141e94900'), { onEnter(args) { send({run: args[0].toString(), queued: ids(args[0].add(0x1c0).readPointer())}); } });
send('trace-action-start ready');
