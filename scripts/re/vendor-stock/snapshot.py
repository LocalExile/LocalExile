"""Fetch factual campaign gem stock from two community tables; never modify runtime data.

Run from the repository root with requests and beautifulsoup4 installed.
"""
import json
from collections import defaultdict
from datetime import date
from pathlib import Path
import requests
from bs4 import BeautifulSoup

WIKI = 'https://www.poewiki.net/wiki/List_of_vendor_rewards'
DB = 'https://poedb.tw/us/QuestRewards'

def fetch(url):
    r = requests.get(url, timeout=60)
    r.raise_for_status()
    r.encoding = 'utf-8'
    return BeautifulSoup(r.text, 'html.parser')

def build():
    wiki, db = fetch(WIKI), fetch(DB)
    table = wiki.select_one('table.wikitable')
    classes = [c.get_text(strip=True) for c in table.select('tr')[0].find_all('th')][1:]
    assert len(classes) == 7
    w, d, vendors = defaultdict(set), defaultdict(set), defaultdict(set)
    for tr in table.select('tr')[2:]:
        cells = tr.find_all(['th', 'td'], recursive=False)
        if not cells or not cells[0].select('a'): continue
        links = cells[0].select('a')
        quest = links[0].get_text(strip=True)
        vendor = links[-1].get_text(strip=True)
        vendors[quest].add(vendor)
        col = 0
        for cell in cells[1:]:
            span = int(cell.get('colspan', 1))
            names = {a.get_text(strip=True) for a in cell.select('a')}
            for cls in classes[col:col+span]: w[quest, vendor, cls].update(names)
            col += span
        assert col == 7, (quest, col)
    table = db.select_one('#QuestVendorRewards table')
    dbclasses = [c.get_text(strip=True) for c in table.select('thead th')][1:]
    assert set(dbclasses) == set(classes)
    for tr in table.select('tbody tr'):
        cells = tr.find_all('td', recursive=False)
        assert len(cells) == 8
        quest = cells[0].get_text(strip=True)
        for cls, cell in zip(dbclasses, cells[1:]):
            d[quest, cls].update(a.get_text(strip=True) for a in cell.select('a'))
    records = []
    for (quest, vendor, cls), gems in sorted(w.items()):
        other = d.get((quest, cls))
        for gem in sorted(gems | (other or set())):
            records.append(dict(vendor=vendor, quest=quest, character_class=cls, gem=gem,
                wiki=gem in gems, poedb=None if other is None else gem in other,
                comparison='not-covered' if other is None else 'agree' if gem in gems and gem in other else 'disputed',
                vendor_source='wiki', requirement='quest completion; vendor exceptions require separate review'))
    assert len(records) > 1000
    result = dict(retrieved=str(date.today()), scope='Community campaign gem stock; standard mode; research only, not runtime policy',
        sources=dict(wiki=WIKI, poedb=DB), caveat='PoEDB groups by quest/class, not NPC. Agreement does not independently verify vendor identity or pinned-client parity.',
        records=records)
    out = Path('scripts/re/vendor-stock/community-stock.json')
    out.write_text(json.dumps(result, indent=2, ensure_ascii=False)+'\n', encoding='utf-8')
    print(json.dumps(dict(rows=len(records), gems=len({r['gem'] for r in records}),
        comparisons={s:sum(r['comparison']==s for r in records) for s in ['agree','disputed','not-covered']})))

if __name__ == '__main__': build()
