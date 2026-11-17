"""Rebuild divination content from SQL/client descriptions without writing a database.

From the repository root:
    python scripts/re/divination_cards.py --check
    python scripts/re/divination_cards.py --output sql/updates/pending_world/divination_refresh.sql

The default client tables and world catalog are built in memory from tracked SQL.
--client-db accepts an already built client database, opened read-only. Output is
only a proposed pending world update; existing files are never overwritten.
"""
import argparse
from collections import defaultdict
import sqlite3
from pathlib import Path

from divination.generate import named_rewards, equipment_rewards, unique_catalog

ROOT = Path('.')
CARD_TABLES = {'divination_card_rewards': ('card_id',), 'divination_reward_rules': ('card_id',),
               'divination_reward_members': ('card_id','member_kind','member_id','base_type'),
               'divination_reward_implicit_mods': ('card_id','mod_id')}


def database(name):
    db = sqlite3.connect(':memory:')
    db.row_factory = sqlite3.Row
    streams = {'client': ('client','client_fixes'), 'world': ('world','ruthless')}[name]
    paths = sorted((ROOT/'sql/base'/name).glob('*.sql'))
    paths += sorted((p for s in streams for p in (ROOT/'sql/updates'/s).glob('*.sql')), key=lambda p:(p.name,str(p)))
    paths += sorted(p for s in streams for p in (ROOT/'sql/updates'/('pending_'+s)).glob('*.sql'))
    for path in paths:
        db.executescript(path.read_text(encoding='utf-8'))
    return db


def current_bases(seed):
    """Preserve the current-base fixes made during the original PR's integration."""
    prefix = 'Metadata/Items/DivinationCards/DivinationCard'
    pools = {
        'BoundlessRealms': [('base',f'Metadata/Items/Maps/MapKeyTier{n}','',1) for n in range(1,17)],
        'LingeringRemnants': [('base','Metadata/Items/Maps/MapKeyVaalTemple','',1)],
        'LastHope': [('base','Metadata/Items/MapFragments/CurrencyVaalFragment2_2','',1)],
        'DivineShard': [('base','Metadata/Items/MapFragments/CurrencyFragmentPantheonFlask','',1)],
        'Remembrance': [('unique',"Precursor's Emblem",f'Metadata/Items/Rings/Ring{n}',1) for n in [5,6,7,8,12,13,14]],
        'TheWatcher': [('unique','Crown of Eyes','Metadata/Items/Armours/Helmets/HelmetInt11',1)],
        'DivineJustice': [('unique','Grand Spectrum',f'Metadata/Items/Jewels/Jewel{n}',1) for n in ['Str','Dex','Int']],
    }
    for card, members in pools.items():
        seed.execute('DELETE FROM divination_reward_members WHERE card_id=?',(prefix+card,))
        seed.executemany('INSERT INTO divination_reward_members VALUES (?,?,?,?,?)',[(prefix+card,*m) for m in members])
    seed.execute("DELETE FROM divination_reward_members WHERE card_id IN (?,?) AND member_kind='base' AND member_id LIKE 'Metadata/Items/MapFragments/VaalFragment%'",(prefix+'HerMask',prefix+'SambodhisVow'))
    # This named normal reward gained explicit map members in the integration fix, without an equipment rule.
    seed.execute('DELETE FROM divination_reward_rules WHERE card_id=?',(prefix+'BoundlessRealms',))
    seed.execute("UPDATE divination_card_rewards SET source='3.29.3d CurrencyItems.Description; exact named rewards only', notes='Exact named reward; factory also refuses missing types or modifiers.' WHERE card_id=?",(prefix+'BoundlessRealms',))


def literal(value):
    if isinstance(value,(int,float)): return str(value)
    return 'NULL' if value is None else "'"+value.replace("'","''")+"'"


def differences(expected, actual, tables, names=None):
    inserts=[]
    deletes=[]
    counts=defaultdict(int)
    for table, keys in tables.items():
        columns=[r[1] for r in expected.execute('PRAGMA table_info('+table+')')]
        def rows(db):
            return {tuple(r[k] for k in keys):dict(r) for r in db.execute('SELECT '+','.join(columns)+' FROM '+table)
                    if names is None or r['name'] in names}
        wanted, have = rows(expected), rows(actual)
        table_deletes=[]
        for key in sorted(have.keys()-wanted.keys()):
            where=' AND '.join(k+'='+literal(v) for k,v in zip(keys,key))
            table_deletes.append('DELETE FROM '+table+' WHERE '+where+';');counts[table]+=1
        deletes=table_deletes+deletes
        for key in sorted(wanted):
            if wanted[key]==have.get(key):continue
            row=wanted[key]
            if key in have:
                changed=[c for c in columns if row[c]!=have[key][c]]
                where=' AND '.join(k+'='+literal(v) for k,v in zip(keys,key))
                inserts.append('UPDATE '+table+' SET '+', '.join(c+'='+literal(row[c]) for c in changed)+' WHERE '+where+';')
            else:
                inserts.append('INSERT INTO '+table+' ('+', '.join(columns)+') VALUES ('+', '.join(literal(row[c]) for c in columns)+');')
            counts[table]+=1
    return deletes+inserts,dict(counts)


def regenerate(db, world):
    # The supplemental identities are generated independently of the checked-in catalog rows.
    catalog=sqlite3.connect(':memory:');catalog.row_factory=sqlite3.Row
    for table in ['unique_items','unique_item_mods']:
        catalog.executescript(world.execute('SELECT sql FROM sqlite_master WHERE name=?',(table,)).fetchone()[0])
    catalog.executescript(unique_catalog(db))
    names={r[0] for r in catalog.execute('SELECT name FROM unique_items')}
    catalog_tables={'unique_items':('name','base_type'), 'unique_item_mods':('name','base_type','implicit','ord'),
                    'unique_item_identities':('name','base_type')}
    catalog_sql,catalog_counts=differences(catalog,world,catalog_tables,names)
    # Resolve card members using the regenerated supplemental rows, even when their persisted rows are missing.
    world.executescript('\n'.join(catalog_sql))
    base=named_rewards(db,world)
    seed,remaining=equipment_rewards(db,world,base)
    if remaining:raise ValueError('Unresolved rewards: '+repr(remaining))
    current_bases(seed)
    statements,counts=differences(seed,world,CARD_TABLES)
    return seed,catalog_sql+statements,{**catalog_counts,**counts}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--client-db',type=Path)
    target=parser.add_mutually_exclusive_group(required=True)
    target.add_argument('--check',action='store_true')
    target.add_argument('--output',type=Path)
    args=parser.parse_args()
    if args.output and args.output.exists():parser.error('Output already exists; use a new pending update filename.')
    if args.client_db:
        db=sqlite3.connect(args.client_db.resolve().as_uri()+'?mode=ro',uri=True);db.row_factory=sqlite3.Row
    else:db=database('client')
    world=database('world')
    seed,statements,counts=regenerate(db,world)
    print('Card rows:',seed.execute('SELECT count(*) FROM divination_card_rewards').fetchone()[0],
          'enabled:',seed.execute('SELECT count(*) FROM divination_card_rewards WHERE enabled=1').fetchone()[0])
    print('Differences:',counts)
    if args.check:return int(bool(statements))
    if not statements:
        print('No update needed.');return 0
    args.output.parent.mkdir(parents=True,exist_ok=True)
    with args.output.open('x',encoding='utf-8',newline='\n') as output:
        output.write('-- Generated by scripts/re/divination_cards.py; local pool weights, not official probabilities.\n')
        output.write('\n'.join(statements)+'\n')
    return 0


if __name__=='__main__':
    raise SystemExit(main())
