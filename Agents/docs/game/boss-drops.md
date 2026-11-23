# Boss drops: pools and known chances

Draft, 2026-11-08. A first cross-reference for boss-specific unique drops, boss
currency and the drop chances that are actually known. The complete client-side
boss inventory is `WorldAreas.Bosses_MonsterVarietiesKeys` (seeded into `drop_bosses`, [drop-data-schema.md](../dev/drop-data-schema.md)):
it gives 997 areas with a boss and 630 distinct boss varieties (394 campaign, 430
map, 140 unique-map, 24 Labyrinth-boss and 9 other areas).

## 1. What the client data has

- `WorldAreas.Bosses_MonsterVarietiesKeys` gives the boss varieties of an area;
  `MonsterVarieties` gives their names and metadata.
- `Maps`, `MapSeriesTiers`, `UniqueMaps`, `AtlasPrimordialBosses`,
  `AtlasExileBossArenas`, `MavenFights` and the league tables identify the
  endgame encounters.
- The client has **no drop table**: neither the unique pool nor a drop chance.
  `MonsterVarieties` has only the monster's stats and mods.
  The server's `ItemDrops.Unique` uses the global `VillageUniqueDisenchantValues`
  weights; only the King in the Mists has a boss-specific table
  (`ItemDrops.KingInMistsUniques` / `KingInMistsExtras`).

## 2. Unique pools by boss

The names below are the uniques PoEDB lists in each boss page's unique tab. The
list is a **pool**, not a probability distribution: the page gives no per-item
percentage. Percentages marked "unknown" are not public.

| Boss | Unique pool (PoEDB) | Chance |
|---|---|---|
| Sirus, Awakener of Worlds | Crown of the Inward Eye, Hands of the High Templar, Oriath's End, The Burden of Truth, The Saviour, The Tempest Rising, Thread of Hope (more in the page's unique tab) | unknown; Awakener's Orb is listed as dropped by Sirus |
| The Shaper | Dying Sun, Echoes of Creation, Entropic Devastation, Shaper's Touch, Starforge, Sublime Vision, The Tides of Time, The Unblinking Eye, Voidwalker, Call of the Void, Disintegrator, Impresence, Indigon, Mark of the Shaper, Replica Eternity Shroud, Solstice Vigil, The Eternity Shroud, Venarius' Astrolabe, Voidfletcher, Voidforge | unknown |
| The Elder | Blasphemer's Grasp, Cyclopean Coil, Disintegrator, Hopeshredder, Indigon, Mark of the Elder, Mark of the Shaper, Nebuloch, Shimmeron, The Eternity Shroud, Voidfletcher, Watcher's Eye, Call of the Void, Impresence, Replica Eternity Shroud, The Devourer of Minds, Venarius' Astrolabe, Voidforge | unknown |
| Atziri, Queen of the Vaal | Atziri's Acuity, Atziri's Disfavour, Atziri's Promise, Atziri's Reflection, Atziri's Rule, Atziri's Splendour, Atziri's Step, Doryani's Catalyst, Doryani's Invitation, Pledge of Hands, The Vertex, Triumvirate Authority | unknown |
| The Maven | Arn's Anguish, Curio of Potential, Doppelganger Guise, Echoforge, Grace of the Goddess, Graven's Secret, Impossible Escape, Legacy of Fury, Olesya's Delight, Progenesis, Viridi's Veil | unknown; Maven's Orb and Orb of Dominance are separate currency drops |
| The Searing Exarch | Annihilation's Approach, Crystallised Omniscience, Curio of Absorption, Dawnbreaker, Dawnstrider, Dissolution of the Flesh, Forbidden Flame, The Annihilating Light, The Celestial Brace | unknown |
| The Eater of Worlds | Ashes of the Stars, Curio of Consumption, Forbidden Flesh, Inextricable Fate, Melding of the Flesh, Nimis, Ravenous Passion, The Gluttonous Tide | unknown |
| Olroth, Origin of the Fall | Cadigan's Crown, Olroth's Resolve, Vorana's March | unknown |
| Lycia, Herald of the Scourge | Eternal Damnation, Sandstorm Visage, The Balance of Terror, The Broken Censer, The First Crest, The Gilded Chalice, The Hour of Divinity, The Night Lamp, The Original Scripture, The Second Sacrament, The Winds of Fate | unknown |
| The Trialmaster | Yaomac's Accord | unknown |
| The King in the Mists | The Untouched Soul, Pragmatism, The Light of Meaning, The Burden of Shadows | **40 / 35 / 20 / 5%** (one guaranteed unique, wiki n=576) |

The King in the Mists also drops Hextoad Support (6%), Hexpass Support (10%) and
The Endless Darkness (15%), all in `ItemDrops.KingInMistsExtras`.

## 3. Boss-only currency

Boss-restricted currency is not in the core table and must not get a normal core
weight:

| Currency | Source | Status |
|---|---|---|
| Awakener's Orb | Sirus, Awakener of Worlds | PoEDB item page lists Sirus under "Dropped by" |
| Maven's Orb, Orb of Dominance | The Maven | game knowledge; verify the item pages |
| Veiled Chaos/Exalted | Betrayal safehouses/Catarina | game knowledge; verify |
| Eldritch Chaos/Exalted/Annulment | Searing Exarch/Eater of Worlds | game knowledge; verify |
| Fracturing Orb, Ancient Orb | core drop since 3.28 | 3.28 patch notes |
| Cartographer's Chisel | map currency; map drop rules | 3.25 patch notes |
| Orb of Unmaking | Atlas currency | client `DropLevel` 70 |

## 4. What is modelled today

- `ItemDrops.KingInMists` is the only boss-specific drop table.
- Every other boss uses the generic core currency, gem, flask, equipment and
  card rolls; its unique pool is not restricted to the boss's own list.
- Map bosses therefore draw from the global unique pool instead of their map's
  unique pool, and boss-only currency (Awakener's Orb, etc.) does not drop.
- A correct model needs `BossDrop(BossId, ItemId, Chance, Source, Confidence)`
  and a boss hook; the core roll remains the fallback for ordinary monsters.

## 5. Sources

- PoEDB boss pages (unique tab and "Dropped by" tables), for example
  [Sirus](https://poedb.tw/us/Sirus%2C_Awakener_of_Worlds),
  [The Shaper](https://poedb.tw/us/The_Shaper), [The Elder](https://poedb.tw/us/The_Elder),
  [Atziri](https://poedb.tw/us/Atziri%2C_Queen_of_the_Vaal), [The Maven](https://poedb.tw/us/The_Maven),
  [The Searing Exarch](https://poedb.tw/us/The_Searing_Exarch),
  [The Eater of Worlds](https://poedb.tw/us/The_Eater_of_Worlds),
  [Olroth](https://poedb.tw/us/Olroth%2C_Origin_of_the_Fall),
  [Lycia](https://poedb.tw/us/Lycia%2C_Herald_of_the_Scourge) and
  [The Trialmaster](https://poedb.tw/us/The_Trialmaster).
- [drop-data-schema.md](../dev/drop-data-schema.md) for the client-side inventory (`drop_bosses`).
- [currency-drops.md](currency-drops.md) for currency weights, card areas and
  the drop penalty.
- Community/King data: `ItemDrops.KingInMistsUniques` (wiki 3.26, n=576);
  Maven's Orb's ~25% from the community 400-run sample: see
  [the Reddit thread](https://www.reddit.com/r/pathofexile/comments/lmc2kg/loot_data_and_notes_from_over_250_maven_kills/).

## 6. Open

- Exact unique chances for every boss: not published; needs a community sample
  or the Prohibited Library data. The table above marks them unknown on purpose.
- Parse every boss page's unique tab into world.db's `drop_boss_items` (the current doc lists the
  major bosses; the 430 map bosses and 140 unique-map bosses are next).
- Verify the Maven/Orb of Dominance, Veiled and Eldritch sources and their
  chances.
- Add boss-only currency and unique pools to `ItemDrops`; keep the core roll as
  the fallback.
