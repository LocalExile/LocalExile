# Drop data schema

2026-11-08, for the drop research in
[currency-drops.md](../game/currency-drops.md), [boss-drops.md](../game/boss-drops.md)
and [drop-research-roadmap.md](../game/drop-research-roadmap.md). The database is
the same SQLite overlay as [editable-game-data.md](editable-game-data.md)
(`data/saves.db`, or `Game_SAVES_DB`); this page is the drop-specific slice.

## Why a table, not more code

`ItemDrops.Currency`, the boss unique pools and the card area lists are
server-side policy, not client data. They change between patches, and the exact
chances are often only community estimates. A typed table lets the data be
reviewed, seeded and edited without a rebuild, while the roll paths keep reading
one immutable in-memory snapshot. No drop roll queries SQLite.

## Tables

```sql
CREATE TABLE IF NOT EXISTS drop_currency_weights (
    currency_id TEXT PRIMARY KEY,              -- BaseItemTypes.Id
    weight REAL NOT NULL CHECK (weight >= 0),  -- relative weight inside the core pool
    drop_level INTEGER NOT NULL CHECK (drop_level >= 0),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    source TEXT NOT NULL,
    confidence TEXT NOT NULL CHECK (confidence IN ('verified', 'estimated', 'unknown')),
    notes TEXT,
    updated_utc TEXT NOT NULL
) STRICT;

CREATE TABLE IF NOT EXISTS drop_currency_sources (
    currency_id TEXT NOT NULL,
    source_kind TEXT NOT NULL CHECK (source_kind IN ('core', 'boss', 'league', 'atlas', 'vendor')),
    source_id TEXT NOT NULL,                   -- boss variety id, league id, area id, vendor id, or ''
    weight REAL NOT NULL CHECK (weight >= 0),
    drop_level INTEGER NOT NULL CHECK (drop_level >= 0),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    confidence TEXT NOT NULL CHECK (confidence IN ('verified', 'estimated', 'unknown')),
    source TEXT NOT NULL,
    notes TEXT,
    updated_utc TEXT NOT NULL,
    PRIMARY KEY (currency_id, source_kind, source_id)
) STRICT;

CREATE TABLE IF NOT EXISTS drop_bosses (
    boss_id TEXT PRIMARY KEY,                  -- MonsterVarieties.Id (Metadata/...)
    boss_name TEXT NOT NULL,
    area_id TEXT,                              -- WorldAreas.Id when known
    category TEXT NOT NULL,                    -- campaign, map, unique-map, labyrinth, league, pinnacle, other
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    updated_utc TEXT NOT NULL
) STRICT;

CREATE TABLE IF NOT EXISTS drop_boss_items (
    boss_id TEXT NOT NULL,
    item_id TEXT NOT NULL,                     -- BaseItemTypes.Id, or a unique's name
    item_kind TEXT NOT NULL CHECK (item_kind IN ('unique', 'currency', 'gem', 'card', 'other')),
    chance REAL CHECK (chance IS NULL OR (chance >= 0 AND chance <= 1)),  -- NULL = unknown, not zero
    confidence TEXT NOT NULL CHECK (confidence IN ('verified', 'estimated', 'unknown')),
    source TEXT NOT NULL,
    notes TEXT,
    updated_utc TEXT NOT NULL,
    PRIMARY KEY (boss_id, item_id)
) STRICT;

CREATE TABLE IF NOT EXISTS drop_card_areas (
    area_id TEXT NOT NULL,                     -- WorldAreas.Id; PoEDB name until mapped
    area_name TEXT NOT NULL,                   -- PoEDB area name, for review and mapping
    card_id TEXT NOT NULL,                     -- BaseItemTypes.Id
    min_map_tier INTEGER NOT NULL CHECK (min_map_tier BETWEEN 1 AND 16),
    source TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    PRIMARY KEY (area_id, card_id)
) STRICT;

CREATE TABLE IF NOT EXISTS drop_card_sources (
    card_id TEXT NOT NULL,
    source_kind TEXT NOT NULL CHECK (source_kind IN ('area', 'boss', 'league')),
    source_id TEXT NOT NULL,
    min_map_tier INTEGER CHECK (min_map_tier IS NULL OR min_map_tier BETWEEN 1 AND 16),
    drop_level INTEGER NOT NULL,
    confidence TEXT NOT NULL CHECK (confidence IN ('verified', 'estimated', 'unknown')),
    source TEXT NOT NULL,
    notes TEXT,
    updated_utc TEXT NOT NULL,
    PRIMARY KEY (card_id, source_kind, source_id)
) STRICT;

CREATE TABLE IF NOT EXISTS drop_item_weights (
    category TEXT NOT NULL CHECK (category IN ('scarab', 'card', 'essence', 'fossil', 'catalyst',
        'delirium-orb', 'breachstone', 'legion-splinter', 'runegraft')),
    item_id TEXT NOT NULL,                     -- BaseItemTypes.Id
    weight REAL NOT NULL CHECK (weight >= 0),  -- relative inside its category
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    source TEXT NOT NULL,                      -- the dataset URL
    confidence TEXT NOT NULL CHECK (confidence IN ('verified', 'estimated', 'unknown')),
    patch TEXT NOT NULL,                       -- the patch the sample is from
    sampled_at TEXT NOT NULL,                  -- the dataset's date
    notes TEXT,
    updated_utc TEXT NOT NULL,
    PRIMARY KEY (category, item_id)
) STRICT;
```

The penalty constants are ordinary `system_settings` rows, not a table:

```
drop_penalty_level_offset = 2        -- no penalty up to area level + 2
drop_penalty_per_level_pct = 2.5     -- 2.5% per level past it
drop_penalty_character_level_cap = 68
```

## Seed files

The seed lives in `Agents/docs/game`, beside the research it came from; the
Gateway's `GameDataDrops` reads it when the drop tables hold an older seed
generation, and `scripts/release/build-release.ps1` copies these files into the
package so a release seeds itself too:

| Table | Seed |
|---|---|
| `drop_currency_weights` | [currency-weights.csv](../game/currency-weights.csv), 24 rows, the current hardcoded table |
| `drop_bosses` | [bosses.csv](../game/bosses.csv), 997 area rows / 630 distinct boss ids |
| `drop_boss_items` | [boss-drops.csv](../game/boss-drops.csv), 108 rows: major boss unique pools, boss-only currency, the King in the Mists exact chances |
| `drop_card_areas` | [divination-card-areas.csv](../game/divination-card-areas.csv), 555 area/card pairs from PoEDB |
| `drop_card_sources` | the boss cards in `boss-drops.csv` (currently The Endless Darkness), derived on seed |
| `drop_item_weights` | [item-weights.csv](../game/item-weights.csv), 531 rows: 422 cards and 109 scarabs from the poedata.dev MLE calculations (cards 3.27.0, scarabs 3.28.0) |

`chance` is `NULL` when the source does not publish it. That is deliberate: it
means unknown, not zero. The King in the Mists uniques are the only exact boss
distribution currently seeded (40/35/20/5%); Maven's Orb is an estimated 25%.
`confidence` is one of the three values in the DDL; a wiki sample is `estimated`
and the citation stays in `source`/`notes`. The seed files use either decimal
comma or point (`0,15` or `0.15`). The poedata weights are community MLE
estimates, so they are `estimated`, and `source`/`patch`/`sampled_at` keep the
provenance. `GameDataDrops.SeedVersion` is bumped when the seed changes; a
database seeded by an older generation is refilled once.

## Load and edit rules

- On startup, if `game_data_meta` has no `drop_seeded`, or its
  `drop_seed_version` is older than `GameDataDrops.SeedVersion`, seed every table
  from the CSV in one transaction and record both. Later starts keep the rows as
  they are, so an edit survives; a new seed generation refills them once.
- Load the tables once into immutable dictionaries on
  `DropDataProvider.Snapshot`: `CurrencyWeights`, `CurrencySources`, `Bosses`,
  `BossItems`, `CardAreas`, `CardSources`, `ItemWeights`. Swap the snapshot
  reference; a roll reads it, never SQLite.
- `ItemDrops.Build` takes the currency pool from `CurrencyWeights` when it is
  non-empty and enabled/positive, and otherwise from the compiled
  `ItemDrops.Currency` table. The client's `BaseItemTypes.DropLevel` still gates
  the roll: `drop_currency_weights.drop_level` mirrors the source and is what a
  boss/league source layer will use.
- `ItemDrops.Card` and `ItemDrops.Scarab` pick by `ItemWeights` when the category
  has enabled rows covering the monster's `DropLevel`, and are uniform over the
  client's cards/scarabs otherwise. A map kill also rolls the base chances
  `drop_card_chance_pct` (0.5) and `drop_scarab_chance_pct` (0.2) per kill,
  scaled by item quantity; both are our own numbers and editable live. The map
  device's card/scarab stats stay separate.
- The boss tables are stored but no roll reads them yet; the current generic
  unique rolls stay the fallback.
- A bad row fails the load with a line naming the table, key and value. Accept
  nothing silently: non-finite or negative weights, `chance` outside 0..1,
  duplicate keys and a confidence outside the three values are errors.
- `drop_currency_sources` is the boss/league/atlas layer and is not seeded yet.
  A boss-only currency must not be added to `drop_currency_weights` without a
  `core` source; the core table is only the global monster pool.


## Commands and tests

This slice implements loading; the Gateway logs the seeded counts at startup.
Editing commands can follow the `unique_weight` pattern:

```
/game_data currency_weight <id> <weight>
/game_data item_weight <category> <item-id> <weight>
/game_data boss_drop <boss-id> <item-id> <chance|unknown>
/game_data card_area <area-id> <card-id> <tier>
/game_data drop_reload
```

Tests:

1. A fresh store seeds a fixture folder once (a second seed does nothing) and the
   provider publishes the currency, boss, boss-item, card-area and item-weight rows.
2. A store with no seeded drop table leaves the provider empty, so
   `ItemDrops.Build` keeps the compiled currency fallback.
3. A seeded card/scarab pool returns only weighted, enabled items at the
   monster's DropLevel, and the uniform fallback still applies the gate.

## Future mapping work

- `drop_card_areas.area_id` currently carries the PoEDB area name; map them to
  `WorldAreas.Id` (`Maps`/`UniqueMaps` names) before the card roll uses them.
- `drop_boss_items.item_id` uses a unique's name for uniques and a
  BaseItemTypes Id for currency/gems/cards; the loader must resolve both.
- Two of the 111 poedata scarab weights name scarabs the 3.29 client no longer
  has (Abyss Scarab of Edifice, Abyss Scarab of Profound Depth); they are skipped,
  leaving 109.
- The 430 map bosses and 140 unique-map bosses still need their own unique pools;
  the 108 rows here are the major bosses and the known extras.
- Exact boss chances need community samples; the table's `NULL` is the honest
  state until then.
