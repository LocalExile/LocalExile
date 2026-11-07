"""corpus_check.py - run `levelgen generate` over every corpus trace and group the first differences: which client
caller the port's draws first part from, per load, and the tile verdict.

A corpus trace is a `.gen.json` in the shape walkmaps.js sends (re/011 §8) for a load the server generated: its level,
seed and tile hash, the client's tiles, the area stats and plugin blobs of its area load, and (when logged) the client's
bounded draws with `"kinds": "n"`. The traces live under data/corpus-traces (git-ignored) and are produced outside this
repository.

    python scripts/re/levelgen/corpus_check.py [--traces DIR] [--out FILE.tsv] [--only REGEX] [--jobs N] [--doodads]

--doodads runs the doodad stage after the generation (`generate --doodads`), so a load's draws are compared through
the whole log: the `doodads` column holds the stage's placed count and draws, and a load whose every logged draw
matched reads "(all draws matched)" in `caller`.

Writes one row per load (area, seed, level, port outcome, port hash, client hash, draws matched / logged, the first
differing client draw and its caller, tiles differing) and prints the groups: by the first-difference caller (the
work queue, biggest first) and by stop reason. Uses the built levelgen (scripts/re/levelgen/lg.ps1 builds it; the
levelgen server keeps the game files open, so a load takes about a second).
"""
import argparse, collections, concurrent.futures, csv, io, json, os, re, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
DLL = os.path.join(ROOT, 'scripts', 're', 'levelgen', 'bin', 'lg', 'levelgen.dll')


DOODADS = False


def run_one(path):
    with io.open(path, encoding='utf-8') as f:
        t = json.load(f)
    area, seed = t['area'], t['seed']
    p = subprocess.run(['dotnet', DLL, 'generate', area, str(seed), path] + (['--doodads'] if DOODADS else []), cwd=ROOT,
                       capture_output=True, text=True, encoding='utf-8', errors='replace')
    out = p.stdout + p.stderr
    row = {'area': area, 'seed': seed, 'level': t.get('level', 1), 'client_hash': t['tileHash'], 'outcome': '', 'port_hash': '',
           'draws': '', 'first_diff': '', 'caller': '', 'tiles': '', 'logged': len(t.get('draws', [])), 'doodads': ''}
    m = re.search(r'doodads: (\d+) placed, (\d+) draws in the stage(.*)', out)
    if m:
        row['doodads'] = f'{m.group(1)} placed, {m.group(2)} draws' + m.group(3).strip()
    m = re.search(r'seed \d+: \d+x\d+, (\d+) draws, tile hash (\d+)', out)
    if m:
        row['port_hash'] = m.group(2)
        row['outcome'] = 'match' if int(m.group(2)) == t['tileHash'] else 'MISMATCH'
    m = re.search(r'stopped: (.*)', out)
    if m:
        row['outcome'] = 'stop: ' + m.group(1).strip()
    m = re.search(r'draws: (\d+) of (\d+) match \(port made (\d+)', out)
    if m:
        row['draws'] = f'{m.group(1)}/{m.group(2)} (port {m.group(3)})'
    m = re.search(r'first difference at #(\d+): client (\S+\(\d+\) = \d+) from (0x[0-9a-f]+)(.*)', out)
    if m:
        row['first_diff'] = f'#{m.group(1)} client {m.group(2)}{m.group(4).strip()}'
        row['caller'] = m.group(3)
        # The doodad stage's draws (0x1420425d0 and its helpers) follow the port's last generation draw: a first
        # difference there with no port draw left means every generation draw matched.
        if m.group(3) in ('0x142042fae', '0x142042fbc') and 'port made no more draws' in m.group(4):
            row['caller'] = '(generation draws all matched; doodad stage next)'
        # After the doodad stage the client runs its own stages (0x14152e730 / 0x1415307bb / 0x141530658 the mesh
        # variation and object placement, 0x141544f0c / 0x141544f16 the ground clutter, 0x14154526b the critters: no
        # tiles, no doodads, not ported): a first difference there with no port draw left means the doodad draws all
        # matched too.
        elif 0x14152e000 <= int(m.group(3), 16) < 0x141546000 and 'port made no more draws' in m.group(4):
            row['caller'] = '(generation and doodad draws all matched; the client-only stages next)'
    elif row['draws'] and ' every draw the port made before stopping matches' in out:
        row['caller'] = '(stopped, every draw matched)'
    elif row['draws']:
        row['caller'] = '(all draws matched)'
    m = re.search(r'tiles: (\d+) of (\d+) entries differ \((\d+) in height only\)', out)
    if m:
        row['tiles'] = f'{m.group(1)}/{m.group(2)} ({m.group(3)} height only)'
    if not row['outcome']:
        row['outcome'] = 'ERROR: ' + (out.strip().splitlines() or ['no output'])[-1][:160]
    return row


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--traces', default=os.path.join(ROOT, 'data', 'corpus-traces'))
    ap.add_argument('--out', default=os.path.join(ROOT, 'data', 'corpus-check.tsv'))
    ap.add_argument('--only', default='.')
    ap.add_argument('--jobs', type=int, default=4)
    ap.add_argument('--doodads', action='store_true')
    a = ap.parse_args()
    global DOODADS
    DOODADS = a.doodads
    paths = sorted(os.path.join(a.traces, f) for f in os.listdir(a.traces) if f.endswith('.gen.json') and re.search(a.only, f))
    rows = []
    with concurrent.futures.ThreadPoolExecutor(a.jobs) as ex:
        for i, row in enumerate(ex.map(run_one, paths), 1):
            rows.append(row)
            if i % 50 == 0:
                print(f'{i} / {len(paths)}', file=sys.stderr, flush=True)
    cols = ['area', 'seed', 'level', 'outcome', 'port_hash', 'client_hash', 'draws', 'logged', 'first_diff', 'caller', 'tiles', 'doodads']
    with io.open(a.out, 'w', encoding='utf-8', newline='') as f:
        w = csv.writer(f, delimiter='\t', lineterminator='\n')
        w.writerow(cols)
        for r in sorted(rows, key=lambda r: (r['area'], r['seed'])):
            w.writerow([r[c] for c in cols])
    n = len(rows)
    match = sum(1 for r in rows if r['outcome'] == 'match')
    print(f'{match} of {n} loads match by tile hash -> {a.out}')
    kinds = collections.Counter(re.sub(r'\(.*', '', r['outcome']).strip() if r['outcome'] != 'match' else 'match' for r in rows)
    for k, v in kinds.most_common(12):
        print(f'  {v:4d}  {k}')
    whole_callers = ('(all draws matched)', '(generation and doodad draws all matched; the client-only stages next)')
    if a.doodads:
        whole = sum(1 for r in rows if r['outcome'] == 'match' and r['caller'] in whole_callers)
        print(f'{whole} of {n} loads match every logged draw through the doodad stage')
    print('\nfirst-difference callers of the loads that do not match' + (' every draw' if a.doodads else '') + ' (the work queue):')
    groups = collections.defaultdict(list)
    for r in rows:
        if (r['outcome'] != 'match' or a.doodads and r['caller'] not in whole_callers) and r['logged']:
            groups[r['caller'] or '(no draw comparison)'].append(r)
    for caller, L in sorted(groups.items(), key=lambda kv: -len(kv[1])):
        areas = collections.Counter(r['area'] for r in L)
        print(f'  {len(L):4d}  {caller:28s} {", ".join(f"{a} x{c}" if c > 1 else a for a, c in areas.most_common(6))}'
              + (' ...' if len(areas) > 6 else ''))


if __name__ == '__main__':
    main()
