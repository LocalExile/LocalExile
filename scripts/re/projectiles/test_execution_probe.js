// Offline callback tests. No Frida import, process attachment or game execution.
'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {test} = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../frida/trace-projectile-execution.js'), 'utf8');
// Run the real scope-registration block with only its external observation services stubbed.
const start = source.indexOf("  for (const name of ['emission', 'contact', 'terminal', 'tornado', 'kb', 'bounce']) {");
const end = source.indexOf('  for (const count of [4, 5, 7, 8]) {', start);
assert.ok(start >= 0 && end > start, 'projectile scope registration block is present');

function fixture() {
  const pointers = new Map(), words = new Map(), hooks = new Map(), events = [];
  class Pointer {
    constructor(value) { this.value = value; }
    add(offset) { return new Pointer(this.value + offset); }
    isNull() { return this.value === 0; }
    toUInt32() { return this.value >>> 0; }
    readPointer() {
      assert.ok(pointers.has(this.value), `unexpected pointer read at ${this.value.toString(16)}`);
      return new Pointer(pointers.get(this.value));
    }
    readU32() {
      assert.ok(words.has(this.value), `unexpected word read at ${this.value.toString(16)}`);
      return words.get(this.value);
    }
  }
  const positioned = 0x2000, entity = 0x5000, targetId = 731;
  pointers.set(positioned + 8, entity);
  // Deliberately valid but wrong values expose confusion between the two native layouts.
  words.set(positioned + 0x88, 999);
  words.set(entity + 0x88, targetId);
  const scopes = new Map();
  vm.runInNewContext(source.slice(start, end), {
    Process: {getCurrentThreadId: () => 7}, calls: 0, scopes,
    snapshot: () => ({owner: 10, sequence: 12, derived: 2, stage: 0}),
    attach: (name, callbacks) => hooks.set(name, callbacks),
    emit: (type, fields) => events.push({type, ...fields})
  });
  function invoke(name, target, flags = 0) {
    const context = {};
    hooks.get(name).onEnter.call(context, [new Pointer(0x1000), new Pointer(target), new Pointer(flags)]);
    hooks.get(name).onLeave.call(context);
    return events.at(-2);
  }
  return {invoke, events, scopes, positioned, entity, targetId};
}

test('contact resolves Positioned owner while terminal reads Entity directly', () => {
  const f = fixture();
  assert.equal(f.invoke('contact', f.positioned).target, f.targetId);
  assert.equal(f.invoke('terminal', f.entity, 0x101).target, f.targetId);
  assert.equal(f.events.at(-2).naturalEnd, true);
  assert.equal(f.scopes.get(7).length, 0);
});

test('targetless contact and terminal keep zero identity without dereferencing null', () => {
  const f = fixture();
  assert.equal(f.invoke('contact', 0).target, 0);
  const terminal = f.invoke('terminal', 0, 0x100);
  assert.equal(terminal.target, 0);
  assert.equal(terminal.naturalEnd, false);
});

function tlsFixture(scoped = true) {
  const begin = source.indexOf('  // TLS observation:');
  const finish = source.indexOf("  attach('seed',", begin);
  assert.ok(begin >= 0 && finish > begin, 'TLS observation registration block is present');
  const hooks = new Map(), events = [], stack = [], words = new Map();
  class Pointer {
    constructor(value) { this.value = value; }
    add(offset) { return new Pointer(this.value + offset); }
    toUInt32() { return this.value >>> 0; }
    toString() { return '0x' + this.value.toString(16); }
    readU32() { assert.ok(words.has(this.value)); return words.get(this.value); }
  }
  vm.runInNewContext(source.slice(begin, finish), {
    Process: {getCurrentThreadId: () => 7}, calls: 100,
    scope: () => scoped ? {call: 1} : null, tlsStack: () => stack,
    attach: (name, callbacks) => hooks.set(name, callbacks), va: p => p.toString(),
    emit: (type, fields) => events.push({type, ...fields})
  });
  const pointer = value => new Pointer(value);
  return {hooks, events, stack, words, pointer};
}

test('TLS callbacks capture observed state and rejection-loop samples without invoking native code', () => {
  const f = tlsFixture(), chance = {returnAddress: f.pointer(0x141ba970d)};
  f.hooks.get('tlsChance').onEnter.call(chance, [f.pointer(50)]);
  for (const [before, after, sample] of [[1103527590, 2524885223, 126], [2524885223, 662824084, 1]]) {
    const bits = {returnAddress: f.pointer(0x140e3b48b)};
    f.hooks.get('tlsBits').onEnter.call(bits, [f.pointer(7)]);
    f.words.set(0x8170, before);
    f.hooks.get('tlsStep').onEnter.call({context: {rcx: f.pointer(0x8000), rbx: f.pointer(0x170), r9: f.pointer(7)}});
    f.words.set(0x8170, after); // The fixture simulates the native store; probe must only read it.
    f.hooks.get('tlsBits').onLeave.call(bits, f.pointer(sample));
  }
  f.hooks.get('tlsChance').onLeave.call(chance, f.pointer(1));
  assert.equal(f.stack.length, 0);
  assert.deepEqual(f.events.filter(e => e.type === 'tls_step').map(e => e.before), [1103527590, 2524885223]);
  assert.deepEqual(f.events.filter(e => e.type === 'tls_bits_end').map(e => e.after), [2524885223, 662824084]);
  assert.equal(f.events.at(-1).success, true);
});

test('TLS callbacks ignore unscoped helpers and retain a zero-bit no-draw call', () => {
  const ignored = tlsFixture(false), context = {returnAddress: ignored.pointer(0x141baa22b)};
  ignored.hooks.get('tlsBits').onEnter.call(context, [ignored.pointer(7)]);
  ignored.hooks.get('tlsBits').onLeave.call(context, ignored.pointer(70));
  assert.equal(ignored.events.length, 0);
  const f = tlsFixture(), zero = {returnAddress: f.pointer(0x141baa22b)};
  f.hooks.get('tlsBits').onEnter.call(zero, [f.pointer(0)]);
  f.hooks.get('tlsBits').onLeave.call(zero, f.pointer(0));
  assert.equal(f.events.at(-1).after, null);
  assert.equal(f.events.at(-1).sample, 0);
  assert.equal(f.stack.length, 0);
});

function contextFixture() {
  const begin = source.indexOf('  // Context receiver observation:');
  const finish = source.indexOf("  attach('rootStart',", begin);
  assert.ok(begin >= 0 && finish > begin, 'context observation registration block is present');
  const hooks = new Map(), events = [], words = new Map(), stacks = new Map();
  const witnessedPointers = new Set([0x1000]), tables = new Map([[0x1000, 0x14333ac40]]);
  let tid = 7;
  class Pointer {
    constructor(value) { this.value = value; }
    add(offset) { return new Pointer(this.value + offset); }
    equals(other) { return this.value === other.value; }
    toUInt32() { return this.value >>> 0; }
    toString() { return '0x' + this.value.toString(16); }
    readPointer() { assert.ok(tables.has(this.value)); return new Pointer(tables.get(this.value)); }
    readS32() { assert.ok(words.has(this.value), 'unexpected or deferred borrowed-pointer read'); return words.get(this.value) | 0; }
  }
  const pointer = value => new Pointer(value);
  const stack = t => {
    if (!stacks.has(t)) stacks.set(t, []);
    return stacks.get(t);
  };
  vm.runInNewContext(source.slice(begin, finish), {
    Process: {getCurrentThreadId: () => tid}, calls: 0,
    contextStack: stack, witnessed: p => witnessedPointers.has(p.value),
    key: () => ({owner: 10, sequence: 12, derived: 2, stage: 0}),
    moduleCaller: p => p.value < 0x150000000 ? p.toString() : null,
    va: p => p.toString(), attach: (name, callbacks) => hooks.set(name, callbacks),
    emit: (type, fields) => events.push({type, ...fields})
  });
  function enter(action = 0x1000, input = -25, caller = 0x141000000) {
    words.set(0x201c, input);
    if (!words.has(0x1434)) words.set(0x1434, 33);
    const invocation = {returnAddress: pointer(caller)};
    hooks.get('contextReceiver').onEnter.call(invocation, [pointer(action), pointer(0x2000)]);
    return invocation;
  }
  function assign(value, action = 0x1000) {
    hooks.get('contextAssign').onEnter.call({context: {rdi: pointer(action), rax: pointer(value >>> 0)}});
    words.set(action + 0x434, value); // Simulated native store, never done by the probe.
  }
  function leave(invocation) { hooks.get('contextReceiver').onLeave.call(invocation); }
  return {hooks, events, words, stacks, enter, assign, leave, setTid: value => { tid = value; }};
}

test('context callbacks observe the actual CPU store even when borrowed input changed', () => {
  const f = contextFixture(), outer = f.enter();
  f.words.delete(0x201c); // Input may be gone after entry; later hooks must not dereference it.
  f.assign(0);
  f.leave(outer);
  assert.deepEqual(f.events.map(e => e.type), ['context_begin', 'context_assign', 'context_end']);
  assert.equal(f.events[0].input1c, -25);
  assert.equal(f.events[1].assigned434, 0);
  assert.equal(f.events[2].after434, 0);
  assert.equal(f.stacks.get(7).length, 0);
});

test('context callbacks filter classes before field reads and retain independent nested pairs', () => {
  const f = contextFixture(), ignored = f.enter(0x3000);
  f.leave(ignored);
  assert.equal(f.events.length, 0);
  const outer = f.enter(0x1000, 12, 0x7ff700001234), inner = f.enter(0x1000, -2147483648);
  assert.equal(f.events[0].caller, null);
  assert.equal(f.events[0].callerRaw, '0x7ff700001234');
  assert.equal(f.events[1].parentCall, f.events[0].call);
  const unknown = f.enter(0x3000);
  f.assign(99, 0x3000); // Filtered nested class must not claim the observed parent's store.
  f.leave(unknown);
  f.assign(-2147483648);
  f.leave(inner);
  f.assign(12);
  f.leave(outer);
  assert.equal(f.events[2].assigned434, -2147483648);
  assert.equal(f.events.at(-1).after434, 12);
  assert.equal(f.stacks.get(7).length, 0);
});

test('context caller normalization uses the actual module range under ASLR', () => {
  const begin = source.indexOf('const moduleCaller =');
  const end = source.indexOf('const hex =', begin);
  assert.ok(begin >= 0 && end > begin);
  class Pointer {
    constructor(value) { this.value = BigInt(value); }
    add(offset) { return new Pointer(this.value + BigInt(offset)); }
    compare(other) { return this.value < other.value ? -1 : this.value > other.value ? 1 : 0; }
  }
  const image = {base: new Pointer(0x7ff700000000n), size: 0x4d8f000};
  const context = {image, va: p => '0x' + (p.value - image.base.value + 0x140000000n).toString(16)};
  vm.runInNewContext(source.slice(begin, end) + '\nthis.normalizeCaller = moduleCaller;', context);
  assert.equal(context.normalizeCaller(image.base), '0x140000000');
  assert.equal(context.normalizeCaller(image.base.add(image.size - 1)), '0x144d8efff');
  assert.equal(context.normalizeCaller(image.base.add(-1)), null);
  assert.equal(context.normalizeCaller(image.base.add(image.size)), null);
});

function windFixture() {
  const begin = source.indexOf('  // Inline TS wind observation:');
  const end = source.indexOf("  attach('rootStart',", begin);
  assert.ok(begin >= 0 && end > begin, 'inline wind registration block is present');
  const hooks = new Map(), events = [], words = new Map(), scopes = new Map(), winds = new Map();
  let tid = 7;
  class Pointer {
    constructor(value) { this.value = BigInt.asUintN(64, BigInt(value)); }
    add(value) { return new Pointer(this.value + BigInt(value)); }
    shr(bits) { return new Pointer(this.value >> BigInt(bits)); }
    equals(other) { return this.value === other.value; }
    isNull() { return this.value === 0n; }
    toUInt32() { return Number(BigInt.asUintN(32, this.value)); }
    toInt32() { return Number(BigInt.asIntN(32, this.value)); }
    toString() { return '0x' + this.value.toString(16); }
    readPointer() { return new Pointer(this.value === 0x1000n ? 0x143423898 : 0x14333ac40); }
    readU32() { assert.ok(words.has(this.toString()), 'unexpected wind field read'); return words.get(this.toString()); }
  }
  const pointer = value => new Pointer(value);
  const windStack = t => { if (!winds.has(t)) winds.set(t, []); return winds.get(t); };
  vm.runInNewContext(source.slice(begin, end), {
    Process: {getCurrentThreadId: () => tid}, calls: 0, scopes, windStack,
    Thread: {backtrace: () => [pointer(0x1414fc32e), pointer(0x700000000000)]}, Backtracer: {ACCURATE: 1},
    key: p => { assert.equal(p.toString(), '0x1000'); return {owner: 10, sequence: 12, derived: 2, stage: 0}; },
    moduleCaller: p => p.value >= 0x140000000n && p.value < 0x144d8f000n ? p.toString() : null,
    va: p => p.toString(), attach: (name, callbacks) => hooks.set(name, callbacks),
    emit: (type, fields) => events.push({type, ...fields})
  });
  function enter(action = 0x1000) {
    const invocation = {returnAddress: pointer(0x1414fc32e), context: {}};
    const packed = (6n << 32n) | 0xfffffffbn;
    hooks.get('windEntry').onEnter.call(invocation, [pointer(action), pointer(packed), pointer(-9)]);
    return invocation;
  }
  const registers = (action, after) => ({context: {rsi: pointer(action), rcx: pointer(0x8000),
    rdx: pointer(0x170), rax: pointer(after)}});
  function before(action = 0x1000, value = 1) {
    words.set('0x8170', value);
    hooks.get('windBefore').onEnter.call(registers(action, 0));
  }
  function after(action = 0x1000, cpu = 1103527590, memory = cpu) {
    words.set('0x8170', memory); // Only the fixture simulates the native store.
    hooks.get('windAfter').onEnter.call(registers(action, cpu));
  }
  const leave = invocation => hooks.get('windEntry').onLeave.call(invocation);
  return {events, scopes, winds, enter, before, after, leave, setTid: value => { tid = value; }};
}

test('wind callbacks read packed signed inputs and independently observe CPU and stored TLS state', () => {
  const f = windFixture(), invocation = f.enter();
  f.before(); f.after(0x1000, 1103527590, 99); f.leave(invocation);
  assert.deepEqual(f.events.map(e => e.type), ['scope_enter', 'wind_begin', 'wind_before', 'wind_after', 'wind_end', 'scope_exit']);
  assert.deepEqual(Array.from(f.events[1].grid), [-5, 6]);
  assert.equal(f.events[1].heightMarker, -9);
  assert.deepEqual(Array.from(f.events[1].frames), ['0x1414fc32e', null]);
  assert.equal(f.events[2].before, 1);
  assert.equal(f.events[3].cpuAfter, 1103527590);
  assert.equal(f.events[3].after, 99); // Analyzer must detect this mismatch, not the probe silently repair it.
  assert.equal(f.events[4].draws, 1);
  assert.equal(f.winds.get(7).length, 0);
  assert.equal(f.scopes.get(7).length, 0);
});

test('wind callbacks shield unknown nested classes and pair same-action reentry and independent threads', () => {
  const f = windFixture(), outer = f.enter(), unknown = f.enter(0x3000);
  assert.equal(f.scopes.get(7).at(-1).observed, false);
  f.before(0x3000); f.after(0x3000); f.leave(unknown);
  assert.equal(f.events.length, 2);
  const nested = f.enter(); f.before(); f.after(); f.leave(nested);
  f.setTid(8);
  const other = f.enter(); f.before(); f.after(); f.leave(other);
  f.setTid(7); f.before(); f.after(); f.leave(outer);
  assert.equal(f.events.filter(e => e.type === 'wind_before').length, 3);
  assert.equal(f.winds.get(7).length, 0);
  assert.equal(f.winds.get(8).length, 0);
});

test('generic RNG scope lookup does not attribute an unknown nested wind to its observed parent', () => {
  const begin = source.indexOf('const scope = tid =>');
  const end = source.indexOf('const tlsStack =', begin);
  assert.ok(begin >= 0 && end > begin);
  const parent = {call: 1}, scopes = new Map([[7, [parent, {observed: false}]]]);
  const context = {scopes};
  vm.runInNewContext(source.slice(begin, end) + '\nthis.lookup = scope;', context);
  assert.equal(context.lookup(7), null);
  scopes.get(7).pop();
  assert.equal(context.lookup(7), parent);
});

test('wind callbacks reject lost, duplicate, wrong-action and wrong-thread phases', () => {
  let f = windFixture(), invocation = f.enter();
  assert.throws(() => f.leave(invocation), /wind/);
  f = windFixture(); f.enter(); f.before();
  assert.throws(() => f.before(), /wind/);
  f = windFixture(); f.enter();
  assert.throws(() => f.before(0x2000), /wind/);
  f = windFixture(); f.enter(); f.before(); f.setTid(8);
  assert.throws(() => f.after(), /wind/);
});
