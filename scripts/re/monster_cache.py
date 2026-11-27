#!/usr/bin/env python3
"""Read-only cache builder & reader for monster .act/.ot/.ao/.amd assets (PR #1618 S0).

Usage:
    python -I scripts/re/monster_cache.py [--cache Tools/re/out/act-ot-cache] [--force]

Extracts all .act/.ot/.otc/.amd files in bundle order (via scripts/re/ggdump) plus every
.ao referenced by MonsterVarieties.AOFiles, monster .act animated_object, and monster .ot
Animated.animated_object (and their full extends chains) into Tools/re/out/act-ot-cache/
(gitignored). Strictly read-only on Client/ and data/client.db; never runs at server startup.
"""
import argparse
import json
import os
import re
import shutil
import sqlite3
import subprocess
import sys
import time

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_CACHE = os.path.join(REPO, "Tools", "re", "out", "act-ot-cache")
CLIENT_DIR = os.path.join(REPO, "Client")
GGDUMP_PROJ = os.path.join(REPO, "scripts", "re", "ggdump")

sys.path.append(os.path.join(REPO, "scripts", "dev"))
import clientdb  # noqa: E402

EXTENDS_RE = re.compile(r'^\s*extends\s+(.+)$', re.M)
QUOTED_RE = re.compile(r'"([^"]+)"')
ACT_AO_RE = re.compile(r'^\s*animated_object\s+"([^"]+)"', re.M)
OT_AO_RE = re.compile(r'\banimated_object\s*=\s*"([^"]+)"')


def norm_path(path: str) -> str:
    return path.replace("\\", "/").lstrip("/").lower()


def cache_path(path: str, cache_dir: str = DEFAULT_CACHE) -> str:
    p = os.path.join(cache_dir, norm_path(path))
    return "\\\\?\\" + p if os.name == "nt" else p


def exists(path: str, cache_dir: str = DEFAULT_CACHE) -> bool:
    return os.path.isfile(cache_path(path, cache_dir))


def read_bytes(path: str, cache_dir: str = DEFAULT_CACHE):
    p = cache_path(path, cache_dir)
    if not os.path.isfile(p):
        return None
    with open(p, "rb") as f:
        return f.read()


def decode_game_text(b: bytes) -> str:
    if b[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return b.decode("utf-16")
    # .amd and some .ao/.act/.ot files are UTF-16LE without BOM.
    if len(b) > 3 and b[1] == 0 and b[3] == 0:
        return b.decode("utf-16-le")
    return b.decode("utf-8", "replace")


def read_text(path: str, cache_dir: str = DEFAULT_CACHE):
    b = read_bytes(path, cache_dir)
    return None if b is None else decode_game_text(b)


def _ggdump_cmd(*args):
    if "Game_GGDUMP_EXE" in os.environ:
        return [os.environ["Game_GGDUMP_EXE"], *args]
    dll = os.path.join(GGDUMP_PROJ, "bin", "Debug", "net10.0", "GgDump.dll")
    if os.path.isfile(dll):
        return ["dotnet", dll, *args]
    return ["dotnet", "run", "--project", GGDUMP_PROJ, "--", *args]


def _extends_of(text: str):
    out = []
    for m in EXTENDS_RE.finditer(text or ""):
        for q in QUOTED_RE.findall(m.group(1)):
            if q != "nothing" and q not in out:
                out.append(q)
    return out


def _load_bundle_order(cache_dir: str, force: bool = False):
    plan_dir = os.path.join(cache_dir, "_plan")
    paths_tsv = os.path.join(plan_dir, "paths.tsv")
    if force or not os.path.isfile(paths_tsv):
        os.makedirs(plan_dir, exist_ok=True)
        subprocess.run(_ggdump_cmd(CLIENT_DIR, plan_dir, "plan"), check=True)
    order = {}
    with open(paths_tsv, encoding="utf-8") as f:
        next(f)
        for line in f:
            p, b, off, sz = line.rstrip("\n").split("\t")
            order[p.lower()] = (int(b), int(off), int(sz))
    return order


def _extract_tree_regex(cache_dir: str, include_re: str, exclude_re: str = "^$", min_free_gb: str = "2"):
    subprocess.run(
        _ggdump_cmd(CLIENT_DIR, cache_dir, "extract", include_re, exclude_re, min_free_gb),
        check=True,
    )


def _extract_specific(paths, bundle_order, cache_dir: str):
    todo, missing = [], []
    for raw in sorted({norm_path(p) for p in paths if p}):
        if exists(raw, cache_dir):
            continue
        if raw in bundle_order:
            todo.append(raw)
        else:
            missing.append(raw)
    if not todo:
        return 0, missing
    todo.sort(key=lambda p: (bundle_order[p][0], bundle_order[p][1]))
    tmp_dir = os.path.join(cache_dir, "_staging")
    os.makedirs(tmp_dir, exist_ok=True)
    batches, cur, cur_len = [], [], 0
    for p in todo:
        if cur and cur_len + len(p) + 2 > 24000:
            batches.append(cur)
            cur, cur_len = [], 0
        cur.append(p)
        cur_len += len(p) + 2
    if cur:
        batches.append(cur)
    written = 0
    for batch in batches:
        subprocess.run(_ggdump_cmd(CLIENT_DIR, tmp_dir, *batch), check=True, stdout=subprocess.DEVNULL)
        for p in batch:
            flat = os.path.join(tmp_dir, p.replace("/", "_"))
            if os.path.isfile(flat):
                dst = cache_path(p, cache_dir)
                os.makedirs(os.path.dirname(dst), exist_ok=True)
                shutil.move(flat, dst)
                written += 1
            else:
                missing.append(p)
    shutil.rmtree(tmp_dir, ignore_errors=True)
    return written, sorted(set(missing))


def ensure_cache(cache_dir: str = DEFAULT_CACHE, force: bool = False):
    """Build or update the read-only monster asset cache; returns its manifest dict."""
    manifest_path = os.path.join(cache_dir, "manifest.json")
    if not force and os.path.isfile(manifest_path):
        with open(manifest_path, encoding="utf-8") as f:
            return json.load(f)

    t0 = time.time()
    os.makedirs(cache_dir, exist_ok=True)
    bundle_order = _load_bundle_order(cache_dir, force=force)
    _extract_tree_regex(cache_dir, r"\.(act|ot|otc|amd)$")

    db = clientdb.connect()
    mvs = db.execute("SELECT Id, ACTFiles, AOFiles, BaseMonsterTypeIndex FROM MonsterVarieties ORDER BY _row").fetchall()
    ao_seeds, act_paths, ot_roots = set(), set(), set()
    for vid, af, aof, base in mvs:
        if vid:
            ot_roots.add(vid)
        if base:
            ot_roots.add(base)
        for a in json.loads(af or "[]"):
            if a:
                act_paths.add(a)
        for ao in json.loads(aof or "[]"):
            if ao:
                ao_seeds.add(ao)
    for act in act_paths:
        txt = read_text(act, cache_dir)
        if txt and (m := ACT_AO_RE.search(txt)):
            ao_seeds.add(m.group(1))
    seen_ot, todo_ot = set(), list(ot_roots)
    while todo_ot:
        p = todo_ot.pop()
        np = norm_path(p if p.lower().endswith(".ot") else p + ".ot")
        if np in seen_ot:
            continue
        seen_ot.add(np)
        txt = read_text(np, cache_dir)
        if not txt:
            continue
        for m in OT_AO_RE.finditer(txt):
            ao_seeds.add(m.group(1))
        todo_ot.extend(_extends_of(txt))

    seen_ao, missing_ao = set(), set()
    frontier = sorted({norm_path(a if a.lower().endswith(".ao") else a + ".ao") for a in ao_seeds})
    depth = 0
    while frontier:
        depth += 1
        batch = [p for p in frontier if p not in seen_ao]
        if not batch:
            break
        _, miss = _extract_specific(batch, bundle_order, cache_dir)
        missing_ao.update(miss)
        seen_ao.update(batch)
        nxt = []
        for p in batch:
            txt = read_text(p, cache_dir)
            if not txt:
                continue
            for par in _extends_of(txt):
                pa = norm_path(par if par.lower().endswith(".ao") else par + ".ao")
                if pa not in seen_ao:
                    nxt.append(pa)
        frontier = sorted(set(nxt))

    counts = {".act": 0, ".ot": 0, ".ao": 0, ".amd": 0}
    total_bytes = 0
    for root, _, files in os.walk(cache_dir):
        if os.path.basename(root) in ("_plan", "_staging", "__pycache__"):
            continue
        for fn in files:
            ext = os.path.splitext(fn)[1].lower()
            if ext in counts:
                counts[ext] += 1
                total_bytes += os.path.getsize(os.path.join(root, fn))

    manifest = {
        "cache_dir": cache_dir,
        "elapsed_seconds": round(time.time() - t0, 2),
        "file_counts": counts,
        "total_mb": round(total_bytes / (1024 * 1024), 2),
        "monster_act_refs": len(act_paths),
        "monster_ot_walked": len(seen_ot),
        "monster_ao_chain_total": len(seen_ao),
        "monster_ao_chain_depth": depth,
        "missing_ao_count": len(missing_ao),
    }
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
    return manifest


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--cache", default=DEFAULT_CACHE, help="Cache output directory (default: Tools/re/out/act-ot-cache)")
    ap.add_argument("--force", action="store_true", help="Rebuild cache even if manifest.json exists")
    args = ap.parse_args()
    manifest = ensure_cache(args.cache, force=args.force)
    print(f"monster_cache: ready at {manifest['cache_dir']} ({manifest['file_counts']}, "
          f"{manifest['total_mb']} MB, missing_ao={manifest['missing_ao_count']})", file=sys.stderr)


if __name__ == "__main__":
    main()
