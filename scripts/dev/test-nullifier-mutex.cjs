// Offline check of nullifier.js's two-copies mutex renaming, with Frida's API stubbed: only Global\PoERunMutexA/B
// become session-local names; every other mutex name (single-instance hand-off, crash upload, LMDB) is left alone.
//   node scripts/dev/test-nullifier-mutex.cjs
'use strict';
const fs = require('fs');
const path = require('path');
const assert = require('assert');

const text = s => ({isNull: () => s === null, readUtf16String: () => s, toString: () => `str:${s}`, value: s});
const hooks = {};
const sandbox = {
  OPTIONS: {runMutexPrefix: 'Local\\LocalExile-pr-x-bot1-', window: 'normal'},
  ptr: v => ({value: v, toInt32: () => Number(v), isNull: () => !v, add: () => sandbox.ptr(0), sub: () => sandbox.ptr(0)}),
  send: () => {},
  rpc: {exports: {}},
  Memory: {allocUtf16String: text},
  Interceptor: {attach: (target, callbacks) => { hooks[target] = callbacks; }},
  Process: {
    getModuleByName: () => ({base: sandbox.ptr(0x140000000)}),
    findModuleByName: name => ({name, getExportByName: api => `${name}!${api}`}),
    attachModuleObserver: () => ({}),
  },
};
const source = fs.readFileSync(path.join(__dirname, 'nullifier.js'), 'utf8');
new Function(...Object.keys(sandbox), source)(...Object.values(sandbox));

function call(api, index, name, count = 3) {
  const args = Array.from({length: count}, () => text(null));
  args[index] = text(name);
  hooks[`kernelbase.dll!${api}`].onEnter(args);
  return args[index].value;
}
assert.strictEqual(call('CreateMutexW', 2, 'Global\\PoERunMutexA'), 'Local\\LocalExile-pr-x-bot1-PoERunMutexA');
assert.strictEqual(call('CreateMutexW', 2, 'Global\\PoERunMutexB'), 'Local\\LocalExile-pr-x-bot1-PoERunMutexB');
assert.strictEqual(call('CreateMutexExW', 1, 'global\\poerunmutexa', 4), 'Local\\LocalExile-pr-x-bot1-PoERunMutexA');
assert.strictEqual(call('OpenMutexW', 2, 'Global\\PoERunMutexB'), 'Local\\LocalExile-pr-x-bot1-PoERunMutexB');
for (const other of ['PathOfExileSingleInstance', 'Global\\PoERunMutexCrashUpload', 'Global\\MDB12345', 'Global\\PoERunMutexAB']) {
  assert.strictEqual(call('CreateMutexW', 2, other), other);
}
assert.strictEqual(hooks['kernelbase.dll!CreateMutexW'].onEnter([text(null), text(null), text(null)]), undefined);
console.log('ok');
