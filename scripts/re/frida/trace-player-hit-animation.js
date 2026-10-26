// Read-only issue #166 probe. Load in the existing Frida session; this never sends a packet or calls a game action.
// Learns the local player's body controller from its 0x1b2 Animated handler, then samples the clip and movement.
'use strict';
const image = Process.getModuleByName('Game.patched.exe');
const at = va => image.base.add(ptr(va).sub(ptr('0x140000000')));
let body = null, positioned = null, actor = null, until = 0, last = 0;
const action = p => {
  if (p.isNull()) return null;
  return { pointer: p.toString(), type: p.add(0x118).readU16(), skill: p.add(0x11a).readU16() };
};
function snapshot(controller) {
  const result = { controller: controller.toString(), index: controller.add(0x190).readS32(),
    elapsed: controller.add(0x1a4).readFloat(), speed: controller.add(0x1b0).readFloat(),
    start: controller.add(0x1b8).readFloat(), end: controller.add(0x1bc).readFloat() };
  if (positioned !== null && !positioned.isNull())
    result.movement = { cell: [positioned.add(0x294).readS32(), positioned.add(0x298).readS32()],
      step: positioned.add(0x240).readFloat(), elapsed: positioned.add(0x238).readFloat(),
      total: positioned.add(0x23c).readFloat(), accumulator: positioned.add(0x244).readFloat() };
  if (actor !== null)
    result.actions = { current: action(actor.add(0x1b0).readPointer()), predicted: action(actor.add(0x1b8).readPointer()),
      queued: action(actor.add(0x1c0).readPointer()), flags219: actor.add(0x219).readU8(),
      animationType: actor.add(0x274).readU32(), animationRemaining: actor.add(0x26c).readFloat() };
  return result;
}
Interceptor.attach(at('0x1421940a0'), { onEnter(args) {
  try {
    if (args[1].add(0x10).readU32() !== 0x1000) return;
    actor = args[0]; positioned = actor.add(8).readPointer().add(0x98).readPointer();
    body = actor.add(0x1a0).readPointer();
    const id = args[1].add(8).readU16();
    if (id === 0x1d1 || id === 0x1d2) {
      until = Date.now() + 2000;
      send({ hitProbeActorAnimation: { message: id, seconds: args[1].add(0x20).readFloat(),
        animationType: args[1].add(0x24).readU32(), flag: args[1].add(0x28).readU8(), before: snapshot(body) } });
    }
  } catch (error) { send({ hitProbeError: String(error) }); }
} });
Interceptor.attach(at('0x141e58ef0'), { onEnter(args) {
  try {
    const message = args[1];
    if (message.add(8).readU16() !== 0x1b2 || message.add(0x10).readU32() !== 0x1000) return;
    // At the kind-zero call site, args0+0x260 is a model-child entity. Its AnimationController is args0 of Play.
    positioned = args[0].add(8).readPointer().add(0x98).readPointer();
    until = Date.now() + 1500;
    send({ hitProbeMessage: { kind: message.add(0x20).readU8(), index: message.add(0x24).readU32(),
      speed: message.add(0x28).readFloat(), model: args[0].add(0x260).readPointer().toString() } });
  } catch (error) { send({ hitProbeError: String(error) }); }
} });
Interceptor.attach(at('0x141dba530'), {
  onEnter(args) {
    if (Date.now() >= until) return;
    try {
      // The second argument is a resource pair; the integer index is the THIRD argument (r8d).
      this.controller = args[0]; this.index = args[2].toUInt32();
      // Only the server-message call establishes the player body; later calls show what replaces its clip.
      if (this.returnAddress.equals(at('0x141e59351'))) body = this.controller;
      if (body === null || !this.controller.equals(body)) { this.controller = null; return; }
      send({ hitProbePlay: { requested: this.index, caller: this.returnAddress.toString(), before: snapshot(body) } });
    } catch (error) { this.controller = null; send({ hitProbeError: String(error) }); }
  },
  onLeave(result) {
    if (!this.controller) return;
    try { send({ hitProbePlayed: { requested: this.index, accepted: !result.isNull(), after: snapshot(this.controller) } }); }
    catch (error) { send({ hitProbeError: String(error) }); }
  }
});
Interceptor.attach(at('0x141dbc010'), { onEnter(args) {
  if (body === null || Date.now() >= until || !args[0].equals(body.add(0x180)) || Date.now() - last < 50) return;
  last = Date.now();
  try { send({ hitProbeTick: snapshot(body) }); }
  catch (error) { send({ hitProbeError: String(error) }); }
} });
Interceptor.attach(at('0x141e934b0'), {
  onEnter(args) {
    this.local = actor !== null && args[0].equals(actor) && body !== null && Date.now() < until;
    if (!this.local) return;
    this.animationType = args[1].toInt32();
    try { send({ hitProbeRecoveryEnd: { animationType: this.animationType, before: snapshot(body) } }); }
    catch (error) { send({ hitProbeError: String(error) }); }
  },
  onLeave() {
    if (!this.local) return;
    try { send({ hitProbeRecovered: { animationType: this.animationType, after: snapshot(body) } }); }
    catch (error) { send({ hitProbeError: String(error) }); }
  }
});
send('player hit-animation probe ready; waiting for local-player 0x1b2/0x1d1/0x1d2');
