// Standalone, optional 3.29.3b recovery probe. No common.js needed. Reads only the local player's pools.
// .venv/Scripts/python.exe scripts/re/frida/run.py Game.patched.exe --seconds 120 --log logs/recovery.jsonl scripts/re/frida/trace-recovery.js
// Hook ABIs/signatures verified against Client/Game.exe with scripts/re/dis.py. A different build is refused.
// tick-change = before/after one client Life tick; server-update = the applied 0x1b1 value, not a raw socket packet.
// No claim that client-tick frequency is server tick rate or HUD rendering frequency. No native function calls.
(() => {
  const module = Process.enumerateModules().find(m => /^(Game\.patched|Game|PathOfExile)\.exe$/i.test(m.name));
  const base = ptr('0x140000000');
  const specs = [
    ['life-tick', '0x141e6a190', '40534883ec3083b9a801000000488bd9'],
    ['pool-update', '0x141e69fb0', '405341574883ec288b42204c8bfa448b'],
    ['frame-length', '0x141645fa0', '89911c010000c3cccccccccccccccccc'],
  ];
  if (!module) { send({event: 'refused', reason: 'no supported executable module'}); return; }
  const addresses = {};
  for (const [name, va, expected] of specs) {
    const p = module.base.add(ptr(va).sub(base)), size = expected.length / 2;
    const inside = p.compare(module.base) >= 0 && p.add(size).compare(module.base.add(module.size)) <= 0;
    let actual = '';
    try { if (inside) actual = Array.from(new Uint8Array(p.readByteArray(size))).map(x => x.toString(16).padStart(2, '0')).join(''); } catch (_) {}
    if (actual !== expected) { send({event: 'refused', hook: name, reason: 'code signature differs from 3.29.3b'}); return; }
    addresses[name] = p;
  }
  const hooks = [], start = Date.now();
  let messages = 0, errors = 0, lastSample = -Infinity, ticks = 0, changes = 0, updates = 0, timer;
  let lastTick = null, intervalTotal = 0, intervalCount = 0, maxInterval = 0;
  const emit = data => {
    if (messages++ >= 6000) return;
    send({tMs: Date.now() - start, ...data});
    if (messages === 6000) {
      send({event: 'limit', reason: '6000 messages; detaching this probe'});
      for (const hook of hooks) hook.detach();
      clearInterval(timer);
    }
  };
  // Life+8 is its owning object; Life tick 0x141e6a26d reads the object's id at +0x88.
  const player = life => !life.isNull() && life.add(8).readPointer().add(0x88).readU32() === 0x1000;
  const snapshot = life => Object.fromEntries([['life', 0x178], ['mana', 0x1c8], ['es', 0x210]].map(([name, offset]) => {
    const pool = life.add(offset);
    return [name, {current: pool.add(0x30).readS32(), fraction: pool.add(0x28).readFloat(),
      reserved: pool.add(0x10).readU32(), recoveryStatKey: pool.add(0x1c).readU32()}];
  }));
  const error = e => { if (errors++ < 5) emit({event: 'read-error', detail: String(e)}); };
  try {
    hooks.push(Interceptor.attach(addresses['life-tick'], {
      onEnter(a) {
        try {
          if (!player(a[0])) return;
          this.life = a[0]; this.before = snapshot(a[0]); ticks++;
          const now = Date.now();
          if (lastTick !== null) { const dt = now - lastTick; intervalTotal += dt; intervalCount++; maxInterval = Math.max(maxInterval, dt); }
          lastTick = now;
        } catch (e) { error(e); }
      },
      onLeave() {
        if (!this.before) return;
        try {
          const after = snapshot(this.life);
          if (JSON.stringify(after) !== JSON.stringify(this.before)) {
            changes++;
            if (Date.now() - lastSample >= 40) { lastSample = Date.now(); emit({event: 'tick-change', before: this.before, after}); }
          }
        } catch (e) { error(e); }
      },
    }));
    hooks.push(Interceptor.attach(addresses['pool-update'], {
      onEnter(a) {
        try {
          const life = a[0].add(8).readPointer();
          if (!player(life)) return;
          updates++;
          emit({event: 'server-update', objectId: 0x1000, pool: a[1].add(0x28).readU8(),
            current: a[1].add(0x20).readU32(), reserved: a[1].add(0x24).readU32(),
            previousCurrent: a[0].add(0x30).readS32(), previousFraction: a[0].add(0x28).readFloat()});
        } catch (e) { error(e); }
      },
    }));
    hooks.push(Interceptor.attach(addresses['frame-length'], {
      // Also called during local initialization (0 ms). This setter alone does not prove a received packet.
      onEnter(a) { emit({event: 'frame-length-set', durationMs: a[1].toUInt32()}); },
    }));
    timer = setInterval(() => {
      emit({event: 'summary', clientTicks: ticks, ticksChangingPools: changes, serverUpdates: updates,
        meanClientTickIntervalMs: intervalCount ? intervalTotal / intervalCount : null, maxClientTickIntervalMs: maxInterval,
        sampledTickChangesAtMostHz: 25});
      ticks = changes = updates = intervalTotal = intervalCount = maxInterval = 0;
    }, 1000);
    emit({event: 'ready', module: module.name, playerObjectId: 0x1000, clock: 'Date.now wall clock, milliseconds'});
  } catch (e) {
    for (const hook of hooks) hook.detach();
    if (timer) clearInterval(timer);
    send({event: 'refused', reason: String(e)});
  }
})();
