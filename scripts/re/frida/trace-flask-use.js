// Read-only 3.29.3c probe. Load after common.js, then use a life/mana flask normally.
// Confirms 0x263 -> native item-use callback -> BaseItemTypes use sound -> audio submission.
Interceptor.attach(at('0x1421b4840'), {
  onEnter(args) { this.message = args[0]; },
  onLeave() {
    const p = this.message;
    send({event: 'item-used', typeHash: p.add(0x10).readU32(), inventoryId: p.add(0x14).readU32(),
      itemId: p.add(0x18).readU32(), result: p.add(0x40).readU16()});
  }
});
for (const callback of ['0x140d931e0', '0x140dda620']) {
  Interceptor.attach(at(callback), {
    onEnter(args) {
      send({event: 'item-use-ui', callback, inventoryId: args[1].toUInt32(), itemId: args[2].toUInt32(),
        typeHash: args[3].toUInt32(), result: args[4].toUInt32() & 0xffff});
    }
  });
}
for (const site of ['0x140d9370c', '0x140ddab68']) {
  Interceptor.attach(at(site), function () {
    send({event: 'item-use-sound', path: this.context.rdx.readUtf16String()});
  });
}
Interceptor.attach(at('0x140ff86e0'), {
  onEnter() {
    if (['0x140d93773', '0x140ddabd0'].some(site => this.returnAddress.equals(at(site))))
      send({event: 'item-use-audio-submitted'});
  }
});
