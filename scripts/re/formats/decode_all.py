# usage: decode_all.py [--ext .ot,.pet] [--no-write] [--fails N]
#
# Decodes every custom script and format under Tools/re/out/full/content/files into Tools/re/out/full/decoded (JSON
# with the same relative path plus .json; GEAL scripts also as a readable .geal). Writes decoded/REPORT.md. Read-only
# on the extracted files. A format with failures is a bug here, or something the client treats specially.
import argparse
import collections
import json
import multiprocessing as mp
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import binfmt  # noqa: E402
import geal  # noqa: E402
import objfile  # noqa: E402
import textfmt  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
ROOT = os.path.join(REPO, "Tools", "re", "out", "full", "content", "files")
OUT = os.path.join(REPO, "Tools", "re", "out", "full", "decoded")
OBJECT = {".ot", ".otc", ".ao", ".aoc", ".it"}
JSON_TEXT = {".mat", ".fxgraph", ".env", ".atl", ".hideout", ".json", ".texprops", ".mat - copy", ".mat_2"}
# Standard formats or plain source: nothing custom to decode.
PASSTHROUGH = {".ogg", ".png", ".jpg", ".bmp", ".ttf", ".xls", ".lnk", ".tsv", ".hlsl", ".h", ".inc", ".txt", ".xml", ".mb"}
DAT = {".datc64", ".datcl64"}


def long_path(p):
    return "\\\\?\\" + p if os.name == "nt" and not p.startswith("\\\\?\\") else p


def decode_text(b):
    if b[:2] == b"\xff\xfe":
        return b.decode("utf-16")
    if b[:3] == b"\xef\xbb\xbf":
        return b.decode("utf-8-sig")
    if len(b) > 1 and b[1] == 0:
        return b.decode("utf-16-le")
    return b.decode("utf-8", "replace")


def object_file(text, path, write):
    tree = objfile.parse(text)
    original = objfile.source_tokens(text)
    scripts = list(objfile.scripts_of(tree))
    if len(original) != len(scripts):
        raise ValueError(f"{len(original)} scripts in the file, {len(scripts)} parsed")
    for o, (key, s) in zip(original, scripts):
        if geal.tokenize(geal.source(s)) != o:
            raise ValueError(f"script {key} does not print back to its tokens")
    if write and scripts:
        target = path + ".geal"
        with open(long_path(target), "w", encoding="utf-8") as f:
            for key, s in scripts:
                f.write(f"{key} = {{\n{geal.source(s)}}}\n\n")
    return tree, len(scripts), scripts


def one(job):
    rel, write = job
    ext = os.path.splitext(rel)[1].lower()
    p = os.path.join(ROOT, rel)
    result = {"ext": ext, "rel": rel, "error": None, "scripts": 0, "calls": collections.Counter(), "size": 0, "partial": False}
    try:
        b = open(long_path(p), "rb").read()
        result["size"] = len(b)
        target = os.path.join(OUT, rel)
        if write:
            os.makedirs(long_path(os.path.dirname(target)), exist_ok=True)
        if ext == "" and rel.startswith("cachedhlslshaders"):
            tree = binfmt.compiled_shader(b)
        elif ext in OBJECT or ext == "":  # the one other file without an extension is an object file saved as "version 2"
            tree, n, scripts = object_file(decode_text(b), target, write)
            result["scripts"] = n
            for _, s in scripts:
                for node in geal.walk(s):
                    if node["t"] == "call":
                        fn = node["fn"]
                        result["calls"][fn.get("v") or fn.get("name") or "?"] += 1
        elif ext in JSON_TEXT:
            if not b.strip(b"\xff\xfe\xef\xbb\xbf\r\n\t \0"):
                tree = None  # some .mat files are empty
            else:
                tree = textfmt.loads_relaxed(decode_text(b))
            write_json = False  # already JSON; the copy would only double the disk use
            return result if not write_json else result
        elif ext in DAT:
            tree = binfmt.dat_table(os.path.splitext(os.path.basename(rel))[0].lower(), b)
            write = False  # a summary per language adds nothing; the rows are in data/tables
        elif ext in textfmt.PARSERS:
            tree = textfmt.PARSERS[ext](decode_text(b))
        elif ext in binfmt.PARSERS or rel.endswith(".header"):
            tree = binfmt.parse(rel, b)
            result["partial"] = tree.get("undecoded_bytes", 0) > 0
        else:
            result["error"] = "no decoder"
            return result
        if write:
            with open(long_path(target + ".json"), "w", encoding="utf-8") as f:
                json.dump(tree, f, ensure_ascii=False)
    except RecursionError:
        result["error"] = "RecursionError: nesting too deep"
    except Exception as x:  # every failure goes in the report
        result["error"] = f"{type(x).__name__}: {x}"
    return result


def write_function_table(calls):
    """geal-functions.json: the built-ins named in the client's RTTI (GEAL::<anonymous>::Name) and how often scripts call
    them. A name scripts call that the client doesn't have is server-side, a script's own function, or a variable."""
    import sqlite3
    db = os.path.join(REPO, "Tools", "re", "out", "full", "game", "index.sqlite")
    names = set()
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    for (v,) in con.execute("select value from strings where value like '%GEAL%'"):
        names.update(re.findall(r"\?\?\$?\??(\w+?)@\?A0x[0-9a-f]+@GEAL@@", v))
        m = re.match(r"GEAL::`anonymous-namespace'::(\w+)$", v)
        if m:
            names.add(m.group(1))
    table = [{"name": n, "in_client": n in names, "script_calls": calls.get(n, 0)} for n in sorted(names | set(calls))]
    with open(os.path.join(OUT, "geal-functions.json"), "w", encoding="utf-8") as f:
        json.dump(table, f, indent=0)
    print(f"{len(names)} built-ins in the client; {sum(1 for x in table if x['in_client'] and x['script_calls'])} of them "
          f"called by scripts; {sum(1 for x in table if not x['in_client'])} called names not in the client")


def key_of(ext):
    return ".header" if ext == ".header" else ext


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ext", default="")
    ap.add_argument("--no-write", action="store_true")
    ap.add_argument("--fails", type=int, default=0)
    ap.add_argument("--jobs", type=int, default=8)
    a = ap.parse_args()
    only = set(a.ext.split(",")) if a.ext else None
    started = time.time()
    jobs = []
    skipped = collections.Counter()
    for r, _, files in os.walk(ROOT):
        for n in files:
            ext = os.path.splitext(n)[1].lower()
            if only and ext not in only:
                continue
            if ext in PASSTHROUGH:
                skipped[ext] += 1
                continue
            jobs.append((os.path.relpath(os.path.join(r, n), ROOT), not a.no_write))
    print(f"{len(jobs)} files", flush=True)
    ok, bad, partial, scripts = collections.Counter(), collections.Counter(), collections.Counter(), 0
    calls = collections.Counter()
    errors = collections.defaultdict(list)
    with mp.Pool(a.jobs) as pool:
        for i, res in enumerate(pool.imap_unordered(one, jobs, 100)):
            k = key_of(res["ext"])
            if res["error"]:
                bad[k] += 1
                errors[k].append((res["rel"], res["error"]))
            elif res["partial"]:
                partial[k] += 1
            else:
                ok[k] += 1
            scripts += res["scripts"]
            calls.update(res["calls"])
            if i % 50000 == 0 and i:
                print(i, flush=True)
    formats = sorted(set(ok) | set(bad) | set(partial), key=lambda x: -(ok[x] + bad[x] + partial[x]))
    for k in formats:
        line = f"{k or '(none)':<14} ok {ok[k]:>7}  partial {partial[k]:>7}  failed {bad[k]:>6}"
        if bad[k]:
            groups = collections.Counter(re.sub(r"\d+|\"[^\"]*\"|'[^']*'", "_", e)[:60] for _, e in errors[k])
            line += "  " + "; ".join(f"{v}x {g}" for g, v in groups.most_common(2))
        print(line)
    print(f"scripts {scripts}, built-ins used {len(calls)}, {time.time() - started:.0f} s")
    shown = 0
    for k, es in errors.items():
        for rel, e in es[:a.fails]:
            print(f"  {rel}: {e[:300]}")
            shown += 1
    if not a.no_write and not only:
        os.makedirs(OUT, exist_ok=True)
        with open(os.path.join(OUT, "REPORT.md"), "w", encoding="utf-8") as f:
            f.write("# Decoded game formats\n\nPartial: the file's structure is read, but a payload in it is not "
                    "(`undecoded` in its JSON says which).\n\n| Format | Decoded | Partial | Failed |\n|---|---:|---:|---:|\n")
            for k in formats:
                f.write(f"| {k} | {ok[k]:,} | {partial[k]:,} | {bad[k]:,} |\n")
            f.write(f"\nScripts: {scripts:,}. Standard or plain files copied by the extractor only: "
                    f"{sum(skipped.values()):,} ({', '.join(sorted(s or '(none)' for s in skipped))}).\n")
            for k, es in sorted(errors.items()):
                f.write(f"\n## {k} failures\n\n")
                for rel, e in es[:200]:
                    f.write(f"- `{rel}`: {e[:200]}\n")
        write_function_table(calls)


if __name__ == "__main__":
    main()
