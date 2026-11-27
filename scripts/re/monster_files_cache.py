# usage: python -I scripts/re/monster_files_cache.py [--cached-only] [--out <folder>] [--limit N]
#
# Local client file cache for the monster skill work: the .act, .ao, .ot and .otc files of every MonsterVarieties row,
# plus what they reference (each .ao's `extends` chain and its rig .amd, each .ot's `extends` chain and its .otc), read
# from the client folder through scripts/re/ggdump (read-only, batched, works while the client runs) and written flat
# (path with "/" as "_", the layout of ot_components.py) under Tools/re/out/monster-files/. That folder is git-ignored
# (/Tools/*); never commit it. A file the client does not have leaves a .missing marker. A rerun fetches only what is
# not there yet. --limit N fetches only the first N .act files (a short test run); --cached-only fetches nothing.
# monster_skill_coverage.py reads this cache for its .act and shared-state columns.
import os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ot_components as oc  # noqa: E402
import clientdb  # noqa: E402

CACHE = os.path.join(oc.REPO, "Tools", "re", "out", "monster-files")


def want():
    """-> (acts, aos, types): the files the varieties name, and their object type paths (Id)."""
    varieties = [r for r in clientdb.table("MonsterVarieties") if isinstance(r, dict) and r.get("Id")]
    acts = sorted({a for r in varieties for a in (r.get("ACTFiles") or [])})
    aos = sorted({a for r in varieties for a in (r.get("AOFiles") or [])})
    return acts, aos, sorted({r["Id"] for r in varieties})


def main():
    args = sys.argv[1:]
    folder = args[args.index("--out") + 1] if "--out" in args else CACHE
    limit = int(args[args.index("--limit") + 1]) if "--limit" in args else 0
    oc.CACHE = folder  # fetch / read_text of ot_components use this folder
    acts, aos, types = want()
    print(f"{len(acts)} .act, {len(aos)} .ao, {len(types)} object types -> {folder}", file=sys.stderr, flush=True)
    if "--cached-only" not in args:
        if not os.path.isdir(os.path.join(oc.REPO, "Tools", "ooz", "node_modules")) and "Game_GGDUMP_EXE" not in os.environ:
            sys.exit("Tools/ooz/node_modules is missing (ggdump's Oodle decoder): run `npm install` in Tools/ooz "
                     "(a worktree can link the main checkout's folder).")
        if limit:
            oc.fetch(acts[:limit])
        else:
            from monster_contacts import fetch_ao_chain
            oc.fetch(acts)
            all_aos = fetch_ao_chain(aos)  # the .ao files and every `extends` parent
            oc.fetch(sorted({m for m in oc.amd_map(sorted(all_aos)).values() if m}))
            oc.load(types)  # each .ot and .otc, and the .ot extends chains
    n = sum(1 for f in os.listdir(folder) if not f.endswith(".missing")) if os.path.isdir(folder) else 0
    print(f"cache holds {n} files", file=sys.stderr)


if __name__ == "__main__":
    main()
