"""End-to-end check of the passive-tree cluster jewels through the real client.

    python scripts/dev/jewel_selftest.py

The client must be logged in (the servers started by scripts/dev/live.ps1). The script drives the
client through scripts/dev/client_control.py: it creates jewels with the dev commands, moves them
with the inventory messages and allocates/refunds nodes with the passive messages, then checks the
Gateway log (the server's life, its node budget and its refusals), the client log (the names the
client gives the nodes: a small passive must not be the empty "Nothingness") and the save database.

What it checks:
  1. a cluster jewel goes into a tree socket and comes back out with no node allocated;
  2. a large cluster's small passives and notables give their stats (life) on the three small
     positions and on the notable positions of the documented cell-20 layout;
  3. a medium cluster jewel goes into a large cluster's socket and adds its nodes to the budget;
  4. a normal jewel in a cluster jewel's socket gives its stats (its +80 life) and loses them again.

It leaves the character as it found it: every cluster node refunded and every cluster jewel back in
the inventory. A node that the client does not have is detected by the client's
"Failed to find node from ID" line and refunded immediately.
"""
import json
import re
import sqlite3
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import client_control as cc

ROOT = cc.ROOT
GATEWAY = ROOT / "logs" / "live-gateway.log"
CLIENT_LOG = ROOT / "Client" / "logs" / "LatestClient.txt"
SAVE_DB = ROOT / "data" / "saves.db"

MAIN, PANE = 1, 24          # inventory ids in the protocol messages
MAIN_ROW, PANE_ROW, CURSOR_ROW = "0", "23", "12"  # the save keeps inventories by row (Inventories.dat row = id - 1)
CLUSTER_BASES = {869564366: "large", 4055676005: "medium", 717719955: "small"}
# The cell-20 block for an 8-passive, 2-socket, 3-notable large cluster, from PassiveTreeExpansionJewels' template
# lists and the cell's StartIndices (7): sockets at 11 and 3, notables at 1, 5 and 9, smalls at 0, 2, 5, 7, 9 of the
# small list (0, 1, 2, 5, 7, 9).
CELL20_SMALLS = [195, 197, 202]


class Failure(RuntimeError):
    pass


def check(condition, message):
    if not condition:
        raise Failure(message)
    print(f"  ok: {message}")


def read_log(path, offset):
    try:
        with path.open("rb") as log:
            log.seek(offset)
            data = log.read()
    except OSError:
        return [], offset
    end = data.rfind(b"\n") + 1
    return data[:end].decode("utf-8", errors="replace").splitlines(), offset + end


def log_size(path):
    try:
        return path.stat().st_size
    except OSError:
        return 0


def wait_log(path, pattern, offset, timeout=8.0):
    """The first line matching `pattern` written after `offset`, and the log size after it."""
    rx = re.compile(pattern)
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        lines, _ = read_log(path, offset)
        for line in lines:
            if rx.search(line):
                return line, log_size(path)
        time.sleep(0.05)
    return None, offset


def save_state(character):
    connection = sqlite3.connect(SAVE_DB)
    try:
        row = connection.execute("select save from characters where name = ?", (character,)).fetchone()
    finally:
        connection.close()
    if row is None:
        raise Failure(f"no save for {character}")
    data = json.loads(row[0])
    inventories = data.get("Inventories", {})
    main = []
    for item in inventories.get(MAIN_ROW, []):
        base = item["Item"]["Type"]
        main.append({"id": item["Id"], "x": item["X"], "y": item["Y"],
                     "cluster": CLUSTER_BASES.get(base), "type": base, "rarity": item["Item"]["Rarity"],
                     "mods": [m["Id"] for m in item["Item"]["Mods"]]})
    jewels = []
    for item in inventories.get(PANE_ROW, []):
        base = item["Item"]["Type"]
        jewels.append({"id": item["Id"], "cell": item["X"], "cluster": CLUSTER_BASES.get(base), "type": base,
                       "mods": [m["Id"] for m in item["Item"]["Mods"]]})
    return {"main": main, "jewels": jewels, "cluster": data.get("ClusterPassives") or [],
            "cursor": inventories.get(CURSOR_ROW) or [],
            "passives": data.get("Passives") or [], "refund": data.get("RefundPoints") or 0}


def wait_save(character, predicate, description, timeout=10.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        state = save_state(character)
        if predicate(state):
            return state
        time.sleep(0.1)
    raise Failure(f"timed out waiting for {description}; check logs/live-gateway.log")


class SelfTest:
    def __init__(self, control):
        self.control = control
        self.character = control.status()["character"]
        self.gateway = log_size(GATEWAY)
        self.client_log = log_size(CLIENT_LOG)
        self.last_mark = self.gateway

    def command(self, text, wait=1.2):
        return self.control.execute({"command": "command", "text": text, "wait": wait})

    def passive(self, node, allocate=True):
        return self.control.execute({"command": "passive:allocate" if allocate else "passive:refund",
                                     "node": node})

    def pickup(self, inventory, item):
        return self.control.execute({"command": "inventory:pickup", "inventoryId": inventory, "id": item})

    def place(self, inventory, x=0, y=0):
        return self.control.execute({"command": "inventory:place", "inventoryId": inventory, "x": x, "y": y})

    def client_crashed_tree(self):
        lines, _ = read_log(CLIENT_LOG, self.client_log)
        return any("Failed to find node from ID" in line for line in lines)

    def allocate(self, node):
        self.last_mark = log_size(GATEWAY)
        self.passive(node)
        line, _ = wait_log(GATEWAY, rf"cluster jewel node {node} (allocated|refunded)", self.last_mark)
        if line is None:
            raise Failure(f"the Gateway did not answer the allocation of node {node}")
        if "refunded" in line:
            self.passive(node, allocate=False)
            raise Failure(f"node {node} was refused (it is above the cluster node budget?)")
        if self.client_crashed_tree():
            self.passive(node, allocate=False)
            raise Failure(f"the client has no node {node} (Failed to find node from ID); refunded it")
        return line

    def refund(self, node):
        self.last_mark = log_size(GATEWAY)
        self.passive(node, allocate=False)
        wait_log(GATEWAY, rf"cluster jewel node {node} refunded", self.last_mark)

    def stats(self):
        """The latest `passive jewel stats: life N ... A/B cluster node(s)` line, once a new one is written."""
        wait_log(GATEWAY, r"passive jewel stats:", self.last_mark, 5)
        time.sleep(0.2)
        pattern = r"passive jewel stats: life (\d+), mana \d+, (\d+)/(\d+) cluster node"
        rx = re.compile(pattern)
        last = None
        for line in read_log(GATEWAY, 0)[0]:
            if rx.search(line):
                last = line
        if last is None:
            raise Failure("no passive jewel stats line in the Gateway log")
        match = rx.search(last)
        return int(match.group(1)), int(match.group(2)), int(match.group(3))

    def free_cell(self, state):
        used = {(item["x"], item["y"]) for item in state["main"]}
        for y in range(4, -1, -1):
            for x in range(11, -1, -1):
                if (x, y) not in used:
                    return x, y
        raise Failure("the main inventory is full")

    def take_out(self, item_id):
        state = save_state(self.character)
        x, y = self.free_cell(state)
        self.last_mark = log_size(GATEWAY)
        self.pickup(PANE, item_id)
        self.place(MAIN, x, y)
        return wait_save(self.character,
                         lambda s: any(i["id"] == item_id for i in s["main"])
                         and all(j["id"] != item_id for j in s["jewels"]),
                         f"jewel {item_id} in the inventory")

    def cleanup(self):
        print("cleanup: refund every cluster passive and take every cluster jewel out")
        state = save_state(self.character)
        # A failed earlier run can leave an item on the cursor, which blocks every pickup.
        for item in state["cursor"]:
            x, y = self.free_cell(state)
            self.place(MAIN, x, y)
            state = wait_save(self.character, lambda s: any(i["id"] == item["Id"] for i in s["main"]),
                              f"cursor item {item['Id']} in the inventory")
        for node in list(state["cluster"]):
            self.refund(node)
        state = save_state(self.character)
        check(not state["cluster"], "no cluster passive is allocated")
        # Inner jewels first: a cluster jewel cannot leave while its own sockets hold one.
        for jewel in sorted([j for j in state["jewels"] if j["cluster"]], key=lambda j: -j["cell"]):
            self.take_out(jewel["id"])
        check(not any(j["cluster"] for j in save_state(self.character)["jewels"]),
              "no cluster jewel is socketed")

    def create(self, size, passives, sockets, theme=None, notables=()):
        text = f"/cluster_jewel {size} {passives} {sockets}"
        if theme:
            text += " " + theme
        text += "".join(" " + notable for notable in notables)
        before = {item["id"] for item in save_state(self.character)["main"]}
        self.command(text)
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            state = save_state(self.character)
            new = [item for item in state["main"] if item["id"] not in before and item["cluster"]]
            if new:
                return new[-1]
            time.sleep(0.1)
        raise Failure(f"{text} did not add a cluster jewel")

    def create_life_jewel(self):
        """A normal jewel with +80 maximum life, still on the cursor (as /mod works on the held item)."""
        before = {item["id"] for item in save_state(self.character)["main"]}
        self.command("/item Prismatic Jewel")
        deadline = time.monotonic() + 8
        jewel = None
        while time.monotonic() < deadline and jewel is None:
            state = save_state(self.character)
            new = [item for item in state["main"] if item["id"] not in before and not item["cluster"]]
            jewel = new[-1] if new else None
            if jewel is None:
                time.sleep(0.1)
        if jewel is None:
            raise Failure("/item Prismatic Jewel did not add a normal jewel; set the cursor clear first")
        self.pickup(MAIN, jewel["id"])
        self.command("/mod explicit IncreasedLife5 80")
        return jewel

    def socket(self, item, cell):
        self.last_mark = log_size(GATEWAY)
        self.pickup(MAIN, item["id"])
        self.place(PANE, cell, 0)
        line, _ = wait_log(GATEWAY, r"refused|inventory 24: add item", self.last_mark, 5)
        if line is not None and "refused" in line:
            raise Failure(f"cell {cell} refused the jewel: {line.strip()}")
        return wait_save(self.character, lambda s: any(j["id"] == item["id"] and j["cell"] == cell
                                                      for j in s["jewels"]), f"jewel {item['id']} in cell {cell}")

    def run(self):
        self.cleanup()

        print("1. a cluster jewel goes in and comes back out with no node allocated")
        jewel = self.create("large", 8, 2)
        state = self.socket(jewel, 20)
        check(any(j["id"] == jewel["id"] for j in state["jewels"]), "the large cluster jewel is in pane cell 20")
        base_life, allocated, budget = self.stats()
        check((allocated, budget) == (0, 8), "a fresh 8-passive, 2-socket large cluster has 8 nodes and none taken")
        print(f"base life {base_life}")
        state = self.take_out(jewel["id"])
        check(all(j["id"] != jewel["id"] for j in state["jewels"]), "it came back out without any node allocated")
        life, allocated, _ = self.stats()
        check((allocated, life) == (0, base_life), "no cluster node and the base life after the round trip")

        print("2. a large cluster's small passives and notables give their stats")
        jewel = self.create("large", 8, 2, "AfflictionJewelSmallPassivesGrantLife2_",
                            ["AfflictionNotableFettle", "AfflictionNotableProdigiousDefense__",
                             "AfflictionNotableAdvanceGuard"])
        check(jewel["rarity"] == 2, "three notables make the jewel rare")
        state = self.socket(jewel, 20)
        life, allocated, budget = self.stats()
        check((allocated, budget) == (0, 8), "an 8-passive, 2-socket large cluster has 8 nodes")
        check(life == base_life, "the jewel alone gives nothing")
        # The client's ids are not mapped to the nodes' kinds: the two sockets are the nodes taken first and give
        # nothing, the theme starts with the third node (3 smalls for this jewel) and the notables' rows come with the
        # last passive. The character's increased maximum life scales the theme.
        # Cell 20's 8/2/3 layout: smalls at 195, 197 and 202 (theme as they are taken), notables at 196, 200 and 204
        # (their rows once every passive is allocated), sockets at 198 and 206 (nothing).
        self.allocate(198)
        check(self.stats()[0] == base_life, "a cluster socket gives no stats")
        self.allocate(195)
        one = self.stats()[0] - base_life
        check(one > 0, f"a small passive gives the theme ({one})")
        self.allocate(197)
        self.allocate(202)
        three = self.stats()[0] - base_life
        check(abs(three - 3 * one) <= 2, f"three smalls add about three times one ({three} vs {3 * one})")
        self.allocate(196)
        check(self.stats()[0] - base_life == three, "a notable's node gives no theme by itself")
        life = self.stats()[0]
        for node in (200, 204, 206):
            self.allocate(node)
        life, allocated, _ = self.stats()
        check(allocated == 8, "the whole cluster is allocated")
        check(life - base_life - three >= 20, f"the notables are added with the last passive ({life - base_life - three})")
        check(not self.client_crashed_tree(), "the client resolved every id (no Failed to find node from ID)")

        print("3. a medium cluster jewel in a large one's socket adds its nodes")
        medium = self.create("medium", 4, 1, "AfflictionJewelSmallPassivesGrantLife2_")
        self.socket(medium, 51)
        _, allocated, budget = self.stats()
        check((allocated, budget) == (8, 12), f"the medium adds its four nodes to the budget ({allocated}/{budget})")
        base = self.stats()[0]
        # The client's expansion-cell ids are not known to the server, so its passives are only checked by the unit
        # tests; injecting ids here would place the client's empty "Nothingness" nodes, which it removes again.
        print("4. a normal jewel in a cluster jewel's socket gives its stats")
        self.cleanup()
        large = self.create("large", 8, 2)
        self.socket(large, 20)
        life, _, _ = self.stats()
        check(life == base_life, "a fresh cluster jewel in the tree gives nothing")
        jewel = self.create_life_jewel()
        self.last_mark = log_size(GATEWAY)
        self.place(PANE, 51, 0)
        wait_save(self.character, lambda s: any(j["id"] == jewel["id"] and j["cell"] == 51 for j in s["jewels"]),
                  "the life jewel in cell 51")
        life, _, _ = self.stats()
        check(life - base_life >= 80, f"the jewel's 80 life counts in a cluster socket ({life - base_life})")
        self.take_out(jewel["id"])
        life, _, _ = self.stats()
        check(life == base_life, "taking it out takes its life away")

        print("cleanup again")
        self.cleanup()
        life, allocated, budget = self.stats()
        check((allocated, budget, life) == (0, 0, base_life), "with no cluster jewel the life is back to the base")

        print("all cluster jewel checks passed")


def main():
    with cc.ClientControl() as control:
        status = control.status()
        if not status.get("running") or status.get("screen") != "game" or status.get("instanceState") != 3:
            print("the client must be logged in and in an area; start it with scripts/dev/live.ps1", file=sys.stderr)
            return 2
        try:
            SelfTest(control).run()
        except Failure as error:
            print(f"FAILED: {error}", file=sys.stderr)
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
