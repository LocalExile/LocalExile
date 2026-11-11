// Self-contained. Measures how far the server's walk estimate is from where the client really is (movement drift).
//   python scripts/re/frida/run.py Game.patched.exe --log logs/drift.jsonl scripts/re/frida/trace-player-drift.js
// - echo: at each S→C 0x1ab for the local, predicted player (Actor handler 0x14219aa10, sequence >= 0), the position the
//   server reports (msg +0x2c/+0x30), the click target (+0x24/+0x28) and the client's own grid cell (Positioned +0x294/+0x298,
//   via entity +0x98), with |d| = their distance in cells. The client snaps to the server's position only when |d|² >= 0x2110.
// - stop: the cell where the player came to rest (no change for 150 ms), sampled every 20 ms after the last echo.
const m = Process.getModuleByName('Game.patched.exe');
const at = va => m.base.add(ptr(va).sub(ptr('0x140000000')));
let positioned = null, lastMove = 0, last = null, resting = true, seq = -1;
const t0 = Date.now();
const now = () => (Date.now() - t0) / 1000;

Interceptor.attach(at('0x14219aa10'), {
  onEnter(args) {
    const actor = args[0], msg = args[1];
    if (msg.add(8).readU16() !== 0x1ab) return;
    const s = msg.add(0x36).readS16();
    if (s < 0 || actor.add(0x1c8).readU8() === 0) return;   // not the local, predicted player
    positioned = actor.add(8).readPointer().add(0x98).readPointer();
    const server = [msg.add(0x2c).readS32(), msg.add(0x30).readS32()];
    const target = [msg.add(0x24).readS32(), msg.add(0x28).readS32()];
    const client = [positioned.add(0x294).readS32(), positioned.add(0x298).readS32()];
    const dx = server[0] - client[0], dy = server[1] - client[1];
    seq = s;
    send({t: now().toFixed(3), echo: s & 0xffff, server, client, target,
          d: Math.sqrt(dx * dx + dy * dy).toFixed(1), snap: dx * dx + dy * dy >= 0x2110});
  }
});

setInterval(() => {
  if (positioned === null) return;
  let p;
  try { p = [positioned.add(0x294).readS32(), positioned.add(0x298).readS32()]; } catch (e) { positioned = null; return; }
  const t = now();
  if (last === null || p[0] !== last[0] || p[1] !== last[1]) { last = p; lastMove = t; resting = false; return; }
  if (!resting && t - lastMove >= 0.15) { resting = true; send({t: lastMove.toFixed(3), stop: p, after: seq & 0xffff}); }
}, 20);

send('trace-player-drift ready');
