# Drop data schema (draft)

Draft, 2026-11-08, for the drop research in
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
```

The penalty constants are ordinary `system_settings` rows, not a table:

```
drop_penalty_level_offset = 2        -- no penalty up to area level + 2
drop_penalty_per_level_pct = 2.5     -- 2.5% per level past it
drop_penalty_character_level_cap = 68
```

## Seed files

The first implementation seeds the tables from the files already collected:

| Table | Seed |
|---|---|
| `drop_currency_weights` | [currency-weights.csv](../game/currency-weights.csv), 24 rows, the current hardcoded table |
| `drop_bosses` | [bosses.csv](../game/bosses.csv), 997 areas / 630 boss varieties |
| `drop_boss_items` | [boss-drops.csv](../game/boss-drops.csv), 108 rows: major boss unique pools, boss-only currency, the King in the Mists exact chances |
| `drop_card_areas` | [divination-card-areas.csv](../game/divination-card-areas.csv), 555 area/card pairs from PoEDB |
| `drop_card_sources` | the boss/league cards listed in [boss-drops.md](../game/boss-drops.md), with `chance`/`drop_level` |

`chance` is `NULL` when the source does not publish it. That is deliberate: it
means unknown, not zero. The King in the Mists uniques are the only exact boss
distribution currently seeded (40/35/20/5%); Maven's Orb is an estimated 25%.

## Load and edit rules

- On startup, if a drop table is empty, seed it from the CSV in one transaction;
  a table that already has rows is left alone. A later extract/seed replaces the
  reference rows only when they are still empty or when the schema version
  changes; edits are kept in the table (there is no separate override table for
  the first slice).
- Load the tables once into immutable dictionaries:
  `CoreCurrency`, `CurrencySources`, `Bosses`, `BossItems`, `CardAreas`,
  `CardSources`. Swap the snapshot under one lock; a roll reads the snapshot
  reference, never SQLite.
- The core currency table keeps the current `ItemDrops.Currency` values as the
  fallback when the table is empty or the database fails.
- The boss/card tables are empty in behaviour terms until the roll paths use
  them; the current generic unique/currency/card rolls stay the fallback.
- A bad row fails the load with a line naming the table, key and value. Accept
  nothing silently: unknown BaseItemTypes/MonsterVarieties ids, non-finite or
  negative weights, `chance` outside 0..1, duplicate keys and a missing
  confidence are errors.
- `drop_currency_sources` is the boss/league/atlas layer. A boss-only currency
  must not be added to `drop_currency_weights` without a `core` source; the
  core table is only the global monster pool.

## Commands and tests

The first implementation needs only loading and the existing `/game_data` list
counts; editing commands can follow the `unique_weight` pattern:

```
/game_data currency_weight <id> <weight>
/game_data boss_drop <boss-id> <item-id> <chance|unknown>
/game_data card_area <area-id> <card-id> <tier>
/game_data drop_reload
```

Tests (at most two in the first code PR):

1. A fresh store seeds 24 currency rows, 997 bosses, 108 boss items and 555 card
   areas; a second open does not duplicate them.
2. A source filter returns only the boss/league/atlas rows for its kind, and an
   empty table falls back to the hardcoded core table.

## Future mapping work

- `drop_card_areas.area_id` currently carries the PoEDB area name; map them to
  `WorldAreas.Id` (`Maps`/`UniqueMaps` names) before the card roll uses them.
- `drop_boss_items.item_id` uses a unique's name for uniques and a
  BaseItemTypes Id for currency/gems/cards; the loader must resolve both.
- The 430 map bosses and 140 unique-map bosses still need their own unique pools;
  the 108 rows here are the major bosses and the known extras.
- Exact boss chances need community samples; the table's `NULL` is the honest
  state until then.
