// Self-contained (Frida 17 gives each script its own scope). How a client projectile continues or ends where its flight
// stops (Agents/docs/re/projectiles/continuation-and-memory.md §2): each call of the shared terminal 0x141ba9350 with its
// action's vtable, and, the first time an action vtable is seen, hooks on that vtable's skill-specific continuation
// (+0x680, +0x520), final stage (+0x590), bounce (+0x768) and chain budget (+0x638) entries, each logged with its result.
// The ordinary chain path (0x141bb2ce0 → 0x141bb14f0 → 0x141bb1d30) is hooked too. Observation only.
// Rolling Magma (3.29.3c, 2026-11-01): its ground bounces never reached the ordinary chain path; cast at empty ground,
// then at monsters, and compare.
const m = Process.getModuleByName('Game.patched.exe');
const at = va => m.base.add(ptr(va).sub(ptr('0x140000000')));
const va = p => (p.compare(m.base) >= 0 && p.compare(m.base.add(m.size)) < 0) ? '0x' + p.sub(m.base).add(ptr('0x140000000')).toString(16) : p.toString();
const t0 = Date.now();
const now = () => Date.now() - t0;
const hooked = new Set();
function hook(name, address) {
  const key = address.toString();
  if (hooked.has(key)) return;
  hooked.add(key);
  try {
    Interceptor.attach(address, {
      onEnter(args) { this.a = args[0]; this.b = args[1]; this.caller = va(this.returnAddress); },
      onLeave(ret) {
        send({t: now(), fn: name, at: va(address), action: this.a.toString(), arg1: this.b.toString(), caller: this.caller,
          ret: ret.toString()});
      }
    });
  } catch (e) { send({hookFailed: name, at: va(address), error: e.message}); }
}
const vtables = new Set();
Interceptor.attach(at('0x141ba9350'), {
  onEnter(args) {
    const action = args[0];
    let vt = null;
    try { vt = action.readPointer(); } catch (e) { }
    const v = vt === null ? '?' : va(vt);
    // The chain fields the notes name: mode +0x450, executed chains +0x458, close-range chains +0x47c, chance byte +0x478.
    let fields = {};
    try {
      fields = {mode450: action.add(0x450).readS32(), executed458: action.add(0x458).readS32(),
        close47c: action.add(0x47c).readS32(), chance478: action.add(0x478).readU8(), sequence11a: action.add(0x11a).readU16()};
    } catch (e) { fields = {error: e.message}; }
    send({t: now(), fn: 'terminal', action: action.toString(), vtable: v, arg1: args[1].toString(), arg2: args[2].toString(),
      caller: va(this.returnAddress), ...fields});
    if (vt === null || vtables.has(v)) return;
    vtables.add(v);
    const entries = {};
    for (const [name, off] of [['skillContinue', 0x680], ['skillContinue2', 0x520], ['final', 0x590], ['bounce', 0x768], ['chainBudget', 0x638]]) {
      try {
        const f = vt.add(off).readPointer();
        entries[name] = va(f);
        hook(name + '@' + v, f);
      } catch (e) { entries[name] = 'unreadable'; }
    }
    send({vtable: v, entries});
  },
  onLeave(ret) { send({t: now(), fn: 'terminal-return', ret: ret.toString()}); }
});
hook('eligible', at('0x141bb2ce0'));
hook('search', at('0x141bb14f0'));
hook('chain', at('0x141bb5570'));
hook('bounceShared', at('0x141bb3450'));
send('trace-chain-decisions ready');
