"""Join captured Scion stock, native prices and community quest candidates for implementation review."""
import argparse
import json
from pathlib import Path


def main():
    ap=argparse.ArgumentParser(); ap.add_argument('capture_folder',type=Path); args=ap.parse_args()
    root=args.capture_folder
    stock=json.loads((root/'decoded-stock.json').read_text(encoding='utf-8'))
    prices=json.loads((root/'native-prices.json').read_text(encoding='utf-8'))
    community=json.loads(Path('scripts/re/vendor-stock/community-stock.json').read_text(encoding='utf-8'))
    price_by_cell={(r['vendor'],r['tab'],r['x'],r['y']):r for r in prices['items']}
    names={'Nessa Act 1':'Nessa','Yeena Act 2':'Yeena','Clarissa Act 3':'Clarissa',
           'Petarus and Vanja Act 4':'Petarus and Vanja','Lilly Roth':'Lilly Roth'}
    result=[]
    for vendor in stock['vendors']:
        for tab in vendor['tabs']:
            for item in tab['items']:
                price=price_by_cell[vendor['vendor'],tab['tab'],item['x'],item['y']]
                quests=[]
                if 'gem_level' in item:
                    for r in community['records']:
                        if r['vendor']==names.get(vendor['vendor']) and r['character_class']=='Scion' and r['gem']==item['name']:
                            quests.append(dict(quest=r['quest'],wiki=r['wiki'],poedb=r['poedb'],comparison=r['comparison']))
                row=dict(vendor=vendor['vendor'],tab=tab['tab'],x=item['x'],y=item['y'],name=item['name'],
                    base_id=item['base_id'],observed_class='Scion',required_level=item.get('required_level'),
                    use_requirements_source='gem table derivation' if 'gem_level' in item else 'wire Mods level; base attributes before modifiers',
                    attributes=item.get('derived_attributes',item.get('base_attributes_before_modifiers')),
                    price=price['price'],price_evidence=price['status'],quest_candidates=quests,
                    unlock_status='community candidate only; not verified by quest transition' if quests else 'not established by this capture')
                generated = 'mods_flags' in item
                row['stock_kind'] = 'generated item observation' if generated else 'gem offer observation' if 'gem_level' in item else 'currency/other offer observation'
                row['is_fixed_stock_definition'] = False
                row['price_scope'] = 'price of this captured roll; recompute for each generated item' if generated else 'price of captured offer'
                if generated:
                    row['generated_properties'] = ['base selection','item level','rarity','modifiers','quality','defence roll','sockets','links','placement']
                    row['use_requirements_source'] += '; generated item, not a permanent vendor requirement'
                if 'gem_level' in item: row['gem_level']=item['gem_level']
                result.append(row)
    output=dict(scope='Review table of observations, not fixed stock or runtime configuration. Equipment rolls change with generation/refresh. Do not apply item-use requirements as purchase gates.',
                limitations=['One Scion character; other class eligibility not observed','Siosa locked; no capture',
                    'Quest names are community candidates, not validated server flags','Prices derived for normal mode except explicit wire prices',
                    'Equipment generation and refresh rules cannot be inferred from one snapshot'],items=result)
    (root/'shop-requirements.json').write_text(json.dumps(output,indent=2)+'\n',encoding='utf-8')
    lines=['# Shop requirements review','',output['scope'],'',
        '| Vendor | Tab / cell | Item | Required level to use | Attributes | Price | Candidate quests |','|---|---|---|---:|---|---|---|']
    for r in result:
        attrs=', '.join(k+' '+str(v) for k,v in (r['attributes'] or {}).items() if v) or 'none reported'
        price=' + '.join(str(p['amount'])+' '+p['currency'] for p in r['price'])
        quests='; '.join(q['quest']+' ['+q['comparison']+']' for q in r['quest_candidates']) or 'not established'
        lines.append(f"| {r['vendor']} | {r['tab']} / {r['x']},{r['y']} | {r['name']} | {r['required_level'] if r['required_level'] is not None else 'not encoded'} | {attrs} | {price} | {quests} |")
    lines += ['','## Limits','']+['- '+x for x in output['limitations']]
    (root/'shop-requirements.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    gems=[r for r in result if 'gem_level' in r]
    print(json.dumps(dict(items=len(result),gems=len(gems),gems_with_quest_candidates=sum(bool(r['quest_candidates']) for r in gems),
                         gems_without_quest_candidates=sum(not r['quest_candidates'] for r in gems))))


if __name__=='__main__': main()
