// Self-contained. Real mouse input and the client's own answers for an outside operator that moves the character the
// way a person does: the walk primitive is a click, the client decides what the server is sent, and the bridge reads
// back what the client itself resolved.
//
// Load with Python's frida into the SAME session as client-control.js (scripts/dev/client_control.py, the `act`
// command). One session, more than one script: two tools attached at once crash the client inside frida-agent.dll.
//
//   attach()            find this client's own window and take over its cursor and button reads
//   release()           give the cursor and the button reads back
//   state()             {cell, moving, target, click, obj, clicks, cursor, held, viewport, window}
//   cursor(x, y)        put the client's cursor at client-area pixels
//   down() / up()       hold and release the left button
//   click(x, y)         cursor, then down and up
//   objects(radius)     every object the client has been told about: {id, cell, world, screen}
//   screen(x, y)        the client-area pixel a world position is drawn at (0 when it is not behind the camera)
//   skill(x, y)         cursor, then the right button (the default attack sits on bar slot 0)
//   key(code)           press and release a virtual key, for a skill bound to the bar
//
// Nothing here writes a packet and nothing is sent to the user's own mouse: PostMessageW targets only this process's
// window, and the client's own action code turns the click into whatever goes to the server. The reads are the
// client's own: the local player's cell, whether it is moving, the cell a click would walk to right now (`target`, so
// aiming costs nothing and happens before the button goes down), and the cell the last action resolved to
// (`click`). scripts/re/frida/trace-sync.js is where the player and action addresses come from; the target publish
// and the world→cell conversion are in Agents/docs/re/ (task PR1 reads) and Agents/docs/dev/client-control.md.
const m = Process.getModuleByName('Game.patched.exe');
const at = va => m.base.add(ptr(va).sub(ptr('0x140000000')));
const user32 = Process.getModuleByName('user32.dll');
const kernel32 = Process.getModuleByName('kernel32.dll');
const fn = (lib, name, ret, args) => new NativeFunction(lib.getExportByName(name), ret, args);

const postMessage = fn(user32, 'PostMessageW', 'int', ['pointer', 'uint', 'pointer', 'pointer']);
const clientToScreen = fn(user32, 'ClientToScreen', 'int', ['pointer', 'pointer']);
const enumWindows = fn(user32, 'EnumWindows', 'int', ['pointer', 'pointer']);
const windowPid = fn(user32, 'GetWindowThreadProcessId', 'uint', ['pointer', 'pointer']);
const isVisible = fn(user32, 'IsWindowVisible', 'int', ['pointer']);
const clientRect = fn(user32, 'GetClientRect', 'int', ['pointer', 'pointer']);
const currentPid = fn(kernel32, 'GetCurrentProcessId', 'uint', []);
const showWindow = fn(user32, 'ShowWindow', 'int', ['pointer', 'int']);
const setWindowPos = fn(user32, 'SetWindowPos', 'int', ['pointer', 'pointer', 'int', 'int', 'int', 'int', 'uint']);
const getCursorPos = user32.getExportByName('GetCursorPos');

const WM_ACTIVATE = 6, WM_SETFOCUS = 7, WM_ACTIVATEAPP = 0x1c, WM_MOUSEMOVE = 0x200;
const WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, MK_LBUTTON = 1, VK_LBUTTON = 1;
const WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, MK_RBUTTON = 2, VK_RBUTTON = 2;
const WM_KEYDOWN = 0x100, WM_KEYUP = 0x101;
const SW_RESTORE = 9, SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_SHOWWINDOW = 0x0040;

let hwnd = ptr(0), width = 0, height = 0, cx = 0, cy = 0, held = false, heldRight = false, active = false;

// The client's own window: the largest visible top-level window of this process. A session client never takes the
// focus, so GetForegroundWindow is no help here.
const findWindow = () => {
  const pid = currentPid(), slot = Memory.alloc(4), box = Memory.alloc(16);
  let best = ptr(0), area = -1;
  const each = new NativeCallback(window => {
    windowPid(window, slot);
    if (slot.readU32() === pid && isVisible(window) !== 0) {
      clientRect(window, box);
      const w = box.add(8).readS32(), h = box.add(12).readS32();
      const a = Math.max(1, w) * Math.max(1, h);
      if (a > area) { best = window; area = a; }
    }
    return 1;
  }, 'int', ['pointer', 'pointer']);
  enumWindows(each, ptr(0));
  if (best.isNull()) throw new Error('no visible window for this process');
  hwnd = best;
  clientRect(hwnd, box);
  // A session client can be minimised, and a minimised window reports a 0x0 client rect and receives no frames at
  // all, so nothing would pump the input the operator posts. Restore it before anything else is read.
  if (box.add(8).readS32() <= 0 || box.add(12).readS32() <= 0) {
    showWindow(hwnd, SW_RESTORE);
    setWindowPos(hwnd, ptr(-2), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOZORDER);
    clientRect(hwnd, box);
  }
  width = box.add(8).readS32(); height = box.add(12).readS32();
  if (width <= 0 || height <= 0) { width = 800; height = 600; }
  cx = Math.floor(width / 2); cy = Math.floor(height / 2);
};

const lparam = () => ptr(((cy & 0xffff) << 16) | (cx & 0xffff));

// The engine reads the mouse only for the window it believes is the foreground one (0x140e6a540), and this one is
// never activated, so the cursor has to answer from here while we drive (same route as scripts/re/frida/drive-input.js,
// which scripts/dev/sync_drive.py uses for its measurement rigs).
const point = Memory.alloc(8);
Interceptor.replace(getCursorPos, new NativeCallback(out => {
  if (!active) return 0;
  point.writeS32(cx); point.add(4).writeS32(cy);
  clientToScreen(hwnd, point);
  out.writeS32(point.readS32()); out.add(4).writeS32(point.add(4).readS32());
  return 1;
}, 'int', ['pointer']));

for (const name of ['GetForegroundWindow', 'GetActiveWindow', 'GetFocus']) {
  const real = new NativeFunction(user32.getExportByName(name), 'pointer', []);
  Interceptor.replace(user32.getExportByName(name), new NativeCallback(() => active ? hwnd : real(), 'pointer', []));
}

// A held button is read as a key state every frame, not from the message queue, so both buttons answer here.
for (const name of ['GetAsyncKeyState', 'GetKeyState']) {
  Interceptor.attach(user32.getExportByName(name), {
    onEnter(args) {
      const vk = args[0].toInt32();
      this.vk = !active ? 0 : vk === VK_LBUTTON ? VK_LBUTTON : vk === VK_RBUTTON ? VK_RBUTTON : 0;
    },
    onLeave(result) {
      if (this.vk === VK_LBUTTON) result.replace(ptr(held ? 0x8000 : 0));
      else if (this.vk === VK_RBUTTON) result.replace(ptr(heldRight ? 0x8000 : 0));
    },
  });
}

// The local player's Positioned object, from stable pointers rather than from a message that only arrives when an
// object changes: game = FUN_1400f3740(), world = *(game+0xc278), entity = FUN_1400f27c0(world), positioned =
// *(entity+0x98). Checked against /pos on 2026-11-12: cell (197, 261) in 1_1_town, same number. The world handler
// below is only a fallback for the moment the entity has not been made yet.
const cellOf = p => [p.add(0x294).readS32(), p.add(0x298).readS32()];
const worldOf = p => [p.add(0x2b8).readFloat(), p.add(0x2bc).readFloat()];
let hooked = null, clickCell = null, clickObject = 0, clickFrom = null, clickWalk = null, clicks = 0;

// Every object the client has been told about, from the same two handlers scripts/re/frida/trace-sync.js uses: the
// object message (0x14219aa10) learns one (its Positioned object carries the cell and the world position) and a
// removal (0x14218ddf0, message 0x2bd) forgets it. A removed object's pointer would read freed memory, so every
// read in objects() is defensive.
const objects = new Map();
const learn = (id, positioned) => { if (!positioned.isNull()) objects.set(id, positioned); };

const positionedNow = () => {
  try {
    const entity = entityOf(gameOf().add(0xc278).readPointer());
    if (!entity.isNull()) {
      const found = entity.add(0x98).readPointer();
      if (!found.isNull()) return found;
    }
  } catch (e) { }
  return hooked;
};

Interceptor.attach(at('0x14219aa10'), {
  onEnter(a) {
    try {
      const actor = a[0], id = a[1].add(0x10).readU32();
      const positioned = actor.add(8).readPointer().add(0x98).readPointer();
      learn(id, positioned);
      if (id === 0x1000) hooked = positioned;
    } catch (e) { }        // another object's message: keep what we already have
  }
});

Interceptor.attach(at('0x14218ddf0'), {
  onEnter(a) {
    for (const arg of [a[1], a[2]]) {
      try {
        if (arg.add(8).readU16() !== 0x2bd) continue;
        objects.delete(arg.add(0x10).readU32());
        return;
      } catch (e) { /* not the message argument */ }
    }
  }
});

// What the client drew a label for: the label pass (0x140ca57f0) receives the actor, and the actor's Positioned
// object holds the world point the label is anchored to. This is the only honest answer to "what can a person click
// right now" in this build; the object-message handler above never fires for spawned monsters, and the label pass
// runs while the game window is the foreground one (attach() makes it so).
const labels = new Map();
Interceptor.attach(at('0x140ca57f0'), {
  onEnter(a) {
    try {
      const actor = a[1];
      if (actor.isNull()) return;
      const positioned = actor.add(0x98).readPointer();
      if (positioned.isNull()) return;
      if (labels.size > 256) labels.clear();
      labels.set(actor.toString(), positioned);
    } catch (e) { }
  }
});

// The client's own resolved action: it computes this from the cursor and asks for the action at 0x1421660b0, whose
// arguments are (game, skill, object, &cell, flags, 0) — reading them here is how the bridge knows what the client
// aimed at, and the object word is the only place a click carries an object id (0 for the Move skill, by design).
// The return address says which caller asked: 0x14012461e is the click→walk path, the other 14 are skill paths.
const WALK_CALLER = at('0x14012461e').toString();
Interceptor.attach(at('0x1421660b0'), {
  onEnter(a) {
    try {
      clickObject = a[2].toInt32();
      clickFrom = this.returnAddress.toString();
      clickWalk = clickFrom === WALK_CALLER;
      if (a[3].isNull()) return;
      clickCell = [a[3].readS32(), a[3].add(4).readS32()]; clicks += 1;
    } catch (e) { }
  }
});

// The cell a click would walk to right now: the client resolves the cursor into a world point every frame and
// publishes it on its instance, and the click asks for exactly that point. Reading it means the bridge can aim with
// no calibration, no camera and no walk packet: move the cursor, read this, then press. `FUN_140df5100` is the
// client's own world→cell conversion (a pure function: cell = rintf(world * 0.092f), no entity, no per-area state).
const gameOf = new NativeFunction(at('0x1400f3740'), 'pointer', []);
const entityOf = new NativeFunction(at('0x1400f27c0'), 'pointer', ['pointer']);
const worldToCell = new NativeFunction(at('0x140df5100'), 'pointer', ['pointer', 'pointer']);
const world = Memory.alloc(8), asCell = Memory.alloc(8);
// The published pair lives on the game object (FUN_1400f3740()), not on the app singleton: that singleton's
// address is data (it moves between client builds), and nothing here needs it.
const targetFrom = game => {
  if (game.isNull()) return null;
  world.writeFloat(game.add(0xc7f4).readFloat());
  world.add(4).writeFloat(game.add(0xc7f8).readFloat());
  worldToCell(asCell, world);
  return { world: [world.readFloat(), world.add(4).readFloat()], cell: [asCell.readS32(), asCell.add(4).readS32()] };
};
const target = () => {
  try { return targetFrom(gameOf()); } catch (e) { return null; }
};

// Frames, so a caller can wait for the client to have seen the cursor before reading what it resolved.
let frame = 0;
Interceptor.attach(at('0x141528fbf'), { onEnter() { frame += 1; } });

// What the client has selected right now. The frame state and the action object hang off the application global,
// which is data (it moves between client builds), so it is found through the instruction that loads it — the same
// discipline as globalAt() in scripts/dev/client-control.js — and never hard-coded.
//   app = *(0x1400f3b30's target), S = *(app+0x260), action = *(S+0x330), T = *(*(action+0x168)+0x338)
// T+0xd8 is the acquired entity (sticky: only the object-request entry and the clear write it), T+0xa8 is the object
// slot whose +0x88 is the id, and *(char*)(action+0x19d) is the flag that decides whether the packet carries an id.
const APP_LOAD = 0x1400f3b30;
const appOf = () => {
  const ins = at(APP_LOAD);
  return ins.add(7).add(ins.add(3).readS32()).readPointer();
};
const actionNow = () => {
  try {
    const action = appOf().add(0x260).readPointer().add(0x330).readPointer();
    return action.isNull() ? null : action;
  } catch (e) { return null; }
};
const selected = () => {
  try {
    const action = actionNow();
    if (action === null) return null;
    const t = action.add(0x168).readPointer().add(0x338).readPointer();
    if (t.isNull()) return null;
    const entity = t.add(0xd8).readPointer(), slot = t.add(0xa8).readPointer();
    return { entity: entity.toString(), id: entity.isNull() || slot.isNull() ? 0 : slot.add(0x88).readU32(),
             objectForm: action.add(0x19d).readU8() !== 0 };
  } catch (e) { return null; }
};

// World → screen, so an object can be aimed at. The camera comes from its own getter (a code address) and the
// projection is the client's own function: no data address and no hard-coded zoom.
const cameraOf = new NativeFunction(at('0x140fcb430'), 'pointer', []);
const project = new NativeFunction(at('0x140fd3b40'), 'pointer', ['pointer', 'pointer', 'pointer']);
const clip = Memory.alloc(8), world3 = Memory.alloc(12);
const screenFor = (wx, wy) => {
  try {
    const camera = cameraOf().add(8).readPointer().readPointer();
    if (camera.isNull()) return null;
    world3.writeFloat(wx); world3.add(4).writeFloat(wy); world3.add(8).writeFloat(0);
    project(camera, clip, world3);
    return [Math.round(clip.readFloat() + camera.add(0x278).readFloat()),
            Math.round(clip.add(4).readFloat() + camera.add(0x27c).readFloat())];
  } catch (e) { return null; }
};

// Every object the client knows about inside `radius` cells of the player, with the pixel each is drawn at.
const objectsNow = radius => {
  const player = positionedNow(), here = player === null ? null : cellOf(player), out = [];
  const collect = (source, table, named) => {
    for (const [key, positioned] of table) {
      try {
        const cell = cellOf(positioned);
        if (here !== null && radius > 0 && Math.max(Math.abs(cell[0] - here[0]), Math.abs(cell[1] - here[1])) > radius) continue;
        const world = worldOf(positioned);
        out.push({ id: named ? Number(key) : 0, cell, world, screen: screenFor(world[0], world[1]), source });
      } catch (e) { table.delete(key); }        // the object went away between the message and this read
    }
  };
  collect('message', objects, true);
  collect('label', labels, false);
  return out;
};

const state = () => {
  let cell = null, moving = null, game = null;
  const player = positionedNow();
  if (player !== null) {
    try { cell = cellOf(player); moving = player.add(0x240).readFloat() !== 0; }
    catch (e) { }
  }
  try { game = gameOf().toString(); } catch (e) { }
  return { cell, moving, target: target(), click: clickCell, clickObject, clickFrom, clickWalk, clicks, selected: selected(),
           cursor: [cx, cy], held, heldRight, frame, game, viewport: [width, height], window: hwnd.toString() };
};

const attach = () => {
  findWindow();
  active = true;
  postMessage(hwnd, WM_ACTIVATEAPP, ptr(1), ptr(0));
  postMessage(hwnd, WM_ACTIVATE, ptr(1), ptr(0));
  postMessage(hwnd, WM_SETFOCUS, ptr(0), ptr(0));
  return { window: hwnd.toString(), viewport: [width, height] };
};

const cursor = (x, y) => {
  cx = x | 0; cy = y | 0;
  postMessage(hwnd, WM_MOUSEMOVE, ptr(held ? MK_LBUTTON : 0), lparam());
};
const down = () => { held = true; postMessage(hwnd, WM_LBUTTONDOWN, ptr(MK_LBUTTON), lparam()); };
const up = () => { held = false; postMessage(hwnd, WM_LBUTTONUP, ptr(0), lparam()); };
const click = (x, y) => { cursor(x, y); down(); up(); };
// The right button is the default attack's slot; a key is held across frames because the client reads key state per
// frame, not from the message queue, so a down and an up inside one frame would never be seen.
const skill = (x, y) => {
  cursor(x, y);
  heldRight = true; postMessage(hwnd, WM_RBUTTONDOWN, ptr(MK_RBUTTON), lparam());
  heldRight = false; postMessage(hwnd, WM_RBUTTONUP, ptr(0), lparam());
};
const keyDown = code => postMessage(hwnd, WM_KEYDOWN, ptr(code | 0), ptr(0));
const keyUp = code => postMessage(hwnd, WM_KEYUP, ptr(code | 0), ptr(0));
const release = () => { if (held) up(); if (heldRight) { heldRight = false; postMessage(hwnd, WM_RBUTTONUP, ptr(0), lparam()); } active = false; };

rpc.exports = { attach, release, state, cursor, down, up, click, skill, keyDown, keyUp, objects: objectsNow, screen: screenFor, selected };

send('client-act ready');
