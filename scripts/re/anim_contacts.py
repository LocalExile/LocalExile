# usage: python -I scripts/re/anim_contacts.py [--json data/animation-contacts.json]
#
# The "contact" keyframe of every character class's animations: for each Characters.dat AOFile, follows the .ao's
# "extends" chain to its AnimationController metadata (.amd, e.g. Art/Models/CHARACTERS/Witch/rig.amd) and reads, per
# animation, its length and the time of its "contact" event, both in ms, plus the event's position in the model's
# space ("positions": [x, y, z] world units; -y is forward, -z is up: the Ranger's attack1_bow releases 59.8 units in
# front and 127.5 up). That keyframe is when an attack lands and when a spell or arrow is released (Witch
# spell_projectile_wand: 1066 ms long, contact at 599). The Gateway reads the output to release projectiles at the right
# moment of the cast and from the right point (Agents/docs/protocol/11-skills.md §9, §10). Events an .ao of the chain
# adds (or substitutes with exclude_metadata_events) are merged as the client merges them (ot_components.rig_clips).
# Also reads each Characters.dat ACTFile's default-attack pools ("defaultAttack", per stance): the stance's Melee
# animations in ACT order for the native sequence-seeded variant selector, each resolved to a rig clip or to a view of
# one (the .ao chain's view_of/view_name/view_begin/view_end: attack_sword_01a is doublestrike_sword's first 365 ms),
# with its contact and slow span relative to the view and the ACT's attack_time budget (seconds). The client plays a
# budgeted view over the budget and stretches it to a longer attack time, so the contact is not a fraction of the attack
# (11-skills.md §4). Only plain names with attack_time and the with_main_hand / item_animation flags are accepted;
# anything else fails extraction instead of inventing a native choice.
#
# .amd text (UTF-16 without a BOM): a version line, a count line, then per animation:
#   "<name>" / once|loop / <length ms> / <event count> / per event: "<event>" / <time ms> / <x y z>, then two lines.
# The reader is ot_components.amd_animations (shared with monster_takehit.py).
# Files come from the GGPK through scripts/re/ggdump (see ot_components.py) and are cached in Tools/re/out/ot.
import json, os, re, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ot_components import REPO, amd_of, extends_of, fetch, first_contact, read_text, rig_clips  # noqa: E402


def contacts(animations):
    """{animation: [length ms, contact ms]} for the rig's animations that have a contact event (the earliest one)."""
    out = {}
    for a in animations:
        if (contact := first_contact(a["events"])) is not None:
            out[a["name"]] = [a["length"], contact[0]]
    return out


def positions(animations):
    """{animation: [x, y, z]} for the animations whose earliest contact event carries a position (model units: +x
    the model's left, -y forward, -z up)."""
    out = {}
    for a in animations:
        contact = first_contact(a["events"])
        if contact is not None and contact[1] is not None:
            out[a["name"]] = contact[1]
    return out


VIEW = re.compile(r'view_of\s*=\s*"([^"]+)"\s*\n\s*view_name\s*=\s*"([^"]+)"((?:\s*\n\s*view_(?:begin|end)\s*=\s*\d+)*)')


def ao_views(ao):
    """{view name: (source animation, begin ms, end ms or None)}: the views the .ao chain defines on its rig's clips
    (DexNoWind.ao: attack_sword_01a = doublestrike_sword up to 365 ms, attack_sword_01b from 365 ms). The first
    definition along the chain wins."""
    out, seen, todo = {}, set(), [ao]
    while todo:
        path = todo.pop(0)
        if not path or path in seen:
            continue
        seen.add(path)
        fetch([path])
        text = read_text(path)
        if text is None:
            continue
        for view in VIEW.finditer(text.replace("\r", "")):
            source, name, bounds = view.groups()
            begin = re.search(r"view_begin\s*=\s*(\d+)", bounds)
            end = re.search(r"view_end\s*=\s*(\d+)", bounds)
            out.setdefault(name, (source, int(begin.group(1)) if begin else 0, int(end.group(1)) if end else None))
        todo.extend(p if p.endswith(".ao") else p + ".ao" for p in extends_of(text))
    return out


# Quoted strings remain whole, so comments, braces and semicolons inside them cannot alter ACT nesting.
TOKENS = re.compile(r'//[^\n]*|/\*.*?\*/|"(?:\\.|[^"\\])*"|[A-Za-z_][A-Za-z_0-9]*|[^\s]', re.S)


def act_tokens(text):
    return [m.group(0) for m in TOKENS.finditer(text)
            if not m.group(0).startswith(("//", "/*"))]


def closing_brace(tokens, start):
    depth = 1
    for i in range(start + 1, len(tokens)):
        depth += (tokens[i] == "{") - (tokens[i] == "}")
        if depth == 0:
            return i
    raise ValueError("Unclosed ACT block")


MELEE_FLAGS = {"with_main_hand", "item_animation", "OffHand"}


def direct_melee(tokens):
    """The body's own Melee expression as (names, flags), or None: a quoted list, then flags, then ";". A child's
    override must never overwrite its parent's, so nested blocks are skipped."""
    i = 0
    while i < len(tokens):
        if tokens[i] == "{":
            i = closing_brace(tokens, i) + 1
        elif tokens[i] == "Melee" and tokens[i + 1:i + 2] == ["="]:
            end = i + 2
            while end < len(tokens) and tokens[end] != ";":
                end += 1
            expression = tokens[i + 2:end]
            if end == len(tokens) or not expression or not expression[0].startswith('"') \
                    or any(t not in MELEE_FLAGS for t in expression[1:]):
                raise ValueError(f"Unsupported default attack ACT expression: {' '.join(tokens[i:end])}")
            return expression[0][1:-1], set(expression[1:])
        else:
            i += 1
    return None


def stances(tokens, inherited, out):
    """Every stance of the body, nested, as {name: (names, flags)}: a stance without its own Melee keeps its parent's."""
    i = 0
    while i < len(tokens):
        if tokens[i:i + 1] == ["stance"] and tokens[i + 2:i + 3] == ["{"]:
            name, end = tokens[i + 1], closing_brace(tokens, i + 2)
            body = tokens[i + 3:end]
            melee = direct_melee(body) or inherited
            out.setdefault(name, melee)
            stances(body, melee, out)
            i = end + 1
        elif tokens[i] == "{":
            i = closing_brace(tokens, i) + 1
        else:
            i += 1
    return out


def default_attack_pools(text, animations, views):
    """{stance: [entries]}: each stance's Melee animations in ACT order, resolved to a rig clip or a view of one, with
    the contact and slow span (ms from the entry's start), the ACT attack_time budget (seconds) and whether the clip is
    the item's (item_animation: the weapon's own rig plays it). A name ending in _01a brings its _01b view along as a
    pair: the client plays the two in an order not predicted (live 2026-11-03: 51 sword swings, no seed found)."""
    tokens = act_tokens(text)
    root_start = tokens.index("{")
    root = tokens[root_start + 1:closing_brace(tokens, root_start)]
    root_melee = direct_melee(root)
    if root_melee is None:
        raise ValueError("Character ACT has no default Melee animation")
    pools = stances(root, root_melee, {"Unarmed": root_melee})
    clips = {a["name"]: (i, a) for i, a in enumerate(animations)}

    def entry(name, budget, item, pair):
        if name in views:
            source, begin, end = views[name]
        elif name in clips:
            source, begin, end = name, 0, None
        else:
            raise ValueError(f"Default attack animation without a rig clip or view: {name}")
        if source not in clips:
            raise ValueError(f"View of an unknown rig clip: {name} of {source}")
        index, clip = clips[source]
        end = clip["length"] if end is None else end
        if not 0 <= begin < end <= clip["length"]:
            raise ValueError(f"Invalid view bounds: {name} [{begin}, {end}] of {clip['length']}")
        events = [(e, t, p) for e, t, p in clip["events"] if begin <= t < end]
        hits = [(t, p) for e, t, p in events if e == "contact"]
        if not hits:
            raise ValueError(f"Default attack animation without a contact: {name}")
        contact, position = min(hits, key=lambda h: h[0])
        slow_begin = next((t for e, t, _ in events if e == "slow_begin"), None)
        slow_end = next((t for e, t, _ in events if e == "slow_end"), None)
        # lock_cancelling: from here the client runs a Move it holds during the swing (attack1_bow: 600 of 1066 ms, after
        # the contact at 399); a clip without one lets the Move run at the contact (11-skills.md §4).
        cancel = next((t for e, t, _ in events if e == "lock_cancelling"), None)
        return {"animation": name, "source": source, "index": index, "begin": begin, "length": end - begin,
                "contact": contact - begin, "cancel": cancel - begin if cancel is not None else None,
                "slow": [slow_begin - begin, slow_end - begin] if slow_begin is not None and slow_end is not None else None,
                "budget": budget, "weight": 1, "item": item, "pair": pair, "position": position}

    result = {}
    for stance, (names, flags) in pools.items():
        if names in ("delete", "@Melee"):
            continue
        pool = []
        for part in names.split(","):
            name, *options = [p.strip() for p in part.strip().split(":")]
            budget = None
            for option in options:
                key, _, value = option.partition("=")
                if key != "attack_time":
                    raise ValueError(f"Unsupported default attack option {option!r} in {stance}")
                budget = float(value)
            if not re.fullmatch(r"[A-Za-z_][A-Za-z_0-9]*", name):
                raise ValueError(f"Unsupported default attack animation {name!r} in {stance}")
            partner = name[:-1] + "b" if name.endswith("_01a") and name[:-1] + "b" in views else None
            pool.append(entry(name, budget, "item_animation" in flags, partner is not None))
            if partner is not None:
                pool.append(entry(partner, budget, "item_animation" in flags, True))
        result[stance] = pool
    return result


def main():
    dest = sys.argv[sys.argv.index("--json") + 1] if "--json" in sys.argv else os.path.join(REPO, "data", "animation-contacts.json")
    chars = json.load(open(os.path.join(REPO, "data", "tables", "Characters.json"), encoding="utf-8"))
    result = {}
    for c in chars:
        ao = c.get("AOFile")
        if not ao or ao in result:
            continue
        amd = amd_of(ao)
        animations = (rig_clips(amd, ao) if amd else None) or []
        act = c.get("ACTFile")
        if not act or not animations:
            raise ValueError(f"Missing character ACT/rig for {c['Name']}")
        fetch([act])
        text = read_text(act)
        if text is None:
            raise ValueError(f"Missing character ACT asset: {act}")
        result[ao] = {"amd": amd, "act": act, "animations": contacts(animations), "positions": positions(animations),
                      "indices": {a["name"]: i for i, a in enumerate(animations)},
                      "defaultAttack": default_attack_pools(text, animations, ao_views(ao))}
        print(f"{c['Name']}: {ao} -> {amd}, {len(result[ao]['animations'])} animations with a contact, "
              f"{len(result[ao]['defaultAttack'])} default attack stances", file=sys.stderr)
    with open(dest + ".tmp", "w", encoding="utf-8") as f:
        json.dump(result, f, indent=1)
    os.replace(dest + ".tmp", dest)   # never leave a truncated file for the Gateway
    print(f"wrote {dest}", file=sys.stderr)


if __name__ == "__main__":
    main()
