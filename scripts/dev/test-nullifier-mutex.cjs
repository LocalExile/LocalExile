// Offline check of nullifier.js's two-copies mutex renaming, with Frida's API stubbed: only Global\PoERunMutexA/B
// become session-local names, through the wide and the ANSI variants; every other mutex name (single-instance
// hand-off, crash upload, LMDB) is left alone, and each rename is reported ("run mutex -> ", which nullifier.py
// waits for).
//   node scripts/dev/test-nullifier-mutex.cjs
'use strict';
const fs = require('fs');
const path = require('path');
const assert = require('assert');

const text = (s, encoding = 'wide') => ({isNull: () => s === null, readUtf16String: () => s, readAnsiString: () => s,
  value: s, encoding});
const hooks = {};
const sent = [];
const sandbox = {
  OPTIONS: {runMutexPrefix: 'Local\\LocalExile-pr-x-bot1-', window: 'normal'},
  ptr: v => ({value: v, toInt32: () => Number(v), isNull: () => !v, add: () => sandbox.ptr(0), sub: () => sandbox.ptr(0)}),
  send: message => sent.push(message.message),
  rpc: {exports: {}},
  Memory: {allocUtf16String: s => text(s, 'wide'), allocAnsiString: s => text(s, 'ansi')},
  Interceptor: {attach: (target, callbacks) => { hooks[target] = callbacks; }},
  Process: {
    getModuleByName: () => ({base: sandbox.ptr(0x140000000)}),
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
console.log('ok');
