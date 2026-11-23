// Find the overlay's text in the client's memory, across every readable range (including read-only file mappings).
// Reports each hit with its range, so the code or structure that shows the "monsters remain" line can be located.
'use strict';
const NEEDLE = 'More than 50 monsters remain';
const u16 = s => Array.from(s).map(c => c.charCodeAt(0).toString(16).padStart(2, '0') + ' 00').join(' ');
const pattern = u16(NEEDLE);
const ranges = Process.enumerateRanges('r').filter(function (r) { return r.size > 0 && r.size < 0x80000000; });
send({ needle: NEEDLE, ranges: ranges.length });

let done = 0;
let hits = 0;

function scanRange(i) {
  if (i >= ranges.length) { send({ done: true, scanned: done, hits: hits }); return; }
  const r = ranges[i];
  let finished = false;
  Memory.scan(r.base, r.size, pattern, {
    onMatch: function (addr) {
      hits++;
      const text = (function () { try { return addr.readUtf16String(48); } catch (e) { return null; } })();
      send({ hit: addr.toString(), range: r.base.toString(), size: r.size,
             protection: r.protection, file: r.file ? r.file.path : null, text: text });
    },
    onError: function () {},
    onComplete: function () {
      if (finished) return;
      finished = true;
      done++;
      if (done % 100 === 0) send({ progress: done, of: ranges.length, hits: hits });
      setTimeout(function () { scanRange(i + 1); }, 0);
    }
  });
}
scanRange(0);
