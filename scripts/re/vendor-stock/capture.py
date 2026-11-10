"""Wait for the exact researched official client, capture decoded messages to ignored output.

Run from repo root: python scripts/re/vendor-stock/capture.py --client <official-install>/PathOfExile.exe
Does not launch or patch a client. Ctrl+C detaches. Raw output can include personal inventory.
"""
import argparse
import hashlib
import json
from pathlib import Path
import threading
import time
import frida

EXPECTED = '78A0E227189C263658A7F65A02F086428B859BFC114AE8E7CE8AAB721EBD48D5'

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--client', type=Path, required=True, help='Official 3.29.3c PathOfExile.exe to observe')
    parser.add_argument('--out', type=Path, default=Path('Tools/re/out/vendor-research/capture'))
    args = parser.parse_args()
    client, out = args.client.resolve(), args.out
    out.mkdir(parents=True, exist_ok=True)
    device = frida.get_local_device()
    print(f'WAITING for {client}; capture has not attached', flush=True)
    while True:
        candidates = [p for p in device.enumerate_processes(scope='full')
                      if p.name.lower() == 'pathofexile.exe'
                      and Path(p.parameters.get('path', '')).resolve() == client]
        if len(candidates) > 1:
            raise RuntimeError('Multiple matching clients; select one before capture')
        if candidates: break
        time.sleep(1)
    digest = hashlib.sha256(client.read_bytes()).hexdigest().upper()
    if digest != EXPECTED:
        raise RuntimeError('Client build changed; remap reader addresses before attaching')
    stopped = threading.Event()
    with (out / 'messages.jsonl').open('a', encoding='utf-8') as log:
        def record(message, data):
            log.write(json.dumps(message) + '\n')
            log.flush()
            payload = message.get('payload', {})
            print(json.dumps({k:v for k,v in payload.items() if k != 'hex'}) if isinstance(payload, dict) else str(payload), flush=True)
        session = device.attach(candidates[0].pid)
        session.on('detached', lambda *args: stopped.set())
        try:
            script = session.create_script(Path('scripts/re/vendor-stock/capture.js').read_text(encoding='utf-8'))
            script.on('message', record)
            script.load()
            print('ATTACHED; wait for ready before opening vendor', flush=True)
            while not stopped.wait(1): pass
        finally:
            if not stopped.is_set(): session.detach()

if __name__ == '__main__':
    try: main()
    except KeyboardInterrupt: pass
