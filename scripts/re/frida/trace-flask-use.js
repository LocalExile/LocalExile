// Read-only 3.29.3b probe. Load after common.js, then use a life/mana flask normally.
// Confirms 0x263 -> native item-use callback -> BaseItemTypes use sound -> audio submission.
Interceptor.attach(at('0x1421aded0'), {
  onEnter(args) { this.message = args[0]; },
  onLeave() {
    const p = this.message;
    send({event: 'item-used', typeHash: p.add(0x10).readU32(), inventoryId: p.add(0x14).readU32(),
      itemId: p.add(0x18).readU32(), result: p.add(0x40).readU16()});
  }
});
for (const callback of ['0x140d95a70', '0x140ddcd60']) {
  Interceptor.attach(at(callback), {
    onEnter(args) {
      send({event: 'item-use-ui', callback, inventoryId: args[1].toUInt32(), itemId: args[2].toUInt32(),
        typeHash: args[3].toUInt32(), result: args[4].toUInt32() & 0xffff});
    }
  });
}
for (const site of ['0x140d95f9c', '0x140ddd2a8']) {
  Interceptor.attach(at(site), function () {
    send({event: 'item-use-sound', path: this.context.rdx.readUtf16String()});
  });
}
Interceptor.attach(at('0x140ff6320'), {
  onEnter() {
    if (['0x140d96003', '0x140ddd310'].some(site => this.returnAddress.equals(at(site))))
      send({event: 'item-use-audio-submitted'});
  }
});
