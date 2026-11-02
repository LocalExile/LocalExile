"""Build runtime vendor membership from the reviewed community snapshot and pinned native tables.

No network or client writes. Disputed membership uses wiki vendor attribution; progression uses native offers.
"""
import json
from pathlib import Path


def table(name):
    return json.loads((Path('data/tables') / (name + '.json')).read_text(encoding='utf-8-sig'))


def main():
    quests, offers, bases, characters = table('Quest'), table('QuestRewardOffers'), table('BaseItemTypes'), table('Characters')
    names = {r['Name']: i for i, r in enumerate(characters)}
    quest_offers = {}
    for i, r in enumerate(offers):
        if r['QuestKey'] is not None:
            quest_offers.setdefault(quests[r['QuestKey']]['Name'], []).append(i)
    # Only the ordinary gem base, never a transfigured variant with the same display name.
    gems = {r['BaseItemTypesKey'] for r in table('SkillGems')}
    by_name = {}
    for i, r in enumerate(bases):
        if i in gems:
            by_name.setdefault(r['Name'], []).append(r['Id'])
    source = json.loads(Path('scripts/re/vendor-stock/community-stock.json').read_text(encoding='utf-8'))
    result, missing = [], set()
    for r in source['records']:
        if not r['wiki']:
            continue
        if r['gem'] not in by_name or r['quest'] not in quest_offers:
            missing.add((r['gem'], r['quest']))
            continue
        for base in by_name[r['gem']]:
            result.append(dict(Vendor=r['vendor'], Class=names[r['character_class']], Base=base,
                               Offers=quest_offers[r['quest']]))
    # Captured Scion additions. Unlocks are assumptions: place each at its native reward requirement tier.
    additions = [('Nessa', 'Volcanic Fissure', 'The Caged Brute'),
                 ('Yeena', 'Mana-Infused Staff', 'Sharp and Cruel'),
                 ('Clarissa', 'Conflagration', 'Sever the Right Hand'),
                 ('Clarissa', 'Holy Hammers', 'Sever the Right Hand')]
    for vendor, gem, quest in additions:
        for base in by_name.get(gem, []):
            result.append(dict(Vendor=vendor, Class=names['Scion'], Base=base, Offers=quest_offers[quest]))
    lilly_additions = ["Alchemist's Mark", 'Arcane Surge Support', 'Chance to Bleed Support',
        'Chance to Poison Support', 'Close Combat Support', 'Controlled Destruction Support',
        'Deadly Ailments Support', 'Elemental Damage with Attacks Support', 'Elemental Proliferation Support',
        'Faster Attacks Support', 'Faster Casting Support', 'Galvanic Field', 'Hallow Support', 'Holy Hammers',
        'Lightning Conduit', 'Minion Speed Support', 'Momentum Support', 'Nightblade Support', 'Overcharge Support',
        'Plague Bearer', 'Predator Support', 'Rage Support', 'Ruthless Support', 'Trap and Mine Damage Support',
        'Volcanic Fissure']
    for gem in lilly_additions:
        for base in by_name[gem]:
            result.append(dict(Vendor='Lilly Roth', Class=-1, Base=base, Offers=quest_offers['Fallen from Grace']))
    # NPCShopSets is authoritative for the ordinary vendor role, including later-act aliases.
    sets, shops = table('NPCShopSets'), {}
    town_levels = {1:13, 2:23, 3:33, 4:40, 5:45, 6:50, 7:55, 8:60, 9:67, 10:69, 11:69}
    for npc in table('NPCs'):
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
    out = dict(Version=1, Shops=shops, Gems=result,
               Policy='Wiki membership; native quest offers; captured Scion additions have assumed tier unlocks. See 17-npcs.md.')
    Path('data/vendor-stock.json').write_text(json.dumps(out, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(shops=len(shops), gem_rules=len(result), unresolved=sorted(missing))))


if __name__ == '__main__':
    main()
