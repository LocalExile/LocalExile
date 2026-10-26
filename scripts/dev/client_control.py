"""Fast, structured control of the local 3.29.3b client through its native functions.

    .venv/Scripts/python.exe scripts/dev/client_control.py game:status
    .venv/Scripts/python.exe scripts/dev/client_control.py game:open
    .venv/Scripts/python.exe scripts/dev/client_control.py login:offline --password-stdin
    .venv/Scripts/python.exe scripts/dev/client_control.py character:list
    .venv/Scripts/python.exe scripts/dev/client_control.py character:play:OfflineWitch
    .venv/Scripts/python.exe scripts/dev/client_control.py command "/hit physical 100"
    .venv/Scripts/python.exe scripts/dev/client_control.py chat "#hello"
    .venv/Scripts/python.exe scripts/dev/client_control.py tp "RhoaChest 2"
    .venv/Scripts/python.exe scripts/dev/client_control.py flask:use 1

Use --stdio for persistent JSON-lines RPC (one request/result per line). Each request is
{"command":"character:play","name":"OfflineWitch"}; login takes account/password fields.
The transport writes no credentials or tokens to disk. It attaches only to this checkout's
copied Game.patched.exe; it never starts an official/Steam client or changes save files.
"""
import argparse
import ctypes
from ctypes import wintypes
import getpass
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import struct
import sys
import time

import frida

ROOT = Path(__file__).resolve().parents[2]
CLIENT = ROOT / "Client" / "Game.patched.exe"
AGENT = Path(__file__).with_name("client-control.js")


def live_process(pid):
    """Frida's process inventory can retain a terminated process while another recorder holds its handle."""
    kernel=ctypes.WinDLL("kernel32",use_last_error=True)
    kernel.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD]
    kernel.OpenProcess.restype=wintypes.HANDLE
    kernel.GetExitCodeProcess.argtypes=[wintypes.HANDLE,ctypes.POINTER(wintypes.DWORD)]
    kernel.GetExitCodeProcess.restype=wintypes.BOOL
    kernel.CloseHandle.argtypes=[wintypes.HANDLE]
    handle=kernel.OpenProcess(0x1000,False,pid)
    if not handle:
        return False
    try:
        code=wintypes.DWORD()
        return bool(kernel.GetExitCodeProcess(handle,ctypes.byref(code))) and code.value==259
    finally:
        kernel.CloseHandle(handle)


def normalize(request):
    if isinstance(request, str):
        request = {"command": request}
    if not isinstance(request, dict):
        raise ValueError("Request must be a command string or JSON object")
    result = dict(request)
    command = result.get("command", "game:status")
    if not isinstance(command, str):
        raise ValueError("command must be a string")
    if command.startswith("character:play:"):
        result.update(command="character:play", name=command[len("character:play:"):])
    elif command.startswith("login:"):
        result.update(command="login", account=command[len("login:"):])
    return result


def chat_packet(text):
    if not isinstance(text, str) or not text or len(text) > 1024:
        raise ValueError("text must contain 1..1024 characters")
    packet = bytearray(b"\x00\x15" + len(text).to_bytes(2, "big"))
    for char in text:
        cp = ord(char)
        if cp < 0x80:
            packet.append(cp)
        elif cp <= 0xffff:
            packet.extend(b"\x80" + cp.to_bytes(2, "little"))
        else:
            packet.extend(b"\xc0" + cp.to_bytes(4, "little"))
    packet.append(0)
    return list(packet)


def flask_packet(request):
    """C→S 0x70 for a flask-bar cell, as the client sends it for a flask key: u32 big-endian cell, 0 for key 1 … 4
    for key 5 (Agents/docs/protocol/13-items.md §8). The request names the cell (`slot` 0-4) or the key (`key` 1-5)."""
    if ("slot" in request) == ("key" in request):
        raise ValueError("flask:use needs exactly one of slot (0-4) or key (1-5)")
    value = request["slot"] if "slot" in request else request["key"]
    if isinstance(value, bool) or not isinstance(value, int):
        raise ValueError("flask slot/key must be an integer")
    slot = value if "slot" in request else value - 1
    if not 0 <= slot <= 4:
        raise ValueError("flask slot must be 0-4 (key 1-5)")
    return list(struct.pack(">HI", 0x70, slot)), slot


GATEWAY_LOG = ROOT / "logs" / "live-gateway.log"
# Program.Commands.cs logs every reply line it sends: `[gateway] <remote> => 0x0017 chat line "<text>": <hex>`.
REPLY_LINE = re.compile(r'=> 0x0017 chat line "(.*)": [0-9A-F]+\s*$')


def tp_text(request):
    """The /tp command for a tp request: x/y (cell), dx/dy (relative), id, target [+ index], or list [+ filter]."""
    def number(name):
        value = request[name]
        if isinstance(value, bool) or not isinstance(value, int):
            raise ValueError(f"{name} must be an integer")
        return value
    if "x" in request or "y" in request:
        return f"/tp {number('x')} {number('y')}"
    if "dx" in request or "dy" in request:
        return f"/tp {number('dx'):+d} {number('dy'):+d}"
    if "id" in request:
        return f"/tp 0x{number('id'):x}"
    if request.get("list"):
        return "/tp list" + (f" {request['list']}" if isinstance(request["list"], str) else "")
    target = request.get("target", request.get("text"))
    if not isinstance(target, str) or not target.strip():
        raise ValueError("tp needs x/y, dx/dy, id, list or a target name")
    index = request.get("index")
    return f"/tp {target.strip()}" + (f" {int(index)}" if index is not None else "")


def log_mark(path=GATEWAY_LOG):
    """The Gateway log's current size, or None without a log (servers not started by live.ps1 in this checkout)."""
    try:
        return path.stat().st_size
    except OSError:
        return None


def read_replies(path, start):
    """The chat lines the Gateway sent after byte offset `start` of its log."""
    with path.open("rb") as log:
        log.seek(start)
        data = log.read()
    end = data.rfind(b"\n") + 1  # a line still being written is read next time
    lines = data[:end].decode("utf-8", errors="replace").splitlines()
    return [m.group(1) for m in map(REPLY_LINE.search, lines) if m], start + end


def collect_replies(start, wait=1.5, quiet=0.25, path=GATEWAY_LOG):
    """The Gateway's reply lines to a command just sent: read until `quiet` seconds pass without a new line after
    the first one, or `wait` seconds in total. None when there is no log to read."""
    if start is None:
        return None
    replies, offset = [], start
    deadline = time.monotonic() + max(0.0, float(wait))
    last = None
    while True:
        try:
            lines, offset = read_replies(path, offset)
        except OSError:
            return None
        if lines:
            replies += lines
            last = time.monotonic()
        now = time.monotonic()
        if now >= deadline or (last is not None and now - last >= quiet):
            return replies
        time.sleep(0.05)


class ClientControl:
    def __init__(self):
        self.device = frida.get_local_device()
        self.session = self.script = None
        self.pid = None
        self.detached = False

    def close(self):
        if self.session is not None:
            try:
                self.session.detach()
            except frida.InvalidOperationError:
                pass
        self.session = self.script = None
        self.pid = None

    def __enter__(self):
        return self

    def __exit__(self, *unused):
        self.close()

    def _find_client(self):
        # Name alone is insufficient: check the module path before any native command.
        candidates = [p for p in self.device.enumerate_processes() if p.name.lower() == "game.patched.exe" and live_process(p.pid)]
        if len(candidates) > 1:
            raise RuntimeError("Multiple patched clients are running; keep only this checkout's client")
        return candidates[0].pid if candidates else None

    def attach(self):
        if self.script is not None and not self.detached:
            return
        self.close()
        self.pid = self._find_client()
        if self.pid is None:
            raise RuntimeError("Client is not running; use game:open after starting the local servers")
        self.session = self.device.attach(self.pid)
        self.detached = False
        self.session.on("detached", lambda *unused: setattr(self, "detached", True))
        self.script = self.session.create_script(AGENT.read_text(encoding="utf-8"), name="localexile-client-control")
        errors = []
        self.script.on("message", lambda message, data: errors.append(message.get("description", "Agent error"))
                       if message.get("type") == "error" else None)
        self.script.load()
        try:
            identity = self.script.exports_sync.identity()
            if Path(identity["path"]).resolve() != CLIENT.resolve():
                raise RuntimeError("Refusing a client outside this checkout's Client directory")
        except Exception:
            self.close()
            if errors:
                raise RuntimeError(errors[0]) from None
            raise

    def rpc(self, op, **fields):
        self.attach()
        return self.script.exports_sync.command({"op": op, **fields})

    def status(self):
        if self.script is None and self._find_client() is None:
            return {"running": False}
        return {"running": True, **self.rpc("status")}

    def wait(self, predicate, description, timeout=15):
        deadline = time.monotonic() + timeout
        while True:
            value = self.status()
            if predicate(value):
                return value
            if time.monotonic() >= deadline:
                raise TimeoutError(f"Timed out waiting for {description}; inspect game:status and server logs")
            time.sleep(0.05)

    def ready(self, timeout=60):
        deadline=time.monotonic()+timeout
        while True:
            try:
                value=self.status()
                if value.get("screen") in ("login","characters","game"):
                    return value
            except (frida.RPCException,frida.InvalidOperationError,frida.ProcessNotFoundError,frida.TransportError):
                self.close()
            if time.monotonic()>=deadline:
                raise TimeoutError("Client did not reach a native menu within 60 seconds")
            time.sleep(.2)

    def open(self):
        if self._find_client() is not None:
            return {"alreadyRunning": True, **self.ready()}
        # ClientOnly never restarts services or kills a process. All launches still go through live.ps1.
        env = dict(os.environ, HTTP_PROXY="http://127.0.0.1:1", HTTPS_PROXY="http://127.0.0.1:1",
                   ALL_PROXY="http://127.0.0.1:1", NO_PROXY="localhost,127.0.0.1,::1")
        env.pop("GGG_CONFIG_FILE_NAME", None)
        log = ROOT / "logs" / "client-control-launch.log"
        log.parent.mkdir(exist_ok=True)
        with log.open("w", encoding="utf-8") as output:
            subprocess.run(["powershell.exe", "-NoProfile", "-File", str(ROOT / "scripts/dev/live.ps1"),
                            "-ClientOnly"], cwd=ROOT, env=env, stdout=output, stderr=subprocess.STDOUT,
                           check=True, timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)
        return {"alreadyRunning":False,**self.ready()}

    def execute(self, request):
        request = normalize(request)
        command = request["command"]
        if command == "game:open":
            return self.open()
        if command == "game:status":
            return self.status()
        if command == "game:resources":
            result = self.rpc("resources")
            deadline = time.monotonic()+5
            while result is None and time.monotonic()<deadline:
                time.sleep(.05)
                result=self.rpc("resources")
            if result is None:
                raise TimeoutError("No local player Life tick within 5 seconds")
            return result
        if command == "login":
            account, password = request.get("account"), request.pop("password", None)
            if not isinstance(account, str) or not account or len(account) > 128 or "\0" in account:
                raise ValueError("account must contain 1..128 characters without NUL")
            if not isinstance(password, str) or not password or len(password) > 1024:
                raise ValueError("A nonempty password is required; use --password-stdin or a JSON request")
            digest = list(hashlib.sha256(password.encode("utf-16-le")).digest())
            del password
            current = self.ready()
            if current.get("screen") == "game":
                self.execute({"command": "character:select"})
                current = self.status()
            if current.get("loginState") != 0 or current.get("screen") != "login":
                self.rpc("logout")
                self.wait(lambda s: s.get("screen") == "login" and s.get("loginState") == 0, "login screen")
            port = int(os.environ.get("Game_LOGIN_PORT", "20493"))
            if not 1 <= port <= 65535:
                raise ValueError("Invalid local login port")
            self.rpc("connect", port=port, account=account, digest=digest)
            self.wait(lambda s: s.get("loginState") == 2 or s.get("loginSubmitted"), "local login handshake")
            self.rpc("login")
            digest[:] = [0] * 32
            result = self.wait(lambda s: s.get("loginState") == 3 or s.get("lastLoginResult") is not None,
                               "login result")
            if result.get("loginState") != 3:
                raise RuntimeError(f"Local login rejected (result {result.get('lastLoginResult')})")
            return self.wait(lambda s: s.get("screen") == "characters", "character selection")
        if command == "character:list":
            return self.rpc("characters")
        if command == "character:play":
            name = request.get("name")
            if not isinstance(name, str) or not name:
                raise ValueError("name is required")
            current = self.status()
            if current.get("screen") == "game" and current.get("character") == name:
                return {"alreadyPlaying": True, **current}
            if current.get("screen") == "game":
                self.execute({"command": "character:select"})
            self.rpc("play", name=name)
            return self.wait(lambda s: s.get("screen") == "game" and s.get("instanceState") == 3
                             and s.get("character") == name, "character entering the area", timeout=45)
        if command == "character:select":
            self.rpc("select")
            return self.wait(lambda s: s.get("screen") == "characters", "character selection")
        if command == "logout":
            if self.status().get("screen") == "game":
                self.execute({"command": "character:select"})
            self.rpc("logout")
            return self.wait(lambda s: s.get("screen") == "login" and s.get("loginState") == 0, "logout")
        if command in ("command", "chat", "tp"):
            text = tp_text(request) if command == "tp" else request.get("text")
            if command == "command" and (not isinstance(text, str) or not text.startswith("/")):
                raise ValueError("text must be a LocalExile slash command")
            packet = chat_packet(text)
            start = log_mark()
            result = {**self.rpc("chat", bytes=packet), "text": text}
            # Slash commands answer with chat lines (S→C 0x17); plain chat has no reply to wait for.
            if text.startswith("/"):
                result["replies"] = collect_replies(start, request.get("wait", 1.5))
            return result
        if command == "object:interact":
            target = int(request["id"])
            if not 0 < target <= 0xffffffff:
                raise ValueError("Object id must be a positive uint32")
            return self.rpc("interaction", bytes=list(struct.pack(">HIHHH",0x24,target,0x266,0,0x408)))
        if command == "move":
            x,y=int(request["x"]),int(request["y"])
            if not 0<=x<=0x7fffffff or not 0<=y<=0x7fffffff:
                raise ValueError("Movement cells must be nonnegative int32 values")
            return self.rpc("interaction",bytes=list(struct.pack(">HIIHHH",0x25,x,y,0x2909,0,0x408)))
        if command == "npc:option":
            index = int(request["index"])
            if not 0 <= index <= 255:
                raise ValueError("NPC option must be a byte")
            return self.rpc("interaction", bytes=[0,0x82,index])
        if command == "npc:close":
            return self.rpc("interaction", bytes=[0,0x85])
        if command == "reward:claim":
            item, x, y = int(request["id"]), int(request["x"]), int(request["y"])
            if not 0 < item <= 0xffffffff or not 0 <= x < 12 or not 0 <= y < 5:
                raise ValueError("Invalid reward item id or main-inventory cell")
            return self.rpc("interaction", bytes=list(struct.pack(">HIIBBBBB",0x88,item,1,x,y,0,0,0)))
        if command == "inventory:pickup":
            inventory, item = int(request["inventoryId"]), int(request["id"])
            return self.rpc("interaction",bytes=list(struct.pack(">HIIBBB",0x28,inventory,item,0,0,0)))
        if command == "inventory:place":
            inventory, x, y = int(request["inventoryId"]), int(request.get("x",0)), int(request.get("y",0))
            return self.rpc("interaction",bytes=list(struct.pack(">HIIIBBBB",0x2a,inventory,x,y,0,3,0,0)))
        if command == "flask:use":
            packet, slot = flask_packet(request)
            return {**self.rpc("interaction", bytes=packet), "slot": slot}
        raise ValueError(f"Unknown command: {command}")


def run_request(control, request):
    started = time.monotonic()
    try:
        result = {"ok": True, "result": control.execute(request)}
    except Exception as error:
        result = {"ok": False, "error": str(error)}
    result["elapsedMs"] = round((time.monotonic() - started) * 1000)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", nargs="?", default="game:status")
    parser.add_argument("text", nargs="?", help="slash command, chat text, or tp target")
    parser.add_argument("--password-stdin", action="store_true", help="read one password line from stdin")
    parser.add_argument("--stdio", action="store_true", help="persistent JSON-lines RPC on stdin/stdout")
    options = parser.parse_args()
    os.chdir(ROOT)
    with ClientControl() as control:
        if options.stdio:
            for line in sys.stdin:
                try:
                    request = json.loads(line)
                    result = run_request(control, request)
                except (ValueError, TypeError) as error:
                    result = {"ok": False, "error": str(error)}
                print(json.dumps(result, ensure_ascii=True), flush=True)
            return 0
        request = normalize(options.command)
        if options.text is not None:
            request["text"] = options.text
        if request["command"] == "flask:use" and options.text is not None:
            request["key"] = int(request.pop("text"))
        if request["command"] == "tp" and options.text is not None:
            parts = options.text.split()
            if len(parts) == 2 and all(re.fullmatch(r"[+-]?\d+", v) for v in parts):
                request.pop("text")
                if any(v[0] in "+-" for v in parts):
                    request.update(dx=int(parts[0]), dy=int(parts[1]))
                else:
                    request.update(x=int(parts[0]), y=int(parts[1]))
        if request["command"] == "login":
            request["password"] = sys.stdin.readline().rstrip("\r\n") if options.password_stdin else getpass.getpass("Local password: ")
        result = run_request(control, request)
        print(json.dumps(result, ensure_ascii=True), flush=True)
        return 0 if result["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
