"""Decode saved vendor snapshots offline. Run from repository root.

Uses extracted data/ tables only as reference; writes research output beside captures.
No runtime stock rules or game archives are changed.
"""
import argparse
import hashlib
import json
import struct
from collections import Counter
from pathlib import Path


class Reader:
    def __init__(self, data): self.data, self.pos = data, 0
    def take(self, n):
        if n < 0 or self.pos + n > len(self.data): raise ValueError('truncated field')
        out = self.data[self.pos:self.pos+n]; self.pos += n
        return out
    def num(self, n, endian='little', signed=False): return int.from_bytes(self.take(n), endian, signed=signed)
    def compact(self):
        first = self.num(1)
        n = 1 if first < 128 else 2 if first < 192 else 3 if first < 224 else 4 if first < 240 else 5
        return first if n == 1 else ((first & (255 >> n)) << (8*(n-1)) if n < 5 else 0) | self.num(n-1, 'big')
    def signed_compact(self):
        first = self.data[self.pos]
        bits = 7 if first < 128 else 14 if first < 192 else 21 if first < 224 else 28 if first < 240 else 32
        value = self.compact()
        return value - (1 << bits) if value & (1 << (bits-1)) else value


def load(name): return json.loads((Path('data/tables') / (name+'.json')).read_text(encoding='utf-8-sig'))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('capture_folder', type=Path)
    args = ap.parse_args()
    bases = load('BaseItemTypes')
    by_hash = {b['HASH32']: (i,b) for i,b in enumerate(bases)}
    gems = {g['BaseItemTypesKey']:g for g in load('SkillGems')}
    effects = load('GemEffects')
    levels = {(g['GrantedEffect'],g['Level']):g['PlayerLevelReq'] for g in load('GrantedEffectsPerLevel')}
    xp = load('ItemExperiencePerLevel')
    attrs = {a['BaseItemTypesKey']:a for a in load('ComponentAttributeRequirements')}
    types = {t['type']:t['components'] for t in json.loads(Path('data/item-types.json').read_text())['types']}
    mods = {m['HASH16']:m for m in load('Mods')}
    stats = load('Stats')

    def mod_list(r):
        result = []
        for _ in range(r.num(1)):
            h = r.num(2); mod = mods[h]; values = []
            for n in range(1,9):
                stat = mod.get('StatsKey'+str(n))
                if stat is None: break
                values.append(dict(stat=stats[stat]['Id'],value=r.signed_compact()))
            result.append(dict(id=mod['Id'],hash=h,stats=values))
        return result

    def item(blob):
        r = Reader(blob)
        h, flags = r.num(4), r.num(2)
        row, b = by_hash[h]
        if flags & 512: r.take(8)
        result = dict(name=b['Name'], base_id=b['Id'], type_hash=h, width=b['Width'], height=b['Height'],
                      observation_only=True, generated_item='Mods' in types[b['Id']])
        # These captures use only these Base flags. Refuse other layouts instead of guessing offsets.
        visual, unknown, state, f, influence = r.num(2), r.num(1), r.num(1), r.num(2), r.num(1)
        assert f in (0, 0x800, 0x82), hex(f)
        result.update(base_flags=f, visual_hash=visual)
        if f & 2: result['flavour_hash'] = r.num(2)
        prices = []
        if f & 128:
            for _ in range(r.num(1)):
                currency, amount = r.num(4), r.compact()
                prices.append(dict(currency=by_hash[currency][1]['Name'], amount=amount))
        result['explicit_price'] = prices if f & 128 else None
        result['price_status'] = 'wire explicit' if f & 128 else 'not transmitted explicitly; see native-prices.json'
        if row in gems:
            g = gems[row]
            experience, variant, gemflags, support_index = r.num(4,signed=True), r.num(4), r.num(1), r.num(2)
            result.update(gem_experience=experience, gem_variant=variant, gem_flags=gemflags, quality=r.num(1),
                          gem_colour=g['GemColour'], is_support=g['IsSupport'])
            thresholds = [x for x in xp if x['ItemExperienceType']==g['ItemExperienceType']]
            level = len(thresholds)+1 if experience == -1 else sum(x['Experience']<=experience for x in thresholds)
            assert 0 <= variant < len(g['GemVariants'])
            effect = effects[g['GemVariants'][variant]]
            if variant: result['name'] = effect['Name']
            req = levels.get((effect['GrantedEffect'],level))
            result.update(gem_level=level, required_level=req, requirements_source='derived from local extracted gem tables; normal-mode effect')
            if req is not None:
                def attribute(percent):
                    value = int((3*req+11)*(percent/100)**0.9*(0.5 if g['IsSupport'] else 0.7)+0.5)
                    return value if value >= 14 else 0
                result['derived_attributes'] = {a:attribute(g[k]) for a,k in [('strength','StrengthRequirementPercent'),('dexterity','DexterityRequirementPercent'),('intelligence','IntelligenceRequirementPercent')]}
            assert r.pos == len(blob), ('gem trailing bytes', b['Name'], len(blob)-r.pos)
            result['component_decode'] = 'complete'
        else:
            components = types[b['Id']]
            for component in components[1:]:
                if component in ('LocalStats','AttributeRequirements','Weapon','Usable','Flask','Quest','Shield','RenderItem'): continue
                if component == 'Quality': result['quality'] = r.num(1)
                elif component == 'Armour': result['defence_roll'] = r.num(1)
                elif component == 'Charges':
                    result['charges'] = r.signed_compact()
                elif component == 'Stack': result['stack_size'] = r.num(2)
                elif component == 'Mods':
                    result['item_level'], result['required_level'] = r.num(1), r.num(1)
                    result['mods_flags'] = r.num(2)
                    assert result['mods_flags'] == 0, ('unsupported Mods flags', b['Name'])
                    magic = r.num(2)
                    assert magic & ~0xfff == 0
                    result.update(rarity=magic & 7, identified=bool(magic & 8), fractured_count=r.num(1), mods={})
                    if magic & 512: result['alternate_quality'] = [r.num(1),r.num(1)]
                    for bit,name in [(4,'implicit'),(6,'enchant'),(10,'scourge'),(11,'hidden')]:
                        if magic & (1<<bit): result['mods'][name] = mod_list(r)
                    if magic & 8:
                        if magic & 128: result['custom_name'] = r.take(r.num(4)*2).decode('utf-16le')
                        elif magic & 256: result['name_word_hashes'] = [r.num(4) for _ in range(r.num(1))]
                        if magic & 32: result['mods']['explicit'] = mod_list(r)
                    result['mods_tail'] = [r.num(1),r.num(1)]
                elif component == 'Sockets':
                    flags = r.num(1); sockets = []; links = []
                    if not flags & 3:
                        for _ in range((flags>>2)&7):
                            socket = r.num(1)
                            assert not socket & 1, 'nested gem not supported in shop capture decoder'
                            sockets.append(socket>>1)
                        links = [r.num(1) for _ in range(flags>>5)]
                        assert sum(links)==len(sockets)
                    result.update(socket_flags=flags,socket_colours=sockets,socket_links=links)
                elif component == 'Tincture': result['tincture_state'] = r.num(1)
                elif component == 'MapKey':
                    flags = r.num(1); result['map_key_flags'] = flags
                    assert flags & ~7 == 0
                    for bit,size in [(0,4),(1,2),(2,1)]:
                        if flags & (1<<bit): result['map_key_override_'+str(bit)] = r.num(size)
                else: raise ValueError('unsupported component '+component)
            if b['Id'] in attrs:
                a = attrs[b['Id']]
                result['base_attributes_before_modifiers'] = dict(strength=a['ReqStr'],dexterity=a['ReqDex'],intelligence=a['ReqInt'])
            if h in (0x192c7aab,0x365f206a,0x503da850,0x9aff2df6): result['special_trailing_u16'] = r.num(2)
            assert r.pos == len(blob), ('trailing component bytes',b['Name'],len(blob)-r.pos)
            result['component_decode'] = 'complete'
        return result

    vendors = []
    for path in sorted(args.capture_folder.glob('*-messages.json')):
        source = json.loads(path.read_text(encoding='utf-8'))
        summary = json.loads(path.with_name(path.name.replace('-messages','-summary')).read_text(encoding='utf-8'))
        shop = Reader(bytes.fromhex(source['shop']['hex']))
        npc, kind, mode = shop.num(4,'big'), shop.num(2,'big'), shop.num(1)
        ids = [shop.num(4,'big') for _ in range(shop.num(2,'big'))]
        assert shop.num(2,'big') == 0 and shop.pos == len(shop.data)
        tabs = []
        for index, id in enumerate(ids):
            raw = bytes.fromhex(source['inventories'][str(id)]['hex']); r = Reader(raw)
            assert r.num(1)==1 and r.num(4,'big')==id
            invkind, invrow, width, height, flags = [r.num(1) for _ in range(5)]
            assert flags == 0x60
            count = r.compact(); records = []; occupied = set(); itemids = set()
            for _ in range(count):
                itemid, x, y, size = r.num(4,'big'),r.num(1),r.num(1),r.num(2,'big')
                data = item(r.take(size)); data.update(x=x,y=y)
                assert itemid not in itemids
                itemids.add(itemid)
                assert x+data['width']<=width and y+data['height']<=height
                cells = {(xx,yy) for xx in range(x,x+data['width']) for yy in range(y,y+data['height'])}
                assert not occupied & cells
                occupied |= cells
                records.append(data)
            assert r.take(2)==b'\x00\x00' and r.pos==len(raw)
            tabs.append(dict(tab=index+1, inventory_kind=invkind, inventory_row=invrow,width=width,height=height,
                             count=count, sha256=hashlib.sha256(raw).hexdigest(),items=records))
        vendors.append(dict(vendor=summary['vendor_user_identified'],location=summary.get('location'),time=summary['time'],
                            server_tab_names=[],tabs=tabs))
    session = json.loads((args.capture_folder/'session.json').read_text(encoding='utf-8'))
    result = dict(scope='Observed vendor stock for one level-91 Luminary; generated equipment rolls are snapshots, not fixed stock or universal unlock policy',
                  character_class=session.get('character_class'),
                  source='Official-client decoded messages, executable SHA256 78A0E227189C263658A7F65A02F086428B859BFC114AE8E7CE8AAB721EBD48D5',
                  limitations=['Exact league unconfirmed; class provenance is recorded in session.json','Siosa locked; not captured','No purchase, refresh or unlock transition tested',
                               'Wire prices only here; see native-prices.json for separately derived prices','Final equipment attributes may depend on decoded modifiers; base attributes are labelled separately'],vendors=vendors)
    out = args.capture_folder/'decoded-stock.json'
    out.write_text(json.dumps(result,indent=2,ensure_ascii=False)+'\n',encoding='utf-8')
    lines=['# Captured vendor stock','',result['scope'],'','All item counts, record boundaries, grid bounds and non-overlap validated.','',
           '| Vendor | Tab | Items | Gems | Gem levels | Explicit prices |','|---|---:|---:|---:|---|---:|']
    for v in vendors:
        for t in v['tabs']:
            items=t['items']; gemlevels=sorted({i['gem_level'] for i in items if 'gem_level' in i})
            lines.append(f"| {v['vendor']} | {t['tab']} | {len(items)} | {sum('gem_level' in i for i in items)} | {', '.join(map(str,gemlevels)) or '—'} | {sum(i['explicit_price'] is not None for i in items)} |")
    lines += ['','## Limits','']+['- '+s for s in result['limitations']]
    for v in vendors:
        lines += ['', '## '+v['vendor'], '', '| Tab | Cell (zero-based) | Item | Rarity | Quality | Sockets / links | Gem level | Required level | Explicit price |','|---:|---|---|---|---:|---|---:|---:|---|']
        for t in v['tabs']:
            for i in t['items']:
                price='; '.join(str(p['amount'])+' '+p['currency'] for p in i['explicit_price']) if i['explicit_price'] is not None else 'not explicit'
                rarity={0:'normal',1:'magic',2:'rare',3:'unique'}.get(i.get('rarity'),'—')
                sockets=' '.join({1:'R',2:'G',3:'B',4:'W',5:'A',6:'D'}.get(c,str(c)) for c in i.get('socket_colours',[]))
                if sockets: sockets += ' / '+','.join(map(str,i['socket_links']))
                lines.append(f"| {t['tab']} | {i['x']}, {i['y']} | {i['name']} | {rarity} | {i.get('quality','—')} | {sockets or '—'} | {i.get('gem_level','—')} | {i.get('required_level','—')} | {price} |")
    (args.capture_folder/'decoded-stock.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    print(json.dumps(dict(vendors=len(vendors),tabs=sum(len(v['tabs']) for v in vendors),items=sum(t['count'] for v in vendors for t in v['tabs']))))


if __name__ == '__main__': main()
