// Offline check of nullifier.js's always-on hooks, with Frida's API stubbed:
// - the two-copies mutexes: only Global\PoERunMutexA/B become session-local names, through the wide and the ANSI
//   variants; every other mutex name (single-instance hand-off, crash upload, LMDB) is left alone, and each rename is
//   reported ("run mutex -> ", which nullifier.py waits for);
// - top-level windows are created with WS_EX_NOACTIVATE;
// - the engine is never told its window was minimized;
// - the user's mouse and keyboard: no cursor confinement, positioning, capture or hiding, no global input hooks, and
//   raw-input devices without background/capture flags.
//   node scripts/dev/test-nullifier-mutex.cjs
'use strict';
const fs = require('fs');
const path = require('path');
const assert = require('assert');

const text = (s, encoding = 'wide') => ({isNull: () => s === null, readUtf16String: () => s, readAnsiString: () => s,
  value: s, encoding});
const hooks = {};
const replaced = {};
const originals = [];
const sent = [];
// The code nullifier.js checks before it hooks (the always-on hooks only), as 3.29.3b has it.
const code = {0x140e67380: '4883ec38488bc1488b0d926376034885'};
const number = v => (typeof v === 'object' && v !== null ? v.value : Number(v));
const pointer = v => ({value: number(v), toInt32: () => number(v) | 0, toUInt32: () => number(v) >>> 0,
  isNull: () => !number(v), add: x => pointer(number(v) + number(x)), sub: x => pointer(number(v) - number(x)),
  readByteArray: () => Uint8Array.from(Buffer.from(code[number(v)] || '', 'hex')).buffer});
const sandbox = {
  OPTIONS: {runMutexPrefix: 'Local\\LocalExile-pr-x-bot1-', window: 'normal'},
  ptr: pointer,
  send: message => sent.push(message.message),
  rpc: {exports: {}},
  Memory: {allocUtf16String: s => text(s, 'wide'), allocAnsiString: s => text(s, 'ansi')},
  Interceptor: {attach: (target, callbacks) => {
    hooks[typeof target === 'string' ? target : `0x${target.value.toString(16)}`] = callbacks;
  }, replace: (target, callback) => { replaced[target] = callback.body; }},
  NativeFunction: function (target) { const f = (...a) => { originals.push([target, ...a]); return pointer(0x77); }; return f; },
  NativeCallback: function (body) { this.body = body; },
  Process: {
    getModuleByName: () => ({base: pointer(0x140000000)}),
    findModuleByName: name => ({name, findExportByName: api => `${name}!${api}`, getExportByName: api => `${name}!${api}`}),
    attachModuleObserver: () => ({}),
  },
};
const source = fs.readFileSync(path.join(__dirname, 'nullifier.js'), 'utf8');
new Function(...Object.keys(sandbox), source)(...Object.values(sandbox));

function call(api, index, name, count = 3) {
  const args = Array.from({length: count}, () => text(null));
  args[index] = text(name);
  hooks[`kernelbase.dll!${api}`].onEnter(args);
  return args[index];
}
const A = 'Local\\LocalExile-pr-x-bot1-PoERunMutexA', B = 'Local\\LocalExile-pr-x-bot1-PoERunMutexB';
assert.strictEqual(call('CreateMutexW', 2, 'Global\\PoERunMutexA').value, A);
assert.strictEqual(call('CreateMutexW', 2, 'Global\\PoERunMutexB').value, B);
assert.strictEqual(call('CreateMutexExW', 1, 'global\\poerunmutexa', 4).value, A);
assert.strictEqual(call('OpenMutexW', 2, 'Global\\PoERunMutexB').value, B);
const ansi = call('CreateMutexA', 2, 'Global\\PoERunMutexA');
assert.strictEqual(ansi.value, A);
assert.strictEqual(ansi.encoding, 'ansi');  // an ANSI caller gets an ANSI string
assert.strictEqual(call('CreateMutexExA', 1, 'Global\\PoERunMutexB', 4).encoding, 'ansi');
assert.strictEqual(call('OpenMutexA', 2, 'Global\\PoERunMutexA').value, A);
assert.strictEqual(call('CreateMutexW', 2, 'Global\\PoERunMutexA').encoding, 'wide');
for (const other of ['PathOfExileSingleInstance', 'Global\\PoERunMutexCrashUpload', 'Global\\MDB12345', 'Global\\PoERunMutexAB']) {
  assert.strictEqual(call('CreateMutexW', 2, other).value, other);
  assert.strictEqual(call('OpenMutexA', 2, other).value, other);
}
assert.strictEqual(hooks['kernelbase.dll!CreateMutexW'].onEnter([text(null), text(null), text(null)]), undefined);
assert.ok(sent.includes(`run mutex -> ${A}`) && sent.includes(`run mutex -> ${B}`), 'each rename is reported');
assert.ok(!sent.some(m => /PoERunMutexCrashUpload|MDB|SingleInstance/.test(m)), 'other names are not reported');

// Top-level windows never activate (WS_EX_NOACTIVATE); child windows are left alone.
const topLevel = Array.from({length: 12}, () => pointer(0));
topLevel[0] = pointer(0x100);
hooks['user32.dll!CreateWindowExW'].onEnter(topLevel);
assert.strictEqual(topLevel[0].value, 0x08000100);
const child = Array.from({length: 12}, () => pointer(0));
child[8] = pointer(0x1234);
hooks['user32.dll!CreateWindowExW'].onEnter(child);
assert.strictEqual(child[0].value, 0);
// The engine is never told its window was minimized (WM_SIZE / SIZE_MINIMIZED becomes WM_NULL); other sizes pass.
const minimized = [pointer(0x10), pointer(5), pointer(1), pointer(0)];
hooks['0x140e67380'].onEnter(minimized);
assert.strictEqual(minimized[1].value, 0);
const restored = [pointer(0x10), pointer(5), pointer(0), pointer(0x02580320)];
hooks['0x140e67380'].onEnter(restored);
assert.strictEqual(restored[1].value, 5);

// The user's mouse and keyboard stay the user's: these do nothing and never reach user32.
for (const api of ['ClipCursor', 'SetCursorPos', 'SetPhysicalCursorPos', 'SetCapture']) {
  assert.ok(replaced[`user32.dll!${api}`], `${api} replaced`);
}
assert.strictEqual(replaced['user32.dll!ClipCursor'](pointer(0x5000)), 1);
assert.strictEqual(replaced['user32.dll!ClipCursor'](pointer(0)), 1);  // not even ClipCursor(NULL): the user's own clip stays
assert.strictEqual(replaced['user32.dll!SetCursorPos'](10, 20), 1);
assert.strictEqual(replaced['user32.dll!SetCapture'](pointer(0x1234)).value, 0);
assert.strictEqual(originals.length, 0, 'none of them called the real function');
// ShowCursor keeps a display count (a loop until < 0 ends), SetCursor(NULL) hides nothing, a real cursor passes.
const showCursor = replaced['user32.dll!ShowCursor'];
assert.strictEqual(showCursor(0), -1);
assert.strictEqual(showCursor(1), 0);
assert.strictEqual(replaced['user32.dll!SetCursor'](pointer(0)).value, 0);
assert.strictEqual(originals.length, 0);
replaced['user32.dll!SetCursor'](pointer(0x99));
assert.strictEqual(originals.length, 1);
// Global input hooks are refused (the client's WH_KEYBOARD_LL among them); a thread-local hook passes.
originals.length = 0;
for (const [id, thread] of [[13, 0], [14, 0], [2, 0], [7, 0]]) {
  assert.strictEqual(replaced['user32.dll!SetWindowsHookExW'](id, pointer(1), pointer(2), thread).value, 0);
  assert.strictEqual(replaced['user32.dll!SetWindowsHookExA'](id, pointer(1), pointer(2), thread).value, 0);
}
assert.strictEqual(originals.length, 0);
assert.strictEqual(replaced['user32.dll!SetWindowsHookExW'](2, pointer(1), pointer(2), 1234).value, 0x77);
assert.strictEqual(replaced['user32.dll!SetWindowsHookExW'](4, pointer(1), pointer(2), 0).value, 0x77);
// Accessibility keys are not changed session-wide (SET of filter, toggle and sticky keys); other actions pass.
originals.length = 0;
for (const action of [0x33, 0x35, 0x3b]) {
  assert.strictEqual(replaced['user32.dll!SystemParametersInfoW'](action, 0, pointer(0x10), 0), 1);
  assert.strictEqual(replaced['user32.dll!SystemParametersInfoA'](action, 0, pointer(0x10), 0), 1);
}
assert.strictEqual(originals.length, 0);
replaced['user32.dll!SystemParametersInfoW'](0x3a, 0, pointer(0x10), 0);  // SPI_GETSTICKYKEYS
assert.strictEqual(originals.length, 1);
// The display mode is never changed.
originals.length = 0;
assert.strictEqual(replaced['user32.dll!ChangeDisplaySettingsExW'](pointer(0), pointer(0x20), pointer(0), 4, pointer(0)), 0);
assert.strictEqual(replaced['user32.dll!ChangeDisplaySettingsW'](pointer(0x20), 4), 0);
assert.strictEqual(originals.length, 0);
// Raw input: background, capture, no-hotkeys, app-keys and no-legacy flags are removed; RIDEV_REMOVE passes.
const devices = [[1, 2, 0x100 | 0x200 | 0x30], [1, 6, 0x2000 | 0x1000 | 0x400 | 0x200], [1, 4, 0x2100], [1, 2, 0x1]];
const memory = devices.map(d => d.slice());
const at = i => ({add: o => (o === 4 ? {readU32: () => memory[i][2], writeU32: v => { memory[i][2] = v; }}
  : {readU16: () => memory[i][1]}), readU16: () => memory[i][0]});
const array = {isNull: () => false, add: o => at(o / 16)};
hooks['user32.dll!RegisterRawInputDevices'].onEnter([array, pointer(devices.length), pointer(16)]);
assert.deepStrictEqual(memory.map(d => d[2]), [0, 0x2000, 0x2000, 0x1]);
assert.ok(sent.some(m => m.startsWith('input: raw input usage 1/2 flags 0x330 -> 0x0')));
console.log('ok');
