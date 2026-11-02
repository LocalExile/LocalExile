// Self-contained (Frida 17 gives each script its own scope). Which stats an action reads that ordinary play does not:
// for the first BASELINE_MS every (stat key, caller) pair the stat getters see is learned; after that only new pairs are
// sent, once each, with the value returned. Walk around during the baseline, then use the skill under study.
// Getters: 0x1424fc1e0 (stats object, runs no calculator: 11-skills.md) and 0x1424cfa40 (network Stats provider,
// 04-player-entity.md §4.1a). Keys are Stats.dat row + 1.
const BASELINE_MS = 20000;
const m = Process.getModuleByName('Game.patched.exe');
const at = va => m.base.add(ptr(va).sub(ptr('0x140000000')));
const va = p => (p.compare(m.base) >= 0 && p.compare(m.base.add(m.size)) < 0) ? '0x' + p.sub(m.base).add(ptr('0x140000000')).toString(16) : p.toString();
const start = Date.now();
const known = new Set();
let announced = false;
for (const [addr, getter] of [['0x1424fc1e0', 'stats'], ['0x1424cfa40', 'network']])
  Interceptor.attach(at(addr), {
    onEnter(args) { this.key = args[1].toUInt32(); this.caller = this.returnAddress; },
    onLeave(ret) {
      const k = getter + ':' + this.key + '@' + this.caller;
      if (Date.now() - start < BASELINE_MS) { known.add(k); return; }
      if (!announced) { announced = true; send({baselineDone: known.size}); }
      if (known.has(k)) return;
      known.add(k);
      send({at: new Date().toISOString().slice(11, 23), getter, key: '0x' + this.key.toString(16), value: ret.toInt32(),
        caller: va(this.caller)});
    }
  });
send('trace-action-stats ready: baseline ' + BASELINE_MS / 1000 + ' s');
