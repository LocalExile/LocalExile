// Load after common.js. Each spark's wander legs: the generator seed (0x14146d8f0's four words, read by the first leg
// 0x141b35a10 from the spark action's +0x11a sequence, +0x11c byte, +0x2d0 and the owner entity's +0x88), and every
// leg's start cell, target cell, heading (+0x538) and speed factor (+0x540) as 0x141bacf60 receives them.
const sparkVtables = ['0x14350ca90', '0x14323bdd8'].map(v => at(v));
const isSpark = a => { try { const v = a.readPointer(); return sparkVtables.some(s => s.equals(v)); } catch (e) { return false; } };
const cell = p => { try { return [p.readS32(), p.add(4).readS32()]; } catch (e) { return null; } };
const here = a => { try { return cell(a.add(0x328).readPointer().add(0x98).readPointer().add(0x294)); } catch (e) { return null; } };
let first = null;
Interceptor.attach(at('0x141b35a10'), {
  onEnter(args) {
    if (!isSpark(args[0])) return;
    const a = args[0]; first = a;
    let owner = null; try { owner = a.add(0xe8).readPointer().add(8).readPointer().add(0x88).readU32(); } catch (e) { }
    send({spark: a.toString(), t: Date.now(), seq: a.add(0x11a).readU16(), b11c: a.add(0x11c).readU8(),
          f2d0: a.add(0x2d0).readU32(), owner, at: here(a), aim: cell(args[1])});
  },
  onLeave() { first = null; }
});
Interceptor.attach(at('0x14146d8f0'), {
  onEnter(args) {
    if (first === null) return;
    const w = args[1]; send({seed: first.toString(), words: [0, 4, 8, 12].map(o => w.add(o).readU32())});
  }
});
Interceptor.attach(at('0x141bacf60'), {
  onEnter(args) { if (isSpark(args[0])) { this.a = args[0]; this.to = cell(args[1]); } },
  onLeave() {
    if (!this.a) return;
    send({leg: this.a.toString(), t: Date.now(), at: here(this.a), to: this.to,
          heading: this.a.add(0x538).readFloat(), speed: this.a.add(0x540).readFloat(),
          life: this.a.add(0x534).readFloat()});
  }
});
send('trace-spark-legs ready');
