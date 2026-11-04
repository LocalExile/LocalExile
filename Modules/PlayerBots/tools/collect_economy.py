"""Collect the poe.ninja economy of a league into PlayerBots' price grid and market-turnover statistics.

Run from the repository root:  python Modules/PlayerBots/tools/collect_economy.py [--league Allflame] [--no-history]

Covers every category in poe.ninja's economy menu (the left column). poe.ninja has two kinds:
- exchange categories (currency, fragments, scarabs, ...): a value in chaos and the chaos volume traded on the
  currency exchange; --history (default) also takes each item's daily rate and volume for the last weeks;
- stash categories (uniques, gems, maps, base types, ...): a value and the number of listings, no trade volume.

Writes (economy/ is gitignored: the snapshot stays local):
  Modules/PlayerBots/economy/<league>/raw/<UTC date>/<category>.json   the responses, as fetched
  Modules/PlayerBots/economy/<league>/prices.json                      the price grid (chaos, divine, alchemy, regal)
  Modules/PlayerBots/economy/<league>/turnover.json                    per-item volume, units a day, daily history
  Modules/PlayerBots/economy/<league>/summary.json                     totals per category and for the market
"""
import argparse
import datetime as dt
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

API = "https://poe.ninja/poe1/api/economy"
# The economy menu, in its order: page slug -> (api kind, api type). Empty in a league means poe.ninja has no data there.
CATEGORIES = {
    "currency": ("exchange", "Currency"), "fragments": ("exchange", "Fragment"), "wombgifts": ("stash", "Wombgift"),
    "runegrafts": ("exchange", "Runegraft"), "allflame-embers": ("exchange", "AllflameEmber"), "corpses": ("stash", "Corpse"),
    "tattoos": ("exchange", "Tattoo"), "omens": ("exchange", "Omen"), "djinn-coins": ("exchange", "DjinnCoin"),
    "ducats": ("exchange", "Ducat"), "enshrouding-crystals": ("exchange", "EnshroudingCrystal"),
    "divination-cards": ("exchange", "DivinationCard"), "artifacts": ("exchange", "Artifact"), "oils": ("exchange", "Oil"),
    "incubators": ("exchange", "Incubator"), "unique-weapons": ("stash", "UniqueWeapon"),
    "unique-armours": ("stash", "UniqueArmour"), "unique-accessories": ("stash", "UniqueAccessory"),
    "unique-flasks": ("stash", "UniqueFlask"), "unique-jewels": ("stash", "UniqueJewel"),
    "forbidden-jewels": ("stash", "ForbiddenJewel"), "shrine-belts": ("stash", "ShrineBelt"),
    "unique-tinctures": ("stash", "UniqueTincture"), "unique-relics": ("stash", "UniqueRelic"),
    "skill-gems": ("stash", "SkillGem"), "imbued-gems": ("stash", "ImbuedGem"), "cluster-jewels": ("stash", "ClusterJewel"),
    "maps": ("stash", "Map"), "blighted-maps": ("stash", "BlightedMap"), "blight-ravaged-maps": ("stash", "BlightRavagedMap"),
    "unique-maps": ("stash", "UniqueMap"), "valdo-maps": ("stash", "ValdoMap"), "delirium-orbs": ("exchange", "DeliriumOrb"),
    "invitations": ("stash", "Invitation"), "scarabs": ("exchange", "Scarab"), "astrolabes": ("exchange", "Astrolabe"),
    "memories": ("stash", "Memory"), "temples": ("stash", "IncursionTemple"), "scrying-orbs": ("stash", "ScryingOrb"),
    "base-types": ("stash", "BaseType"), "flasks": ("stash", "Flask"), "fossils": ("exchange", "Fossil"),
    "resonators": ("exchange", "Resonator"), "beasts": ("stash", "Beast"), "essences": ("exchange", "Essence"),
    "vials": ("stash", "Vial"),
}
# The currencies every price is also given in (exchange ids in the Currency overview).
UNITS = {"chaos": "chaos", "divine": "divine", "alchemy": "alch", "regal": "regal"}
DELAY = 0.6  # seconds between requests: poe.ninja is a free service


def fetch(path, **query):
    url = f"{API}/{path}?{urllib.parse.urlencode(query)}"
    request = urllib.request.Request(url, headers={"User-Agent": "LocalExile-PlayerBots/0.1 (economy research)"})
    for attempt in range(4):
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                time.sleep(DELAY)
                return json.load(response)
        except urllib.error.HTTPError as error:
            if error.code == 404:
                return None
            if error.code != 429 and error.code < 500:
                raise
        except (urllib.error.URLError, TimeoutError):
            pass
        time.sleep(5 * (attempt + 1))
    raise RuntimeError(f"poe.ninja did not answer: {url}")


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--league", default="Allflame")
    parser.add_argument("--no-history", action="store_true", help="skip the per-item daily history (one request per item)")
    args = parser.parse_args()
    now = dt.datetime.now(dt.timezone.utc)
    root = os.path.join("Modules", "PlayerBots", "economy", args.league.lower().replace(" ", "-"))
    raw = os.path.join(root, "raw", now.strftime("%Y-%m-%d"))

    items, turnover = [], []
    for slug, (kind, api_type) in CATEGORIES.items():
        if kind == "exchange":
            data = fetch("exchange/current/overview", league=args.league, type=api_type) or {}
            meta = {i["id"]: i for i in data.get("items", [])}
            for line in data.get("lines", []):
                info = meta.get(line["id"], {})
                items.append({"category": slug, "id": line["id"], "name": info.get("name", line["id"]),
                              "detailsId": info.get("detailsId", line["id"]), "group": info.get("category"),
                              "chaos": line.get("primaryValue"), "volumeChaosPerDay": line.get("volumePrimaryValue"),
                              "change7d": (line.get("sparkline") or {}).get("totalChange"), "source": "exchange", "apiType": api_type})
        else:
            data = fetch("stash/current/item/overview", league=args.league, type=api_type) or {}
            for line in data.get("lines", []):
                name = line.get("name")
                variant = " ".join(str(v) for v in (line.get("variant"), f"{line['links']}L" if line.get("links") else None) if v)
                items.append({"category": slug, "id": line.get("detailsId") or line.get("id"),
                              "name": f"{name} ({variant})" if variant else name, "baseType": line.get("baseType"),
                              "chaos": line.get("chaosValue"), "listings": line.get("listingCount"),
                              "change7d": (line.get("sparkLine") or {}).get("totalChange"), "source": "stash", "apiType": api_type})
        write(os.path.join(raw, f"{slug}.json"), data)
        print(f"{slug:22} {kind:8} {len(data.get('lines', [])):5} item(s)", flush=True)

    # The exchange rates every price is converted with, from the Currency overview (chaos is 1).
    rate = {"chaos": 1.0}
    for item in items:
        if item["category"] == "currency":
            for unit, exchange_id in UNITS.items():
                if item["id"] == exchange_id and item["chaos"]:
                    rate[unit] = item["chaos"]
    missing = [u for u in UNITS if u not in rate]
    if missing:
        print(f"warning: no Currency rate for {missing}; those columns are empty", file=sys.stderr)

    prices = []
    for item in items:
        chaos = item["chaos"]
        entry = {k: item[k] for k in ("category", "id", "name", "source") if item.get(k) is not None}
        for unit in UNITS:
            entry[unit] = round(chaos / rate[unit], 6) if chaos is not None and unit in rate else None
        if item.get("listings") is not None:
            entry["listings"] = item["listings"]
        prices.append(entry)

    # Turnover: the currency exchange reports the chaos value traded; units = volume / value. Every exchange trade is one
    # side buying and the other selling, so bought and sold units a day are the same number. Stash categories have no
    # trades on poe.ninja, only listings (offers): their turnover is unknown, the listing count shows the supply.
    for item in items:
        if item["source"] != "exchange":
            continue
        chaos, volume = item["chaos"], item.get("volumeChaosPerDay")
        units = round(volume / chaos, 2) if chaos and volume is not None else None
        entry = {"category": item["category"], "id": item["id"], "name": item["name"], "chaos": chaos,
                 "volumeChaosPerDay": volume, "unitsTradedPerDay": units, "change7d": item.get("change7d")}
        if not args.no_history:
            details = fetch("exchange/current/details", league=args.league, type=item["apiType"], id=item["detailsId"]) or {}
            pair = next((p for p in details.get("pairs", []) if p.get("id") == "chaos"), None)
            if pair:
                entry["history"] = [{"day": h["timestamp"][:10], "rate": h.get("rate"), "volumeChaos": h.get("volumePrimaryValue")}
                                    for h in pair.get("history", [])]
        turnover.append(entry)
    if not args.no_history:
        print(f"history: {sum(1 for t in turnover if t.get('history'))} of {len(turnover)} exchange item(s)")

    by_category = {}
    for slug, (kind, _) in CATEGORIES.items():
        rows = [i for i in items if i["category"] == slug]
        volume = sum(i.get("volumeChaosPerDay") or 0 for i in rows)
        by_category[slug] = {"source": kind, "items": len(rows),
                             "volumeChaosPerDay": round(volume, 2) if kind == "exchange" else None,
                             "listings": sum(i.get("listings") or 0 for i in rows) if kind == "stash" else None}
    market = sum(c["volumeChaosPerDay"] or 0 for c in by_category.values())
    stamp = {"league": args.league, "collectedUtc": now.isoformat(timespec="seconds"), "source": "poe.ninja", "rates": rate}
    write(os.path.join(root, "prices.json"), {**stamp, "items": prices})
    write(os.path.join(root, "turnover.json"), {**stamp, "items": sorted(turnover, key=lambda t: -(t["volumeChaosPerDay"] or 0))})
    write(os.path.join(root, "summary.json"), {**stamp, "items": len(items), "exchangeVolumeChaosPerDay": round(market, 2),
                                               "exchangeVolumeDivinePerDay": round(market / rate["divine"], 2) if "divine" in rate else None,
                                               "categories": by_category})
    print(f"{len(items)} item(s); exchange volume {market:,.0f} chaos a day -> {root}")


if __name__ == "__main__":
    main()
