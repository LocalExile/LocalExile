// Shared helpers for the Frida scripts in this folder (REA: start_frida_session attach → load_frida_script).
// Addresses are Ghidra VAs (image base 0x140000000) and are rebased onto the running game module: the patched
// checkout client (Game.patched.exe) or the official Steam/standalone client.
const m = (() => {
  for (const name of ['Game.patched.exe', 'PathOfExileSteam.exe', 'PathOfExile.exe']) {
    try { return Process.getModuleByName(name); } catch (e) { }
  }
  const found = Process.enumerateModules().find(x => /^(Game\.patched|PathOfExile(Steam)?)\.exe$/i.test(x.name));
  if (!found) throw new Error('game module not found');
  return found;
})();
const B = ptr('0x140000000');
// Data addresses in the scripts of this folder are the 3.29.3c release's (build 327984). Its 3.29.3.5 hotfix and
// 3.29.3d have the same code and move data (Agents/docs/re/tooling.md section 11): MOVED lists where each data
// address those scripts use sits in them. at() takes a release address and va() gives one back, so a script and its
// output stay in release addresses on every build. An unlisted object found where a listed one used to be is marked
// with the build.
const MOVED = {
  '3.29.3.5': {
    '0x142faa4d8':'0x142faa4d0','0x1435172b0':'0x143517280','0x143545408':'0x1435453d8','0x1435a6cb8':'0x1435a6c88',
    '0x1435a7a20':'0x1435a79f0','0x1435aa590':'0x1435aa560','0x1435ab608':'0x1435ab5d8','0x1435af9c0':'0x1435af9f0',
    '0x1435b0d98':'0x1435b0d60','0x1435b3800':'0x1435b37c0','0x1435e7f88':'0x1435e7f58','0x1436ae110':'0x1436ae0e0',
    '0x1436ae46c':'0x1436ae43c','0x1436af6cc':'0x1436af69c'},
  '3.29.3d': {
    '0x143245f88':'0x143245f08','0x14346c6c0':'0x14346c640','0x143470e78':'0x143470df8','0x1434730b8':'0x143473038',
    '0x143476978':'0x1434768f8','0x14347a818':'0x14347a7e0','0x1435172b0':'0x143517210','0x143545408':'0x143545368',
    '0x1435a6cb8':'0x1435a6c18','0x1435a7a20':'0x1435a7980','0x1435aa590':'0x1435aa4f0','0x1435ab608':'0x1435ab568',
    '0x1435af9c0':'0x1435af950','0x1435b0d98':'0x1435b0cf0','0x1435b3800':'0x1435b3750','0x1435e7f88':'0x1435e7ed8',
    '0x1436ae110':'0x1436ae060','0x1436ae46c':'0x1436ae3bc','0x1436af6cc':'0x1436af61c'}};
// The build by its link time (the PE header's TimeDateStamp): the release and its hotfix both call themselves 3.29.3c.
const BUILD = ({0x6af1219e: '3.29.3.5', 0x6af26d5e: '3.29.3d'})[m.base.add(m.base.add(0x3c).readU32() + 8).readU32()] || '';
const moved = MOVED[BUILD] || {};
const movedBack = Object.fromEntries(Object.entries(moved).map(([from, to]) => [to, from]));
const at = va => m.base.add(ptr(typeof va === 'string' && moved[va.toLowerCase()] || va).sub(B));
const va = p => {
  if (p.compare(m.base) < 0 || p.compare(m.base.add(m.size)) >= 0) return p.toString();
  const s = '0x' + p.sub(m.base).add(B).toString(16);
  return movedBack[s] || (moved[s] ? s + '@' + BUILD : s);
};
// Blob reader passed to every component deserializer: { u8* data; u64 size; u64 pos }.
const pos = r => { try { return r.add(0x10).readU64().toNumber() + '/' + r.add(8).readU64().toNumber(); } catch (e) { return '?'; } };
// First-chance exceptions with an image-relative backtrace. Returning false lets the client handle/crash as usual.
Process.setExceptionHandler(d => {
  send({exc: d.type, at: va(d.address), mem: d.memory ? va(d.memory.address) : null,
        bt: Thread.backtrace(d.context, Backtracer.FUZZY).slice(0, 18).map(va)});
  return false;
});
