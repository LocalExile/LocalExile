"""Play the campaign through the native client bridge, quest by quest (Agents/docs/dev/campaign-automation.md).

    .venv/Scripts/python.exe scripts/dev/campaign/runner.py --new --class Witch
    .venv/Scripts/python.exe scripts/dev/campaign/runner.py --character CampAbc --only a1q4
    .venv/Scripts/python.exe scripts/dev/campaign/runner.py --new --preset Act1Complete --from a2q11
    .venv/Scripts/python.exe scripts/dev/campaign/runner.py --list
    .venv/Scripts/python.exe scripts/dev/campaign/runner.py --dry-run --to a1q9

Needs the local servers (scripts/dev/live.ps1 -NoClient); opens the client itself (game:open). Quests come from
quests/<Id>.py in order.ORDER; a quest whose DONE flag is set is skipped, so a run resumes where the last one ended.
"""
import argparse
import datetime
import getpass
import hashlib
import importlib.util
import logging
import os
from pathlib import Path
import re
import sys

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

from bridge import ROOT, Bridge, WaitTimeout  # noqa: E402  (bridge sets sys.path)
import order as order_module  # noqa: E402
import steps  # noqa: E402

log = logging.getLogger("campaign")

DONE, ALREADY, NOT_IMPLEMENTED, BLOCKED, FAILED, NOT_RUN = (
    "done", "skipped (already)", "not implemented", "blocked", "failed", "not run")
CLASSES = ["Marauder", "Witch", "Scion", "Ranger", "Duelist", "Shadow", "Templar"]


class SessionError(RuntimeError):
    pass


class SessionLock:
    r"""One run per client session of this checkout: a named mutex (Local\LocalExile-campaign-<checkout>-<session>) held
    while the run drives the session's client, so a second runner on the same session stops instead of sending its
    requests to the same client. Released by `close` (or when the process ends). No-op off Windows."""

    def __init__(self, session, root=ROOT):
        self.handle = None
        checkout = hashlib.sha1(os.path.normcase(os.path.abspath(str(root))).encode("utf-8")).hexdigest()[:16]
        self.name = "Local\\" + f"LocalExile-campaign-{checkout}-{session}"
        if os.name != "nt":
            return
        import ctypes
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.CreateMutexW.restype = ctypes.c_void_p
        kernel.CreateMutexW.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_wchar_p]
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        self._close = kernel.CloseHandle
        handle = kernel.CreateMutexW(None, True, self.name)
        error = ctypes.get_last_error()
        if not handle:
            raise SessionError(f"cannot create the session lock {self.name} (error {error})")
        if error == 183:  # ERROR_ALREADY_EXISTS: another run of this checkout drives this session
            kernel.CloseHandle(handle)
            raise SessionError(f"another campaign run drives session {session!r} of this checkout; wait for it to end "
                               "or use another --session")
        self.handle = handle

    def close(self):
        if self.handle:
            self._close(self.handle)
            self.handle = None


# --- inputs ------------------------------------------------------------------------------------------------------

def read_password(path):
    """The `Password: <x>` line of a login notes file (data/offline-login.txt); None when absent."""
    try:
        text = Path(path).read_text(encoding="utf-8-sig")
    except OSError:
        return None
    match = re.search(r"^\s*Password\s*:\s*(.*?)\s*$", text, re.IGNORECASE | re.MULTILINE)
    return match.group(1) if match and match.group(1) else None


def default_name(now=None):
    """`Camp` + the month-day-hour-minute-second, digits as letters (character names here are letters only)."""
    stamp = (now or datetime.datetime.now()).strftime("%m%d%H%M%S")
    return "Camp" + "".join(chr(ord("a") + int(d)) for d in stamp)


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    pick = parser.add_argument_group("quest selection (quest Ids from order.py)")
    pick.add_argument("--only", help="only these quests (comma-separated)")
    pick.add_argument("--from", dest="start", help="start at this quest")
    pick.add_argument("--to", dest="end", help="stop after this quest")
    pick.add_argument("--preset", help="send /quest_preset <CharacterStartQuestState Id> first (e.g. Act1Complete)")
    who = parser.add_argument_group("character")
    who.add_argument("--character", help="continue this character (login + play)")
    who.add_argument("--new", action="store_true", help="create a fresh character")
    who.add_argument("--class", dest="class_name", default="Witch", choices=CLASSES, help="class for --new")
    who.add_argument("--name", help="name for --new (default Camp + time, letters only)")
    who.add_argument("--replace", action="store_true", help="--new: delete an existing character of that name")
    who.add_argument("--league", default="Standard")
    who.add_argument("--account", default="offline")
    who.add_argument("--password-file", default=str(ROOT / "data" / "offline-login.txt"),
                     help="file with a 'Password: <x>' line; prompted once when missing")
    run = parser.add_argument_group("run")
    run.add_argument("--session", help="the client session to drive (live.ps1 -Session; default Game_CLIENT_SESSION, "
                                       "else the default client)")
    run.add_argument("--god", dest="god", action="store_true", default=True, help="god mode on (default)")
    run.add_argument("--no-god", dest="god", action="store_false")
    run.add_argument("--retries", type=int, default=3, help="retries per failed step (default 3)")
    run.add_argument("--seed", type=int, default=1, help="seed for the new-instance recovery (default 1)")
    run.add_argument("--done-timeout", type=float, default=15.0, help="seconds to wait for a quest's DONE flag")
    run.add_argument("--stop-on-fail", action="store_true", help="stop at the first failed quest")
    run.add_argument("--spend-passives", action="store_true",
                     help="allocate unspent passive points after each quest and in `passives` steps (default: leave them)")
    run.add_argument("--no-auto-rewards", dest="auto_rewards", action="store_false",
                     help="do not claim QuestReward options after each quest")
    run.add_argument("--dry-run", action="store_true", help="print the plan, touch nothing")
    run.add_argument("--list", action="store_true", help="print the quest order and which quests are implemented")
    options = parser.parse_args(argv)
    if options.new and options.character:
        parser.error("--new and --character exclude each other")
    if options.name and not re.fullmatch(r"[A-Za-z]{3,23}", options.name):
        parser.error("--name must be 3-23 letters")
    return options


def select(order, only=None, start=None, end=None):
    """The quests to run, in order. Raises ValueError for an Id not in the order."""
    def check(quest):
        if quest not in order:
            raise ValueError(f"unknown quest {quest!r} (not in order.py)")
        return quest
    if only:
        wanted = {check(q.strip()) for q in only.split(",") if q.strip()}
        return [q for q in order if q in wanted]
    first = order.index(check(start)) if start else 0
    last = order.index(check(end)) if end else len(order) - 1
    return list(order[first:last + 1])


class Quest:
    def __init__(self, quest, done=None, steps_=None, requires=(), error=None):
        self.id, self.done, self.steps, self.requires, self.error = quest, done, steps_ or [], list(requires), error


def load_quest(quest, quests_dir):
    """The quest's step module, or None when it has no file. A broken file gives a Quest with `error`."""
    path = order_module.quest_path(quest, quests_dir)
    if not path.is_file():
        return None
    try:
        spec = importlib.util.spec_from_file_location(f"campaign_quest_{quest}", path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        if getattr(module, "QUEST", None) != quest:
            raise ValueError(f"QUEST is {getattr(module, 'QUEST', None)!r}, expected {quest!r}")
        done = getattr(module, "DONE", None)
        if not isinstance(done, str) or not done:
            raise ValueError("DONE must be a quest flag Id")
        steps_ = getattr(module, "STEPS", None)
        if not isinstance(steps_, list):
            raise ValueError("STEPS must be a list")
        problems = [f"step {i}: {e}" for i, e in enumerate(map(steps.validate, steps_), 1) if e]
        if problems:
            raise ValueError("; ".join(problems))
        return Quest(quest, done, [steps.normalize(s) for s in steps_], getattr(module, "REQUIRES", []) or [])
    except Exception as error:  # a broken quest file fails that quest, not the run
        return Quest(quest, error=f"{type(error).__name__}: {error}")


def quest_names():
    try:
        import json
        rows = json.loads((ROOT / "data" / "tables" / "Quest.json").read_text(encoding="utf-8"))
        return {r["Id"]: r.get("Name", "") for r in rows}
    except (OSError, ValueError, KeyError):
        return {}


# --- logging and diagnostics -------------------------------------------------------------------------------------

def close_logging():
    for handler in [h for h in log.handlers if getattr(h, "campaign_run", False)]:
        log.removeHandler(handler)
        handler.close()


def setup_logging(log_dir, stamp=None):
    stamp = stamp or datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    Path(log_dir).mkdir(parents=True, exist_ok=True)
    path = Path(log_dir) / f"campaign-{stamp}.log"
    close_logging()  # a previous run's (tests run main repeatedly)
    log.setLevel(logging.DEBUG)
    log.propagate = False
    console = logging.StreamHandler(sys.stdout)
    console.setLevel(logging.INFO)
    console.setFormatter(logging.Formatter("%(asctime)s %(message)s", "%H:%M:%S"))
    file = logging.FileHandler(path, encoding="utf-8")
    file.setLevel(logging.DEBUG)
    file.setFormatter(logging.Formatter("%(asctime)s.%(msecs)03d %(levelname)s %(message)s", "%H:%M:%S"))
    for handler in (console, file):
        handler.campaign_run = True
        log.addHandler(handler)
    return path


def tail(path, lines=40, width=300):
    """The last `lines` lines of a text file, each cut to `width` characters ([] without the file)."""
    try:
        with Path(path).open("rb") as handle:
            handle.seek(0, 2)
            size = handle.tell()
            handle.seek(max(0, size - 256 * 1024))
            data = handle.read()
    except OSError:
        return []
    text = data.decode("utf-8", errors="replace").splitlines()[-lines:]
    return [line if len(line) <= width else line[:width] + " ..." for line in text]


def summarize_state(state, quest=None, radius=80.0):
    """A compact multi-line view of /state for failure diagnostics."""
    if not state:
        return "  (no state)"
    pos = state.get("pos")
    flags = [f for f in state.get("flags") or [] if quest and f.lower().startswith(quest.lower() + "-")]
    out = [f"  area {state.get('area')} ({state.get('areaName')}) seed {state.get('seed')} instance "
           f"{state.get('instance')} pos {pos} level {state.get('level')} life {state.get('life')}/"
           f"{state.get('maxLife')} god {state.get('god')} talking {state.get('talking')} inTalk {state.get('inTalk')}",
           f"  {quest} flags: {flags}"]
    if state.get("reward"):
        out.append(f"  reward offer: {[i.get('name') for i in state['reward'].get('items') or []]}")

    def near(key, fmt):
        entries = [e for e in state.get(key) or [] if steps.distance(e.get("pos"), pos) <= radius or key == "npcs"]
        if entries:
            out.append(f"  {key}: " + "; ".join(fmt(e) for e in steps.nearest(entries, pos)[:8]))
    near("monsters", lambda m: f"{m.get('name')} #{m.get('id')} {'alive' if m.get('alive', True) else 'dead'} "
                               f"{m.get('pos')}")
    near("npcs", lambda n: f"{n.get('name')} #{n.get('id')} {n.get('pos')} options "
                           f"{[(o.get('index'), o.get('text')) for o in n.get('options') or []]}")
    near("objects", lambda o: f"{o.get('name') or o.get('type')} #{o.get('id')} used={o.get('used')} {o.get('pos')}")
    near("items", lambda i: f"{i.get('name')} ({i.get('base')}) #{i.get('id')} {i.get('pos')}")
    near("transitions", lambda t: f"{t.get('name')} -> {t.get('to')} #{t.get('id')} {t.get('pos')}")
    inventory = state.get("inventory")
    if inventory:
        out.append(f"  inventory: {len(inventory.get('items') or [])} items, {steps.free_cells(inventory)} free cells")
    return "\n".join(out)


def diagnostics(bridge, quest, step, error):
    lines = [f"step failed: {quest}: {steps.describe(step)}: {type(error).__name__}: {error}"]
    try:
        state = bridge.state()
    except Exception as state_error:
        state = getattr(error, "state", None)
        lines.append(f"  (/state failed: {state_error})")
    lines.append(summarize_state(state, quest))
    lines.append(f"  last lines of {bridge.gateway_log}:")
    lines += ["    " + line for line in tail(bridge.gateway_log)]
    return "\n".join(lines)


# --- recovery and per-quest work ---------------------------------------------------------------------------------

def in_game(bridge):
    try:
        status = bridge.status()
    except Exception as error:
        log.debug("  status: %s", error)
        return False
    return status.get("screen") == "game" and status.get("instanceState") == 3


def reconnect_if_needed(ctx, wait=15.0):
    """When the client left the game (the Gateway closes a connection whose stream no longer decodes, or the client
    was disconnected) or /state no longer answers, log in again and play the character. Returns True if it did."""
    if getattr(ctx, "reconnect", None) is None:
        return False
    if in_game(ctx.bridge):
        try:
            ctx.bridge.state(brief=True)
            return False
        except Exception as error:
            log.warning("  recovery: /state does not answer (%s); waiting for the connection to close", error)
        deadline = ctx.bridge.clock() + wait
        while in_game(ctx.bridge) and ctx.bridge.clock() < deadline:
            ctx.bridge.sleep(0.5)
    log.info("  recovery: logging in again and playing the character")
    try:
        ctx.reconnect()
    except Exception as error:
        log.warning("  recovery: reconnect failed: %s", error)
    return True


def recover(ctx, retry):
    """First, when the client is out of the game or deaf: log in again and play (reconnect_if_needed). Otherwise before
    retry 1: nothing (re-run). Retry 2: close any dialogue, re-enter the current area. Retry 3+: a new instance of the
    current area with the run's seed (1 by default: quest objects follow the seed-1 captures)."""
    if reconnect_if_needed(ctx):
        return
    if retry <= 1:
        log.info("  recovery: re-run the step")
        return
    try:
        steps.close_dialogue(ctx)
    except Exception as error:
        log.debug("  close dialogue: %s", error)
    area = (ctx.bridge.last_state or {}).get("area")
    try:
        area = ctx.bridge.state(brief=True).get("area") or area
    except Exception as error:
        log.debug("  state: %s", error)
    if not area:
        log.warning("  recovery: current area unknown")
        return
    # Leave and come back as a player does (never /goto): out by the nearest exit, else by waypoint to another activated
    # one, then back by the usual goto (its waypoint, the exit back or a route). The Gateway makes the area anew.
    try:
        state = ctx.bridge.state()
        exits = steps.nearest([t for t in state.get("transitions") or [] if t.get("to") and t.get("to") != area], state.get("pos"))
        others = [w for w in state.get("waypoints") or [] if w != area]
        away = exits[0]["to"] if exits else (others[0] if others else None)
        if away is None:
            log.warning("  recovery: no exit or other waypoint to leave %s by; re-run the step", area)
            return
        log.info("  recovery: leave %s for %s and come back (a new instance)", area, away)
        steps.goto(ctx, {"area": away, "via": "transition" if exits else "waypoint"})
        steps.goto(ctx, {"area": area})
    except (steps.StepFailed, WaitTimeout) as error:
        log.warning("  recovery: %s", error)


def run_step_with_retries(ctx, quest, index, total, step, retries):
    label = f"{quest} [{index}/{total}] {steps.describe(step)}"
    # An optional step (e.g. the opening encounter on a character that already did it) is tried once; failing it is
    # only a warning.
    optional = bool(step.get("optional"))
    if optional:
        retries = 0
    for attempt in range(retries + 1):
        if attempt:
            recover(ctx, attempt)
        started = ctx.bridge.clock()
        try:
            result = steps.run_step(ctx, step)
            log.info("%s: %s in %d ms", label, result, (ctx.bridge.clock() - started) * 1000)
            return True
        except NotImplementedError as error:
            log.error("%s: not implemented: %s", label, error)
            return False
        except Exception as error:
            log.warning("%s: attempt %d/%d failed after %d ms", label, attempt + 1, retries + 1,
                        (ctx.bridge.clock() - started) * 1000)
            if optional:
                log.warning("%s: optional step skipped: %s", label, error)
                return True
            log.warning(diagnostics(ctx.bridge, quest, step, error))
    return False


def claim_rewards(ctx, limit=8):
    """Claim every QuestReward option of the NPCs in the current area (a reward forgotten by a quest file)."""
    tried = set()
    for _ in range(limit):
        state = ctx.bridge.state()
        offer = next(((n, o) for n in state.get("npcs") or [] for o in n.get("options") or []
                      if o.get("category") == "QuestReward" and (n.get("id"), o.get("text")) not in tried), None)
        if offer is None:
            return
        npc, option = offer
        tried.add((npc.get("id"), option.get("text")))
        log.info("  claiming %s's reward %r", npc.get("name"), option.get("text"))
        try:
            steps.reward(ctx, {"verb": "reward", "npc": npc["id"], "option": option["text"]})
        except Exception as error:
            log.warning("  reward %r from %s failed: %s", option.get("text"), npc.get("name"), error)
            try:
                steps.close_dialogue(ctx)
            except Exception:
                pass


def make_room(ctx, minimum=16):
    """Keep at least `minimum` free main-inventory cells: below that, in an area with a Stash (a town), the carried
    non-quest items go into it (steps.stash_items); elsewhere it waits for the next town (a reward without room makes
    room itself at its NPC's Stash)."""
    try:
        state = ctx.bridge.state()
    except Exception as error:
        log.debug("make_room: %s", error)
        return
    free = steps.free_cells(state.get("inventory") or {})
    if free >= minimum:
        return
    if not steps.stash_reachable(state):
        log.info("inventory has %d free cells (< %d); no Stash reachable from %s yet", free, minimum, state.get("area"))
        return
    log.info("inventory has %d free cells (< %d): to the Stash", free, minimum)
    steps.stash_items(ctx)


def after_quest(ctx, auto_rewards=True):
    for name, action in (("rewards", lambda: claim_rewards(ctx) if auto_rewards else None),
                         ("passives", lambda: steps.passives(ctx)), ("inventory", lambda: make_room(ctx))):
        try:
            action()
        except Exception as error:
            log.warning("after-quest %s: %s", name, error)


# --- session -----------------------------------------------------------------------------------------------------

def start_session(bridge, options, password_provider):
    """Open the client, log in when needed, create/play the character; returns its name."""
    log.info("opening the client")
    bridge.execute({"command": "game:open"})
    status = bridge.status()
    screen = status.get("screen")
    if options.character and screen == "game" and status.get("character") == options.character:
        log.info("already playing %s", options.character)
        return options.character
    if not options.character and not options.new:
        if screen == "game" and status.get("character"):
            log.info("continuing the character in game: %s", status["character"])
            return status["character"]
        raise SessionError("no character in game: give --character <name> or --new")
    if screen not in ("characters", "game"):
        log.info("logging in as %s", options.account)
        password = password_provider()
        try:
            bridge.execute({"command": "login", "account": options.account, "password": password})
        finally:
            del password
    name = options.character
    if options.new:
        name = options.name or default_name()
        if bridge.status().get("screen") == "game":
            bridge.execute({"command": "character:select"})
        existing = [c.get("name") for c in bridge.execute({"command": "character:list"}).get("characters") or []]
        if name in existing:
            if not options.replace:
                raise SessionError(f"character {name} exists; use --replace to delete it first, or another --name")
            log.info("deleting the existing %s", name)
            bridge.execute({"command": "character:delete", "name": name, "missingOk": True})
        log.info("creating %s (%s, %s)", name, options.class_name, options.league)
        bridge.execute({"command": "character:create", "name": name, "class": options.class_name,
                        "league": options.league})
    log.info("playing %s", name)
    bridge.execute({"command": "character:play", "name": name})
    return name


def ensure_god(bridge, on):
    """`/god immune` (god mode + no damage) when wanted and off; `/god off` when not wanted and on."""
    state = bridge.state(brief=True)
    if on and (not state.get("god") or state.get("immune") is False):
        for line in bridge.cmd("/god immune", wait=1.0):
            log.info("  /god: %s", line)
        try:
            bridge.wait_until(lambda s: s.get("god") and s.get("immune") is not False, timeout=5, what="god mode")
        except WaitTimeout:
            log.warning("god mode did not turn on")
    elif not on and state.get("god"):
        bridge.cmd("/god off", wait=1.0)


# --- the run -----------------------------------------------------------------------------------------------------

def print_list(order, quests_dir, out=print):
    names = quest_names()
    present = {q for q in order if order_module.quest_path(q, quests_dir).is_file()}
    for quest in order:
        out(f"{quest:<10} {'implemented' if quest in present else 'not implemented':<16} {names.get(quest, '')}")
    out(f"{len(present)} of {len(order)} implemented")


def print_plan(selected, modules, out=print):
    for quest in selected:
        module = modules.get(quest)
        if module is None:
            out(f"{quest}: not implemented")
        elif module.error:
            out(f"{quest}: invalid quest file: {module.error}")
        else:
            requires = f" (requires {', '.join(module.requires)})" if module.requires else ""
            out(f"{quest}: DONE {module.done}{requires}")
            for i, step in enumerate(module.steps, 1):
                out(f"  {i}. {steps.describe(step)}")


def run_quest(ctx, module, modules, options, quests_dir):
    """Run one implemented quest; returns (status, note)."""
    flags = set(ctx.bridge.state(brief=True).get("flags") or [])
    if module.done in flags:
        return ALREADY, ""
    blockers = []
    for required in module.requires:
        other = modules.get(required) if required in modules else load_quest(required, quests_dir)
        if other is None or other.error or other.done not in flags:
            blockers.append(required)
    if blockers:
        return BLOCKED, f"requires {', '.join(blockers)}"
    log.info("=== %s (%d steps)", module.id, len(module.steps))
    status, note = DONE, ""
    for i, step in enumerate(module.steps, 1):
        if not run_step_with_retries(ctx, module.id, i, len(module.steps), step, options.retries):
            status, note = FAILED, f"step {i}: {steps.describe(step)}"
            break
    if status == DONE:
        try:
            ctx.bridge.wait_until(lambda s: module.done in (s.get("flags") or []), timeout=options.done_timeout,
                                  poll=0.5, what=f"flag {module.done}")
        except WaitTimeout:
            status, note = FAILED, f"all steps ran but {module.done} is not set"
    after_quest(ctx, options.auto_rewards)
    return status, note


def run_quests(ctx, selected, modules, options, quests_dir):
    """Run the selected quests; returns [(quest, status, seconds, note)]."""
    results = []
    status_by_quest = {}
    for position, quest in enumerate(selected):
        started = ctx.bridge.clock()
        module = modules.get(quest)
        note = ""
        if module is None:
            # No step file yet: the quest's act is not on the quest engine (each act's PR brings its step files).
            status, note = NOT_IMPLEMENTED, "no step file"
        elif module.error:
            status, note = FAILED, module.error
        else:
            try:
                status, note = run_quest(ctx, module, modules, options, quests_dir)
            except Exception as error:  # e.g. /state unavailable: this quest fails, the run goes on
                log.error("%s: %s: %s", quest, type(error).__name__, error)
                status, note = FAILED, f"{type(error).__name__}: {error}"
        seconds = ctx.bridge.clock() - started
        status_by_quest[quest] = status
        log.info("%s: %s%s (%.1f s)", quest, status, f" - {note}" if note else "", seconds)
        results.append((quest, status, seconds, note))
        if status == FAILED and options.stop_on_fail:
            results += [(q, NOT_RUN, 0.0, "") for q in selected[position + 1:]]
            break
    return results


def print_summary(results, total):
    log.info("%-10s %-18s %8s  %s", "quest", "status", "time", "note")
    for quest, status, seconds, note in results:
        log.info("%-10s %-18s %7.1fs  %s", quest, status, seconds, note)
    acts = act_table(results)
    if len(acts) > 1:
        log.info("%-9s %6s %6s %6s %9s", "act", "quests", "done", "failed", "time")
        for act, row in acts.items():
            log.info("%-9s %6d %6d %6d %8.1fs", act, row["quests"], row["done"], row["failed"], row["seconds"])
    counts = {}
    for _, status, _, _ in results:
        counts[status] = counts.get(status, 0) + 1
    log.info("total %.1f s: %s", total, ", ".join(f"{v} {k}" for k, v in counts.items()))


def act_of(quest):
    """The act a quest Id belongs to ("a10q3" -> "Act 10", "A11q0" and "a10q3a11" -> "Epilogue")."""
    if quest.endswith("a11") or quest.lower().startswith("a11"):
        return "Epilogue"
    match = re.match(r"a(\d+)q", quest)
    return f"Act {match.group(1)}" if match else "other"


def act_table(results):
    """Per act, in run order: quests, done (ran now or already), failed, seconds."""
    acts = {}
    for quest, status, seconds, _ in results:
        row = acts.setdefault(act_of(quest), {"quests": 0, "done": 0, "failed": 0, "seconds": 0.0})
        row["quests"] += 1
        row["done"] += status in (DONE, ALREADY)
        row["failed"] += status == FAILED
        row["seconds"] += seconds
    return acts


# The presets that name what a finished act's character has (CharacterStartQuestState), by the quest that ends the
# act: the flags each lists must all be on a character that played that far (the Eramir variant: every bandit
# killed). The table has none for acts 5-10; its E3StartOfAct7 / E3EndOfAct8 are E3 demo snapshots (quest items still
# held, chat flags of a played demo), not an act's end, so they are not checked.
ACT_PRESETS = {"Act1Complete": "a1q9", "Act2CompleteEramir": "a2q8", "Act3Complete": "a3q10", "Act4Complete": "a5q1a"}


def preset_flags():
    """{preset Id: [QuestFlags Ids]} of ACT_PRESETS from the extracted tables ({} without them)."""
    try:
        import json
        tables = ROOT / "data" / "tables"
        flags = [r["Id"] for r in json.loads((tables / "QuestFlags.json").read_text(encoding="utf-8"))]
        rows = json.loads((tables / "CharacterStartQuestState.json").read_text(encoding="utf-8"))
        return {r["Id"]: [flags[i] for i in r.get("QuestFlags") or [] if 0 <= i < len(flags)]
                for r in rows if r.get("Id") in ACT_PRESETS}
    except (OSError, ValueError, KeyError, IndexError):
        return {}


def character_report(bridge, character, results, modules, total, presets=None):
    """The character after the run: where it is, its passive and refund points, Pantheon, Kitava penalty, which DONE
    flags of the quests that ran are missing, and each finished act's preset flags it lacks. Logged and returned."""
    state = bridge.state(brief=True)
    flags = set(state.get("flags") or [])
    passives = state.get("passives") or {}
    pantheon = state.get("pantheon") or {}
    statuses = {}
    for _, status, _, _ in results:
        statuses[status] = statuses.get(status, 0) + 1
    ran = {quest for quest, status, _, _ in results if status in (DONE, ALREADY)}
    presets = preset_flags() if presets is None else presets
    report = {
        "character": character, "area": state.get("area"), "areaName": state.get("areaName"), "level": state.get("level"),
        "seconds": round(total, 1), "quests": statuses,
        "failed": [{"quest": q, "note": n} for q, s, _, n in results if s == FAILED],
        "acts": act_table(results),
        "passives": {"unspent": passives.get("unspent"), "quest": passives.get("quest"), "refund": passives.get("refund"),
                     "allocated": len(passives.get("allocated") or [])},
        "pantheon": {"major": pantheon.get("major"), "minor": pantheon.get("minor")},
        "resistancePenalty": state.get("resistancePenalty"),
        "missingDone": sorted(m.done for q, m in modules.items()
                              if q in ran and getattr(m, "done", None) and m.done not in flags),
        "presets": {p: sorted(set(f) - flags) for p, f in presets.items() if ACT_PRESETS[p] in ran},
    }
    log.info("character %s, level %s, in %s (%s)", character, report["level"], report["areaName"], report["area"])
    log.info("passive points: %s unspent (%s from quests), %s allocated; refund points: %s",
             passives.get("unspent"), passives.get("quest"), report["passives"]["allocated"], passives.get("refund"))
    log.info("pantheon: major %s, minor %s; Kitava resistance penalty: %s%%",
             pantheon.get("major") or "-", pantheon.get("minor") or "-", report["resistancePenalty"])
    for preset, missing in report["presets"].items():
        log.info("preset %s: %s", preset, "all flags set" if not missing else f"missing {', '.join(missing)}")
    if report["missingDone"]:
        log.info("DONE flags missing: %s", ", ".join(report["missingDone"]))
    return report


def main(argv=None, bridge=None, quests_dir=None, order=None, log_dir=None, password_reader=None):
    options = parse_args(argv)
    order = list(order or order_module.ORDER)
    quests_dir = Path(quests_dir or order_module.QUESTS)
    if options.list:
        print_list(order, quests_dir)
        return 0
    try:
        selected = select(order, options.only, options.start, options.end)
    except ValueError as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    modules = {q: m for q in selected if (m := load_quest(q, quests_dir)) is not None}
    if options.dry_run:
        print_plan(selected, modules)
        return 0
    log_path = setup_logging(log_dir or ROOT / "logs")
    log.info("campaign run: %d quests (%s .. %s), log %s", len(selected), selected[0] if selected else "-",
             selected[-1] if selected else "-", log_path)

    def password_provider():
        if password_reader is not None:
            return password_reader()
        value = read_password(options.password_file)
        return value if value else getpass.getpass(f"Local password for {options.account}: ")

    own_bridge = bridge is None
    lock = None
    try:
        session = options.session or os.environ.get("Game_CLIENT_SESSION") or "default"
        try:
            lock = SessionLock(session)
        except SessionError as error:
            log.error("%s", error)
            return 2
        bridge = bridge or Bridge(session=options.session)
        started = bridge.clock()
        try:
            character = start_session(bridge, options, password_provider)
            if options.preset:
                for line in bridge.cmd(f"/quest_preset {options.preset}", wait=1.5):
                    log.info("  /quest_preset: %s", line)
            ensure_god(bridge, options.god)
        except Exception as error:
            log.error("session start failed: %s: %s", type(error).__name__, error)
            return 2
        ctx = steps.Context(bridge, class_name=options.class_name if options.new else None, seed=options.seed)
        ctx.spend_passives = options.spend_passives
        resume = argparse.Namespace(**{**vars(options), "character": character, "new": False})

        def reconnect():
            start_session(bridge, resume, password_provider)
            ensure_god(bridge, options.god)
        ctx.reconnect = reconnect
        log.info("character %s", character)
        results = run_quests(ctx, selected, modules, options, quests_dir)
        total = bridge.clock() - started
        print_summary(results, total)
        try:
            report = character_report(bridge, character, results, modules, total)
            summary = Path(str(log_path)[:-len(".log")] + ".summary.json")
            import json
            summary.write_text(json.dumps(report, indent=1), encoding="utf-8")
            log.info("summary: %s", summary)
        except Exception as error:  # the report is a convenience; the run's result stands without it
            log.warning("character report failed: %s: %s", type(error).__name__, error)
        log.info("log: %s", log_path)
        return 1 if any(status == FAILED for _, status, _, _ in results) else 0
    finally:
        if own_bridge and bridge is not None:
            bridge.close()
        if lock is not None:
            lock.close()
        close_logging()


if __name__ == "__main__":
    sys.exit(main())
