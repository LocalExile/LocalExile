# Drop research roadmap

Working checklist for the currency, card and boss drop work. It exists so the
next question is chosen here, not asked again: client data first, then external
sources, then implementation and a live check. Status: 2026-11-08, draft PR
#709.

Legend: `[x]` done, `[~]` started / partial, `[ ]` not started. Every claim that
is not in the client tables needs a source and a confidence note.

## Phase 0. Inventory: what the client actually contains

- [x] Currency item rows: `BaseItemTypes` (`DropLevel`), `CurrencyItems`
  (`StackSize`, `Action`, `CurrencyUseType`); 1,962 rows, no drop weight.
- [x] Core currency table: `ItemDrops.Currency`, 24 entries, sum 4049.51.
- [x] Divination card rows: `BaseItemTypes` class `DivinationCard`, `DropLevel`;
  no area list in the client.
- [x] Boss identity: `WorldAreas.Bosses_MonsterVarietiesKeys`,
  `MonsterVarieties`, `Maps`, `MapSeriesTiers`, `UniqueMaps`,
  `AtlasPrimordialBosses`, `AtlasExileBossArenas`, `MavenFights`.
- [x] Boss inventory seeded from `WorldAreas` into `drop_bosses`, 997 areas / 630 boss
  varieties (394 campaign, 430 map, 140 unique-map, 24 Labyrinth, 9 other).
- [x] Card area table seeded (`DropDataSeed.CardAreas`),
  135 areas / 555 area-card pairs from PoEDB.
- [ ] A single generated report that joins currency, cards and bosses with
  their known sources; the current files are separate.

## Phase 1. Currency weights and sources

- [x] Document the current hardcoded table, its shares and the `DropLevel` gate
  ([currency-drops.md](currency-drops.md)).
- [x] Record the 3.26/3.28/3.29 changes: Regret lower, currency category larger,
  Exalted/Regal more common, Fracturing/Ancient core, Chromatic rarer.
- [x] Find the only full public distribution (2014 leak) and the 2024 Mirror /
  Exalted / Divine estimate.
- [x] Identify missing core types: Orb of Binding (25), Sacred Orb (35), Ancient
  Orb (35), Cartographer's Chisel (52), Fracturing Orb (68), Orb of Unmaking
  (70); Orb of Horizons removed in 3.28.
- [ ] Decide the baseline for a best-effort recalibration: 2014 shape corrected
  by the 3.26/3.28/3.29 notes, or a collected sample.
- [~] Add a `CurrencySource(Id, SourceKind, SourceId, DropLevel, Weight)` model:
  `core`, `boss`, `league`, `atlas`, `vendor`; the `drop_currency_sources` table
  and record exist, the core table is seeded and wired, the source rows are not.
- [ ] Add boss/league-only currency: Awakener's Orb (Sirus), Maven's Orb and Orb
  of Dominance (Maven, verify), Veiled (Betrayal), Eldritch (Exarch/Eater),
  Fracturing/Ancient (core), Cartographer's Chisel (maps), Orb of Unmaking
  (Atlas).
- [ ] Split the category weights: currency 35%, gems 4%, flasks 8%, equipment
  53% are also our numbers; 3.28 made currency a larger portion.
- [ ] Implement the drop penalty (Phase 4) against the currency/card categories.

## Phase 2. Boss drop tables

- [x] Inventory all client bosses (`drop_bosses`, from `WorldAreas`), categorized by
  campaign / map / unique map / Labyrinth / other.
- [x] Document the major unique pools in [boss-drops.md](boss-drops.md):
  Sirus, Shaper, Elder, Atziri, Maven, Searing Exarch, Eater of Worlds, Olroth,
  Lycia, Trialmaster, King in the Mists.
- [x] Record the only exact boss distribution we have: King in the Mists
  40/35/20/5%, Hextoad 6%, Hexpass 10%, The Endless Darkness 15%.
- [x] Record that exact per-boss unique chances are mostly unpublished; mark
  unknown rather than guess.
- [x] Add the community scarab weights: 109 scarabs from the poedata.dev MLE
  calculation (3.28.0; two 3.28 names are gone from the 3.29 client), seeded into
  `drop_item_weights`; the scarab pick follows them.
- [ ] Parse every boss page's unique tab into `DropDataSeed.BossItems` (major bosses done;
  430 map bosses and 140 unique-map bosses next).
- [ ] Add the **boss-only currency** rows to the currency source model; do not
  put them in the global core table.
- [ ] Add **boss-only cards** (`The Endless Darkness` is already in
  `ItemDrops.KingInMistsExtras`; the other boss cards are not).
- [ ] Collect community samples for the important chances (Maven's Orb ~25% from
  the 400-run sample is the current lead; verify).
- [~] Implement `BossDrop(BossId, ItemId, Chance, Source, Confidence)` and the
  boss hooks; the `drop_boss_items` table and snapshot exist, no roll reads them
  yet, and the generic unique roll stays the fallback.
- [ ] Decide how campaign bosses (mostly no unique guarantee) and map bosses
  (global pool vs map-specific pool) are handled.

## Phase 3. Divination cards

- [x] Document `DropLevel` for cards (The Doctor 75, House of Mirrors 80, The
  Apothecary 75, The Endless Darkness 81).
- [x] Seed the PoEDB Areas table (`DropDataSeed.CardAreas`)
  (135 areas, 555 pairs, minimum map tier).
- [x] Add the community card weights: 422 cards from the poedata.dev MLE
  calculation (3.27.0), seeded into `drop_item_weights`; the card pick follows
  them and falls back to uniform when no weighted card meets the DropLevel.
- [x] List boss/league examples: The Endless Darkness, The Breach, The Dreamer,
  The Calling, The Valkyrie, The Undaunted, The Admirer, The Professor.
- [x] Record the current bug: `ItemDrops.Card(Random rng)` ignores `DropLevel`,
  area, map tier, boss and league and picks uniformly from all cards.
- [ ] Implement `Card(rng, areaId, mapTier, monsterLevel, source)` with the
  intersection of the area table, `DropLevel <= monsterLevel` and the special
  source; keep the generic pool as a fallback only where the game has one.
- [ ] Handle disabled cards (`The Cartographer`, `The Obscured`, `The Puzzle`)
  and the campaign-area card pools (the PoEDB table is map/unique-map based).
- [ ] Wire the card drop chance: map mods (`CardDropChance`, `PacksDropCards`),
  Divination Scarabs, Atlas passives; chance and pool are separate.
- [ ] Verify a few cards live: The Doctor in Burial Chambers (tier 7+), The
  Apothecary in Cage (tier 7+), a boss/league card.

## Phase 4. Drop penalty and drop modifiers

- [x] Formula: `effective = min(level, 68); excess = max(0, effective -
  (areaMonsterLevel + 2)); multiplier = max(0, 1 - 0.025 * excess)`.
- [x] Sources: PoEDB Drop rate, PoE Wiki, the 2012 spreadsheet formula.
- [x] Record that LocalExile does not implement the penalty yet; `ItemDrops` has
  no character/area level.
- [ ] Implement the penalty for currency and cards (not equipment or gems).
- [ ] Check the order relative to item quantity and party bonuses against the
  client/official behaviour; do not guess.
- [ ] Audit item quantity/rarity: currency and cards are not affected by rarity;
  quantity increases the amount, not the type distribution.
- [ ] Audit map/scarab/Atlas modifiers that change card/currency drops.

## Phase 5. Data model and implementation

- [x] Define the source kinds and the boss/card schemas in prose.
- [x] Draft the SQLite schema and the seed files: [drop-data-schema.md](../dev/drop-data-schema.md).
- [x] Add typed tables for `DropCurrencyWeights`, `DropCurrencySources`,
  `DropBosses`, `DropBossItems`, `DropCardAreas`, `DropCardSources` and
  `DropItemWeights` in `GameDataStore`, seeded from the CSVs by `GameDataDrops`
  (schema version 5); the currency, card and scarab tables are wired into
  `ItemDrops`.
- [x] Keep the current `ItemDrops.Currency` core table as the fallback; no
  boss-only row goes into it without a source.
- [~] Loader validation: finite non-negative weights, `chance` in 0..1, the
  three confidence values and the decimal style are checked; unknown game ids
  and unsupported base types are skipped by the pool, not rejected at load.
- [~] Add tests (at most 2 per PR, Gateway only): seed and publication, empty
  fallback done; source filtering, card area/tier/DropLevel filter and King in
  the Mists distribution next.
- [~] Move the tables to editable data (SQLite, `game_data_meta`/`drop_seeded`);
  the `/game_data` edit commands and a re-seed on schema change are next.

## Phase 6. Verification and data collection

- [ ] Live-check `/item`, `/spawn_mob`, map bosses and the King in the Mists
  after the implementation; use `logs/`.
- [ ] Add a currency/card drop counter (a mod or a dev command) that records
  type, area, monster level, map tier and source; use it to collect a sample.
- [ ] Compute MLE/Bayesian weights from the sample, as PoeData.dev does.
- [ ] Cross-check source order with poe.ninja prices, not as weights.
- [ ] Check PoEDB/Prohibited Library for a newer full currency table before
  freezing the weights.

## Phase 7. PR sequence

1. **#709 (current, draft):** docs, source collection, the boss inventory,
   the card area table, boss/card cross-reference, plus the SQLite tables,
   the seed and the currency-weight wiring.
2. **#711 (stacked on #709):** poedata card/scarab weights and the base map
   card/scarab chances.
3. **Next:** card pool filtering by area/tier/`DropLevel` (one code PR, one or
   two tests).
4. **Next:** boss drop tables and boss-only currency (one code PR, one or two
   tests).
5. **Next:** currency baseline recalibration + missing core types (one code PR,
   two tests at most).
6. **Next:** drop penalty (one code PR, one test).
7. **Next:** `/game_data` edit commands, source rows and re-seeding.
8. **Later:** collector and MLE recalibration from live samples.

## Open questions

- Which exact source do we trust for a post-3.28 currency shape? No public full
  table exists; the 2014 leak plus patch notes is a baseline, not a verification.
- Are the map bosses' unique pools per map or a shared pool with a map-specific
  bias? The client data does not say.
- Do campaign bosses have any unique guarantees in 3.29, or only the global
  pool? The PoEDB pages are the first source to check.
- How should boss-only cards interact with the area table and the generic card
  pool? The current code has no distinction.
- The exact chance order with item quantity, party bonus and the drop penalty is
  unconfirmed; verify before implementing.

## Definition of done

- Every core currency has a `DropLevel`, a source kind and a documented weight
  (or an explicit `unknown`).
- Every boss in `drop_bosses` has its unique pool and a known/estimated chance,
  with a source and confidence.
- Every card in `drop_card_areas` is filtered by area, map tier and
  `DropLevel`; boss/league cards are separate.
- The drop penalty is implemented and checked against the client.
- The tables are editable without a rebuild, and the tests cover loading,
  filtering and the King in the Mists distribution.
