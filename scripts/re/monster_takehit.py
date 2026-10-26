# usage: python -I scripts/re/monster_takehit.py [--json data/monster-takehit.json]
#
# For every MonsterVarieties row: the indexes of its TakeHit clips in the rig's animation list, and their lengths; and
# its Emerge clip ("emerge": index and length), for monsters that rise from the ground.
# The client's S->C 0x1b2 "play animation" takes an index into the actor's animation list, and that list is the rig
# .amd's animations in file order (0-based). Live 2026-10-25 on the Sanctum zombie: index 156 = flinch_unarmed and
# 151 = flinch_pitchfork played the flinch *with its SoundEvent*, both at the position of the clip in
# Art/Models/MONSTERS/skeleton/rig.amd.
#
# Chain: MonsterVarieties.ACTFiles (.act maps Animation type -> clip, and names the .ao) and AOFiles; the .ao's
# "extends" chain holds "AnimationController { metadata = <rig>.amd }". Files come from the GGPK through
# scripts/re/ggdump (cached in Tools/re/out/ot, see scripts/re/ot_components.py). Everything is fetched in batches
# first: one ggdump process per file was minutes per hundred rows.
import json, os, re, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ot_components import REPO, amd_animations, amd_map, animation_controller, extends_of, fetch, read_text  # noqa: E402


def fetch_ao_chain(paths):
    """Fetch these .ao files and every "extends" parent; return all .ao paths seen."""
    known, todo = set(), set(paths)
    while todo:
        fetch(sorted(todo))
        known |= todo
        nxt = set()
        for ao in todo:
            text = read_text(ao)
            if text is None or animation_controller(text):
                continue
            for parent in extends_of(text):
                p = parent if parent.endswith(".ao") else parent + ".ao"
                if p not in known:
                    nxt.add(p)
        todo = nxt
    return known


def expand_clips(value, entries, seen):
    """The clips a TakeHit value names: a comma list whose items may be "@Name" aliases to another such list.

    A variant's condition ("flinch_small:max_duration=0.45") is stripped: it picks the small/large reaction by the
    hit's stun duration, which the server does not model.
    """
    clips = []
    for item in value.split(","):
        clip = item.split(":", 1)[0].strip()
        if not clip:
            continue
        if clip.startswith("@"):
            if clip[1:] and clip[1:] not in seen:
                clips += expand_clips(entries.get(clip[1:]) or "", entries, seen | {clip[1:]})
        elif clip not in clips:
            clips.append(clip)
    return clips


def act_takehit(act):
    """The clips an .act maps TakeHit to ("@Name" aliases and "clip:condition" variants resolved), the .ao it animates,
    and the first clip of its Emerge animation (None without one)."""
    text = read_text(act)
    if text is None:
        return [], None, None
    entries = {m.group(1): m.group(2).strip() for m in re.finditer(r'^\s*([A-Za-z0-9_]+)\s*=\s*"([^"]*)"\s*;', text, re.M)}
    clips = expand_clips(entries.get("TakeHit") or "", entries, set())
    m = re.search(r'^\s*animated_object\s+"([^"]+)"', text, re.M)
    # Emerge (the basic action of monsters that rise from the ground, 10-chat-and-commands.md section 5) may carry an
    # effect after the clip: Emerge = "grasp_emerge" effect_detached "...";
    e = re.search(r'^\s*Emerge\s*=\s*"([^"]*)"', text, re.M)
    emerge = (expand_clips(e.group(1), entries, set()) or [None])[0] if e else None
    return clips, (m.group(1) if m else None), emerge


def main():
    dest = sys.argv[sys.argv.index("--json") + 1] if "--json" in sys.argv else os.path.join(REPO, "data", "monster-takehit.json")
    rows = json.load(open(os.path.join(REPO, "data", "tables", "MonsterVarieties.json"), encoding="utf-8"))
    variants = [r for r in rows if isinstance(r, dict) and r.get("Id")]

    # One batch per kind of file: the per-file fetch opened the 69 GB GGPK for every row before.
    acts = sorted({a for r in variants for a in (r.get("ACTFiles") or [])})
    aos = sorted({a for r in variants for a in (r.get("AOFiles") or [])})
    print(f"fetching {len(acts)} .act and {len(aos)} .ao", file=sys.stderr, flush=True)
    fetch(acts)
    fetch(aos)
    # The .act's own animated_object is the model that act is for; MonsterVarieties.AOFiles is the fallback (a
    # variety can list several weapon variants, only one of which the type uses).
    act_aos = set()
    for act in acts:
        m = re.search(r'^\s*animated_object\s+"([^"]+)"', read_text(act) or "", re.M)
        if m:
            act_aos.add(m.group(1))
    print(f"fetching the .ao extends chain ({len(act_aos)} from the acts)", file=sys.stderr, flush=True)
    all_aos = fetch_ao_chain(aos + sorted(act_aos))
    # One ao -> amd map for everything fetched, and the .amd files themselves in one batch: amd_of was walking the
    # chains (and fetching each file) again for every row before.
    amds = amd_map(all_aos, fetch_missing=False)
    fetch(sorted({amd for amd in amds.values() if amd}))
    print(f"fetched; indexing {len(variants)} monster varieties", file=sys.stderr, flush=True)

    # Parsed once per rig and per .act: thousands of varieties share a handful of rigs.
    rigs, act_clips = {}, {}

    def rig(amd):
        if amd not in rigs:
            anims = {}
            for i, a in enumerate(amd_animations(amd, fetch_missing=False) or []):
                anims.setdefault(a["name"], (i, a["length"]))   # a repeated name: the first one, as names.index did
            rigs[amd] = anims
        return rigs[amd]

    cache, out = {}, {}
    for n, row in enumerate(variants, 1):
        key = (tuple(row.get("ACTFiles") or []), tuple(row.get("AOFiles") or []))
        if key not in cache:
            index, emerge = None, None
            for act in key[0]:
                if act not in act_clips:
                    act_clips[act] = act_takehit(act)
                clips, act_ao, emerge_clip = act_clips[act]
                for ao in ([act_ao] if act_ao else []) + list(key[1]):
                    amd = amds.get(ao)
                    anims = rig(amd) if amd else {}
                    found = [c for c in clips if c in anims]
                    if found and not index:
                        index = ([anims[c][0] for c in found], [anims[c][1] for c in found], found, amd, act)
                    if emerge_clip in anims and not emerge:
                        emerge = {"index": anims[emerge_clip][0], "length": anims[emerge_clip][1], "clip": emerge_clip}
                if index and (emerge or not emerge_clip):
                    break
            cache[key] = (index, emerge)
        index, emerge = cache[key]
        entry = {}
        if index:
            indexes, lengths, clips, amd, act = index
            # lengths: each clip's length in ms (the Gateway holds the next flinch until the clip has played).
            entry = {"index": indexes[0], "indexes": indexes, "lengths": lengths, "clips": clips, "amd": amd, "act": act}
        if emerge:
            # The Emerge clip (index in the rig, length in ms): how long a burrowed monster takes to rise
            # (MonsterEmerge.cs).
            entry["emerge"] = emerge
        if entry:
            out[row["Id"]] = entry
        if n % 1000 == 0:
            print(f"  {n}/{len(variants)}", file=sys.stderr, flush=True)
    with open(dest, "w", encoding="utf-8") as f:
        json.dump(out, f, indent=1, sort_keys=True)
    print(f"wrote {dest}: {sum('index' in e for e in out.values())} of {len(variants)} monster varieties have a TakeHit "
          f"index, {sum('emerge' in e for e in out.values())} an Emerge clip", file=sys.stderr, flush=True)


if __name__ == "__main__":
    main()
