# usage: ot_components.py [--check] [--all] [--md <out.md>] [--json <out.json>] [Metadata/Type ...]
#        ot_components.py --items --json data/item-types.json   (every BaseItemTypes row from the .it files)
#
# Merged component list per object type, as the client builds it from the .ot/.otc files:
#   1. the object-type constructor 0x141ec1360 creates "Positioned" first, for every type, before it loads the file
#      (0x142474ae0 -> parser 0x142475bd0);
#   2. "extends" chains are loaded parent-first (0x142476300 -> 0x142474fc0, recursive; "nothing" ends the chain);
#   3. the common pass (0x142476580, mode 0) appends each top-level section in file order; a name already present
#      keeps its first position (lookup in the name map at type+0x28, append 0x142474d10). "client"/"server" blocks are
#      skipped in this pass and GEAL blocks are scripts, not components;
#   4. type+0x80 = the count after the common pass: 0x141ebe190 calls slot 17 (+0x88) on exactly these components,
#      in this order, when it reads the 0x2b9 blob;
#   5. the client pass (mode 1) then appends the sections of every "client { }" block in the chain (parent-first)
#      and, unverified, the sections of .otc files (none of the traced types has one). They are never deserialized.
# Validated with --check against the two objects traced live with Frida (Agents/docs/protocol/04, 08).
#
# Files come from the GGPK through scripts/re/ggdump (read-only; works while the client runs) and are cached in
# Tools/re/out/ot. --all enumerates object types from every Metadata path in data/tables/*.json plus the raw
# strings of the dat files our extractor could not decode (MonsterVarieties, Chests), plus the tile objects' types
# in data/area-transitions.json (scripts/re/atlas; e.g. Metadata/QuestObjects/WaterCave/WaterCaveAreaTransition), the
# server objects of the rooms in data/room-points.json and QUEST_TILE_TYPES (quest objects that tiles place).
import json, os, re, subprocess, sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ROOT = REPO
GGPK = os.path.join(ROOT, r"Client\Content.ggpk")
CACHE = os.path.join(ROOT, r"Tools\re\out\ot")
REGISTRY = os.path.join(REPO, "Agents", "docs", "re", "components.json")
RAW_DATS = ["Data/MonsterVarieties.datc64", "Data/Chests.datc64"]

# Live component vtables, in deserialize order (Frida, scripts/re/frida/trace-player-blob.js).
GROUND_TRUTH = {
    # Agents/docs/protocol/04-player-entity.md section 3 (12 components, the last two not reached by the read loop)
    "Metadata/Characters/Int/Int": ["0x1435a0f18", "0x1435dd4e8", "0x1435a6ba0", "0x14359f5f8", "0x1435a7340",
                                    "0x1435a5030", "0x143466450", "0x1435a0078", "0x14359e078", "0x14346e558",
                                    "0x143468920", "0x14346c238"],
    # Agents/docs/protocol/08-area-transitions.md section 2 (InstanceEntities.AreaTransitionObject.ComponentOrder)
    "Metadata/MiscellaneousObjects/AreaTransition": ["0x1435a0f18", "0x1435a55e8", "0x1435a5030", "0x14346de00",
                                                     "0x1435dd4e8", "0x1435a6ae0", "0x1435a7018", "0x14359f7d0",
                                                     "0x143468920", "0x14346c238"],
}
# Components the read loop reaches (type+0x80), traced.
GROUND_TRUTH_READ = {"Metadata/Characters/Int/Int": 10, "Metadata/MiscellaneousObjects/AreaTransition": 8}


def cache_name(path):
    # \\?\: these names are long, and a release's install folder can take them past Windows' 260 characters.
    return "\\\\?\\" + os.path.join(CACHE, path.replace("/", "_"))


def fetch(paths):
    """Dump the given GGPK paths into the cache (missing files leave a .missing marker)."""
    todo = [p for p in paths if not os.path.exists(cache_name(p)) and not os.path.exists(cache_name(p) + ".missing")]
    os.makedirs(CACHE, exist_ok=True)
    proj = os.path.join(REPO, "scripts", "re", "ggdump")
    built = False
    batches, cur = [], []
    for p in todo:  # Windows limits a command line to 32767 characters
        if cur and sum(len(x) + 3 for x in cur) + len(p) > 24000:
            batches.append(cur); cur = []
        cur.append(p)
    if cur:
        batches.append(cur)
    n = 0
    for batch in batches:
        # A release has no SDK: its launcher names the published tool.
        tool = [os.environ["Game_GGDUMP_EXE"]] if "Game_GGDUMP_EXE" in os.environ else ["dotnet", "run", "--project", proj] + (["--no-build"] if built else []) + ["--"]
        cmd = tool + [GGPK, CACHE] + batch
        out = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace").stdout
        built = True
        for line in out.splitlines():
            if line.endswith(": not found"):
                open(cache_name(line[:-len(": not found")]) + ".missing", "w").close()
        n += len(batch)
        print(f"  fetched {n}/{len(todo)}", file=sys.stderr)


def read_text(path):
    f = cache_name(path)
    if not os.path.exists(f):
        return None
    with open(f, "rb") as cached:
        b = cached.read()
    if b[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return b.decode("utf-16")
    # .amd files, and some .ao/.act files, are UTF-16LE without a byte order mark: ASCII text leaves every other byte 0.
    if len(b) > 3 and b[1] == 0 and b[3] == 0:
        return b.decode("utf-16-le")
    return b.decode("utf-8", "replace")


AC = re.compile(r'AnimationController\s*\{[^}]*?metadata\s*=\s*"([^"]+)"', re.S)


def animation_controller(text):
    """The AnimationController metadata an .ot text sets, or None (an empty AnimationController block does not count)."""
    m = AC.search(text or "")
    return m.group(1) if m else None


def extends_of(text):
    """Every metadata this .ot file extends, in file order; "nothing" marks the root and is dropped."""
    names = []
    for m in re.finditer(r'^\s*extends\s+(.+)$', text or "", re.M):
        for n in re.findall(r'"([^"]+)"', m.group(1)):
            if n != "nothing" and n not in names:
                names.append(n)
    return names


def amd_of(ao, fetch_missing=True):
    """The AnimationController metadata (.amd) of an .ao: the first of its whole extends chain that sets one."""
    seen, todo = set(), [ao]
    while todo:
        path = todo.pop(0)
        if not path or path in seen:
            continue
        seen.add(path)
        if fetch_missing:
            fetch([path])
        text = read_text(path)
        if text is None:
            continue
        if (amd := animation_controller(text)) is not None:
            return amd
        todo.extend(p if p.endswith(".ao") else p + ".ao" for p in extends_of(text))
    return None


def amd_map(aos, fetch_missing=True):
    """{ao: amd} for these .ao files, from one walk over the cached extends chains (see fetch_ao_chain)."""
    return {ao: amd_of(ao, fetch_missing) for ao in aos}


def amd_animations(amd, fetch_missing=True):
    """The .amd's animations in file order (the client's animation list): name, once/loop, length ms, events.

    Format: a version line, a count line, then per animation "<name>" / once|loop / <length ms> / <event count> /
    per event "<event>" / <time ms> / "<x> <y> <z>", then two lines. None when the file is missing, or when the
    animations read do not add up to the count line: a skipped entry would shift every later index, and a shifted index
    is still in range, so the client would play the wrong clip rather than ignore it.
    """
    if fetch_missing:
        fetch([amd])
    text = read_text(amd)
    if text is None:
        return None
    lines = [l.strip() for l in text.replace("\r", "").split("\n")]
    out, i = [], 0
    while i < len(lines):
        m = re.fullmatch(r'"([^"]+)"', lines[i])
        if m and i + 3 < len(lines) and lines[i + 1] in ("once", "loop") and lines[i + 2].isdigit() and lines[i + 3].isdigit():
            events, j = [], i + 4
            for _ in range(int(lines[i + 3])):
                if j + 2 >= len(lines):
                    break
                events.append((lines[j].strip('"'), lines[j + 1], lines[j + 2]))
                j += 3
            out.append({"name": m.group(1), "once": lines[i + 1] == "once", "length": int(lines[i + 2]), "events": events})
            i = j
        else:
            i += 1
    if len(lines) < 2 or not lines[1].isdigit() or int(lines[1]) != len(out):
        count = lines[1] if len(lines) > 1 else "?"
        print(f"warning: {amd}: read {len(out)} animations, the count line says {count}; skipped", file=sys.stderr, flush=True)
        return None
    return out



def _block(text, name):
    """The body of the first top-level `name { ... }` block of an .ao/.ot text, with quoted strings (the JSON
    animation lists in single quotes) kept whole, or None."""
    m = re.search(r'^\s*' + re.escape(name) + r'\s*\{', text or "", re.M)
    if not m:
        return None
    i, depth, quote = m.end(), 1, None
    while i < len(text) and depth:
        c = text[i]
        if quote:
            if c == "\\":
                i += 1          # an escaped character cannot close the string
            elif c == quote:
                quote = None
        elif text.startswith("//", i):
            i = text.find("\n", i)   # a comment runs to its line's end (PaleWalker01.ao has a quote in one)
            if i < 0:
                break
        elif c in "'\"":
            quote = c
        elif c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
        i += 1
    return text[m.end():i - 1] if depth == 0 else None


def _position(value):
    """An event position as [x, y, z] floats, or None (the .amd writes "0 -0 -0" for events without one; a zero
    vector is kept, since a ground-level contact is a position too)."""
    try:
        parts = [float(v) for v in (value.split() if isinstance(value, str) else value)]
    except (TypeError, ValueError):
        return None
    return parts if len(parts) == 3 else None


def ao_animation_events(ao, fetch_missing=True):
    """What the .ao chain says about its rig's animation events: (exclude, {animation: [(event, ms, [x, y, z] or
    None), ...]}).

    An .ao's AnimationController may carry `animations = '[json]'`, a list of animations with their events
    (StandardEventType: "time" in seconds, "name", optional "position"), which the client adds to the rig .amd's
    events; with `exclude_metadata_events = true` the .amd's events are dropped and this list is the whole event set
    (the Sand Spitter's attack1 contact: 416 ms in the rig, 374.5 ms at [1.33, 45.89, -71.56] in its .ao). The chain
    is walked child first, as amd_of does; a child's entries come first.
    """
    exclude, events, seen, todo = False, {}, set(), [ao]
    while todo:
        path = todo.pop(0)
        if not path or path in seen:
            continue
        seen.add(path)
        if fetch_missing:
            fetch([path])
        text = read_text(path)
        if text is None:
            continue
        body = _block(text, "AnimationController")
        if body:
            if re.search(r'\bexclude_metadata_events\s*=\s*true\b', body):
                exclude = True
            m = re.search(r"\banimations\s*=\s*'(.*?)'", body, re.S)
            if m:
                try:
                    listed = json.loads(m.group(1))
                except ValueError:
                    print(f"warning: {path}: unreadable animations list; ignored", file=sys.stderr, flush=True)
                    listed = []
                for a in listed if isinstance(listed, list) else []:
                    name = a.get("name") if isinstance(a, dict) else None
                    if not name:
                        continue
                    for e in a.get("events") or []:
                        if not isinstance(e, dict) or not e.get("name") or not isinstance(e.get("time"), (int, float)):
                            continue
                        events.setdefault(name, []).append((e["name"], int(round(e["time"] * 1000)), _position(e.get("position"))))
        todo.extend(p if p.endswith(".ao") else p + ".ao" for p in extends_of(text))
    return exclude, events


def rig_clips(amd, ao, fetch_missing=True):
    """The rig's animations (amd_animations) with the events the client ends up with for this .ao: the .amd's,
    unless the .ao chain excludes them, plus the .ao chain's own, in time order. Each event is (name, ms, position
    or None). None when the rig is unreadable."""
    animations = amd_animations(amd, fetch_missing)
    if animations is None:
        return None
    exclude, added = ao_animation_events(ao, fetch_missing)
    out = []
    for a in animations:
        events = [] if exclude else [(e, int(t), _position(p)) for e, t, p in a["events"] if t.lstrip("-").isdigit()]
        events += added.get(a["name"], [])
        events.sort(key=lambda e: e[1])
        out.append({**a, "events": events})
    return out


def first_contact(events):
    """The earliest "contact" event of a clip as (ms, position or None), or None."""
    hits = [(ms, pos) for name, ms, pos in events if name == "contact"]
    return min(hits, key=lambda h: h[0]) if hits else None


TOKEN = re.compile(r'"[^"]*"|\'[^\']*\'|[{}=]|[^\s{}=]+')


_QUEST_FLAGS = None


def quest_flag(name):
    """A quest flag named in an .ot/.it: "minus" stands for "-" (the client's name lookup 0x141de7b20), but only where the
    literal name is not a flag itself ("a3q10-KilledDominus" keeps its "minus"); QuestFlags.json decides."""
    global _QUEST_FLAGS
    if _QUEST_FLAGS is None:
        path = os.path.join(ROOT, "data", "tables", "QuestFlags.json")
        _QUEST_FLAGS = {f["Id"] for f in json.load(open(path, encoding="utf-8"))} if os.path.exists(path) else set()
    if name in _QUEST_FLAGS or "minus" not in name:
        return name
    dashed = name.replace("minus", "-")
    if dashed in _QUEST_FLAGS:
        return dashed
    # Only some of the "minus" are dashes (e.g. a3q10minusKilledDominus): try each one alone.
    parts = name.split("minus")
    for i in range(1, len(parts)):
        candidate = "minus".join(parts[:i]) + "-" + "minus".join(parts[i:])
        if candidate in _QUEST_FLAGS:
            return candidate
    return dashed


def parse(text):
    """-> {extends: [...], abstract, sections: [names], client: [names], server: [names]}"""
    toks = TOKEN.findall(re.sub(r"//[^\n]*", "", text))
    r = {"extends": [], "abstract": False, "sections": [], "client": [], "server": [], "version": None,
         "shared_states": [], "shared_timers": [], "transitionable": {}, "tags": [], "basic_actions": [],
         "client_transitionable": {}, "client_targetable": None, "life": {}, "positioned": {}, "animated": {}, "quest": {}}
    i = 0

    def block(i):  # i at '{' -> index after matching '}', plus the names of sections directly inside
        assert toks[i] == "{", toks[i - 3:i + 3]
        depth, names, j = 1, [], i + 1
        while depth:
            t = toks[j]
            if t == "{":
                if depth == 1:
                    names.append(toks[j - 1])
                depth += 1
            elif t == "}":
                depth -= 1
            j += 1
        return j, names

    while i < len(toks):
        t = toks[i]
        if t == "version":
            r["version"] = toks[i + 1]; i += 2
        elif t == "abstract":
            r["abstract"] = True; i += 1
        elif t == "extends":
            r["extends"].append(toks[i + 1].strip("\"'")); i += 2
        elif i + 1 < len(toks) and toks[i + 1] == "{":
            j, inner = block(i + 1)
            if t in ("client", "server"):
                r[t] += inner
                if t == "client":
                    client_sections(toks[i + 2:j - 1], r)
            elif t != "GEAL":
                r["sections"].append(t)
                if t == "StateMachine":
                    state_machine(toks[i + 2:j - 1], r)
                elif t == "Transitionable":
                    top_level_keys(toks[i + 2:j - 1], r["transitionable"], ("num_states", "no_serialise"))
                elif t == "Life":
                    keys = {}
                    top_level_keys(toks[i + 2:j - 1], keys, ("corpse_usable", "prevent_corpse_sinking"))
                    # Unknown literals must not become true through Python's truthiness.
                    r["life"].update({k: {"true": True, "false": False}.get(v.lower()) for k, v in keys.items()})
                elif t == "Positioned":
                    top_level_keys(toks[i + 2:j - 1], r["positioned"], ("object_size", "blocking", "static", "block_league_flag"))
                elif t == "Animated":
                    top_level_keys(toks[i + 2:j - 1], r["animated"], ("animated_object",))
                elif t == "Actor":  # basic actions (ActionTypes Ids): basic_action = "Emerge"
                    body = toks[i + 2:j - 1]
                    r["basic_actions"] += [body[k + 2].strip("\"'") for k in range(len(body) - 2)
                                           if body[k] == "basic_action" and body[k + 1] == "="]
                elif t == "Quest":  # a quest item's flags (12-quests.md §5): "minus" in a name stands for "-"
                    keys = {}
                    top_level_keys(toks[i + 2:j - 1], keys, ("grant_flag", "extra_flag", "remove_flag", "use_flag"))
                    r["quest"].update({k: quest_flag(v) for k, v in keys.items()})
                elif t == "Base":  # item tags (Tags.dat Ids) for mod spawn weights: tag = "wand"
                    body = toks[i + 2:j - 1]
                    r["tags"] += [body[k + 2].strip("\"'") for k in range(len(body) - 2) if body[k] == "tag" and body[k + 1] == "="]
            i = j
        else:
            i += 1
    return r


def state_machine(toks, r):
    """Networked StateMachine data (0x141e3ee40 reads it): `define_shared_state = "a; b = 2;"` -> [(a, 0), (b, 2)],
    `define_shared_timer = "t;"` -> [t]. Plain define_state / define_timer stay on the client. Only top-level
    tokens of the section: nested blocks (event handlers) are skipped."""
    depth = 0
    for k, t in enumerate(toks):
        if t == "{": depth += 1
        elif t == "}": depth -= 1
        elif depth == 0 and t in ("define_shared_state", "define_shared_timer") and k + 2 < len(toks) and toks[k + 1] == "=":
            for item in toks[k + 2].strip("\"'").split(";"):
                name, _, value = item.partition("=")
                name = name.strip()
                if not name:
                    continue
                if t == "define_shared_timer":
                    r["shared_timers"].append(name)
                else:
                    try:
                        v = int(value.strip() or 0)
                    except ValueError:
                        v = 0
                    r["shared_states"].append([name, v])


def client_sections(toks, r):
    """The client block's Transitionable events (create_untransitioned, create_transitioned, on_transition: their script
    as one line of tokens) and Targetable's `targetable` key. Emerge monsters pause their emerge animation in
    create_untransitioned and are untargetable until on_transition (Gateway MonsterEmerge.cs)."""
    k = 0
    while k + 1 < len(toks):
        if toks[k] in ("Transitionable", "Targetable") and toks[k + 1] == "{":
            depth, j = 1, k + 2
            while j < len(toks) and depth:
                depth += {"{": 1, "}": -1}.get(toks[j], 0)
                j += 1
            body = toks[k + 2:j - 1]
            if toks[k] == "Targetable":
                keys = {}
                top_level_keys(body, keys, ("targetable",))
                if "targetable" in keys:
                    r["client_targetable"] = keys["targetable"].lower() != "false"
            else:
                for e in range(len(body) - 2):
                    if body[e] in ("create_untransitioned", "create_transitioned", "on_transition") and body[e + 1] == "="                             and body[e + 2] == "{":
                        depth, f = 1, e + 3
                        while f < len(body) and depth:
                            depth += {"{": 1, "}": -1}.get(body[f], 0)
                            f += 1
                        r["client_transitionable"][body[e]] = " ".join(body[e + 3:f - 1])
            k = j
        else:
            k += 1


def top_level_keys(toks, out, keys):
    """`key = value` pairs at the top level of a section (nested event blocks skipped)."""
    depth = 0
    for k, t in enumerate(toks):
        if t == "{": depth += 1
        elif t == "}": depth -= 1
        elif depth == 0 and t in keys and k + 2 < len(toks) and toks[k + 1] == "=":
            out[t] = toks[k + 2].strip("\"'")


def chain(type_path, files, seen=None):
    """Files of a type in load order (parents first)."""
    seen = seen or set()
    if type_path in seen or type_path == "nothing":
        return []
    seen.add(type_path)
    f = files.get(type_path)
    if f is None:
        return [(type_path, None)]
    out = []
    for p in f["extends"]:
        out += chain(p, files, seen)
    return out + [(type_path, f)]


def merge(type_path, files, otc, first=("Positioned",)):
    ch = chain(type_path, files)
    comps = list(first)
    for _, f in ch:
        for s in (f or {}).get("sections", []):
            if s not in comps:
                comps.append(s)
    read = len(comps)
    for p, f in ch:
        for s in (f or {}).get("client", []) + otc.get(p, []):
            if s not in comps:
                comps.append(s)
    # Shared states/timers in definition order, parents first; a redefinition keeps the first position and takes the
    # later default.
    states, timers = {}, []
    for _, f in ch:
        for name, v in (f or {}).get("shared_states", []):
            states[name] = v
        for name in (f or {}).get("shared_timers", []):
            if name not in timers:
                timers.append(name)
    # Transitionable (type +0xb0 = num_states; states run 1..num_states, num_states = fully transitioned): later files
    # in the chain override. Without the key the type's constructor (0x141e42d50) sets 2, and the key itself only takes
    # 2..127 (0x141e42a80), so 1 below means "not given": state 1 is then the untransitioned state, 2 the transitioned.
    trans = {}
    for _, f in ch:
        trans.update((f or {}).get("transitionable", {}))
    try:
        num_states = int(trans.get("num_states", 1))
    except ValueError:
        num_states = 1
    result = {"type": type_path, "chain": [p for p, _ in ch], "missing": [p for p, f in ch if f is None],
            "abstract": bool(files.get(type_path, {}).get("abstract")), "components": comps, "read": read,
            "positioned": {k: v for _, f in ch for k, v in (f or {}).get("positioned", {}).items()},
            "animated_object": next((f["animated"]["animated_object"] for _, f in reversed(ch)
                                     if f and "animated_object" in f.get("animated", {})), None),
            "shared_states": [[k, v] for k, v in states.items()], "shared_timers": timers,
            # The Actor's basic actions (ClientActor ctor 0x142192860 adds one skill per name, id = HASH16 & 0x3fff),
            # parents first: e.g. Monster.ot's Move, and "Emerge" for monsters that rise from the ground.
            "basic_actions": list(dict.fromkeys(a for _, f in ch for a in (f or {}).get("basic_actions", []))),
            # The client scripts of Transitionable (later files override each event) and Targetable's `targetable`
            # (null: not set, the component's default: targetable).
            "client_transitionable": {k: v for _, f in ch for k, v in (f or {}).get("client_transitionable", {}).items()},
            "client_targetable": next((f["client_targetable"] for _, f in reversed(ch)
                                       if f and f.get("client_targetable") is not None), None),
            "transitionable": {"num_states": num_states,
                               "no_serialise": str(trans.get("no_serialise", "false")).lower() == "true"}}
    if "Life" in comps[:read]:
        # Life type constructor 0x141e254f0, store 0x141e25645: e4..e7 = 00 00 00 01.
        # A missing parent can override these defaults; only subsequent explicit keys resolve that uncertainty.
        life = {"corpse_usable": True, "prevent_corpse_sinking": False}
        for _, f in ch:
            if f is None:
                life = dict.fromkeys(life)
            else:
                life.update(f.get("life", {}))
        # Life finalizer 0x141e66ec0 sets config+e6 when the type's component map contains Player.
        life["corpse_use_veto"] = True if "Player" in comps else (None if result["missing"] else False)
        result["life"] = life
    return result


def load(types, ext=".ot", companion=".otc"):
    """Parse every type and its extends chain (fetching as needed)."""
    files, otc, frontier = {}, {}, list(types)
    while frontier:
        fetch([p + ext for p in frontier] + ([p + companion for p in frontier] if companion else []))
        nxt = []
        for p in frontier:
            t = read_text(p + ext)
            if t is None:
                continue
            files[p] = parse(t)
            c = read_text(p + companion) if companion else None
            if c is not None:
                pc = parse(c)
                otc[p] = pc["sections"] + pc["client"]
            nxt += [e for e in files[p]["extends"] if e != "nothing" and e not in files]
        frontier = sorted(set(nxt))
    return files, otc


# Quest tile objects: types that tiles (.tdt/.tmd records) place, which no table names and the GGPK index lists only in
# lower case (object type hashes need the real case). From the server's own generation of every act 1-10 area: quest
# objects, quest chests and the quest doors/levers/spawners the Gateway creates itself (Gateway QuestObjectTypes,
# AreaObjects.TileObjects). A quest whose tile object is missing here adds it. Room objects need no
# entry when data/room-points.json keeps their room (QUEST_ROOM_TYPES below otherwise).
QUEST_TILE_TYPES = [
    "Metadata/Chests/QuestChests/ChurchAltar",
    "Metadata/Chests/QuestChests/Fairgraves/FruitTree",
    "Metadata/Chests/QuestChests/Ossuary/PurityBox",
    "Metadata/Monsters/IncaShadowBoss/IncaBossSpawner",
    "Metadata/QuestObjects/Act10/Square/CanalHornBlockage",
    "Metadata/QuestObjects/Act10/Square/CanalHornTransition",
    "Metadata/QuestObjects/Act4/AscentTransition",
    "Metadata/QuestObjects/Act4/CoreMouth",
    "Metadata/QuestObjects/Act4/CoreTransition",
    "Metadata/QuestObjects/Act4/MalachaiGroundHole",
    "Metadata/QuestObjects/Act4/MineEntranceTransition",
    "Metadata/QuestObjects/Act4/RaptureTransition",
    "Metadata/QuestObjects/Act5/BloodWingBlockingDoor",
    "Metadata/QuestObjects/Act5/CathedralKitavaLadder",
    "Metadata/QuestObjects/Act5/ControlBlocksToDestroyedSquare",
    "Metadata/QuestObjects/Act5/ControlBlocksToSquare",
    "Metadata/QuestObjects/Act5/InnocenceShortcutDoor",
    "Metadata/QuestObjects/Act5/InnocenceToCourts",
    "Metadata/QuestObjects/Act5/InnocenceToDestroyedCourts",
    "Metadata/QuestObjects/Act5/OriathSquareToCourts",
    "Metadata/QuestObjects/Act5/TemplarCourtsDoor",
    "Metadata/QuestObjects/Act6/BeaconFuelTank",
    "Metadata/QuestObjects/Act6/BlackCrest_BeaconFlame",
    "Metadata/QuestObjects/Act6/BlackCrest_BeaconInteract",
    "Metadata/QuestObjects/Act6/FortressKaruiDoor",
    "Metadata/QuestObjects/Act6/MudFlatsKaruiDoor",
    "Metadata/QuestObjects/Act6/MudFlatsKaruiTransition",
    "Metadata/QuestObjects/Act6/ToRidgeTransition",
    "Metadata/QuestObjects/Act7/GreustShrine",
    "Metadata/QuestObjects/Act7/MaligaroOrrery",
    "Metadata/QuestObjects/Act7/MaligaroOrrery_Main",
    "Metadata/QuestObjects/Act7/MaligaroPassage",
    "Metadata/QuestObjects/Act7/MaligaroPassageCover",
    "Metadata/QuestObjects/Act7/TempleOfDecayEntrance",
    "Metadata/QuestObjects/Act8/GoddessStatueBreak",
    "Metadata/QuestObjects/Act9/BeastMembraneAnimated",
    "Metadata/QuestObjects/Act9/BeastMembraneTransition",
    "Metadata/QuestObjects/Act9/Refinery_Boss_Door",
    "Metadata/QuestObjects/BossArenaEntranceTransition",
    "Metadata/QuestObjects/BridgeBarrier",
    "Metadata/QuestObjects/EpicDoor/EpicDoor",
    "Metadata/QuestObjects/EpicDoor/EpicDoorLock",
    "Metadata/QuestObjects/EpicDoor/EpicDoorTransition",
    "Metadata/QuestObjects/Inca/IncaDarknessRelease",
    "Metadata/QuestObjects/Inca/IncaPyramidDoor",
    "Metadata/QuestObjects/Inca/IncaPyramidOutsideDust",
    "Metadata/QuestObjects/Inca/IncaPyramidTransition",
    "Metadata/QuestObjects/Inca/IncaTreeTransition",
    "Metadata/QuestObjects/Inca/PoisonTree",
    "Metadata/QuestObjects/Library/HiddenDoor",
    "Metadata/QuestObjects/Library/HiddenDoorGeneric",
    "Metadata/QuestObjects/Library/HiddenDoorTransition",
    "Metadata/QuestObjects/Library/HiddenDoorTrigger",
    "Metadata/QuestObjects/Library/HiddenDoorTriggerGeneric",
    "Metadata/QuestObjects/MapRoom/OrreryInert",
    "Metadata/QuestObjects/MerveilExit",
    "Metadata/QuestObjects/MerveilExitTransition",
    "Metadata/QuestObjects/Sewers/SewersGrate",
    "Metadata/QuestObjects/Sewers/SewersGrateTransition",
    "Metadata/QuestObjects/Sewers/SewersHiddenDoorBase",
    "Metadata/QuestObjects/Sewers/SewersHiddenDoor_Baths",
    "Metadata/QuestObjects/Sewers/SewersHiddenDoor_Battlefront",
    "Metadata/QuestObjects/Sewers/SewersHiddenDoor_Warehouse",
    "Metadata/QuestObjects/Sewers/SewersLockedDoor",
    "Metadata/QuestObjects/Sewers/SewersLockedTransition",
    "Metadata/QuestObjects/Spikes/BlockedPassageTransition",
    "Metadata/QuestObjects/Spikes/SpikeBlockageButton",
    "Metadata/QuestObjects/WaterCave/GlyphWall",
    "Metadata/QuestObjects/WaterCave/WaterCaveAreaTransition",
    "Metadata/Terrain/Act11/Area2/Reliquary/Objects/VaultDoor",
    "Metadata/Terrain/Act5/Area2/Objects/SlavePenSecurityDoor",
    "Metadata/Terrain/Act5/Area6/Objects/Ossuary_HiddenDoor",
    "Metadata/Terrain/Act5/Area7/Objects/LeverHidden",
    "Metadata/Terrain/Act5/Area7/Objects/LeverMissingLeft",
    "Metadata/Terrain/Act5/Area7/Objects/LeverMissingRight",
    "Metadata/Terrain/Act5/Area7/Objects/VaultDoor",
    "Metadata/Terrain/Act5/Area8/Objects/KitavaDoor",
    "Metadata/Terrain/Act8/Area1/Objects/HiddenDoor",
    "Metadata/Terrain/Mountain/Arena/Colosseum/Objects/ColosseumDoubleDoors",
    "Metadata/Terrain/Mountain/Arena/Colosseum/Objects/ColosseumDoubleDoorsDaresso",
    "Metadata/Terrain/Mountain/Arena/Colosseum/Objects/ColosseumDoubleDoorsSpawner",
    "Metadata/Terrain/Mountain/Arena/Colosseum/Objects/ColosseumDoubleDoorsSpawnerLeft",
    "Metadata/Terrain/Mountain/Arena/Colosseum/Objects/ColosseumDoubleDoorsSpawnerRight",
    "Metadata/Terrain/Mountain/Arena/Colosseum/Objects/ColosseumGladiatorTrapdoor",
    "Metadata/Terrain/Mountain/Arena/Colosseum/Objects/ColosseumTrapDoorSpawnerLeft",
    "Metadata/Terrain/Mountain/Arena/Colosseum/Objects/ColosseumTrapDoorSpawnerRight",
    "Metadata/Terrain/Mountain/Arena/Colosseum/Objects/ColosseumTrapDoorSpawnerSand",
    "Metadata/Terrain/Mountain/Arena/Pit/Objects/PitGate",
    "Metadata/Terrain/Mountain/Arena/Pit/Objects/PitGateTransition",
    "Metadata/Terrain/Mountain/Arena/Pit/Objects/PitSpawner",
    "Metadata/Terrain/Mountain/Arena/Pit/Objects/PitSpawnerGate",
    "Metadata/Terrain/Mountain/Belly/Objects/BellyArenaDoor",
    "Metadata/Terrain/Mountain/Belly/Objects/BellyArenaTransition",
    "Metadata/Terrain/Mountain/Belly/Objects/BellyArseDoor",
    "Metadata/Terrain/Mountain/Belly/Objects/BellyWallSpawner",
    "Metadata/Terrain/Mountain/Mines/Objects/BeastSkinDoor",
    "Metadata/Terrain/Mountain/Mines/Objects/BeastSkinDoorExplosion",
    "Metadata/Terrain/Mountain/Mines/Objects/BeastSkinDoorFX",
    "Metadata/Terrain/Theopolis/SlaveDens/Objects/SlavePensDoubleDoors",
    "Metadata/Terrain/Theopolis/SlaveDens/Objects/SlavePensDoubleDoorsSpawner1",
    "Metadata/Terrain/Theopolis/SlaveDens/Objects/SlavePensDoubleDoorsSpawner2",
    "Metadata/Terrain/Theopolis/SlaveDens/Objects/SlavePensDoubleDoorsSpawner3",
]

# Quest room objects of rooms data/room-points.json does not keep (they hold no monster point and no QuestObjects/ type, so
# their objects come from the server's own placed rooms, AreaObjects.RoomObjects): The Ascent's Resonator
# (ascent_summit_01.arm, a5q1a).
QUEST_ROOM_TYPES = [
    "Metadata/Terrain/Act4/Area7/Objects/PortalDevice",
    "Metadata/Terrain/Act4/Area7/Objects/PortalDeviceLever",
]


def all_types():
    paths = set()

    def walk(x):
        if isinstance(x, str):
            if x.startswith("Metadata/") and "." not in x.rsplit("/", 1)[-1] and not x.startswith("Metadata/Items/"):
                paths.add(x)
        elif isinstance(x, list):
            for y in x: walk(y)
        elif isinstance(x, dict):
            for y in x.values(): walk(y)

    tables = os.path.join(ROOT, "data", "tables")
    for f in sorted(os.listdir(tables)):
        if f.endswith(".json"):
            walk(json.load(open(os.path.join(tables, f), encoding="utf-8")))
    atlas = os.path.join(ROOT, "data", "area-transitions.json")
    if os.path.exists(atlas):
        for area in json.load(open(atlas, encoding="utf-8")).get("areas", []):
            for topology in area.get("topologies") or []:
                for t in topology.get("transitions") or []:
                    if t.get("objectType"): paths.add(t["objectType"])
    # The rooms' server objects (NPCs, quest objects and chests: scripts/re/atlas room-points) and the quest tile objects.
    points = os.path.join(ROOT, "data", "room-points.json")
    if os.path.exists(points):
        walk(json.load(open(points, encoding="utf-8")).get("rooms"))
    paths.update(QUEST_TILE_TYPES)
    paths.update(QUEST_ROOM_TYPES)
    fetch(RAW_DATS)
    for d in RAW_DATS:
        b = open(cache_name(d), "rb").read()
        for m in re.finditer(rb"M\0e\0t\0a\0d\0a\0t\0a\0/\0(?:[\x20-\x7e]\0)+?\0\0", b):
            s = m.group(0)[:-2].decode("utf-16-le")
            if "." not in s.rsplit("/", 1)[-1] and not s.startswith("Metadata/Items/"):
                paths.add(s)
    return sorted(paths)


def item_types():
    """BaseItemTypes rows -> {Id: InheritsFrom}. Item types are not object types: the client's
    Metadata/ItemTypeRegister files each BaseItemTypes row under its HASH32 (MurmurHash2 of the Id, the same hash as
    object types; FUN_1421ec390 looks it up) and builds it from the .it chain. Most leaf rows have no .it file of their
    own: their chain is InheritsFrom's."""
    rows = json.load(open(os.path.join(ROOT, "data", "tables", "BaseItemTypes.json"), encoding="utf-8"))
    return {r["Id"]: r.get("InheritsFrom") or "nothing" for r in rows}


def merge_items():
    """Component lists of every BaseItemTypes row from the .it files. Unlike object types nothing comes first (the item
    reader FUN_1421ec390 calls slot 5 (+0x28) on the type's first type+0x80 components; no Positioned for items), and
    the "client" blocks (RenderItem) are never read. Verified live for Wand1, the flasks and the glyphs (05 §6)."""
    rows = item_types()
    files, _ = load(sorted(set(rows) | {v for v in rows.values() if v != "nothing"}), ext=".it", companion=None)
    out = {}
    for tid, parent in rows.items():
        if tid in files:
            r = merge(tid, files, {}, first=())
        elif parent in files:
            r = merge(parent, files, {}, first=())
            r["chain"].append(tid)
            r["abstract"] = False
        else:
            continue
        r["type"] = tid
        # Tags along the chain, parents first (the BaseItemTypes row adds its own TagsKeys on top).
        r["tags"] = list(dict.fromkeys(tag for p in r["chain"] for tag in files.get(p, {}).get("tags", [])))
        # The Quest component's flags along the chain (later files override): grant_flag while carried, extra_flag once
        # picked up, remove_flag takes the item away, use_flag set by using it (the quest books).
        quest = {k: v for p in r["chain"] for k, v in files.get(p, {}).get("quest", {}).items()}
        if quest:
            r["quest"] = quest
        for k in ("shared_states", "shared_timers", "transitionable", "positioned", "animated_object", "basic_actions",
                  "client_transitionable", "client_targetable"):
            r.pop(k, None)
        out[tid] = r
    return out


def registry():
    if not os.path.exists(REGISTRY):
        return {}, {}
    regs = json.load(open(REGISTRY))["registrations"]
    by_name, by_vt = {}, {}
    for r in regs:
        if r["typeId"] < 0 and not r["name"].startswith("Client"):
            continue
        by_name.setdefault(r["name"], r)
        for v in r["vtables"]:
            by_vt.setdefault(v["vt"], set()).add(r["name"])
    return by_name, by_vt


def check(results):
    _, by_vt = registry()
    ok = True
    for t, vts in GROUND_TRUTH.items():
        got = results[t]
        # A live vtable can belong to the component's own registration (factory or subclass) and to its client
        # class registration ("ClientPlayer" for Player): the client instantiates that subclass.
        owners = [sorted(by_vt.get(v, {"?" + v})) for v in vts]
        live = [c if c in o or "Client" + c in o else "/".join(o) for c, o in zip(got["components"] + [""] * 99, owners)]
        same = got["components"] == live and got["read"] == GROUND_TRUTH_READ[t]
        ok &= same
        print(f"{'MATCH' if same else 'MISMATCH'} {t}")
        print(f"  generated: {got['components']} (read loop: first {got['read']})")
        print(f"  live:      {live} (read loop: first {GROUND_TRUTH_READ[t]})")
        print(f"  live vtables: {', '.join(v + '=' + '/'.join(o) for v, o in zip(vts, owners))}")
    return ok


def md(results, out, types_shown):
    by_name, _ = registry()

    def deser(c):
        r = by_name.get(c)
        if not r:
            return "(no registration)"
        ks = {}  # deserializer -> vtables using it
        live = {v for vts in GROUND_TRUTH.values() for v in vts}
        for v in r["vtables"]:
            if v.get("primary") and "slot17" in v and v.get("slot17Kind") != "noinsn":
                k = f"`{v['slot17']}`" + (" (ret)" if v["slot17Kind"] == "ret" else f" → slot 20 `{v.get('slot20', '?')}`"
                                          if v["slot17Kind"] == "fwd20" else "")
                ks.setdefault(k, []).append(v["vt"] + (" ★" if v["vt"] in live else ""))
        return "; ".join(f"{k} (vt {', '.join(vs)})" for k, vs in ks.items())

    L = ["# Object types: merged component lists", "",
         "Generated by `python -I scripts/re/ot_components.py --md Agents/docs/re/object-types.md` from the client's own",
         ".ot/.otc files (GGPK). Do not edit by hand. The merge rule and its sources are in the script header; it is",
         "checked against the two objects traced live (`--check`): **the player (Int) and AreaTransition match**.",
         "", "Components marked ✱ are deserialized from the 0x2b9 blob (the first `type+0x80`), in this order. The",
         "rest are client-side. Deserializers are the slot 17 functions of the component's vtables from",
         "[components.md](components.md) (several when the factory or a subclass installs more than one vtable; the",
         "live one is not always the first; ★ = seen live). Read sequences: [../protocol/schema.md](../protocol/schema.md).",
         "", "Print any type: `python -I scripts/re/ot_components.py Metadata/Monsters/...`.", ""]
    for t in types_shown:
        r = results.get(t)
        if not r:
            continue
        L.append(f"## `{t}`" + (" (abstract)" if r["abstract"] else ""))
        L.append("")
        L.append("Chain: " + " → ".join(f"`{p}`" for p in r["chain"]) +
                 (f" (missing: {', '.join(r['missing'])})" if r["missing"] else ""))
        L.append("")
        L.append("| # | Component | Read | Deserializer(s) |")
        L.append("|---|---|---|---|")
        for i, c in enumerate(r["components"]):
            L.append(f"| {i} | {c} | {'✱' if i < r['read'] else ''} | {deser(c) if i < r['read'] else ''} |")
        L.append("")
    if len(results) > len(types_shown) + 10:
        groups = {}
        for t, r in results.items():
            if not r["abstract"]:
                groups.setdefault((tuple(r["components"][:r["read"]]), tuple(r["components"][r["read"]:])), []).append(t)
        L += ["## Every type, grouped by component list", "",
              f"{sum(len(v) for v in groups.values())} instantiable types (with an .ot file) from the Metadata paths in the",
              f"dat tables, {len(groups)} distinct lists. Deserialized components in order; client components after them.",
              "", "| Types | Deserialized components (in order) | Client | Examples |", "|---|---|---|---|"]
        for (rd, cl), ts in sorted(groups.items(), key=lambda kv: (-len(kv[1]), kv[0])):
            ex = ", ".join(f"`{t}`" for t in sorted(ts)[:3]) + (" …" if len(ts) > 3 else "")
            L.append(f"| {len(ts)} | {', '.join(rd)} | {', '.join(cl)} | {ex} |")
        L.append("")
    open(out, "w", encoding="utf-8", newline="\n").write("\n".join(L))


DOC_TYPES = ["Metadata/Characters/Int/Int", "Metadata/Characters/Str/Str", "Metadata/Characters/Dex/Dex",
             "Metadata/Characters/StrDex/StrDex", "Metadata/Characters/DexInt/DexInt",
             "Metadata/Characters/StrInt/StrInt", "Metadata/Characters/StrDexInt/StrDexInt",
             "Metadata/MiscellaneousObjects/AreaTransition", "Metadata/MiscellaneousObjects/Waypoint",
             "Metadata/MiscellaneousObjects/Stash", "Metadata/MiscellaneousObjects/Checkpoint",
             "Metadata/MiscellaneousObjects/WorldItem", "Metadata/MiscellaneousObjects/MultiplexPortal",
             "Metadata/MiscellaneousObjects/PortalToggleable", "Metadata/NPC/NPC", "Metadata/NPC/Act1/Nessa",
             "Metadata/NPC/Act1/Tarkleigh", "Metadata/NPC/Act1/Bestel", "Metadata/Monsters/Monster",
             "Metadata/Monsters/Zombies/Zombie", "Metadata/Monsters/Zombies/Drowned/BiteZombie", "Metadata/Chests/Chest",
             "Metadata/Projectiles/Projectile", "Metadata/Effects/Effect"]


def main():
    a = sys.argv[1:]
    flag = lambda f: f in a and (a.remove(f) or True)

    def popopt(f):
        if f in a:
            i = a.index(f); v = a[i + 1]; del a[i:i + 2]; return v
        return None

    do_check, do_all, do_items = flag("--check"), flag("--all"), flag("--items")
    md_out, json_out = popopt("--md"), popopt("--json")
    if do_items:
        results = merge_items()
        for t in a:
            r = results.get(t)
            print(f"{t}: no .it chain" if not r else f"{t}  chain={' > '.join(r['chain'])}\n  " + ", ".join(r["components"][:r["read"]]))
        if json_out:
            json.dump({"generator": "scripts/re/ot_components.py --items", "types": [results[t] for t in sorted(results)]},
                      open(json_out, "w"), indent=0)
            print(f"{len(results)} item types -> {json_out}", file=sys.stderr)
        return
    types = list(a)
    if do_check or md_out:
        types += list(GROUND_TRUTH) + DOC_TYPES
    if do_all:
        types += all_types()
    types = sorted(set(types))
    files, otc = load(types)
    results = {t: merge(t, files, otc) for t in types if t in files}
    for t in a:
        r = results.get(t)
        if not r:
            print(f"{t}: no .ot"); continue
        print(f"{t}  chain={' > '.join(r['chain'])}")
        for i, c in enumerate(r["components"]):
            print(f"  {i:2} {'*' if i < r['read'] else ' '} {c}")
    if md_out:
        md(results, md_out, [t for t in DOC_TYPES if t in results])
    if json_out:
        json.dump({"generator": "scripts/re/ot_components.py", "types": [results[t] for t in sorted(results)]},
                  open(json_out, "w"), indent=0)
        print(f"{len(results)} types with an .ot -> {json_out}", file=sys.stderr)
    if do_check and not check(results):
        sys.exit(1)


if __name__ == "__main__":
    main()
