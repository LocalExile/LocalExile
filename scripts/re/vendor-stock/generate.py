"""Build runtime vendor membership from the reviewed community snapshot and pinned native tables.

No network or client writes. Disputed membership uses wiki vendor attribution; progression uses native offers.
Rows are matched by Id, never by display name: a client with translated text (a third-party patch writes it over the
English files) names the same rows differently. ids.json pins each English name used here to its rows' Ids; rebuild it
with --ids from English tables when the snapshot or the additions below change.
"""
import functools
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
# Captured Scion additions. Unlocks are assumptions: place each at its native reward requirement tier.
ADDITIONS = [('Nessa', 'Volcanic Fissure', 'The Caged Brute'),
             ('Yeena', 'Mana-Infused Staff', 'Sharp and Cruel'),
             ('Clarissa', 'Conflagration', 'Sever the Right Hand'),
             ('Clarissa', 'Holy Hammers', 'Sever the Right Hand')]
LILLY_ADDITIONS = ["Alchemist's Mark", 'Arcane Surge Support', 'Chance to Bleed Support',
    'Chance to Poison Support', 'Close Combat Support', 'Controlled Destruction Support',
    'Deadly Ailments Support', 'Elemental Damage with Attacks Support', 'Elemental Proliferation Support',
    'Faster Attacks Support', 'Faster Casting Support', 'Galvanic Field', 'Hallow Support', 'Holy Hammers',
    'Lightning Conduit', 'Minion Speed Support', 'Momentum Support', 'Nightblade Support', 'Overcharge Support',
    'Plague Bearer', 'Predator Support', 'Rage Support', 'Ruthless Support', 'Trap and Mine Damage Support',
    'Volcanic Fissure']
LILLY_QUEST = 'Fallen from Grace'


def table(tables, name):
    return json.loads((Path(tables) / (name + '.json')).read_text(encoding='utf-8-sig'))


def rows(tables):
    """Each kind's usable rows by index: gems (only the ordinary gem base, never a transfigured variant with the
    same display name), quests and classes."""
    bases = table(tables, 'BaseItemTypes')
    gems = sorted({r['BaseItemTypesKey'] for r in table(tables, 'SkillGems')})
    return dict(Gems={i: bases[i] for i in gems}, Quests=dict(enumerate(table(tables, 'Quest'))),
                Classes=dict(enumerate(table(tables, 'Characters'))))


def wanted(source):
    """The English names this file looks up, by kind."""
    records = [r for r in source['records'] if r['wiki']]
    return dict(Gems={r['gem'] for r in records} | {g for _, g, _ in ADDITIONS} | set(LILLY_ADDITIONS),
                Quests={r['quest'] for r in records} | {q for _, _, q in ADDITIONS} | {LILLY_QUEST},
                Classes={r['character_class'] for r in records} | {'Scion'})


def build_ids(tables, source):
    """ids.json from English tables: each wanted name's row Ids in row order. Every name must be there."""
    found, missing = {}, []
    for kind, names in wanted(source).items():
        by_name = {}
        for r in rows(tables)[kind].values():
            by_name.setdefault(r['Name'], []).append(r['Id'])
        missing += [f'{kind}: {n}' for n in sorted(names - by_name.keys())]
        found[kind] = {n: by_name[n] for n in sorted(names & by_name.keys())}
    if missing:
        sys.exit('vendor-stock: not in these tables (are they English?):\n  ' + '\n  '.join(missing))
    return found


def generate(tables, source, ids, layouts):
    found = rows(tables)
    index = {kind: {r['Id']: i for i, r in found[kind].items()} for kind in found}
    errors = []

    def find(kind, name):
        """The row indexes of one English name; a name or row that is not there is collected and reported."""
        if name not in ids[kind]:
            errors.append(f'{kind}: {name} is not in ids.json (rebuild it with --ids)')
            return []
        absent = [i for i in ids[kind][name] if i not in index[kind]]
        if absent:
            errors.append(f'{kind}: {name} has no row {", ".join(absent)} in these tables')
        return [index[kind][i] for i in ids[kind][name] if i in index[kind]]

    offers = table(tables, 'QuestRewardOffers')

    @functools.cache
    def quest_offers(name):
        keys = set(find('Quests', name))
        return [i for i, r in enumerate(offers) if r['QuestKey'] in keys]

    def gem_bases(name):
        return [found['Gems'][i]['Id'] for i in find('Gems', name)]

    def character(name):
        return next(iter(find('Classes', name)), -1)

    result = []
    for r in source['records']:
        if not r['wiki']:
            continue
        for base in gem_bases(r['gem']):
            result.append(dict(Vendor=r['vendor'], Class=character(r['character_class']), Base=base,
                               Offers=quest_offers(r['quest'])))
    for vendor, gem, quest in ADDITIONS:
        for base in gem_bases(gem):
            result.append(dict(Vendor=vendor, Class=character('Scion'), Base=base, Offers=quest_offers(quest)))
    for gem in LILLY_ADDITIONS:
        for base in gem_bases(gem):
            result.append(dict(Vendor='Lilly Roth', Class=-1, Base=base, Offers=quest_offers(LILLY_QUEST)))
    if errors:
        sys.exit('vendor-stock: cannot resolve:\n  ' + '\n  '.join(sorted(set(errors))))
    # NPCShopSets is authoritative for the ordinary vendor role, including later-act aliases.
    sets, shops = table(tables, 'NPCShopSets'), {}
    town_levels = {1:13, 2:23, 3:33, 4:40, 5:45, 6:50, 7:55, 8:60, 9:67, 10:69, 11:69}
    for npc in table(tables, 'NPCs'):
        key = npc['ShopSet']
        if key is None:
            continue
        role = sets[key]['Id']
        if role.startswith('A') and role.split('_')[0][1:].isdigit():
            act = int(role.split('_')[0][1:])
        else:
            act = 11
        kind = 'Weapons' if role.endswith('_Weapons') else 'Magic' if role.endswith('_Magic') else 'Gems' if 'LillyRoth' in role or role == 'Siosa' else 'Kirac' if role == 'Kirac' else None
        if kind:
            shops[npc['Id']] = dict(Kind=kind, Act=act, TownLevel=town_levels[act])
    return dict(Version=2, Shops=shops, Gems=result, GemLayouts=layouts,
                Policy='Wiki membership; native quest offers; captured Scion additions have assumed tier unlocks. See 17-npcs.md.')


def main():
    source = json.loads((HERE / 'community-stock.json').read_text(encoding='utf-8'))
    if sys.argv[1:] == ['--ids']:
        kinds = [f'  {json.dumps(kind)}: {{\n' + ',\n'.join(f'    {json.dumps(n)}: {json.dumps(i)}' for n, i in names.items())
                 + '\n  }' for kind, names in build_ids('data/tables', source).items()]
        (HERE / 'ids.json').write_text('{\n' + ',\n'.join(kinds) + '\n}\n', encoding='utf-8')
        return
    ids = json.loads((HERE / 'ids.json').read_text(encoding='utf-8'))
    layouts = json.loads((HERE / 'gem-layout.json').read_text(encoding='utf-8'))['Vendors']
    out = generate('data/tables', source, ids, layouts)
    Path('data/vendor-stock.json').write_text(json.dumps(out, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(shops=len(out['Shops']), gem_rules=len(out['Gems']))))


if __name__ == '__main__':
    main()
