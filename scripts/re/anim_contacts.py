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
# Also reads each Characters.dat ACTFile's ordinary wand Melee pools, resolving nested stance inheritance and
# preserving ACT order for the native sequence-seeded variant selector. Only the current simple, unconditional
# animation lists are accepted; aliases/modifiers/conditions fail extraction instead of inventing a native choice.
#
# .amd text (UTF-16 without a BOM): a version line, a count line, then per animation:
#   "<name>" / once|loop / <length ms> / <event count> / per event: "<event>" / <time ms> / <x y z>, then two lines.
# The reader is ot_components.amd_animations (shared with monster_takehit.py).
# Files come from the GGPK through scripts/re/ggdump (see ot_components.py) and are cached in Tools/re/out/ot.
import json, os, re, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ot_components import REPO, amd_of, fetch, first_contact, read_text, rig_clips  # noqa: E402


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


def direct_assignments(tokens):
    """Direct Melee/OffMelee expressions only; a child's overrides must never overwrite its parent."""
    result = {}
    i = 0
    while i < len(tokens):
        if tokens[i] == "{":
            i = closing_brace(tokens, i) + 1
        elif tokens[i] in ("Melee", "OffMelee") and tokens[i + 1:i + 2] == ["="]:
            end = i + 2
            while end < len(tokens) and tokens[end] != ";":
                end += 1
            expression = tokens[i + 2:end]
            if end == len(tokens) or len(expression) != 1 or not expression[0].startswith('"'):
                raise ValueError(f"Unsupported ordinary wand ACT expression: {' '.join(tokens[i:end])}")
            result[tokens[i]] = expression[0][1:-1]
            i = end + 1
        else:
            i += 1
    return result


def stance_body(tokens, name):
    """The named direct child stance, or None. The caller passes the root or the Wand body."""
    i = 0
    while i < len(tokens):
        if tokens[i:i + 3] == ["stance", name, "{"]:
            return tokens[i + 3:closing_brace(tokens, i + 2)]
        if tokens[i] == "{":
            i = closing_brace(tokens, i) + 1
        else:
            i += 1
    return None


def ordinary_wand_pools(text, animations):
    """Resolve ACT main-hand wand stances in file order, then attach the rig's exact contacts and indices.

    These character pools contain only plain animation names: native flags0/group0/weight1, with no exclusion.
    Fail on aliases, conditions, weights or modifiers rather than inventing timing for an unsupported pool.
    """
    tokens = act_tokens(text)
    root_start = tokens.index("{")
    root = tokens[root_start + 1:closing_brace(tokens, root_start)]
    wand = stance_body(root, "Wand")
    if wand is None:
        raise ValueError("Character ACT has no Wand stance")
    inherited = direct_assignments(wand)
    if "Melee" not in inherited:
        raise ValueError("Wand stance must define its ordinary main-hand Melee pool")
    clips = {a["name"]: (i, a) for i, a in enumerate(animations)}
    contact_times = contacts(animations)
    contact_positions = positions(animations)
    result = {}
    for name in ("Wand", "WandShield", "WandWand"):
        assignments = dict(inherited)
        if name != "Wand":
            child = stance_body(wand, name)
            if child is not None:
                assignments.update(direct_assignments(child))
        pool = []
        for animation in assignments["Melee"].split(","):
            animation = animation.strip()
            if not re.fullmatch(r"[A-Za-z_][A-Za-z_0-9]*", animation):
                raise ValueError(f"Unsupported ordinary wand animation {animation!r} in {name}")
            if animation not in clips or animation not in contact_times:
                raise ValueError(f"Ordinary wand clip has no rig/contact: {name}/{animation}")
            index, _ = clips[animation]
            length, contact = contact_times[animation]
            if length <= 0 or not 0 <= contact <= length:
                raise ValueError(f"Invalid ordinary wand contact: {name}/{animation}")
            pool.append({"animation": animation, "index": index, "length": length, "contact": contact, "weight": 1,
                         "position": contact_positions.get(animation)})
        result[name] = pool
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
                      "defaultWand": ordinary_wand_pools(text, animations)}
        print(f"{c['Name']}: {ao} -> {amd}, {len(result[ao]['animations'])} animations with a contact", file=sys.stderr)
    with open(dest + ".tmp", "w", encoding="utf-8") as f:
        json.dump(result, f, indent=1)
    os.replace(dest + ".tmp", dest)   # never leave a truncated file for the Gateway
    print(f"wrote {dest}", file=sys.stderr)


if __name__ == "__main__":
    main()
