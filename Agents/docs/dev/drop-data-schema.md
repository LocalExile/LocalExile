# Drop data schema

2026-11-08, for the drop research in
[currency-drops.md](../game/currency-drops.md), [boss-drops.md](../game/boss-drops.md)
and [drop-research-roadmap.md](../game/drop-research-roadmap.md). The tables are
world.db content ([databases](databases.md), [editable game data](editable-game-data.md));
this page is the drop-specific slice.

## Why a table, not more code

`ItemDrops.Currency`, the boss unique pools and the card area lists are
server-side policy, not client data. They change between patches, and the exact
chances are often only community estimates. A typed table lets the data be
reviewed and changed without a rebuild, while the roll paths keep reading
one immutable in-memory snapshot. No drop roll queries SQLite.

## Tables

The schema is `sql/base/world/` (`drop_currency_weights`, `drop_currency_sources`, `drop_bosses`,
`drop_boss_items`, `drop_card_areas`, `drop_card_sources`, `drop_item_weights`); each row keeps its `source`,
`confidence` (`verified`, `estimated`, `unknown`) and notes beside the value.

The penalty constants are ordinary `system_settings` rows, not a table:

```
drop_penalty_level_offset = 2        -- no penalty up to area level + 2
drop_penalty_per_level_pct = 2.5     -- 2.5% per level past it
drop_penalty_character_level_cap = 68
```

## Content

The rows are world.db's first drop update (`sql/updates/world/*_drop_tables.sql`), written once from the former code
seed; nothing is read from the docs:

| Table | Rows |
|---|---|
| `drop_currency_weights` | `ItemDrops.Currency` (24 rows), each with its `BaseItemTypes.DropLevel` |
| `drop_bosses` | `WorldAreas.Bosses_MonsterVarietiesKeys` (630 distinct boss ids); the category comes from the area's `IsUniqueMapArea`/`IsMapArea`/`IsLabyrinthArea`/`IsLabyrinthBossArea` flags, else `campaign` (act > 0) or `other` |
| `drop_boss_items` | 108 rows: major boss unique pools (PoEDB), boss-only currency, the King in the Mists exact chances |
| `drop_card_areas` | 555 area/card pairs from PoEDB |
| `drop_card_sources` | the boss cards in `drop_boss_items` (currently The Endless Darkness) |
| `drop_item_weights` | 531 rows: 422 cards and 109 scarabs from the poedata.dev MLE calculations (cards 3.27.0, scarabs 3.28.0) |

`chance` is `NULL` when the source does not publish it. That is deliberate: it
means unknown, not zero. The King in the Mists uniques are the only exact boss
distribution in the tables (40/35/20/5%); Maven's Orb is an estimated 25%.
`confidence` is one of the three values above; a wiki sample is `estimated`
and the citation stays in `source`/`notes`. The poedata weights are community MLE
estimates, so they are `estimated`, and `source`/`patch`/`sampled_at` keep the
provenance. A change to the rows is a world update file (`sql/updates/pending_world/`).

## Load rules

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
  scaled by item quantity; both are our own numbers (`system_settings`). The map
  device's card/scarab stats stay separate.
- The boss tables are stored but no roll reads them yet; the current generic
  unique rolls stay the fallback.
- A bad row fails the load with a line naming the table, key and value. Accept
  nothing silently: non-finite or negative weights, `chance` outside 0..1,
  duplicate keys and a confidence outside the three values are errors.
- `drop_currency_sources` is the boss/league/atlas layer and has no rows yet.
  A boss-only currency must not be added to `drop_currency_weights` without a
  `core` source; the core table is only the global monster pool.


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

### Encounter jewel overrides

A server owner can set an independent per-kill probability for a named encounter jewel on its existing boss row
in world.db. The reference chance stays NULL when unpublished. For example, this gives the local Elder a 10%
chance to grant a two-mod Watcher's Eye. Set the override to 0 to disable it, or delete it to use the reference
chance. Restart the Gateway or reload world data to publish the change.

```sql
INSERT INTO encounter_jewel_drop_overrides (boss_id, item_id, chance)
VALUES ('Metadata/Monsters/AtlasBosses/TheElder/TheElder', 'Watcher''s Eye', 0.10)
ON CONFLICT (boss_id, item_id) DO UPDATE SET chance = excluded.chance;
```
