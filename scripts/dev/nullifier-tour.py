"""The coverage tour for session clients (Agents/docs/dev/headless-client.md §6): a client of this checkout's stack,
already started by live.ps1 -ClientOnly -Session S [-Nullifier], logs in, plays a character and goes through a sample of
every act (each town and a combat area), a boss area, a map and the default hideout (HideoutForest), fighting a
spawned monster in each combat area with the default attack, then chat and /tp. After every step it checks that the
client is alive and still runs frames, and that /pos names the expected area.

    .venv/Scripts/python.exe scripts/dev/nullifier-tour.py --session bench [--character OfflineWitch] [--quick]

It sends only the bridge's own messages (no keyboard or mouse input). /god is turned on for the fight and off again at
the end (it is a per-character toggle that lasts until the Gateway restarts).
Results append to logs\\nullifier\\tour.jsonl.
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import re
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parent))
import client_control  # noqa: E402  (same directory)

bench = __import__("nullifier-bench")  # noqa: E402  (password, with_timeout)

# (area id, fight here): each act's town and a combat area, a boss area, a late act, a map and the hideout.
TOUR = [("1_1_town", False), ("1_1_2", True), ("1_1_7_2", True), ("1_2_town", False), ("1_2_2", True),
        ("1_3_town", False), ("1_3_1", True), ("1_4_town", False), ("1_4_1", True), ("1_5_town", False),
        ("1_5_1", True), ("2_6_town", False), ("2_6_1", True), ("2_7_town", False), ("2_7_1", True),
        ("2_8_town", False), ("2_8_1", True), ("2_9_town", False), ("2_9_1", True), ("2_10_town", False),
        ("2_10_1", True), ("MapWorldsBeach", True), ("HideoutForest", False), ("1_1_town", False)]
QUICK = [("1_1_town", False), ("1_1_2", True), ("1_2_town", False), ("2_6_1", True), ("MapWorldsBeach", True),
         ("HideoutForest", False), ("1_1_town", False)]
DEFAULT_ATTACK = 0x0102
MONSTER = "ZombieBite@13"


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--session", required=True)
    parser.add_argument("--character", default="OfflineWitch")
    parser.add_argument("--quick", action="store_true", help="a shorter sample (7 areas)")
    parser.add_argument("--rpc-timeout", type=float, default=90)
    parser.add_argument("--label", default="")
    parser.add_argument("--out", type=Path, default=client_control.ROOT / "logs" / "nullifier" / "tour.jsonl")
    options = parser.parse_args()
    session = client_control.session_name(options.session)
    if session == client_control.DEFAULT_SESSION:
        raise SystemExit("the tour needs a named session")
    control = client_control.ClientControl(client_session=session)
    steps, started = [], time.monotonic()
    log = lambda *a: print(datetime.datetime.now().strftime("%H:%M:%S"), *a, flush=True)  # noqa: E731

    def rpc(request, quiet=False):
        result = bench.with_timeout(options.rpc_timeout, client_control.run_request, control, request)
        if not quiet:
            log(request["command"], request.get("text", request.get("id", "")), "->", json.dumps(result)[:240])
        return result

    def replies(text, wait=1.5):
        result = rpc({"command": "command", "text": text, "wait": wait})
        return result.get("result", {}).get("replies", []) if result["ok"] else []

    def in_game(seconds=60):
        for _ in range(seconds):
            status = rpc({"command": "game:status"}, quiet=True)
            if status["ok"] and status["result"].get("screen") == "game" and status["result"].get("instanceState") == 3:
                return True
            time.sleep(1)
        return False

    def step(name, ok, **detail):
        frames = rpc({"command": "game:frames"}, quiet=True)
        fps = frames.get("result", {}).get("fpsSinceLastCall") if frames["ok"] else None
        entry = {"step": name, "ok": bool(ok and frames["ok"]), "fps": fps,
                 "t": round(time.monotonic() - started, 1), **detail}
        steps.append(entry)
        log("STEP", json.dumps(entry))
        if not frames["ok"]:
            raise SystemExit(f"the client stopped answering after {name}")

    try:
        rpc({"command": "game:open"})
        rpc({"command": "login", "account": "offline", "password": bench.password()}, quiet=True)
        rpc({"command": "character:list"})
        rpc({"command": "character:play", "name": options.character})
        step("play", in_game())
        replies("/waypoints all")
        if any("God mode off." in line for line in replies("/god")):
            replies("/god")
        client_log = client_control.ROOT / "data" / "clients" / session / "logs" / "Client.txt"
        for index, (area, fight) in enumerate(QUICK if options.quick else TOUR):
            # Combat areas get a seed of their own: with seed 1 the client's own level generation fails in some
            # (2_6_1: "Could not tilise edge BeachCliff"), whatever the renderer, and the client then stays loading.
            text = f"/goto {area} {101 + index}" if fight else f"/goto {area}"
            mark = client_log.stat().st_size if client_log.exists() else 0
            replies(text, wait=2)
            time.sleep(3)
            arrived = in_game()
            where = " ".join(replies("/pos"))
            ok = arrived and f" in {area}" in where
            detail = {"area": area, "pos": where}
            if not arrived and client_log.exists():
                with client_log.open("rb") as handle:
                    handle.seek(mark)
                    failure = [line for line in handle.read().decode("utf-8", "replace").splitlines()
                               if "Terrain generation failed" in line or "[EXCEPTION]" in line]
                detail["clientError"] = failure[:2]
                if failure:
                    step(text, False, **detail)
                    raise SystemExit("the client's level generation failed; it stays loading (not a renderer fault)")
            if ok and fight:
                replies(f"/spawn_mob {MONSTER}", wait=1)
                spawned = list(re.finditer(r"0x([0-9a-f]+) Hungry Corpse@\d+ \((-?[\d.]+), (-?[\d.]+)\)",
                                           " ".join(replies("/mobs"))))
                if spawned:
                    target, mx, my = max((int(m.group(1), 16), round(float(m.group(2))), round(float(m.group(3))))
                                         for m in spawned)
                    detail["monster"] = hex(target)
                    here = re.search(r"\((\d+), (\d+)\)", where)
                    if here:  # next to it: the attack has a reach
                        replies(f"/tp {mx + 3 - int(here.group(1)):+d} {my - int(here.group(2)):+d}")
                    # A kill shows in the Gateway's log as the target's life update to 0 (a dead monster stays in
                    # /mobs for 10 s).
                    gateway_log = client_control.ROOT / "logs" / "live-gateway.log"
                    mark_log = gateway_log.stat().st_size if gateway_log.exists() else 0

                    def killed():
                        if not gateway_log.exists():
                            return False
                        with gateway_log.open("rb") as handle:
                            handle.seek(mark_log)
                            pattern = rf"target=0x{target:x} damage=\d+ life=0/".encode()
                            return re.search(pattern, handle.read()) is not None
                    for _ in range(20):
                        rpc({"command": "object:interact", "id": target, "skill": DEFAULT_ATTACK}, quiet=True)
                        time.sleep(1)
                        if killed():
                            break
                    detail["killed"] = killed()
                    replies("/despawn all")
                position = re.search(r"\((\d+), (\d+)\)", " ".join(replies("/pos")))
                if position:
                    rpc({"command": "move", "x": int(position.group(1)) + 6, "y": int(position.group(2))}, quiet=True)
                    time.sleep(2)
            step(text, ok, **detail)
        step("chat", rpc({"command": "chat", "text": "nullifier tour"})["ok"])
        step("/tp", any("Teleported" in line for line in replies("/tp +5 +0")))
    finally:
        try:  # /god off again (a toggle): at most two tries
            for _ in range(2):
                if any("God mode off." in line for line in replies("/god")):
                    break
        except BaseException:  # noqa: BLE001  (best effort; the client may be gone)
            pass
        control.close()
        options.out.parent.mkdir(parents=True, exist_ok=True)
        with options.out.open("a", encoding="utf-8") as out:
            now = datetime.datetime.now().isoformat()
            out.write(json.dumps({"label": options.label, "session": session, "time": now,
                                  "steps": steps, "passed": sum(s["ok"] for s in steps), "total": len(steps)}) + "\n")
    failed = [s["step"] for s in steps if not s["ok"]]
    log(f"tour: {len(steps) - len(failed)}/{len(steps)} steps ok" + (f"; failed: {failed}" if failed else ""))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
