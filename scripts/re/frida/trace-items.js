// Items: every item the client reads (0x1421f2d00: type hash, reader position before/after) and each item component's
// read (vtable slot 5) with the bytes it consumed. Load after common.js. Used to check the item layouts of
// Agents/docs/protocol/05-inventories-and-ui.md §6.
const ITEM_READERS = {
  '0x1421ed330': 'Base', '0x1421e6050': 'Mods', '0x1421e9790': 'Sockets', '0x1424b5720': 'Quality',
  '0x1424a3f80': 'Charges', '0x1423fb5f0': 'Stack', '0x1424755d0': 'SkillGem',
};
Interceptor.attach(at('0x1421f2d00'), {
  onEnter(a) { this.r = a[1]; this.p = pos(a[1]); try { this.h = a[1].readPointer().add(a[1].add(0x10).readU64().toNumber()).readU32(); } catch (e) {} },
  onLeave(rv) { send({item: '0x' + (this.h >>> 0).toString(16), from: this.p, to: pos(this.r), ok: !this.r.isNull()}); },
});
for (const [addr, name] of Object.entries(ITEM_READERS))
  Interceptor.attach(at(addr), {
    onEnter(a) { this.r = a[1]; this.p = pos(a[1]); },
    onLeave() { send({component: name, from: this.p, to: pos(this.r)}); },
  });
Interceptor.attach(at('0x1421fb650'), {   // an inventory object (0xc0)
  onEnter(a) { this.r = a[1]; },
  onLeave(rv) { try { send({inventory: true, result: rv.readPointer().isNull() ? 'null' : 'ok'}); } catch (e) {} },
});
send('trace-items loaded');
