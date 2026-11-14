"""Fast, structured control of the local 3.29.3c client through its native functions.

    .venv/Scripts/python.exe scripts/dev/client_control.py game:status
    .venv/Scripts/python.exe scripts/dev/client_control.py game:open
    .venv/Scripts/python.exe scripts/dev/client_control.py --session a1 game:open
    .venv/Scripts/python.exe scripts/dev/client_control.py login:offline --password-stdin
    .venv/Scripts/python.exe scripts/dev/client_control.py login:saved
    .venv/Scripts/python.exe scripts/dev/client_control.py character:list
    .venv/Scripts/python.exe scripts/dev/client_control.py character:play:OfflineWitch
    .venv/Scripts/python.exe scripts/dev/client_control.py command "/hit physical 100"
    .venv/Scripts/python.exe scripts/dev/client_control.py chat "#hello"
    .venv/Scripts/python.exe scripts/dev/client_control.py tp "RhoaChest 2"
    .venv/Scripts/python.exe scripts/dev/client_control.py flask:use 1
    .venv/Scripts/python.exe scripts/dev/client_control.py passive:allocate 202
    .venv/Scripts/python.exe scripts/dev/client_control.py passive:refund 202
    echo '{"command":"waypoint:travel","area":"0x7ad2"}' | .venv/Scripts/python.exe scripts/dev/client_control.py --stdio
    .venv/Scripts/python.exe scripts/dev/client_control.py game:frames
    .venv/Scripts/python.exe scripts/dev/client_control.py character:delete CampaignWitch
    .venv/Scripts/python.exe scripts/dev/client_control.py waypoint:travel 1_1_town
    .venv/Scripts/python.exe scripts/dev/client_control.py area:wait 1_1_2
    .venv/Scripts/python.exe scripts/dev/client_control.py item:pickup 1027
    echo '{"command":"character:create","name":"CampaignWitch","class":"Witch"}' | .venv/Scripts/python.exe scripts/dev/client_control.py --stdio
    echo '{"command":"inventory:pickup","inventoryId":1,"id":1027}' | .venv/Scripts/python.exe scripts/dev/client_control.py --stdio
    echo '{"command":"act","kind":"walkTo","x":512,"y":740}' | .venv/Scripts/python.exe scripts/dev/client_control.py --stdio
    echo '{"command":"act","kind":"useSkill","target":8000001,"slot":0}' | .venv/Scripts/python.exe scripts/dev/client_control.py --stdio
    echo '{"command":"act","kind":"loot","object":65536}' | .venv/Scripts/python.exe scripts/dev/client_control.py --stdio

`act` is the primitive that moves the character the way a person does: a click into the client's own window, aimed
from the client's own answers (the local player's cell, and the cell the client itself resolved the click to), never a
walk packet written by the bridge. It exists for an outside operator (Agents/docs/dev/ai-operator.md), not for tests:
the `move` command below writes the C→S walk packet itself, so the client predicts nothing.

`act kind=walkTo` walks; `useSkill`, `loot` and `interact` put the cursor on an object the client can see and press
the button or bar key that acts on it (`slot` 0 is the right button, 1-5 are the bar's own keys). Every answer says
what the client's pick named and which order it sent, so a caller can tell "the client acted" from "nothing happened".

Use --stdio for persistent JSON-lines RPC (one request/result per line). Each request is
{"command":"character:play","name":"OfflineWitch"}; login takes account/password fields.
The transport writes no credentials or tokens to disk. It attaches only to this checkout's
copied Game.patched.exe; it never starts an official/Steam client or changes save files.

--session NAME (or Game_CLIENT_SESSION) drives one of several clients of this checkout's stack: the one
`live.ps1 -ClientOnly -Session NAME` started and recorded in data/clients/NAME.json. Without it, the client as before.
Ports come from data/stack.json (live.ps1 -PortBase), else Game_LOGIN_PORT, else the defaults.
"""
import argparse
import ctypes
from ctypes import wintypes
import getpass
import hashlib
import json
import math
import os
from pathlib import Path
import re
import subprocess
import struct
import sys
import threading
import time

import frida

import clientdb

ROOT = Path(__file__).resolve().parents[2]
CLIENT = ROOT / "Client" / "Game.patched.exe"
AGENT = Path(__file__).with_name("client-control.js")
# The input agent, loaded into the same Frida session as AGENT by the `act` command (one session, more than one script:
# two tools attached at once crash the client).
ACT = Path(__file__).with_name("client-act.js")
# live.ps1 writes both: the stack's ports when it runs on its own (-PortBase), and each client's PID record.
STACK = ROOT / "data" / "stack.json"
CLIENTS = ROOT / "data" / "clients"
DEFAULT_SESSION = "default"
DEFAULT_PORTS = {"patch": 12995, "patchWeb": 8080, "login": 20493, "control": 20495, "gateway": 6112}
# The servers' own variables, which live.ps1 also honours on the default ports.
PORT_VARIABLES = {"patch": "Game_PATCH_TCP_PORT", "login": "Game_LOGIN_PORT", "control": "Game_AUTH_CONTROL_PORT",
                  "gateway": "Game_GATEWAY_PORT"}
SESSION_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]{0,23}")


def keep_agent(session):
    """Leaves Frida's agent loaded in the client for good: a script with nothing in it outlives this session. Two tools
    attached at once crash the client inside frida-agent.dll (0xc0000409) as the agent comes and goes under them, within two
    attach and detach cycles in a fight. With the agent resident it survives. Only for the LocalExile client."""
    script = session.create_script("", name="localexile-keep-agent")
    script.load()
    script.eternalize()


def session_name(value):
    """'' and 'default' are the client as before; others are named sessions (live.ps1 -Session)."""
    if not value or value == DEFAULT_SESSION:
        return DEFAULT_SESSION
    if not SESSION_NAME.fullmatch(value):
        raise ValueError("session name: up to 24 letters, digits, - and _")
    return value


def stack_port(name, environ=os.environ, path=STACK):
    """This checkout's port `name`. live.ps1 is the one source: a stack on its own ports (-PortBase, Game_PORT_BASE) is
    written to data/stack.json with every port it gave its servers, which wins. On the default ports there is no file,
    and the servers' own variable (Game_LOGIN_PORT, ...) or the default applies, as for live.ps1."""
    try:
        ports = json.loads(path.read_text(encoding="utf-8-sig")).get("ports") or {}
        return int(ports[name])
    except (OSError, ValueError, AttributeError, KeyError, TypeError):
        pass
    value = environ.get(PORT_VARIABLES.get(name, ""), "")
    return int(value) if value else DEFAULT_PORTS[name]


def read_records(folder=CLIENTS):
    """live.ps1's client records by session: {"pid": …, "started": FILETIME (UTC)}. While it starts a client the
    record has pid 0 and the time just before the start (a pending launch)."""
    records = {}
    for file in folder.glob("*.json") if folder.is_dir() else []:
        try:
            record = json.loads(file.read_text(encoding="utf-8-sig"))
            records[file.stem] = {"pid": int(record["pid"]), "started": int(record["started"])}
        except (OSError, ValueError, KeyError, TypeError):
            continue
    return records


def filetime_now():
    return int((time.time() + 11644473600) * 10_000_000)


def same_path(a, b):
    return os.path.normcase(os.path.abspath(str(a))) == os.path.normcase(os.path.abspath(str(b)))


def launched_path(command_line):
    """The executable as a command line names it (the first argument, quoted or not)."""
    command_line = command_line.lstrip()
    if command_line.startswith('"'):
        return command_line[1:].split('"', 1)[0]
    return command_line.split(None, 1)[0] if command_line else ""


def process_info(pid):
    """(executable path as launched, creation FILETIME) of a running process, else None. Frida's process inventory can
    retain a terminated process while another recorder holds its handle. Each checkout reaches the shared client
    through its own Client junction, and only the path the process was started by tells them apart: the image name
    Windows reports (QueryFullProcessImageNameW) resolves the junction to the shared folder. That path comes from the
    process's own command line (NtQueryInformationProcess, ProcessCommandLineInformation); live.ps1 starts every
    client by its full path."""
    kernel=ctypes.WinDLL("kernel32",use_last_error=True)
    kernel.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD]
    kernel.OpenProcess.restype=wintypes.HANDLE
    kernel.GetExitCodeProcess.argtypes=[wintypes.HANDLE,ctypes.POINTER(wintypes.DWORD)]
    kernel.GetExitCodeProcess.restype=wintypes.BOOL
    kernel.GetProcessTimes.argtypes=[wintypes.HANDLE]+[ctypes.POINTER(wintypes.FILETIME)]*4
    kernel.GetProcessTimes.restype=wintypes.BOOL
    kernel.CloseHandle.argtypes=[wintypes.HANDLE]
    handle=kernel.OpenProcess(0x1000,False,pid)
    if not handle:
        return None
    try:
        code=wintypes.DWORD()
        if not kernel.GetExitCodeProcess(handle,ctypes.byref(code)) or code.value!=259:
            return None
        times=[wintypes.FILETIME() for _ in range(4)]
        if not kernel.GetProcessTimes(handle,*map(ctypes.byref,times)):
            return None
        ntdll=ctypes.WinDLL("ntdll")
        ntdll.NtQueryInformationProcess.argtypes=[wintypes.HANDLE,ctypes.c_int,ctypes.c_void_p,wintypes.ULONG,
                                                  ctypes.POINTER(wintypes.ULONG)]
        size=wintypes.ULONG(0)
        ntdll.NtQueryInformationProcess(handle,60,None,0,ctypes.byref(size))
        if not size.value:
            return None
        buffer=ctypes.create_string_buffer(size.value)
        if ntdll.NtQueryInformationProcess(handle,60,buffer,size,ctypes.byref(size))!=0:
            return None
        # UNICODE_STRING {u16 Length, u16 MaximumLength, pointer Buffer}; the text follows it in the same buffer.
        length=struct.unpack_from("<H",buffer.raw)[0]
        address=struct.unpack_from("<Q",buffer.raw,8)[0]
        text=ctypes.wstring_at(address,length//2)
        return launched_path(text), (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
    finally:
        kernel.CloseHandle(handle)


def find_client(session, pids, info=process_info, records=None, client=CLIENT, now=None):
    """The PID of the session's client among `pids` (running Game.patched.exe processes), or None. A session's client
    is the one its record names (PID and start time: a PID may be reused), started from this checkout's Client path.
    Without a record, the default session's is the one client of this checkout no record names (one an older live.ps1
    started), except one started while a launch is pending: that one is another session's, not yet recorded."""
    records = read_records() if records is None else records
    now = filetime_now() if now is None else now
    infos = {pid: info(pid) for pid in pids}
    def recorded(pid, record):
        details = infos.get(pid)
        return (record["pid"] == pid and details is not None and abs(details[1] - record["started"]) < 20_000_000  # 2 s
                and same_path(details[0], client))
    if session in records:
        return next((pid for pid in infos if recorded(pid, records[session])), None)
    if session != DEFAULT_SESSION:
        return None
    pending = [r["started"] for r in records.values() if r["pid"] == 0 and now - r["started"] < 600_000_000]  # 60 s
    candidates = [pid for pid, details in infos.items()
                  if details is not None and same_path(details[0], client)
                  and not any(recorded(pid, r) for r in records.values())
                  and not any(details[1] >= started for started in pending)]
    if len(candidates) > 1:
        raise RuntimeError("Several clients of this checkout run without a record; name one with --session")
    return candidates[0] if candidates else None


def connection_ports(pid, remote_port, table=None):
    """The local ports of `pid`'s TCP connections to `remote_port` (the client's connections to its Gateway), or None
    when the table cannot be read. MIB_TCPROW_OWNER_PID rows: state, local address, local port, remote address, remote
    port, owner; ports in network order in the low 16 bits."""
    if table is None:
        table = tcp_table()
        if table is None:
            return None
    count = struct.unpack_from("<I", table)[0]
    ports = set()
    for index in range(count):
        state, _, local, _, remote, owner = struct.unpack_from("<6I", table, 4 + 24 * index)
        swap = lambda value: ((value & 0xff) << 8) | ((value >> 8) & 0xff)
        if owner == pid and swap(remote) == remote_port and state == 5:  # ESTABLISHED
            ports.add(swap(local))
    return ports


def tcp_table():
    """GetExtendedTcpTable(AF_INET, TCP_TABLE_OWNER_PID_ALL) as bytes, or None."""
    iphlpapi = ctypes.WinDLL("iphlpapi")
    size = wintypes.DWORD(0)
    for _ in range(4):
        buffer = ctypes.create_string_buffer(size.value or 1)
        result = iphlpapi.GetExtendedTcpTable(buffer, ctypes.byref(size), False, 2, 5, 0)
        if result == 0:
            return buffer.raw
        if result != 122:  # ERROR_INSUFFICIENT_BUFFER: try again with the size it asked for
            return None
    return None


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
    elif command.startswith("login:") and command != "login:saved":
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


def stash_deposit_packet(request):
    """C->S 0xe0 (20 bytes, 18-stash.md §5): Ctrl + click on an inventory item while the stash is open: u16 tab, u32 item
    id, u8 flags (0), u8 kind (0: the personal stash), u16 token, u64 0. The server puts it in the tab's first free cell."""
    item, tab = request.get("id"), request.get("tab", 0)
    for name, value, top in (("id", item, 0xffffffff), ("tab", tab, 0xffff)):
        if isinstance(value, bool) or not isinstance(value, int) or not 0 <= value <= top or (name == "id" and value == 0):
            raise ValueError(f"stash:deposit needs an integer {name}")
    return list(struct.pack(">HHIBBHQ", 0xe0, tab, item, 0, 0, 0, 0))


def use_item_packet(request):
    """C->S 0x58 (10 bytes, 13-items.md): use an item that needs no target (a right-click: a quest book, an Orb of
    Regret), u32 inventory id (default 1, the main inventory) and u32 item id, both big-endian."""
    inventory, item = request.get("inventoryId", 1), request.get("id")
    for value in (inventory, item):
        if isinstance(value, bool) or not isinstance(value, int) or not 0 < value <= 0xffffffff:
            raise ValueError("item:use needs a positive uint32 item id (and inventoryId)")
    return list(struct.pack(">HII", 0x58, inventory, item))


def pantheon_packet(request):
    """C->S 0x1ee (3 bytes, the Pantheon panel; the later acts' Pantheon steps): choose a Pantheon god, u8 =
    PantheonPanelLayout row + 1 (the request names the 0-based `row`); the row's IsMajorGod says whether it is the major
    or the minor god."""
    row = request.get("row")
    if isinstance(row, bool) or not isinstance(row, int) or not 0 <= row < 255:
        raise ValueError("pantheon:select needs a PantheonPanelLayout row (0-254)")
    return [0x01, 0xEE, row + 1]


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


CLIENT_DB = ROOT / "data" / "client.db"


def table(name, db=None):
    """A client table's rows from client.db (clientdb.table), cached per database."""
    return clientdb.table(name, db or CLIENT_DB)


# S→C 0x13 / 0x0d results: BackendErrors rows + 1 (Agents/docs/protocol/01-login.md §7).
CREATE_ERRORS = {21: "name taken", 22: "invalid name", 23: "name too short", 24: "name too long",
                 25: "invalid class", 26: "no such league", 49: "no free character slot"}
DELETE_ERRORS = {16: "character does not exist"}


def character_name(name):
    """The Auth rule (AccountService.ValidName(name, 3, 23)): 3..23 letters, digits, '_' or '-'."""
    if not isinstance(name, str) or not 3 <= len(name.encode("utf-16-le")) // 2 <= 23:
        raise ValueError("Character name must have 3..23 characters")
    if not all(ch.isalpha() or ch.isdecimal() or ch in "_-" for ch in name):
        raise ValueError("Character name may contain only letters, digits, '_' and '-'")
    return name


def start_state(request, db=None):
    """The CharacterStartStates Id for a create request: `startState` as given, or the default (set 0, level 1)
    start state of the Characters row named `class` (Witch → IntDefault)."""
    if ("class" in request) == ("startState" in request):
        raise ValueError("character:create needs exactly one of class or startState")
    states = table("CharacterStartStates", db)
    if "startState" in request:
        value = request["startState"]
        if not isinstance(value, str) or not any(row["Id"] == value for row in states):
            raise ValueError(f"Unknown CharacterStartStates Id: {value!r}")
        return value
    name = request["class"]
    classes = table("Characters", db)
    index = next((i for i, row in enumerate(classes)
                  if isinstance(name, str) and row["Name"].lower() == name.lower()), None)
    if index is None:
        raise ValueError(f"Unknown class {name!r}; expected one of {', '.join(row['Name'] for row in classes)}")
    for row in states:
        if row["CharactersKey"] == index and row["CharacterStartStateSetKey"] == 0 and row["Level"] == 1:
            return row["Id"]
    raise ValueError(f"No default start state for class {name!r}")


def create_fields(request, db=None):
    """Validated (name, league, start state) of a character:create request."""
    league = request.get("league", "Standard")
    if not isinstance(league, str) or not 1 <= len(league) <= 64 or "\0" in league:
        raise ValueError("league must contain 1..64 characters")
    return character_name(request.get("name")), league, start_state(request, db)


def area_row(request, db=None):
    """(WorldAreas Id or None, HASH16) for a request naming the area Id (`area`) or its HASH16 (`hash`)."""
    if ("area" in request) == ("hash" in request):
        raise ValueError("waypoint:travel needs exactly one of area (WorldAreas Id) or hash")
    if "hash" in request:
        value = request["hash"]
        if isinstance(value, bool) or not isinstance(value, int) or not 0 < value <= 0xffff:
            raise ValueError("hash must be a WorldAreas HASH16 (1..65535)")
        try:
            area = next((row["Id"] for row in table("WorldAreas", db) if row["HASH16"] == value), None)
        except (OSError, KeyError):
            area = None
        return area, value
    area = request["area"]
    rows = table("WorldAreas", db) if isinstance(area, str) else []
    row = next((row for row in rows if row["Id"] == area), None)
    if row is None:
        raise ValueError(f"Unknown WorldAreas Id: {area!r}")
    return area, row["HASH16"]


def travel_packet(hash16):
    """C→S 0x4d, the World panel's pin click (16-waypoints.md §3): u16 HASH16, u32 0x2f00 (panel +0x524, live),
    u8 Ctrl (0: travel, not the instance list)."""
    return list(struct.pack(">HHIB", 0x4d, hash16, 0x2f00, 0))


def interact_packet(target, skill=0x266, ctrl=False, alt=False):
    """C→S 0x24 on an object with flags 0x408: skill 0x266 is the default left-click interaction (a ground item goes to
    the first free main-inventory cell); another skill id uses that skill on the target. `ctrl` and `alt` add the bits
    the client sets while those keys are held (0x004; 0x200, on an NPC only): an NPC's shortcuts (17-npcs.md §3)."""
    if isinstance(target, bool) or not isinstance(target, int) or not 0 < target <= 0xffffffff:
        raise ValueError("Object id must be a positive uint32")
    return list(struct.pack(">HIHHH", 0x24, target, skill, 0, 0x408 | (0x004 if ctrl else 0) | (0x200 if alt else 0)))


def lockstep_interact_packet(target, skill=0x266):
    """C→S 0x18f, what a client in lockstep sends instead of 0x24."""
    return list(struct.pack(">HIHHB", 0x18f, target, skill, 0x400, 0))


def in_area(status):
    return status.get("screen") == "game" and status.get("instanceState") == 3


def arrived(status, area, before, left):
    """Whether an area:wait is done: in an area and, when the area is readable, the requested one. With `before`
    (the status before the travel) the area must also have changed: the instance left state 3 meanwhile or the area
    Id differs; without an area to compare, only that change counts."""
    if not in_area(status):
        return False
    current = status.get("area")
    previous = before.get("area") if before else None
    changed = left or (current is not None and previous is not None and current != previous)
    if area is not None and current is not None:
        return current == area and (before is None or changed)
    return changed


GATEWAY_LOG = ROOT / "logs" / "live-gateway.log"
# Program.Commands.cs logs every reply line it sends: `[gateway] <remote> => 0x0017 chat line "<text>": <hex>`. The
# remote (127.0.0.1:<the client's local port>) tells the clients of one Gateway apart.
REPLY_LINE = re.compile(r'(?:\[gateway\] \S+:(\d+) )?=> 0x0017 chat line "(.*)": [0-9A-F]+\s*$')


def waypoint_packet(request):
    """C→S 0x4d, the World panel pin's travel (Agents/docs/protocol/16-waypoints.md §3): u16 destination HASH16 (the
    WorldAreas row's), u32 (the panel's waypoint object id; the Gateway only logs it), u8 Ctrl held. The Gateway
    accepts it for an activated waypoint (`/waypoints all` activates every one) and changes area."""
    value = request.get("area")
    area = int(value, 0) if isinstance(value, str) else value
    if isinstance(area, bool) or not isinstance(area, int) or not 0 < area <= 0xffff:
        raise ValueError("area must be a WorldAreas HASH16 (1-0xffff)")
    return list(struct.pack(">HHIB", 0x4d, area, 0x2f00, 0)), area


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


def read_replies(path, start, ports=None):
    """The chat lines the Gateway sent after byte offset `start` of its log; with `ports`, only those sent to a
    connection from one of these local ports (one client's, when several share the Gateway; none when the client has
    no connection to the Gateway). `ports` None (the TCP table could not be read): every line."""
    with path.open("rb") as log:
        log.seek(start)
        data = log.read()
    end = data.rfind(b"\n") + 1  # a line still being written is read next time
    lines = data[:end].decode("utf-8", errors="replace").splitlines()
    return [m.group(2) for m in map(REPLY_LINE.search, lines)
            if m and (ports is None or (m.group(1) is not None and int(m.group(1)) in ports))], start + end


def collect_replies(start, wait=1.5, quiet=0.25, path=GATEWAY_LOG, ports=None):
    """The Gateway's reply lines to a command just sent: read until `quiet` seconds pass without a new line after
    the first one, or `wait` seconds in total. None when there is no log to read."""
    if start is None:
        return None
    replies, offset = [], start
    deadline = time.monotonic() + max(0.0, float(wait))
    last = None
    while True:
        try:
            lines, offset = read_replies(path, offset, ports)
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
    def __init__(self, client_session=None):
        self.device = frida.get_local_device()
        self.session = self.script = self.act = None
        self.pid = None
        self.detached = False
        self.client_session = session_name(os.environ.get("Game_CLIENT_SESSION") if client_session is None else client_session)

    def close(self):
        if self.session is not None:
            try:
                self.session.detach()
            except frida.InvalidOperationError:
                pass
        self.session = self.script = self.act = None
        self.pid = None

    def __enter__(self):
        return self

    def __exit__(self, *unused):
        self.close()

    def _find_client(self):
        # Name alone is insufficient: other checkouts and sessions run clients too (find_client).
        return find_client(self.client_session,
                           [p.pid for p in self.device.enumerate_processes() if p.name.lower() == "game.patched.exe"])

    def attach(self):
        if self.script is not None and not self.detached:
            return
        self.close()
        self.pid = self._find_client()
        if self.pid is None:
            raise RuntimeError(f"Client (session {self.client_session}) is not running; use game:open after starting the local servers")
        self.session = self.device.attach(self.pid)
        keep_agent(self.session)
        self.detached = False
        self.session.on("detached", lambda *unused: setattr(self, "detached", True))
        self.script = self.session.create_script(AGENT.read_text(encoding="utf-8"), name="localexile-client-control")
        errors = []
        self.script.on("message", lambda message, data: errors.append(message.get("description", "Agent error"))
                       if message.get("type") == "error" else None)
        self.script.load()
        try:
            identity = self.script.exports_sync.identity()
            # The patched client, started from this checkout's Client path (process_info: unresolved).
            launched = process_info(self.pid)
            if (identity.get("pid") != self.pid or Path(identity["path"]).resolve() != CLIENT.resolve()
                    or launched is None or not same_path(launched[0], CLIENT)):
                raise RuntimeError("Refusing a client outside this checkout's Client directory")
        except Exception:
            self.close()
            if errors:
                raise RuntimeError(errors[0]) from None
            raise

    def rpc(self, op, **fields):
        self.attach()
        return self.script.exports_sync.command({"op": op, **fields})

    def act_rpc(self):
        """The input agent (client-act.js), loaded once into the same Frida session as client-control.js."""
        self.attach()
        if self.act is None:
            errors = []
            script = self.session.create_script(ACT.read_text(encoding="utf-8"), name="localexile-client-act")
            script.on("message", lambda message, data: errors.append(message.get("description", "Agent error"))
                      if message.get("type") == "error" else None)
            try:
                script.load()
                script.exports_sync.attach()
            except Exception:
                self.act = None
                if errors:
                    raise RuntimeError(errors[0]) from None
                raise
            self.act = script
        return self.act.exports_sync

    @staticmethod
    def act_gap(cell, goal):
        return max(abs(cell[0] - goal[0]), abs(cell[1] - goal[1]))

    @classmethod
    def act_target(cls, value):
        """The cell the client says a click would walk to right now (client-act.js reads it; no click, no walking)."""
        published = value.get("target")
        cell = (published or {}).get("cell") if isinstance(published, dict) else None
        if not cell:
            raise RuntimeError("The client is not publishing a walk target; is the character in game?")
        return (int(cell[0]), int(cell[1]))

    def act_probe(self, act, point, frames=2, timeout=1.0):
        """Point the client's cursor and read back what it resolved. Nothing is clicked and the character stands still.

        The client republishes what a click would walk to once per frame, and a session client runs at about 9 fps, so
        the read waits for frames to pass instead of sleeping a guess.
        """
        before = act.state().get("frame", 0)
        act.cursor(int(point[0]), int(point[1]))
        deadline, value = time.monotonic() + timeout, None
        while time.monotonic() < deadline:
            value = act.state()
            if value.get("frame", 0) - before >= frames:
                break
            time.sleep(.01)
        return value if value is not None else act.state()

    def act_aim(self, act, goal, viewport, margin=12, tries=8, reach=24, step=12, limit=48):
        """Where to put the cursor so a click walks toward `goal`, from the client's own answers.

        The client publishes the cell a click would walk to, but that mapping is only linear near the character: a
        click saturates at about 17 cells (a cursor radius scaled by 150 world units plus 30), and beyond that the
        client resolves a circle, not a plane. So the response is measured *locally*, around the cursor, every
        iteration, and every read is checked against the character's own cell: a resolved cell farther than a click
        can reach is a misread (a stale frame, or the cursor over the interface), never a target to chase. The cursor
        is stepped with a damped Newton step capped at `limit` pixels. Returns (point, cell the client resolved for
        it).
        """
        far = (max(margin, viewport[0] - margin - 1), max(margin, viewport[1] - margin - 1))

        def place(p):
            return (min(max(int(round(p[0])), margin), far[0]), min(max(int(round(p[1])), margin), far[1]))

        def read(p):
            """(character cell, resolved cell) for cursor `p`: one frame's read, no clicking."""
            state = self.act_probe(act, p)
            player = state.get("cell")
            return (int(player[0]), int(player[1])) if player else None, self.act_target(state)

        point = place((viewport[0] / 2, viewport[1] / 2))
        player, cell = read(point)
        best = (self.act_gap(cell, goal), point, cell)
        for unused in range(tries):
            if self.act_gap(cell, goal) <= 1:
                break
            _, right = read(place((point[0] + step, point[1])))
            _, down = read(place((point[0], point[1] + step)))
            j = ((right[0] - cell[0]) / step, (down[0] - cell[0]) / step,
                 (right[1] - cell[1]) / step, (down[1] - cell[1]) / step)
            det = j[0] * j[3] - j[1] * j[2]
            if abs(det) < 1e-6:
                break
            dx, dy = goal[0] - cell[0], goal[1] - cell[1]
            offset = ((j[3] * dx - j[1] * dy) / det, (j[0] * dy - j[2] * dx) / det)
            length = math.hypot(*offset)
            if length < 1.0:
                break
            if length > limit:
                offset = (offset[0] * limit / length, offset[1] * limit / length)
            moved = place((point[0] + offset[0], point[1] + offset[1]))
            if moved == point:
                break
            player, cell = read(moved)
            gap = self.act_gap(cell, goal)
            if gap < best[0]:
                best = (gap, moved, cell)
            elif gap > best[0] + 4:
                break                                       # the step took us away from it: keep the best
            point = moved
        # The click is built from the cell the client published on its last frame, so leave the cursor on the winning
        # point and let a frame publish it before anything is pressed.
        return best[1], self.act_target(self.act_probe(act, best[1]))

    def act_step(self, act, before, timeout=2.5):
        """After the button: wait for the client's own walk to end (the cell moved and the player stopped)."""
        deadline, value = time.monotonic() + timeout, act.state()
        while time.monotonic() < deadline:
            time.sleep(.05)
            value = act.state()
            moved = value.get("cell")
            if moved and tuple(int(v) for v in moved) != before and value.get("moving") is False:
                return value
        return value

    @staticmethod
    def act_result(reached, cell, goal, clicks, predicted, order, started, reason=None):
        result = {"reached": reached, "cell": [int(v) for v in cell], "target": [int(v) for v in goal],
                  "clicks": clicks, "predicted": predicted, "order": order,
                  "elapsedMs": round((time.monotonic() - started) * 1000)}
        if reason is not None:
            result["reason"] = reason
        return result

    def act_walk_to(self, x, y, tolerance=1, timeout=25.0, clicks=30):
        """Walk the character to cell (x, y) the way a person does: aim, click, look, and click again.

        The client resolves the click with its own cursor maths and its own pathfinder, so the order that reaches the
        server is the client's own; the bridge writes no walk packet and never teleports. A click reaches about a
        screen radius, so a far cell takes several clicks. The answer is a verdict rather than an exception:
        {"reached": True, cell, target, clicks, predicted, order, elapsedMs}, or reached False with a short `reason`
        ("blocked: …", "gave up …") that an operator can act on. `predicted` is the cell the client resolved while
        aiming and `order` is the cell it resolved when the button went down (null if it never spoke).
        """
        act = self.act_rpc()
        started, used, stalled, previous = time.monotonic(), 0, 0, None
        deadline = started + float(timeout)
        goal = (int(x), int(y))
        predicted = order = None
        while True:
            value = act.state()
            cell = value.get("cell")
            if cell is None:
                raise RuntimeError("The client has no local player position; is the character in game?")
            cell = (int(cell[0]), int(cell[1]))
            gap = self.act_gap(cell, goal)
            if gap <= tolerance:
                return self.act_result(True, cell, goal, used, predicted, order, started)
            # A click that leaves the character where it stood is the honest sign of a blocked walk: three in a row
            # and the caller gets the reason instead of a silent retry loop.
            stalled = stalled + 1 if used and cell == previous else 0
            previous = cell
            if stalled >= 3:
                return self.act_result(False, cell, goal, used, predicted, order, started,
                                       f"blocked: {gap} cells short of {goal} after {used} clicks")
            if used >= clicks or time.monotonic() >= deadline:
                return self.act_result(False, cell, goal, used, predicted, order, started,
                                       f"gave up {gap} cells short of {goal} after {used} clicks")
            viewport = tuple(int(v) for v in (value.get("viewport") or (0, 0)))
            if not all(viewport):
                raise RuntimeError("The client reported no window size")
            before = value.get("clicks", 0)
            # One click walks about 15 cells at most: the reach is min(|cursor|,1)*150+30 world units of direction, so
            # aiming straight at a far cell only saturates. Aim at a waypoint inside one reach, then click, look, and
            # aim again -- the same thing a person does with a distant target.
            step_cells = min(15.0, math.hypot(goal[0] - cell[0], goal[1] - cell[1]))
            span = math.hypot(goal[0] - cell[0], goal[1] - cell[1])
            waypoint = (int(round(cell[0] + (goal[0] - cell[0]) * step_cells / span)),
                        int(round(cell[1] + (goal[1] - cell[1]) * step_cells / span)))
            point, resolved = self.act_aim(act, waypoint, viewport)
            predicted = [int(v) for v in resolved]
            # A read taken while the character is still moving is a frame behind, so accept a resolution that points
            # the right way rather than demanding the exact cell: the click only needs a direction, the client caps
            # the distance itself.
            aim = (resolved[0] - cell[0], resolved[1] - cell[1])
            want = (goal[0] - cell[0], goal[1] - cell[1])
            dot = aim[0] * want[0] + aim[1] * want[1]
            reach = math.hypot(*aim)
            if self.act_gap(waypoint, resolved) > 10 and not (dot > 0 and reach > 5):
                return self.act_result(False, cell, goal, used, predicted, None, started,
                                       f"aim misread: the client resolved {predicted} for a waypoint {list(waypoint)} "
                                       f"one click away from {list(cell)}, so the cursor is not reading the world "
                                       "(a stale frame, or the pointer is over the interface); nothing was pressed")
            act.click(int(point[0]), int(point[1]))
            used += 1
            value = self.act_step(act, cell)
            if value.get("clicks", 0) > before and value.get("click"):
                order = [int(v) for v in value["click"]]

    ACT_SLOTS = {0: None, 1: 0x51, 2: 0x57, 3: 0x45, 4: 0x52, 5: 0x54}

    def act_objects(self, radius=60):
        """What the client's label pass has seen recently: {world, cell, screen} per object inside `radius` cells."""
        return self.act_rpc().objects(int(radius))

    def act_radius_for(self, cell):
        """How far the client's object readback has to reach to include `cell`, in cells."""
        try:
            here = self.act_rpc().state().get("cell")
        except Exception:
            return 80
        if not here or not cell:
            return 80
        return max(24, min(400, self.act_gap((int(here[0]), int(here[1])), (int(cell[0]), int(cell[1]))) + 12))

    def act_drawn_point(self, act, object_id, cell=None, agree=2):
        """The pixel the client draws an object at, and the pixel of its floating label when it has one.

        The client projects every object it has been told about with its own camera (`client-act.js` objects()), so a
        click meant for a monster or a ground item can be aimed at the thing itself rather than at the cell under it
        and a blind lift above it. A ground item and an NPC answer only when the cursor is on their label, which the
        client draws separately and names with no id of its own: it is matched to the object by its cell.

        The row is only used when the client's cell for the object agrees with the one the caller was given
        (`agree` cells): a monster walks, so a row older than the caller's snapshot points where the monster *was*,
        and a click there is a move in the wrong direction — measured live: a stale row sent the character 68 cells
        away from the target it was asked to attack.

        Returns (body pixel, label pixel, the client's row for the object). Any of them is None when the client has
        not seen the object, or has not seen it lately — it is not streamed in yet, it is behind the camera, or it
        has moved — and the caller then aims at the cell, which is what this did before.
        """
        if not object_id:
            return None, None, None
        try:
            rows = self.act_objects(radius=self.act_radius_for(cell))
        except Exception:
            return None, None, None                    # no readback is not an error: the cell aim still works
        mine = next((row for row in rows if int(row.get("id") or 0) == int(object_id)), None)
        if mine is None:
            return None, None, None
        at = mine.get("cell") or cell
        if cell and at and self.act_gap((int(at[0]), int(at[1])), (int(cell[0]), int(cell[1]))) > agree:
            return None, None, mine                     # the client's row is stale: it names another place
        label = None
        for row in rows:
            if int(row.get("id") or 0) != 0 or not row.get("screen") or not at:
                continue
            row_cell = row.get("cell")
            if row_cell and list(row_cell) == list(at):
                label = row["screen"]
                break
        return mine.get("screen"), label, mine

    def act_wait_frames(self, act, frames=3, timeout=1.5):
        """Wait for the client to draw frames, so a press and the order it produced are both visible."""
        before, deadline, value = act.state().get("frame", 0), time.monotonic() + timeout, act.state()
        while time.monotonic() < deadline:
            value = act.state()
            if value.get("frame", 0) - before >= int(frames):
                break
            time.sleep(.01)
        return value

    def act_point_at(self, act, cell, viewport, step=26):
        """Aim the cursor so the client's own readback lands on `cell`.

        The client publishes the cell under the cursor (its own conversion, so no calibration constant is baked in
        here). `act_aim` measures the cursor-to-cell response on the fly and inverts it; when the exact cell cannot be
        hit (rounding at a cell edge) the nearest one is used and reported, never assumed.
        """
        goal = (int(cell[0]), int(cell[1]))
        point, resolved = self.act_aim(act, goal, viewport)
        return point, resolved

    BAR_KEYS = (None, 0x51, 0x57, 0x45, 0x52, 0x54)   # 0 is the left button; 1-5 are the bar's Q W E R T

    def act_move_cursor(self, act, point, frames=2):
        """Put the cursor on a pixel and let the client draw a frame there before anything is pressed.

        The client picks what is under the cursor once per frame, so a `click` (move, down and up inside one frame)
        presses on whatever was under the cursor *before* it moved. Measured live on Hillock: aiming exactly at his
        cell and clicking in one call sent `UseSkillAtLocation` at his cell with no object — the client had not looked
        at the cursor's new place yet. Split into move, frame, press, the same pixel is what the client picks.
        """
        act.cursor(int(point[0]), int(point[1]))
        self.act_wait_frames(act, frames)

    def act_press(self, act, point, button="left", key=None, slot=None, frames=3):
        """Press what fires the skill: a bar key, the right button (the bar's second skill), or the left button.

        The game's own layout: the left button is move and the default attack, the right button is the other skill.
        `slot` names a bar slot instead of a bare key — 0 is the left button, 1-5 are Q/W/E/R/T — which is the only
        way to fire anything but the left button's own skill. Measured live: this character's left button sends
        `UseSkillAtLocation { SkillId = 10505 }` ("Move") whatever it is aimed at, and its bar has slot 2 = Fireball,
        so a left click was a walk however well it was aimed.

        Every press moves the cursor first and waits a frame, so the client's pick sees the cursor where it is
        (`act_move_cursor`), and holds the button across frames because the client reads it per frame.
        """
        if key is not None:
            self.act_move_cursor(act, point)
            act.keyDown(int(key))
            self.act_wait_frames(act, 2)
            act.keyUp(int(key))
            return "key"
        if slot is not None and int(slot) != 0:
            self.act_move_cursor(act, point)
            code = self.BAR_KEYS[int(slot)]
            act.keyDown(code)
            self.act_wait_frames(act, 2)
            act.keyUp(code)
            return f"slot {int(slot)}"
        if button == "right":
            # `skill` moves the cursor and presses in one call; the move is to the same pixel the cursor is already on,
            # so the client's pick has had its frame (act_move_cursor) and the press lands on what it picked.
            self.act_move_cursor(act, point)
            act.skill(int(point[0]), int(point[1]))
            return "right"
        self.act_move_cursor(act, point)
        act.down()
        self.act_wait_frames(act, max(1, int(frames) - 1))
        act.up()
        return "left"

    @staticmethod
    def act_action_result(ok, kind, started, reason=None, **fields):
        result = {"ok": bool(ok), "kind": kind, "elapsedMs": round((time.monotonic() - started) * 1000)}
        if reason is not None:
            result["reason"] = reason
        result.update(fields)
        return result

    def act_object_act(self, kind, cell, object_id=None, button="left", key=None, slot=None, frames=3, rise=12):
        """One primitive aimed at an object: loot, interact, or a skill used on whatever stands there.

        The result is the order the client sent (`order.cell`, `order.object`, `order.walk`, `order.from`). Nothing is
        written to the socket and nothing is teleported.

        Aiming happens in the order a person would try it, and every point comes from the client's own readback:
        the pixel it draws the object at, then the pixel of its floating label (a ground item and an NPC answer only
        on their label), and only then the cell under it, pressed and lifted a few pixels at a time. The client's
        object pass is what makes the first two possible; without it (an object it has not streamed in yet) the cell
        aim is exactly what this did before.
        """
        act = self.act_rpc()
        started = time.monotonic()
        state = act.state()
        viewport = state.get("viewport") or [800, 600]
        point, resolved = self.act_point_at(act, cell, viewport)
        if point is None:
            return self.act_action_result(False, kind, started,
                                          "the client did not resolve a cell under the cursor; is the character in game?",
                                          cell=list(cell))
        body, label, seen = self.act_drawn_point(act, object_id, cell)
        spots = []                                      # (how, point), tried in this order
        if body:
            spots.append(("drawn", (int(body[0]), int(body[1]))))
        if label:
            spots.append(("label", (int(label[0]), int(label[1]))))
        spots += [("cell" if not lift else f"cell+{lift}px", (int(point[0]), int(point[1]) - int(lift)))
                  for lift in [0] + [int(rise) * step for step in (1, 2, 3, 4, -1)]]

        attempts, sent, order, pressed, hows = 0, False, {}, tuple(point), []
        pressed_how = None
        for how, spot in spots:
            before = act.state()
            pressed_how = self.act_press(act, spot, button=button, key=key, slot=slot)
            after = self.act_wait_frames(act, frames)
            sent = after.get("clicks", 0) > before.get("clicks", 0)
            order = {"cell": after.get("click"), "object": int(after.get("clickObject", 0) or 0),
                     "walk": bool(after.get("clickWalk")), "from": after.get("clickFrom")}
            attempts += 1
            pressed, hows = spot, hows + [how]
            if not sent:
                continue
            # The click has to name the thing that was asked for. Any object-form order would do when the caller named
            # no id, but with an id in hand the wrong object is a miss, not a hit: keep trying the next point.
            if order["object"] and (not object_id or order["object"] == int(object_id)):
                break
            # A click resolves within about sixteen cells of the character. An order that named a cell far beyond
            # that is not a near miss to be lifted away from: it is the client aiming somewhere else entirely, and
            # pressing again only asks it to move there. Stop and say so.
            if order["cell"] and self.act_gap(tuple(int(v) for v in order["cell"]), cell) > 6:
                break
        answer = self.act_action_result(
            sent, kind, started,
            None if sent else f"the client sent no order at {pressed}; the click landed on nothing",
            cell=list(cell), resolved=list(resolved) if resolved else None, target=object_id,
            pressed=list(pressed), attempts=attempts, how=pressed_how, tried=hows, sent=sent, order=order)
        hit = bool(order["object"]) and (not object_id or order["object"] == int(object_id))
        if sent and order["object"] and not hit:
            answer["ok"] = False
            answer["reason"] = (f"the click named object {order['object']} instead of {int(object_id)}: something "
                                "else is drawn over it — move away from it and try again")
        # A click reaches about 16 cells (the client clamps its aim to a screen's worth of ground), so a resolved cell
        # far from the target means the cursor is pointing at ground between here and there, not at the thing itself.
        elif resolved and not hit and self.act_gap(resolved, cell) > 1:
            answer["ok"] = False
            answer["reason"] = (f"the closest the cursor reaches is {resolved}, {self.act_gap(resolved, cell)} cells "
                                f"short of {list(cell)}; a click reaches about 16 cells, so walk closer first")
        elif sent and not hit:
            answer["ok"] = False
            answer["reason"] = (f"the click landed on the ground instead of on an object ({kind}); if this was a "
                                "monster, its body was not under the cursor, and the order that went out was a move")
        return answer

    def screen(self, out=None, title="Path of Exile", timeout=6.0):
        """Save the client's own frame to a PNG and say where it is, without touching the focus or the window.

        Windows Graphics Capture asks DWM for the window's composited surface, which is right even while another
        window covers the game. The optional `windows_capture` package is what talks to it; without it this says so
        and nothing else changes.
        """
        try:
            from windows_capture import WindowsCapture
        except Exception as exc:                       # noqa: BLE001 - an optional extra, reported as a sentence
            raise RuntimeError("game:screen needs the windows_capture package "
                               f"(pip install windows-capture): {exc}") from exc
        out = os.path.abspath(out or os.path.join("logs", "client-screen.png"))
        os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
        done, failure = threading.Event(), []
        # A machine can have more than one client open and they all share the title "Path of Exile", so the capture
        # would sometimes return the *other* one's picture. This client's own window is found by pid and its title is
        # set to a name only it has, for the length of the capture.
        title, restore = self._own_window_title(title)

        capture = WindowsCapture(cursor_capture=False, draw_border=None, window_name=title)

        @capture.event
        def on_frame_arrived(frame, control):
            try:
                frame.save_as_image(out)
            except Exception as exc:                   # noqa: BLE001 - a callback may not raise
                failure.append(f"{type(exc).__name__}: {exc}")
            finally:
                done.set()
                control.stop()

        @capture.event
        def on_closed():
            done.set()

        worker = threading.Thread(target=capture.start, daemon=True)
        worker.start()
        if not done.wait(timeout):
            raise RuntimeError(f"the client drew no frame within {timeout:g} s (a minimised window never does)")
        worker.join(2)
        if failure:
            raise RuntimeError(f"the frame could not be saved: {failure[0]}")
        if not os.path.exists(out):
            raise RuntimeError("the capture produced no image")
        if restore is not None:
            self._set_window_title(restore[0], restore[1])
        return {"path": out, "bytes": os.path.getsize(out), "window": title}

    def _windows_for_pid(self, pid):
        """Every visible top-level window of a process, as (hwnd, title) — one client, two windows, no guessing."""
        import ctypes
        from ctypes import wintypes
        user32 = ctypes.windll.user32
        found = []

        @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
        def each(hwnd, _):
            owner = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
            if owner.value != int(pid) or not user32.IsWindowVisible(hwnd):
                return True
            size = user32.GetWindowTextLengthW(hwnd)
            if not size:
                return True
            buffer = ctypes.create_unicode_buffer(size + 1)
            user32.GetWindowTextW(hwnd, buffer, size + 1)
            found.append((hwnd, buffer.value))
            return True

        user32.EnumWindows(each, 0)
        return found

    def _set_window_title(self, hwnd, title):
        import ctypes
        ctypes.windll.user32.SetWindowTextW(hwnd, ctypes.c_wchar_p(title))

    def _own_window_title(self, title):
        """Give this client's window a title no other window has, and return (title, (hwnd, old title) or None)."""
        windows = [row for row in self._windows_for_pid(self.pid) if row[1] == title]
        if len({row[1] for row in self._windows_for_pid(self.pid)}) == 0:
            return title, None
        if len(windows) != 1:
            return title, None
        hwnd, old = windows[0]
        unique = f"{title} ({os.getpid()}-{int(hwnd)})"
        self._set_window_title(hwnd, unique)
        time.sleep(0.2)
        return unique, (hwnd, old)

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

    def characters(self):
        return self.rpc("characters")["characters"]

    def area_wait(self, area=None, before=None, timeout=60):
        """Poll until `arrived` (see there); `left` records the instance leaving state 3 during the wait."""
        deadline, left = time.monotonic() + timeout, False
        while True:
            value = self.status()
            left = left or not in_area(value)
            if arrived(value, area, before, left):
                return value
            if time.monotonic() >= deadline:
                raise TimeoutError(f"Timed out waiting for area {area or 'change'}; "
                                   "inspect game:status and server logs")
            time.sleep(0.05)

    def create(self, request):
        name, league, state = create_fields(request)
        if self.status().get("screen") != "characters":
            raise RuntimeError("character:create requires the character selection screen")
        if any(c["name"].lower() == name.lower() for c in self.characters()):
            raise RuntimeError(f"Character {name} is already in the client list")
        self.rpc("create", name=name, league=league, startState=state)
        # Auth sends the new 0x15 list before 0x13 result 0; a rejection is 0x13 alone.
        deadline = time.monotonic() + float(request.get("timeout", 15))
        while True:
            result = self.status().get("lastCreateResult")
            if result not in (None, 0):
                reason = CREATE_ERRORS.get(result, "unknown")
                raise RuntimeError(f"Character creation rejected (result {result}: {reason})")
            created = next((c for c in self.characters() if c["name"] == name), None)
            if created is not None:
                return {"character": created, "startState": state, "result": result}
            if time.monotonic() >= deadline:
                raise TimeoutError("Timed out waiting for the new character in the client list; inspect the Auth log")
            time.sleep(0.05)

    def delete(self, request):
        name = request.get("name")
        if not isinstance(name, str) or not 1 <= len(name) <= 64:
            raise ValueError("name is required")
        if self.status().get("screen") != "characters":
            raise RuntimeError("character:delete requires the character selection screen")
        # Auth matches the name case-insensitively; send the listed spelling and wait for it to go.
        entry = next((c for c in self.characters() if c["name"].lower() == name.lower()), None)
        if entry is None:
            if request.get("missingOk"):
                return {"deleted": False, "name": name}
            raise RuntimeError(f"Character {name} is not in the client list")
        self.rpc("delete", name=entry["name"])
        deadline = time.monotonic() + float(request.get("timeout", 15))
        while True:
            result = self.status().get("lastDeleteResult")
            if result not in (None, 0):
                reason = DELETE_ERRORS.get(result, "unknown")
                raise RuntimeError(f"Character deletion rejected (result {result}: {reason})")
            if all(c["name"] != entry["name"] for c in self.characters()):
                return {"deleted": True, "character": entry, "result": result}
            if time.monotonic() >= deadline:
                raise TimeoutError("Timed out waiting for the character to leave the client list; inspect the Auth log")
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
        # ClientOnly never restarts services or kills a process. All launches still go through live.ps1. Its worst case:
        # the gateway-list read and write (a GGPK open each), the Python/Frida check and up to 60 s for a session
        # launcher (whose own wait for the client's run-mutex rename is 30 s); 240 s leaves room for a slow disk.
        env = dict(os.environ, HTTP_PROXY="http://127.0.0.1:1", HTTPS_PROXY="http://127.0.0.1:1",
                   ALL_PROXY="http://127.0.0.1:1", NO_PROXY="localhost,127.0.0.1,::1")
        env.pop("GGG_CONFIG_FILE_NAME", None)
        env["Game_CLIENT_SESSION"] = self.client_session
        log = ROOT / "logs" / f"client-control-launch-{self.client_session}.log"
        log.parent.mkdir(exist_ok=True)
        with log.open("w", encoding="utf-8") as output:
            subprocess.run(["powershell.exe", "-NoProfile", "-File", str(ROOT / "scripts/dev/live.ps1"),
                            "-ClientOnly", "-Session", self.client_session], cwd=ROOT, env=env, stdout=output,
                           stderr=subprocess.STDOUT, check=True, timeout=240, creationflags=subprocess.CREATE_NO_WINDOW)
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
            port = stack_port("login")
            if not 1 <= port <= 65535:
                raise ValueError("Invalid local login port")
            self.rpc("connect", port=port, account=account, digest=digest)
            self.wait(lambda s: s.get("loginState") == 2 or s.get("loginSubmitted"), "local login handshake")
            self.rpc("login")
            digest[:] = [0] * 32
            # Success can arrive before the client commits authenticated state on its next frame.
            result = self.wait(lambda s: s.get("loginState") == 3 or s.get("lastLoginResult") not in (None, 0),
                               "login result")
            if result.get("loginState") != 3:
                raise RuntimeError(f"Local login rejected (result {result.get('lastLoginResult')})")
            return self.wait(lambda s: s.get("screen") == "characters", "character selection")
        if command == "login:saved":
            # The client's own remembered login (Remember Password): no password goes through this bridge.
            current = self.ready()
            if current.get("screen") == "game":
                self.execute({"command": "character:select"})
                current = self.status()
            if current.get("loginState") != 0 or current.get("screen") != "login":
                self.rpc("logout")
                self.wait(lambda s: s.get("screen") == "login" and s.get("loginState") == 0, "login screen")
            self.rpc("connectSaved", port=stack_port("login"))
            result = self.wait(lambda s: s.get("loginState") == 3 or s.get("lastLoginResult") is not None, "login result")
            if result.get("loginState") != 3:
                raise RuntimeError(f"Saved login rejected (result {result.get('lastLoginResult')})")
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
        if command == "character:create":
            return self.create(request)
        if command == "character:delete":
            return self.delete(request)
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
            # Slash commands answer with chat lines (S→C 0x17); plain chat has no reply to wait for. Only the lines
            # sent to this client's connection: other sessions may play on the same Gateway.
            if text.startswith("/"):
                ports = connection_ports(self.pid, stack_port("gateway"))
                result["replies"] = collect_replies(start, request.get("wait", 1.5), ports=ports)
            return result
        if command == "object:interact":
            target = int(request["id"])
            if not 0 < target <= 0xffffffff:
                raise ValueError("Object id must be a positive uint32")
            # The same C->S 0x24 a click sends: 0x266 interacts; a skill id uses that skill on the target (the
            # default attack is 0x0102, 11-skills.md).
            skill = int(request.get("skill", 0x266))
            if not 0 < skill <= 0xffff:
                raise ValueError("Skill id must be a positive uint16")
            if request.get("lockstep"):
                return self.rpc("interaction", bytes=lockstep_interact_packet(target, skill))
            return self.rpc("interaction", bytes=interact_packet(target, skill, bool(request.get("ctrl")), bool(request.get("alt"))))
        if command == "item:pickup":
            return {**self.rpc("interaction", bytes=interact_packet(request.get("id"))), "id": request["id"]}
        if command == "waypoint:travel":
            # The destination as a WorldAreas Id ("1_1_town", or `hash`) or as its HASH16 (an int or "0x7ad2"); `wait`
            # returns once the new area is entered (area:wait).
            value = request.get("area")
            if "hash" in request or (isinstance(value, str) and not re.fullmatch(r"0[xX][0-9a-fA-F]+|\d+", value.strip())):
                area, hash16 = area_row(request)
                packet, reply = travel_packet(hash16), {"area": area, "hash": hash16}
            else:
                packet, hash16 = waypoint_packet(request)
                area, reply = None, {"area": hash16}
            before = self.status() if request.get("wait") else None
            result = {**self.rpc("interaction", bytes=packet), **reply}
            if request.get("wait"):
                result["status"] = self.area_wait(area, before, float(request.get("timeout", 60)))
            return result
        if command == "area:wait":
            area, before = request.get("area"), request.get("from")
            if area is not None:
                area = area_row({"area": area})[0]
            if isinstance(before, dict) and isinstance(before.get("result"), dict):
                before = before["result"]  # a whole {"ok":…, "result": status} line
            if before is not None and not isinstance(before, dict):
                raise ValueError("from must be a previous status object")
            if area is None and before is None:
                raise ValueError("area:wait needs area (WorldAreas Id) and/or from (a previous status)")
            return self.area_wait(area, before, float(request.get("timeout", 60)))
        if command == "move":
            x,y=int(request["x"]),int(request["y"])
            if not 0<=x<=0x7fffffff or not 0<=y<=0x7fffffff:
                raise ValueError("Movement cells must be nonnegative int32 values")
            return self.rpc("interaction",bytes=list(struct.pack(">HIIHHH",0x25,x,y,0x2909,0,0x408)))
        if command == "act":
            # The hands of an outside operator: primitives clicked through the client's own input, with the client's
            # own answers as the result (Agents/docs/dev/ai-operator.md).
            kind = request.get("kind")
            if kind == "walkTo":
                for field in ("x", "y"):
                    if field not in request:
                        raise ValueError(f"act walkTo needs {field} (a cell)")
                x, y = int(request["x"]), int(request["y"])
                if not -(2**31) <= x < 2**31 or not -(2**31) <= y < 2**31:
                    raise ValueError("Cell coordinates must be int32 values")
                return self.act_walk_to(x, y, tolerance=int(request.get("tolerance", 1)),
                                        timeout=float(request.get("timeout", 25)),
                                        clicks=int(request.get("clicks", 30)))
            if kind == "click":
                # A press at a pixel of the client's own window. The interface is not made of world objects: the death
                # panel's RESURRECT IN TOWN, a vendor's row, a stash tab are drawn at a place and answered by a click
                # there, so `screen` is how a caller finds the place and this is how it presses it. Same press as any
                # other (move, frame, down, frame, up), so what the client picks is where the cursor was put.
                act = self.act_rpc()
                started = time.monotonic()
                before = act.state()
                how = self.act_press(act, (int(request["x"]), int(request["y"])),
                                     button=str(request.get("button", "left")), slot=request.get("slot"))
                after = self.act_wait_frames(act, int(request.get("frames", 3)))
                return self.act_action_result(True, "click", started, None, x=int(request["x"]),
                                              y=int(request["y"]), how=how,
                                              clicks=int(after.get("clicks", 0)) - int(before.get("clicks", 0)))
            if kind in ("useSkill", "loot", "interact"):
                # The aim is a cell: this client does not expose the object ids it draws, so the caller says where the
                # thing stands (the world snapshot has the cell) and the client resolves what is there when clicked.
                if "x" not in request or "y" not in request:
                    raise ValueError(f"act {kind} needs x and y (the cell the thing stands on); "
                                     "the client does not name its objects, so an id alone cannot be aimed")
                slot, key = request.get("slot"), request.get("key")
                if key is None and slot is not None and int(slot) != 0:
                    key = self.ACT_SLOTS.get(int(slot))
                    if key is None:
                        raise ValueError(f"act useSkill slot {slot} is not on the bar; "
                                         "slots are 0 (the right button) and 1-5")
                # The game's own layout: the left button is move and the default attack, the right button is the
                # second skill. Slots 1-5 are the bar's keys.
                button = "left"
                target = request.get("target", request.get("object"))
                return self.act_object_act(kind, [int(request["x"]), int(request["y"])],
                                           None if target is None else int(target),
                                           button=str(request.get("button", button)), key=key, slot=slot,
                                           frames=int(request.get("frames", 3)))
            raise ValueError(f"Unknown act kind: {kind!r}; this bridge has walkTo, useSkill, loot and interact")
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
        if command == "stash:deposit":
            return {**self.rpc("interaction", bytes=stash_deposit_packet(request)), "id": request["id"]}
        if command == "item:use":
            return {**self.rpc("interaction", bytes=use_item_packet(request)), "id": request["id"]}
        if command == "pantheon:select":
            return {**self.rpc("interaction", bytes=pantheon_packet(request)), "row": request["row"]}
        if command == "game:frames":
            # Main-loop frames since the bridge attached and the rate since the previous call (client-control.js).
            self.attach()
            return {"pid": self.pid, **self.script.exports_sync.frames()}
        if command == "game:objects":
            # What the client's own object pass has seen recently (client-act.js objectsNow): the ids the client uses
            # for them (its own, not the server's), the cell and world position it has for each, and the screen pixel
            # it draws that at. A click aimed at an object has to land on that pixel, so the caller can ask for it
            # instead of converting a cell and hoping the body is under the cursor (Agents/docs/dev/ai-operator.md).
            self.attach()
            return {"pid": self.pid, "objects": self.act_objects(radius=float(request.get("radius", 60)))}
        if command == "game:screen":
            # The client's own frame, taken from its composited surface (Windows Graphics Capture), so it works while
            # the game is neither focused nor the top window: GDI cannot see a Direct3D frame at all (PrintWindow
            # returns a black client area) and a screen grab returns whatever is on top. The operator needs this for
            # the death panel, tooltips and anything else the world readbacks do not describe.
            self.attach()                                  # so the capture can find this client's own window by pid
            return self.screen(request.get("out"), request.get("title", "Path of Exile"),
                               float(request.get("timeout", 6)))
        if command == "flask:use":
            packet, slot = flask_packet(request)
            return {**self.rpc("interaction", bytes=packet), "slot": slot}
        if command in ("passive:allocate", "passive:refund"):
            node = int(request["node"])
            expansion = bool(request.get("expansion", True))
            if not 0 < node <= 0xffff:
                raise ValueError("Passive node must be a positive uint16")
            flags = 1 if expansion else 0
            if command == "passive:allocate":
                packet = [0x00, 0x34, 0x00, 0x00, node >> 8, node & 0xff, flags]
            else:
                packet = [0x00, 0x36, 0x00, 0x00, node >> 8, node & 0xff, flags, 0]
            return self.rpc("chat", bytes=packet)
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
    parser.add_argument("--session", help="the client session (live.ps1 -Session; default Game_CLIENT_SESSION, else the default client)")
    options = parser.parse_args()
    os.chdir(ROOT)
    with ClientControl(options.session) as control:
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
        if request["command"] in ("passive:allocate", "passive:refund") and options.text is not None:
            request["node"] = int(request.pop("text"))
        if request["command"] == "character:delete" and options.text is not None:
            request["name"] = request.pop("text")
        if request["command"] in ("waypoint:travel", "area:wait") and options.text is not None:
            request["area"] = request.pop("text")
            request.setdefault("wait", True)
        if request["command"] == "pantheon:select" and options.text is not None:
            request["row"] = int(request.pop("text"))
        if request["command"] in ("item:pickup", "item:use") and options.text is not None:
            request["id"] = int(request.pop("text"), 0)
        result = run_request(control, request)
        print(json.dumps(result, ensure_ascii=True), flush=True)
        return 0 if result["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
