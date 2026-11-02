"""Campaign step verbs (Contributions CONTRACT "Driver step format"); one function per verb.

A step is a dict with the verb under `verb` (aliases `do`, `action`) and the verb's fields, e.g.
`{"verb": "kill", "target": "Hillock"}`, or the compact form whose first key is the verb itself, mapped to its main
argument: `{"kill": "Hillock"}`, `{"talk": "Tarkleigh", "option": "Hillock Reward"}` (MAIN_ARGUMENT lists them).
Common optional fields on every step:

- `skip_if`: a quest flag, or a list of flags; the step is skipped when the character has (any of) it.
- `optional`: tried once; a failure is only a warning (runner.py).
- `area`: a WorldAreas Id; `goto` it first when the player is elsewhere (for `goto` it is the destination).
- `timeout`: seconds for the step's own wait (each verb has a default).
- `expect_flag`: a quest flag to wait for after the action (same timeout).

Every verb takes `(ctx, step)` and raises on failure (StepFailed, bridge.WaitTimeout, a bridge error).
"""
import json
import logging
import math
from pathlib import Path
import re

from bridge import ROOT, WaitTimeout

log = logging.getLogger("campaign")


class NoRoom(RuntimeError):
    """The main inventory has no free cell for an item (a reward); `reward` makes room at the area's Stash and retries."""


class StepFailed(RuntimeError):
    pass


class Context:
    """What the steps share: the bridge, the character's class (gem choice) and run options."""

    def __init__(self, bridge, class_name=None, seed=1, data_dir=ROOT / "data" / "tables"):
        self.bridge = bridge
        self.class_name = class_name
        self.seed = seed
        self.data_dir = Path(data_dir)
        self._gems = None
        self._classes = None
        self._connections = None
        self._gates = None
        # Passive points stay unspent unless asked for (--spend-passives): the `passives` step is a no-op by default.
        self.spend_passives = False

    def gates(self):
        """WorldAreas Id -> the QuestFlags Ids the client needs to enter it (EndQuestFlags); {} without the tables."""
        if self._gates is None:
            self._gates = {}
            try:
                flags = [r.get("Id") for r in json.loads((self.data_dir / "QuestFlags.json").read_text(encoding="utf-8"))]
                for r in json.loads((self.data_dir / "WorldAreas.json").read_text(encoding="utf-8")):
                    keys = r.get("EndQuestFlags") or []
                    self._gates[r.get("Id")] = [flags[k] for k in keys if isinstance(k, int) and 0 <= k < len(flags)]
            except (OSError, ValueError) as error:
                log.debug("area gates unavailable: %s", error)
        return self._gates

    def connections(self):
        """WorldAreas Id -> the Ids its exits lead to (WorldAreas.json Connections_WorldAreasKeys); {} without the table."""
        if self._connections is None:
            self._connections = {}
            try:
                rows = json.loads((self.data_dir / "WorldAreas.json").read_text(encoding="utf-8"))
                ids = [r.get("Id") for r in rows]
                for r in rows:
                    keys = r.get("Connections_WorldAreasKeys") or []
                    self._connections[r.get("Id")] = [ids[k] for k in keys if isinstance(k, int) and 0 <= k < len(ids)]
            except (OSError, ValueError) as error:
                log.debug("area connections unavailable: %s", error)
        return self._connections

    def gem_attributes(self):
        """BaseItemTypes Id -> (str%, dex%, int%, is_support) from SkillGems.json; {} without the tables."""
        if self._gems is None:
            self._gems = {}
            try:
                bases = json.loads((self.data_dir / "BaseItemTypes.json").read_text(encoding="utf-8"))
                gems = json.loads((self.data_dir / "SkillGems.json").read_text(encoding="utf-8"))
                for gem in gems:
                    key = gem.get("BaseItemTypesKey")
                    if isinstance(key, int) and 0 <= key < len(bases):
                        self._gems[bases[key]["Id"]] = (gem.get("StrengthRequirementPercent", 0),
                                                        gem.get("DexterityRequirementPercent", 0),
                                                        gem.get("IntelligenceRequirementPercent", 0),
                                                        bool(gem.get("IsSupport")))
            except (OSError, ValueError, KeyError) as error:
                log.debug("gem table unavailable: %s", error)
        return self._gems

    def class_attributes(self, state=None):
        """(str, dex, int) starting attributes of the character's class (Characters.json), or None."""
        if self._classes is None:
            try:
                self._classes = json.loads((self.data_dir / "Characters.json").read_text(encoding="utf-8"))
            except (OSError, ValueError):
                self._classes = []
        row = None
        if self.class_name:
            row = next((r for r in self._classes if r.get("Name", "").lower() == self.class_name.lower()), None)
        elif state is not None and isinstance(state.get("classRow"), int) and 0 <= state["classRow"] < len(self._classes):
            row = self._classes[state["classRow"]]
        if row is None:
            return None
        return row.get("BaseStrength", 0), row.get("BaseDexterity", 0), row.get("BaseIntelligence", 0)


# --- matching ----------------------------------------------------------------------------------------------------

def as_id(value):
    """An object id from an int or a '0x..'/decimal string; None for names."""
    if isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value
    if isinstance(value, str):
        text = value.strip()
        if re.fullmatch(r"0x[0-9a-fA-F]+", text):
            return int(text, 16)
        if re.fullmatch(r"\d+", text):
            return int(text)
    return None


def matches(entry, target, *fields):
    """True when `target` is the entry's id, or a case-insensitive substring of one of `fields`."""
    ident = as_id(target)
    if ident is not None:
        return entry.get("id") == ident
    needle = str(target).lower()
    return any(needle in str(entry.get(f) or "").lower() for f in fields)


def distance(a, b):
    if not a or not b:
        return math.inf
    return math.hypot(a[0] - b[0], a[1] - b[1])


def nearest(entries, pos):
    return sorted(entries, key=lambda e: distance(e.get("pos"), pos))


def find_monsters(state, target, alive_only=True):
    monsters = state.get("monsters") or []
    text = str(target).strip().lower()
    if text == "boss":
        found = [m for m in monsters if m.get("boss")]
    elif text == "all":
        found = list(monsters)
    elif text.startswith("near"):
        parts = text.split()
        radius = float(parts[1]) if len(parts) > 1 else 60.0
        found = [m for m in monsters if distance(m.get("pos"), state.get("pos")) <= radius]
    else:
        found = [m for m in monsters if matches(m, target, "name", "variety")]
    if text == "all" or text.startswith("near"):
        found = [m for m in found if m.get("hostile", True)]  # not allied/neutral ones
    return [m for m in found if m.get("alive", True)] if alive_only else found


def find_npc(state, name):
    return next(iter(nearest([n for n in state.get("npcs") or [] if matches(n, name, "name", "npc")],
                             state.get("pos"))), None)


def find_items(entries, item):
    """Ground or inventory items matching `item`: a base Id, or a case-insensitive substring of the base Id, name or
    item class; a list of such strings matches any of them."""
    wanted = item if isinstance(item, (list, tuple)) else [item]
    return [e for e in entries or [] if any(e.get("base") == w or matches(e, w, "base", "name", "class")
                                            for w in wanted)]


def has_flag(state, flag):
    return flag in (state.get("flags") or [])


# --- shared actions ----------------------------------------------------------------------------------------------

def tp_next_to(ctx, ident, pos=None):
    """`/tp <id>`; when the Gateway refuses the id (a reply without "Teleported") and the cell is known, `/tp x y`."""
    result = ctx.bridge.execute({"command": "tp", "id": int(ident)})
    replies = result.get("replies") if isinstance(result, dict) else None
    if pos and replies is not None and not any(str(r).startswith("Teleported") for r in replies):
        log.info("  tp 0x%x refused (%s); to its cell %s", int(ident), "; ".join(map(str, replies)) or "no reply", pos)
        ctx.bridge.execute({"command": "tp", "x": int(pos[0]), "y": int(pos[1])})


def ensure_area(ctx, area, timeout=60.0):
    state = ctx.bridge.state(brief=True)
    if state.get("area") != area:
        goto(ctx, {"verb": "goto", "area": area, "timeout": timeout})


def close_talk_text(ctx, timeout=5.0, limit=10):
    """While an NPC talk text is playing (`inTalk`), npc:close to finish it (the server sets its finish flag)."""
    state = ctx.bridge.state(brief=True)
    for _ in range(limit):
        if not state.get("inTalk"):
            return state
        ctx.bridge.execute({"command": "npc:close"})
        try:
            state = ctx.bridge.wait_until(lambda s: not s.get("inTalk"), timeout=timeout, what="talk text to end")
        except WaitTimeout as error:
            state = error.state or state
    if state.get("inTalk"):
        raise StepFailed("an NPC talk text did not end after npc:close")
    return state


def close_dialogue(ctx, timeout=3.0):
    """End an open NPC dialogue (no-op when none is open). Never raises for a dialogue that stays open."""
    state = close_talk_text(ctx)
    if state.get("talking"):
        ctx.bridge.execute({"command": "npc:close"})
        try:
            state = ctx.bridge.wait_until(lambda s: not s.get("talking"), timeout=timeout, what="dialogue to close")
        except WaitTimeout:
            log.warning("dialogue with %s still open after npc:close", state.get("talking"))
    return state


# NPC display name -> {act: town}. Used by talk/reward when the NPC is not in the current area and the step has no
# `area`. Acts 1-6 checked against the NPCs table and the town rooms (Act 5's Lilly Roth stands on the Cathedral Rooftop,
# not in town); acts 7-11 from the NPCs.json town Ids (…Town, Act<N>/<Name>) and not yet verified live.
# Acts 7-11 checked against the towns' rooms (batch C): the Epilogue's town is Karui Shores (2_11_endgame_town: Lani,
# Lilly, Weylam, Helena; no Sin or Innocence); Act 9's Lilly and Weylam stand in The Rotting Core, not in Highgate.
NPC_TOWNS = {
    "Tarkleigh": {1: "1_1_town", 6: "2_6_town"},
    "Nessa": {1: "1_1_town"},
    "Bestel": {1: "1_1_town", 6: "2_6_town"},
    "Greust": {2: "1_2_town"},
    "Yeena": {2: "1_2_town", 7: "2_7_town"},
    "Helena": {2: "1_2_town", 7: "2_7_town", 11: "2_11_endgame_town"},
    "Silk": {2: "1_2_town"},
    "Eramir": {2: "1_2_town", 7: "2_7_town"},
    "Clarissa": {3: "1_3_town", 8: "2_8_town"},
    "Hargan": {3: "1_3_town", 8: "2_8_town"},
    "Maramoa": {3: "1_3_town", 8: "2_8_town"},
    "Grigor": {3: "1_3_town"},
    "Lady Dialla": {4: "1_4_town"},
    "Dialla": {4: "1_4_town"},
    "Kira": {4: "1_4_town"},
    "Oyun": {4: "1_4_town"},
    "Petarus and Vanja": {4: "1_4_town", 9: "2_9_town"},
    "Tasuni": {4: "1_4_town", 9: "2_9_town"},
    "Lani": {5: "1_5_town", 10: "2_10_town", 11: "2_11_endgame_town"},
    "Vilenta": {5: "1_5_town"},
    "Utula": {5: "1_5_town"},
    "Bannon": {5: "1_5_town", 10: "2_10_town"},
    "Lilly Roth": {5: "1_5_8", 6: "2_6_town", 9: "2_9_10_2", 10: "2_10_town", 11: "2_11_endgame_town"},
    "Sin": {6: "2_6_town", 7: "2_7_town", 8: "2_8_town", 9: "2_9_town", 10: "2_10_town"},
    "Weylam Roth": {7: "2_7_town", 9: "2_9_10_2", 10: "2_10_town", 11: "2_11_endgame_town"},
    "Irasha": {9: "2_9_town"},
    "Innocence": {10: "2_10_town"},
}


def area_act(area):
    """The act of a WorldAreas Id such as 1_1_2 (act 1) or 2_6_town (act 6); None if unknown."""
    match = re.match(r"^(\d)_(\d+)_", area or "")
    return int(match.group(2)) if match else None


def npc_town(name, current_area=None):
    """The town for an NPC name: the current act's town when it has one, else the next act's, else the last."""
    towns = next((v for k, v in NPC_TOWNS.items() if k.lower() == str(name).lower()), None)
    if towns is None:
        towns = next((v for k, v in NPC_TOWNS.items() if str(name).lower() in k.lower()), None)
    if not towns:
        return None
    act = area_act(current_area)
    if act in towns:
        return towns[act]
    later = sorted(a for a in towns if act is not None and a > act)
    return towns[later[0]] if later else towns[max(towns)]


# --- verbs -------------------------------------------------------------------------------------------------------

# Routes go through every area: the areas the client cannot generate at seed 1 (1_2_3, 2_6_1, 2_6_5, 2_6_12, 2_7_2)
# are played at a seed it built (ServerLevelAreas.SeedsInPlaceOfOne, Auth's AreaSeeds and ServerLevels.PlayableSeed),
# so a seed-1 run (campaign.ps1: Game_RANDOM_SEEDS=0) needs no detour and no /goto.

# Areas a quest flag replaces by their other version: no exit leads into them any more (the exits into them exist only
# until the flag, Quests.ExistsRule), so routes go around them. Act 5: Avarius' death burns Oriath (Oriath Square and The
# Templar Courts become The Ruined Square and The Torched Courts).
REPLACED_AFTER = {"1_5_3": "a5q4-KilledBoss", "1_5_4": "a5q4-KilledBoss"}


def route(connections, start, target, waypoints, enterable=lambda area: True, closed=()):
    """The shortest way to `target` as the game travels: from `start`, or by waypoint to an area whose waypoint is
    activated, then exit by exit along the areas' connections, through areas the character may enter (`enterable`:
    their WorldAreas gate) and not through an exit found `closed` ((from, to) pairs: a quest's exit not open yet,
    Prisoner's Gate's spikes). A list of (method, area) hops ("waypoint" first at most, then "transition"), [] when
    already there, None without a way."""
    if start == target:
        return []
    from collections import deque
    seen = {start: []}
    queue = deque([start])
    for w in waypoints or []:
        if w not in seen:
            seen[w] = [("waypoint", w)]
            queue.append(w)
    while queue:
        here = queue.popleft()
        for there in connections.get(here, []):
            if there in seen or not enterable(there) or (here, there) in closed:
                continue
            seen[there] = seen[here] + [("transition", there)]
            if there == target:
                return seen[there]
            queue.append(there)
    return seen.get(target)


def goto(ctx, step):
    """Be in `area` as a player travels: waypoint travel when the destination's waypoint is activated, else an area
    transition to it from here, else a route (by waypoint to the nearest activated one, then exit by exit along
    WorldAreas connections). Never `/goto`: an area no waypoint, exit or route reaches fails the step loudly (a missing
    exit or portal is a server bug to fix, not something to jump over). `via` forces one method. Each method waits for
    the area; on a timeout the next one is tried."""
    area = step["area"]
    timeout = float(step.get("timeout", 60))
    via = step.get("via")
    state = ctx.bridge.state()
    if state.get("area") == area:
        return
    current = state.get("area")
    methods = []
    waypoint = state.get("waypoint")
    # The Gateway takes a 0x4d travel from anywhere once the destination's waypoint is activated; the runner still
    # steps up to this area's waypoint first when it has one, as a player would.
    if via in (None, "waypoint") and area in (state.get("waypoints") or []):
        methods.append("waypoint")
    transition = next(iter(nearest([t for t in state.get("transitions") or [] if t.get("to") == area],
                                   state.get("pos"))), None)
    if via in (None, "transition") and transition:
        methods.append("transition")
    flags = set(state.get("flags") or [])

    def enterable(a):
        return REPLACED_AFTER.get(a) not in flags and all(f in flags for f in ctx.gates().get(a, []))
    closed = set()
    hops = None if methods or via not in (None, "route") else route(
        ctx.connections(), current, area, state.get("waypoints"), enterable, closed)
    if hops:
        methods.append("route")
    if via == "command":
        raise StepFailed(f"goto {area}: via command (/goto) is not allowed; the campaign travels by waypoint, exit or portal")
    if not methods:
        raise StepFailed(f"goto {area}: NO ROUTE from {current} by {via or 'waypoint, exit or route'} "
                         f"(waypoints {sorted(state.get('waypoints') or [])}); a missing exit or portal is a server bug")
    errors = []
    for method in methods:
        log.info("  goto %s via %s", area, method)
        before = ctx.bridge.status()  # area:wait's `from`: the status before the travel
        if method == "waypoint":
            if waypoint:
                tp_next_to(ctx, waypoint["id"], waypoint.get("pos"))
            ctx.bridge.execute({"command": "waypoint:travel", "area": area})
        elif method == "transition":
            tp_next_to(ctx, transition["id"], transition.get("pos"))
            ctx.bridge.execute({"command": "object:interact", "id": transition["id"]})
        elif method == "route":
            # An exit the data lists but the area does not show (a quest keeps it closed) is left out and the route planned
            # again from where the player stands, as a player would turn back.
            for _ in range(6):
                log.info("  route to %s: %s", area, " -> ".join(f"{m} {a}" for m, a in hops))
                try:
                    for hop_method, hop_area in hops:
                        here = ctx.bridge.state(brief=True).get("area")
                        try:
                            goto(ctx, {"verb": "goto", "area": hop_area, "via": hop_method, "timeout": timeout})
                        except StepFailed:
                            if hop_method != "transition":
                                raise
                            closed.add((here, hop_area))
                            raise
                    return
                except (StepFailed, WaitTimeout) as error:
                    errors.append(f"route: {error}")
                    now = ctx.bridge.state()
                    hops = route(ctx.connections(), now.get("area"), area, now.get("waypoints"),
                                 enterable, closed)
                    if not hops:
                        break
                    log.info("  route to %s: the exit %s is closed; planning again", area, " / ".join(f"{a} -> {b}" for a, b in closed))
            continue
        try:
            ctx.bridge.wait_area(area, timeout=timeout, previous=before)
            activate_waypoint(ctx)
            return
        except WaitTimeout as error:
            errors.append(f"{method}: {error}")
    raise StepFailed(f"goto {area} failed ({'; '.join(errors)})")


def activate_waypoint(ctx, timeout=3.0):
    """Step up to the area's waypoint when it is not activated yet: the Gateway activates a waypoint the player comes
    near, as the game does, and later gotos travel by it instead of walking exit by exit. Never fails the step."""
    try:
        state = ctx.bridge.state()
        waypoint = state.get("waypoint")
        if not waypoint or waypoint.get("active") or state.get("area") in (state.get("waypoints") or []):
            return
        log.info("  activating %s's waypoint", state.get("area"))
        tp_next_to(ctx, waypoint["id"], waypoint.get("pos"))
        area = state.get("area")
        ctx.bridge.wait_until(lambda s: area in (s.get("waypoints") or []), timeout=timeout, what=f"{area}'s waypoint")
    except (WaitTimeout, StepFailed, KeyError) as error:
        log.info("  waypoint not activated: %s", error)


def tp(ctx, step):
    """Teleport next to `target` (as /tp: object id, name, `x y`), or to cell `x`,`y`."""
    if "x" in step and "y" in step:
        request = {"command": "tp", "x": int(step["x"]), "y": int(step["y"])}
    else:
        target = step["target"]
        ident = as_id(target)
        request = {"command": "tp", "id": ident} if ident is not None else {"command": "tp", "target": str(target)}
        if "index" in step:
            request["index"] = int(step["index"])
    result = ctx.bridge.execute(request)
    for line in result.get("replies") or []:
        log.info("  tp: %s", line)


def kill(ctx, step):
    """tp next to the (first living) target, `/kill <target>`, wait until no matched monster is alive. Targets as
    /kill: id, name/variety substring, `boss`, `near [radius]`, `all`. Done at once when the targets are dead."""
    target = step["target"]
    timeout = float(step.get("timeout", 20))
    try:
        state = ctx.bridge.wait_until(lambda s: find_monsters(s, target, alive_only=False), timeout=float(
            step.get("appear_timeout", 5)), brief=False, what=f"a monster matching {target!r}")
    except WaitTimeout as error:
        raise StepFailed(f"kill: no monster matches {target!r} in {(error.state or {}).get('area')}") from None
    alive = find_monsters(state, target)
    if not alive:
        log.info("  kill %s: already dead", target)
        return
    ids = {m["id"] for m in alive}
    text = str(target).strip()
    if not (text.lower() == "all" or text.lower().startswith("near")):
        first = nearest(alive, state.get("pos"))[0]
        tp_next_to(ctx, first["id"], first.get("pos"))
    ident = as_id(target)
    replies = ctx.bridge.cmd(f"/kill 0x{ident:x}" if ident is not None else f"/kill {text}", wait=1.0)
    for line in replies:
        log.info("  kill: %s", line)

    def dead(s):
        living = {m["id"] for m in s.get("monsters") or [] if m.get("alive", True)}
        return not (ids & living)
    ctx.bridge.wait_until(dead, timeout=timeout, brief=False, what=f"{target} to die")


def pickup(ctx, step):
    """tp to the nearest matching ground `item` (see find_items: base Id, base/name/class substring, or a list), pick
    it up (`item:pickup`, else `object:interact`), wait until it left the ground and is in the main inventory. Done at
    once when no match is on the ground but one is already in the inventory."""
    item = step["item"]
    timeout = float(step.get("timeout", 20))
    try:
        state = ctx.bridge.wait_until(lambda s: find_items(s.get("items"), item)
                                      or find_items((s.get("inventory") or {}).get("items"), item),
                                      timeout=float(step.get("appear_timeout", 5)), brief=False,
                                      what=f"ground item {item!r}")
    except WaitTimeout as error:
        raise StepFailed(f"pickup: no ground item {item!r} in {(error.state or {}).get('area')}") from None
    ground = nearest(find_items(state.get("items"), item), state.get("pos"))
    if not ground:
        log.info("  pickup %s: already in the inventory", item)
        return
    target = ground[0]
    before = {i.get("id") for i in (state.get("inventory") or {}).get("items") or []}
    x, y = target["pos"]
    ctx.bridge.execute({"command": "tp", "x": int(x), "y": int(y)})
    try:
        ctx.bridge.execute({"command": "item:pickup", "id": target["id"]})
    except Exception as error:
        log.info("  item:pickup failed (%s); using object:interact", error)
        ctx.bridge.execute({"command": "object:interact", "id": target["id"]})

    def picked(s):
        if any(i.get("id") == target["id"] for i in s.get("items") or []):
            return False
        inventory = find_items((s.get("inventory") or {}).get("items"), item)
        return any(i.get("id") == target["id"] or i.get("id") not in before for i in inventory)
    ctx.bridge.wait_until(picked, timeout=timeout, brief=False, what=f"{item} in the inventory")


def interact(ctx, step):
    """Use an object: `target` matches objects (type/name/id), else transitions (name/destination), else NPCs.
    tp next to it, `object:interact`; then wait until the object's `used` flips (objects not yet used), the area
    changes (transitions) or its dialogue opens (NPCs). `expect_flag` is waited for afterwards by run_step."""
    target = step["target"]
    timeout = float(step.get("timeout", 20))
    state = ctx.bridge.state()
    objects = [o for o in state.get("objects") or [] if matches(o, target, "type", "name")]
    if objects:
        unused = [o for o in objects if not o.get("used")]
        chosen = nearest(unused or objects, state.get("pos"))[0]
        tp_next_to(ctx, chosen["id"], chosen.get("pos"))
        ctx.bridge.execute({"command": "object:interact", "id": chosen["id"]})
        if not chosen.get("used") and not step.get("expect_flag") and not step.get("no_wait"):
            # An opened chest can come back as a new object (a1q4's Rhoa Nests do): the same type at the same cell.
            def used(s):
                return any(o.get("used") and (o.get("id") == chosen["id"] or (o.get("type") == chosen.get("type")
                                                                             and o.get("pos") == chosen.get("pos")))
                           for o in s.get("objects") or [])
            ctx.bridge.wait_until(used, timeout=timeout, brief=False, what=f"{target} to be used")
        return
    transitions = [t for t in state.get("transitions") or [] if matches(t, target, "name", "to")]
    if transitions:
        chosen = nearest(transitions, state.get("pos"))[0]
        tp_next_to(ctx, chosen["id"], chosen.get("pos"))
        before = ctx.bridge.status()
        ctx.bridge.execute({"command": "object:interact", "id": chosen["id"]})
        ctx.bridge.wait_area(chosen["to"], timeout=timeout, previous=before)
        return
    npc = find_npc(state, target)
    if npc:
        tp_next_to(ctx, npc["id"], npc.get("pos"))
        ctx.bridge.execute({"command": "object:interact", "id": npc["id"]})
        ctx.bridge.wait_until(lambda s: s.get("talking") == npc["id"], timeout=timeout, what=f"{target} dialogue")
        return
    raise StepFailed(f"interact: nothing matches {target!r} in {state.get('area')}")


def open_dialogue(ctx, step):
    """Find the NPC (going to the step's `area` or its town when absent), talk to it, finish any talk text that
    starts on opening. Returns (state, npc) with the dialogue open."""
    name = step["npc"]
    timeout = float(step.get("timeout", 20))
    state = ctx.bridge.state()
    npc = find_npc(state, name)
    if npc is None:
        town = step.get("area") or npc_town(name, state.get("area"))
        if not town or town == state.get("area"):
            raise StepFailed(f"NPC {name!r} is not in {state.get('area')} and its town is unknown (give `area`)")
        goto(ctx, {"verb": "goto", "area": town})
        state = ctx.bridge.state()
        npc = find_npc(state, name)
        if npc is None:
            raise StepFailed(f"NPC {name!r} is not in {town}")
    for attempt in range(2):
        if state.get("talking") != npc["id"]:
            if state.get("talking"):
                close_dialogue(ctx)
            tp_next_to(ctx, npc["id"], npc.get("pos"))
            ctx.bridge.execute({"command": "object:interact", "id": npc["id"]})
            ctx.bridge.wait_until(lambda s: s.get("talking") == npc["id"] or s.get("inTalk"), timeout=timeout,
                                  what=f"dialogue with {npc.get('name')}")
        state = close_talk_text(ctx)
        if state.get("talking") == npc["id"]:
            break
        state = ctx.bridge.state()  # a greeting text ended the dialogue: open it again
    else:
        raise StepFailed(f"dialogue with {npc.get('name')} did not stay open")
    return ctx.bridge.state(), npc


def option_wanted(option=None, index=None, row=None):
    """A predicate for an NPC option of /state: text containing `option` (case-insensitive), the client `index`, the
    NPCTalk `row`; every given one must match. Rows with the same text (Piety's organs, a3q12's pages) need index/row."""
    needle = None if option is None else str(option).lower()

    def wanted(o):
        return ((needle is None or needle in str(o.get("text", "")).lower())
                and (index is None or o.get("index") == int(index)) and (row is None or o.get("row") == int(row)))
    return wanted


def describe_option(option=None, index=None, row=None):
    return " ".join(f"{k}={v!r}" for k, v in (("text", option), ("index", index), ("row", row)) if v is not None)


def choose_option(ctx, npc, option=None, timeout=5.0, index=None, row=None):
    """Pick the NPC option whose text contains `option` (case-insensitive) and/or whose client `index` / NPCTalk `row`
    is the given one; returns the option."""
    wanted = option_wanted(option, index, row)
    what = describe_option(option, index, row)

    def find(s):
        # Of several rows with the text, the one not heard yet (NPCTalk QuestFlag3) is the quest's next: Yeena's second
        # "The Fellshrine Hand" hands the Golden Hand in, the first only gave the quest.
        current = next((n for n in s.get("npcs") or [] if n.get("id") == npc["id"]), None) or {}
        found = [o for o in current.get("options") or [] if wanted(o)]
        return next((o for o in found if not o.get("heard")), found[0] if found else None)
    try:
        state = ctx.bridge.wait_until(find, timeout=timeout, brief=False, what=f"option {what}")
    except WaitTimeout as error:
        current = next((n for n in (error.state or {}).get("npcs") or [] if n.get("id") == npc["id"]), {})
        texts = [(o.get("index"), o.get("text")) for o in current.get("options") or []]
        raise StepFailed(f"{npc.get('name')} has no option {what} (options: {texts})") from None
    chosen = find(state)
    log.info("  %s: option %d %r", npc.get("name"), chosen["index"], chosen.get("text"))
    ctx.bridge.execute({"command": "npc:option", "index": int(chosen["index"])})
    return chosen


def talk(ctx, step):
    """Talk to `npc` (name or NPCs Id substring) and pick `option` (text substring; optional). Finishes the talk
    text with npc:close (sets its finish flag) and closes the dialogue."""
    _, npc = open_dialogue(ctx, step)
    if step.get("option") or "index" in step or "row" in step:
        choose_option(ctx, npc, step.get("option"), index=step.get("index"), row=step.get("row"))
        try:  # the option starts a talk text, opens a reward panel or a sub-menu, or ends the dialogue
            ctx.bridge.wait_until(lambda s: s.get("inTalk") or s.get("reward") or s.get("talking") != npc["id"],
                                  timeout=float(step.get("option_timeout", 3)), what="the option's effect")
        except WaitTimeout:
            pass
    close_dialogue(ctx)


def free_cell(inventory, w, h):
    """The first main-inventory cell (column by column, top to bottom) where a w x h item fits; None when full."""
    cols, rows = int(inventory.get("w", 12)), int(inventory.get("h", 5))
    used = set()
    for item in inventory.get("items") or []:
        for dx in range(int(item.get("w", 1))):
            for dy in range(int(item.get("h", 1))):
                used.add((item["x"] + dx, item["y"] + dy))
    for x in range(cols - w + 1):
        for y in range(rows - h + 1):
            if all((x + dx, y + dy) not in used for dx in range(w) for dy in range(h)):
                return x, y
    return None


def free_cells(inventory):
    cols, rows = int(inventory.get("w", 12)), int(inventory.get("h", 5))
    taken = sum(int(i.get("w", 1)) * int(i.get("h", 1)) for i in inventory.get("items") or [])
    return cols * rows - taken


def item_score(ctx, item, state=None):
    """Usefulness of a reward item: gems the class can use (active, then support), flasks, other gems, anything."""
    cls = str(item.get("class") or "").lower()
    base = str(item.get("base") or "")
    if "gem" in cls or "/Gems/" in base:
        support = "support" in cls
        usable = True
        attrs = ctx.gem_attributes().get(base)
        stats = ctx.class_attributes(state)
        if attrs and stats:
            # a gem is usable when its main attribute is one of the class's main (above-minimum) attributes
            main = [i for i, v in enumerate(stats) if v > min(stats)] or [0, 1, 2]
            best = max(range(3), key=lambda i: attrs[i])
            usable = attrs[best] == 0 or best in main
            support = attrs[3]
        if usable:
            return 250 if support else 300
        return 100
    if "flask" in cls or "/Flasks/" in base:
        return 150
    return 50


def choose_reward(ctx, offer, picks, state=None):
    items = offer.get("items") or []
    if not items:
        raise StepFailed("the reward offer has no items")
    for pick in picks or []:
        found = [i for i in items if matches(i, pick, "name", "base")]
        if found:
            return found[0]
    return max(items, key=lambda i: item_score(ctx, i, state))  # first of the best on ties (stable max)


def claim_offer(ctx, step, npc=None):
    """Claim one item of the open reward panel into a free main-inventory cell; waits for it in the inventory."""
    timeout = float(step.get("timeout", 30))
    state = ctx.bridge.state()
    offer = state.get("reward")
    chosen = choose_reward(ctx, offer, step.get("pick"), state)
    inventory = state.get("inventory") or {}
    cell = free_cell(inventory, int(chosen.get("w", 1)), int(chosen.get("h", 1)))
    if cell is None:
        raise NoRoom(f"no free {chosen.get('w', 1)}x{chosen.get('h', 1)} inventory cell for {chosen.get('name')}")
    before = {i.get("id") for i in inventory.get("items") or []}
    log.info("  reward: %s (%s) -> cell %d,%d", chosen.get("name"), chosen.get("base"), *cell)
    ctx.bridge.execute({"command": "reward:claim", "id": chosen["id"], "x": cell[0], "y": cell[1]})

    def claimed(s):
        if s.get("reward"):
            return False
        items = (s.get("inventory") or {}).get("items") or []
        return any(i.get("id") == chosen["id"] or (i.get("id") not in before and i.get("base") == chosen.get("base"))
                   for i in items)
    ctx.bridge.wait_until(claimed, timeout=timeout, brief=False, what=f"{chosen.get('name')} in the inventory")
    return chosen


def reward(ctx, step):
    """Talk to `npc`, pick `option` (default: its first QuestReward option), claim the offered item chosen by
    `pick` (name/base substrings in preference order) or the usefulness heuristic, close the dialogue. An option
    that gives its reward directly (no item panel, e.g. a skill book) is accepted once the option disappears."""
    _, npc = open_dialogue(ctx, step)
    option, index, row = step.get("option"), step.get("index"), step.get("row")
    if not option and index is None and row is None:
        state = ctx.bridge.state()
        current = next((n for n in state.get("npcs") or [] if n.get("id") == npc["id"]), {})
        rewards = [o for o in current.get("options") or [] if o.get("category") == "QuestReward"]
        if not rewards:
            raise StepFailed(f"{npc.get('name')} has no quest reward option")
        option = rewards[0]["text"]
    chosen = choose_option(ctx, npc, option, index=index, row=row)
    try:
        ctx.bridge.wait_until(lambda s: s.get("reward"), timeout=float(step.get("offer_timeout", 8)),
                              what="the reward panel")
    except WaitTimeout:
        state = close_talk_text(ctx)
        state = ctx.bridge.state()
        current = next((n for n in state.get("npcs") or [] if n.get("id") == npc["id"]), {})
        if any(o.get("index") == chosen["index"] and o.get("text") == chosen.get("text")
               for o in current.get("options") or []):
            raise StepFailed(f"{npc.get('name')}: no reward panel after {chosen.get('text')!r}") from None
        log.info("  reward %r given without an item choice", chosen.get("text"))
        close_dialogue(ctx)
        return
    try:
        claim_offer(ctx, step, npc)
    except NoRoom as error:
        # A whole campaign's rewards fill the 12x5 inventory (Act 10's books found 3 free cells): put the carried
        # non-quest items into the Stash, as a player would, and ask again. The offer stays until claimed.
        if step.get("_stashed") or not stash_reachable(ctx.bridge.state(brief=True)):
            raise StepFailed(f"{error} and no Stash to make room at") from None
        log.info("  %s: making room at the Stash", error)
        close_dialogue(ctx)
        stash_items(ctx)
        reward(ctx, {**step, "_stashed": True})
        return
    close_dialogue(ctx)


# The tabs every account has from the start (18-stash.md §6: two normal, two premium, a quad tab), in the order tried.
STASH_TABS = (0, 1, 2, 3, 4)
# Campaign towns with a Stash (StashCatalog: the other towns' are not placed yet), reached by waypoint when the
# current area has none, as a player would.
STASH_TOWNS = ("1_1_town",)


def stash_reachable(state):
    """Whether the area has a Stash, or a town with one is an activated waypoint."""
    return bool(state.get("stash")) or any(t in (state.get("waypoints") or []) for t in STASH_TOWNS)


def stash_items(ctx, timeout=10.0):
    """Put every non-quest item of the main inventory into a Stash (towns, hideouts): the area's, else that of a town
    in STASH_TOWNS reached by waypoint (and back by waypoint or route afterwards). tp next to it, open it
    (object:interact), then Ctrl + click each item into a tab (`stash:deposit`, C->S 0xe0: the server takes the tab's
    first free cell); what a tab refuses (full) goes to the next. Quest items stay. Returns how many items moved."""
    state = ctx.bridge.state()
    carried = [i for i in (state.get("inventory") or {}).get("items") or [] if not i.get("quest")]
    if not carried:
        return 0
    back = None
    if not state.get("stash"):
        town = next((t for t in STASH_TOWNS if t in (state.get("waypoints") or [])), None)
        if town is None:
            raise StepFailed(f"stash: no Stash in {state.get('area')} and no waypoint to a town with one")
        back = state.get("area")
        log.info("  stash: none in %s; to %s by waypoint", back, town)
        goto(ctx, {"verb": "goto", "area": town, "via": "waypoint"})
        state = ctx.bridge.state()
    stash = state.get("stash")
    if not stash:
        raise StepFailed(f"stash: no Stash in {state.get('area')}")
    tp_next_to(ctx, stash["id"], stash.get("pos"))
    ctx.bridge.execute({"command": "object:interact", "id": int(stash["id"])})
    left = {int(i["id"]) for i in carried}

    def held(s):
        return {int(i.get("id")) for i in (s.get("inventory") or {}).get("items") or []}
    for tab in STASH_TABS:
        for ident in sorted(left):
            ctx.bridge.execute({"command": "stash:deposit", "id": ident, "tab": tab})
        try:
            state = ctx.bridge.wait_until(lambda s: not (left & held(s)), timeout=timeout, brief=False,
                                          what="the items in the stash")
        except WaitTimeout as error:
            state = error.state or ctx.bridge.state()
        left &= held(state)
        if not left:
            break
    log.info("  stash: %d of %d item(s) put into the stash%s", len(carried) - len(left), len(carried),
             f"; {len(left)} refused" if left else "")
    if back:
        goto(ctx, {"verb": "goto", "area": back})
    return len(carried) - len(left)


def stash(ctx, step):
    """Step verb: put the carried non-quest items into the Stash of the step's `area` (else the current one)."""
    stash_items(ctx, timeout=float(step.get("timeout", 10)))


def use_item(ctx, step):
    """Use (right-click) a main-inventory `item` (as pickup: base Id, base/name/class substring or a list): C->S 0x58
    through `item:use`, then wait until that item left the inventory (a book is used up). Quest books set their
    use_flag (QuestStaticRewards' points follow); `expect_flag` waits for it. Done at once when no match is carried
    and the step's `expect_flag` is already set."""
    item = step["item"]
    timeout = float(step.get("timeout", 10))
    state = ctx.bridge.state()
    found = find_items((state.get("inventory") or {}).get("items"), item)
    if not found:
        if step.get("expect_flag") and has_flag(state, step["expect_flag"]):
            log.info("  use_item %s: not carried, %s already set", item, step["expect_flag"])
            return
        raise StepFailed(f"use_item: no inventory item matches {item!r}")
    chosen = found[0]
    log.info("  use %s (%s) 0x%x", chosen.get("name"), chosen.get("base"), int(chosen["id"]))
    ctx.bridge.execute({"command": "item:use", "inventoryId": MAIN_INVENTORY, "id": int(chosen["id"])})

    def used(s):
        return not any(i.get("id") == chosen["id"] for i in (s.get("inventory") or {}).get("items") or [])
    ctx.bridge.wait_until(used, timeout=timeout, brief=False, what=f"{chosen.get('name')} to be used")


def wait_flag(ctx, step):
    flag = step["flag"]
    ctx.bridge.wait_until(lambda s: has_flag(s, flag), timeout=float(step.get("timeout", 60)), poll=0.5,
                          what=f"flag {flag}")


def command(ctx, step):
    for line in ctx.bridge.cmd(step["text"], wait=float(step.get("wait", 1.5))):
        log.info("  %s: %s", step["text"].split()[0], line)


# The gods the runner picks by default, in preference order (PantheonPanelLayout Ids): the major gods Arakaali (less
# damage over time), Lunaris, Solaris, then The Brine King (the first one the campaign unlocks); the minor ones as the
# campaign unlocks them, Tukohama first.
PANTHEON_MAJOR = ["Arakaali", "Lunaris", "Solaris", "TheBrineKing"]
PANTHEON_MINOR = ["Tukohama", "Ryslatha", "Abberath", "Gruthkul", "Shakari", "Garukhan", "Yugul", "Ralakesh"]


def pantheon_pick(unlocked, wanted, major):
    """The unlocked god (a /state pantheon.unlocked entry) of the wanted kind that comes first in `wanted` (Ids or
    name substrings), or None."""
    gods = [g for g in unlocked or [] if bool(g.get("major")) == major]
    for want in wanted:
        for g in gods:
            if want.lower() in (g.get("id") or "").lower() or want.lower() in (g.get("name") or "").lower():
                return g
    return None


def pantheon(ctx, step):
    """Choose the Pantheon gods: `major` and `minor` (an Id or name substring, or a list in preference order; default
    the runner's own order) among those /state `pantheon.unlocked` lists, each by `pantheon:select <row>` (C->S
    0x1ee), waiting until /state shows it. Only where the server allows it (`pantheon.canChoose`: a town, a hideout, or
    after Sin's god talk); a slot with no unlocked god is left as it is."""
    timeout = float(step.get("timeout", 10))
    state = ctx.bridge.state(brief=True)
    info = state.get("pantheon")
    if info is None:
        raise StepFailed("pantheon: /state has no pantheon field (server too old)")
    for slot, default, major in (("major", PANTHEON_MAJOR, True), ("minor", PANTHEON_MINOR, False)):
        wanted = step.get(slot) or default
        wanted = [wanted] if isinstance(wanted, str) else list(wanted) + default
        god = pantheon_pick(info.get("unlocked"), wanted, major)
        if god is None:
            log.info("  pantheon %s: none unlocked yet", slot)
            continue
        if info.get(slot) == god["id"]:
            log.info("  pantheon %s: %s already", slot, god.get("name"))
            continue
        if not info.get("canChoose"):
            raise StepFailed(f"pantheon: cannot choose {god.get('name')} here (not a town or hideout, no CanChangePantheon)")
        ctx.bridge.execute({"command": "pantheon:select", "row": int(god["row"])})
        try:
            state = ctx.bridge.wait_until(lambda s: ((s.get("pantheon") or {}).get(slot)) == god["id"], timeout=timeout,
                                          what=f"{god.get('name')} as the {slot} god")
        except WaitTimeout as error:
            # The client sent C->S 0x1ee (pantheon:select); a Gateway that does not frame it logs "unframed 0x01ee" and
            # never applies the choice (fixed by 2efd7504).
            raise StepFailed(f"pantheon: the server did not apply {god.get('name')} as the {slot} god ({error}); check the "
                             "Gateway log for 0x01ee (\"unframed 0x01ee\" = a Gateway without the 0x1ee framing)") from error
        info = state.get("pantheon") or info
        log.info("  pantheon %s: %s", slot, god.get("name"))


def passives(ctx, step=None):
    """Spend unspent passive points: allocate the first allocatable candidate, wait for the count to drop.
    Quietly does nothing without points, candidates or the `passives` field."""
    if not getattr(ctx, "spend_passives", False):
        return
    timeout = float((step or {}).get("timeout", 5))
    state = ctx.bridge.state(brief=True)
    tried = set()
    while True:
        info = state.get("passives") or {}
        unspent = int(info.get("unspent") or 0)
        candidates = [c for c in info.get("candidates") or [] if c not in tried]
        if unspent <= 0 or not candidates:
            return
        node = candidates[0]
        tried.add(node)
        # Ordinary nodes: expansion 0 (the bridge defaults to 1, which the Gateway reads as a cluster jewel node).
        ctx.bridge.execute({"command": "passive:allocate", "node": int(node), "expansion": False})
        try:
            state = ctx.bridge.wait_until(lambda s: int((s.get("passives") or {}).get("unspent") or 0) < unspent,
                                          timeout=timeout, what=f"passive {node} to be allocated")
            log.info("  passive %s allocated (%d left)", node, unspent - 1)
        except WaitTimeout as error:
            log.warning("  passive %s was not allocated", node)
            state = error.state or ctx.bridge.state(brief=True)


def equip(ctx, step):
    """Equip an inventory `item` (as pickup: base/name/class substring or a list) in `slot` "weapon" (default) or
    "offhand": inventory:pickup from the main inventory (1) onto the cursor, inventory:place into the slot (3 =
    Weapon1, 4 = Offhand1) at 0,0; waits for /state `equipped.<slot>`. Done at once when a match is equipped."""
    item = step["item"]
    slot = step.get("slot", "weapon")
    if slot not in EQUIP_SLOTS:
        raise StepFailed(f"equip: unknown slot {slot!r} (weapon, offhand)")
    state = ctx.bridge.state()
    equipped = (state.get("equipped") or {}).get(slot)
    if equipped and find_items([equipped], item):
        log.info("  equip %s: already equipped (%s)", item, equipped.get("name"))
        return
    found = find_items((state.get("inventory") or {}).get("items"), item)
    if not found:
        raise StepFailed(f"equip: no inventory item matches {item!r}")
    chosen = found[0]
    ctx.bridge.execute({"command": "inventory:pickup", "inventoryId": MAIN_INVENTORY, "id": chosen["id"]})
    ctx.bridge.execute({"command": "inventory:place", "inventoryId": EQUIP_SLOTS[slot], "x": 0, "y": 0})
    ctx.bridge.wait_until(lambda s: ((s.get("equipped") or {}).get(slot) or {}).get("id") == chosen["id"],
                          timeout=float(step.get("timeout", 10)), brief=False, what=f"{chosen.get('name')} equipped")


MAIN_INVENTORY = 1
EQUIP_SLOTS = {"weapon": 3, "offhand": 4}  # InventoryId: Weapon1 (row 2) + 1, Offhand1

VERBS = {
    "goto": goto, "tp": tp, "kill": kill, "pickup": pickup, "interact": interact, "talk": talk,
    "reward": reward, "wait_flag": wait_flag, "command": command, "pantheon": pantheon, "passives": passives,
    "equip": equip, "use_item": use_item, "stash": stash,
}
# Other names of verbs: the act 1 step files say "use" for a book.
ALIASES = {"use": "use_item"}
REQUIRED = {"goto": ["area"], "kill": ["target"], "pickup": ["item"], "interact": ["target"], "talk": ["npc"],
            "reward": ["npc"], "wait_flag": ["flag"], "command": ["text"], "pantheon": [],
            "equip": ["item"], "use_item": ["item"]}
# The compact form's value: {"kill": "Hillock"} == {"verb": "kill", "target": "Hillock"}.
MAIN_ARGUMENT = {"goto": "area", "tp": "target", "kill": "target", "pickup": "item", "interact": "target",
                 "talk": "npc", "reward": "npc", "wait_flag": "flag", "command": "text", "equip": "item",
                 "use_item": "item"}
VERB_KEYS = ("verb", "do", "action")


def normalize(step):
    """The step in its explicit form: {"verb": ..., <fields>}. Accepts the `verb`/`do`/`action` keys and the
    compact form (first key = the verb, mapped to its main argument). Returns non-dicts unchanged."""
    if not isinstance(step, dict):
        return step
    for key in VERB_KEYS:
        if key in step:
            result = {k: v for k, v in step.items() if k not in VERB_KEYS}
            return {"verb": ALIASES.get(step[key], step[key]), **result}
    if step:
        key, value = next(iter(step.items()))
        first = ALIASES.get(key, key)
        if first in VERBS:
            result = {"verb": first}
            if first in MAIN_ARGUMENT:
                result[MAIN_ARGUMENT[first]] = value
            result.update((k, v) for k, v in step.items() if k != key)
            return result
    return dict(step)


def verb_of(step):
    return normalize(step).get("verb")


def validate(step):
    """An error text for a malformed step, else None."""
    if not isinstance(step, dict):
        return f"step is not a dict: {step!r}"
    step = normalize(step)
    verb = step.get("verb")
    if verb not in VERBS:
        return f"unknown verb {verb!r} (known: {', '.join(VERBS)})"
    if verb == "tp" and "target" not in step and not ("x" in step and "y" in step):
        return "tp needs target or x/y"
    missing = [f for f in REQUIRED.get(verb, []) if f not in step]
    return f"{verb} needs {', '.join(missing)}" if missing else None


def describe(step):
    step = normalize(step)
    fields = " ".join(f"{k}={v!r}" for k, v in step.items() if k != "verb")
    return f"{step.get('verb')} {fields}".strip()


def run_step(ctx, step):
    """Run one step; returns 'skipped' (skip_if flag set) or 'done'."""
    step = normalize(step)
    flags = step.get("skip_if")
    flags = [flags] if isinstance(flags, str) else list(flags or [])
    if flags:
        brief = ctx.bridge.state(brief=True)
        if any(has_flag(brief, f) for f in flags):   # a list: skipped when any of them is set
            return "skipped"
    verb = step["verb"]
    if step.get("area") and verb != "goto":
        ensure_area(ctx, step["area"])
    VERBS[verb](ctx, step)
    if step.get("expect_flag"):
        expected = step["expect_flag"]
        ctx.bridge.wait_until(lambda s: has_flag(s, expected), timeout=float(step.get("timeout", 30)), poll=0.5,
                              what=f"flag {expected}")
    return "done"
