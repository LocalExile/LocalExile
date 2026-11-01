// Offline regression for the actual outgoing login hook. No Frida, game process, account or credentials.
'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, 'client-control.js'), 'utf8');
const start = 'Interceptor.attach(addresses.loginWriter, {onEnter(args) {';
assert.equal(source.split(start).length, 2);
const body = source.split(start)[1].split('\n}});')[0];

function fixture(pending) {
  const bytes = Buffer.alloc(0x98, 0xa5);
  bytes[0x94] = bytes[0x95] = 1;
  let writes = 0, account = null;
  const pointer = offset => ({
    add: value => pointer(offset + value),
    writeByteArray: values => {writes++; Buffer.from(values).copy(bytes, offset);},
    writeU8: value => {writes++; bytes[offset] = value;},
    offset
  });
  const context = {
    credentials: pending, loginSubmitted: false,
    Memory: {allocUtf8String: value => value},
    assignText: (target, value, length) => {account = {offset: target.offset, value, length};},
    unescape, encodeURIComponent
  };
  vm.createContext(context);
  const invoke = () => {context.args = [pointer(0)]; vm.runInContext('(function () {' + body + '\n})();', context);};
  return {bytes, context, invoke, writes: () => writes, account: () => account};
}

// A pending explicit-password request can replace an automatic remembered-form submission.
const digest = Array.from({length: 32}, (_, i) => i + 1);
const explicit = fixture({account: 'synthetic_\u00e9', digest: [...digest]});
const untouched = Buffer.from(explicit.bytes);
explicit.invoke();
assert.deepEqual([...explicit.bytes.subarray(0x34, 0x54)], digest);
assert.equal(explicit.bytes[0x95], 0); // Password digest must never be verified as a saved token.
assert.equal(explicit.bytes[0x94], 1); // Keep the user's Remember Password preference.
for (let offset = 0; offset < untouched.length; offset++) {
  if (offset >= 0x34 && offset < 0x54 || offset === 0x95) continue;
  assert.equal(explicit.bytes[offset], untouched[offset]);
}
assert.equal(explicit.account().value, 'synthetic_\u00e9');
assert.equal(explicit.account().length, Buffer.byteLength('synthetic_\u00e9', 'utf8'));
assert.equal(explicit.context.credentials, null);
assert.equal(explicit.context.loginSubmitted, true);
const after = Buffer.from(explicit.bytes);
explicit.invoke(); // Only the one pending password submission is changed.
assert.deepEqual(explicit.bytes, after);
assert.equal(explicit.writes(), 2);

// Ordinary native saved login has no pending explicit credentials and must be untouched.
const saved = fixture(null), original = Buffer.from(saved.bytes);
saved.invoke();
assert.deepEqual(saved.bytes, original);
assert.equal(saved.writes(), 0);
assert.equal(saved.context.loginSubmitted, false);
console.log('Password replacement and unchanged saved-login controls passed.');
