// Logs the paths that the client's file lookup 0x1421d2bf0 fails on (it throws via 0x1421d2ebe). Load after common.js.
// The path argument (a3) is read as {wchar_t* ptr, u64 len} and, failing that, as an inline UTF-16 string.
let last = null;
function path(p) {
  try { const s = p.readPointer().readUtf16String(); if (s && s.length > 3) return s; } catch (e) {}
  try { return p.readUtf16String(); } catch (e) { return '?'; }
}
Interceptor.attach(at('0x1421d2bf0'), { onEnter(a) { last = a[2]; } });
Interceptor.attach(at('0x1421d2ebe'), function () { send({ missing: last ? path(last) : null }); });
send({ ready: 'trace-missing-files' });
