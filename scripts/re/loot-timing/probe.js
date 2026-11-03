// Frozen 3.29.3b observer. Loaded by capture.py after full on-disk SHA256 validation.
// No common.js, game function calls, packet sends, or gameplay data writes.
'use strict';
const image = Process.mainModule, base = ptr('0x140000000');
const at = va => image.base.add(ptr(va).sub(base));
const specs = {
  intake: ['0x1421872b0', '48895c24084c89442418565741564883'],
  dispatch: ['0x142187480', '48895c24105556574154415541564157'],
  construct: ['0x141ebe190', '48895c241048894c2408565741564883'],
  item: ['0x141ea10a0', '48895c24085556574154415541564157'],
  life: ['0x141e69fb0', '405341574883ec288b42204c8bfa448b'],
  chest: ['0x14218823f', '418896680100000fb646214188866901']
};
const hex = (p, n) => Array.from(new Uint8Array(p.readByteArray(n)))
  .map(x => x.toString(16).padStart(2, '0')).join('');
if (Process.arch !== 'x64' || image.base.readU16() !== 0x5a4d) throw Error('not x64 PE');
const pe = image.base.add(image.base.add(0x3c).readU32());
if (pe.readU32() !== 0x4550 || pe.add(8).readU32() !== 0x6a9a0ce8 ||
    pe.add(0x50).readU32() !== 0x4d8f000) throw Error('incompatible PE profile');
for (const [name, [va, signature]] of Object.entries(specs)) {
  if (hex(at(va), signature.length / 2) !== signature) throw Error('signature mismatch: ' + name);
}
const kernel = Process.getModuleByName('kernel32.dll');
const qpc = new NativeFunction(kernel.getExportByName('QueryPerformanceCounter'), 'int', ['pointer']);
const qpf = new NativeFunction(kernel.getExportByName('QueryPerformanceFrequency'), 'int', ['pointer']);
const clock = () => { const p = Memory.alloc(8); if (!qpc(p)) throw Error('QPC failed'); return p.readU64().toString(); };
const frequencyBuffer = Memory.alloc(8);
if (!qpf(frequencyBuffer)) throw Error('QPF failed');
const frequency = frequencyBuffer.readU64().toString();
let sequence = 0, messageNo = 0, trialNo = 0, trial = null, errors = 0, dropped = 0, stopped = false;
const maxEvents = 100000, hooks = [], dispatches = new Map(), builds = new Map(), incoming = new Map(), typePaths = new Map();
const watched = new Set([0x1b1, 0x1bd, 0x2b9, 0x2bb, 0x1a9]);
function emit(event, fields = {}, q = clock(), selectedTrial = trial, force = false) {
  if (stopped) return;
  if (sequence >= maxEvents && !force) { dropped++; return; }
  send({lootTiming: 1, event, n: sequence++, q, tid: Process.getCurrentThreadId(), trial: selectedTrial, ...fields});
}
function guard(fn) {
  return function (...args) {
    if (stopped || sequence >= maxEvents) { if (!stopped) dropped++; return; }
    try { return fn.apply(this, args); }
    catch (e) { errors++; emit('error', {detail: String(e)}, clock(), trial, true); }
  };
}
function push(map, value) {
  const tid = Process.getCurrentThreadId();
  if (!map.has(tid)) map.set(tid, []);
  map.get(tid).push(value);
}
function top(map) { return (map.get(Process.getCurrentThreadId()) || []).slice(-1)[0]; }
function pop(map) { const stack = map.get(Process.getCurrentThreadId()); if (stack) stack.pop(); }
function pathOf(rec) {
  const key = rec.toString();
  if (typePaths.has(key)) return typePaths.get(key);
  // Existing type-record witness; bounded scan. Unknown is retained rather than guessed.
  for (const candidate of [rec, rec.add(0x28).readPointer()]) {
    for (let off = 0; off < 0x100; off += 8) {
      try {
        const p = candidate.add(off).readPointer();
        if (p.readUtf16String(9) === 'Metadata/') {
          const path = p.readUtf16String(192).split('\0')[0];
          if (typePaths.size >= 4096) typePaths.clear();
          typePaths.set(key, path);
          return path;
        }
      } catch (_) {}
    }
  }
  return null;
}
function entityInfo(entity) {
  // 0x142427360 stores the shared type record at entity+8; +0x78 is the World.
  return {id: entity.add(0x88).readU32(), path: pathOf(entity.add(8).readPointer())};
}
function packet(p) {
  const op = p.add(8).readU16();
  if (!watched.has(op)) return null;
  const data = {op, id: p.add(0x10).readU32(), ordering: p.add(0x14).readU32(), checkpoint: p.add(0x18).readU16()};
  if (op === 0x1b1) Object.assign(data, {current: p.add(0x20).readU32(), pool: p.add(0x28).readU8()});
  if (op === 0x1bd) Object.assign(data, {open: p.add(0x20).readU8(), flag21: p.add(0x21).readU8()});
  if (op === 0x2b9) data.typeHash = p.add(0x20).readU32();
  return data;
}
function attach(name, callbacks) { hooks.push(Interceptor.attach(at(specs[name][0]), callbacks)); }
try {
  attach('intake', {onEnter: guard(function (a) {
    const q = clock(), p = a[2].readPointer();
    if (p.isNull()) return;
    const data = packet(p); if (!data) return;
    const key = ++messageNo;
    incoming.set(p.toString(), key);
    emit('intake', {key, world: a[0].toString(), ...data}, q);
  })});
  attach('dispatch', {
    onEnter: guard(function (a) {
      const q = clock(), p = a[1], data = packet(p);
      this.scope = {key: incoming.get(p.toString()) || null, world: a[0].toString(), data,
        pointer: p.toString(), trial};
      push(dispatches, this.scope);
      if (data) emit('dispatch', {key: this.scope.key, world: this.scope.world, ...data}, q);
    }),
    onLeave: function () {
      if (!this.scope) return;
      try {
        if (this.scope.data) emit('dispatch_end', {key: this.scope.key, ...this.scope.data}, clock(), this.scope.trial);
        incoming.delete(this.scope.pointer);
      } finally { pop(dispatches); }
    }
  });
  attach('construct', {
    onEnter: guard(function (a) {
      const q = clock();
      this.scope = {id: a[2].toUInt32(), path: pathOf(a[1].readPointer()), trial,
        key: (top(dispatches) || {}).key || null};
      push(builds, this.scope);
      // Needed for source classification and area-load diagnostics, but not a loot identity.
      emit('construct', {id: this.scope.id, path: this.scope.path, key: this.scope.key}, q);
    }),
    onLeave: function () { if (this.scope) pop(builds); }
  });
  attach('item', {onEnter: guard(function () {
    const q = clock(), scope = top(builds);
    if (!scope) throw Error('WorldItem read outside observed server construction');
    emit('item_construct', {id: scope.id, path: scope.path, key: scope.key}, q, scope.trial);
  })});
  attach('life', {onEnter: guard(function (a) {
    const q = clock(), msg = a[1];
    if (msg.add(0x28).readU8() !== 0 || msg.add(0x20).readU32() !== 0) return;
    const before = a[0].add(0x30).readS32();
    if (before <= 0) return;
    const life = a[0].add(8).readPointer(), info = entityInfo(life.add(8).readPointer());
    emit('life_zero_apply', {...info, before, key: (top(dispatches) || {}).key || null}, q);
  })});
  // Mid-instruction-boundary hook just BEFORE the authoritative Chest+0x168 write.
  // Static dispatch case 0x1bd: R14=Chest; DL=new open byte; old byte at +0x168.
  attach('chest', {onEnter: guard(function () {
    const q = clock(), chest = this.context.r14;
    const before = chest.add(0x168).readU8(), open = this.context.rdx.toUInt32() & 255;
    if (before !== 0 || open === 0) return;
    emit('chest_open_apply', {...entityInfo(chest.add(8).readPointer()), before, open,
      key: (top(dispatches) || {}).key || null}, q);
  })});
  emit('start', {frequency, image: image.name, profile: '3.29.3b-loot-v1', maxEvents,
    semantics: 'decoded intake and pre-application observers; not socket/server timestamps'});
} catch (e) {
  for (const h of hooks) h.detach();
  throw e;
}
rpc.exports = {
  begin(kind, source) {
    if (trial !== null) throw Error('end the current trial first');
    if (!['kill', 'chest', 'pack'].includes(kind)) throw Error('kind must be kill, chest or pack');
    trial = ++trialNo;
    emit('trial_begin', {kind, source});
    return trial;
  },
  end() {
    if (trial === null) throw Error('no active trial');
    emit('trial_end'); trial = null;
  },
  mark(text) { emit('marker', {text}); },
  finish() {
    // Leave an active trial unclosed: the analyzer must flag interruption.
    for (const h of hooks) h.detach();
    const pending = [...dispatches.values(), ...builds.values()].reduce((n, s) => n + s.length, 0);
    emit('finish', {errors, dropped, pending, activeTrial: trial}, clock(), trial, true);
    stopped = true;
    return {errors, dropped, pending};
  }
};
