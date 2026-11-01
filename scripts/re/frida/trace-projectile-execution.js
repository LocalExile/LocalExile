// Self-contained, observation-only probe for the pinned PoE1 3.29.3b x64 profile.
// Operator workflow and limitations: Agents/docs/re/projectiles/capture-contract.md.
// Do not load common.js. No game NativeFunction calls or gameplay memory writes.
'use strict';
const candidates = Process.enumerateModules().filter(m => /^(Game|Game\.patched)\.exe$/i.test(m.name));
if (candidates.length !== 1) throw new Error('expected exactly one project game module');
const image = candidates[0], preferred = ptr('0x140000000');
const at = address => image.base.add(ptr(address).sub(preferred));
const va = address => '0x' + address.sub(image.base).add(preferred).toString(16);
const moduleCaller = address => address.compare(image.base) >= 0 &&
  address.compare(image.base.add(image.size)) < 0 ? va(address) : null;
const hex = (address, n) => Array.from(new Uint8Array(address.readByteArray(n)))
  .map(b => b.toString(16).padStart(2, '0')).join('');
const hooks = {
  seed: ['0x14193a720', '48895c24080fb7811a010000'],
  bind: ['0x141bab560', '48895c241048896c24185657'],
  consume: ['0x141939f40', '48895c241048896c24185657'],
  result: ['0x141e90f50', '488bc448895808488968104c'],
  resultControl: ['0x141e90c30', '48895c240848897424104889'],
  resultControlMatch: ['0x141e90cc8', '488bd3488d4db7e81ce10000'],
  resultProcess: ['0x141e91580', '48895c24204c894424185556'],
  init5: ['0x141a0c9d0', '48895c240848897424104889'],
  init7: ['0x141bba070', '48895c240848897424104889'],
  init8: ['0x1419c29b0', '48895c240848897424104889'],
  init4: ['0x14146d8f0', '48895c240848897424104889'],
  bound: ['0x140484fa0', '48895c240848896c24104889'],
  tlsChance: ['0x140e3b450', '40574883ec208bf983f964'],
  tlsBits: ['0x140e3b310', '4883ec084533d285c97470'],
  tlsStep: ['0x140e3b34f', '6904196d4ec6410539300000'],
  emission: ['0x141bb13a0', '85d20f84cb100000488bc448'],
  contact: ['0x141ba7b30', '405553565741544155415641'],
  terminal: ['0x141ba68c0', '48895c241848895424105556'],
  tornado: ['0x141a827c0', '48895c241055565741564157'],
  windEntry: ['0x1414fc340', '488954241053555657415648'],
  windBefore: ['0x1414fc4ae', '6904116d4ec6410539300000'],
  windAfter: ['0x1414fc4bd', 'c1e81025ff7f00000f57c9f3'],
  kb: ['0x141ac6bd0', '488bc455535741544156488d'],
  bounce: ['0x141bb09c0', '488bc4555641564881ec0001'],
  stage: ['0x14193a170', '40535556574883ec48488bf9'],
  skillMessage: ['0x1421940a0', '48895c240855565741544155'],
  statApply: ['0x1424cfaf0', '48895c241848896c24205648'],
  statView: ['0x14193b350', '40574883ec304c8b81b80100'],
  contextReceiver: ['0x141bae280', '48895c2408574883ec2048'],
  contextAssign: ['0x141bae29d', '8987340400004883c4205f'],
  rootStart: ['0x14193d870', '48895c241048896c241848'],
  animationRequest: ['0x141e9a3c0', '488bc44889580848895010'],
  alternateRequest: ['0x141e9bb80', '48895c2408555657415441'],
  animationEvent: ['0x141e938c0', '488bc44889581048897018'],
  alternateEvent: ['0x141e93e90', '48895c2410488974241857']
};
// Validate every site BEFORE installing any hook. Prefixes do not attest the full binary.
if (Process.arch !== 'x64' || image.base.readU16() !== 0x5a4d) throw new Error('not x64 PE');
const pe = image.base.add(image.base.add(0x3c).readU32());
if (pe.readU32() !== 0x4550 || pe.add(8).readU32() !== 0x6a9a0ce8 ||
    pe.add(0x50).readU32() !== 0x4d8f000) throw new Error('incompatible projectile PE profile');
for (const [name, [address, prefix]] of Object.entries(hooks)) {
  if (hex(at(address), prefix.length / 2) !== prefix) throw new Error('hook prefix mismatch: ' + name);
}
const k32 = Process.getModuleByName('kernel32.dll');
const qpc = new NativeFunction(k32.getExportByName('QueryPerformanceCounter'), 'int', ['pointer']);
const qpf = new NativeFunction(k32.getExportByName('QueryPerformanceFrequency'), 'int', ['pointer']);
const qbuf = Memory.alloc(8);
qpf(qbuf);
const frequency = qbuf.readU64().toString();
let n = 0, calls = 0, dropped = 0, errors = 0, ended = false, capped = false;
const maxEvents = 20000, scopes = new Map(), statScopes = new Map(), resultScopes = new Map(),
  tlsScopes = new Map(), contextScopes = new Map(), windScopes = new Map(), listeners = [];
function emit(type, fields = {}, force = false) {
  if (ended) return;
  if (!force && n >= maxEvents) {
    dropped++;
    if (!capped) { capped = true; emit('cap', {dropped, errors}, true); }
    return;
  }
  // A NativeFunction call can yield the JS lock; do not share its output buffer across callbacks.
  const stamp = Memory.alloc(8);
  qpc(stamp);
  send(Object.assign({projectileExecution: 1, type, n: n++, q: stamp.readU64().toString(),
    tid: Process.getCurrentThreadId()}, fields));
}
function guard(callback) {
  return function (...args) {
    if (ended || capped) return;
    try { return callback.apply(this, args); }
    catch (error) { errors++; emit('error', {error: String(error)}, true); }
  };
}
const state = p => [0, 4, 8, 12, 16].map(o => p.add(o).readU32());
const owner = p => p.add(0xe8).readPointer().add(8).readPointer().add(0x88).readU32();
const key = p => ({owner: owner(p), sequence: p.add(0x11a).readU16(),
  derived: p.add(0x11c).readU8(), stage: p.add(0x120).readU8()});
const actorOwner = actor => actor.add(8).readPointer().add(0x88).readU32();
const resultHandle = (actor, p) => ({key: {owner: actorOwner(actor), sequence: p.readU16(),
  derived: p.add(2).readU8(), stage: p.add(3).readU8()}, target: p.add(4).readU32()});
const resultSnapshot = (actor, p) => Object.assign(resultHandle(actor, p.add(0x40)), {
  auxiliary34: p.add(0x34).readU16(), flags: p.add(0x36).readU16(), code: p.add(0x39).readU8(),
  kind0: p.add(0x48).readU8(), kind1: p.add(0x49).readU8()});
const controlProcessReturns = new Set(['0x141e90e04', '0x141e90e22', '0x141e90ec6', '0x141e90ee8']);
const resultStack = tid => {
  if (!resultScopes.has(tid)) resultScopes.set(tid, []);
  return resultScopes.get(tid);
};
// Exact final client classes for the six research witnesses (bow/wand share one).
const witnessTables = new Set(['0x14326d930', '0x1432ec7e8', '0x14333ac40',
  '0x14326b9c0', '0x143423898']);
const witnessed = p => !p.isNull() && witnessTables.has(va(p.readPointer()));
// Main request builders only: common projectile, spell, and both bow branches.
// Requests recursively made inside Actor animation helpers are not these inputs.
const mainRequestReturns = new Set(['0x141ba31a3', '0x141ba319c', '0x1419eebc3',
  '0x1419eebad', '0x141b6f273', '0x141b6f33f']);
function snapshot(p) {
  const data = Object.assign({action: p.toString(), vtable: va(p.readPointer()),
    outerRemaining: p.add(0x2d0).readU32(), outerTotal: p.add(0x2d4).readU32(),
    innerRemaining: p.add(0x2b8).readU8(), innerTotal: p.add(0x2b9).readU8(),
    releaseOrdinal: p.add(0x3f8).readU32(), speedCacheBits: p.add(0x3a4).readU32(),
    sourceOrdinal: p.add(0x448).readU32(), volleyAbsoluteSlot: p.add(0x450).readU32(),
    pierces: p.add(0x454).readU32(), chains: p.add(0x458).readU32(),
    forkGeneration: p.add(0x45c).readU32(), phase1: p.add(0x461).readU8(),
    phase2: p.add(0x462).readU8()}, key(p));
  const entity = p.add(0x328).readPointer();
  if (!entity.isNull()) {
    const pos = entity.add(0x98).readPointer();
    data.projectileGrid = [pos.add(0x294).readS32(), pos.add(0x298).readS32()];
    data.heightMarker = pos.add(0x290).readS32(); // Raw sentinel; no game getter is invoked.
    data.worldZ = pos.add(0x2b4).readFloat();
  }
  return data;
}
const scope = tid => {
  const s = scopes.get(tid), top = s && s.length ? s[s.length - 1] : null;
  return top && top.observed !== false ? top : null;
};
const tlsStack = tid => {
  if (!tlsScopes.has(tid)) tlsScopes.set(tid, []);
  return tlsScopes.get(tid);
};
const statScope = tid => { const s = statScopes.get(tid); return s && s.length ? s[s.length - 1] : null; };
const contextStack = tid => {
  if (!contextScopes.has(tid)) contextScopes.set(tid, []);
  return contextScopes.get(tid);
};
const windStack = tid => {
  if (!windScopes.has(tid)) windScopes.set(tid, []);
  return windScopes.get(tid);
};
function attach(name, callbacks) {
  const wrapped = {};
  for (const [event, callback] of Object.entries(callbacks)) wrapped[event] = guard(callback);
  listeners.push(Interceptor.attach(at(hooks[name][0]), wrapped));
}
emit('start', {profile: '3.29.3b', compatible: true, module: image.name, base: image.base.toString(),
  qpf: frequency, maxEvents, identityCheck: 'PE timestamp/size and 35 hook prefixes; not full digest',
  observations: {resultIngress: 1, contactTargetLayout: 1, tlsRandom: 1, contextReceiver: 1, windRandom: 1},
  limitations: ['Scoped RNG subset; inline and unscoped TLS draws are not all observed.',
    'Contact/result callbacks do not prove damage or a C2S report.',
    'Stat views are scoped pointer observations; no stat getter or cache refresh is invoked.',
    'Animation requests/events are entry observations, not successful clip admission or release.',
    'Context receivers are final-class-filtered; zero observed calls do not establish absence of a producer.',
    'Wind TLS is one inline draw per returned TS wind call; bounded backtraces do not establish outer TLS history.']}, true);
try {
  // Context receiver observation: independent of root/projectile scopes and their timing.
  attach('contextReceiver', {
    onEnter(args) {
      if (!witnessed(args[0])) return;
      const tid = Process.getCurrentThreadId(), stack = contextStack(tid), parent = stack[stack.length - 1];
      const data = {call: ++calls, tid, parentCall: parent ? parent.call : null,
        action: args[0].toString(), vtable: va(args[0].readPointer()), key: key(args[0]),
        context: args[1].toString(), input1c: args[1].add(0x1c).readS32(),
        before434: args[0].add(0x434).readS32(),
        callerRaw: this.returnAddress.toString(), caller: moduleCaller(this.returnAddress)};
      this.entry = {call: data.call, tid, action: args[0], assigned: false};
      stack.push(this.entry);
      emit('context_begin', data);
    },
    onLeave() {
      if (!this.entry) return;
      const entry = this.entry, stack = contextStack(entry.tid);
      if (stack[stack.length - 1] !== entry || !entry.assigned) throw new Error('unpaired context receiver return');
      const after434 = entry.action.add(0x434).readS32();
      stack.pop();
      emit('context_end', {call: entry.call, tid: entry.tid, action: entry.action.toString(), after434});
      // Borrowed input is never retained/dereferenced after onEnter.
    }
  });
  attach('contextAssign', {onEnter() {
    const action = this.context.rdi;
    // An unobserved class may be nested inside an observed one; filter before pairing.
    if (!witnessed(action)) return;
    const tid = Process.getCurrentThreadId(), stack = contextStack(tid), entry = stack[stack.length - 1];
    if (!entry || !entry.action.equals(action) || entry.assigned) throw new Error('unpaired context assignment');
    entry.assigned = true;
    emit('context_assign', {call: entry.call, tid, action: action.toString(),
      assigned434: this.context.rax.toUInt32() | 0});
    // At the aligned store instruction EAX is the actual value read after the base helper.
  }});
  // Inline TS wind observation: independent invocation stack, including unobserved-class sentinels.
  attach('windEntry', {
    onEnter(args) {
      const tid = Process.getCurrentThreadId(), action = args[0];
      const observed = !action.isNull() && va(action.readPointer()) === '0x143423898';
      this.wind = {tid, action, observed, phase: 0};
      windStack(tid).push(this.wind);
      if (!scopes.has(tid)) scopes.set(tid, []);
      scopes.get(tid).push(this.wind);
      if (!observed) return; // No projectile-specific fields on other classes.
      const entry = this.wind;
      entry.call = ++calls;
      entry.name = 'tornadoWind';
      emit('scope_enter', {call: entry.call, tid, name: entry.name});
      emit('wind_begin', {call: entry.call, tid, action: action.toString(),
        vtable: va(action.readPointer()), key: key(action),
        grid: [args[1].toInt32(), args[1].shr(32).toInt32()], heightMarker: args[2].toInt32(),
        callerRaw: this.returnAddress.toString(), caller: moduleCaller(this.returnAddress),
        frames: Thread.backtrace(this.context, Backtracer.ACCURATE).slice(0, 12).map(moduleCaller)});
    },
    onLeave() {
      const entry = this.wind;
      if (!entry) return;
      const stack = windStack(Process.getCurrentThreadId()), activeScopes = scopes.get(entry.tid);
      if (stack[stack.length - 1] !== entry || activeScopes[activeScopes.length - 1] !== entry)
        throw new Error('wind nesting/thread/scope mismatch');
      if (entry.observed) {
        if (entry.phase !== 2) throw new Error('incomplete wind draw');
        emit('wind_end', {call: entry.call, tid: entry.tid, action: entry.action.toString(), draws: 1});
        emit('scope_exit', {call: entry.call, tid: entry.tid, name: entry.name});
      }
      scopes.get(entry.tid).pop();
      stack.pop();
    }
  });
  attach('windBefore', {onEnter() {
    const tid = Process.getCurrentThreadId(), stack = windStack(tid), entry = stack[stack.length - 1];
    if (entry && !entry.observed) return;
    if (!entry || !entry.action.equals(this.context.rsi) || entry.phase !== 0 ||
        this.context.rdx.toUInt32() !== 0x170) throw new Error('unpaired wind pre-store');
    entry.state = this.context.rcx.add(0x170);
    entry.phase = 1;
    emit('wind_before', {call: entry.call, tid, action: entry.action.toString(),
      state: entry.state.toString(), before: entry.state.readU32()});
  }});
  attach('windAfter', {onEnter() {
    const tid = Process.getCurrentThreadId(), stack = windStack(tid), entry = stack[stack.length - 1];
    if (entry && !entry.observed) return;
    if (!entry || !entry.action.equals(this.context.rsi) || entry.phase !== 1 ||
        this.context.rdx.toUInt32() !== 0x170 || !entry.state.equals(this.context.rcx.add(0x170)))
      throw new Error('unpaired wind post-store');
    entry.phase = 2;
    emit('wind_after', {call: entry.call, tid, action: entry.action.toString(),
      state: entry.state.toString(), cpuAfter: this.context.rax.toUInt32(), after: entry.state.readU32()});
  }});
  attach('rootStart', {
    onEnter(args) {
      if (!witnessed(args[0])) return;
      const tid = Process.getCurrentThreadId();
      this.entry = {call: ++calls, name: 'root_start', tid, key: snapshot(args[0])};
      if (!scopes.has(tid)) scopes.set(tid, []);
      scopes.get(tid).push(this.entry);
      emit('scope_enter', this.entry);
    },
    onLeave() {
      if (!this.entry) return;
      emit('scope_exit', {call: this.entry.call, name: 'root_start', tid: this.entry.tid});
      scopes.get(this.entry.tid).pop();
    }
  });
  for (const name of ['animationRequest', 'alternateRequest']) {
    attach(name, {onEnter(args) {
      const tid = Process.getCurrentThreadId();
      const context = (scopes.get(tid) || []).slice().reverse().find(s => s.name === 'root_start');
      if (!context || !mainRequestReturns.has(va(this.returnAddress))) return;
      const actorOwner = args[0].add(8).readPointer().add(0x88).readU32();
      if (actorOwner !== context.key.owner) return;
      // Preparation may update repeat state after root entry; read the request-time inputs.
      const action = ptr(context.key.action);
      emit('animation_request', {call: context.call, tid, key: key(action),
        request: args[1].toString(), requestSeed: args[1].add(8).readU32(),
        outerRemaining: action.add(0x2d0).readU32(), actorSeed: args[0].add(0x21c).readU16(),
        route: name, caller: va(this.returnAddress)});
    }});
  }
  for (const [name, channel] of [['animationEvent', 'normal'], ['alternateEvent', 'alternate']]) {
    attach(name, {onEnter(args) {
      const event = args[3], action = args[0].add(0x50).readPointer();
      if (event.add(0x38).readU16() !== 0x9b8d || !witnessed(action)) return;
      emit('animation_event', {key: key(action), action: action.toString(), channel,
        eventHash: event.add(0x38).readU16(), eventType: event.add(8).readU32(),
        descriptorKind: args[2].add(0x4c).readU32(),
        actorFlags: args[0].sub(0x160).add(0x218).readU8()});
      // Entry only: the native +0x68/getter and all forwarding gates still run afterward.
    }});
  }
  attach('skillMessage', {
    onEnter(args) {
      const message = args[1];
      if (message.add(8).readU16() !== 0x1b4) return;
      const begin = message.add(0x28).readPointer(), end = message.add(0x30).readPointer();
      if (end.compare(begin) < 0) throw new Error('reversed skill-stat vector');
      const bytes = end.sub(begin).toUInt32();
      if (!end.sub(begin).equals(ptr(bytes)) || bytes % 8 !== 0) throw new Error('invalid skill-stat vector');
      const count = bytes / 8, pairs = [];
      for (let i = 0; i < Math.min(count, 256); i++) {
        const pair = begin.add(i * 8);
        pairs.push({statKey: pair.readU32(), value: pair.add(4).readS32()});
      }
      const tid = Process.getCurrentThreadId();
      this.entry = {update: ++calls, owner: args[0].add(8).readPointer().add(0x88).readU32(),
        skillId: message.add(0x20).readU16(), selector: message.add(0x22).readU8(),
        pairCount: count, pairsTruncated: count > pairs.length, pairs};
      if (!statScopes.has(tid)) statScopes.set(tid, []);
      statScopes.get(tid).push(this.entry);
      this.tid = tid;
      emit('skill_stat_update', this.entry);
    },
    onLeave() {
      if (this.entry) {
        statScopes.get(this.tid).pop();
        emit('skill_stat_end', {update: this.entry.update});
      }
    }
  });
  attach('statApply', {
    onEnter(args) {
      const update = statScope(Process.getCurrentThreadId());
      // Only the checked 0x1b4 pair loop; nested/unrelated raw writes are different observations.
      if (!update || !this.returnAddress.equals(at('0x142194652'))) return;
      this.pair = {update: update.update, stats: args[0].toString(),
        statKey: args[1].toUInt32(), value: args[2].toInt32()};
    },
    onLeave() { if (this.pair) emit('skill_stat_apply', this.pair); }
  });
  attach('statView', {
    onEnter(args) {
      const context = scope(Process.getCurrentThreadId());
      if (!context) return;
      const skill = args[0].add(0xf0).readPointer();
      this.view = {call: context.call, key: key(args[0]), action: args[0].toString(),
        selector: args[1].toUInt32(), skill: skill.toString(), skillId: skill.add(0x40).readU16(),
        rawPrimary: skill.add(0xf8).readPointer().toString(),
        effectivePrimary: skill.add(0x138).readPointer().toString(), caller: va(this.returnAddress)};
    },
    onLeave(ret) { if (this.view) emit('stat_view', Object.assign(this.view, {selected: ret.toString()})); }
  });
  for (const name of ['emission', 'contact', 'terminal', 'tornado', 'kb', 'bounce']) {
    attach(name, {
      onEnter(args) {
        const tid = Process.getCurrentThreadId();
        const entry = {call: ++calls, name, tid, key: snapshot(args[0])};
        if (name === 'contact' || name === 'terminal') {
          // Contact receives Positioned*, terminal receives Entity*. Both may be null.
          const target = name === 'contact' && !args[1].isNull() ? args[1].add(8).readPointer() : args[1];
          entry.target = target.isNull() ? 0 : target.add(0x88).readU32();
          if (name === 'terminal') entry.naturalEnd = (args[2].toUInt32() & 255) !== 0;
        }
        if (!scopes.has(tid)) scopes.set(tid, []);
        scopes.get(tid).push(entry);
        this.entry = entry;
        emit('scope_enter', entry);
      },
      onLeave() {
        if (!this.entry) return;
        emit('scope_exit', {call: this.entry.call, name, tid: this.entry.tid});
        scopes.get(this.entry.tid).pop();
      }
    });
  }
  for (const count of [4, 5, 7, 8]) {
    attach('init' + count, {
      onEnter(args) {
        this.context = scope(Process.getCurrentThreadId());
        if (!this.context) return;
        this.state = args[0];
        this.words = Array.from({length: count}, (_, i) => args[1].add(i * 4).readU32());
        this.caller = va(this.returnAddress);
      },
      onLeave() {
        if (this.words) emit('rng_init', {call: this.context.call, state: this.state.toString(),
          caller: this.caller, words: this.words, after: state(this.state)});
      }
    });
  }
  attach('bound', {
    onEnter(args) {
      this.context = scope(Process.getCurrentThreadId());
      if (!this.context) return;
      this.state = args[0].readPointer();
      this.before = state(this.state);
      this.bound = args[1].toUInt32();
      this.bits = args[0].add(8).readU32();
      this.mask = args[0].add(12).readU32();
      this.caller = va(this.returnAddress);
    },
    onLeave(ret) {
      if (this.before) emit('rng_bound', {call: this.context.call, state: this.state.toString(),
        caller: this.caller, bound: this.bound, wordBits: this.bits, mask: this.mask,
        before: this.before, after: state(this.state), sample: ret.toUInt32()});
    }
  });
  // TLS observation: native helper entries and the aligned pre-LCG instruction only.
  // Read the native-selected TLS pointer; never call a game getter or reconstruct gs in JS.
  attach('tlsChance', {
    onEnter(args) {
      const tid = Process.getCurrentThreadId(), context = scope(tid);
      if (!context) return;
      this.entry = {call: ++calls, tid, kind: 'chance'};
      tlsStack(tid).push(this.entry);
      emit('tls_chance_begin', {call: this.entry.call, tid, scope: context.call,
        p: args[0].toUInt32(), caller: va(this.returnAddress)});
    },
    onLeave(ret) {
      if (!this.entry) return;
      if (tlsStack(this.entry.tid).pop() !== this.entry) throw new Error('TLS chance nesting mismatch');
      emit('tls_chance_end', {call: this.entry.call, tid: this.entry.tid,
        success: (ret.toUInt32() & 255) !== 0});
    }
  });
  attach('tlsBits', {
    onEnter(args) {
      const tid = Process.getCurrentThreadId(), context = scope(tid);
      if (!context) return;
      const stack = tlsStack(tid), parent = stack[stack.length - 1];
      this.entry = {call: ++calls, tid, kind: 'bits', steps: 0, state: null};
      stack.push(this.entry);
      emit('tls_bits_begin', {call: this.entry.call, tid, scope: context.call,
        bits: args[0].toUInt32(), caller: va(this.returnAddress),
        chanceCall: parent && parent.kind === 'chance' ? parent.call : null});
    },
    onLeave(ret) {
      if (!this.entry) return;
      const entry = this.entry;
      if (tlsStack(entry.tid).pop() !== entry) throw new Error('TLS bit-helper nesting mismatch');
      emit('tls_bits_end', {call: entry.call, tid: entry.tid, sample: ret.toUInt32(),
        after: entry.state === null ? null : entry.state.readU32()});
    }
  });
  attach('tlsStep', {onEnter() {
    const tid = Process.getCurrentThreadId(), stack = tlsStack(tid), entry = stack[stack.length - 1];
    if (!entry || entry.kind !== 'bits') return;
    if (this.context.rbx.toUInt32() !== 0x170) throw new Error('unexpected TLS offset');
    const state = this.context.rcx.add(0x170);
    entry.state = state;
    emit('tls_step', {call: entry.call, tid, index: entry.steps++, state: state.toString(),
      bits: this.context.r9.toUInt32(), before: state.readU32()});
  }});
  attach('seed', {
    onEnter(args) {
      this.key = key(args[0]); this.newDerived = args[1].toUInt32() & 255;
      this.ordinal = args[2].toUInt32();
    },
    onLeave(ret) { if (this.key) emit('object_seed', {key: this.key, newDerived: this.newDerived,
      ordinal: this.ordinal, seed: ret.toUInt32()}); }
  });
  attach('bind', {onEnter(args) {
    emit('bind', {key: snapshot(args[0]), entity: args[1].toString(),
      object: args[1].add(0x88).readU32()});
  }});
  attach('result', {onEnter(args) {
    emit('result', Object.assign(resultSnapshot(args[0], args[1]), {caller: va(this.returnAddress)}));
  }});
  attach('resultControl', {
    onEnter(args) {
      const tid = Process.getCurrentThreadId();
      this.entry = {call: ++calls, tid, type: 'control', actor: args[0], handle: args[1], matched: false};
      const data = Object.assign(resultHandle(args[0], args[1]), {call: this.entry.call, tid,
        actor: args[0].toString(), processByte: args[2].toUInt32() & 255, caller: va(this.returnAddress)});
      resultStack(tid).push(this.entry);
      emit('result_control_begin', data);
    },
    onLeave() {
      if (!this.entry) return;
      const entry = this.entry;
      if (resultStack(entry.tid).pop() !== entry) throw new Error('result control nesting mismatch');
      emit('result_control_end', {call: entry.call, tid: entry.tid, matched: entry.matched});
      // This function does not return a business/found bool. Never interpret AL here.
    }
  });
  attach('resultControlMatch', {onEnter() {
    const tid = Process.getCurrentThreadId(), stack = resultStack(tid), entry = stack[stack.length - 1];
    if (!entry || entry.type !== 'control' || !entry.actor.equals(this.context.rsi) ||
        !entry.handle.equals(this.context.r14) || entry.matched) throw new Error('unpaired result control match');
    entry.matched = true;
    emit('result_control_match', {call: entry.call, tid,
      record: resultSnapshot(entry.actor, this.context.rbx)});
  }});
  attach('resultProcess', {
    onEnter(args) {
      const tid = Process.getCurrentThreadId(), stack = resultStack(tid), parent = stack[stack.length - 1];
      const caller = va(this.returnAddress), action = args[3];
      this.entry = {call: ++calls, tid, type: 'process', actor: args[0], record: args[1]};
      const controlCall = controlProcessReturns.has(caller) && parent && parent.type === 'control' &&
        parent.actor.equals(args[0]) ? parent.call : null;
      const data = {call: this.entry.call, tid, actor: args[0].toString(), caller, controlCall,
        before: resultSnapshot(args[0], args[1]), inputAction: action.toString(),
        inputActionMissing: action.isNull(), inputActionKey: action.isNull() ? null : key(action),
        statsHandle: [args[2].readPointer().toString(), args[2].add(8).readPointer().toString()]};
      stack.push(this.entry);
      emit('result_process_begin', data);
    },
    onLeave(ret) {
      if (!this.entry) return;
      const entry = this.entry;
      // Copy scalar fields before the caller destroys the temporary result. No deferred pointer reads.
      const after = resultSnapshot(entry.actor, entry.record);
      if (resultStack(entry.tid).pop() !== entry) throw new Error('result processing nesting mismatch');
      emit('result_process_end', {call: entry.call, tid: entry.tid, after,
        completed: (ret.toUInt32() & 255) !== 0});
    }
  });
  attach('consume', {
    onEnter(args) { this.key = key(args[0]); this.target = args[1].toUInt32(); },
    onLeave(ret) { if (this.key) emit('consume', {key: this.key, target: this.target,
      found: (ret.toUInt32() & 255) !== 0}); }
  });
  attach('stage', {
    onEnter(args) { this.action = args[0]; this.before = key(args[0]); },
    onLeave() { if (this.before) emit('stage', {before: this.before, after: key(this.action)}); }
  });
} catch (error) {
  for (const listener of listeners) listener.detach();
  errors++; emit('error', {error: 'hook installation failed: ' + String(error)}, true);
  emit('end', {dropped, errors}, true); ended = true;
  throw error;
}
const timer = setInterval(() => emit('status', {dropped, errors}, true), 2000);
rpc.exports = {finish() {
  if (!ended) { clearInterval(timer); emit('end', {dropped, errors}, true); ended = true; }
  return {events: n, dropped, errors};
}};
