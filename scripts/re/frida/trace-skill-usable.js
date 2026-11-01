// Self-contained (Frida 17 gives each script its own scope, so common.js's helpers are not visible here).
// Why the client greys a skill out ("[Skill can't be used]", 11-skills.md §10a).
// FUN_141ee7fb0(skill, mainType, offType) sets the skill's +0xb0 (usable) / +0xb1; FUN_141ee8410 checks a hand's type
// against the ActiveSkills WeaponRestriction_ItemClassesKeys (+0xb2 main hand fits, +0xb3 off hand fits). The hand types
// live in the ActiveSkillManager at +0x14c / +0x150, set through FUN_142199200. A type is the ItemClasses row + 1.
// Each distinct result is sent once (the checks run every time the skill list or the equipment changes).
const m = Process.getModuleByName('Game.patched.exe');
const at = va => m.base.add(ptr(va).sub(ptr('0x140000000')));
const skillName = s => {
  try { return s.add(0x48).readPointer().readPointer().readPointer().readUtf16String(); } catch (e) { return '?'; }
};
const hex = (p, from, n) => { try { return Array.from(new Uint8Array(p.add(from).readByteArray(n))).map(b => b.toString(16).padStart(2, '0')).join(' '); } catch (e) { return '?'; } };
const seen = new Set();
const once = msg => { const k = JSON.stringify(msg); if (!seen.has(k)) { seen.add(k); send(msg); } };

Interceptor.attach(at('0x142199200'), {
  onEnter(args) { this.mgr = args[0]; this.a = [args[1], args[2], args[3]].map(a => a.toString()); },
  onLeave() {
    once({setHandTypes: {args: this.a, mgr14c: this.mgr.add(0x14c).readU32(), mgr150: this.mgr.add(0x150).readU32()}});
  }
});
Interceptor.attach(at('0x141ee7fb0'), {
  onEnter(args) {
    this.skill = args[0];
    this.main = args[1].toUInt32() & 0xff; this.off = args[2].toUInt32() & 0xff;
  },
  onLeave() {
    const s = this.skill;
    once({usable: {name: skillName(s), id: (() => { try { return '0x' + s.add(0x40).readU16().toString(16); } catch (e) { return '?'; } })(),
      mainType: this.main, offType: this.off, b0_b3: hex(s, 0xb0, 4), raw40_60: hex(s, 0x40, 0x20)}});
  }
});
Interceptor.attach(at('0x141ee8410'), {
  onEnter(args) { this.a = [args[0], args[1], args[2], args[3]].map(a => a.toString()); },
  onLeave(ret) { once({handFits: {args: this.a, ret: ret.toString()}}); }
});
send('trace-skill-usable ready');
