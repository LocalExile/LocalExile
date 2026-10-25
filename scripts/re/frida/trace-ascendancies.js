// Read-only 3.29.3b evidence: the received world stats, graph family, class byte and applied passive/point state.
// Run standalone: python scripts/re/frida/run.py <pid> --log logs/ascendancies.jsonl scripts/re/frida/trace-ascendancies.js
// No replacement, writes, or calls into game functions. Relog/area entry records graph selection; P records tree use.
(() => {
  const module = Process.getModuleByName('Game.patched.exe');
  const at = address => module.base.add(ptr(address).sub(ptr('0x140000000')));
  let instance = null;
  let lastWorld = '';

  function pairs(list) {
    const start = list.add(8).readPointer(), end = list.add(16).readPointer();
    const bytes = end.sub(start).toInt32();
    if (bytes < 0 || bytes > 800000 || bytes % 8 !== 0) return { invalid: bytes };
    const values = {};
    for (let p = start; p.compare(end) < 0; p = p.add(8)) values[p.readU32()] = p.add(4).readS32();
    return values;
  }

  function worldState(world) {
    return { world: world.toString(), stats: pairs(world.add(0x120)), areaLevel: world.add(0xd4).readU8() };
  }

  Interceptor.attach(at('0x142148880'), {
    onEnter(args) {
      instance = args[0];
      try {
        const world = instance.add(0xc278).readPointer();
        if (world.toString() !== lastWorld) {
          lastWorld = world.toString();
          send({ ascendancyWorld: worldState(world) });
        }
      } catch (error) { send({ ascendancyReadError: String(error) }); }
    }
  });

  Interceptor.attach(at('0x141ec5750'), {
    onEnter(args) { this.world = args[0]; },
    onLeave(result) {
      try {
        const row = result.readPointer();
        send({ ascendancyGraph: {
          ...worldState(this.world),
          tree: row.readPointer().readUtf16String(),
          graph: row.add(0x43).readPointer().readUtf16String()
        } });
      } catch (error) { send({ ascendancyGraphReadError: String(error) }); }
    }
  });

  Interceptor.attach(at('0x141ef2710'), {
    onEnter(args) { this.tree = args[0]; },
    onLeave() {
      try {
        const t = this.tree, packed = t.add(0x268).readU8();
        const first = t.add(0x180).readPointer(), end = t.add(0x188).readPointer();
        const count = end.sub(first).toInt32() / 2;
        const allocated = [];
        if (count >= 0 && count < 10000)
          for (let i = 0; i < count; i++) allocated.push(first.add(i * 2).readU16());
        send({ ascendancyPassives: {
          packed, baseClass: packed & 31, classNo: (packed >>> 6) | ((packed >>> 3) & 4),
          secondary: t.add(0x269).readU8(), level: t.add(0x26c).readU32(),
          refund: t.add(0x270).readU32(), ordinaryExtra: t.add(0x274).readU32(),
          ascendancyPoints: t.add(0x27c).readU32(),
          ordinaryPointsFromNodes: t.add(0x278).readU32(),
          ascendancyNodeCount: t.add(0x280).readU32(),
          secondaryNodeCount: t.add(0x284).readU32(),
          ordinaryFreeOptions: t.add(0x288).readU32(),
          ascendancyFreeOptions: t.add(0x28c).readU32(),
          secondaryFreeOptions: t.add(0x290).readU32(), allocated
        } });
      } catch (error) { send({ ascendancyPassiveReadError: String(error) }); }
    }
  });

  rpc.exports = {
    snapshot() {
      if (instance === null) return { pending: 'wait for an instance tick' };
      return { instance: instance.toString(), ...worldState(instance.add(0xc278).readPointer()) };
    }
  };
  send('trace-ascendancies ready (read-only)');
})();
