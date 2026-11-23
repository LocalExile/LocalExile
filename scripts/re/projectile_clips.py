# For every Projectiles.dat row: what the client needs to show a server-created object of that projectile type. The
# row's first .ao names a rig (.amd, through the .ao's "extends" chain); the rig's animation list gives the index of
# the row's flight clip (LoopAnimationIds[0]) and impact clip (ImpactAnimationIds[0]) and their lengths; the .ao chain
# may declare a default_animation the object plays by itself. A server object of a projectile type plays nothing
# otherwise (the client's Projectile component is inert for it, 11-skills.md): the wand bolt's .ao has
# default_animation = projectile_01 and flies visibly, the Sand Spitter mortar's has none, so without a 0x1b2 for its
# "proj" clip the object was invisible (live 2026-10-29). The flight clip's "contact" also times a spell mortar: the
# client lands it that long after its release over its speed multiplier (mover 0x141a13760); a clip without one ends at
# its length (the client's second lookup, hash 0, finds the record that ends every clip). A flight name may be an .ao
# view of a clip (anim_contacts.ao_views: the Sanctum candles' animate_01..10, the first 830 ms of "animate"), a group
# of clips the client picks one of by its seed (the Anger beam mortars' mortar_01..04), or a name the .ao does not
# define, which plays its default animation (the Monolithic Slug's); "varies" marks a flight whose candidates land at
# different contacts. Files come from the GGPK through scripts/re/ggdump (ot_components.fetch, cached under
# Tools/re/out). Writes data/projectile-clips.json {Projectiles Id: entry}.
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ot_components import REPO, amd_map, extends_of, fetch, first_contact, read_text, rig_clips  # noqa: E402
from anim_contacts import ao_views  # noqa: E402
import clientdb  # noqa: E402  (on the path through ot_components)

GROUP = re.compile(r'group\s*=\s*"([^"]+)"((?:\s*\n\s*group_entry\s*=\s*"[^"]*")*)')


def ao_groups(ao):
    """{group name: [clip, ...]}: the groups the .ao chain defines (`group = "x"` and its `group_entry = "clip weight"`
    lines), the first definition along the chain winning."""
    out, seen, todo = {}, set(), [ao]
    while todo:
        path = todo.pop(0)
        if not path or path in seen:
            continue
        seen.add(path)
        text = read_text(path)
        if text is None:
            continue
        for group in GROUP.finditer(text.replace("\r", "")):
            name, entries = group.groups()
            out.setdefault(name, [e.split()[0] for e in re.findall(r'group_entry\s*=\s*"([^"]*)"', entries) if e.split()])
        todo.extend(p if p.endswith(".ao") else p + ".ao" for p in extends_of(text))
    return out

DEFAULT = re.compile(r'default_animation\s*=\s*"([^"]+)"')


def fetch_ao_chain(paths):
    """Fetch these .ao files and every "extends" parent, a whole level per ggdump run; return all paths seen."""
    seen, todo = set(), list(paths)
    while todo:
        todo = [p for p in todo if p not in seen]
        if not todo:
            break
        fetch(todo)
        seen.update(todo)
        parents = []
        for p in todo:
            for parent in extends_of(read_text(p)):
                ao = parent if parent.endswith(".ao") else parent + ".ao"
                if ao not in seen:
                    parents.append(ao)
        todo = parents
    return seen


def default_animation(ao):
    """The first default_animation along the .ao's extends chain, or None."""
    seen, todo = set(), [ao]
    while todo:
        path = todo.pop(0)
        if path in seen:
            continue
        seen.add(path)
        text = read_text(path)
        if text is None:
            continue
        if (m := DEFAULT.search(text)) is not None:
            return m.group(1)
        todo.extend(p if p.endswith(".ao") else p + ".ao" for p in extends_of(text))
    return None


def main():
    dest = os.path.join(REPO, "data", "projectile-clips.json")
    rows = clientdb.table("Projectiles")
    aos = sorted({row["AOFiles"][0] for row in rows if row.get("AOFiles")})
    print(f"fetching {len(aos)} .ao and their extends chains", file=sys.stderr, flush=True)
    fetch_ao_chain(aos)
    amds = amd_map(aos, fetch_missing=False)
    rig_paths = sorted({a for a in amds.values() if a})
    print(f"fetching {len(rig_paths)} .amd", file=sys.stderr, flush=True)
    fetch(rig_paths)

    rigs = {}

    def rig(amd, ao):
        # The rig's clips with the events the client ends up with for this .ao (rig_clips: the .ao chain may add or drop them).
        if (amd, ao) not in rigs:
            anims = {}
            for i, a in enumerate(rig_clips(amd, ao, fetch_missing=False) or []):
                contact = first_contact(a["events"])
                anims.setdefault(a["name"], {"index": i, "ms": a["length"], "once": a["once"],    # a repeated name: the first
                                             "contact": contact[0] if contact else a["length"], "events": a["events"]})
            rigs[(amd, ao)] = anims
        return rigs[(amd, ao)]

    out = {}
    for row in rows:
        if not row.get("AOFiles"):
            continue
        ao = row["AOFiles"][0]
        amd = amds.get(ao)
        anims = rig(amd, ao) if amd else {}
        entry = {"ao": ao}
        if amd:
            entry["amd"] = amd
        if (default := default_animation(ao)) is not None:
            entry["default"] = default
        views, groups = (ao_views(ao), ao_groups(ao)) if amd else ({}, {})

        def resolve(name, depth=0):
            """The clips a flight name plays: a rig clip, a view of one (its events within the view, from its start), a
            group's entries, or for an unknown name the .ao's default animation."""
            if name in anims:
                return [(name, anims[name])]
            if name in views:
                source, begin, end = views[name]
                if source not in anims:
                    return []
                src = anims[source]
                end = src["ms"] if end is None else min(end, src["ms"])
                inside = [(e, int(t) - begin, p) for e, t, p in src["events"] if begin <= int(t) <= end]
                contact = first_contact(inside)
                return [(name, {"index": src["index"], "ms": end - begin, "once": src["once"],
                                "contact": contact[0] if contact else end - begin})]
            if depth < 3 and name in groups:
                return [c for entry in groups[name] for c in resolve(entry, depth + 1)]
            if depth == 0 and entry.get("default") and entry["default"] != name:
                return resolve(entry["default"], depth + 1)
            return []
        for key, ids in (("flight", row.get("LoopAnimationIds")), ("impact", row.get("ImpactAnimationIds"))):
            names = [n for n in (ids or []) if n]
            if not names:
                continue
            if key == "flight":
                # Every clip the client may fly it with: the row's names, each resolved.
                options = [c for n in names for c in resolve(n)]
                if options:
                    name, first = options[0]
                    clip = {k: v for k, v in first.items() if k != "events"}
                    if len({c["contact"] for _, c in options}) > 1:
                        clip["varies"] = True
                    entry[key] = {"name": name, **clip}
            elif names[0] in anims:
                entry[key] = {"name": names[0], **{k: v for k, v in anims[names[0]].items() if k not in ("contact", "events")}}
        if len(entry) > 1:
            out[row["Id"]] = entry
    with open(dest + ".tmp", "w", encoding="utf-8") as f:
        json.dump(out, f, indent=1, sort_keys=True)
    os.replace(dest + ".tmp", dest)   # never leave a truncated file for the Gateway
    flights = sum(1 for e in out.values() if "flight" in e)
    impacts = sum(1 for e in out.values() if "impact" in e)
    defaults = sum(1 for e in out.values() if "default" in e)
    print(f"wrote {dest}: {len(out)} projectiles, {flights} with a flight clip, {impacts} with an impact clip, "
          f"{defaults} with a default animation", file=sys.stderr)


if __name__ == "__main__":
    main()
