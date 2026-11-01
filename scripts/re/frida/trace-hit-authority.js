// Standalone, bounded observation probe for the pinned 3.29.3b client.
// Load WITHOUT common.js. See Agents/docs/re/client-hit-authority-probe.md.
'use strict';
(function () {
  const preferredBase = ptr('0x140000000');
  const limits = { milliseconds: 60000, events: 1000, readErrors: 3, depth: 32 };
  const entries = [
    ['world', '0x142187480', '48895c24105556574154415541564157'],
    ['result', '0x141e91580', '48895c24204c89442418555657415441'],
    ['assignment', '0x141e69fb0', '405341574883ec288b42204c8bfa448b'],
    ['sender', '0x14216e940', '48895c241048896c2418488974242057'],
    ['loss', '0x141e6a6b0', '48895c2410574883ec30488bd90f2974'],
  ];
  const watched = new Set([0x1ab, 0x1af, 0x1b0, 0x1b1, 0x1b3, 0x1b4, 0x1d1, 0x1d2]);
  const handles = [], stacks = new Map();
  let stopped = false, count = 0, errors = 0, serial = 0, timer = null;
  const started = Date.now();
  const module = Process.enumerateModules().find(m =>
    ['game.patched.exe', 'game.exe', 'pathofexile.exe'].includes(m.name.toLowerCase()));
  if (!module) {
    send({ event: 'refused', reason: 'recognized-module-missing' });
    return;
  }
  const address = va => module.base.add(ptr(va).sub(preferredBase));
  const hex = bytes => Array.from(new Uint8Array(bytes), b => b.toString(16).padStart(2, '0')).join('');
  // Check every signature BEFORE installing the first hook. This is a layout
  // guard, not a full-image hash check; operators must check the disk hash too.
  try {
    for (const [name, va, signature] of entries) {
      const entry = address(va);
      if (entry.compare(module.base) < 0 || entry.add(16).compare(module.base.add(module.size)) > 0 ||
          hex(entry.readByteArray(16)) !== signature) {
        send({ event: 'refused', reason: 'signature-mismatch', hook: name });
        return;
      }
    }
  } catch (_) {
    send({ event: 'refused', reason: 'signature-unreadable' });
    return;
  }

  function stop(reason) {
    if (stopped) return;
    stopped = true;
    if (timer !== null) clearTimeout(timer);
    let detachErrors = 0;
    for (const handle of handles.splice(0)) {
      try { handle.detach(); } catch (_) { detachErrors++; }
    }
    stacks.clear();
    send({ event: 'stopped', reason, events: count, readErrors: errors,
      detachErrors, elapsedMs: Date.now() - started });
  }
  function emit(event, fields) {
    if (stopped) return;
    send(Object.assign({ event, ordinal: ++count, elapsedMs: Date.now() - started }, fields));
    if (count >= limits.events) stop('event-limit');
  }
  function guarded(fn) {
    return function (...args) {
      if (stopped) return;
      try { fn.apply(this, args); } catch (_) {
        // Do not export arbitrary exception text, buffers or process strings.
        errors++;
        emit('read-error', { errors });
        if (errors >= limits.readErrors) stop('read-error-limit');
      }
    };
  }
  function parents(invocation) {
    const stack = stacks.get(invocation.threadId) || [];
    const last = kind => {
      for (let i = stack.length - 1; i >= 0; i--) if (stack[i].kind === kind) return stack[i].id;
      return null;
    };
    return { threadId: invocation.threadId, worldCallId: last('world'), resultCallId: last('result') };
  }
  function push(invocation, kind) {
    const stack = stacks.get(invocation.threadId) || [];
    if (stack.length >= limits.depth) { stop('nesting-limit'); return; }
    invocation.scope = { kind, id: ++serial };
    stack.push(invocation.scope);
    stacks.set(invocation.threadId, stack);
  }
  function pop(invocation) {
    const stack = stacks.get(invocation.threadId);
    if (!stack || !invocation.scope) return;
    const index = stack.indexOf(invocation.scope);
    if (index >= 0) stack.splice(index, 1);
    if (!stack.length) stacks.delete(invocation.threadId);
  }
  function result(r) {
    const mask = r.add(0x36).readU16(), hitData = r.add(0x49).readU8();
    return { sequence: r.add(0x40).readU16(), selector: r.add(0x42).readU8(),
      phase: r.add(0x43).readU8(), target: r.add(0x44).readU32(), mask,
      immediate: r.add(0x48).readU8(), hitDataAdmission: hitData,
      controlDuration: hitData && (mask & 0x400) ? r.add(0x34).readU16() : null,
      effectCode: hitData ? r.add(0x39).readU8() : null,
      quantizedDirection: hitData ? r.add(0x3a).readU8() : null };
  }
  function assignment(m) {
    return { current: m.add(0x20).readU32(), flatReservation: m.add(0x24).readU32(),
      pool: m.add(0x28).readU8(), percentageReservation: m.add(0x2a).readU16(),
      notificationFlags: m.add(0x2c).readU8() };
  }
  function pool(p) {
    return { current: p.add(0x30).readS32(), carry: p.add(0x28).readFloat(),
      flatReservation: p.add(0x10).readU32(), percentageReservation: p.add(0x14).readU32() };
  }
  function message(m, opcode) {
    const data = { opcode, entity: m.add(0x10).readU32(),
      ordering32: m.add(0x14).readU32(), ordering16: m.add(0x18).readU16() };
    if (opcode === 0x1af) data.result = result(m.add(0x20));
    if (opcode === 0x1b1) data.assignment = assignment(m);
    if (opcode === 0x1ab) {
      // Scalar offsets verified in the reader. Labels are deliberately neutral.
      data.action = { flags: m.add(0x38).readU16(), field34: m.add(0x34).readU16(),
        field36: m.add(0x36).readU16() };
    }
    if (opcode === 0x1b0) {
      data.key = { sequence: m.add(0x20).readU16(), selector: m.add(0x22).readU8(),
        phase: m.add(0x23).readU8(), target: m.add(0x24).readU32(),
        processing: m.add(0x28).readU8() };
    }
    return data;
  }
  const callbacks = {
    world: {
      onEnter: guarded(function (args) {
        const opcode = args[1].add(8).readU16();
        if (!watched.has(opcode)) return;
        const fields = { message: message(args[1], opcode), networkingMode: args[0].add(0x984).readU8(),
          ...parents(this) };
        push(this, 'world');
        if (this.scope) emit('world-enter', { ...fields, callId: this.scope.id });
      }),
      onLeave: guarded(function () {
        if (!this.scope) return;
        emit('world-leave', { ...parents(this), callId: this.scope.id });
        pop(this);
      }),
    },
    result: {
      onEnter: guarded(function (args) {
        this.record = args[1];
        const fields = { source: args[0].add(8).readPointer().add(0x88).readU32(),
          before: result(this.record), ...parents(this) };
        push(this, 'result');
        if (this.scope) emit('result-enter', { ...fields, callId: this.scope.id });
      }),
      onLeave: guarded(function () {
        if (!this.scope) return;
        // Always unwind the scope even if a record becomes unreadable on return.
        try { emit('result-leave', { ...parents(this), callId: this.scope.id, after: result(this.record) }); }
        finally { pop(this); }
      }),
    },
    assignment: {
      onEnter: guarded(function (args) {
        this.pool = args[0];
        this.sample = { ...parents(this), poolAddress: this.pool.toString(),
          entity: args[1].add(0x10).readU32(), input: assignment(args[1]), before: pool(this.pool) };
      }),
      onLeave: guarded(function () {
        if (this.sample) emit('pool-assignment', { ...this.sample, after: pool(this.pool) });
      }),
    },
    loss: {
      onEnter: guarded(function (args) {
        this.pool = args[0];
        this.sample = { ...parents(this), poolAddress: this.pool.toString(),
          caller: this.returnAddress.toString(), before: pool(this.pool) };
        // Amount is XMM1, NOT integer args[1]. Observe state changes instead.
      }),
      onLeave: guarded(function () {
        if (!this.sample) return;
        const after = pool(this.pool), before = this.sample.before;
        if (after.current !== before.current || after.carry !== before.carry)
          emit('local-loss', { ...this.sample, after });
      }),
    },
    sender: {
      onEnter: guarded(function (args) {
        emit('outbound-id', { ...parents(this), opcode: args[1].add(8).readU16(),
          caller: this.returnAddress.toString() });
      }),
    },
  };
  try {
    for (const [name, va] of entries) handles.push(Interceptor.attach(address(va), callbacks[name]));
    timer = setTimeout(() => stop('duration-limit'), limits.milliseconds);
    emit('ready', { probeVersion: 1, module: module.name, moduleBase: module.base.toString(), limits,
      observation: 'decoded-state-and-common-sender-only' });
  } catch (_) {
    stop('install-error');
  }
})();
