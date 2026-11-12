// Self-contained. Real mouse input and the client's own answers for an outside operator that moves the character the
// way a person does: the walk primitive is a click, the client decides what the server is sent, and the bridge reads
// back what the client itself resolved.
//
// Load with Python's frida into the SAME session as client-control.js (scripts/dev/client_control.py, the `act`
// command). One session, more than one script: two tools attached at once crash the client inside frida-agent.dll.
//
//   attach()            find this client's own window and take over its cursor and button reads
//   release()           give the cursor and the button reads back
//   state()             {cell, moving, target, click, clicks, cursor, held, viewport, window}
//   cursor(x, y)        put the client's cursor at client-area pixels
//   down() / up()       hold and release the left button
//   click(x, y)         cursor, then down and up
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
const getCursorPos = user32.getExportByName('GetCursorPos');

const WM_ACTIVATE = 6, WM_SETFOCUS = 7, WM_ACTIVATEAPP = 0x1c, WM_MOUSEMOVE = 0x200;
const WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, MK_LBUTTON = 1, VK_LBUTTON = 1;

let hwnd = ptr(0), width = 0, height = 0, cx = 0, cy = 0, held = false, active = false;

// The client's own window: the largest visible top-level window of this process. A session client never takes the
// focus, so GetForegroundWindow is no help here.
const findWindow = () => {
  const pid = currentPid(), slot = Memory.alloc(4), box = Memory.alloc(16);
  let best = ptr(0), area = 0, bestWidth = 0, bestHeight = 0;
  const each = new NativeCallback(window => {
    windowPid(window, slot);
    if (slot.readU32() === pid && isVisible(window) !== 0) {
      clientRect(window, box);
      const w = box.add(8).readS32(), h = box.add(12).readS32();
      if (w > 0 && h > 0 && w * h > area) { best = window; area = w * h; bestWidth = w; bestHeight = h; }
    }
    return 1;
  }, 'int', ['pointer', 'pointer']);
  enumWindows(each, ptr(0));
  if (best.isNull()) throw new Error('no visible window for this process');
  hwnd = best; width = bestWidth; height = bestHeight;
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

// A held button is read as a key state every frame, not from the message queue.
for (const name of ['GetAsyncKeyState', 'GetKeyState']) {
  Interceptor.attach(user32.getExportByName(name), {
    onEnter(args) { this.left = active && args[0].toInt32() === VK_LBUTTON; },
    onLeave(result) { if (this.left) result.replace(ptr(held ? 0x8000 : 0)); },
  });
}

// The local player's Positioned object, from stable pointers rather than from a message that only arrives when an
// object changes: game = FUN_1400f3740(), world = *(game+0xc278), entity = FUN_1400f27c0(world), positioned =
// *(entity+0x98). Checked against /pos on 2026-11-12: cell (197, 261) in 1_1_town, same number. The world handler
// below is only a fallback for the moment the entity has not been made yet.
const cellOf = p => [p.add(0x294).readS32(), p.add(0x298).readS32()];
let hooked = null, clickCell = null, clicks = 0;

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
      if (id === 0x1000) hooked = actor.add(8).readPointer().add(0x98).readPointer();
    } catch (e) { }        // another object's message: keep what we already have
  }
});

// The client's own resolved walk target: it computes this from the cursor and asks for the action at 0x1421660b0,
// whose fourth argument points at the cell it chose. Reading it here is how the bridge knows the client aimed.
Interceptor.attach(at('0x1421660b0'), {
  onEnter(a) {
    if (a[3].isNull()) return;
    try { clickCell = [a[3].readS32(), a[3].add(4).readS32()]; clicks += 1; } catch (e) { }
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

const state = () => {
  let cell = null, moving = null, game = null;
  const player = positionedNow();
  if (player !== null) {
    try { cell = cellOf(player); moving = player.add(0x240).readFloat() !== 0; }
    catch (e) { }
  }
  try { game = gameOf().toString(); } catch (e) { }
  return { cell, moving, target: target(), click: clickCell, clicks, cursor: [cx, cy], held, frame,
           game, viewport: [width, height], window: hwnd.toString() };
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
const release = () => { if (held) up(); active = false; };

rpc.exports = { attach, release, state, cursor, down, up, click };

send('client-act ready');
