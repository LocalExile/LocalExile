// Paste after common.js. Logs the 0x2bb blob, the created object's component vtables in order, and the blob reader
// position on entry/exit of each component deserializer (Agents/docs/protocol/04-player-entity.md).
const names = {'0x1435ab608':'Positioned','0x1435e7f88':'Comp1','0x1435b3800':'Pathfinding','0x1435a7a20':'Comp3',
  '0x1435b0d98':'Life','0x1435af9c0':'Animated','0x143470e78':'Player','0x1435aa590':'Comp7','0x1435a6cb8':'Inventories',
  '0x14347a818':'Comp9','0x1434730b8':'Comp10','0x143476978':'Comp11'};
const hooks = {'0x141ec4a40':['CreateObject',3],'0x141dedf80':['BaseData',1],'0x141e323f0':['Positioned',1],
  '0x1421c98b0':['Comp1',1],'0x141e14b30':['Comp3',1],'0x141e72890':['Life',1],'0x141e5afd0':['Animated',1],
  '0x141e84300':['Player',1],'0x141e22490':['Inventories',1],'0x1421993f0':['Comp9',1],'0x142199c20':['Comp9Slot20',1],
  '0x141ee6950':['Tasks',1]};
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
