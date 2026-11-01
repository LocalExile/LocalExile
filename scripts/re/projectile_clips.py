# For every Projectiles.dat row: what the client needs to show a server-created object of that projectile type. The
# row's first .ao names a rig (.amd, through the .ao's "extends" chain); the rig's animation list gives the index of
# the row's flight clip (LoopAnimationIds[0]) and impact clip (ImpactAnimationIds[0]) and their lengths; the .ao chain
# may declare a default_animation the object plays by itself. A server object of a projectile type plays nothing
# otherwise (the client's Projectile component is inert for it, 11-skills.md): the wand bolt's .ao has
# default_animation = projectile_01 and flies visibly, the Sand Spitter mortar's has none, so without a 0x1b2 for its
# "proj" clip the object was invisible (live 2026-10-29). Files come from the GGPK through scripts/re/ggdump
# (ot_components.fetch, cached under Tools/re/out). Writes data/projectile-clips.json {Projectiles Id: entry}.
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ot_components import REPO, amd_animations, amd_map, extends_of, fetch, read_text  # noqa: E402

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
    tables = os.path.join(REPO, "data", "tables")
    dest = os.path.join(REPO, "data", "projectile-clips.json")
    with open(os.path.join(tables, "Projectiles.json"), encoding="utf-8") as f:
        rows = json.load(f)
    aos = sorted({row["AOFiles"][0] for row in rows if row.get("AOFiles")})
    print(f"fetching {len(aos)} .ao and their extends chains", file=sys.stderr, flush=True)
    fetch_ao_chain(aos)
    amds = amd_map(aos, fetch_missing=False)
    rig_paths = sorted({a for a in amds.values() if a})
    print(f"fetching {len(rig_paths)} .amd", file=sys.stderr, flush=True)
    fetch(rig_paths)

    rigs = {}

    def rig(amd):
        if amd not in rigs:
            anims = {}
            for i, a in enumerate(amd_animations(amd, fetch_missing=False) or []):
                anims.setdefault(a["name"], {"index": i, "ms": a["length"], "once": a["once"]})   # a repeated name: the first
            rigs[amd] = anims
        return rigs[amd]

    out = {}
    for row in rows:
        if not row.get("AOFiles"):
            continue
        ao = row["AOFiles"][0]
        amd = amds.get(ao)
        anims = rig(amd) if amd else {}
        entry = {"ao": ao}
        if amd:
            entry["amd"] = amd
        if (default := default_animation(ao)) is not None:
            entry["default"] = default
        for key, ids in (("flight", row.get("LoopAnimationIds")), ("impact", row.get("ImpactAnimationIds"))):
            name = (ids or [None])[0]
            if name and name in anims:
                entry[key] = {"name": name, **anims[name]}
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
