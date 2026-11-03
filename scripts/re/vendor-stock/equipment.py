"""Summarize generated stock observations without turning sampled rolls into generation rules."""
import argparse
import json
from collections import Counter
from pathlib import Path


def main():
    ap=argparse.ArgumentParser();ap.add_argument('capture_folder',type=Path);args=ap.parse_args()
    root=args.capture_folder
    data=json.loads((root/'decoded-stock.json').read_text(encoding='utf-8'))
    areas={r['Id']:r for r in json.loads(Path('data/tables/WorldAreas.json').read_text(encoding='utf-8'))}
    towns={'Nessa Act 1':'1_1_town','Tarkleigh Act 1':'1_1_town','Yeena Act 2':'1_2_town',
           'Clarissa Act 3':'1_3_town','Petarus and Vanja Act 4':'1_4_town'}
    rows=[]
    for v in data['vendors']:
        if v['vendor'] not in towns:continue
        town=areas[towns[v['vendor']]];level=town['AreaLevel']
        items=[i for t in v['tabs'] for i in t['items'] if 'mods_flags' in i]
        for rarity in sorted({i['rarity'] for i in items}):
            sample=[i for i in items if i['rarity']==rarity]
            levels=sorted({i['item_level'] for i in sample})
            rows.append(dict(vendor=v['vendor'],town_level=level,rarity=rarity,count=len(sample),
                             observed_item_levels=levels,
                             matches_town_plus_two_minus_rarity=all(i['item_level']==level+2-rarity for i in sample)))
    result=dict(scope='Single-character snapshot; sample counts are not rarity probabilities or stock-size guarantees.',
        refresh_evidence={'source':'https://www.pathofexile.com/forum/view-thread/3361403',
            'version':'3.21.0','behavior':'Level-ups rotate some old items out and add new items; other stock persists across multiple levels.'},
        generation_boundary={'fixed_rules':['vendor category eligibility','progression and mode policy','native price calculation'],
            'generated_state':['selected bases','item levels','rarity','modifiers','quality','defence rolls','sockets','links','grid placement'],
            'unknown':['exact stock-size rules','base weights','rarity probabilities','refresh batch size','retention duration','socket/link distribution','low-level item-level formula']},
        item_level_pattern='All sampled campaign items match town level + 2 - rarity (normal=0, magic=1, rare=2); not proven below the vendor level cap.',
        rows=rows)
    (root/'equipment-observations.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    lines=['# Generated vendor equipment observations','',result['scope'],'',result['item_level_pattern'],'',
           '| Vendor | Town level | Rarity | Sample count | Observed item levels |','|---|---:|---|---:|---|']
    for r in rows:
        lines.append(f"| {r['vendor']} | {r['town_level']} | {['normal','magic','rare','unique'][r['rarity']]} | {r['count']} | {', '.join(map(str,r['observed_item_levels']))} |")
    lines += ['', 'Refresh: '+result['refresh_evidence']['behavior'],
        '[Official 3.21 patch notes]('+result['refresh_evidence']['source']+').','',
        'Persist surviving item instances with their existing rolls. Generate new instances for added stock; calculate their requirements and prices after rolling.',
        'This is an implementation implication of partial rotation, not a measured retention algorithm.',
        'Opening a shop should display its current state; this capture does not justify using open as a refresh trigger.','',
        'Unknown: '+', '.join(result['generation_boundary']['unknown'])+'.']
    (root/'equipment-observations.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    assert all(r['matches_town_plus_two_minus_rarity'] for r in rows)
    print(json.dumps(dict(sampled_campaign_items=sum(r['count'] for r in rows),groups=len(rows),pattern_matches=True)))


if __name__=='__main__':main()
