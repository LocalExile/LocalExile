// The Labyrinth's day layout as the client builds it (0x1424ec930 → 0x1424e9d80), for comparing the server's port
// (LabyrinthLayout.cs). Load after common.js; the rpc export `layout(seed, difficulty)` builds one in the client and
// returns its nodes: the LabyrinthSection Id and the layout row's index in it (+0x10), the connections (node indexes,
// -1 for an exit), the two secrets' Ids and numbers, and the last-node-of-section mark.
//   python scripts/re/frida/run.py Game.patched.exe scripts/re/frida/common.js scripts/re/frida/dump-lab-layout.js
// then call it from a REPL, or use scripts/re/frida/lab-layout.py.
const build = new NativeFunction(at('0x1424ec930'), 'pointer', ['pointer', 'uint32', 'uint8', 'uint32']);

function wide(p) {
  try { return p.isNull() ? null : p.readUtf16String(); } catch (e) { return null; }
}

// A row reference in a loaded table is (row pointer, table pointer); a row's first string column is a wchar pointer.
const idOf = row => row.isNull() ? null : wide(row.readPointer());

rpc.exports = {
  layout(seed, difficulty) {
    const obj = Memory.alloc(0x30);
    build(obj, seed >>> 0, difficulty, 0xffffffff);
    const first = obj.readPointer(), end = obj.add(8).readPointer();
    const count = end.sub(first).toInt32() / 0x60;
    const index = p => p.isNull() ? -1 : p.sub(first).toInt32() / 0x60;
    const nodes = [];
    for (let i = 0; i < count; i++) {
      const n = first.add(i * 0x60);
      const row = n.add(0x28).readPointer();
      const links = [];
      for (let k = 0; k < n.add(0x5b).readU8(); k++) links.push(index(n.add(k * 8).readPointer()));
      nodes.push({
        section: idOf(row.readPointer()),
        col1: row.add(0x10).readS32(),
        links,
        secret1: idOf(n.add(0x38).readPointer()), number1: n.add(0x58).readU8(),
        secret2: idOf(n.add(0x48).readPointer()), number2: n.add(0x59).readU8(),
        sectionEnd: n.add(0x5a).readU8(),
      });
    }
    return nodes;
  },
};
