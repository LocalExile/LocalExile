// Load after common.js. Why a Holy Strike ground click crashes the client (issue #167): the action's update
// 0x141c9b190 reads the main-hand weapon's AnimationController (+0x180 = its animation set) when the action's +0x3b1
// ("animates the owner's main hand weapon", from holy_strike_animates_owners_main_hand_weapon) is set, and crashes on a
// null animation set. The log shows who starts the action (the 0x1ab handler 0x14219ad1a or the client's own click),
// how often, and the weapon controller's state on every action, so a null set can be tied to what dropped it.
const getAnimated = new NativeFunction(at('0x14013e1b0'), 'pointer', ['pointer']);
const getAnimController = new NativeFunction(at('0x14013e220'), 'pointer', ['pointer']);

// The action's actor (+0xe8) → Animated (+0x260 is the animated weapon child) → AnimationController.
function weapon(action) {
  try {
    const actor = action.add(0xe8).readPointer();
    const animated = getAnimated(actor.add(8).readPointer());
    if (animated.isNull()) return 'animated=null';
    const child = animated.add(0x260).readPointer();
    if (child.isNull()) return 'child=null';
    const controller = getAnimController(child);
    if (controller.isNull()) return 'controller=null';
    return 'controller=' + controller + ' set=' + controller.add(0x180).readPointer() + ' current=' + controller.add(0x190).readU32();
  } catch (e) { return 'probe:' + e; }
}

function actionInfo(action) {
  try {
    return {sequence: action.add(0x11a).readU16(), animates: action.add(0x3b1).readU8(),
            target: action.add(0x130).readU32() + ',' + action.add(0x134).readU32(), weapon: weapon(action)};
  } catch (e) { return {probe: String(e)}; }
}

// Who queues an action on an Actor: the 0x1ab "action started" handler (a call at 0x14219ad1a) or the client's own click.
const FROM_1AB = '0x14219ad1a';
Interceptor.attach(at('0x141e937b0'), {
  onEnter(args) {
    try {
      const action = args[1].readPointer();
      const ra = this.returnAddress;
      const from = ra.equals(at(FROM_1AB)) ? '1ab-echo' : 'client';
      send({queue: action.isNull() ? null : action.toString(), from, ra: va(ra), t: Date.now()});
    } catch (e) { send({queueError: String(e)}); }
  }
});

const updates = new Map();   // action -> how many updates it has had
Interceptor.attach(at('0x141c9b190'), {
  onEnter(args) {
    try {
      const action = args[0];
      const key = action.toString();
      const seen = (updates.get(key) || 0) + 1;
      updates.set(key, seen);
      if (seen > 4 && seen % 30 !== 0) return;   // a few per action, then every 30th call
      send(Object.assign({update: key, n: seen, t: Date.now()}, actionInfo(action)));
    } catch (e) { send({updateError: String(e)}); }
  }
});

// The action's start/reset: it clears the weapon animation vectors and the flag before the action sets them again.
Interceptor.attach(at('0x141c9b060'), {
  onEnter(args) {
    try {
      const action = args[0];
      updates.delete(action.toString());
      send(Object.assign({reset: action.toString(), t: Date.now()}, actionInfo(action)));
    } catch (e) { send({resetError: String(e)}); }
  }
});

send('trace-weapon-anim ready');
