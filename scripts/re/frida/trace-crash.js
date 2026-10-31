// What a client crash looked like, for a crash that has no other lead (load on its own, without common.js):
//   python scripts/re/frida/run.py Game.patched.exe scripts/re/frida/trace-crash.js --log logs/client-crash.jsonl
// Every native exception the client raises (first chance; a crash is the one it does not survive) is sent with the
// local time in the servers' log format (HH:mm:ss.fff, ServerLog), the faulting instruction and memory access as Ghidra
// VAs, the registers, an image-relative backtrace and the last S→C messages the client read: the id of each message a
// handler group took in 0x14299e300(group, id, stream) (02-instance-connection.md §5). The pump offers every id to each
// group in turn (the world group first) and the first one that accepts it wins, so an id is recorded when the call
// returns non-zero, once. Not verified live yet: if lastReceived stays empty, that return value is not the acceptance.
// Line the time up with logs\live-gateway.log and the Gateway's packet dump (logs\packets, PacketHistory) to find the
// message the server sent wrong or left out. run.py prints {"detached": …, "reason": "process-terminated"} when the
// client goes.
(() => {
  const image = Process.getModuleByName('Game.patched.exe');
  const base = ptr('0x140000000');
  const rebase = v => image.base.add(ptr(v).sub(base));
  const inImage = p => p.compare(image.base) >= 0 && p.compare(image.base.add(image.size)) < 0;
  const where = p => {
    if (p === null || p === undefined) return null;
    if (inImage(p)) return '0x' + p.sub(image.base).add(base).toString(16);
    const mod = Process.findModuleByAddress(p);
    return mod ? `${mod.name}+0x${p.sub(mod.base).toString(16)}` : p.toString();
  };
  const pad = (n, w) => String(n).padStart(w, '0');
  const now = () => {
    const d = new Date();
    return `${pad(d.getHours(), 2)}:${pad(d.getMinutes(), 2)}:${pad(d.getSeconds(), 2)}.${pad(d.getMilliseconds(), 3)}`;
  };

  // The last S→C message ids, oldest first.
  const Kept = 64;
  const received = [];
  // The clock messages, several a second, would push the rest out: the 0x1c tick answers and the 0x2b5 frame ends
  // (the Gateway's PacketHistory leaves out the same ones). Id 0 is the pump's call without a message.
  const skipped = new Set([0, 0x1c, 0x2b5]);
  Interceptor.attach(rebase('0x14299e300'), {
    onEnter(args) { this.id = args[1].toUInt32() & 0xffff; },
    onLeave(accepted) {
      if (skipped.has(this.id) || (accepted.toUInt32() & 0xff) === 0) return;   // a C++ bool: only al is set
      received.push(`${now()} 0x${this.id.toString(16).padStart(4, '0')}`);
      if (received.length > Kept) received.shift();
    },
  });

  const registers = ['rip', 'rsp', 'rbp', 'rax', 'rbx', 'rcx', 'rdx', 'rsi', 'rdi', 'r8', 'r9', 'r10', 'r11', 'r12', 'r13', 'r14', 'r15'];
  // A client that throws and handles its own exceptions in a loop would flood the log: at most 20 reports a minute.
  let reports = 0, windowStart = Date.now();
  Process.setExceptionHandler(d => {
    if (Date.now() - windowStart > 60000) { windowStart = Date.now(); reports = 0; }
    if (reports++ >= 20) return false;
    const regs = {};
    for (const r of registers) try { regs[r] = d.context[r].toString(); } catch (e) { }
    let bytes = null;
    try { bytes = hexdump(d.address, { length: 16, header: false, ansi: false }); } catch (e) { }
    send({
      crash: now(), type: d.type, at: where(d.address), code: bytes,
      memory: d.memory ? { operation: d.memory.operation, address: where(d.memory.address) } : null,
      thread: Process.getCurrentThreadId(), registers: regs,
      backtrace: Thread.backtrace(d.context, Backtracer.FUZZY).slice(0, 24).map(where),
      lastReceived: received.slice(),
    });
    return false;   // the client handles it (or crashes) as it would without us
  });

  send({ ready: 'trace-crash', at: now() });
})();
