#!/usr/bin/env python3
# usage: python -I scripts/re/overnight/report.py <root> [--index]
# Writes <root>/REPORT.md from what the overnight run left on disk: only measured numbers. --index also builds
# <root>/game/index.sqlite (functions, calls, strings, vtables, datarefs) from the Game.exe tables.
import csv, os, re, sqlite3, sys, time

csv.field_size_limit(1 << 30)
root = sys.argv[1]
game = os.path.join(root, 'game')


def rows(path):
    with open(path, encoding='utf-8', errors='replace', newline='') as f:
        r = csv.reader(f, delimiter='\t', quoting=csv.QUOTE_NONE)
        next(r, None)
        yield from r


def build_index():
    path = os.path.join(game, 'index.sqlite')
    tmp = path + '.tmp'
    if os.path.exists(tmp):
        os.remove(tmp)
    db = sqlite3.connect(tmp)
    db.executescript('PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;')
    tables = {
        'functions': ('functions.tsv', 'addr,size,name,namespace,thunk_of,callers,callees,signature', ['addr', 'name']),
        'calls': ('calls.tsv', 'caller_fn,callsite,target,target_name', ['caller_fn', 'target']),
        'datarefs': ('datarefs.tsv', 'from_fn,from_addr,to_addr,type', ['to_addr', 'from_fn']),
        'strings': ('strings.tsv', 'addr,type,value', ['addr', 'value']),
        'vtables': ('vtables.tsv', 'class,vtable,slot,function,name', ['class', 'function']),
    }
    for name, (file, cols, indexes) in tables.items():
        src = os.path.join(game, file)
        if not os.path.exists(src):
            continue
        n = cols.count(',') + 1
        db.execute(f'CREATE TABLE {name} ({cols})')
        db.executemany(f'INSERT INTO {name} VALUES ({",".join("?" * n)})', (r for r in rows(src) if len(r) == n))
        db.commit()
        for i in indexes:
            db.execute(f'CREATE INDEX {name}_{i} ON {name} ({i})')
        db.commit()
    db.close()
    os.replace(tmp, path)


def lines(path):
    with open(path, 'rb') as f:
        return sum(1 for _ in f) - 1


def decomp_stats(d):
    p = os.path.join(d, 'index.tsv')
    if not os.path.exists(p):
        return None
    ok = failed = missing = 0
    failures = []
    for r in rows(p):
        if len(r) < 5:
            continue
        if r[4] == 'ok':
            ok += 1
        elif r[4] == 'failed':
            failed += 1
            failures.append((r[0], r[1]))
        else:
            missing += 1
    return ok, failed, missing, failures


def size_of(d):
    total = files = 0
    for base, _, names in os.walk(d):
        for n in names:
            try:
                total += os.path.getsize(os.path.join(base, n))
                files += 1
            except OSError:
                pass
    return total, files


def coverage(d):
    p = os.path.join(d, 'coverage.tsv')
    if not os.path.exists(p):
        return None
    r = next(rows(p), None)
    return [int(x) for x in r] if r else None


def main():
    if '--index' in sys.argv:
        build_index()
    o = [f'# Overnight decompile report', '', f'Written {time.strftime("%Y-%m-%d %H:%M:%S")}.', '']
    o += ['## Stages', '', '| Stage | State |', '|---|---|']
    state = os.path.join(root, 'state')
    if os.path.isdir(state):
        seen = sorted(os.listdir(state), key=lambda n: os.path.getmtime(os.path.join(state, n)))
        for n in seen:
            stage, _, kind = n.rpartition('.')
            if kind in ('done', 'failed'):
                o.append(f'| {stage} | {"ok" if kind == "done" else "**FAILED**"} |')
    o += ['', '## Game.exe', '']
    s = decomp_stats(os.path.join(game, 'decomp'))
    if s:
        o.append(f'- Decompiled: {s[0]:,} ok, {s[1]:,} failed, {s[2]:,} missing.')
        if s[3]:
            o.append('- Still failing (read `game/disasm/` for these): ' + ', '.join(f'{a} ({b} B)' for a, b in s[3][:60]))
    else:
        o.append('- Decompile has not finished.')
    c = coverage(game)
    if c:
        o.append(f'- Executable bytes: {c[0]:,}; in functions {c[1]:,} ({100 * c[1] / max(c[0], 1):.2f}%); padding {c[2]:,}; '
                 f'outside functions {c[3]:,} disassembled, {c[4]:,} undefined (`game/undecompiled.tsv`).')
    for label, f in (('Functions', 'functions.tsv'), ('Call edges', 'calls.tsv'), ('Data references', 'datarefs.tsv'),
                     ('Strings', 'strings.tsv'), ('RTTI vtable slots', 'vtables.tsv'), ('Unnamed vtable slots', 'vtables-unnamed.tsv')):
        p = os.path.join(game, f)
        if os.path.exists(p):
            o.append(f'- {label}: {lines(p):,}.')
    p = os.path.join(game, 'recover.log')
    if os.path.exists(p):
        for l in open(p, encoding='utf-8', errors='replace'):
            if l.startswith(('pdata:', 'rtti:')):
                o.append(f'- {l.strip()}')
    if os.path.exists(os.path.join(game, 'paramid.done')):
        o.append('- Parameter ID: complete.')
    elif os.path.exists(os.path.join(game, 'paramid.cursor')):
        cur = open(os.path.join(game, 'paramid.cursor')).read().strip()
        o.append(f'- Parameter ID: **partial**, reached 0x{cur}; functions after it keep their weaker prototypes.')
    else:
        o.append('- Parameter ID: did not run.')
    o += ['', '## Other client binaries', '', '| Binary | Decompiled ok | Failed | In functions |', '|---|---|---|---|']
    bins = os.path.join(root, 'bins')
    if os.path.isdir(bins):
        for n in sorted(os.listdir(bins)):
            d = os.path.join(bins, n)
            s = decomp_stats(os.path.join(d, 'decomp'))
            c = coverage(d)
            pct = f'{100 * c[1] / max(c[0], 1):.1f}%' if c else '-'
            o.append(f'| {n} | {s[0]:,} | {s[1]:,} | {pct} |' if s else f'| {n} | **not finished** | - | {pct} |')
    o += ['', '## Content.ggpk', '']
    content = os.path.join(root, 'content')
    plan = os.path.join(content, 'extensions.tsv')
    if os.path.exists(plan):
        total = [(r[0], int(r[1]), int(r[2])) for r in rows(plan) if len(r) == 3]
        o.append(f'- Index: {sum(t[1] for t in total):,} files, {sum(t[2] for t in total) / 2**30:,.1f} GB uncompressed.')
        o += ['', '| Extension | Files | GB |', '|---|---|---|']
        o += [f'| {e or "(none)"} | {n:,} | {b / 2**30:.2f} |' for e, n, b in total[:25]]
    tree = os.path.join(content, 'files')
    if os.path.isdir(tree):
        b, n = size_of(tree)
        o += ['', f'- Extracted: {n:,} files, {b / 2**30:.1f} GB in `content/files/`.']
    textures = os.path.join(content, 'textures')
    if os.path.isdir(textures):
        b, n = size_of(textures)
        full = 'complete' if os.path.exists(os.path.join(state, 'textures.complete')) else \
            '**partial, the disk floor stopped it**' if os.path.exists(os.path.join(state, 'textures.partial')) else 'in progress'
        o.append(f'- Textures: {n:,} files, {b / 2**30:.1f} GB before compression in `content/textures/` ({full}).')
    errs = os.path.join(tree, 'extract-errors.tsv')
    if os.path.exists(errs) and os.path.getsize(errs):
        o.append(f'- Extract errors: {lines(errs) + 1:,} (`content/files/extract-errors.tsv`).')
    tmp = os.path.join(root, 'REPORT.md.' + str(os.getpid()))
    with open(tmp, 'w', encoding='utf-8') as f:
        f.write('\n'.join(o) + '\n')
    os.replace(tmp, os.path.join(root, 'REPORT.md'))


main()
