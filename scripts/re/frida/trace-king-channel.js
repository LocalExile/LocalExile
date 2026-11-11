// Load after common.js. Why the King's ritual/hex channel starts: the 0x1ab action reader, the channel base's
// StartChannelling call (the client crashed here with "No action set"), and the buffs component's 0x1c1 add.
// Run: python scripts/re/frida/run.py PathOfExileSteam.exe --follow --log logs/frida-king-channel.jsonl \
//      scripts/re/frida/common.js scripts/re/frida/trace-king-channel.js
const u16 = (p, o) => { try { return p.add(o).readU16(); } catch (e) { return null; } };
const u32 = (p, o) => { try { return p.add(o).readU32(); } catch (e) { return null; } };
const ptrAt = (p, o) => { try { return p.add(o).readPointer(); } catch (e) { return null; } };

// The Actor's S->C 0x1ab handler (trace-actions.js hooks the same function for skill/seq/target). Log the raw fields a
// channel skill may need: flags, target object, start/target coordinates, skill and sequence.
Interceptor.attach(at('0x14219aa10'), {
  onEnter(args) {
    const m = args[1];
    if (u16(m, 8) !== 0x1ab) return;
    const fields = {};
    for (let off = 0x18; off <= 0x40; off += 2) fields['u16@' + off.toString(16)] = u16(m, off);
    send({ts: Date.now(), kind: 'action-start', actor: u32(m, 0x10), target: u32(m, 0x20),
      skill: u16(m, 0x34), seq: u16(m, 0x36), fields});
  }
});

// GeometryChannelBase::StartChannelling. The crash is the CMP [RAX+0x28],R12 with RAX = [this+8]; the action pointer
// is what we need to see set on the official client.
Interceptor.attach(at('0x1419e3680'), {
  onEnter(args) {
    const self = args[0], action = ptrAt(self, 8);
    send({ts: Date.now(), kind: 'start-channelling', self: self.toString(), action: action ? action.toString() : null,
      actionFields: action ? {
        u32_0: u32(action, 0), u32_8: u32(action, 8), u32_10: u32(action, 0x10), u32_20: u32(action, 0x20),
        u32_28: u32(action, 0x28), u32_30: u32(action, 0x30), u16_34: u16(action, 0x34), u16_36: u16(action, 0x36),
      } : null,
      ret: va(this.returnAddress), bt: Thread.backtrace(this.context, Backtracer.FUZZY).slice(0, 12).map(va)});
  }
});

// The object's Buffs component AddBuff (S->C 0x1c1). Log the message object and a bounded raw view: the buff row,
// source id, source skill, flags and duration are what the server sends for the hex.
Interceptor.attach(at('0x141e14d80'), {
  onEnter(args) {
    const m = args[1];
    const raw = {};
    for (let off = 0x10; off <= 0x50; off += 4) raw['u32@' + off.toString(16)] = u32(m, off);
    send({ts: Date.now(), kind: 'buff-add', self: args[0].toString(), msg: m.toString(), raw});
  }
});

send('trace-king-channel ready');
