"""Campaign runner checks against a simulated server; no client, servers or saves are touched.

    .venv/Scripts/python.exe -m unittest discover -s scripts/dev/campaign -p "test_*.py"
"""
import contextlib
import copy
import io
import json
import logging
from pathlib import Path
import sys
import tempfile
import textwrap
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import bridge as bridge_module  # noqa: E402
from bridge import Bridge, StateUnavailable, WaitTimeout, parse_state  # noqa: E402
import order as order_module  # noqa: E402
import runner  # noqa: E402
import steps  # noqa: E402

logging.getLogger("campaign").addHandler(logging.NullHandler())  # steps tests run without the runner's handlers


class FakeClock:
    def __init__(self):
        self.now = 0.0

    def time(self):
        return self.now

    def sleep(self, seconds):
        self.now += max(seconds, 0.01)


def area(**content):
    base = {"npcs": [], "objects": [], "items": [], "transitions": [], "monsters": [], "waypoint": None,
            "spawn": [10, 10]}
    base.update(content)
    return base


class FakeServer:
    """A fake ClientControl: keeps the player's and areas' state and reacts to bridge requests like the
    Gateway/bridge would. Hooks (`on_interact`, `on_option`, `on_kill`, `on_enter`) model quest reactions."""

    def __init__(self, world, start="1_1_town", **player):
        self.world = world
        self.calls = []
        self.drops_pantheon = False
        self.screen = "game"
        self.character = "Tester"
        self.characters = [{"index": 0, "name": "Tester", "league": "Standard", "level": 1, "classId": 3}]
        self.passwords = []
        self.area_wait = False
        self.split = 120
        self.partial_states = 0     # next N /state replies are cut short (no STATE+ tail)
        self.reject_nodes = set()
        self.unknown = set()        # bridge commands this fake reports as unknown
        self.on_interact, self.on_option, self.on_kill, self.on_enter = {}, {}, {}, {}
        self.talk_flag = None       # set when the playing talk text is closed
        self.reward_flag = None     # set when a reward is claimed
        self.use_flag = None        # set when an item is used
        self.stashed, self.full_tabs = [], set()   # (tab, item id) put into the stash; tabs that refuse
        self.s = {"v": 1, "area": start, "areaName": start, "town": start.endswith("town"), "seed": 1, "instance": 1,
                  "character": "Tester", "classRow": 1, "level": 2, "xp": 0, "life": 50, "maxLife": 50,
                  "dead": False, "god": False, "immune": False, "pos": list(world[start]["spawn"]),
                  "passives": {"unspent": 0, "allocated": [], "candidates": []}, "flags": [], "waypoints": [],
                  "talking": 0, "inTalk": False, "reward": None, "equipped": {"weapon": None},
                  "inventory": {"w": 12, "h": 5, "items": []}}
        self.s.update(player)

    # --- helpers ---
    def here(self):
        return self.world[self.s["area"]]

    def entity(self, ident):
        place = self.here()
        for key in ("npcs", "objects", "items", "transitions", "monsters"):
            for entry in place[key]:
                if entry["id"] == ident:
                    return key, entry
        if place["waypoint"] and place["waypoint"]["id"] == ident:
            return "waypoint", place["waypoint"]
        return None, None

    def flag(self, name):
        if name not in self.s["flags"]:
            self.s["flags"].append(name)

    def enter(self, target, seed=None):
        self.s.update(area=target, areaName=target, pos=list(self.world[target]["spawn"]), talking=0,
                      inTalk=False, reward=None)
        if target in self.on_enter:
            self.on_enter[target](self, seed)

    def state_lines(self, brief):
        value = copy.deepcopy(self.s)
        place = self.here()
        value["waypoint"] = place["waypoint"]
        value["stash"] = place.get("stash")
        if not brief:
            for key in ("npcs", "objects", "items", "transitions", "monsters"):
                value[key] = copy.deepcopy(place[key])
        else:
            value.pop("inventory")
        text = json.dumps(value)
        chunks = [text[i:i + self.split] for i in range(0, len(text), self.split)]
        lines = ["STATE " + chunks[0]] + ["STATE+ " + c for c in chunks[1:]]
        if self.partial_states:
            self.partial_states -= 1
            return lines[:1]
        return ["someone else's reply"] + lines

    # --- the bridge ---
    def execute(self, request):
        self.calls.append(dict(request))
        command = request["command"]
        if command in self.unknown:
            raise ValueError(f"Unknown command: {command}")
        if command == "game:open":
            return {"alreadyRunning": True, "screen": self.screen}
        if command == "game:status":
            return {"running": True, "screen": self.screen, "instanceState": 3 if self.screen == "game" else 0,
                    "character": self.character if self.screen == "game" else None,
                    "area": self.s["area"] if self.screen == "game" else None}
        if command == "login":
            self.passwords.append(request["password"])
            self.screen = "characters"
            return {"screen": "characters"}
        if command == "character:list":
            return {"characters": copy.deepcopy(self.characters)}
        if command == "character:create":
            self.characters.append({"index": len(self.characters), "name": request["name"], "league":
                                    request["league"], "level": 1, "classId": 0})
            return {"character": self.characters[-1]}
        if command == "character:delete":
            self.characters = [c for c in self.characters if c["name"] != request["name"]]
            return {}
        if command == "character:play":
            self.screen, self.character = "game", request["name"]
            return {"screen": "game"}
        if command == "character:select":
            self.screen = "characters"
            return {}
        if command == "area:wait":
            if not self.area_wait:
                raise ValueError("Unknown command: area:wait")
            if "from" in request and not isinstance(request["from"], dict):  # client_control.py's own check
                raise ValueError("from must be a previous status object")
            return {"area": self.s["area"]}
        if command == "command":
            return {"text": request["text"], "replies": self.slash(request["text"])}
        if command == "tp":
            if "id" in request:
                kind, entry = self.entity(request["id"])
                if entry is None:
                    return {"replies": ["No such object."]}
                self.s["pos"] = [entry["pos"][0] + 1, entry["pos"][1]]
                if kind == "waypoint" and self.s["area"] not in self.s["waypoints"]:
                    self.s["waypoints"].append(self.s["area"])   # the Gateway activates a waypoint the player comes near
                    entry["active"] = True
            elif "x" in request:
                self.s["pos"] = [request["x"], request["y"]]
            return {"replies": []}
        if command == "waypoint:travel":
            if request["area"] in self.s["waypoints"]:
                self.enter(request["area"])
            return {}
        if command == "object:interact":
            kind, entry = self.entity(request["id"])
            if kind == "transitions":
                self.enter(entry["to"])
            elif kind == "npcs":
                self.s["talking"] = entry["id"]
                if entry.get("greeting"):
                    self.s["inTalk"] = True
                    self.talk_flag = entry.pop("greeting")
            elif kind == "objects":
                entry["used"] = True
            elif kind == "items":
                self.pick(entry)
            if request["id"] in self.on_interact:
                self.on_interact[request["id"]](self)
            return {}
        if command == "npc:option":
            hook = self.on_option.get((self.s["talking"], request["index"]))
            if hook:
                hook(self)
            return {}
        if command == "npc:close":
            if self.s["inTalk"]:
                self.s["inTalk"] = False
                if self.talk_flag:
                    self.flag(self.talk_flag)
                    self.talk_flag = None
            else:
                self.s["talking"] = 0
            return {}
        if command == "pantheon:select":
            info = self.s["pantheon"]
            god = next(g for g in info["unlocked"] if g["row"] == request["row"])
            if not self.drops_pantheon:   # a Gateway without the 0x1ee framing drops the choice
                info["major" if god["major"] else "minor"] = god["id"]
            return {"row": request["row"]}
        if command == "reward:claim":
            offer = self.s["reward"]
            item = next(i for i in offer["items"] if i["id"] == request["id"])
            self.s["inventory"]["items"].append({**item, "x": request["x"], "y": request["y"], "quest": False})
            self.s["reward"] = None
            if self.reward_flag:
                self.flag(self.reward_flag)
            return {}
        if command == "item:pickup":
            _, entry = self.entity(request["id"])
            self.pick(entry)
            return {}
        if command == "item:use":
            item = next(i for i in self.s["inventory"]["items"] if i["id"] == request["id"])
            self.s["inventory"]["items"].remove(item)
            if self.use_flag:
                self.flag(self.use_flag)
            return {}
        if command == "passive:allocate":
            info = self.s["passives"]
            if request["node"] not in self.reject_nodes and info["unspent"] > 0:
                info["unspent"] -= 1
                info["allocated"].append(request["node"])
                info["candidates"].remove(request["node"])
            return {}
        if command == "inventory:pickup":
            self.cursor = next(i for i in self.s["inventory"]["items"] if i["id"] == request["id"])
            self.s["inventory"]["items"].remove(self.cursor)
            return {}
        if command == "stash:deposit":
            if request["tab"] not in self.full_tabs:
                item = next(i for i in self.s["inventory"]["items"] if i["id"] == request["id"])
                self.s["inventory"]["items"].remove(item)
                self.stashed.append((request["tab"], item["id"]))
            return {"id": request["id"]}
        if command == "inventory:place":
            if request["inventoryId"] == 3:
                self.s["equipped"]["weapon"] = self.cursor
            return {}
        raise ValueError(f"Unknown command: {command}")

    def pick(self, entry):
        self.here()["items"].remove(entry)
        x, y = steps.free_cell(self.s["inventory"], entry.get("w", 1), entry.get("h", 1))
        self.s["inventory"]["items"].append({k: v for k, v in entry.items() if k != "pos"} | {"x": x, "y": y})

    def slash(self, text):
        words = text.split()
        if words[0] == "/state":
            return self.state_lines(brief=len(words) > 1 and words[1] == "brief")
        if words[0] == "/goto":
            self.enter(words[1], int(words[2]) if len(words) > 2 else None)
            return []
        if words[0] == "/kill":
            target = " ".join(words[1:])
            killed = steps.find_monsters({**self.s, "monsters": self.here()["monsters"]}, target)
            for monster in killed:
                monster["alive"] = False
                if monster["name"] in self.on_kill:
                    self.on_kill[monster["name"]](self)
            return [f"Killed {len(killed)}: " + ", ".join(m["name"] for m in killed)] if killed else \
                [f"No monster matches {target}."]
        if words[0] == "/god":
            mode = words[1] if len(words) > 1 else None
            on = mode != "off" and (mode == "immune" or not self.s["god"])
            self.s.update(god=on, immune=on and mode == "immune")
            return [f"God mode {'on' if on else 'off'}."]
        if words[0] == "/quest_preset":
            self.flag(f"preset-{words[1]}")
            return [f"Preset {words[1]} applied."]
        return []


def make_bridge(server, directory):
    clock = FakeClock()
    log_path = Path(directory) / "live-gateway.log"
    log_path.write_text("".join(f"line {i}\n" for i in range(100)), encoding="utf-8")
    return Bridge(control=server, gateway_log=log_path, sleep=clock.sleep, clock=clock.time)


def act1_world():
    return {
        "1_1_town": area(
            npcs=[{"id": 13000, "npc": "Metadata/NPC/Act1/Tarkleigh", "name": "Tarkleigh", "pos": [50, 50],
                   "options": [{"index": 1, "text": "Hillock Reward", "category": "QuestReward", "reward": 1}]},
                  {"id": 13001, "npc": "Metadata/NPC/Act1/Nessa", "name": "Nessa", "pos": [60, 50],
                   "options": [{"index": 0, "text": "Mercy Mission", "category": "Quest"}]}],
            transitions=[{"id": 300, "to": "1_1_2", "name": "The Coast", "pos": [90, 90]},
                         {"id": 305, "to": "1_1_1", "name": "The Twilight Strand", "pos": [120, 90]}],
            waypoint={"id": 400, "pos": [40, 40], "active": True}),
        "1_1_1": area(monsters=[{"id": 4660, "variety": "Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal",
                                 "name": "Hillock", "rarity": "Unique", "alive": True, "pos": [200, 200],
                                 "boss": True, "hostile": True},
                                {"id": 4661, "variety": "Metadata/Monsters/Zombie", "name": "Drowned",
                                 "rarity": "Normal", "alive": True, "pos": [20, 20], "hostile": True}],
                      transitions=[{"id": 301, "to": "1_1_town", "name": "Lioneye's Watch", "pos": [300, 300]}]),
        "1_1_2": area(waypoint={"id": 401, "pos": [30, 30], "active": True},
                      transitions=[{"id": 302, "to": "1_1_town", "name": "Lioneye's Watch", "pos": [5, 5]},
                                   {"id": 303, "to": "1_1_2a", "name": "The Tidal Island", "pos": [80, 5]}],
                      objects=[{"id": 20000, "type": "Metadata/Chests/QuestChests/RhoaChest", "name": "RhoaChest",
                                "pos": [70, 70], "used": False}],
                      items=[{"id": 1048576, "base": "Metadata/Items/QuestItems/MedicineChest",
                              "name": "Medicine Chest", "class": "QuestItem", "pos": [44, 45], "w": 2, "h": 2}]),
        "1_1_2a": area(),
        "1_1_3": area(),
    }


class StateParsingTests(unittest.TestCase):
    def test_joins_state_and_continuation_lines(self):
        text = json.dumps({"v": 1, "area": "1_1_1", "flags": ["a", "b"], "pos": [1, 2]})
        lines = ["noise", "STATE " + text[:10], "other command's line", "STATE+ " + text[10:25], "STATE+ " + text[25:]]
        self.assertEqual(parse_state(lines)["area"], "1_1_1")
        self.assertIsNone(parse_state(["STATE " + text[:10]]))
        self.assertIsNone(parse_state([]))
        old = "STATE " + json.dumps({"area": "old"})
        self.assertEqual(parse_state([old, "STATE " + text])["area"], "1_1_1")

    def test_state_retries_a_partial_reply_and_fails_after_three(self):
        with tempfile.TemporaryDirectory() as directory:
            server = FakeServer(act1_world())
            bridge = make_bridge(server, directory)
            server.partial_states = 2
            self.assertEqual(bridge.state()["area"], "1_1_town")
            self.assertEqual(sum(1 for c in server.calls if c.get("text") == "/state"), 3)
            server.partial_states = 3
            with self.assertRaises(StateUnavailable):
                bridge.state(brief=True)

    def test_chat_waits_while_the_client_loads_an_area(self):
        # After an NPC's AreaTransitionTo the client has no active area for a moment: /state waits instead of failing.
        with tempfile.TemporaryDirectory() as directory:
            server = FakeServer(act1_world())
            bridge = make_bridge(server, directory)
            original, loading = server.execute, [3]

            def execute(request):
                if request["command"] == "command" and loading[0] > 0:
                    loading[0] -= 1
                    raise RuntimeError("Error: Chat requires an active area")
                return original(request)
            server.execute = execute
            bridge.control = server
            self.assertEqual(bridge.state()["area"], "1_1_town")
            server.execute = lambda request: (_ for _ in ()).throw(RuntimeError("Multiple patched clients are running"))
            with self.assertRaises(RuntimeError):
                bridge.state()

    def test_brief_state_omits_lists(self):
        with tempfile.TemporaryDirectory() as directory:
            bridge = make_bridge(FakeServer(act1_world()), directory)
            self.assertNotIn("npcs", bridge.state(brief=True))
            self.assertIn("npcs", bridge.state())

    def test_wait_until_times_out_with_last_state(self):
        with tempfile.TemporaryDirectory() as directory:
            bridge = make_bridge(FakeServer(act1_world()), directory)
            with self.assertRaises(WaitTimeout) as caught:
                bridge.wait_until(lambda s: False, timeout=2, what="never")
            self.assertEqual(caught.exception.state["area"], "1_1_town")


class StepTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.server = FakeServer(act1_world())
        self.bridge = make_bridge(self.server, self.directory.name)
        self.ctx = steps.Context(self.bridge, class_name="Witch")
        self.classes = [{"Name": "Marauder", "BaseStrength": 32, "BaseDexterity": 14, "BaseIntelligence": 14},
                        {"Name": "Witch", "BaseStrength": 14, "BaseDexterity": 14, "BaseIntelligence": 32}]
        self.ctx._classes = self.classes

    def tearDown(self):
        self.directory.cleanup()

    def commands(self, name):
        return [c for c in self.server.calls if c["command"] == name]

    def texts(self):
        return [c["text"] for c in self.commands("command") if not c["text"].startswith("/state")]

    def test_goto_prefers_a_known_waypoint(self):
        self.server.s["waypoints"] = ["1_1_town", "1_1_2"]
        steps.run_step(self.ctx, {"goto": "1_1_2"})
        self.assertEqual(self.server.s["area"], "1_1_2")
        self.assertEqual(self.commands("waypoint:travel"), [{"command": "waypoint:travel", "area": "1_1_2"}])
        self.assertIn({"command": "tp", "id": 400}, self.server.calls)

    def test_goto_uses_a_transition_without_the_waypoint(self):
        steps.run_step(self.ctx, {"verb": "goto", "area": "1_1_2"})
        self.assertEqual(self.server.s["area"], "1_1_2")
        self.assertEqual(self.commands("object:interact"), [{"command": "object:interact", "id": 300}])
        self.assertFalse(self.commands("waypoint:travel"))

    def test_goto_routes_exit_by_exit_instead_of_the_command(self):
        self.ctx._connections = {"1_1_town": ["1_1_2"], "1_1_2": ["1_1_town", "1_1_2a"], "1_1_2a": ["1_1_2"]}
        self.ctx._gates = {}
        steps.run_step(self.ctx, {"verb": "goto", "area": "1_1_2a"})
        self.assertEqual(self.server.s["area"], "1_1_2a")
        self.assertEqual([c["id"] for c in self.commands("object:interact")], [300, 303])
        self.assertNotIn("/goto 1_1_2a", self.texts())

    def test_goto_activates_the_waypoint_of_an_area_it_arrives_in(self):
        self.server.world["1_1_2"]["waypoint"]["active"] = False
        steps.run_step(self.ctx, {"verb": "goto", "area": "1_1_2"})
        self.assertIn("1_1_2", self.server.s["waypoints"])
        self.assertIn({"command": "tp", "id": 401}, self.commands("tp"))

    def test_route_uses_the_nearest_waypoint_then_exits(self):
        links = {"a": ["b"], "b": ["a", "c"], "c": ["b", "d"], "d": ["c"], "w": ["c"]}
        self.assertEqual(steps.route(links, "a", "d", []), [("transition", "b"), ("transition", "c"), ("transition", "d")])
        self.assertEqual(steps.route(links, "a", "d", ["w"]), [("waypoint", "w"), ("transition", "c"), ("transition", "d")])
        self.assertEqual(steps.route(links, "a", "a", []), [])
        self.assertIsNone(steps.route(links, "a", "z", []))
        # a gated area (its EndQuestFlags not held) is not on the way
        self.assertIsNone(steps.route(links, "a", "d", [], enterable=lambda area: area != "c"))

    def test_goto_never_uses_the_command_and_tries_the_exit_after_an_ignored_waypoint(self):
        # No waypoint, exit or route to The Mud Flats: the step fails loudly instead of jumping there with /goto.
        self.ctx._connections = {}
        with self.assertRaises(steps.StepFailed) as failure:
            steps.run_step(self.ctx, {"do": "goto", "area": "1_1_3"})
        self.assertIn("NO ROUTE", str(failure.exception))
        with self.assertRaises(steps.StepFailed):
            steps.run_step(self.ctx, {"do": "goto", "area": "1_1_2", "via": "command"})
        self.assertEqual(self.texts(), [])
        # a waypoint the server ignores (not really activated) times out, then the next method is used
        self.server.enter("1_1_town")
        self.server.s["waypoints"] = ["1_1_2a"]
        self.server.world["1_1_town"]["transitions"].append({"id": 304, "to": "1_1_2a", "name": "x", "pos": [1, 1]})
        self.server.s["waypoints"] = ["1_1_2a"]
        original = self.server.execute

        def ignore_waypoints(request):
            if request["command"] == "waypoint:travel":
                self.server.calls.append(dict(request))
                return {}
            return original(request)
        self.server.execute = ignore_waypoints
        self.bridge.control = self.server
        steps.run_step(self.ctx, {"goto": "1_1_2a", "timeout": 2})
        self.assertEqual(self.server.s["area"], "1_1_2a")
        self.assertEqual(self.commands("object:interact")[-1], {"command": "object:interact", "id": 304})

    def test_goto_uses_area_wait_when_the_bridge_has_it(self):
        self.server.area_wait = True
        steps.goto(self.ctx, {"area": "1_1_2"})
        self.assertEqual(self.commands("area:wait")[0]["from"]["area"], "1_1_town")
        self.assertTrue(self.bridge.area_wait_supported)

    def test_kill_teleports_and_waits_for_death(self):
        self.server.enter("1_1_1")
        steps.run_step(self.ctx, {"kill": "Hillock"})
        self.assertIn({"command": "tp", "id": 4660}, self.server.calls)
        self.assertIn("/kill Hillock", self.texts())
        self.assertFalse(self.server.world["1_1_1"]["monsters"][0]["alive"])
        steps.run_step(self.ctx, {"kill": "boss"})  # already dead: no second /kill
        self.assertEqual(self.texts().count("/kill boss"), 0)
        with self.assertRaises(steps.StepFailed):
            steps.run_step(self.ctx, {"kill": "Merveil"})

    def test_pickup_moves_to_the_item_and_waits_for_the_inventory(self):
        self.server.enter("1_1_2")
        steps.run_step(self.ctx, {"pickup": "Medicine"})
        self.assertIn({"command": "tp", "x": 44, "y": 45}, self.server.calls)
        self.assertEqual(self.commands("item:pickup"), [{"command": "item:pickup", "id": 1048576}])
        self.assertEqual(self.server.s["inventory"]["items"][0]["name"], "Medicine Chest")
        steps.run_step(self.ctx, {"pickup": "Metadata/Items/QuestItems/MedicineChest"})  # already picked up
        self.assertEqual(len(self.commands("item:pickup")), 1)

    def test_pickup_falls_back_to_object_interact_and_matches_lists(self):
        self.server.unknown.add("item:pickup")
        self.server.enter("1_1_2")
        steps.run_step(self.ctx, {"pickup": ["Wand", "QuestItem"]})
        self.assertIn({"command": "object:interact", "id": 1048576}, self.server.calls)
        self.assertEqual(len(self.server.s["inventory"]["items"]), 1)

    def test_interact_waits_for_used_and_expect_flag(self):
        self.server.enter("1_1_2")
        self.server.on_interact[20000] = lambda s: s.flag("a1q4-SawGlyph")
        steps.run_step(self.ctx, {"interact": "RhoaChest", "expect_flag": "a1q4-SawGlyph"})
        self.assertTrue(self.server.world["1_1_2"]["objects"][0]["used"])
        steps.run_step(self.ctx, {"interact": "Tidal"})  # a transition
        self.assertEqual(self.server.s["area"], "1_1_2a")

    def test_talk_closes_talk_texts_and_the_dialogue(self):
        nessa = self.server.world["1_1_town"]["npcs"][1]
        nessa["greeting"] = "a1-MetNessa"

        def option(server):
            server.s["inTalk"] = True
            server.talk_flag = "a1q5-TalkedToNessa"
        self.server.on_option[(13001, 0)] = option
        steps.run_step(self.ctx, {"talk": "Nessa", "option": "mercy"})
        self.assertEqual(self.commands("npc:option"), [{"command": "npc:option", "index": 0}])
        self.assertIn("a1-MetNessa", self.server.s["flags"])
        self.assertIn("a1q5-TalkedToNessa", self.server.s["flags"])
        self.assertEqual(self.server.s["talking"], 0)
        self.assertFalse(self.server.s["inTalk"])
        self.assertEqual(len(self.commands("npc:close")), 3)  # greeting, option text, dialogue

    def test_talk_goes_to_the_npc_town(self):
        self.server.enter("1_1_2")
        steps.run_step(self.ctx, {"talk": "Tarkleigh"})
        self.assertEqual(self.server.s["area"], "1_1_town")
        self.assertEqual(steps.npc_town("Sin", "2_7_4"), "2_7_town")
        self.assertEqual(steps.npc_town("Tarkleigh", "2_6_1"), "2_6_town")
        self.assertEqual(steps.npc_town("Tarkleigh", "1_2_1"), "2_6_town")
        self.assertEqual(steps.npc_town("Tarkleigh", None), "2_6_town")
        self.assertIsNone(steps.npc_town("Nobody", "1_1_1"))

    def offer(self, server):
        server.s["reward"] = {"offer": 5, "items": [
            {"id": 1, "base": "Metadata/Items/Gems/SkillGemHeavyStrike", "name": "Heavy Strike",
             "class": "Active Skill Gem", "w": 1, "h": 1},
            {"id": 2, "base": "Metadata/Items/Flasks/FlaskLife1", "name": "Small Life Flask", "class": "Life Flask",
             "w": 1, "h": 2},
            {"id": 3, "base": "Metadata/Items/Gems/SkillGemFireball", "name": "Fireball", "class": "Active Skill Gem",
             "w": 1, "h": 1},
            {"id": 4, "base": "Metadata/Items/Weapons/TwoHandWeapons/Staves/Staff1", "name": "Gnarled Branch",
             "class": "Staff", "w": 1, "h": 3}]}

    def test_reward_picks_by_preference_then_heuristic(self):
        gems = {"Metadata/Items/Gems/SkillGemHeavyStrike": (100, 0, 0, False),
                "Metadata/Items/Gems/SkillGemFireball": (0, 0, 100, False)}
        self.ctx._gems = gems
        self.server.on_option[(13000, 1)] = self.offer
        self.server.reward_flag = "a1q1-RewardTaken"
        steps.run_step(self.ctx, {"reward": "Tarkleigh", "option": "Hillock"})
        claim = self.commands("reward:claim")[0]
        self.assertEqual((claim["id"], claim["x"], claim["y"]), (3, 0, 0))
        self.assertIn("a1q1-RewardTaken", self.server.s["flags"])
        self.assertEqual(self.server.s["talking"], 0)
        # a preference list wins over the heuristic; the Gnarled Branch (1x3) goes under the Fireball
        steps.run_step(self.ctx, {"reward": "Tarkleigh", "option": "Hillock", "pick": ["Branch", "Flask"]})
        claim = self.commands("reward:claim")[1]
        self.assertEqual((claim["id"], claim["x"], claim["y"]), (4, 0, 1))
        # without a class the gem tables are neutral: a gem still beats a flask
        state = {"classRow": None}
        ctx = steps.Context(self.bridge)
        ctx._gems = gems
        ctx._classes = self.classes
        self.offer(self.server)
        self.assertEqual(steps.choose_reward(ctx, self.server.s["reward"], None, state)["id"], 1)
        marauder = steps.Context(self.bridge, class_name="Marauder")
        marauder._gems = gems
        marauder._classes = self.classes
        self.assertEqual(steps.choose_reward(marauder, self.server.s["reward"], None, state)["id"], 1)

    def test_a_reward_without_room_stashes_the_carried_items_and_is_claimed(self):
        town = self.server.world["1_1_town"]
        town["stash"] = {"id": 0x777, "pos": [30, 30]}
        items = [{"id": 100 + n, "base": "Metadata/Items/Rings/Ring1", "name": "Iron Ring", "class": "Ring",
                  "w": 1, "h": 1, "x": n % 12, "y": n // 12, "quest": False} for n in range(59)]
        items.append({"id": 99, "base": "Metadata/Items/QuestItems/Book", "name": "Book", "class": "QuestItem",
                      "w": 1, "h": 1, "x": 11, "y": 4, "quest": True})
        self.server.s["inventory"]["items"] = [dict(i) for i in items]
        self.server.full_tabs = {0}   # the first tab is full: the next takes them
        self.server.on_option[(13000, 1)] = self.offer
        steps.run_step(self.ctx, {"reward": "Tarkleigh", "option": "Hillock", "pick": ["Branch"]})
        self.assertEqual(59, len(self.server.stashed))
        self.assertEqual({1}, {tab for tab, _ in self.server.stashed})
        held = self.server.s["inventory"]["items"]
        self.assertEqual({99, 4}, {i["id"] for i in held})      # the quest item stays, the reward came
        self.assertIn(0x777, [c.get("id") for c in self.commands("object:interact")])
        # Without a Stash anywhere the step fails with the reason.
        del town["stash"]
        self.server.s["inventory"]["items"] = [dict(i) for i in items]
        with self.assertRaises(steps.StepFailed):
            steps.run_step(self.ctx, {"reward": "Tarkleigh", "option": "Hillock", "pick": ["Branch"]})

    def test_without_a_stash_here_the_items_go_to_lioneyes_watch_by_waypoint_and_back(self):
        self.server.world["1_1_town"]["stash"] = {"id": 0x777, "pos": [30, 30]}
        self.server.s["waypoints"] = ["1_1_town", "1_1_2"]
        self.server.s["inventory"]["items"] = [{"id": 5, "base": "Metadata/Items/Rings/Ring1", "name": "Iron Ring",
                                                "class": "Ring", "w": 1, "h": 1, "x": 0, "y": 0, "quest": False}]
        self.server.enter("1_1_2")
        self.assertEqual(1, steps.stash_items(self.ctx))
        self.assertEqual([(0, 5)], self.server.stashed)
        self.assertEqual("1_1_2", self.server.s["area"])
        self.assertEqual(["1_1_town", "1_1_2"], [c["area"] for c in self.commands("waypoint:travel")])

    def test_reward_without_item_panel_is_accepted_when_the_option_goes(self):
        def book(server):
            server.world["1_1_town"]["npcs"][0]["options"] = []
            server.s["passives"]["unspent"] += 1
        self.server.on_option[(13000, 1)] = book
        steps.run_step(self.ctx, {"reward": "Tarkleigh", "offer_timeout": 1})
        self.assertFalse(self.commands("reward:claim"))
        self.assertEqual(self.server.s["passives"]["unspent"], 1)

    def test_talk_and_reward_by_option_index_or_row(self):
        tarkleigh = self.server.world["1_1_town"]["npcs"][0]
        tarkleigh["options"] = [{"index": 3, "row": 623, "text": "Organ", "category": "Quests"},
                                {"index": 4, "row": 624, "text": "Organ", "category": "Quests"},
                                {"index": 1, "row": 40, "text": "Hillock Reward", "category": "QuestReward", "reward": 1}]
        steps.run_step(self.ctx, {"talk": "Tarkleigh", "option": "organ", "index": 4})
        steps.run_step(self.ctx, {"talk": "Tarkleigh", "row": 623})
        self.assertEqual([c["index"] for c in self.commands("npc:option")], [4, 3])
        with self.assertRaises(steps.StepFailed):
            steps.run_step(self.ctx, {"talk": "Tarkleigh", "option": "organ", "index": 1})
        self.server.on_option[(13000, 1)] = self.offer
        steps.run_step(self.ctx, {"reward": "Tarkleigh", "row": 40})
        self.assertEqual(self.commands("npc:option")[-1]["index"], 1)
        self.assertEqual(len(self.commands("reward:claim")), 1)

    def test_talk_prefers_the_unheard_row_of_the_same_text(self):
        yeena = self.server.world["1_1_town"]["npcs"][0]
        yeena["options"] = [{"index": 14, "row": 133, "text": "The Fellshrine Hand", "category": "Quests", "heard": True},
                            {"index": 15, "row": 134, "text": "The Fellshrine Hand", "category": "Quests", "heard": False}]
        steps.run_step(self.ctx, {"talk": "Tarkleigh", "option": "Fellshrine"})
        self.assertEqual(self.commands("npc:option")[-1]["index"], 15)
        yeena["options"][1]["heard"] = True
        steps.run_step(self.ctx, {"talk": "Tarkleigh", "option": "Fellshrine"})
        self.assertEqual(self.commands("npc:option")[-1]["index"], 14)

    def test_use_item_right_clicks_the_book_and_waits_until_it_is_gone(self):
        self.server.s["inventory"]["items"] = [{"id": 81, "base": "Metadata/Items/QuestItems/SkillBooks/Book-a1q6",
                                                "name": "Book of Skill", "class": "QuestItem", "x": 0, "y": 0, "w": 1,
                                                "h": 1, "quest": True}]
        self.server.use_flag = "a1q6-UsedRewardBook"
        steps.run_step(self.ctx, {"use": "Book-a1q6", "expect_flag": "a1q6-UsedRewardBook"})
        self.assertEqual(self.commands("item:use"), [{"command": "item:use", "inventoryId": 1, "id": 81}])
        self.assertEqual(self.server.s["inventory"]["items"], [])
        # used before (the flag is set, no book carried): nothing to do; without the flag it fails
        steps.run_step(self.ctx, {"verb": "use_item", "item": "Book-a1q6", "expect_flag": "a1q6-UsedRewardBook"})
        self.assertEqual(len(self.commands("item:use")), 1)
        with self.assertRaises(steps.StepFailed):
            steps.run_step(self.ctx, {"use_item": "Book-a1q7"})

    def test_free_cell_for_tall_items_and_a_nearly_full_inventory(self):
        inventory = {"w": 12, "h": 5, "items": [{"id": 1, "x": 0, "y": 0, "w": 2, "h": 2},
                                                {"id": 2, "x": 0, "y": 3, "w": 1, "h": 1}]}
        self.assertEqual(steps.free_cell(inventory, 1, 1), (0, 2))
        self.assertEqual(steps.free_cell(inventory, 1, 3), (1, 2))
        full = {"w": 12, "h": 5, "items": [{"id": i, "x": i % 12, "y": i // 12, "w": 1, "h": 1}
                                            for i in range(60) if i not in (59, 47, 35)]}
        self.assertEqual(steps.free_cell(full, 1, 3), (11, 2))
        self.assertEqual(steps.free_cell(full, 1, 1), (11, 2))
        self.assertIsNone(steps.free_cell(full, 2, 1))
        full["items"].append({"id": 99, "x": 11, "y": 2, "w": 1, "h": 1})
        self.assertIsNone(steps.free_cell(full, 1, 3))
        self.assertEqual(steps.free_cells(full), 2)

    def test_skip_if_and_wait_flag(self):
        self.server.s["flags"] = ["a1q1-KilledHillock"]
        self.assertEqual(steps.run_step(self.ctx, {"kill": "Hillock", "skip_if": "a1q1-KilledHillock"}), "skipped")
        self.assertNotIn("/kill Hillock", self.texts())
        steps.run_step(self.ctx, {"wait_flag": "a1q1-KilledHillock"})
        with self.assertRaises(WaitTimeout):
            steps.run_step(self.ctx, {"wait_flag": "never", "timeout": 2})

    def test_passives_spend_points_and_skip_refused_nodes(self):
        self.server.s["passives"] = {"unspent": 2, "allocated": [], "candidates": [10, 11, 12]}
        self.server.reject_nodes = {10}
        steps.run_step(self.ctx, {"passives": None})   # points stay unspent by default
        self.assertEqual(self.commands("passive:allocate"), [])
        self.ctx.spend_passives = True
        steps.run_step(self.ctx, {"passives": None})
        self.assertEqual([c["node"] for c in self.commands("passive:allocate")], [10, 11, 12])
        self.assertEqual(self.server.s["passives"]["unspent"], 0)
        steps.passives(self.ctx)  # nothing to spend: no request
        self.assertEqual(len(self.commands("passive:allocate")), 3)

    def test_equip_moves_the_item_into_the_weapon_slot(self):
        self.server.s["inventory"]["items"] = [{"id": 77, "base": "Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1",
                                                "name": "Driftwood Wand", "class": "Wand", "x": 0, "y": 0, "w": 1,
                                                "h": 3, "quest": False}]
        steps.run_step(self.ctx, {"equip": ["Sword", "wand"]})
        self.assertEqual(self.commands("inventory:pickup"), [{"command": "inventory:pickup", "inventoryId": 1, "id": 77}])
        self.assertEqual(self.commands("inventory:place"), [{"command": "inventory:place", "inventoryId": 3, "x": 0,
                                                             "y": 0}])
        steps.run_step(self.ctx, {"equip": "Wand"})  # already equipped
        self.assertEqual(len(self.commands("inventory:pickup")), 1)

    def test_step_forms_and_validation(self):
        self.assertEqual(steps.normalize({"talk": "Tarkleigh", "option": "x"}),
                         {"verb": "talk", "npc": "Tarkleigh", "option": "x"})
        self.assertEqual(steps.normalize({"action": "kill", "target": "boss"}), {"verb": "kill", "target": "boss"})
        self.assertEqual(steps.normalize({"passives": True}), {"verb": "passives"})
        self.assertEqual(steps.normalize({"verb": "use", "item": "Book"}), {"verb": "use_item", "item": "Book"})
        self.assertEqual(steps.normalize({"use": "Book", "skip_if": "x"}), {"verb": "use_item", "item": "Book", "skip_if": "x"})
        self.assertIsNone(steps.validate({"verb": "use", "item": "Book"}))
        self.assertIsNone(steps.validate({"wait_flag": "x"}))
        self.assertIn("unknown verb", steps.validate({"verb": "fly"}))
        self.assertIn("needs area", steps.validate({"verb": "goto"}))
        self.assertIn("tp needs", steps.validate({"verb": "tp"}))
        self.assertIsNone(steps.validate({"verb": "pantheon"}))

    def pantheon_state(self, *ids, can_choose=True):
        gods = {"TheBrineKing": (0, True, "Soul of the Brine King"), "Arakaali": (1, True, "Soul of Arakaali"),
                "Tukohama": (11, False, "Soul of Tukohama"), "Ryslatha": (15, False, "Soul of Ryslatha")}
        self.server.s["pantheon"] = {"major": None, "minor": None, "canChoose": can_choose, "unlocked": [
            {"id": i, "name": gods[i][2], "major": gods[i][1], "row": gods[i][0]} for i in ids]}

    def test_pantheon_picks_the_preferred_unlocked_gods(self):
        self.pantheon_state("TheBrineKing", "Tukohama", "Ryslatha")
        steps.run_step(self.ctx, {"verb": "pantheon"})
        self.assertEqual((self.server.s["pantheon"]["major"], self.server.s["pantheon"]["minor"]), ("TheBrineKing", "Tukohama"))
        self.assertEqual([c["row"] for c in self.server.calls if c["command"] == "pantheon:select"], [0, 11])
        # Arakaali comes before The Brine King once unlocked; a step's own choice comes first.
        self.pantheon_state("TheBrineKing", "Arakaali", "Tukohama", "Ryslatha")
        steps.run_step(self.ctx, {"verb": "pantheon", "minor": "Ryslatha"})
        self.assertEqual((self.server.s["pantheon"]["major"], self.server.s["pantheon"]["minor"]), ("Arakaali", "Ryslatha"))

    def test_pantheon_leaves_a_slot_without_unlocked_gods_and_needs_a_town(self):
        self.pantheon_state("Tukohama")
        steps.run_step(self.ctx, {"verb": "pantheon"})
        self.assertEqual((self.server.s["pantheon"]["major"], self.server.s["pantheon"]["minor"]), (None, "Tukohama"))
        calls = len(self.server.calls)
        steps.run_step(self.ctx, {"verb": "pantheon"})   # already chosen: nothing sent
        self.assertFalse(any(c["command"] == "pantheon:select" for c in self.server.calls[calls:]))
        self.pantheon_state("TheBrineKing", can_choose=False)
        with self.assertRaises(steps.StepFailed):
            steps.run_step(self.ctx, {"verb": "pantheon"})

    def test_pantheon_fails_loudly_when_the_server_does_not_apply_the_choice(self):
        # Batch C's Act 7 run: the client sent 0x1ee but the Gateway logged it unframed and nothing changed.
        self.pantheon_state("TheBrineKing")
        self.server.drops_pantheon = True
        with self.assertRaises(steps.StepFailed) as failed:
            steps.run_step(self.ctx, {"verb": "pantheon", "timeout": 0.5})
        self.assertIn("0x01ee", str(failed.exception))



class LoadingTests(unittest.TestCase):
    def test_state_waits_through_an_area_load(self):
        # The bridge refuses chat while the client loads an area: /state counts that as no reply yet.
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        server = FakeServer(act1_world())
        bridge = make_bridge(server, directory.name)
        original, refusals = server.execute, [2]

        def loading(request):
            if request["command"] == "command" and refusals[0] > 0:
                refusals[0] -= 1
                raise ValueError("Error: Chat requires an active area")
            return original(request)
        server.execute = loading
        bridge.control = server
        self.assertEqual(bridge.wait_until(lambda s: True, timeout=5)["area"], server.s["area"])


QUEST_A = '''
QUEST = "qa"
DONE = "qa-Done"
STEPS = [
    {"goto": "1_1_1"},
    {"kill": "Hillock", "skip_if": "qa-Killed"},
    {"command": "/noop"},
    {"verb": "wait_flag", "flag": "qa-Done"},
]
'''


class RunnerTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.quests = Path(self.directory.name) / "quests"
        self.quests.mkdir()
        self.server = FakeServer(act1_world())
        self.server.on_kill["Hillock"] = lambda s: (s.flag("qa-Killed"), s.flag("qa-Done"))
        self.bridge = make_bridge(self.server, self.directory.name)

    def tearDown(self):
        runner.close_logging()
        self.directory.cleanup()

    def write(self, name, text):
        (self.quests / f"{name}.py").write_text(textwrap.dedent(text), encoding="utf-8")

    def run_main(self, *argv, order=("qa", "qb", "qc")):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            code = runner.main(list(argv), bridge=self.bridge, quests_dir=self.quests, order=list(order),
                               log_dir=Path(self.directory.name) / "logs", password_reader=lambda: "secret-pw")
        return code, output.getvalue()

    def test_a_second_run_on_the_same_session_stops(self):
        self.write("qa", QUEST_A)
        held = runner.SessionLock("Tester")   # another run of this checkout drives session Tester
        try:
            if held.handle is None:
                self.skipTest("named mutexes are Windows-only")
            code, _ = self.run_main("--session", "Tester", "--character", "Tester", "--only", "qa")
            self.assertEqual(code, 2)
            self.assertEqual(self.server.calls, [])   # nothing was sent to the client
        finally:
            held.close()
        code, _ = self.run_main("--session", "Tester", "--character", "Tester", "--only", "qa")
        self.assertEqual(code, 0)

    def test_select_only_from_to(self):
        order = ["a", "b", "c", "d"]
        self.assertEqual(runner.select(order), order)
        self.assertEqual(runner.select(order, only="c,a"), ["a", "c"])
        self.assertEqual(runner.select(order, start="b"), ["b", "c", "d"])
        self.assertEqual(runner.select(order, end="b"), ["a", "b"])
        self.assertEqual(runner.select(order, start="b", end="c"), ["b", "c"])
        with self.assertRaises(ValueError):
            runner.select(order, only="x")

    def test_run_then_resume_does_nothing(self):
        self.write("qa", QUEST_A)
        code, output = self.run_main()
        self.assertEqual(code, 0)
        self.assertRegex(output, r"qa\s+done")
        self.assertRegex(output, r"qb\s+not implemented")
        self.assertIn("/kill Hillock", [c.get("text") for c in self.server.calls])
        self.assertTrue(self.server.s["god"] and self.server.s["immune"])
        calls = len(self.server.calls)
        code, output = self.run_main()
        self.assertEqual(code, 0)
        self.assertRegex(output, r"qa\s+skipped \(already\)")
        actions = [c for c in self.server.calls[calls:]
                   if not (c["command"] == "command" and c["text"].startswith("/state"))]
        self.assertEqual([c["command"] for c in actions], ["game:open", "game:status"])
        logs = list((Path(self.directory.name) / "logs").glob("campaign-*.log"))
        self.assertTrue(logs)
        self.assertNotIn("secret-pw", "".join(p.read_text(encoding="utf-8") for p in logs))

    def test_only_selects_and_blocked_follows_missing_requirements(self):
        self.write("qa", QUEST_A)
        self.write("qc", '''
            QUEST = "qc"
            DONE = "qc-Done"
            REQUIRES = ["qb"]
            STEPS = [{"command": "/noop"}]
        ''')
        code, output = self.run_main("--only", "qc")
        self.assertEqual(code, 0)
        self.assertRegex(output, r"qc\s+blocked\s+[\d.]+s\s+requires qb")
        self.assertNotRegex(output, r"qa\s+done")
        self.write("qb", '''
            QUEST = "qb"
            DONE = "qa-Done"
            STEPS = []
        ''')
        code, output = self.run_main("--from", "qb", "--to", "qc", "--no-god")
        self.assertEqual(code, 1)  # qb's DONE (qa-Done) is not set: qb fails, qc stays blocked
        self.assertRegex(output, r"qb\s+failed")
        self.assertRegex(output, r"qc\s+blocked")
        self.assertFalse(self.server.s["god"])

    def test_invalid_quest_file_fails_only_that_quest(self):
        self.write("qa", 'QUEST = "qa"\nDONE = "x"\nSTEPS = [{"verb": "fly"}]\n')
        self.write("qb", 'raise RuntimeError("broken")\n')
        code, output = self.run_main("--no-god")
        self.assertEqual(code, 1)
        self.assertRegex(output, r"qa\s+failed.*unknown verb")
        self.assertRegex(output, r"qb\s+failed.*broken")

    def test_retry_prints_diagnostics_and_recovers_with_a_new_instance(self):
        self.write("qa", QUEST_A)
        hillock = self.server.world["1_1_1"]["monsters"][0]
        self.server.world["1_1_1"]["monsters"].remove(hillock)

        entries = []

        def enter(server, seed):  # only the instance made when the area is entered again has Hillock
            entries.append(seed)
            if len(entries) >= 2 and hillock not in server.world["1_1_1"]["monsters"]:
                server.world["1_1_1"]["monsters"].append(hillock)
        self.server.on_enter["1_1_1"] = enter
        with self.assertLogs("campaign", level="WARNING") as logs:
            code, output = self.run_main()
        self.assertEqual(code, 0)
        text = "\n".join(logs.output)
        self.assertIn("step failed: qa: kill target='Hillock'", text)
        self.assertIn("area 1_1_1", text)
        self.assertIn("line 99", text)  # tail of the Gateway log
        self.assertIn("attempt 2/4 failed", text)
        texts = [c.get("text") or "" for c in self.server.calls]
        self.assertFalse([t for t in texts if t.startswith("/goto")])
        # recovered by leaving through the exit to Lioneye's Watch and coming back through its exit
        self.assertIn({"command": "object:interact", "id": 301}, self.server.calls)
        self.assertRegex(output, r"qa\s+done")

    def test_recovery_logs_in_again_when_the_client_left_the_game(self):
        class Stub:
            def __init__(self):
                self.screen, self.last_state, self.now = "login", None, 0.0

            def status(self):
                return {"screen": self.screen, "instanceState": 3 if self.screen == "game" else 0}

            def clock(self):
                return self.now

            def sleep(self, seconds):
                self.now += seconds

        stub = Stub()
        ctx = steps.Context(stub)
        calls = []
        ctx.reconnect = lambda: (calls.append("reconnect"), setattr(stub, "screen", "game"))
        runner.recover(ctx, 1)
        self.assertEqual(calls, ["reconnect"])
        # In game and /state answering: no reconnect, the usual recoveries.
        stub.state = lambda brief=False: {"area": "1_1_1"}
        self.assertFalse(runner.reconnect_if_needed(ctx))

        # In game but deaf: wait until the client drops to the login screen, then log in again.
        def deaf(brief=False):
            raise RuntimeError("no STATE reply")
        stub.state = deaf
        stub.sleep = lambda seconds: (setattr(stub, "now", stub.now + seconds),
                                      setattr(stub, "screen", "login") if stub.now > 3 else None)
        self.assertTrue(runner.reconnect_if_needed(ctx))
        self.assertEqual(calls, ["reconnect", "reconnect"])

    def test_step_failing_every_retry_fails_the_quest(self):
        self.write("qa", 'QUEST = "qa"\nDONE = "qa-Done"\nSTEPS = [{"kill": "Merveil"}]\n')
        code, output = self.run_main("--retries", "1", "--stop-on-fail")
        self.assertEqual(code, 1)
        self.assertRegex(output, r"qa\s+failed\s+[\d.]+s\s+step 1: kill target='Merveil'")
        self.assertRegex(output, r"qb\s+not run")

    def test_after_quest_claims_rewards_and_spends_points(self):
        self.write("qa", 'QUEST = "qa"\nDONE = "qa-Done"\nSTEPS = [{"command": "/quest_flag qa-Done"}]\n')
        original = self.server.slash

        def slash(text):
            if text == "/quest_flag qa-Done":
                self.server.flag("qa-Done")
                return []
            return original(text)
        self.server.slash = slash

        def offer(server):
            StepTests.offer(None, server)
            server.world["1_1_town"]["npcs"][0]["options"] = []
        self.server.on_option[(13000, 1)] = offer
        self.server.s["passives"] = {"unspent": 1, "allocated": [], "candidates": [5]}
        code, _ = self.run_main("--only", "qa")
        self.assertEqual(code, 0)
        self.assertEqual(len([c for c in self.server.calls if c["command"] == "reward:claim"]), 1)
        self.assertEqual(self.server.s["passives"]["unspent"], 1)   # left unspent without --spend-passives
        self.server.s["flags"].remove("qa-Done")
        code, _ = self.run_main("--only", "qa", "--spend-passives")
        self.assertEqual(self.server.s["passives"]["unspent"], 0)

    def test_new_character_session(self):
        self.server.screen = "login"
        self.server.characters.append({"index": 1, "name": "CampTest", "league": "Standard", "level": 1,
                                       "classId": 0})
        code, output = self.run_main("--new", "--name", "CampTest", "--only", "qa", "--preset", "Act1Complete")
        self.assertEqual(code, 2)  # exists, no --replace
        self.assertIn("--replace", output)
        self.server.screen = "login"
        code, output = self.run_main("--new", "--name", "CampTest", "--replace", "--class", "Ranger",
                                     "--only", "qa", "--preset", "Act1Complete")
        self.assertEqual(code, 0)
        self.assertEqual(self.server.passwords, ["secret-pw", "secret-pw"])
        create = [c for c in self.server.calls if c["command"] == "character:create"]
        self.assertEqual(create, [{"command": "character:create", "name": "CampTest", "class": "Ranger",
                                   "league": "Standard"}])
        self.assertIn({"command": "character:delete", "name": "CampTest", "missingOk": True}, self.server.calls)
        self.assertEqual(self.server.character, "CampTest")
        self.assertIn("preset-Act1Complete", self.server.s["flags"])

    def test_continue_requires_a_character(self):
        self.server.screen = "characters"
        code, output = self.run_main("--only", "qa")
        self.assertEqual(code, 2)
        self.assertIn("--character", output)
        code, _ = self.run_main("--character", "Tester", "--only", "qa")
        self.assertEqual(code, 0)
        self.assertEqual(self.server.passwords, [])  # already logged in: no password needed

    def test_list_and_dry_run_touch_nothing(self):
        self.write("qa", QUEST_A)
        _, output = self.run_main("--list")
        self.assertRegex(output, r"qa\s+implemented")
        self.assertRegex(output, r"qb\s+not implemented")
        _, output = self.run_main("--dry-run")
        self.assertIn("1. goto area='1_1_1'", output)
        self.assertIn("qb: not implemented", output)
        self.assertEqual(self.server.calls, [])

    def test_password_file_and_default_name(self):
        path = Path(self.directory.name) / "login.txt"
        path.write_text("Local server only.\nAccount: offline\nPassword:  pass word \nCharacter: X\n", encoding="utf-8")
        self.assertEqual(runner.read_password(path), "pass word")
        path.write_text("Account: offline\n", encoding="utf-8")
        self.assertIsNone(runner.read_password(path))
        self.assertIsNone(runner.read_password(Path(self.directory.name) / "missing.txt"))
        import datetime
        name = runner.default_name(datetime.datetime(2026, 10, 30, 9, 5, 7))
        self.assertEqual(name, "Campbadaajafah")
        self.assertRegex(name, r"^[A-Za-z]{3,23}$")


class OrderTests(unittest.TestCase):
    def test_order_is_unique_and_matches_the_quest_table(self):
        self.assertEqual(len(order_module.ORDER), len(set(order_module.ORDER)))
        table = bridge_module.ROOT / "data" / "tables" / "Quest.json"
        if not table.is_file():
            self.skipTest("data/tables/Quest.json not extracted")
        ids = {row["Id"] for row in json.loads(table.read_text(encoding="utf-8"))}
        self.assertEqual([q for q in order_module.ORDER if q not in ids], [])

    def test_the_shipped_step_files_load_and_need_only_shipped_quests(self):
        # A quest whose act is not on the quest engine has no step file: "not implemented", never "blocked" or "failed".
        shipped = order_module.implemented()
        self.assertIn("a1q1", shipped)
        for quest in shipped:
            module = runner.load_quest(quest, order_module.QUESTS)
            self.assertIsNone(module.error, quest)
            self.assertEqual([q for q in module.requires if q not in shipped], [], quest)
        stray = [p.stem for p in order_module.QUESTS.glob("*.py") if not p.stem.startswith("_") and p.stem not in shipped]
        self.assertEqual(stray, [])   # every step file is in ORDER

    def test_implemented_lists_step_files(self):
        with tempfile.TemporaryDirectory() as directory:
            Path(directory, "a1q4.py").write_text("", encoding="utf-8")
            Path(directory, "_example.py").write_text("", encoding="utf-8")
            self.assertEqual(order_module.implemented(directory), ["a1q4"])


class ReportTests(unittest.TestCase):
    def test_acts_group_the_quests_in_run_order(self):
        self.assertEqual(["Act 1", "Act 10", "Epilogue", "Epilogue"],
                         [runner.act_of(q) for q in ("a1q1", "a10q3", "A11q0", "a10q3a11")])
        results = [("a1q1", runner.DONE, 10.0, ""), ("a1q5", runner.ALREADY, 1.0, ""), ("a2q11", runner.FAILED, 5.0, "x")]
        self.assertEqual({"Act 1": {"quests": 2, "done": 2, "failed": 0, "seconds": 11.0},
                          "Act 2": {"quests": 1, "done": 0, "failed": 1, "seconds": 5.0}}, runner.act_table(results))

    def test_the_character_report_names_missing_preset_and_done_flags(self):
        class Stub:
            def state(self, brief=False):
                return {"area": "2_11_endgame_town", "areaName": "Karui Shores", "level": 68, "flags": ["a1-done", "p1"],
                        "passives": {"unspent": 91, "quest": 24, "refund": 20, "allocated": []},
                        "pantheon": {"major": "TheBrineKing", "minor": "Tukohama"}, "resistancePenalty": -60}
        modules = {"a1q9": mock.Mock(done="a1-done"), "a2q8": mock.Mock(done="a2-done")}
        results = [("a1q9", runner.DONE, 3.0, ""), ("a2q8", runner.FAILED, 4.0, "step 2")]
        presets = {"Act1Complete": ["p1", "p2"], "Act2CompleteEramir": ["p3"]}
        with contextlib.redirect_stdout(io.StringIO()):
            report = runner.character_report(Stub(), "Camp", results, modules, 7.0, presets=presets)
        self.assertEqual({"Act1Complete": ["p2"]}, report["presets"])   # Act 2 did not finish: not checked
        self.assertEqual([], report["missingDone"])
        self.assertEqual([{"quest": "a2q8", "note": "step 2"}], report["failed"])
        self.assertEqual((91, 24, 20, -60), (report["passives"]["unspent"], report["passives"]["quest"],
                                             report["passives"]["refund"], report["resistancePenalty"]))


if __name__ == "__main__":
    logging.basicConfig(level=logging.WARNING)
    unittest.main()
