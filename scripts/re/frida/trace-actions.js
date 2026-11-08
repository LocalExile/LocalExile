// Load after common.js. Why an S->C 0x1ab action does or does not start on an object: logs the Actor handler
// 0x14219aa10 (message id, skill id +0x34), the skill-usable check 0x141ef22b0 (manager, skill instance id, result), and
// the action created by 0x141ef0050 (manager, skill id, action or null).
Interceptor.attach(at('0x14219aa10'), {
  onEnter(args) {
    const id = args[1].add(8).readU16();
    if (id !== 0x1ab) return;
    send({actor: '0x' + args[1].add(0x10).readU32().toString(16), skill: '0x' + args[1].add(0x34).readU16().toString(16),
      seq: '0x' + args[1].add(0x36).readU16().toString(16), target: '0x' + args[1].add(0x20).readU32().toString(16)});
  }
});
Interceptor.attach(at('0x141ef22b0'), {
  onEnter(args) { this.skill = '0x' + args[1].add(0x40).readU16().toString(16); this.mgr = args[0].toString(); },
  onLeave(r) { send({usable: this.skill, mgr: this.mgr, result: r.toInt32() & 0xff, from: va(this.returnAddress)}); }
});
Interceptor.attach(at('0x141ef0050'), {
  onEnter(args) { this.out = args[1]; this.skill = '0x' + (args[2].toInt32() & 0xffff).toString(16); this.mgr = args[0].toString(); },
  onLeave() { send({action: this.out.readPointer().toString(), skill: this.skill, mgr: this.mgr}); }
});
send('trace-actions ready');
