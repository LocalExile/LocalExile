# Currency drops: drop levels, weights and the drop penalty

Draft, 2026-11-08. This page collects the client's `DropLevel` data, the server's
currency weights and the currency/divination-card drop penalty. It is data
collection, not a protocol page: the loot policy itself is in
[15-progression.md](../protocol/15-progression.md) §3.

## 1. `DropLevel` is a minimum monster level

`BaseItemTypes.DropLevel` is the minimum level of the monster (or the area's
monster level, for area-wide rolls) at which the type may enter a drop pool.
`ItemDrops.RollOne` picks only types with `DropLevel <= level`; the caller passes
the killed monster's level (`ItemDrops.Roll(m.Level, ...)`). It is a **gate, not a
chance**: a type with `DropLevel` 35 can drop from any monster at level 35 or
above, and the weights decide whether it does. The item's `ItemLevel` is a
separate field.

The client data has no currency drop weights. `CurrencyItems` carries `StackSize`,
`Action` and `CurrencyUseType`; `BaseItemTypes` carries `DropLevel`. The weights
are the Gateway's own table.

## 2. The current hardcoded table

`src/GameServer.Gateway/Item/ItemDrops.cs` (`Currency`): 24 entries, sum 4049.51.
The category is 35% of an item drop (`drop_multiplier_currency` scales that
category only). Shares below are conditional on a currency drop.

| Currency | BaseItemTypes Id | DropLevel | Weight | Share |
|---|---|---:|---:|---:|
| Scroll of Wisdom | `CurrencyIdentification` | 1 | 1000 | 24.694% |
| Portal Scroll | `CurrencyPortal` | 2 | 300 | 7.408% |
| Orb of Transmutation | `CurrencyUpgradeToMagic` | 1 | 500 | 12.347% |
| Armourer's Scrap | `CurrencyArmourQuality` | 1 | 450 | 11.113% |
| Blacksmith's Whetstone | `CurrencyWeaponQuality` | 1 | 300 | 7.408% |
| Orb of Augmentation | `CurrencyAddModToMagic` | 1 | 400 | 9.878% |
| Orb of Alteration | `CurrencyRerollMagic` | 2 | 250 | 6.174% |
| Chromatic Orb | `CurrencyRerollSocketColours` | 20 | 200 | 4.939% |
| Jeweller's Orb | `CurrencyRerollSocketNumbers` | 8 | 150 | 3.704% |
| Orb of Chance | `CurrencyUpgradeRandomly` | 2 | 120 | 2.963% |
| Orb of Alchemy | `CurrencyUpgradeToRare` | 2 | 90 | 2.222% |
| Glassblower's Bauble | `CurrencyFlaskQuality` | 2 | 60 | 1.482% |
| Orb of Fusing | `CurrencyRerollSocketLinks` | 8 | 50 | 1.235% |
| Chaos Orb | `CurrencyRerollRare` | 12 | 40 | 0.988% |
| Orb of Scouring | `CurrencyConvertToNormal` | 12 | 30 | 0.741% |
| Orb of Regret | `CurrencyPassiveRefund` | 2 | 25 | 0.617% |
| Vaal Orb | `CurrencyCorrupt` | 12 | 25 | 0.617% |
| Regal Orb | `CurrencyUpgradeMagicToRare` | 12 | 20 | 0.494% |
| Gemcutter's Prism | `CurrencyGemQuality` | 12 | 20 | 0.494% |
| Blessed Orb | `CurrencyRerollImplicit` | 35 | 15 | 0.370% |
| Exalted Orb | `CurrencyAddModToRare` | 35 | 2 | 0.049% |
| Orb of Annulment | `CurrencyRemoveMod` | 35 | 1.5 | 0.037% |
| Divine Orb | `CurrencyModValues` | 35 | 1 | 0.025% |
| Mirror of Kalandra | `CurrencyDuplicate` | 35 | 0.01 | 0.00025% |

The table is not the whole core currency set. Examples of types with a
`DropLevel` that are not in it:

| Currency | BaseItemTypes Id | DropLevel | Note |
|---|---|---:|---|
| Orb of Binding | `CurrencyUpgradeToRareAndSetSockets` | 25 | core orb, missing |
| Sacred Orb | `CurrencyRerollDefences` | 35 | source needs checking |
| Ancient Orb | `CurrencyRerollUnique` | 35 | core drop since 3.28 |
| Cartographer's Chisel | `CurrencyMapQuality` | 52 | map currency; 3.25 follows map drop rules |
| Fracturing Orb | `CurrencyFractureRare` | 68 | core drop since 3.28 |
| Orb of Unmaking | `CurrencyAtlasPassiveRefund` | 70 | Atlas currency |
| Orb of Horizons | `CurrencyRerollMapType` | 68 | no longer obtained in 3.28 |

Eldritch, Tainted, Foulborn, coin, omen and similar items have their own sources
and are not part of the ordinary orb pool. `CurrencyItems` has 1,962 rows in
3.29.3c, including cards, scarabs, essences and map fragments; only the core orb
table above is used by `ItemDrops.Currency`.

## 3. Recent changes to relative currency rates

These are the fresh data points; the full public table is only the 2014 leak
(§7), so treat these as corrections to its shape.

| Version | Change | Consequence |
|---|---|---|
| 3.26 | Orb of Regret drop chance lowered; distributed across the other currency items | Regret weight should be lower than the 2014 shape |
| 3.28 | Currency items are a significantly larger portion of dropped items; non-unique equipment is about 6% less common; Ruthless unaffected | the 35% currency category is too low for 3.28+ |
| 3.28 | Relative rarities adjusted; Exalted and Regal Orbs comparatively more common | Exalted/Regal weights should rise |
| 3.28 | Fracturing and Ancient Orbs enter the core drop pool | add both to the table |
| 3.28 | Quality currency from Synthesised Monsters reduced | not a core-table row |
| 3.29 | Chromatic Orbs are rarer after the socket changes | lower the 200 weight |
| 3.29 | Currency, fragments and cards stack up to 10,000 in stash tabs | stack size only, not a weight |

A 2024 community estimate puts the Mirror of Kalandra at **0.000033%** of
currency drops, with Exalted and Divine around **0.055%**. The current table's
Mirror share is 0.00025% (about 7.5 times higher); Divine is 0.025% (about half
the 0.055% figure). These are estimates, not server constants.

## 4. The currency/divination-card drop penalty

PoEDB's [Drop rate](https://poedb.tw/us/Drop_rate) page (same as the wiki's
[Drop Rate](https://www.poewiki.net/wiki/Drop_Rate)):

- It applies to **currency and divination cards**, not equipment or gems, when
  the area's monster level is more than two levels below the character's level.
- The character level is capped at 68 for this calculation.
- Each level beyond `areaMonsterLevel + 2` reduces the chance by 2.5% (linear,
  floored at zero):

```
effectiveLevel = min(characterLevel, 68)
excess         = max(0, effectiveLevel - (areaMonsterLevel + 2))
multiplier     = max(0, 1 - 0.025 * excess)
```

Examples from the page: level 30 in a level 20 area gives `excess = 8` and
`multiplier = 0.80` (20% less); level 30 in a level 28 area gives no penalty;
level 75 in a level 66 area caps at 68 and also gives none.

`ItemDrops.Roll` / `RollOne` do **not** implement this penalty yet. They receive
the monster's level and rarity, but not the character's level or the area's
monster level; adding it needs both, applied to the currency (and, once cards
exist, card) category. The order relative to item quantity is not documented
here; do not guess it.

## 5. Boss, league and area-specific sources

`ItemDrops.Currency` is the global core pool, and `ItemDrops.Card` picks a
uniform random card from every supported card. Neither models the game's
restricted pools: some currency is boss- or league-only, and every card has an
area, a minimum map tier, a `DropLevel`, or a boss/league source. The weights
cannot be treated as complete until those sources are part of the model.

### 5.1 Currency

| Item | Restriction | Evidence |
|---|---|---|
| Awakener's Orb | Sirus, Awakener of Worlds | PoEDB item page, "Dropped by" |
| Maven's Orb, Orb of Dominance | The Maven | game knowledge; verify against the item pages |
| Veiled Chaos/Exalted | Betrayal safehouses/Catarina | game knowledge; verify |
| Eldritch Chaos/Exalted/Annulment | Searing Exarch/Eater of Worlds | game knowledge; verify |
| Fracturing Orb, Ancient Orb | core drop since 3.28 | 3.28 patch notes |
| Cartographer's Chisel | map currency; map drop rules | 3.25 patch notes |
| Orb of Unmaking | Atlas currency | client `DropLevel` 70 |
| Breach, Timeless and Simulacrum splinters | league mechanics | not boss-only; separate pools |

The Gateway table has no source column. A boss-only currency must not be added
as a normal core weight; the model needs a source field, for example
`CurrencySource(Id, SourceKind, SourceId, DropLevel, Weight)` with kinds
`core`, `boss`, `league`, `atlas` and `vendor`. The current core table stays the
`core` fallback, and each boss/league executor adds its own rows.

### 5.2 Divination cards

The card's `BaseItemTypes.DropLevel` is its minimum monster level (The Doctor
75, House of Mirrors 80, The Apothecary 75, The Endless Darkness 81). The client
tables do not contain the area list: PoEDB's
[Divination_Cards](https://poedb.tw/us/Divination_Cards) page has an **Areas**
table of 135 areas and 555 area/card pairs, with `(tier N+)` minimum map tiers.
It is seeded into `drop_card_areas` (`DropDataSeed.CardAreas`, [drop-data-schema.md](../dev/drop-data-schema.md)).

Examples:

| Area | Card | Min map tier |
|---|---|---:|
| Burial Chambers | The Doctor | 7 |
| Cage | The Apothecary | 7 |
| Shrine | The Progeny of Lunaris | 13 |
| Tropical Island | Pride of the First Ones | 12 |
| The Putrid Cloister | The Professor | 1 |

Boss and league cards are not all in that table: The Endless Darkness comes
from the King in the Mists (already in `ItemDrops.KingInMistsCards` at 15%);
The Breach and The Dreamer are Breach/Chayula cards; The Calling, The Valkyrie
and The Undaunted are Beyond/Nemesis cards; The Admirer is an Atziri card; The
Professor is the Putrid Cloister's. Some cards are disabled in the client
(`The Cartographer`, `The Obscured`, `The Puzzle`).

Current code: `ItemDrops.Card(Random rng)` does not check `DropLevel`, area,
map tier, boss or league; it picks uniformly from every supported card. A
correct `Card` needs the area id, map tier and monster level (and a boss/league
flag for the special hooks), then the intersection of the area table,
`DropLevel <= monster level` and the special source. `MapRun.CardDropChance` and
`PacksDropCards` only change the chance, not the pool.

Boss unique pools and the known chances are in [boss-drops.md](boss-drops.md);
the full work plan is in [drop-research-roadmap.md](drop-research-roadmap.md).

## 6. Community spreadsheets and datasets

Searched for 2024-2026 community Excel/Google Sheets. There is **no public
per-orb weight sheet**: the recent sheets are prices, farming strategies, chest
loot or other item pools. The useful ones found:

| Source | Updated | What it has | Use here |
|---|---|---|---|
| [Path of Exile Trading](https://docs.google.com/spreadsheets/d/1_haU1OqI2gJLY3VTLJEBqpC-m28q4Fx_AiDW3TitRSk/) | 2026-11-07 | current currency prices, vendor conversions, challenge/standard margins | price cross-check, not weights |
| [Useful Spreadsheet Functions](https://www.pathofexile.com/forum/view-thread/62670) ([sheet](https://docs.google.com/spreadsheet/ccc?key=0Aksb09m440_wdHhuMXpBcllDRFVPWGpMb0lwVTJxUmc)) | 2012/2013 | XP formula and the currency drop-rate formula (the drop penalty) | confirms the penalty formula |
| [PoE Currency Values Spreadsheet](https://www.reddit.com/r/pathofexile/comments/z47el/poe_currency_values_spreadsheet/) | old | orb values, not counts | price cross-check |
| [LOOT LIST](https://docs.google.com/spreadsheets/d/12TCRIho7ViJSuM5ihbkKRUXn3g3IE5GjEISRh-Ae_7w/edit?gid=1252303170) | 3.29 | Voyage chest/merchant-crate counts | not ordinary orb weights |
| Prohibited Library sheets (divination cards, Heist) | 2022-2024 | MLE weights for those pools | method, not orbs |
| [PoeData.dev](https://poedata.dev/) | 3.28 scarabs | MLE/Bayesian weights and raw JSON | method/raw data, not orbs |

The 2014 leak remains the only complete public orb distribution; the fresh
sources above are corrections and cross-checks, not a replacement table.

## 7. Sources

- 2014 leak table (the only complete public orb distribution; historic):
  [Reddit](https://www.reddit.com/r/pathofexile/comments/2dvjol/drop_rate_of_currency_in_poetw_wiki/),
  quoted in the [2017 forum thread](https://www.pathofexile.com/forum/view-thread/1848036).
- 2024 Mirror/Exalted/Divine estimate:
  [Reddit](https://www.reddit.com/r/pathofexile/comments/1ax10hn/mirror_of_kalandra_drop_rate/).
- [PoeData.dev](https://poedata.dev/) and its
  [GitHub](https://github.com/weo-soft/poeData): MLE/Bayesian weights for
  scarabs, cards, essences, fossils, oils, catalysts and other pools. No ordinary
  orb dataset yet; the method and raw JSON are the model to copy.
- Prohibited Library's recent black-box datasets:
  [divination cards](https://www.reddit.com/r/pathofexile/comments/wsi0j8/complete_divination_card_dropweight_tables_drop/)
  and [Heist curio cases](https://www.reddit.com/r/pathofexile/comments/1c93vfk/drop_rate_data_of_rewards_from_blueprint_curio/);
  the wiki's [unique-tier analysis](https://www.poewiki.net/wiki/Guide:Analysis_of_unique_item_tiers)
  (September 2026) uses the same estimates.
- [PoE Wiki: Weight](https://www.poewiki.net/wiki/Weight) and
  [Drop rate](https://www.poewiki.net/wiki/Drop_Rate); [PoEDB: Drop rate](https://poedb.tw/us/Drop_rate).
- [poe.ninja](https://poe.ninja/): economy cross-check, not a weight source.
- The 3.26, 3.28 and 3.29 changes above: PoEDB's
  [3.26.0](https://poedb.tw/us/Version_3.26.0),
  [3.28.0](https://poedb.tw/us/Version_3.28.0) and
  [3.29.0](https://poedb.tw/us/Version_3.29.0) pages.

## 8. Open

- Calibrate the 24 weights to the 3.28/3.29 changes, then add Orb of Binding,
  Sacred Orb, Ancient Orb, Cartographer's Chisel and Fracturing Orb.
- Decide whether Scroll of Wisdom and Portal Scroll belong in the same currency
  pool as the orbs; the 2014 table excludes them.
- Implement the drop penalty in `ItemDrops` and apply it to currency (and cards
  when they exist).
- Move the table to editable data (`system_settings`/SQLite) so the weights can
  be tuned without a build, as [editable-game-data.md](../dev/editable-game-data.md)
  proposes.
