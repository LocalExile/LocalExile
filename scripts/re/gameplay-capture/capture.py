"""Record the exact official client after the user launches it. Never launches or patches it."""
import argparse
import hashlib
import json
from pathlib import Path
import threading
import time
import subprocess
import sys
import frida

EXPECTED = "78A0E227189C263658A7F65A02F086428B859BFC114AE8E7CE8AAB721EBD48D5"

def digest_of(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()

def main():
    ap = argparse.ArgumentParser(description=__doc__)
    client_source = ap.add_mutually_exclusive_group(required=True)
    client_source.add_argument("--client", type=Path, help="Exact reference executable to observe; never launched")
    ap.add_argument("--check", action="store_true")
    client_source.add_argument("--local", action="store_true", help="Observe this checkout's prepared client")
    ap.add_argument("--session", help="Named prepared-client session; requires --local")
    args = ap.parse_args()
    if args.session and not args.local:
        ap.error("--session requires --local")
    selected_session = None
    if args.local:
        sys.path.insert(0, str(Path("scripts/dev").resolve()))
        from client_control import find_client, read_records, session_name
        selected_session = session_name(args.session)
    client = (Path("Client/Game.patched.exe") if args.local else args.client).resolve()
    expected = "3375B36162D29A4B5842340A28B79A3D102AD05BA1F0B2DC40E19D8564D9C1A4" if args.local else EXPECTED
    digest = digest_of(client)
    if digest != expected:
        raise SystemExit("Executable changed; remap and validate hooks before recording.")
    if args.check:
        print("Exact researched 3.29.3b executable verified; Frida " + frida.__version__)
        return
    out = Path("Tools/re/out/gameplay-captures") / time.strftime("%Y%m%d-%H%M%S")
    out.mkdir(parents=True, exist_ok=False)
    device = frida.get_local_device()
    print(f"WAITING for {client}; output {out}", flush=True)
    while True:
        found = [p for p in device.enumerate_processes(scope="full")
                 if p.name.lower() == client.name.lower() and Path(p.parameters.get("path", "")).resolve() == client]
        if found and args.local:
            records = read_records()
            selected = find_client(selected_session, [p.pid for p in found], records=records)
            found = [p for p in found if p.pid == selected]
        if len(found) > 1:
            raise SystemExit("Multiple matching clients; select a local session or keep one reference client running.")
        if found:
            break
        time.sleep(0.5)
    if digest_of(client) != expected:
        raise SystemExit("Executable changed during launch; capture stopped.")
    start = time.time()
    source_log = client.parent / "logs/Client.txt"
    if args.local and selected_session != "default":
        source_log = Path("data/clients") / selected_session / "logs/Client.txt"
    log_offset = source_log.stat().st_size if source_log.exists() else 0
    manifest = dict(mode="local" if args.local else "official", client=str(client), sha256=digest, pid=found[0].pid, started=start,
                    route="Ranger: Twilight Strand -> Hillock -> Lioneye's Watch",
                    scope="Instance gameplay only; no login capture, video, side-channel or completeness guarantee.")
    manifest.update(client_log_offset=log_offset, stop_file=str((out / "STOP").resolve()))
    if args.local:
        manifest["client_session"] = selected_session
        manifest["source_commit"] = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
        diff = subprocess.check_output(["git", "diff", "HEAD", "--", "src", "Modules", "Mods"])
        manifest["tracked_gameplay_diff_sha256"] = hashlib.sha256(diff).hexdigest()
        manifest["working_tree"] = subprocess.check_output(["git", "status", "--short"], text=True).splitlines()
    (out / "session.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    stopped = threading.Event()
    lock = threading.Lock()
    counts = {}
    with (out / "events.jsonl").open("w", encoding="utf-8") as events, (out / "payloads.bin").open("wb") as binary:
        def record(message, data):
            with lock:
                event = dict(received=time.time(), message=message)
                if data is not None:
                    event.update(offset=binary.tell(), length=len(data))
                    binary.write(data)
                    binary.flush()
                events.write(json.dumps(event) + "\n")
                events.flush()
                payload = message.get("payload", {})
                if payload.get("kind") == "bytes" and payload.get("accepted"):
                    direction = payload["direction"]
                    counts[direction] = counts.get(direction, 0) + 1
                    if counts[direction] == 1:
                        print("FIRST " + direction + " captured", flush=True)
                elif payload.get("kind") != "bytes":
                    print(json.dumps(message), flush=True)
        session = device.attach(found[0].pid)
        session.on("detached", lambda *a: stopped.set())
        try:
            code = Path(__file__).with_name("capture.js").read_text(encoding="utf-8")
            code = code.replace("Process.getModuleByName('PathOfExile.exe')", "Process.getModuleByName(" + json.dumps(client.name) + ")")
            script = session.create_script(code)
            script.on("message", record)
            script.load()
            print("ATTACHED: wait for ready and both FIRST direction messages before the reference run. Create the session STOP file to stop cleanly.", flush=True)
            while not stopped.wait(0.5):
                if (out / "STOP").exists():
                    break
        except KeyboardInterrupt:
            pass
        finally:
            if not stopped.is_set():
                session.detach()
    if source_log.exists():
        with source_log.open("rb") as source:
            source.seek(log_offset)
            (out / "Client.txt").write_bytes(source.read())
    manifest.update(ended=time.time(), counts=counts)
    (out / "session.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print("Saved " + str(out), flush=True)

if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
