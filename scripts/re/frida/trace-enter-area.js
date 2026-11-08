// Paste after common.js. Milestones of entering an area plus the usual UI prerequisites: inventory registrations
// (0x141d65c80), inventory lookups that return null (0x141d65830), passive slot creation (0x14215c540).
let inventories = 0;
Interceptor.attach(at('0x141d65c80'), { onEnter() { inventories++; } });
Interceptor.attach(at('0x14215c540'), { onEnter(a) { send({passiveSlot: a[1].toInt32()}); } });
for (const [a, name] of [['0x14218d910','SetLocalPlayerId'],['0x14215d6d0','EnterArea'],['0x142189fe0','WorldActivate'],
                         ['0x141ec4a40','CreateObject'],['0x14078a490','UiBuild785160']]) {
  Interceptor.attach(at(a), { onEnter() { send({enter: name, inventories}); }, onLeave() { send({leave: name}); } });
}
Interceptor.attach(at('0x141d65830'), {
  onEnter(a) { this.out = a[1]; this.row = a[2].toUInt32(); this.ret = this.returnAddress; },
  onLeave() { try { if (this.out.readPointer().isNull()) send({missingInventoryId: this.row, from: va(this.ret)}); } catch (e) {} }
});
send({ready: 'trace-enter-area'});
