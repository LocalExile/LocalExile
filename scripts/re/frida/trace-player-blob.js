// Paste after common.js. Logs the 0x2bb blob, the created object's component vtables in order, and the blob reader
// position on entry/exit of each component deserializer (Agents/docs/protocol/04-player-entity.md).
const names = {'0x1435a0f18':'Positioned','0x1435dd4e8':'Comp1','0x1435a6ba0':'Pathfinding','0x14359f5f8':'Comp3',
  '0x1435a7340':'Life','0x1435a5030':'Animated','0x143466450':'Player','0x1435a0078':'Comp7','0x14359e078':'Inventories',
  '0x14346e558':'Comp9','0x143468920':'Comp10','0x14346c238':'Comp11'};
const hooks = {'0x141ebe190':['CreateObject',3],'0x141dea920':['BaseData',1],'0x141e2f0b0':['Positioned',1],
  '0x1421c2f40':['Comp1',1],'0x141e114d0':['Comp3',1],'0x141e6c2f0':['Life',1],'0x141e56a80':['Animated',1],
  '0x141e7dc10':['Player',1],'0x141e1ee30':['Inventories',1],'0x142192a80':['Comp9',1],'0x1421932b0':['Comp9Slot20',1],
  '0x141ee00f0':['Tasks',1]};
for (const [a, [name, ri]] of Object.entries(hooks)) {
  Interceptor.attach(at(a), {
    onEnter(args) {
      this.r = args[ri];
      if (name === 'CreateObject') {
        const d = this.r.readPointer(), n = this.r.add(8).readU64().toNumber();
        send({name, id: args[2].toInt32(), n, blob: Array.from(new Uint8Array(d.readByteArray(Math.min(n, 300)))).map(x => x.toString(16).padStart(2, '0')).join('')});
      }
      if (name === 'BaseData') {   // args[2] = the object; its component pointers are at +0x10..+0x18
        const obj = args[2], comps = [];
        for (let p = obj.add(0x10).readPointer(); p.compare(obj.add(0x18).readPointer()) < 0; p = p.add(8)) {
          const vt = va(p.readPointer().readPointer());
          comps.push((names[vt] || vt) + '@' + va(p.readPointer().readPointer().add(0x88).readPointer()));
        }
        send({components: comps});
      }
      send({enter: name, pos: pos(this.r)});
    },
    onLeave() { send({leave: name, pos: pos(this.r)}); }
  });
}
send({ready: 'trace-player-blob'});
