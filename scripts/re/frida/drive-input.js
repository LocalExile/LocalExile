// Self-contained. Real mouse input for a client whose window never has the focus (a session client), so that the
// client runs its own predicted actions, which the bridge's `move` does not: that one only writes a C→S 0x25.
// Load with Python's frida and call the exports (see scripts/dev/sync_drive.py):
//   cursor(x, y)  the client's cursor at client-area pixels (GetCursorPos answers it, a WM_MOUSEMOVE tells the window)
//   down() / up() the left button
//   skill()       a click of the right button, the skill bar's slot 2 by default
//   key(code)     a press of the virtual key, Q (0x51) for slot 3
// Nothing is sent to the user's own mouse: PostMessageW targets only the client's window.
const user32 = Process.getModuleByName('user32.dll');
const fn = (name, ret, args) => new NativeFunction(user32.getExportByName(name), ret, args);
const postMessage = fn('PostMessageW', 'int', ['pointer', 'uint', 'pointer', 'pointer']);
const clientToScreen = fn('ClientToScreen', 'int', ['pointer', 'pointer']);
const getCursorPos = user32.getExportByName('GetCursorPos');
const MK_RBUTTON = 2, WM_ACTIVATE = 6, WM_SETFOCUS = 7, WM_ACTIVATEAPP = 0x1c, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, MK_LBUTTON = 1;

let hwnd = ptr(0), cx = 0, cy = 0, held = false, active = false;
const point = Memory.alloc(8);
const lparam = () => ptr(((cy & 0xffff) << 16) | (cx & 0xffff));

Interceptor.replace(getCursorPos, new NativeCallback(out => {
  if (!active) return 0;
  point.writeS32(cx); point.add(4).writeS32(cy);
  clientToScreen(hwnd, point);
  out.writeS32(point.readS32()); out.add(4).writeS32(point.add(4).readS32());
  return 1;
}, 'int', ['pointer']));

// The engine reads the mouse only for the foreground window (0x140e68220), and this one is never activated.
for (const name of ['GetForegroundWindow', 'GetActiveWindow', 'GetFocus']) {
  const real = new NativeFunction(user32.getExportByName(name), 'pointer', []);
  Interceptor.replace(user32.getExportByName(name), new NativeCallback(() => active ? hwnd : real(), 'pointer', []));
}

rpc.exports = {
  attach(window) {
    hwnd = ptr(window); active = true;
    postMessage(hwnd, WM_ACTIVATEAPP, ptr(1), ptr(0));
    postMessage(hwnd, WM_ACTIVATE, ptr(1), ptr(0));
    postMessage(hwnd, WM_SETFOCUS, ptr(0), ptr(0));
  },
  cursor(x, y) {
    cx = x; cy = y;
    postMessage(hwnd, WM_MOUSEMOVE, ptr(held ? MK_LBUTTON : 0), lparam());
  },
  down() { held = true; postMessage(hwnd, WM_LBUTTONDOWN, ptr(MK_LBUTTON), lparam()); },
  up() { held = false; postMessage(hwnd, WM_LBUTTONUP, ptr(0), lparam()); },
  key(code) {
    postMessage(hwnd, WM_KEYDOWN, ptr(code), ptr(1));
    postMessage(hwnd, WM_KEYUP, ptr(code), ptr(0xc0000001));
  },
  skill() {
    postMessage(hwnd, WM_RBUTTONDOWN, ptr(MK_RBUTTON), lparam());
    postMessage(hwnd, WM_RBUTTONUP, ptr(0), lparam());
  },
};

send('drive-input ready');
