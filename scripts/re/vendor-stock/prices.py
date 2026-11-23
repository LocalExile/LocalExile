"""Join native price tables to captured items; keep static derivations distinct from observed quotes.

Static evidence: Base price resolver 0x1421ef8f0; best level row 0x1421ef6e0;
rarity column 0x1421f0a20; amount/stack handling 0x1421f0940.
"""
import argparse
import json
import os
import sys
from pathlib import Path

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "dev"))
import clientdb  # noqa: E402  (scripts/dev/clientdb.py: the client tables in data/client.db)


def table(name): return clientdb.table(name)


def main():
    ap=argparse.ArgumentParser(); ap.add_argument('capture_folder',type=Path); args=ap.parse_args()
    root=args.capture_folder
    data=json.loads((root/'decoded-stock.json').read_text(encoding='utf-8'))
    bases=table('BaseItemTypes'); indices={b['Id']:i for i,b in enumerate(bases)}
    costs=table('ItemCosts'); levels=table('ItemCostPerLevel')
    currency_rows={r['BaseItemTypesKey']:r for r in table('CurrencyItems')}
    rows=[]
    for vendor in data['vendors']:
        for tab in vendor['tabs']:
            for item in tab['items']:
                row=dict(vendor=vendor['vendor'],tab=tab['tab'],x=item['x'],y=item['y'],name=item['name'])
                if item['explicit_price'] is not None:
                    row.update(status='observed explicit',price=item['explicit_price'])
                else:
                    # The native selector uses Mods item level, or 1 if that component is absent.
                    level=item.get('item_level',1)
                    candidates=[(i,r) for i,r in enumerate(levels) if r['BaseItem']==indices[item['base_id']] and r['Level']<=level]
                    assert candidates, item['name']
                    index,selected=max(candidates,key=lambda p:p[1]['Level'])
                    cost=costs[selected['Cost']]
                    options=[]
                    for rarity in range(4):
                        currencies=cost[f'Cost{rarity+1}Currencies']; amounts=cost[f'Cost{rarity+1}Values']
                        assert len(currencies)==len(amounts)
                        options.append([dict(currency=bases[c]['Name'],amount=abs(n),
                                             scales_with_stack=n>0) for c,n in zip(currencies,amounts)])
                    row.update(cost_id=cost['Id'],item_cost_per_level_row=index,selected_level=selected['Level'])
                    if 'gem_level' in item:
                        assert options[0]
                        row.update(status='native-derived normal-mode gem; not tooltip/purchase checked',
                                   price=[dict(currency=p['currency'],amount=p['amount']) for p in options[0]])
                    else:
                        rarity=item.get('rarity',0); stack=item.get('stack_size',1)
                        currency=currency_rows.get(indices[item['base_id']])
                        if 'stack_size' in item:
                            # Captured currency stacks have one unit, below their normal maximum.
                            # Full-stack conversion/cost overrides are not implemented by this offline join.
                            assert currency is not None and stack < currency['StackSize'], item['name']
                        chosen=options[rarity]
                        assert chosen, ('map/fallback price branch needs review',item['name'],rarity)
                        amounts={}
                        for p in chosen:
                            amounts[p['currency']]=amounts.get(p['currency'],0)+p['amount']*(stack if p['scales_with_stack'] else 1)
                        if item.get('identified') is False and rarity in (1,2):
                            surcharge='Scroll of Wisdom' if rarity==1 else 'Orb of Chance'
                            amounts[surcharge]=amounts.get(surcharge,0)+1
                            row['unidentified_surcharge']=dict(currency=surcharge,amount=1)
                        row.update(status='native-derived normal-mode item; not tooltip/purchase checked',
                                   rarity=rarity,stack_size=stack,
                                   price=[dict(currency=c,amount=n) for c,n in amounts.items()])
                rows.append(row)
    out=dict(scope='Normal-mode native data; exact league mode not recorded. Explicit wire price overrides table candidates.',items=rows)
    (root/'native-prices.json').write_text(json.dumps(out,indent=2)+'\n',encoding='utf-8')
    lines=['# Native vendor price research','',out['scope'],'',
           'Gem costs use the native no-Mods level-1 lookup. Equipment uses item level, rarity and stack count; unidentified magic/rare surcharges are included. No purchase was verified.','',
           '| Vendor | Tab | Cell | Item | Price / candidate row | Evidence |','|---|---:|---|---|---|---|']
    for r in rows:
        price=' + '.join(str(p['amount'])+' '+p['currency'] for p in r['price']) if 'price' in r else r['cost_id']
        lines.append(f"| {r['vendor']} | {r['tab']} | {r['x']}, {r['y']} | {r['name']} | {price} | {r['status']} |")
    (root/'native-prices.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    print(json.dumps({'explicit':sum(r['status']=='observed explicit' for r in rows),
        'derived_gems':sum(r['status'].startswith('native-derived normal-mode gem') for r in rows),
        'derived_other_items':sum(r['status'].startswith('native-derived normal-mode item') for r in rows)}))


if __name__=='__main__': main()
