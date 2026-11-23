"""km-fix.py -- make the Path of Exile 3.29.3c client build the Kingsmarch (hub-town) room
doodads that its own metadata flags as server-only.

What it changes (runtime only, nothing on disk):

    type+0x2a8  (u32 state of a doodad type object)
        0 = client may create,  1 = client may create,  2 = server_only -> the client drops
            the .arm room line instead of creating it.
    The state is written once per type object, the first time such a type is seen in a room
    whose .arm path matches --room-regex, and only while that state is 2.  Re-running the
    script (or re-entering the area) is a no-op: the types are already 1.

How it selects the types: it does *not* guess with a hash.  A type object does not expose its
Id path (see NOTES at the bottom), and the register entries are hash-only, so the exact
selection is "the type objects this room actually instantiates", taken from the room-line
loop itself: for every dropped line in a Settlers room we patch its type and then let the
line through.  Every other area keeps vanilla behaviour because the gate never matches there.

Usage:
    python kingsmarch-doodads.py --name Game.patched.exe --seconds 0        (attach, run until Ctrl+C)
    python kingsmarch-doodads.py --pid 1234 --seconds 120
    python kingsmarch-doodads.py --record data/clients/default.json          (live.ps1 -Doodads runs this)
    python kingsmarch-doodads.py --name Game.patched.exe --dry-run         (report, change nothing)

Output: one JSON line per event (room change, summary every 5 s, final summary).
"""

import argparse
import json
import sys
import threading
import time

import frida

try:                                    # the console may be GBK; paths and payloads are not
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

SCRIPT = r"""
// ---------------------------------------------------------------------------------------
// Room-doodad predicate hook (RVA 0x152f9a0) + room-path capture (RVA 0x2046fc0).
//
// 0x142046fc0 = the per-room "create the room's doodads" loop.  Its 3rd argument (r8) is the
// room data holder; *(r8) is a Ptr to the room's .arm path std::wstring.  The loop runs once
// per room and always before that room's lines are judged.
// 0x14152f9a0 = the per-line predicate reached through manager vtable +0x98; it returns a
// bool in AL only (upper bits of RAX are stale): AL=1 -> create the line, AL=0 -> drop it.
// Its RDX is &row+0x30; *(*(rdx)) + 0x28 is the doodad *type* object holding the u32 state.
// ---------------------------------------------------------------------------------------
const BASE = Process.getModuleByName("Game.patched.exe").base;
const A = (rva) => BASE.add(rva);

let roomPath = null, roomSeq = 0;
let decisions = 0, kept = 0, dropped = 0, forced = 0, skipped = 0, creates = 0, loops = 0;
let patched = new Set(), dryRun = false, dirty = false, roomRegex = null;
// How many serialised components each dropped type has ([type+0x80], the .ot read count): this histogram is what
// proves the 6/8 split, so it is reported next to the counters and settles it in one run.
let readCounts = {};
// The .ao path of each line that was let through, once per path (what actually came back).
let forcedPaths = new Set(), skippedPaths = new Set();

function readW(w) {                      // std::wstring: size @+0x18, cap @+0x20, data @[+8] if cap>7
  if (w === null || w.isNull()) return null;
  const size = w.add(0x18).readU32();
  const cap = w.add(0x20).readU64().toNumber();
  if (size === 0 || size > 0x400 || cap < size) return null;
  const data = cap > 7 ? w.add(8).readPointer() : w.add(8);
  return data.readUtf16String(size) || null;   // size = character count, no terminator needed
}

Interceptor.attach(A(0x2046fc0), {
  onEnter() {
    loops++; dirty = true;
    try {
      const s = readW(this.context.r8.readPointer());
      if (s !== null && s !== roomPath) {
        roomPath = s; roomSeq++;
        send({ event: "room", seq: roomSeq, path: roomPath, settlers: roomRegex.test(roomPath) });
      }
    } catch (e) {}
  }
});

Interceptor.attach(A(0x2048bb0), { onEnter() { creates++; dirty = true; } });

Interceptor.attach(A(0x152f9a0), {
  onEnter(args) {
    this.rdx = args[1];
    this.state = null;
    this.nSer = 0;
    try {
      const holder = this.rdx.readPointer();               // &row+0x30 -> room-line holder
      this.type = holder.add(0x28).readPointer();          // -> type record
      this.state = this.type.add(0x2a8).readU32();
      this.nSer = this.type.add(0x80).readU32();           // serialised component count
    } catch (e) { this.type = null; }
  },
  onLeave(retval) {
    decisions++; dirty = true;
    const al = retval.and(0xff).toInt32();
    if (al === 1) { kept++; return; }
    dropped++;
    // AL==0 <=> state >= 2 (server_only).  Patch the type only inside a matching room.
    if (this.type === null || roomPath === null || !roomRegex.test(roomPath)) return;
    // Only the structural types go back. Buildings, decks, wharves and ships carry six serialised components,
    // while the four interactive types (FarmingPlotInteractable, NoticeboardInteractable,
    // NoticeboardInteractableSmelting, MapDeviceButton) carry eight - InteractionAction and MinimapIcon - and
    // those are the ones that steal the NPCs clicks and crash the village interaction path when they come back.
    readCounts[this.nSer] = (readCounts[this.nSer] || 0) + 1;
    const line = this.rdx.sub(0x30);                    // the room-line row (stride 0x58)
    let ao = null;
    try { const p = line.add(0x38).readPointer(); ao = readW(p) ?? readW(p.add(8)); } catch (e) {}
    if (ao !== null && !skippedPaths.has(ao) && this.nSer >= 8) {
      skippedPaths.add(ao); send({ event: "skip", nSer: this.nSer, path: ao });
    }
    if (this.nSer === 0 || this.nSer >= 8) { skipped++; return; }
    if (ao !== null && !forcedPaths.has(ao)) {
      forcedPaths.add(ao); send({ event: "force", nSer: this.nSer, path: ao });
    }
    const key = this.type.toString();
    if (this.state !== 1) {
      if (!dryRun) this.type.add(0x2a8).writeU32(1);
      if (!patched.has(key)) { patched.add(key); dirty = true; }
    }
    if (!dryRun) { retval.replace(ptr(1)); forced++; }   // let this line through as well
  }
});

function summary(final) {
  return {
    event: final ? "final" : "summary",
    room: roomPath, roomChanges: roomSeq,
    decisions: decisions, kept: kept, dropped: dropped, forced: forced, skipped: skipped, readCounts: readCounts,
    creates: creates, loops: loops, typesPatched: patched.size, dryRun: dryRun
  };
}

setInterval(() => { if (dirty) { dirty = false; send(summary(false)); } }, 5000);

rpc.exports = {
  configure(o) {
    if (o.roomRegex !== undefined) roomRegex = new RegExp(o.roomRegex, "i");
    if (o.dryRun !== undefined) dryRun = !!o.dryRun;
    return { roomRegex: String(roomRegex), dryRun: dryRun };
  },
  reset() {
    decisions = kept = dropped = forced = creates = loops = 0;
    patched = new Set(); dirty = true; return true;
  },
  state() { return summary(false); },
  patchedTypes() { return Array.from(patched); }
};
"""

DEFAULT_REGEX = r"Metadata/Terrain/Leagues/Settlers/"


def recorded_pid(record):
    """The client PID live.ps1 records in data/clients/<session>.json, or None while the record has none yet (pid 0
    between the start and the client's first frame) or is unreadable."""
    try:
        with open(record, encoding="utf-8-sig") as f:
            saved = json.load(f)
    except (OSError, ValueError):
        return None
    pid = int(saved.get("pid") or 0)
    return pid or None


def attach(args, wait=None):
    """Attach to the client: the recorded one (--record, which waits for live.ps1 to write its PID), a PID, or the
    first process with the client's name. The patch has to be in place before the Settlers room loads, so a script
    started beside the client catches the room's first build after the player walks in. Returns None when no client
    showed up within the wait, so the caller can wait for the next one."""
    if args.pid:
        return frida.attach(args.pid)
    if not args.record:
        return frida.attach(args.name)
    deadline = time.time() + (args.wait if wait is None else wait)
    while True:
        pid = recorded_pid(args.record)
        if pid:
            try:
                return frida.attach(pid)
            except frida.ProcessNotFoundError:
                pass
        if time.time() > deadline:
            return None
        time.sleep(0.25)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pid", type=int)
    ap.add_argument("--name", default="Game.patched.exe")
    ap.add_argument("--record", default=None,
                    help="attach to the client live.ps1 records in data/clients/<session>.json (several clients)")
    ap.add_argument("--wait", type=float, default=120, help="how long --record waits for that client to appear")
    ap.add_argument("--seconds", type=float, default=0, help="0 = run until Ctrl+C")
    ap.add_argument("--room-regex", default=DEFAULT_REGEX)
    ap.add_argument("--dry-run", action="store_true", help="report only, change nothing")
    ap.add_argument("--log", default=None)
    ap.add_argument("--supervise", action="store_true",
                    help="restart this script when it dies (a frida attach on a starting client can abort it)")
    args = ap.parse_args()

    if args.supervise:
        # frida's windows helper can abort the whole interpreter ("Unable to allocate target memory") when it
        # attaches while the client is still coming up, so run the work in a child and start it again.
        import subprocess
        child = [a for a in sys.argv[1:] if a != "--supervise"]
        while True:
            code = subprocess.call([sys.executable, "-X", "utf8", __file__] + child)
            if args.seconds and args.seconds > 0:
                return code
            time.sleep(2.0)

    out = open(args.log, "w", encoding="utf-8") if args.log else None

    def emit(obj):
        line = json.dumps(obj, ensure_ascii=False)
        if out:
            out.write(line + chr(10))
            out.flush()
        try:
            print(line, flush=True)
        except OSError:
            # Started next to a client that runs detached, this process has no usable stdout handle (Errno 22); the
            # --log file is the record then.
            pass

    def serve(session):
        """Patch one client until its process ends."""
        script = session.create_script(SCRIPT)
        done = threading.Event()

        def on_message(message, data):
            if message.get("type") == "send":
                emit(message["payload"])
            elif message.get("type") == "error":
                emit({"event": "error", "description": message.get("description")})

        script.on("message", on_message)
        script.load()
        emit({"event": "ready", "config": script.exports_sync.configure(
            {"roomRegex": args.room_regex, "dryRun": args.dry_run})})

        def on_detached(reason, crash):
            emit({"event": "detached", "reason": str(reason),
                  "crash": None if crash is None else {"type": crash.type, "address": hex(crash.address),
                                                       "description": crash.description}})
            done.set()

        session.on("detached", on_detached)
        if args.seconds and args.seconds > 0:
            time.sleep(args.seconds)
        else:
            while not done.is_set():
                time.sleep(0.5)
        try:
            emit(script.exports_sync.state())
        except Exception:
            pass
        try:
            session.detach()
        except Exception:
            pass

    # A client restart (or a crash the patch cannot see) ends the session, so this re-attaches to whichever client
    # comes next instead of stopping with the first one: live.ps1 starts it once and leaves it running.
    first = True
    try:
        while True:
            session = None
            try:
                session = attach(args, wait=args.wait if first else min(args.wait, 30.0))
            except frida.ProcessNotFoundError:
                pass
            except Exception as exc:  # noqa: BLE001 - a failed attach on a starting client is retried
                emit({"event": "attach-failed", "error": str(exc)})
            first = False
            if session is None:
                if args.seconds and args.seconds > 0:
                    break
                time.sleep(1.0)
                continue
            serve(session)
            if args.seconds and args.seconds > 0:
                break
            time.sleep(0.5)
    except KeyboardInterrupt:
        pass
    if out:
        out.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())

# NOTES -------------------------------------------------------------------------------------
# * type+0x2a8 is the state the drop decision reads.  It is initialised to 0 by the type ctor
#   0x141ec7c10 and then set from the .ot "Info" record: record+0xef != 0 -> 1,
#   record+0xf0 != 0 -> 2 (the inherited BaseEvents{server_only=true}), record+0xf4 -> +0x2ac.
#   Writing 1 therefore also makes the creator skip `or byte [obj+0x1e6], 0x20` (0x142048d59),
#   which is what a "vanilla" client-creatable type would do.
# * Why not select by Id path instead: (a) the register entry is {u32 murmur, Ptr<type>} with no
#   path; (b) the only place the Id path exists next to a type is the ctor argument rdx, and the
#   ctor's single caller is the .ot enumeration callback 0x1401d59a0 (reached indirectly), which
#   has already run by the time anything can attach; (c) a type object does not store its Id
#   path in a readable wstring in its first 0x400 bytes.  Selecting by room + "this type is in
#   this room" is exact and needs no hash.
