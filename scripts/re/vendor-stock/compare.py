"""Compare observed gem membership against community candidates without inferring unlocks."""
import argparse
import json
from pathlib import Path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('capture_folder', type=Path)
    args = ap.parse_args()
    root = args.capture_folder
    observed = json.loads((root/'decoded-stock.json').read_text(encoding='utf-8'))
    reference = json.loads(Path('scripts/re/vendor-stock/community-stock.json').read_text(encoding='utf-8'))
    session = json.loads((root/'session.json').read_text(encoding='utf-8'))
    resolved_class = session.get('character_class')
    names = {'Nessa Act 1':'Nessa','Yeena Act 2':'Yeena','Clarissa Act 3':'Clarissa',
             'Petarus and Vanja Act 4':'Petarus and Vanja','Lilly Roth':'Lilly Roth'}
    result = []
    for vendor in observed['vendors']:
        if vendor['vendor'] not in names: continue
        actual = {i['name'] for t in vendor['tabs'] for i in t['items'] if 'gem_level' in i}
        refs = [r for r in reference['records'] if r['vendor']==names[vendor['vendor']]]
        comparisons = []
        for cls in sorted({r['character_class'] for r in refs}):
            for source in ['wiki','poedb']:
                candidates = {r['gem'] for r in refs if r['character_class']==cls and r[source] is True}
                if not candidates: continue
                comparisons.append(dict(character_class=cls,source=source,reference_count=len(candidates),
                    overlap=len(actual & candidates),observed_not_listed=sorted(actual-candidates),
                    listed_not_observed=sorted(candidates-actual)))
        result.append(dict(vendor=vendor['vendor'],observed_unique_gems=len(actual),comparisons=comparisons))
    output = dict(character_class=resolved_class,
        limit='Membership differences only: exact league unconfirmed; quest progression not exhaustively verified. No source is promoted to authoritative unlock policy.',vendors=result)
    (root/'community-comparison.json').write_text(json.dumps(output,indent=2)+'\n',encoding='utf-8')
    lines=['# Community membership comparison','',output['limit'],'',
        '| Vendor | Candidate class | Source | Observed | Reference | Shared | Observed only | Reference only |',
        '|---|---|---|---:|---:|---:|---:|---:|']
    for v in result:
        for c in v['comparisons']:
            lines.append(f"| {v['vendor']} | {c['character_class']} | {c['source']} | {v['observed_unique_gems']} | {c['reference_count']} | {c['overlap']} | {len(c['observed_not_listed'])} | {len(c['listed_not_observed'])} |")
    (root/'community-comparison.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    if resolved_class:
        focused = [lines[0], '', 'Class: '+resolved_class+' (native Ascendancy mapping from user-reported Luminary).', '',
                   output['limit']]
        for v in result:
            for c in v['comparisons']:
                if c['character_class'] != resolved_class: continue
                focused += ['', '## '+v['vendor']+' / '+c['source'], '',
                    f"Observed {v['observed_unique_gems']}; reference {c['reference_count']}; shared {c['overlap']}.", '',
                    'Observed but not listed: '+(', '.join(c['observed_not_listed']) or 'none')+'.', '',
                    'Listed but not observed: '+(', '.join(c['listed_not_observed']) or 'none')+'.']
        (root/'scion-comparison.md').write_text('\n'.join(focused)+'\n',encoding='utf-8')
    print(json.dumps([{'vendor':v['vendor'],'unique_gems':v['observed_unique_gems'],'comparisons':len(v['comparisons'])} for v in result]))


if __name__ == '__main__': main()
