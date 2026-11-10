// Gold: the session's u32 at +0xc40c (0 in the session constructor 0x14214b4a0; read by the gold respec cost check
// 0x1409996c0 and the village gold label 0x1404ffb90). Takes the session from the S→C dispatcher (0x142150ae0, first
// argument; again when the session changes) and puts a hardware write watchpoint on the field in every thread; each write is reported with its value
// and an image-relative backtrace. Load after common.js.
let field = null;
let misses = 0;
Process.setExceptionHandler(d => {
  // Only the watchpoint's single-step; anything else (a real crash) goes on to the client.
  if (field === null || d.type !== 'single-step') return false;
  const t = Process.getCurrentThreadId();
  send({goldWrite: field.readU32(), at: va(d.address), bt: Thread.backtrace(d.context, Backtracer.ACCURATE).slice(0, 12).map(va)});
  // A watchpoint fires after the write; re-arm it so the next write is caught too.
  for (const th of Process.enumerateThreads()) if (th.id === t) { th.unsetHardwareWatchpoint(0); th.setHardwareWatchpoint(0, field, 4, 'w'); }
  return true;
});
Interceptor.attach(at('0x142150ae0'), {
  onEnter(a) {
    // The dispatcher gets the session's second base (+8, vtable 0x14346c6c0, set by the constructor next to +0's).
    if (!a[0].readPointer().equals(at('0x14346c6c0'))) { if (misses++ < 3) send({notSession: va(a[0].readPointer())}); return; }
    const s = a[0].sub(8);
    if (field !== null && field.equals(s.add(0xc40c))) return;
    field = s.add(0xc40c);
    send({session: s.toString(), gold: field.readU32()});
    for (const th of Process.enumerateThreads())
      try { th.setHardwareWatchpoint(0, field, 4, 'w'); } catch (e) { send({wpErr: '' + e, thread: th.id}); }
  },
});
send('trace-gold loaded');
