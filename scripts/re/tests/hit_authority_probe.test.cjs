// Offline Frida API/memory fixtures. These do not execute or attach the client.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const probe = path.join(__dirname, '..', 'frida', 'trace-hit-authority.js');
const prefixes = new Map([
  [0x142187480, '48895c24105556574154415541564157'],
  [0x141e91580, '48895c24204c89442418555657415441'],
  [0x141e69fb0, '405341574883ec288b42204c8bfa448b'],
  [0x14216e940, '48895c241048896c2418488974242057'],
  [0x141e6a6b0, '48895c2410574883ec30488bd90f2974'],
]);

function fixture(badSignature = false, failInstall = 0) {
  const memory = new Map(), hooks = new Map(), events = [], timers = new Map();
  let detached = 0, installed = 0;
  class Pointer {
    constructor(value) { this.value = Number(value); }
    add(n) { return new Pointer(this.value + Number(n)); }
    sub(n) { return new Pointer(this.value - Number(n.value ?? n)); }
    compare(n) { return Math.sign(this.value - n.value); }
    isNull() { return this.value === 0; }
    toString() { return '0x' + this.value.toString(16); }
    readU8() { return this.readU32() & 255; }
    readU16() { return this.readU32() & 65535; }
    readU32() { assert.ok(memory.has(this.value), `unexpected read ${this}`); return memory.get(this.value) >>> 0; }
    readS32() { return this.readU32() | 0; }
    readFloat() { assert.ok(memory.has(this.value)); return memory.get(this.value); }
    readPointer() { return new Pointer(this.readU32()); }
    readByteArray() {
      assert.ok(prefixes.has(this.value));
      return Buffer.from(badSignature === true || badSignature === this.value ? '00'.repeat(16) : prefixes.get(this.value), 'hex');
    }
  }
  const ptr = value => value instanceof Pointer ? value : new Pointer(value);
  const context = {
    Process: { enumerateModules: () => [{ name: 'Game.patched.exe', base: ptr(0x140000000), size: 0x5000000 }] },
    ptr, Uint8Array, Date,
    send: value => events.push(JSON.parse(JSON.stringify(value))),
    setTimeout: fn => { const id = timers.size + 1; timers.set(id, fn); return id; },
    clearTimeout: id => timers.delete(id),
    Interceptor: { attach(address, callbacks) {
      if (installed + 1 === failInstall) throw new Error('fixture install failure');
      installed++; hooks.set(address.value, callbacks);
      return { detach() { detached++; hooks.delete(address.value); } };
    } },
  };
  assert.ok(fs.existsSync(probe), 'bounded hit-authority probe has not been implemented');
  vm.runInNewContext(fs.readFileSync(probe, 'utf8'), context, { filename: probe });
  function enter(address, args) {
    const callbacks = hooks.get(address); assert.ok(callbacks, `hook ${address.toString(16)}`);
    const invocation = { threadId: 7, returnAddress: ptr(0x141111111) };
    callbacks.onEnter.call(invocation, args.map(ptr));
    return () => callbacks.onLeave?.call(invocation, ptr(0));
  }
  const put = (address, fields) => Object.entries(fields).forEach(([offset, value]) => memory.set(address + Number(offset), value));
  return { ptr, memory, put, enter, hooks, events, timers, stats: () => ({ detached, installed }) };
}

test('records decoded identities and separates result-scoped loss from absolute pool assignment', () => {
  const f = fixture(), world = 0x10000, message = 0x20000, actor = 0x30000, entity = 0x31000;
  const result = message + 0x20, life = 0x40000, pool = 0x40178, resource = 0x50000, outbound = 0x60000;
  f.put(world, { 0x984: 0 });
  f.put(message, { 8: 0x1af, 0x10: 12, 0x14: 0x10101, 0x18: 1 });
  f.put(result, { 0x40: 0x80bc, 0x42: 2, 0x43: 3, 0x44: 99, 0x36: 0x4201, 0x48: 1, 0x49: 1, 0x34: 0, 0x39: 0, 0x3a: 128 });
  f.put(actor, { 8: entity }); f.put(entity, { 0x88: 12 });
  f.put(pool, { 8: life, 0x30: 100, 0x28: 0.25, 0x10: 0, 0x14: 0 });
  f.put(outbound, { 8: 0x25 });
  const endWorld = f.enter(0x142187480, [world, message]);
  const endResult = f.enter(0x141e91580, [actor, result, 0, 0]);
  f.enter(0x14216e940, [0, outbound])();
  const endLoss = f.enter(0x141e6a6b0, [pool]);
  f.put(pool, { 0x30: 96 }); endLoss();
  f.put(result, { 0x36: 0x4221 }); endResult(); endWorld();
  f.put(resource, { 8: 0x1b1, 0x10: 99, 0x14: 0, 0x18: 0, 0x20: 90, 0x24: 0, 0x28: 0, 0x2a: 0, 0x2c: 0 });
  const endAssignmentWorld = f.enter(0x142187480, [world, resource]);
  const endAssignment = f.enter(0x141e69fb0, [pool, resource]);
  f.put(pool, { 0x30: 90 }); endAssignment(); endAssignmentWorld();
  const find = name => f.events.find(e => e.event === name);
  assert.equal(find('world-enter').message.result.sequence, 0x80bc);
  assert.equal(find('world-enter').networkingMode, 0);
  assert.equal(find('result-leave').after.mask, 0x4221);
  assert.equal(find('local-loss').resultCallId, find('result-enter').callId);
  assert.equal(find('outbound-id').resultCallId, find('result-enter').callId);
  assert.equal(find('pool-assignment').resultCallId, null);
  assert.equal(find('pool-assignment').worldCallId, f.events.filter(e => e.event === 'world-enter')[1].callId);
  assert.equal(find('pool-assignment').before.current, 96);
  assert.equal(find('pool-assignment').after.current, 90);
  f.put(message, { 8: 0x1ab, 0x34: 0x4001, 0x36: 0x80bd, 0x38: 1 });
  f.enter(0x142187480, [world, message])();
  assert.equal(f.events.filter(e => e.event === 'world-enter').at(-1).message.action.field36, 0x80bd);
  f.put(message, { 8: 0x1b0, 0x20: 0x80bc, 0x22: 2, 0x23: 3, 0x24: 99, 0x28: 1 });
  f.enter(0x142187480, [world, message])();
  assert.equal(f.events.filter(e => e.event === 'world-enter').at(-1).message.key.target, 99);
  f.put(message, { 8: 0x1af }); f.put(result, { 0x49: 0 });
  for (const offset of [0x34, 0x39, 0x3a]) f.memory.delete(result + offset);
  f.enter(0x142187480, [world, message])();
  assert.equal(f.events.filter(e => e.event === 'world-enter').at(-1).message.result.effectCode, null);
  assert.ok(!JSON.stringify(f.events).includes('payloadBytes'));
  assert.equal(f.hooks.size, 5);
});

test('refuses mismatched signatures and bounds owned hooks, errors and output', () => {
  const bad = fixture(true);
  assert.equal(bad.stats().installed, 0);
  assert.equal(bad.events[0].event, 'refused');
  assert.equal(fixture(0x141e6a6b0).stats().installed, 0, 'a late signature mismatch must precede all installations');
  const partial = fixture(false, 3);
  assert.equal(partial.hooks.size, 0);
  assert.equal(partial.stats().detached, 2);
  assert.equal(partial.events.at(-1).reason, 'install-error');
  const limited = fixture();
  limited.put(0x60000, { 8: 0x25 });
  for (let i = 0; i < 1200 && limited.hooks.size; i++) limited.enter(0x14216e940, [0, 0x60000])();
  assert.equal(limited.hooks.size, 0);
  assert.equal(limited.stats().detached, 5);
  assert.equal(limited.events.at(-1).reason, 'event-limit');
  assert.ok(limited.events.length <= 1001);
  const timed = fixture();
  [...timed.timers.values()][0]();
  assert.equal(timed.hooks.size, 0);
  assert.equal(timed.events.at(-1).reason, 'duration-limit');
  assert.equal(timed.timers.size, 0);
  const errors = fixture();
  for (let i = 0; i < 3; i++) errors.enter(0x14216e940, [0, 0x60000])();
  assert.equal(errors.hooks.size, 0);
  assert.equal(errors.events.at(-1).reason, 'read-error-limit');
});
