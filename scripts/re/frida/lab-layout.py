"""Dump the Labyrinth day layouts the running client builds (dump-lab-layout.js) as JSON lines, one per (seed, difficulty).

    .venv\\Scripts\\python.exe -I scripts/re/frida/lab-layout.py --seeds 0 1 2 12345 --difficulties 1 2 3 > layouts.jsonl

The client must be running (any area): the script calls its layout builder directly, it sends nothing to the server.
"""
import argparse
import json
import pathlib

import frida

HERE = pathlib.Path(__file__).resolve().parent


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--target", default="Game.patched.exe")
    ap.add_argument("--seeds", type=int, nargs="+", default=[0])
    ap.add_argument("--difficulties", type=int, nargs="+", default=[1, 2, 3, 4])
    args = ap.parse_args()
    session = frida.attach(args.target)
    source = (HERE / "common.js").read_text(encoding="utf-8") + "\n" + (HERE / "dump-lab-layout.js").read_text(encoding="utf-8")
    script = session.create_script(source)
    script.load()
    try:
        for seed in args.seeds:
            for difficulty in args.difficulties:
                nodes = script.exports_sync.layout(seed, difficulty)
                print(json.dumps({"seed": seed, "difficulty": difficulty, "nodes": nodes}), flush=True)
    finally:
        script.unload()
        session.detach()


if __name__ == "__main__":
    main()
