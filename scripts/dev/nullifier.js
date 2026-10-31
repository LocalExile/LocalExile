// Cheap ("nullified") client for agent testing: loaded by scripts/dev/nullifier.py into Client/Game.patched.exe
// while the process is still suspended at spawn, before the engine starts. See Agents/docs/dev/headless-client.md.
// nullifier.py prepends `const OPTIONS = {...};`. No input simulation and no executable patching on disk; the hooks
// live only in this process and only for its life. The settings file is not this script's business: the client runs
// in a folder whose config.json names its own settings file.
// Always: the window is never activated (no focus stealing), and the two-copies mutexes get session-local names
// (runMutexPrefix). Everything else only when asked for:
//   window        minimized | hidden | normal: how the window shows.
//   nullRenderer  the engine's own "Render: OFF" switch: it then creates its Null device (no D3D/Vulkan device, no
//                 swap chain, no GPU memory, no texture uploads) instead of the configured DirectX/Vulkan one, and
//                 the Null index buffers report their index format (below).
//   noRenderJobs  the engine's "Test Disable Frame Render Jobs" switch (frame render jobs are not kicked).
//   noTextureUpload, noTextureStreaming  the texture manager's Upload / Streaming switches.
//   frameSleepMs  sleep this long once per main-loop frame (in the state-stack update), a frame-rate cap that also
//                 works with the Null device, which has no vsync or present to wait on.
//   crashLog      append a crash's registers and backtrace to this file (diagnostics).
//   traceFiles    log every CreateFileW of a path matching this regular expression (diagnostics).
//   traceMovies   log every Bink movie the client opens and closes, with its frames (diagnostics).
//   mimalloc      {name: value} the launcher put in the client's environment as MIMALLOC_<NAME>: the allocator's
//                 values for these options are logged once it has read them.
'use strict';
const module = Process.getModuleByName('Game.patched.exe');
const at = va => module.base.add(ptr(va).sub(ptr('0x140000000')));
function checked(name, va, expected) {
  const p = at(va);
  const actual = Array.from(new Uint8Array(p.readByteArray(expected.length / 2)))
    .map(x => x.toString(16).padStart(2, '0')).join('');
  if (actual !== expected) throw new Error(`Unsupported client: ${name} code differs from 3.29.3b`);
  return p;
}
const log = message => send({type: 'log', message});
const keep = [];

// At spawn only ntdll and the executable are mapped: hook a DLL once the loader brings it in.
function whenLoaded(name, install) {
  const loaded = Process.findModuleByName(name);
  if (loaded) { install(loaded); return; }
  let done = false;
  const observer = Process.attachModuleObserver({onAdded(m) {
    if (done || m.name.toLowerCase() !== name) return;
    done = true; install(m);
  }});
  keep.push(observer);
}

// The client allows two copies per PC: at startup (0x140101d85) it creates Global\PoERunMutexA, takes it with a zero
// wait, else Global\PoERunMutexB, else throws "You may only run up to two copies of Path of Exile at a time." The
// handles are never used again. Our clients get session-local names instead (OPTIONS.runMutexPrefix + "PoERunMutexA"
// / "B", e.g. Local\LocalExile-pr-x-bot1-PoERunMutexA), so they never take one of the two Global slots the user's and
// other sessions' clients need. Only these two exact names are renamed: PathOfExileSingleInstance (file-association
// hand-off), Global\PoERunMutexCrashUpload and LMDB's Global\MDB… locks (the shared shader cache) stay as they are.
// The client imports CreateMutexW (this check) and CreateMutexA / OpenMutexA (LMDB, 0x1411c0a40); all six name-taking
// variants are watched, so the names are renamed whichever path asks for them. nullifier.py waits for the "run mutex
// ->" line and fails the launch without it.
if (OPTIONS.runMutexPrefix) {
  const renamed = {};
  for (const slot of ['A', 'B']) {
    const name = OPTIONS.runMutexPrefix + 'PoERunMutex' + slot;
    const key = 'global\\poerunmutex' + slot.toLowerCase();
    renamed[key] = {name, wide: Memory.allocUtf16String(name), ansi: Memory.allocAnsiString(name)};
    keep.push(renamed[key].wide, renamed[key].ansi);
  }
  // [export, index of the name argument, wide]
  const apis = [['CreateMutexW', 2, true], ['CreateMutexExW', 1, true], ['OpenMutexW', 2, true],
    ['CreateMutexA', 2, false], ['CreateMutexExA', 1, false], ['OpenMutexA', 2, false]];
  whenLoaded('kernelbase.dll', kernelbase => {
    for (const [api, index, wide] of apis) {
      const target = kernelbase.findExportByName(api);
      if (target === null) continue;
      Interceptor.attach(target, {onEnter(args) {
        const p = args[index];
        if (p.isNull()) return;
        const name = (wide ? p.readUtf16String() : p.readAnsiString()) || '';
        const to = renamed[name.toLowerCase()];
        if (to) { args[index] = wide ? to.wide : to.ansi; log(`run mutex -> ${to.name}`); }
      }});
    }
  });
}

// The window is created and shown as usual (the engine needs it), but never activated: it cannot take the keyboard
// focus from the user's window. Its top-level windows get WS_EX_NOACTIVATE (live 2026-10-31, without it a new client's
// window took the focus at creation even with the hooks below; it also keeps them off the taskbar), ShowWindow never
// activates, and SetForegroundWindow is neutralised.
whenLoaded('user32.dll', user32 => {
  const show = {minimized: 7 /* SW_SHOWMINNOACTIVE */, hidden: 0 /* SW_HIDE */, normal: 4 /* SW_SHOWNOACTIVATE */};
  const mode = show[OPTIONS.window] ?? 4;
  Interceptor.attach(user32.getExportByName('CreateWindowExW'), {onEnter(args) {
    if (args[8].isNull()) args[0] = ptr(args[0].toUInt32() | 0x08000000);  // a top-level window: WS_EX_NOACTIVATE
  }});
  Interceptor.attach(user32.getExportByName('ShowWindow'), {onEnter(args) {
    const cmd = args[1].toInt32();
    if (cmd !== 0 && cmd !== 6 /* SW_MINIMIZE */) args[1] = ptr(mode);
  }});
  // SetForegroundWindow(NULL) fails, which the client tolerates (it does so whenever Windows refuses).
  Interceptor.attach(user32.getExportByName('SetForegroundWindow'), {onEnter(args) { args[0] = ptr(0); }});
});

// The user's mouse and keyboard stay the user's (live 2026-10-31: a new session client locked the user's mouse inside
// its window). The bridge drives the client through its own calls and packets, never through input, so a session
// client needs none of these; they become no-ops whatever the settings say:
// - ClipCursor (window procedure 0x140e67df0 / 0x140e68220, the confine_mouse_to_window option): never confines, and
//   never frees either (ClipCursor(NULL) would free the user's own client's confinement): returns success, does nothing;
// - SetCursorPos / SetPhysicalCursorPos (not imported by 3.29.3b, but cheap to close): do nothing;
// - SetCapture (41 call sites): does nothing, returns no previous window;
// - ShowCursor: does nothing, but keeps the display count the client expects (it may loop until the count is < 0);
//   SetCursor(NULL) (hiding the cursor): does nothing;
// - SetWindowsHookExW/A: global input hooks are refused (NULL, which the client tolerates). 3.29.3b installs a
//   low-level keyboard hook (WH_KEYBOARD_LL, proc 0x140e68f40, 0x140e6905c) that sees every key the user presses;
//   WH_MOUSE_LL and thread-less WH_KEYBOARD / WH_MOUSE are refused too. Thread-local hooks of other kinds pass;
// - RegisterRawInputDevices (0x14103b998): RIDEV_INPUTSINK / RIDEV_EXINPUTSINK (input while in the background),
//   RIDEV_CAPTUREMOUSE / RIDEV_NOHOTKEYS (0x200), RIDEV_APPKEYS and RIDEV_NOLEGACY are removed from every device, so
//   the client never receives, captures or swallows the user's input. Removals (RIDEV_REMOVE) pass unchanged.
// - SystemParametersInfoW/A SPI_SETFILTERKEYS (0x33), SPI_SETTOGGLEKEYS (0x35), SPI_SETSTICKYKEYS (0x3b): the client
//   changes these accessibility keys session-wide (0x140e68fb0, 0x140e68939; fWinIni 0) and restores them only when it
//   exits normally, which a client its job ends never does. They return success and change nothing; the GET calls pass;
// - ChangeDisplaySettingsExW/A, ChangeDisplaySettingsW/A: a seeded fullscreen=true must not change the user's display
//   mode: they return DISP_CHANGE_SUCCESSFUL and change nothing.
// No DirectInput: 3.29.3b imports none (no dinput8, no DirectInput8Create).
const inputFlags = flags => {
  if (flags & 0x1) return flags;  // RIDEV_REMOVE
  let out = flags & ~(0x100 | 0x200 | 0x400 | 0x1000);
  if ((out & 0x30) === 0x30) out &= ~0x30;  // RIDEV_NOLEGACY
  return out;
};
whenLoaded('user32.dll', user32 => {
  const reported = new Set();
  const once = message => { if (!reported.has(message)) { reported.add(message); log(message); } };
  // Some exports share their code (live: replacing all of them threw "already replaced this function", which ended
  // the whole agent before the renderer switch): each address is replaced once, and a failure is logged per API.
  const done = new Set();
  const replace = (api, ret, args, body) => {
    try {
      const target = user32.findExportByName(api);
      if (target === null || done.has(target.toString())) return;
      done.add(target.toString());
      const original = new NativeFunction(target, ret, args);
      const callback = new NativeCallback((...values) => body(original, ...values), ret, args);
      Interceptor.replace(target, callback);
      keep.push(callback);
    } catch (e) {
      log(`input: ${api} not replaced: ${e.message}`);
    }
  };
  replace('ClipCursor', 'int', ['pointer'], () => { once('input: ClipCursor ignored'); return 1; });
  replace('SetCursorPos', 'int', ['int', 'int'], () => { once('input: SetCursorPos ignored'); return 1; });
  replace('SetPhysicalCursorPos', 'int', ['int', 'int'], () => { once('input: SetPhysicalCursorPos ignored'); return 1; });
  replace('SetCapture', 'pointer', ['pointer'], () => { once('input: SetCapture ignored'); return ptr(0); });
  let shown = 0;
  replace('ShowCursor', 'int', ['int'], (original, show) => (shown += show ? 1 : -1));
  replace('SetCursor', 'pointer', ['pointer'], (original, cursor) => (cursor.isNull() ? ptr(0) : original(cursor)));
  for (const api of ['SetWindowsHookExW', 'SetWindowsHookExA']) {
    replace(api, 'pointer', ['int', 'pointer', 'pointer', 'uint32'], (original, id, proc, module, thread) => {
      // WH_KEYBOARD_LL 13, WH_MOUSE_LL 14: always global; WH_KEYBOARD 2, WH_MOUSE 7: global without a thread.
      if (id === 13 || id === 14 || ((id === 2 || id === 7) && thread === 0)) {
        once(`input: ${api}(${id}) refused`);
        return ptr(0);
      }
      return original(id, proc, module, thread);
    });
  }
  for (const api of ['SystemParametersInfoW', 'SystemParametersInfoA']) {
    replace(api, 'int', ['uint32', 'uint32', 'pointer', 'uint32'], (original, action, value, param, winIni) => {
      if (action === 0x33 || action === 0x35 || action === 0x3b) {
        once(`input: ${api}(0x${action.toString(16)}) ignored`);
        return 1;
      }
      return original(action, value, param, winIni);
    });
  }
  for (const [api, args] of [['ChangeDisplaySettingsExW', ['pointer', 'pointer', 'pointer', 'uint32', 'pointer']],
    ['ChangeDisplaySettingsExA', ['pointer', 'pointer', 'pointer', 'uint32', 'pointer']],
    ['ChangeDisplaySettingsW', ['pointer', 'uint32']], ['ChangeDisplaySettingsA', ['pointer', 'uint32']]]) {
    replace(api, 'int', args, () => { once(`input: ${api} ignored`); return 0; });
  }
  const raw = user32.findExportByName('RegisterRawInputDevices');
  if (raw !== null) {
    Interceptor.attach(raw, {onEnter(args) {
      const count = args[1].toUInt32(), size = args[2].toUInt32();
      if (args[0].isNull() || size < 16) return;
      for (let i = 0; i < count; i++) {
        const device = args[0].add(i * size), flags = device.add(4).readU32(), out = inputFlags(flags);
        if (out !== flags) device.add(4).writeU32(out);
        once(`input: raw input usage ${device.readU16()}/${device.add(2).readU16()} ` +
          `flags 0x${flags.toString(16)} -> 0x${out.toString(16)}`);
      }
    }});
  }
});

// A minimized client runs no frames: its window procedure (0x140e67380 → 0x140e68220) logs "[WINDOW] Minimized" on
// WM_SIZE / SIZE_MINIMIZED and the main loop then waits (live 2026-10-31: a Null client someone minimized stopped
// answering the bridge, and a DirectX 11 client started minimized never reached the login screen). An agent client
// must keep running whoever minimizes its window, so that message becomes WM_NULL (0) before the engine sees it; the
// window itself still minimizes. The engine keeps its last size and runs at its background frame rate.
{
  const windowProc = checked('windowProc', '0x140e67380', '4883ec38488bc1488b0d926376034885');
  let reported = false;
  Interceptor.attach(windowProc, {onEnter(args) {
    if (args[1].toInt32() === 5 /* WM_SIZE */ && args[2].toInt32() === 1 /* SIZE_MINIMIZED */) {
      args[1] = ptr(0);
      if (!reported) { reported = true; log('window minimized: the engine is not told'); }
    }
  }});
}

// Engine::Init(engine, options) (0x1410048f0) copies the options' switches into the engine, renderer and texture
// manager and logs each ("[RENDER] Render: ON", "[TEXTURE] Upload: ON", ...). Options offsets, from that function:
// +0x2d5 Render (passed to the renderer's init 0x140faf1e0 as its 4th argument; OFF makes the device start
// 0x140fadb20 select device type 6, the Null device, whatever device_type the settings name), +0x2bf texture
// Upload, +0x2bc texture Streaming (also the renderer's Async), +0x306 Test Disable Frame Render Jobs.
if (OPTIONS.nullRenderer || OPTIONS.noRenderJobs || OPTIONS.noTextureUpload || OPTIONS.noTextureStreaming) {
  const engineInit = checked('engineInit', '0x1410048f0', '488954241048894c2408555356574154');
  Interceptor.attach(engineInit, {onEnter(args) {
    const options = args[1], set = [];
    const write = (offset, value, name) => { options.add(offset).writeU8(value); set.push(`${name}=${value}`); };
    if (OPTIONS.nullRenderer) write(0x2d5, 0, 'render');
    if (OPTIONS.noRenderJobs) write(0x306, 1, 'disableFrameRenderJobs');
    if (OPTIONS.noTextureUpload) write(0x2bf, 0, 'textureUpload');
    if (OPTIONS.noTextureStreaming) write(0x2bc, 0, 'textureStreaming');
    log(`engine options: ${set.join(', ')}`);
  }});
}

// The Null device's index buffer (vtable 0x14320e140, built in 0x140e939b0) keeps a zero-filled byte vector of the
// buffer's size at +0x130 (Map, slot 2, returns its start) and the index kind at +0x148 (1: 16-bit, 2: 32-bit, as the
// DirectX 12 buffer keeps it at +0x318), but its slot 4, "describe" (out: u32 format 0x65 = 16-bit / 0x66 = 32-bit,
// u32), is the shared no-op `ret`. Callers then read an uninitialised format: the mesh ray test 0x142be7270 took a
// 16-bit buffer for a 32-bit one, read past its end and computed vertex pointers gigabytes away (the crash at
// 0x142be6be4 on entering any area). Slot 4 of that vtable gets the DirectX 12 buffer's answer, from the Null
// buffer's own fields; the second u32 (the DirectX 12 buffer's +0x228) is given the index count.
if (OPTIONS.nullRenderer) {
  const vtable = at('0x14320e140');
  const map = checked('nullBufferMap', '0x140ee81b0', '488b8130010000498901c3');
  if (!vtable.add(0x10).readPointer().equals(map) || !vtable.add(0x20).readPointer().equals(at('0x1400f0f00'))) {
    throw new Error('Unsupported client: Null index buffer vtable differs from 3.29.3b');
  }
  const describe = Memory.alloc(Process.pageSize);
  keep.push(describe);
  {
    const w = new X86Writer(describe);
    // eax = kind; r8 = byte size (end - begin); format 0x65 with size/2 indices, 0x66 with size/4.
    w.putMovRegRegOffsetPtr('eax', 'rcx', 0x148);
    w.putMovRegRegOffsetPtr('r8', 'rcx', 0x138);
    w.putMovRegRegOffsetPtr('r9', 'rcx', 0x130);
    w.putSubRegReg('r8', 'r9');
    w.putCmpRegI32('eax', 1);
    w.putJccShortLabel('jne', 'wide', 'no-hint');
    w.putMovRegU32('eax', 0x65);
    w.putShrRegU8('r8', 1);
    w.putJmpShortLabel('store');
    w.putLabel('wide');
    w.putCmpRegI32('eax', 2);
    w.putJccShortLabel('jne', 'done', 'no-hint');
    w.putMovRegU32('eax', 0x66);
    w.putShrRegU8('r8', 2);
    w.putLabel('store');
    w.putMovRegOffsetPtrReg('rdx', 0, 'eax');
    w.putMovRegOffsetPtrReg('rdx', 4, 'r8d');
    w.putLabel('done');
    w.putRet();
    w.flush();
  }
  Memory.protect(describe, Process.pageSize, 'r-x');
  const protection = Process.findRangeByAddress(vtable.add(0x20)).protection;
  Memory.protect(vtable.add(0x20), 8, 'rw-');
  vtable.add(0x20).writePointer(describe);
  Memory.protect(vtable.add(0x20), 8, protection);
  log('Null index buffers describe their format');
}

// Null buffers' CPU copies, committed on first touch (lazyNullBuffersKb, with the Null renderer only). Each Null device
// buffer (the index buffer 0x14320e140 and the buffers 0x143212bc0 / 0x143212be0 / 0x143212c00 / 0x14369c088, built in
// 0x140e939b0, 0x142bb4730, 0x142bb4a00, 0x142bb4cd0 and 0x142bb5110) keeps a byte vector of its full size, zero-filled
// by vector<u8>::resize (0x140898aa0) right after the object is made; Map returns it, and nothing else of the class
// touches it until its destructor (0x140ee8150, 0x140f74000, 0x140f740c0, 0x140f74060, 0x142bbb6b0: the vector at
// +0x130, +0x130, +0x140, +0x130, +0x1a0) frees it. Most are never written: the GPU particle ring buffers alone
// ("Particle bone buffer", "Particle instance buffer", "Particle Index Buffer") are tens of MB each, all zero.
// With the option (all native code, compiled by Frida's CModule; no JavaScript runs per call):
// - the resize is filtered by its return address: only the five construction calls, and only a vector that was empty
//   before the resize and is at least lazyNullBuffersKb KiB, is recorded (whole pages, keyed to the vector's address)
//   and then decommitted;
// - a vectored exception handler handles only read or write faults inside a recorded range: a reserved page there is
//   committed (zero-filled, what the zero fill had left) and the access continues; a page another thread committed
//   meanwhile also continues; anything else goes on to the next handler;
// - the destructors commit the vector's range again and drop it before the vector is freed, so the allocator gets back
//   exactly the memory it handed out (committed), and no range outlives its buffer.
// Every access during the buffer's life sees what it saw before; commit is charged only for touched pages. (A system
// call reading into or writing from an untouched page of such a buffer would fail instead of faulting; nothing passes
// a Null buffer's memory to the system: only Map exposes it, for the engine's own copies and the mesh ray test.)
if (OPTIONS.nullRenderer && OPTIONS.lazyNullBuffersKb > 0) {
  const resize = checked('vectorResize', '0x140898aa0', '48895c24084889742410574883ec2048');
  const siteVas = ['0x140e93cc1', '0x142bb496a', '0x142bb4c34', '0x142bb4f58', '0x142bb547b'];
  for (const va of siteVas) checked(`resize call before ${va}`, ptr(va).sub(5), 'e8');
  const destructors = [['0x140ee8150', '48895c2408574883ec20488d05df5f32', 0x130],
    ['0x140f74000', '48895c2408574883ec20488bf98bda48', 0x130],
    ['0x140f740c0', '48895c2408574883ec20488bf98bda48', 0x140],
    ['0x140f74060', '48895c2408574883ec20488bf98bda48', 0x130],
    ['0x142bbb6b0', '48895c2408574883ec20488d05c709ae', 0x1a0]];
  const MAX = 1 << 16;
  const ranges = Memory.alloc(MAX * 24), sites = Memory.alloc(8 * siteVas.length), settings = Memory.alloc(16);
  const stats = Memory.alloc(8 * 8), lock = Memory.alloc(8);
  siteVas.forEach((va, i) => sites.add(8 * i).writePointer(at(va)));
  settings.writeU64(OPTIONS.lazyNullBuffersKb * 1024);
  settings.add(8).writeU32(MAX);
  keep.push(ranges, sites, settings, stats, lock);
  const kernel32 = Process.getModuleByName('kernel32.dll');
  const lazy = new CModule(`
    #include <gum/guminterceptor.h>
    typedef unsigned long long u64;
    typedef struct { u64 lo, hi, vector; } Range;
    typedef struct Record { unsigned int code; unsigned int flags; struct Record *next; void *address;
                            unsigned int count; u64 info[15]; } Record;
    typedef struct { Record *record; void *context; } Pointers;
    typedef struct { u64 base, allocation_base; unsigned int allocation_protect; unsigned short partition, pad1;
                     u64 size; unsigned int state, protect, type, pad2; } Region;
    extern Range ranges[];
    extern void *sites[];
    extern u64 settings[];        /* [0] minimum bytes, [1] low u32: capacity */
    extern volatile u64 stats[];  /* live ranges, live bytes, decommitted total, pages committed again, dropped */
    extern volatile long lock[];
    extern void *VirtualAlloc (void *, u64, unsigned int, unsigned int);
    extern int VirtualFree (void *, u64, unsigned int);
    extern u64 VirtualQuery (const void *, Region *, u64);
    #define SITES ${siteVas.length}

    static void acquire (void) {
      long v;
      do { v = 1; __asm__ __volatile__ ("xchgl %0, %1" : "+r" (v), "+m" (lock[0]) : : "memory"); } while (v != 0);
    }
    static void release (void) { __asm__ __volatile__ ("" : : : "memory"); lock[0] = 0; }
    /* index of the first range with lo > a (ranges sorted by lo, never overlapping) */
    static long upper (u64 a) {
      long l = 0, h = (long) stats[0];
      while (l < h) { long m = (l + h) / 2; if (ranges[m].lo <= a) l = m + 1; else h = m; }
      return l;
    }
    static int insert (u64 lo, u64 hi, u64 vector) {
      int ok = 0;
      acquire ();
      long n = (long) stats[0];
      if (n < (long) (unsigned int) settings[1]) {
        long i = upper (lo);
        for (long j = n; j > i; j--) ranges[j] = ranges[j - 1];
        ranges[i].lo = lo; ranges[i].hi = hi; ranges[i].vector = vector;
        stats[0] = n + 1; stats[1] += hi - lo;
        ok = 1;
      }
      release ();
      return ok;
    }
    /* the range recorded for this vector (by its begin), committed again and dropped */
    static void drop (u64 *vector) {
      u64 lo = (vector[0] + 0xfff) & ~0xfffULL, hi = 0;
      acquire ();
      long i = upper (lo) - 1;
      if (i >= 0 && ranges[i].lo == lo && ranges[i].vector == (u64) vector) hi = ranges[i].hi;
      release ();
      if (hi == 0) return;
      /* MEM_COMMIT, PAGE_READWRITE: the allocator gets back what it handed out */
      VirtualAlloc ((void *) lo, hi - lo, 0x1000, 0x04);
      acquire ();
      i = upper (lo) - 1;
      if (i >= 0 && ranges[i].lo == lo && ranges[i].vector == (u64) vector) {
        long n = (long) stats[0];
        for (long j = i; j < n - 1; j++) ranges[j] = ranges[j + 1];
        stats[0] = n - 1; stats[1] -= hi - lo; stats[4]++;
      }
      release ();
    }

    typedef struct { u64 *vector; } State;
    void on_resize_enter (GumInvocationContext *ic) {
      State *s = GUM_IC_GET_INVOCATION_DATA (ic, State);
      void *from = gum_invocation_context_get_return_address (ic);
      s->vector = 0;
      for (int i = 0; i < SITES; i++) {
        if (from == sites[i]) {
          u64 *v = (u64 *) gum_invocation_context_get_nth_argument (ic, 0);
          if (v[0] == v[1]) s->vector = v;  /* empty before: the whole vector is the zero fill */
          return;
        }
      }
    }
    void on_resize_leave (GumInvocationContext *ic) {
      State *s = GUM_IC_GET_INVOCATION_DATA (ic, State);
      u64 *v = s->vector;
      if (v == 0 || v[1] - v[0] < settings[0]) return;
      u64 lo = (v[0] + 0xfff) & ~0xfffULL, hi = v[1] & ~0xfffULL;
      if (hi <= lo || !insert (lo, hi, (u64) v)) return;  /* recorded before its pages go */
      if (VirtualFree ((void *) lo, hi - lo, 0x4000)) stats[2] += hi - lo;  /* MEM_DECOMMIT */
      else drop (v);
    }
    void on_destroy_130 (GumInvocationContext *ic) {
      drop ((u64 *) ((char *) gum_invocation_context_get_nth_argument (ic, 0) + 0x130));
    }
    void on_destroy_140 (GumInvocationContext *ic) {
      drop ((u64 *) ((char *) gum_invocation_context_get_nth_argument (ic, 0) + 0x140));
    }
    void on_destroy_1a0 (GumInvocationContext *ic) {
      drop ((u64 *) ((char *) gum_invocation_context_get_nth_argument (ic, 0) + 0x1a0));
    }

    long handle (Pointers *e) {
      Record *r = e->record;
      if (r->code != 0xC0000005u || r->count < 2 || r->info[0] > 1) return 0;  /* read or write faults only */
      u64 a = r->info[1];
      acquire ();
      long i = upper (a) - 1;
      int inside = i >= 0 && a < ranges[i].hi;
      release ();
      if (!inside) return 0;  /* EXCEPTION_CONTINUE_SEARCH */
      Region region;
      void *page = (void *) (a & ~0xfffULL);
      if (VirtualQuery (page, &region, sizeof region) != sizeof region) return 0;
      if (region.state == 0x2000) {  /* MEM_RESERVE: the decommitted zero fill */
        if (VirtualAlloc (page, 0x1000, 0x1000, 0x04) == 0) return 0;
        stats[3]++;
        return -1;  /* EXCEPTION_CONTINUE_EXECUTION */
      }
      if (region.state == 0x1000 && region.protect == 0x04) return -1;  /* another thread committed it meanwhile */
      return 0;
    }`, {ranges, sites, settings, stats, lock, VirtualAlloc: kernel32.getExportByName('VirtualAlloc'),
    VirtualFree: kernel32.getExportByName('VirtualFree'), VirtualQuery: kernel32.getExportByName('VirtualQuery')});
  keep.push(lazy);
  const addHandler = new NativeFunction(kernel32.getExportByName('AddVectoredExceptionHandler'), 'pointer',
    ['uint32', 'pointer']);
  if (addHandler(1, lazy.handle).isNull()) throw new Error('AddVectoredExceptionHandler failed');
  for (const [va, code, offset] of destructors) {
    Interceptor.attach(checked(`Null buffer destructor ${va}`, va, code),
      {onEnter: lazy[`on_destroy_${offset.toString(16)}`]});
  }
  Interceptor.attach(resize, {onEnter: lazy.on_resize_enter, onLeave: lazy.on_resize_leave});
  const mb = i => (stats.add(8 * i).readU64().toNumber() / 1048576).toFixed(1);
  setInterval(() => log(`lazy Null buffers: ${stats.readU64()} live (${mb(1)} MB uncommitted at most), ${mb(2)} MB ` +
    `decommitted in all, ${(stats.add(24).readU64().toNumber() * 4 / 1024).toFixed(1)} MB touched again, ` +
    `${stats.add(32).readU64()} dropped with their buffer`), 60000);
  log(`lazy Null buffers: vectors of ${OPTIONS.lazyNullBuffersKb} KiB or more`);
}

// The state-stack update (0x141526c40), on the main thread once per frame: with frameSleepMs, a per-frame sleep. Its
// entry, not client-control.js's point 0x7f bytes in (0x141526cbf), so the bridge's code check there still sees the
// original. (The frame rate itself: client_control.py game:frames.)
if (OPTIONS.frameSleepMs > 0) {
  const frame = checked('stateStackUpdate', '0x141526c40', '48895c241048896c2418488974242057');
  let sleep = null;
  Interceptor.attach(frame, function () {
    sleep ??= new NativeFunction(Process.getModuleByName('kernel32.dll').getExportByName('Sleep'), 'void', ['uint32']);
    sleep(OPTIONS.frameSleepMs);
  });
}

if (OPTIONS.traceFiles) {
  const trace = new RegExp(OPTIONS.traceFiles, 'i');
  whenLoaded('kernelbase.dll', kernelbase => {
    Interceptor.attach(kernelbase.getExportByName('CreateFileW'), {onEnter(args) {
      if (!args[0].isNull()) { const path = args[0].readUtf16String(); if (trace.test(path)) log(`open ${path}`); }
    }});
  });
}

// Bink movies (bink2w64.dll, delay-loaded; 23 imports, BinkOpen ... BinkClose): every open with its name and flags,
// and at its close the frames decoded (BinkDoFrameAsyncMulti / BinkNextFrame calls) and how long it was open.
if (OPTIONS.traceMovies) {
  whenLoaded('bink2w64.dll', bink => {
    const open = new Map();
    const name = p => {
      try { const a = p.readAnsiString(); return a && a.length > 1 ? a : p.readUtf16String(); } catch (_) { return `${p}`; }
    };
    Interceptor.attach(bink.getExportByName('BinkOpen'), {
      // The engine appends its file-callback handle (decimal) to the name.
      onEnter(args) { this.name = name(args[0]).replace(/(\.bk2)\d+$/i, '$1'); this.flags = args[1].toUInt32(); },
      onLeave(handle) {
        if (!handle.isNull()) open.set(handle.toString(), {name: this.name, since: Date.now(), frames: 0});
        log(`movie open ${this.name} flags 0x${this.flags.toString(16)} -> ${handle.isNull() ? 'failed' : 'ok'}`);
      }});
    for (const api of ['BinkDoFrameAsyncMulti', 'BinkNextFrame']) {
      Interceptor.attach(bink.getExportByName(api), {onEnter(args) {
        const movie = open.get(args[0].toString());
        if (movie) movie.frames++;
      }});
    }
    Interceptor.attach(bink.getExportByName('BinkClose'), {onEnter(args) {
      const movie = open.get(args[0].toString());
      open.delete(args[0].toString());
      if (movie) log(`movie close ${movie.name} after ${Date.now() - movie.since} ms, ${movie.frames} frame calls`);
    }});
    log('movie trace on');
  });
}

// A crash is written to OPTIONS.crashLog synchronously, with registers and image-relative return addresses, before
// the process dies as it would anyway.
if (OPTIONS.crashLog) {
  Process.setExceptionHandler(details => {
    // 'system' covers the C++ exceptions (0xe06d7363) the client throws and catches itself.
    if (details.type === 'system') return false;
    try {
      const rva = p => { const m = Process.findModuleByAddress(p); return m ? `${m.name}+0x${p.sub(m.base).toString(16)}` : `${p}`; };
      const c = details.context;
      const regs = ['rax', 'rbx', 'rcx', 'rdx', 'rsi', 'rdi', 'rbp', 'rsp', 'r8', 'r9', 'r12', 'r13', 'r14', 'r15']
        .map(r => `${r}=${c[r]}`).join(' ');
      const stack = Thread.backtrace(c, Backtracer.FUZZY).slice(0, 24).map(rva).join('\n  ');
      const file = new File(OPTIONS.crashLog, 'a');
      file.write(`${new Date().toISOString()} pid ${Process.id} thread ${Process.getCurrentThreadId()} ${details.type}` +
        ` at ${rva(details.address)} (${details.memory ? details.memory.operation + ' ' + details.memory.address : ''})` +
        `\n  ${regs}\n  ${stack}\n`);
      file.close();
    } catch (_) {}
    return false;
  });
}

// The client's allocator is mimalloc 2.2 (built Aug 15 2025, linked in; the engine's aligned allocation 0x142c839f0 is
// mi_heap_malloc_aligned). It reads each option from MIMALLOC_<NAME> the first time it needs it. Its option table
// (0x143a5ab90, 37 entries of 0x20 bytes: i32 value, i32 initialised, i32 option, pad, char *name, char *legacy name)
// is read 20 s after the start, when the allocator has read them, and the asked-for options are logged.
if (OPTIONS.mimalloc && Object.keys(OPTIONS.mimalloc).length) {
  const table = at('0x143a5ab90');
  if (table.add(0x10).readPointer().readCString() !== 'show_errors') {
    throw new Error('Unsupported client: mimalloc option table differs from 3.29.3b');
  }
  setTimeout(() => {
    const seen = {};
    for (let i = 0; i < 37; i++) {
      const entry = table.add(i * 0x20);
      const name = entry.add(0x10).readPointer().readCString();
      if (name in OPTIONS.mimalloc) seen[name] = entry.add(4).readS32() ? entry.readS32() : 'not read yet';
    }
    log(`mimalloc options: ${JSON.stringify(seen)}`);
  }, 20000);
}

rpc.exports.ready = () => ({pid: Process.id, options: OPTIONS});
