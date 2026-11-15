# usage: python -I scripts/re/monster_contacts.py [--json data/monster-contacts.json] [--stances data/monster-stances.json]
#
# For every MonsterVarieties row: the clips its .act maps to the animation keys of its projectile attacks (Animation.dat
# Ids: "Melee" and the keys MonsterMortar / MonsterProjectileSpell name, e.g. the Sand Spitter mortar's
# "SpellProjectile" = "attack1,attack2"), in ACT order, each with its index in the rig's animation list, its length
# and its "contact" keyframe: time in ms and position in the model's space ([x, y, z] world units: +x the model's left,
# -y forward, -z up). The client picks the clip of a pool with its sequence-seeded selector (AnimationVariantSelector)
# and plays it stretched to the attack time; a projectile skill with `projectile_uses_contact_position` releases its
# projectile at the contact (the Spitter's attack1: 374.5 ms of 1000, [1.33, 45.89, -71.56], i.e. 45.9 units behind
# the body, which faces away from its target, and 71.6 up). The Gateway releases monster projectiles at that time and
# from that point (Program.ProjectileHostile.cs; Agents/docs/protocol/11-skills.md §6).
#
# Events come from the rig .amd merged with the .ao chain's own list (ot_components.rig_clips: the Spitter's .ao
# replaces the rig's events with exclude_metadata_events). Only plain pools are recorded (comma-separated clip names:
# no "@alias", no "clip:condition" variants, every clip present in the rig); a key with anything else is left out, and
# the Gateway keeps its fallback timing for it. Files come from the GGPK through scripts/re/ggdump (cached in
# Tools/re/out/ot, see ot_components.py), fetched in batches first.
#
# monster-stances.json: an .act with stances (the Rogue Exiles' Metadata/Monsters/Exiles/Characters/*.act, like the
# character classes') plays a skill by the Animation.dat row its action class asks for (ACTION_KEYS) in the stance its
# hands' items select: an exile with a sword and a shield leaps with OneHandSwordShield's LeapSlam =
# "leapslam_sword_shield", an unarmed one with the root's "leapslam_sword". Per .act (not per variety: 500 varieties
# share 50 of them), each key's root pool and, as "<stance>/<key>", a stance's pool where it differs from the root's. A
# stance takes its parent's entry for a key it does not set, and "@Key" names the stance's own entry for Key; both are
# resolved here. Town stances (never in combat) and pools with no contact anywhere are left out.
import json, os, re, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ot_components import REPO, amd_map, animation_controller, extends_of, fetch, first_contact, read_text, rig_clips  # noqa: E402
from anim_contacts import act_tokens, closing_brace  # noqa: E402
import clientdb  # noqa: E402  (on the path through ot_components)
NAME = re.compile(r"[A-Za-z_][A-Za-z_0-9]*")


def table(name):
    return clientdb.table(name)


def animation_keys():
    """"Melee" and every Animation.dat Id a monster projectile table names, sorted."""
    animations = table("Animation")
    keys = {"Melee", "Throw"}
    for name in ("MonsterMortar", "MonsterProjectileSpell", "MonsterProjectileAttack"):
        try:
            rows = table(name)
        except FileNotFoundError:
            continue
        for r in rows:
            a = r.get("Animation")
            if isinstance(a, int) and 0 <= a < len(animations) and animations[a].get("Id"):
                keys.add(animations[a]["Id"])
    return sorted(keys)


def fetch_ao_chain(paths):
    """Fetch these .ao files and every "extends" parent; return all .ao paths seen."""
    known, todo = set(), set(paths)
    while todo:
        fetch(sorted(todo))
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
    """`Key = "value";` entries of the .act's root block only, first one wins. A nested stance's entries (a weapon or
    phase stance) apply in that stance, not to the type: taking the last entry at any depth had given 191 varieties
    another stance's pool (review 2026-10-30). Entries with anything between the string and the `;` (an Emerge's
    effect) are left out."""
    tokens = act_tokens(text)
    if "{" not in tokens:
        return {}
    try:
        start = tokens.index("{")
        body = tokens[start + 1:closing_brace(tokens, start)]
        out, i = {}, 0
        while i < len(body):
            if body[i] == "{":
                i = closing_brace(body, i) + 1
            elif i + 3 < len(body) and body[i + 1] == "=" and body[i + 2].startswith('"') and body[i + 3] == ";":
                out.setdefault(body[i], body[i + 2][1:-1].strip())
                i += 4
            else:
                i += 1
        return out
    except ValueError:
        return {}


def act_blocks(text):
    """{stance: [own entries of the root, ..., of the stance]} for every stance of the .act ("" for the root), each
    entry {key: tokens after "="}. A nested stance (OneHandSwordShield in OneHandSword) lists its parents first."""
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
                own.setdefault(body[i], body[i + 2:end])
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
    """The clip list a stance plays for an Animation key: its own entry, else its nearest parent's; "@Other" is the
    stance's entry for Other. Only plain lists of clip names count (trailing effect_* and item flags do not change the
    clip); anything else is None."""
    for own in reversed(chain):
        if key in own:
            expression = own[key]
            if not expression or not expression[0].startswith('"'):
                return None
            value = expression[0][1:-1].strip()
            if value.startswith("@"):
                return stance_entry(chain, value[1:], depth + 1) if depth < 8 else None
            clips = [c.strip() for c in value.split(",")]
            return clips if all(NAME.fullmatch(c) for c in clips) else None
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


def act_pools(text, keys):
    """{key: [clip, ...]} for the .act's root-block `Key = "a,b";` entries among `keys` that are plain clip lists, and
    the .ao the act animates."""
    entries = root_assignments(text)
    pools = {}
    for key in keys:
        value = entries.get(key)
        if not value:
            continue
        clips = [c.strip() for c in value.split(",")]
        # Stage casts use Throw (ZombieBossExtract). Preserve its clip/contact even when the ACT also
        # specifies an attack budget; that budget is not a replacement for the granted effect's cast time.
        if key == "Throw":
            clips = [re.sub(r":attack_time=\d+(?:\.\d+)?$", "", c) for c in clips]
        if all(NAME.fullmatch(c) for c in clips):
            pools[key] = clips
    m = re.search(r'^\s*animated_object\s+"([^"]+)"', text, re.M)
    return pools, (m.group(1) if m else None)


# The Animation.dat rows the client's skill action classes request for their main animation (Agents/docs/protocol/
# 11-skills.md §9, "Skill animation rows"): the start routines' row argument (0x141b58830 attacks, 0x141c9b060 the
# melee family, 0x141ba58f0 projectiles; 0x141b720e0 bow and thrown attacks: GrantedEffects.Animation, else Melee)
# and the spell base's slot +0x4e0 (warcries +0x4d8).
ACTION_KEYS = [
    "BlastRain", "CastFast", "Charge", "ChargeStart", "Cleave", "CloneShot", "DoubleStrike", "DualStrike", "Flurry",
    "Frenzy", "GlacialHammer", "GroundSlam", "HeavyStrike", "LeapSlam", "Melee", "Puncture", "RainOfArrows",
    "ShadowProjectiles", "Snipe", "SpellAreaOfEffect", "SpellAreaOfEffectChaos", "SpellAreaOfEffectCold",
    "SpellAreaOfEffectFire", "SpellAreaOfEffectLightning", "SpellBuff", "SpellBuffCold", "SpellBuffFast", "SpellBuffFire",
    "SpellProjectile", "SpellProjectileChaos", "SpellProjectileCold", "SpellProjectileFire", "SpellProjectileLightning",
    "Throw", "Warcry",
]
# A key whose action hits on another event than "contact": the LeapSlam action's event slot (0x1419fd8e0) lifts on
# leave_ground and slams on hit_ground (leapslam_sword: 933 of 1400 ms; event hashes 0xf1d5, 0xf2b2).
HIT_EVENT = {"LeapSlam": "hit_ground"}


def main():
    dest = sys.argv[sys.argv.index("--json") + 1] if "--json" in sys.argv else os.path.join(REPO, "data", "monster-contacts.json")
    keys = animation_keys()
    variants = [r for r in table("MonsterVarieties") if isinstance(r, dict) and r.get("Id")]
    acts = sorted({a for r in variants for a in (r.get("ACTFiles") or [])})
    aos = sorted({a for r in variants for a in (r.get("AOFiles") or [])})
    print(f"keys {', '.join(keys)}", file=sys.stderr, flush=True)
    print(f"fetching {len(acts)} .act and {len(aos)} .ao", file=sys.stderr, flush=True)
    fetch(acts)
    fetch(aos)
    stances_dest = sys.argv[sys.argv.index("--stances") + 1] if "--stances" in sys.argv         else os.path.join(os.path.dirname(dest), "monster-stances.json")
    parsed, stanced = {}, {}
    for act in acts:
        text = read_text(act) or ""
        parsed[act] = act_pools(text, keys)
        if pools := {k: v for k, v in stance_pools(text, ACTION_KEYS).items() if not k.split("/")[0].endswith("Town")
                     or "/" not in k}:
            stanced[act] = pools
    act_aos = {ao for _, ao in parsed.values() if ao}
    all_aos = fetch_ao_chain(aos + sorted(act_aos))
    amds = amd_map(all_aos, fetch_missing=False)
    fetch(sorted({amd for amd in amds.values() if amd}))
    print(f"fetched; indexing {len(variants)} monster varieties", file=sys.stderr, flush=True)

    rigs = {}

    def rig(amd, ao):
        # Parsed once per (rig, .ao): the .ao chain's event list differs per model.
        if (amd, ao) not in rigs:
            clips = rig_clips(amd, ao, fetch_missing=False) or []
            index = {}
            for i, a in enumerate(clips):
                index.setdefault(a["name"], (i, a))
            rigs[(amd, ao)] = index
        return rigs[(amd, ao)]

    cache, out, pools_written = {}, {}, 0
    for n, row in enumerate(variants, 1):
        key = (tuple(row.get("ACTFiles") or []), tuple(row.get("AOFiles") or []))
        if key not in cache:
            entry = None
            for act in key[0]:
                pools, act_ao = parsed[act]
                if not pools:
                    continue
                ao = act_ao or (key[1][0] if key[1] else None)
                amd = amds.get(ao) if ao else None
                if not amd:
                    continue
                clips = rig(amd, ao)
                recorded = {}
                for k, names in pools.items():
                    if not all(name in clips for name in names):
                        continue
                    pool = []
                    for name in names:
                        i, a = clips[name]
                        contact = first_contact(a["events"])
                        pool.append({"animation": name, "index": i, "length": a["length"],
                                     "contact": None if contact is None else contact[0],
                                     "position": None if contact is None else contact[1]})
                    recorded[k] = pool
                if recorded:
                    entry = {"act": act, "ao": ao, "amd": amd, "pools": recorded}
                    break
            cache[key] = entry
        if cache[key]:
            out[row["Id"]] = cache[key]
            pools_written += len(cache[key]["pools"])
        if n % 2000 == 0:
            print(f"  {n}/{len(variants)}", file=sys.stderr, flush=True)
    with open(dest + ".tmp", "w", encoding="utf-8") as f:
        json.dump(out, f, indent=1, sort_keys=True)
    os.replace(dest + ".tmp", dest)   # never leave a truncated file for the Gateway
    print(f"wrote {dest}: {len(out)} of {len(variants)} monster varieties have a pool ({pools_written} pools)",
          file=sys.stderr, flush=True)

    # The stanced .acts, with the .ao each animates: its own animated_object, else its first variety's.
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
                hits = [ms for e, ms, _ in a["events"] if e == event]
                pool.append({"animation": name, "index": i, "length": a["length"], "contact": min(hits) if hits else None})
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
