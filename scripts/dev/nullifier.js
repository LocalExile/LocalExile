// Headless ("nullified") client for agent testing: loaded by scripts/dev/nullifier.py into Client/Game.patched.exe
// while the process is still suspended at spawn, before the engine starts. See Agents/docs/dev/headless-client.md.
// nullifier.py prepends `const OPTIONS = {...};`. No input simulation and no executable patching on disk; the hooks
// live only in this process and only for its life.
//   nullRenderer  the engine's own "Render: OFF" switch: it then creates its Null device (no D3D/Vulkan device, no
//                 swap chain, no GPU memory, no texture uploads) instead of the configured DirectX/Vulkan one.
//   noRenderJobs  the engine's "Test Disable Frame Render Jobs" switch (frame render jobs are not kicked).
//   noTextureUpload, noTextureStreaming  the texture manager's Upload / Streaming switches.
//   configPath    open this file wherever the client opens its settings file (Documents\My Games\Path of
//                 Exile\poeoffline_Config.ini), so each client has its own settings and the user's file is not used.
//   frameSleepMs  sleep this long once per main-loop frame (in the state-stack update), a frame-rate cap that also
//                 works with the Null device, which has no vsync or present to wait on.
//   window        minimized | hidden | normal: how the window shows; it is never activated (no focus stealing).
//   crashLog      append a crash's registers and backtrace to this file (diagnostics).
//   traceFiles    log every CreateFileW of a path matching this regular expression (diagnostics).
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

// Engine::Init(engine, options) (0x1410048f0) copies the options' switches into the engine, renderer and texture
// manager and logs each ("[RENDER] Render: ON", "[TEXTURE] Upload: ON", ...). Options offsets, from that function:
// +0x2d5 Render (passed to the renderer's init 0x140faf1e0 as its 4th argument; OFF makes the device start
// 0x140fadb20 select device type 6, the Null device, whatever device_type the settings name), +0x2bf texture
// Upload, +0x2bc texture Streaming (also the renderer's Async), +0x306 Test Disable Frame Render Jobs.
const engineInit = checked('engineInit', '0x1410048f0', '488954241048894c2408555356574154');
Interceptor.attach(engineInit, {onEnter(args) {
  const options = args[1], set = [];
  const write = (offset, value, name) => { options.add(offset).writeU8(value); set.push(`${name}=${value}`); };
  if (OPTIONS.nullRenderer) write(0x2d5, 0, 'render');
  if (OPTIONS.noRenderJobs) write(0x306, 1, 'disableFrameRenderJobs');
  if (OPTIONS.noTextureUpload) write(0x2bf, 0, 'textureUpload');
  if (OPTIONS.noTextureStreaming) write(0x2bc, 0, 'textureStreaming');
  log(`engine options: ${set.join(', ') || 'unchanged'}`);
}});

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

whenLoaded('kernelbase.dll', kernelbase => {
  if (!OPTIONS.configPath && !OPTIONS.traceFiles) return;
  const configName = /\\poeoffline_Config\.ini$/i;
  const trace = OPTIONS.traceFiles ? new RegExp(OPTIONS.traceFiles, 'i') : null;
  const replacement = OPTIONS.configPath ? Memory.allocUtf16String(OPTIONS.configPath) : null;
  if (replacement) keep.push(replacement);
  const redirect = args => {
    if (args[0].isNull()) return;
    const path = args[0].readUtf16String();
    if (replacement && configName.test(path)) {
      args[0] = replacement;
      log(`config ${path} -> ${OPTIONS.configPath}`);
    } else if (trace && trace.test(path)) log(`open ${path}`);
  };
  // The settings file is read, written (saved on change and on exit) and checked through these.
  for (const name of ['CreateFileW', 'GetFileAttributesW', 'GetFileAttributesExW', 'DeleteFileW']) {
    Interceptor.attach(kernelbase.getExportByName(name), {onEnter: redirect});
  }
  // MoveFileExW(temporary, settings): a save written next to the file and renamed over it.
  Interceptor.attach(kernelbase.getExportByName('MoveFileExW'), {onEnter(args) {
    if (args[1].isNull() || !replacement) return;
    const path = args[1].readUtf16String();
    if (configName.test(path)) { args[1] = replacement; log(`config save ${path} -> ${OPTIONS.configPath}`); }
  }});
});

// The window is created and shown as usual (the engine needs it), but never activated: it cannot take the keyboard
// focus from the user's window. "minimized" shows it minimized, "hidden" not at all.
whenLoaded('user32.dll', user32 => {
  const show = {minimized: 7 /* SW_SHOWMINNOACTIVE */, hidden: 0 /* SW_HIDE */, normal: 4 /* SW_SHOWNOACTIVATE */};
  const mode = show[OPTIONS.window] ?? 7;
  Interceptor.attach(user32.getExportByName('ShowWindow'), {onEnter(args) {
    const cmd = args[1].toInt32();
    if (cmd !== 0 && cmd !== 6 /* SW_MINIMIZE */) args[1] = ptr(mode);
  }});
  // SetForegroundWindow(NULL) fails, which the client tolerates (it does so whenever Windows refuses).
  Interceptor.attach(user32.getExportByName('SetForegroundWindow'), {onEnter(args) { args[0] = ptr(0); }});
});

// The state-stack update (0x141526c40), on the main thread once per frame: counts frames for `stats` and, with
// frameSleepMs, caps the frame rate, which also works with the Null device (no vsync or present to wait on). Its entry,
// not client-control.js's point 0x7f bytes in (0x141526cbf), so the bridge's code check there still sees the original.
const frame = checked('stateStackUpdate', '0x141526c40', '48895c241048896c2418488974242057');
let frames = 0, sleep = null;
const started = Date.now();
Interceptor.attach(frame, function () {
  frames++;
  if (OPTIONS.frameSleepMs > 0) {
    sleep ??= new NativeFunction(Process.getModuleByName('kernel32.dll').getExportByName('Sleep'), 'void', ['uint32']);
    sleep(OPTIONS.frameSleepMs);
  }
});
let last = {time: started, frames: 0};
rpc.exports.stats = () => {
  const now = Date.now(), fps = (frames - last.frames) * 1000 / Math.max(1, now - last.time);
  last = {time: now, frames};
  return {pid: Process.id, frames, fpsSinceLastStats: Math.round(fps * 10) / 10, uptimeS: Math.round((now - started) / 1000)};
};

// A crash (with the Null renderer, a code path that expects a real device) is written to OPTIONS.crashLog
// synchronously, with registers and image-relative return addresses, before the process dies as it would anyway.
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

rpc.exports.ready = () => ({pid: Process.id, options: OPTIONS});
