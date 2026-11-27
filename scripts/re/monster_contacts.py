# usage: python -I scripts/re/monster_contacts.py [--json data/monster-contacts.json] [--stances data/monster-stances.json] [--cached-only]
#
# For every MonsterVarieties row: the clips its .act maps to every Animation.dat key (including Melee, Throw,
# MonsterMortar / MonsterProjectileSpell / MonsterProjectileAttack / GrantedEffects.Animation / EmptyAction* parents),
# in ACT order, each with its index in the rig's animation list, its length, its "contact" keyframe (time in ms and
# position in model space: +x left, -y forward, -z up), optional ACT budget/weight, and ALL merged named clip events
# (.amd + .ao AnimationController StandardEventType / MoveEventType / StartFaceEventType / EndFaceEventType / etc.).
#
# v9 (PR #1618 S2, Option B profile normalization):
#   - Accepts grammar-validated trailing ACT modifiers: effect / effect_locked / effect_detached / effect_flipped
#     (repeatable, with optional "&"), item_animation MainHand|OffHand [using "<clip>"], with_main_hand / with_off_hand,
#     subanim / subanim_continue, append_anim, timeline.
#   - Resolves root-block "@Alias" references (cycle-safe, depth < 8) and optional per-clip ":attack_time=" / ":weight=".
#   - Resolves .ao animation views (view_of / view_name / view_begin / view_end / view_exclude_event = "StandardEvent")
#     along the .ao extends chain and attaches view-local AnimationController events (e.g. HarvestSandspitterT1).
#   - Emits all 179 Animation.dat keys and all named clip events, deduplicated by unique (act, ao, amd) rig profile
#     (5.5x smaller than per-variety duplication; preserves legacy contact positions and separate melee_clocks).
#
# Reads from Tools/re/out/act-ot-cache/ first when present (monster_cache.py), falling back to ot_components' GGPK
# cache (Tools/re/out/ot) and scripts/re/ggdump. --cached-only fetches nothing.
import json
import math
import os
import re
import sys

sys.path.append(os.path.dirname(os.path.abspath(__file__)))
from ot_components import REPO, _block, _position, all_contacts, amd_animations, amd_map, extends_of, fetch, first_contact, rig_clips  # noqa: E402
from ot_components import read_text as _ot_read_text  # noqa: E402
from anim_contacts import act_tokens, closing_brace  # noqa: E402
import clientdb  # noqa: E402
import monster_cache  # noqa: E402

NAME = re.compile(r"[A-Za-z_][A-Za-z_0-9]*")
EFFECT_KWS = {"effect", "effect_locked", "effect_detached", "effect_flipped"}
SUBANIM_KWS = {"subanim", "subanim_continue"}
HAND_FLAGS = {"with_main_hand", "with_off_hand"}
ITEM_HANDS = {"MainHand", "OffHand"}
VIEW_BLOCK_RE = re.compile(
    r'view_of\s*=\s*"([^"]+)"\s*\n\s*view_name\s*=\s*"([^"]+)"'
    r'((?:\s*\n\s*view_(?:begin|end|exclude_event|speed|loop)\s*=\s*(?:"[^"]*"|\d+(?:\.\d+)?))*)',
)


def read_text(path):
    """Read an asset from Tools/re/out/act-ot-cache/ if present, else ot_components' cache."""
    text = monster_cache.read_text(path)
    if text is not None:
        return text
    return _ot_read_text(path)


def table(name):
    return clientdb.table(name)


def animation_keys():
    """Every Animation.dat Id (plus Melee and Throw), sorted."""
    animations = table("Animation")
    keys = {"Melee", "Throw"}
    for row in animations:
        if isinstance(row, dict) and row.get("Id"):
            keys.add(row["Id"])
    return sorted(keys)


def valid_trailing_modifiers(tokens):
    """Validate that all tokens after the clip string in an ACT assignment are known non-clip-altering modifiers."""
    i, n = 0, len(tokens)
    while i < n:
        t = tokens[i]
        if t == "&":
            i += 1
        elif t in HAND_FLAGS:
            i += 1
        elif t in EFFECT_KWS:
            i += 1
            if i >= n or not tokens[i].startswith('"'):
                return False
            while i < n and tokens[i].startswith('"'):
                i += 1
        elif t == "item_animation":
            i += 1
            if i >= n or tokens[i] not in ITEM_HANDS:
                return False
            i += 1
            if i < n and tokens[i] == "using":
                i += 1
                if i >= n or not tokens[i].startswith('"'):
                    return False
                i += 1
        elif t in SUBANIM_KWS:
            i += 1
            if i + 1 >= n or not tokens[i].startswith('"') or not tokens[i + 1].startswith('"'):
                return False
            i += 2
        elif t in ("append_anim", "timeline"):
            i += 1
            if i >= n or not tokens[i].startswith('"'):
                return False
            i += 1
        else:
            return False
    return True


def fetch_ao_chain(paths, fetch_missing=True):
    """Fetch these .ao files and every "extends" parent; return all .ao paths seen."""
    known, todo = set(), set(paths)
    while todo:
        missing = [p for p in sorted(todo) if read_text(p) is None]
        if fetch_missing and missing:
            fetch(missing)
        known |= todo
        nxt = set()
        for ao in todo:
            text = read_text(ao)
            if text is None:
                continue
            for parent in extends_of(text):
                p = parent if parent.endswith(".ao") else parent + ".ao"
                if p not in known:
                    nxt.add(p)
        todo = nxt
    return known


def root_assignments(text):
    """Root-block `Key = "<clips>" [modifiers...];` entries (first wins), with cycle-safe `@Alias` resolution."""
    tokens = act_tokens(text)
    if "{" not in tokens:
        return {}
    try:
        start = tokens.index("{")
        body = tokens[start + 1:closing_brace(tokens, start)]
    except ValueError:
        return {}
    raw, i = {}, 0
    while i < len(body):
        if body[i] == "{":
            i = closing_brace(body, i) + 1
        elif i + 2 < len(body) and body[i + 1] == "=":
            end = i + 2
            while end < len(body) and body[end] != ";":
                end += 1
            if end < len(body):
                expr = body[i + 2:end]
                if expr and expr[0].startswith('"') and valid_trailing_modifiers(expr[1:]):
                    raw.setdefault(body[i], expr[0][1:-1].strip())
            i = end + 1
        else:
            i += 1

    resolved = {}

    def resolve(key, depth=0):
        if key in resolved:
            return resolved[key]
        val = raw.get(key)
        if not val or val == "delete":
            return None
        if val.startswith("@"):
            if depth >= 8:
                return None
            return resolve(val[1:], depth + 1)
        return val

    for key in raw:
        val = resolve(key)
        if val is not None:
            resolved[key] = val
    return resolved


def act_blocks(text):
    """{stance: [own entries of the root, ..., of the stance]} for every stance of the .act ("" for the root)."""
    tokens = act_tokens(text)
    if "{" not in tokens:
        return {}
    start = tokens.index("{")
    result = {}

    def walk(name, body, chain):
        own, children, i = {}, [], 0
        while i < len(body):
            if body[i] == "stance" and i + 2 < len(body) and body[i + 2] == "{":
                end = closing_brace(body, i + 2)
                children.append((body[i + 1], body[i + 3:end]))
                i = end + 1
            elif body[i] == "{":
                i = closing_brace(body, i) + 1
            elif i + 2 < len(body) and body[i + 1] == "=":
                end = i + 2
                while end < len(body) and body[end] != ";":
                    end += 1
                if end < len(body):
                    expr = body[i + 2:end]
                    if expr and expr[0].startswith('"') and valid_trailing_modifiers(expr[1:]):
                        own.setdefault(body[i], expr)
                i = end + 1
            else:
                i += 1
        result[name] = chain = chain + [own]
        for child, child_body in children:
            walk(child, child_body, chain)

    try:
        walk("", tokens[start + 1:closing_brace(tokens, start)], [])
    except ValueError:
        return {}
    return result


def stance_entry(chain, key, depth=0):
    """The clip list a stance plays for an Animation key: its own entry, else its nearest parent's; "@Other" resolves
    within the stance chain. Accepts valid ACT modifiers and plain clip lists (or :attack_time=/:weight= options)."""
    for own in reversed(chain):
        if key in own:
            expression = own[key]
            if not expression or not expression[0].startswith('"'):
                return None
            value = expression[0][1:-1].strip()
            if value == "delete":
                return None
            if value.startswith("@"):
                return stance_entry(chain, value[1:], depth + 1) if depth < 8 else None
            parsed = parse_clip_list(value, allow_budgets=True, allow_weights=True)
            return [c[0] for c in parsed] if parsed else None
    return None


def stance_pools(text, keys):
    """{pool: [clip, ...]} of an .act with stances: every key at the root, and "<stance>/<key>" where the stance's clips
    differ from the root's; {} for an .act without stances."""
    blocks = act_blocks(text)
    if len(blocks) < 2:
        return {}
    root = {key: stance_entry(blocks[""], key) for key in keys}
    pools = {key: clips for key, clips in root.items() if clips}
    for stance, chain in blocks.items():
        if not stance:
            continue
        for key in keys:
            clips = stance_entry(chain, key)
            if clips and clips != root[key]:
                pools[f"{stance}/{key}"] = clips
    return pools


def parse_clip_list(value, allow_budgets=True, allow_weights=True):
    """Parse a comma-separated clip pool -> [(name, budget_or_None, weight), ...], or None on any invalid entry."""
    if not value or value == "delete":
        return None
    clips = []
    for part in value.split(","):
        pieces = [p.strip() for p in part.split(":")]
        name, options = pieces[0], pieces[1:]
        if not NAME.fullmatch(name):
            return None
        budget = None
        weight = 1
        seen_opts = set()
        for opt in options:
            k, sep, v = opt.partition("=")
            k, v = k.strip(), v.strip()
            if not sep or k in seen_opts:
                return None
            seen_opts.add(k)
            if k == "attack_time" and allow_budgets:
                try:
                    b = float(v)
                except ValueError:
                    return None
                if not math.isfinite(b) or b <= 0:
                    return None
                budget = b
            elif k == "weight" and allow_weights:
                if not v.isdigit() or int(v) <= 0:
                    return None
                weight = int(v)
            else:
                return None
        clips.append((name, budget, weight))
    return clips if clips else None


def act_pools(text, keys, budgets=False, weights=False):
    """{key: [(clip, optional budget seconds), ...]} (or with weight when weights=True) for supported root-block pools,
    and the .ao the act animates. A single unsupported entry rejects the whole pool to preserve selector order."""
    entries = root_assignments(text)
    pools = {}
    for key in keys:
        value = entries.get(key)
        if not value:
            continue
        parsed = parse_clip_list(value, allow_budgets=budgets or key == "Throw", allow_weights=weights)
        if parsed:
            pools[key] = parsed if weights else [(n, b) for n, b, _ in parsed]
    m = re.search(r'^\s*animated_object\s+"([^"]+)"', text, re.M)
    return pools, (m.group(1) if m else None)


# The Animation.dat rows the client's skill action classes request for their main animation (11-skills.md §9).
ACTION_KEYS = [
    "BlastRain", "CastFast", "Charge", "ChargeStart", "Cleave", "CloneShot", "DoubleStrike", "DualStrike", "Flurry",
    "Frenzy", "GlacialHammer", "GroundSlam", "HeavyStrike", "LeapSlam", "Melee", "Puncture", "RainOfArrows",
    "ShadowProjectiles", "Snipe", "SpellAreaOfEffect", "SpellAreaOfEffectChaos", "SpellAreaOfEffectCold",
    "SpellAreaOfEffectFire", "SpellAreaOfEffectLightning", "SpellBuff", "SpellBuffCold", "SpellBuffFast", "SpellBuffFire",
    "SpellProjectile", "SpellProjectileChaos", "SpellProjectileCold", "SpellProjectileFire", "SpellProjectileLightning",
    "Throw", "Warcry",
]
HIT_EVENT = {"LeapSlam": "hit_ground"}


def complete_ao_events(text):
    """Certify only the event data the existing AO reader can retain; a skipped list/event is not an absent span."""
    body = _block(text, "AnimationController")
    if body is None:
        return re.search(r'^\s*AnimationController\b', text, re.M) is None
    assignments = re.findall(r"\banimations\s*=", body)
    lists = re.findall(r"\banimations\s*=\s*'(.*?)'", body, re.S)
    if len(assignments) != len(lists) or len(lists) > 1:
        return False
    for raw in lists:
        try:
            animations = json.loads(raw)
        except ValueError:
            return False
        if not isinstance(animations, list):
            return False
        for animation in animations:
            if not isinstance(animation, dict) or not isinstance(animation.get("name"), str) or not animation["name"]:
                return False
            events = animation.get("events", [])
            if not isinstance(events, list):
                return False
            for event in events:
                if (not isinstance(event, dict) or not isinstance(event.get("name"), str) or not event["name"]
                        or type(event.get("time")) not in (int, float) or not math.isfinite(event["time"] * 1000)):
                    return False
    return True


def complete_ao_chain(ao):
    """All metadata contributing to this AO's events is cached and readable, including inherited event lists."""
    seen, todo = set(), [ao]
    while todo:
        path = todo.pop()
        if path in seen:
            continue
        seen.add(path)
        text = read_text(path)
        if text is None or not complete_ao_events(text):
            return False
        todo.extend(p if p.endswith(".ao") else p + ".ao" for p in extends_of(text))
    return True


def clip_clock(clip, budget, complete=True):
    """Preserve optional clock inputs without treating missing, duplicated or malformed spans as no span."""
    begins = [ms for event, ms, *_ in clip["events"] if event == "slow_begin"]
    ends = [ms for event, ms, *_ in clip["events"] if event == "slow_end"]
    begin = begins[0] if len(begins) == 1 else None
    end = ends[0] if len(ends) == 1 else None
    valid = not begins and not ends or (len(begins) == len(ends) == 1 and 0 <= begin < end <= clip["length"])
    return {"clock": complete and valid, "slow_begin": begin, "slow_end": end, "budget": budget}


def animation_source(acts, aos, act_ao, type_ao=None):
    """A monster's Animated resource comes from its variety, not the ACT's authoring model.
    Only one unambiguous ACT/AO binding supplies a clock; alternative resources and type overrides stay unproven."""
    ao = aos[0] if aos else act_ao
    known = len(acts) == len(aos) == 1 and (type_ao is None or type_ao == ao)
    return ao, known


def parse_amd_from_text(text):
    """Parse an .amd rig's animations from text (same count-checked format as ot_components.amd_animations)."""
    if text is None:
        return None
    lines = [l.strip() for l in text.replace("\r", "").split("\n")]
    out, i = [], 0
    while i < len(lines):
        m = re.fullmatch(r'"([^"]+)"', lines[i])
        if (m and i + 3 < len(lines) and lines[i + 1] in ("once", "loop", "loopnoint")
                and lines[i + 2].isdigit() and lines[i + 3].isdigit()):
            events, j = [], i + 4
            for _ in range(int(lines[i + 3])):
                if j + 2 >= len(lines):
                    break
                events.append((lines[j].strip('"'), lines[j + 1], lines[j + 2]))
                j += 3
            out.append({"name": m.group(1), "once": lines[i + 1] == "once",
                        "length": int(lines[i + 2]), "events": events})
            i = j
        else:
            i += 1
    if len(lines) < 2 or not lines[1].isdigit() or int(lines[1]) != len(out):
        return None
    return out


def parse_ao_chain_events_and_views(ao):
    """Walk .ao extends chain (child first) -> (amd_path, exclude_metadata_events, ao_events_by_clip, views_by_name).

    Retains all named AnimationController events (StandardEventType, MoveEventType, StartFaceEventType,
    EndFaceEventType, MeleeTrailEventType, DeathMoveEventType, InterruptSkillEventType, StartRotateEventType,
    GEALEventType) plus view_of/view_name/view_begin/view_end/view_exclude_event definitions.
    """
    amd = None
    exclude = False
    ao_events = {}
    views = {}
    seen, todo = set(), [ao]
    while todo:
        path = todo.pop(0)
        if not path or path in seen:
            continue
        seen.add(path)
        text = read_text(path)
        if text is None:
            continue
        body = _block(text, "AnimationController")
        if body:
            if amd is None:
                m_amd = re.search(r'\bmetadata\s*=\s*"([^"]+)"', body)
                if m_amd:
                    amd = m_amd.group(1)
            if re.search(r'\bexclude_metadata_events\s*=\s*true\b', body):
                exclude = True
            clean_body = body.replace("\r", "")
            for vm in VIEW_BLOCK_RE.finditer(clean_body):
                src, vname, attrs = vm.groups()
                if vname not in views:
                    b_m = re.search(r'view_begin\s*=\s*(\d+)', attrs)
                    e_m = re.search(r'view_end\s*=\s*(\d+)', attrs)
                    excl_std = bool(re.search(r'view_exclude_event\s*=\s*"StandardEvent"', attrs))
                    views[vname] = (src, int(b_m.group(1)) if b_m else 0,
                                    int(e_m.group(1)) if e_m else None, excl_std)
            for am in re.finditer(r"\banimations\s*=\s*'(.*?)'", body, re.S):
                try:
                    listed = json.loads(am.group(1))
                except ValueError:
                    listed = []
                for a in listed if isinstance(listed, list) else []:
                    cname = a.get("name") if isinstance(a, dict) else None
                    if not cname:
                        continue
                    for e in a.get("events") or []:
                        if not isinstance(e, dict) or not isinstance(e.get("time"), (int, float)):
                            continue
                        if not math.isfinite(e["time"] * 1000):
                            continue
                        etype = e.get("type", "StandardEventType")
                        ename = e.get("name")
                        if not ename:
                            if etype == "GEALEventType" and e.get("geal"):
                                ename = "geal"
                            else:
                                continue
                        ms = int(round(e["time"] * 1000))
                        pos = _position(e.get("position")) if "position" in e else None
                        args = {}
                        if "contact_ref" in e:
                            args["contact_ref"] = e["contact_ref"]
                        if "end_time" in e and isinstance(e["end_time"], (int, float)) and math.isfinite(e["end_time"] * 1000):
                            args["end_ms"] = int(round(e["end_time"] * 1000))
                        if "data" in e and e["data"] not in (None, "", {}):
                            args["data"] = e["data"]
                        if etype == "GEALEventType" and e.get("geal"):
                            args["geal"] = e["geal"]
                        if etype != "StandardEventType":
                            args["type"] = etype
                        ao_events.setdefault(cname, []).append((ename, ms, pos, args or None))
        for parent in extends_of(text):
            pa = parent if parent.endswith(".ao") else parent + ".ao"
            if pa not in seen:
                todo.append(pa)
    return amd, exclude, ao_events, views


def rig_clips_with_views(amd, ao, amd_cache=None, ao_cache=None):
    """Resolve every clip AND .ao view for (amd, ao) -> {name: (index, {"name", "length", "events"})}.

    Each event is (name, ms, pos_or_None, args_or_None) sorted by ms.
    """
    if ao_cache is not None and ao in ao_cache:
        _, exclude, ao_events, views = ao_cache[ao]
    else:
        parsed_ao = parse_ao_chain_events_and_views(ao)
        if ao_cache is not None:
            ao_cache[ao] = parsed_ao
        _, exclude, ao_events, views = parsed_ao

    if amd_cache is not None and amd in amd_cache:
        anims = amd_cache[amd]
    else:
        anims = parse_amd_from_text(read_text(amd))
        if amd_cache is not None:
            amd_cache[amd] = anims
    if not anims:
        return {}

    index = {}
    for i, a in enumerate(anims):
        if a["name"] in index:
            continue
        events = []
        if not exclude:
            for en, t_str, p_str in a["events"]:
                if t_str.lstrip("-").isdigit():
                    events.append((en, int(t_str), _position(p_str), None))
        events.extend(ao_events.get(a["name"], []))
        events.sort(key=lambda e: e[1])
        index[a["name"]] = (i, {"name": a["name"], "once": a["once"], "length": a["length"], "events": events})

    for vname, (src, begin, end, excl_std) in views.items():
        if vname in index or src not in index:
            continue
        src_i, src_clip = index[src]
        src_len = src_clip["length"]
        ve = src_len if end is None else min(end, src_len)
        if not (0 <= begin < ve <= src_len):
            continue
        events = []
        if not excl_std:
            for en, ms, pos, args in src_clip["events"]:
                if begin <= ms < ve:
                    events.append((en, ms - begin, pos, args))
        events.extend(ao_events.get(vname, []))
        events.sort(key=lambda e: e[1])
        index[vname] = (src_i, {"name": vname, "once": src_clip["once"], "length": ve - begin, "events": events})

    return index


def _first_contact4(events):
    hits = [(ms, pos) for name, ms, pos, _ in events if name == "contact"]
    return min(hits, key=lambda h: h[0]) if hits else None


def _all_contacts4(events):
    return sorted([(ms, pos) for name, ms, pos, _ in events if name == "contact"], key=lambda h: h[0])


def main():
    dest = sys.argv[sys.argv.index("--json") + 1] if "--json" in sys.argv else os.path.join(REPO, "data", "monster-contacts.json")
    stances_dest = (sys.argv[sys.argv.index("--stances") + 1] if "--stances" in sys.argv
                    else os.path.join(os.path.dirname(dest), "monster-stances.json"))
    fetch_missing = "--cached-only" not in sys.argv
    keys = animation_keys()
    variants = [r for r in table("MonsterVarieties") if isinstance(r, dict) and r.get("Id")]
    type_file = os.path.join(REPO, "data", "object-types.json")
    if os.path.isfile(type_file):
        with open(type_file, encoding="utf-8-sig") as source:
            object_types = {r["type"]: r for r in json.load(source)["types"]}
    else:
        object_types = {r["type"]: r for r in table("object_types")} if clientdb.has("object_types") else {}

    acts = sorted({a for r in variants for a in (r.get("ACTFiles") or [])})
    aos = sorted({a for r in variants for a in (r.get("AOFiles") or [])})
    print(f"keys: {len(keys)} Animation.dat keys", file=sys.stderr, flush=True)
    print(f"{'reading/fetching' if fetch_missing else 'reading cached'} {len(acts)} .act and {len(aos)} .ao",
          file=sys.stderr, flush=True)
    if fetch_missing:
        missing_acts = [a for a in acts if read_text(a) is None]
        missing_aos = [a for a in aos if read_text(a) is None]
        if missing_acts:
            fetch(missing_acts)
        if missing_aos:
            fetch(missing_aos)

    parsed, stanced, melee_pools = {}, {}, {}
    for act in acts:
        text = read_text(act) or ""
        parsed[act] = act_pools(text, keys, budgets=True, weights=True)
        if pools := {k: v for k, v in stance_pools(text, ACTION_KEYS).items()
                     if not k.split("/")[0].endswith("Town") or "/" not in k}:
            stanced[act] = pools
        melee_pools[act] = act_pools(text, ["Melee"], budgets=True)[0].get("Melee")

    act_aos = {ao for _, ao in parsed.values() if ao}
    ot_aos = {r["animated_object"] for r in object_types.values() if r.get("animated_object")}
    all_aos = fetch_ao_chain(aos + sorted(act_aos) + sorted(ot_aos), fetch_missing)

    ao_meta_cache = {ao: parse_ao_chain_events_and_views(ao) for ao in all_aos}
    amds = {ao: meta[0] for ao, meta in ao_meta_cache.items()}
    if fetch_missing:
        missing_amds = sorted({amd for amd in amds.values() if amd and read_text(amd) is None})
        if missing_amds:
            fetch(missing_amds)
    complete = {ao: complete_ao_chain(ao) for ao in all_aos}
    print(f"indexing {len(variants)} monster varieties", file=sys.stderr, flush=True)

    amd_cache, rigs = {}, {}

    def rig(amd, ao):
        if (amd, ao) not in rigs:
            rigs[(amd, ao)] = rig_clips_with_views(amd, ao, amd_cache=amd_cache, ao_cache=ao_meta_cache)
        return rigs[(amd, ao)]

    # Deduplicate pool/clip/event data by unique (act, ao, amd) profile (Option B normalization).
    profile_list = []
    profile_index_by_key = {}
    variety_cache = {}
    varieties_out = {}
    pools_written = 0

    def get_or_build_profile(acts_t, aos_t, ot_ao):
        for act in acts_t:
            pools, act_ao = parsed[act]
            if not pools:
                continue
            ao_cands = []
            for cand in (act_ao, aos_t[0] if aos_t else None, ot_ao):
                if cand and cand not in ao_cands:
                    ao_cands.append(cand)
            if not ao_cands:
                continue
            primary_ao = ao_cands[0]
            primary_amd = amds.get(primary_ao)
            primary_clips = rig(primary_amd, primary_ao) if primary_amd else {}
            fallback_rigs = []
            for cand in ao_cands[1:]:
                f_amd = amds.get(cand)
                if f_amd:
                    fc = rig(f_amd, cand)
                    if fc:
                        fallback_rigs.append((cand, f_amd, fc))

            used_ao = primary_ao if primary_amd else (fallback_rigs[0][0] if fallback_rigs else None)
            used_amd = primary_amd or (fallback_rigs[0][1] if fallback_rigs else None)
            if not used_amd:
                continue
            # Key by (act, primary_ao, fallback AOs actually used) so varieties differing only in an unused AO share one profile.
            needed_fallbacks = []
            for k, specs in pools.items():
                if primary_clips and all(name in primary_clips for name, _, _ in specs):
                    continue
                for cand_ao, _, fc in fallback_rigs:
                    if all(name in fc for name, _, _ in specs):
                        if cand_ao not in needed_fallbacks:
                            needed_fallbacks.append(cand_ao)
                        break
            pkey = (act, used_ao, used_amd, tuple(needed_fallbacks))
            if pkey in profile_index_by_key:
                pid = profile_index_by_key[pkey]
                return pid, profile_list[pid]

            recorded = {}
            for k, specs in pools.items():
                chosen = None
                if primary_clips and all(name in primary_clips for name, _, _ in specs):
                    chosen = primary_clips
                else:
                    for _, _, fc in fallback_rigs:
                        if all(name in fc for name, _, _ in specs):
                            chosen = fc
                            break
                if chosen is None:
                    continue
                pool = []
                for name, budget, weight in specs:
                    i, a = chosen[name]
                    contact = _first_contact4(a["events"])
                    named_evs = []
                    for en, ms, pos, args in a["events"]:
                        ev_obj = {"name": en, "ms": ms}
                        if pos is not None:
                            ev_obj["position"] = [round(x, 3) for x in pos]
                        if args:
                            ev_obj["args"] = json.dumps(args, separators=(",", ":"), sort_keys=True)
                        named_evs.append(ev_obj)
                    entry_clip = {
                        "animation": name, "index": i, "length": a["length"],
                        "contact": None if contact is None else contact[0],
                        "position": None if contact is None else contact[1],
                        "budget": budget, "weight": weight,
                        "events": named_evs,
                    }
                    every = _all_contacts4(a["events"])
                    if len(every) > 1:
                        entry_clip["contacts"] = [[ms, pos] for ms, pos in every]
                    pool.append(entry_clip)
                recorded[k] = pool
            if recorded:
                pid = len(profile_list)
                prof = {"act": act, "ao": used_ao, "amd": used_amd, "pools": recorded}
                profile_list.append(prof)
                profile_index_by_key[pkey] = pid
                return pid, prof
        return None, None

    for n, row in enumerate(variants, 1):
        resolved_type = object_types.get(row["Id"], object_types.get(row.get("BaseMonsterTypeIndex"), {}))
        key = (tuple(row.get("ACTFiles") or []), tuple(row.get("AOFiles") or []), resolved_type.get("animated_object"),
               bool(resolved_type) and not resolved_type.get("missing"))
        if key not in variety_cache:
            pid, prof = get_or_build_profile(key[0], key[1], key[2])
            entry = None
            if pid is not None:
                entry = {"profile": pid, "act": prof["act"], "ao": prof["ao"], "amd": prof["amd"]}
            for act in key[0]:
                names = melee_pools[act]
                if not names:
                    continue
                ao, known_binding = animation_source(key[0], key[1], parsed[act][1], key[2])
                amd = amds.get(ao) if ao else None
                if not amd:
                    continue
                clips = rig(amd, ao)
                if not all(name in clips for name, _ in names):
                    continue
                clocks = []
                for name, budget in names:
                    i, a = clips[name]
                    contact = _first_contact4(a["events"])
                    clocks.append({"animation": name, "index": i, "length": a["length"],
                                   "contact": None if contact is None else contact[0],
                                   "act": act, "ao": ao, "amd": amd,
                                   **clip_clock(a, budget, complete.get(ao, False) and known_binding and key[3])})
                if entry is None:
                    entry = {"profile": None, "act": act, "ao": ao, "amd": amd}
                entry["melee_clocks"] = clocks
                break
            variety_cache[key] = entry
        if variety_cache[key]:
            varieties_out[row["Id"]] = variety_cache[key]
            pid = variety_cache[key].get("profile")
            if pid is not None:
                pools_written += len(profile_list[pid]["pools"])
        if n % 2000 == 0:
            print(f"  {n}/{len(variants)}", file=sys.stderr, flush=True)

    doc = {"version": 9, "profiles": profile_list, "varieties": varieties_out}
    with open(dest + ".tmp", "w", encoding="utf-8") as f:
        json.dump(doc, f, separators=(",", ":"), sort_keys=True)
    os.replace(dest + ".tmp", dest)
    total_profile_clips = sum(len(pl) for p in profile_list for pl in p["pools"].values())
    total_profile_events = sum(len(c["events"]) for p in profile_list for pl in p["pools"].values() for c in pl)
    print(f"wrote {dest}: {len(varieties_out)}/{len(variants)} varieties -> {len(profile_list)} unique profiles "
          f"({total_profile_clips} clips, {total_profile_events} named events, {pools_written} variety pools)",
          file=sys.stderr, flush=True)

    # Stanced .acts (Rogue Exiles & weapon-stanced monsters)
    first_ao = {}
    for row in variants:
        for act in row.get("ACTFiles") or []:
            if row.get("AOFiles"):
                first_ao.setdefault(act, row["AOFiles"][0])
    stance_out, stance_pools_written = {}, 0
    for act, pools in sorted(stanced.items()):
        ao = parsed[act][1] or first_ao.get(act)
        amd = amds.get(ao) if ao else None
        if not amd:
            continue
        clips = rig(amd, ao)
        recorded = {}
        for k, names in sorted(pools.items()):
            if not all(name in clips for name in names):
                continue
            pool, event = [], HIT_EVENT.get(k.split("/")[-1], "contact")
            for name in names:
                i, a = clips[name]
                hits = [ms for e, ms, _, _ in a["events"] if e == event]
                pool.append({"animation": name, "index": i, "length": a["length"],
                             "contact": min(hits) if hits else None, "contacts": sorted(hits)})
            if any(c["contact"] is not None for c in pool):
                recorded[k] = pool
        if recorded:
            stance_out[act] = {"ao": ao, "amd": amd, "pools": recorded}
            stance_pools_written += len(recorded)
    with open(stances_dest + ".tmp", "w", encoding="utf-8") as f:
        json.dump(stance_out, f, indent=1, sort_keys=True)
    os.replace(stances_dest + ".tmp", stances_dest)
    print(f"wrote {stances_dest}: {len(stance_out)} stanced .act files ({stance_pools_written} pools)",
          file=sys.stderr, flush=True)


if __name__ == "__main__":
    main()
