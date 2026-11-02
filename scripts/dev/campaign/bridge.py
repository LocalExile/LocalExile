"""Thin campaign wrapper around the native client bridge (`scripts/dev/client_control.py`).

`Bridge` sends bridge requests in-process, parses the Gateway's `/state` reply and polls for conditions. The
control object is injectable: anything with `execute(request) -> dict` that raises on error (tests use a fake).
"""
import json
import logging
from pathlib import Path
import sys
import time

ROOT = Path(__file__).resolve().parents[3]
DEV = ROOT / "scripts" / "dev"
if str(DEV) not in sys.path:
    sys.path.insert(0, str(DEV))

GATEWAY_LOG = ROOT / "logs" / "live-gateway.log"
log = logging.getLogger("campaign")


class BridgeError(RuntimeError):
    """A bridge request or reply that the campaign cannot use."""


class StateUnavailable(BridgeError):
    """`/state` gave no complete reply (loading screen, no Gateway log, a split reply cut short)."""


class WaitTimeout(TimeoutError):
    """A polled condition did not become true in time; `state` is the last state seen (or None)."""

    def __init__(self, what, state=None):
        super().__init__(f"timed out waiting for {what}")
        self.state = state


def parse_state(replies):
    """The `/state` JSON from a command's reply lines: the last `STATE ` line plus the `STATE+ ` lines after it,
    concatenated in order. None when there is no `STATE ` line or the joined text is not complete JSON."""
    if not replies:
        return None
    start = None
    for i, line in enumerate(replies):
        if line.startswith("STATE "):
            start = i
    if start is None:
        return None
    parts = [replies[start][len("STATE "):]]
    for line in replies[start + 1:]:
        if line.startswith("STATE+ "):
            parts.append(line[len("STATE+ "):])
        elif line.startswith("STATE"):
            break
        # other chat lines mixed in (another command's replies) are ignored
    try:
        value = json.loads("".join(parts))
    except ValueError:
        return None
    return value if isinstance(value, dict) else None


def redact(request):
    return {k: ("***" if k == "password" else v) for k, v in request.items()}


class Bridge:
    """Campaign helpers over `ClientControl.execute`. `sleep`/`clock` are injectable so tests run instantly."""

    def __init__(self, control=None, gateway_log=GATEWAY_LOG, sleep=time.sleep, clock=time.monotonic, session=None):
        if control is None:
            from client_control import ClientControl  # imports frida; only for real runs
            # The client of this checkout's session (live.ps1 -Session; None: Game_CLIENT_SESSION, else the default
            # client): the bridge attaches to it by its recorded PID, never to another session's or checkout's client.
            control = ClientControl(session)
        self.control = control
        self.gateway_log = Path(gateway_log)
        self.sleep = sleep
        self.clock = clock
        self.area_wait_supported = None  # unknown until the first area:wait
        self.last_state = None

    def close(self):
        close = getattr(self.control, "close", None)
        if close:
            close()

    # --- raw requests -------------------------------------------------------------------------------------------
    def execute(self, request):
        started = self.clock()
        try:
            result = self.control.execute(dict(request))
        except Exception as error:
            log.debug("bridge %s failed after %d ms: %s", redact(request), (self.clock() - started) * 1000, error)
            raise
        log.debug("bridge %s -> %d ms", redact(request), (self.clock() - started) * 1000)
        return result if isinstance(result, dict) else {"result": result}

    # How long a chat command waits for the client to finish a loading screen ("Chat requires an active area": an NPC's
    # AreaTransitionTo, a waypoint or an exit was just taken).
    LOADING_TIMEOUT = 60.0

    def cmd(self, text, wait=1.5):
        """Send a slash command; returns the Gateway's reply lines ([] when none came within `wait`). While the client is
        between areas (no active area to chat in) it waits for the new area, up to LOADING_TIMEOUT."""
        deadline = self.clock() + self.LOADING_TIMEOUT
        while True:
            try:
                result = self.execute({"command": "command", "text": text, "wait": wait})
                break
            except Exception as error:
                if "requires an active area" not in str(error) or self.clock() >= deadline:
                    raise
                log.debug("  %s: the client is loading an area; waiting", text)
                self.sleep(0.5)
        replies = result.get("replies")
        if replies is None:
            raise BridgeError("no Gateway replies: the servers must be started by this checkout's live.ps1 "
                              "(logs/live-gateway.log)")
        if not text.startswith("/state"):
            for line in replies:
                log.debug("  reply: %s", line)
        return list(replies)

    def status(self):
        return self.execute({"command": "game:status"})

    # --- /state ---------------------------------------------------------------------------------------------------
    def state(self, brief=False, retries=3, wait=3.0):
        """The parsed `/state` (or `/state brief`) reply. Retries a missing/partial reply `retries` times."""
        text = "/state brief" if brief else "/state"
        for attempt in range(retries):
            replies = self.cmd(text, wait=wait)   # waits through an area load (Bridge.cmd)
            value = parse_state(replies)
            if value is not None:
                self.last_state = value
                return value
            log.debug("%s: no complete STATE reply (attempt %d): %s", text, attempt + 1,
                      [r[:80] for r in replies[:3]])
            self.sleep(0.3)
        raise StateUnavailable(f"{text} gave no complete STATE reply after {retries} attempts")

    def wait_until(self, predicate, timeout=30.0, poll=0.3, brief=True, what="condition"):
        """Poll `/state` until `predicate(state)` is true; returns that state. A missing state reply (loading)
        counts as not yet. Raises WaitTimeout with the last state seen."""
        deadline = self.clock() + timeout
        last = None
        while True:
            try:
                last = self.state(brief=brief, retries=1)
                if predicate(last):
                    return last
            except StateUnavailable:
                pass
            if self.clock() >= deadline:
                raise WaitTimeout(what, last)
            self.sleep(poll)

    def wait_area(self, area, timeout=60.0, previous=None, poll=0.3):
        """Wait until the player is in `area` (WorldAreas Id) with the instance entered. Uses the bridge's
        `area:wait` when it exists, then confirms through `/state brief`. `previous` is the bridge's `game:status`
        from before the travel (area:wait's `from`); anything else is not sent."""
        deadline = self.clock() + timeout
        if self.area_wait_supported is not False:
            request = {"command": "area:wait", "area": area, "timeout": timeout}
            if isinstance(previous, dict):
                request["from"] = previous
            try:
                self.execute(request)
                self.area_wait_supported = True
            except Exception as error:  # unknown command (older bridge) or its own timeout: poll below
                if "Unknown command" in str(error):
                    self.area_wait_supported = False
                else:
                    log.debug("area:wait %s: %s", area, error)
        last = None
        while True:
            try:
                status = self.status()
                ready = status.get("instanceState") == 3 and status.get("screen", "game") == "game"
                if ready and status.get("area", area) == area:
                    last = self.state(brief=True, retries=1)
                    if last.get("area") == area:
                        return last
            except StateUnavailable:
                pass
            if self.clock() >= deadline:
                raise WaitTimeout(f"area {area}", last)
            self.sleep(poll)
