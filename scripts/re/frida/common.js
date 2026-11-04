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
const at = va => m.base.add(ptr(va).sub(B));
const va = p => (p.compare(m.base) >= 0 && p.compare(m.base.add(m.size)) < 0) ? '0x' + p.sub(m.base).add(B).toString(16) : p.toString();
// Blob reader passed to every component deserializer: { u8* data; u64 size; u64 pos }.
const pos = r => { try { return r.add(0x10).readU64().toNumber() + '/' + r.add(8).readU64().toNumber(); } catch (e) { return '?'; } };
// First-chance exceptions with an image-relative backtrace. Returning false lets the client handle/crash as usual.
Process.setExceptionHandler(d => {
  send({exc: d.type, at: va(d.address), mem: d.memory ? va(d.memory.address) : null,
        bt: Thread.backtrace(d.context, Backtracer.FUZZY).slice(0, 18).map(va)});
  return false;
});
