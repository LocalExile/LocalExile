"""Attach only; never launches or updates a client. See README.txt."""
import argparse
import hashlib
import json
from pathlib import Path
import threading
import time


HASHES = {
    "78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5": "stock Game.exe",
    "3375b36162d29a4b5842340a28b79a3d102ad05ba1f0b2dc40e19d8564d9c1a4": "local Game.patched.exe",
}


def digest(path):
    with open(path, "rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--check", type=Path, help="check an executable without attaching")
    ap.add_argument("--pid", type=int, help="explicit running client PID")
    ap.add_argument("--out", type=Path, help="new JSONL file; never overwrites")
    ap.add_argument("--reference", choices=["local", "official", "unknown"], default="unknown")
    ap.add_argument("--seconds", type=float, default=0, help="bounded observer smoke capture; no armed trials")
    args = ap.parse_args()
    if args.check:
        value = digest(args.check)
        print(json.dumps({"sha256": value, "profile": HASHES.get(value), "compatible": value in HASHES}))
        return 0 if value in HASHES else 1
    if not args.pid or not args.out or args.seconds < 0:
        ap.error("capture requires --pid and --out, with nonnegative --seconds")

    import frida  # Offline check/report do not require a Frida installation.

    args.out.parent.mkdir(parents=True, exist_ok=True)
    device = frida.get_local_device()
    if not any(p.pid == args.pid for p in device.enumerate_processes()):
        raise SystemExit(f"PID {args.pid} is not running; choose the current client PID")
    session = device.attach(args.pid)
    script = None
    ready = threading.Event()
    started = threading.Event()
    gone = threading.Event()
    lock = threading.Lock()
    failed = []
    stream = None
    try:
        # No observer hooks installed until on-disk executable identity is checked.
        bootstrap = session.create_script("rpc.exports = {image() {return {path: Process.mainModule.path, name: Process.mainModule.name};}};")
        bootstrap.load()
        image = bootstrap.exports_sync.image()
        bootstrap.unload()
        sha = digest(image["path"])
        if sha not in HASHES:
            raise RuntimeError(f"unsupported executable SHA256 {sha}; no observer hooks installed")
        stream = args.out.open("x", encoding="utf-8")

        def write(data):
            with lock:
                stream.write(json.dumps(data, ensure_ascii=False) + "\n")
                stream.flush()

        write({"event": "manifest", "schema": 1, "pid": args.pid, "image": image,
               "sha256": sha, "reference": args.reference, "utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
               "probeSha256": digest(Path(__file__).with_name("probe.js"))})

        def on_message(message, data):
            if message.get("type") == "send":
                payload = message["payload"]
                write(payload)
                if payload.get("event") == "start":
                    started.set()
                    ready.set()
                if payload.get("event") == "error":
                    print("Observer read error; this capture will be invalid for parity measurements.", flush=True)
            else:
                failed.append(message)
                write({"event": "host_error", "detail": message})
                ready.set()

        def detached(reason, crash):
            gone.set()
            if reason != "application-requested":
                failed.append(reason)
                write({"event": "unexpected_detach", "reason": reason, "crash": str(crash) if crash else None})

        session.on("detached", detached)
        script = session.create_script(Path(__file__).with_name("probe.js").read_text(encoding="utf-8"))
        script.on("message", on_message)
        script.load()
        if not ready.wait(10) or failed:
            raise RuntimeError("observer did not start cleanly; see capture errors")
        print(f"Recording {image['name']} PID {args.pid} -> {args.out}", flush=True)
        print("Enter k [ID], c [ID], or p BEFORE one kill/chest/pack; e ends trial; m text marks; q finishes.", flush=True)
        if args.seconds:
            gone.wait(args.seconds)
        else:
            while not gone.is_set():
                try:
                    command = input("> ").strip().split(maxsplit=1)
                except EOFError:
                    break
                if not command:
                    continue
                op = command[0].lower()
                try:
                    if op == "q":
                        break
                    if op in ("k", "c", "p"):
                        source = int(command[1], 0) if len(command) > 1 else None
                        number = script.exports_sync.begin({"k": "kill", "c": "chest", "p": "pack"}[op], source)
                        print(f"Trial {number} armed. Finish only the intended encounter, wait for all loot, then enter e.", flush=True)
                    elif op == "e":
                        script.exports_sync.end()
                    elif op == "m" and len(command) > 1:
                        script.exports_sync.mark(command[1])
                    else:
                        print("Commands: k [ID], c [ID], p, e, m text, q", flush=True)
                except Exception as exc:
                    print(f"Command rejected: {exc}", flush=True)
    except KeyboardInterrupt:
        pass
    finally:
        try:
            if script and started.is_set() and not gone.is_set():
                summary = script.exports_sync.finish()
                print("Observer summary:", summary, flush=True)
                if any(summary.values()):
                    failed.append(summary)
        finally:
            # Preserve the patched local client's existing keep-agent convention.
            if not gone.is_set() and 'image' in locals() and image["name"].lower() == "game.patched.exe":
                keeper = session.create_script("", name="localexile-keep-agent")
                keeper.load()
                keeper.eternalize()
            session.detach()
            if stream:
                stream.close()
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
