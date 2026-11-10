// Load after common.js. Logs every skill the client adds to an ActiveSkillManager (Actor +0x598) and where from.
// SkillInstanceAdded(manager, ActiveSkillInstance&) = 0x141ef0f00 (RTTI lambda name). The instance holds the u16 skill id
// at +0x40 (the id 0x1ab and C->S 0x24 carry), a u16 at +0x42 and a GrantedEffects row handle at +0x48.
const skillName = s => {
  try { return s.add(0x48).readPointer().readPointer().readPointer().readUtf16String(); } catch (e) { return '?'; }
};
Interceptor.attach(at('0x141ef0f00'), {
  onEnter(args) {
    const s = args[1];
    send({skillAdded: {mgr: args[0].toString(), id: '0x' + s.add(0x40).readU16().toString(16),
      w42: '0x' + s.add(0x42).readU16().toString(16), name: skillName(s),
      bt: Thread.backtrace(this.context, Backtracer.FUZZY).slice(0, 14).map(va)}});
  }
});
send('trace-skills ready');
