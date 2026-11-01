"""Attach to the running client and load trace scripts, printing their send() messages.

The Frida CLI is not installed here (only the Python package), and REA's frida_session tools are not always available,
so this is the plain-Python runner for scripts/re/frida/*.js.

    python scripts/re/frida/run.py <pid|process name> scripts/re/frida/common.js scripts/re/frida/trace-http.js
    python scripts/re/frida/run.py Game.patched.exe --seconds 90 scripts/re/frida/trace-http.js

Scripts load in the order given (common.js first, as its helpers are globals for the trace scripts). Messages print as
JSON lines to stdout and, with --log, to a file as they arrive. Ctrl-C detaches; it never kills the client.
"""
import argparse
import json
import sys
import threading
import time

import frida


def pick_pid(device, target):
    if target.isdigit():
        return int(target)
    want = target.lower()
    for proc in device.enumerate_processes():
        if proc.name.lower() == want:
            return proc.pid
    for proc in device.enumerate_processes():
        if want in proc.name.lower():
            return proc.pid
    raise SystemExit(f"no process matching {target!r}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("target", help="pid or process name")
    ap.add_argument("scripts", nargs="+", help="JS files, loaded in order")
    ap.add_argument("--follow", action="store_true",
                    help="wait for a NEW process with this name (attach as it starts) instead of attaching to the running one")
    ap.add_argument("--seconds", type=float, default=0, help="exit after this many seconds (0 = until Ctrl-C)")
    ap.add_argument("--log", help="also append messages to this file")
    ap.add_argument("--finish-rpc", help="call this exported RPC, when present, before detaching")
    args = ap.parse_args()

    device = frida.get_local_device()
    if args.follow:
        # Catch the client's startup traffic: a process that starts after this runner was launched, not the one running.
        want, before = args.target.lower(), {p.pid for p in device.enumerate_processes() if p.name.lower() == args.target.lower()}
        print(json.dumps({"waiting": want, "existing": sorted(before)}), flush=True)
        while True:
            pid = next((p.pid for p in device.enumerate_processes()
                        if p.name.lower() == want and p.pid not in before), None)
            if pid is not None:
                break
            time.sleep(0.02)
    else:
        pid = pick_pid(device, args.target)
    log = open(args.log, "a", encoding="utf-8") if args.log else None
    stop = threading.Event()
    session_gone = threading.Event()
    lock = threading.Lock()   # Frida's callbacks run on its own threads
    session = None
    scripts = []

    def emit(line):
        with lock:
            print(line, flush=True)
            if log:
                log.write(line + "\n")
                log.flush()

    def on_message(message, data):
        emit(json.dumps(message, ensure_ascii=False))

    # A crashed or closed client ends the session: say why ("process-terminated") and stop. Our own detach (Ctrl-C,
    # --seconds) is reported below.
    def detached(reason, crash):
        if reason != "application-requested":
            emit(json.dumps({"detached": pid, "reason": reason, "crash": str(crash) if crash else None}))
        stop.set()
        session_gone.set()

    def timer():
        time.sleep(args.seconds)
        stop.set()

    try:
        session = device.attach(pid)
        session.on("detached", detached)
        for path in args.scripts:
            with open(path, encoding="utf-8") as source_file:
                source = source_file.read()
            script = session.create_script(source, name=path)
            script.on("message", on_message)
            script.load()
            scripts.append(script)
            emit(json.dumps({"loaded": path}))
        emit(json.dumps({"attached": pid, "message": "listening; Ctrl-C detaches"}))
        if args.seconds:
            threading.Thread(target=timer, daemon=True).start()
        while not stop.is_set():
            time.sleep(0.2)
    except KeyboardInterrupt:
        pass
    finally:
        try:
            if session is not None and not session_gone.is_set():
                try:
                    if args.finish_rpc:
                        for script in scripts:
                            try:
                                if args.finish_rpc in script.list_exports_sync():
                                    getattr(script.exports_sync, args.finish_rpc)()
                            except Exception as exc:
                                emit(json.dumps({"type": "error", "description": f"finish RPC failed: {exc}"}))
                finally:
                    session.detach()
                emit(json.dumps({"detached": pid}))
        finally:
            if log:
                log.close()


if __name__ == "__main__":
    sys.exit(main())
