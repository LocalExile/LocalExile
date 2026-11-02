// Self-contained (Frida 17 gives each script its own scope). The stat values FUN_141ee7fb0 reads while it decides
// whether a skill can be used (11-skills.md §10a), probed after each read; each stat getter returns in eax and the
// skill is in rdi. Keys are Stats.dat row + 1. Each distinct (skill, stat, value) is sent once.
const m = Process.getModuleByName('Game.patched.exe');
const at = va => m.base.add(ptr(va).sub(ptr('0x140000000')));
const skillName = s => {
  try { return s.add(0x48).readPointer().readPointer().readPointer().readUtf16String(); } catch (e) { return '?'; }
};
const seen = new Set();
const probes = [
  ['0x141ee80b7', 'skill_unusable'],
  ['0x141ee80e0', 'skill_number_of_triggers'],
  ['0x141ee8152', 'skill_disabled_by_stat'],
  ['0x141ee81a4', 'is_dual_wielding'],
  ['0x141ee81d0', 'disable_skill_if_melee_attack'],
  ['0x141ee81f3', 'disable_skill_if_weapon_not_bow'],
];
for (const [addr, stat] of probes)
  Interceptor.attach(at(addr), function () {
    const name = skillName(this.context.rdi), value = this.context.rax.toInt32();
    const k = name + stat + value;
    if (!seen.has(k)) { seen.add(k); send({skill: name, stat, value}); }
  });
// Inside the is_dual_wielding calculator (0x142521190), after it has read both hands: edi = main_hand_weapon_type,
// ebx = off_hand_weapon_type, as the client's stats hold them.
Interceptor.attach(at('0x142521337'), function () {
  const k = 'dual' + this.context.rdi.toInt32() + '/' + this.context.rbx.toInt32();
  if (!seen.has(k)) { seen.add(k); send({isDualWieldingReads: {main: this.context.rdi.toInt32(), off: this.context.rbx.toInt32()}}); }
});
send('trace-skill-usable-stats ready');
