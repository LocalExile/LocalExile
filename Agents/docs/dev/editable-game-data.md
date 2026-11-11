# Editable game data

What of the server's game data can be content in `world.db`, and what cannot (the client owns it). The content is
built from SQL in git ([databases](databases.md)): a change is an update file in `sql/updates/pending_world/`, and `/reload`
reads world.db again without a restart. Mod roll ranges (`mod_stat_overrides`), item limits (`limit_overrides`), the
numeric settings (`system_settings`), unique weights, the item/jewel/tattoo catalogues (`catalog_entries`) and the drop
tables ([drop data](drop-data-schema.md)) are content today; item templates (`item_templates`) have no reader yet.

The reference data (the mod list, base types, tag lists and generated tables) stays in memory. Hot paths do not query
SQLite per roll, per item or per socket check: a reload publishes new in-memory snapshots atomically, and a reader sees
the old snapshot or the new one, never a half-applied change. A reload that fails (a bad payload, a setting out of its
range, `SystemSettings.Validate`) keeps the previous snapshots.

## What can be edited safely

| Area | Editable at runtime | Why |
|---|---|---|
| Mod stat roll ranges (`Stat{n}Min`/`Max`) | **Yes** | The client receives the rolled values with the item; it does not need the server's range. New rolls and max-roll dev commands can use the override. |
| Mod level, spawn weights, generation weights, tags, families, essence-only, generation type/domain | **Yes, server-side** | These decide what the server may generate. The client identifies a mod by its hash and reads its stat keys, not its spawn metadata. Changing them can make a mod appear that the client did not expect but can still display. |
| Mod stat keys and mod hash/Id | **No** | The client looks the mod up by `HASH16` and reads the stat keys in its own order. Rewriting either desynchronises the item. |
| Unique/radius/timeless definitions that reuse existing base types, mod hashes, art and word hashes | **Yes** | The item wire data is made of existing client records. The server decides the values and the limit. |
| Item limits (radius jewels, timeless, Impossible Escape, tattoos) | **Yes** | The limit is enforced by the server. The client tooltip may still print its own limit text; that is display data, not enforcement. |
| Custom item name | **Yes** | `MagicProperties.Write` already has the client's custom-name bit (`0x80`, a length-prefixed string). `Item` does not expose it yet; adding a `CustomName` field and saving it is enough. |
| Custom item from an existing base + existing mods + explicit values | **Yes** | This is what `/item_mod` does manually today. A template table makes it repeatable. |
| Drop chances, vendor prices, crafting costs | **Yes, later** | Server-only decisions. They need their own typed settings and tests. |
| Base item name, hash, class, width/height, components, tags, visual identity | **No** | The client owns these and renders the item from them. A server-only change desynchronises the inventory, tooltip or model. |
| New base item types, new mod rows, new word hashes, new art | **No** | The client has no row for them. A new HASH32/HASH16 is an unknown object to the client. Use an existing base/mod/word/visual instead. |
| New stat keys or changed stat semantics | **No** | The client and the server's consumers both key behaviour by `Stats.dat` Id/row. This is code, not a data edit. |
| Stack size, max stack, inventory shape, item class | **No / restricted** | The client's inventory UI and item component reader use their own values. Only change these if the client table is changed too. |

### System settings

`system_settings` holds the numeric server settings; world.db's first content update gives every key its default (a key
with no row reads 0):

| Key | Default | Used by |
|---|---:|---|
| `socket_max_wand` | 3 | wand socket capacity |
| `socket_max_one_hand_sword`, `socket_max_thrusting_one_hand_sword`, `socket_max_one_hand_axe`, `socket_max_one_hand_mace`, `socket_max_sceptre`, `socket_max_dagger`, `socket_max_rune_dagger`, `socket_max_claw` | 3 | one-handed weapon socket capacity |
| `socket_max_two_hand_sword`, `socket_max_two_hand_axe`, `socket_max_two_hand_mace`, `socket_max_bow`, `socket_max_staff`, `socket_max_warstaff` | 6 | two-handed/bow socket capacity |
| `socket_max_body_armour` | 6 | body armour sockets |
| `socket_max_helmet`, `socket_max_gloves`, `socket_max_boots` | 4 | armour sockets |
| `socket_max_shield` | 3 | shield sockets |
| `socket_max_belt` | 0 | belt sockets (the base has no Sockets component by default) |
| `rare_max_prefixes`, `rare_max_suffixes` | 3 | rare item prefix/suffix caps (0..6); a cap sum above six extends the 4-6 count by the excess |
| `rare_jewel_max_prefixes`, `rare_jewel_max_suffixes` | 2 | the same caps for jewel and abyss jewel bases |
| `passive_points_per_level` | 1 | passive points per level after the first |
| `gold_drop_multiplier` | 1 | multiplier on the mode's gold amount; the Ruthless 0.25 base still applies |
| `gold_drop_chance_multiplier` | 1 | multiplier on the per-rarity gold drop chance |
| `gold_auto_loot_radius` | 15 | cells within which gold is picked up without a click |
| `projectile_maximum_range_cells` | 150 | one projectile movement instance's maximum flight (150 cells = 15 m); fork/chain/split start a fresh instance |
| `drop_multiplier_currency`, `drop_multiplier_equipment`, `drop_multiplier_flasks`, `drop_multiplier_gems` | 1 | category weights in the ordinary drop roll |
| `drop_multiplier_maps` | 1 | generic map drop chance |
| `drop_multiplier_unique_rarity` | 1 | equipment magic/rare-roll multiplier; unique item drops themselves are not in the drop table yet |
| `monster_pack_density_multiplier` | 1 | scattered packs per walkable-cell area |
| `monster_pack_size_multiplier` | 1 | members per pack |
| `experience_multiplier` | 1 | monster experience after rarity and level penalty |
| `max_level` | 100 | active level cap (1..100; the client/table ceiling) |
| `tattoo_total_limit` | 50 | global tattoo cap |
| `tattoo_group_limit` | 1 | fallback for the five tattoo group limits; `limit_overrides` still wins |
| `timeless_jewel_limit` | 1 | Historic timeless jewel cap |
| `impossible_escape_limit` | 1 | Impossible Escape cap |
| `radius_jewel_default_limit` | 0 | fallback for radius jewels whose catalog `Limit` is 0; 0 means unlimited |
| `grace_period_seconds` | 60 | entry protection duration; 0 disables it |

The socket value is the class cap, clamped to the client's six sockets. It is not tied to the inventory cell count: a
1x3 wand set to six can carry six sockets (the old `Game_SOCKET_CAPACITY` dev flag already exercised this). Item level
still limits what currency rerolls may create through `CurrencyEffects.MaximumSockets`; `/sockets` uses the class cap.
Rare affix caps are read by `ItemRolls` on every roll. The defaults reproduce the existing 4-6 count with at most
three prefixes/suffixes (jewels 3-4 with two each). Lowering a cap reduces the count to the caps' sum; raising the sum
above the class default (six ordinary, four jewel) adds the excess to the count, so 4/4 gives 6-8 ordinary affixes.
`passive_points_per_level` is read through `Progression.PassivePoints`; changing it affects the next point calculation or
area/level refresh, not a retroactive trim of an online character. `gold_drop_multiplier` multiplies the already
mode-scaled amount, so the ordinary mode default 1 and Ruthless 0.25 keep their current behaviour.

The drop multipliers scale the four category weights in `ItemDrops.RollOne`; the map multiplier scales the separate
map chance, and `drop_multiplier_unique_rarity` scales the magic/rare branch (unique item drops are not in the table
yet). Pack density scales the scattered-pack count, pack size scales each pack's member roll. `experience_multiplier`
multiplies monster experience after rarity and the level penalty. `max_level` lowers the active cap from the client's
100; values above 100 are refused because the client and `ExperienceLevels` do not have them. `gold_drop_chance_multiplier`
scales the per-rarity chance, `gold_auto_loot_radius` sets the auto-pickup distance, and `grace_period_seconds` drives
both the buff duration and the protection deadline. The tattoo/jewel limits in `system_settings` are the defaults;
`limit_overrides` remains the per-key override layer.

### Unique catalogue and weights

The full unique-item catalogue is `catalog_entries.kind = 'unique_item'` with 1,299 rows from PoB's Export/Uniques files
(`scripts/re/unique_items.py` writes the ones world.db does not have as a pending update, and lists those whose export
differs); `unique_item` keys are `name|base` because a few names have several bases. Some rows are corrections of the
export (Mageblood, Crown of Eyes and the other 3.29 definitions), so a row is never replaced from PoB wholesale.

Unique drop weights are **absolute** (0..1): `ItemDrops.PickUnique` compares each weight directly and the rest of the
range is "no unique". A unique missing from `unique_weights` weighs zero and does not drop; `unique_weight_overrides`
wins over it. The weights come from `VillageUniqueDisenchantValues` (1 / value, capped at 1); ItemDrops reads that
client table only while world.db is not open.

To change a mod's roll range, a row in `mod_stat_overrides` names the Mods.dat Id and the 0-based stat index; the
client's own ranges are client.db's `Mods` (`Stat1Min`..). An override applies to new rolls only: an existing item stores
its values, not the range.

Mod affix weights (`SpawnWeight_*`, `GenerationWeight_*`, families and tags in `Mods`) are **not** in the database
as content; they are client.db's `Mods` columns.

### Candidate lists currently in code

The catalogue rows marked done are `catalog_entries` kinds in world.db; the rest are candidates, with the safety boundary:

| Candidate | Where | State | Why / what it unlocks |
|---|---|---|---|
| Unique item definitions | `unique_item` | **Done** | Base, implicits, mod ids and roll overrides for Mageblood, Crown of Eyes and the rest (`scripts/re/unique_items.py`). Uses existing client hashes. |
| Radius jewel definitions and variants | `radius_jewel` | **Done** | 76 jewel rows (43 radius + 33 threshold), base/mod/variant/value/limit. |
| Non-radius unique jewels | `unique_jewel_global`, `unique_jewel_special` | **Done** | 42 global + 1 special jewel definitions generated from PoB. |
| Threshold jewel definitions | `threshold_jewel` | **Done** | 33 threshold jewels with base, limit and client mods. |
| Timeless jewel variants | `timeless_jewel` | **Done** | Six timeless jewels with seed ranges and conqueror versions; the one-jewel limit is already overridable. |
| Unique jewel art map | `unique_jewel_visual` | **Done, read-only** | Maps an existing unique name to an existing `ItemVisualIdentity` HASH16. A new art hash is client-owned. |
| Tattoo stat effects | `tattoo_effect` | **Done** | 48 current name -> stat/id/value rows. The apply/target/limit mechanics stay code; the effect table is data. |
| Cluster jewel themes and layouts | `ClusterJewelStats.ThemeStats`, `ClusterJewelLayout.Templates` | **Partly** | Theme stat mappings and layout counts can be data; the allocatable node algorithm stays code. Verify against the client tables first. |
| NPC shop stock | `NpcShops.Stock`, `GemTabs`, `SellsMaps` | **Yes** | The client names shops but not their stock; this is server content. Also `GemVendorStock.CurrencyIds` and `Vendors`. |
| NPC/town/stash/hideout placement | `NpcCatalog.Towns`, `Hideout`; `TownNpcs.All`; `StashCatalog.Towns`; `HideoutCatalog.Moved`; `AreaLayouts.OneWayArrivals`, `TownFallback`, `Ids` | **Yes, captured data** | Positions and captured objects; keep the captured seed as the reference and allow DB overrides. Do not change client-owned models/art. |
| Drop tables and rates | `drop_*` tables; `EquipmentClasses`, `NotDropped`, `EquipmentRarity` in `ItemDrops` | **Partly** | Server-only loot policy; high value for balance. The drop table can be typed rows with weights, level caps and enabled flags. |
| Quest flags and World panel pins | `Quests.WorldPanelPins`, `EveryCharacter`, `Quests.All` scripts | **Yes, with care** | Flags and pins are data, but a wrong flag can reveal locked content. Keep the known-good list as the seed. |
| Vendor recipe edge cases | `VendorRecipes` oil/rune upgrade arrays, `VendorEquipmentRecipes` | **Some** | The main recipe/pricing tables already come from the client tables. The small hardcoded oil/rune chains can move; the recipe algorithm stays code. |
| Gem/utility/monster mapping lists | `GemSources.DropOnlySupports`, `GemSupportDamage.Unconditional`, `UtilityFlasks.Modelled`/`PanelOnly`/`DuringEffect`, `MonsterMods.Modelled`/`Vacuous`/`SelfAuraMods`, `HarvestCrafting.Metamods`, `EssenceCrafting.Columns`, `SkillCatalog.ProjectileFallbacks` | **Yes, later** | These are server mapping tables. Move one typed family at a time with tests; the consumers are code. |
| Ruthless/league policy lists | `RuthlessLeague.Of` (`UnavailableGemBases`, `UnavailableGemEffects`, in `Modules/ruthless/`); `SingleUseCooldown.Unsupported`; `GemVendorStock` | **Yes, later** | Policy/config, not client data. |
| Charge/damage/panel rule lists | `ChargeEffects`, `ChargeGains`, `DamageImmunities`, `PanelStats.Sums`, `StatKeys` | **No as raw tables, low priority** | They encode calculation order and stat semantics. A data-driven rule engine would be a separate, tested project. |
| Protocol and component lists | message ids, `InstanceItems.ComponentOrder`, `BaseComponents`, `PlayerDefaults`, packet layouts | **No** | The client and the server must agree exactly; these are code/protocol, not user settings. |
| Terrain/RNG tables | `EdgeShaping`, `TileKey`, pathfinder tables, level-generator constants | **No** | Reverse-engineered algorithms; changing them needs a generator gate and baseline, not a settings table. |
| Client-owned hashes, names, geometry, art, stat keys | `BaseItemTypes`, `Mods`, `Stats`, `Words`, `ItemVisualIdentity`, item components | **No** | A server-only edit desynchronises the client. Keep the client tables (client.db) as the reference. |

The practical split is:

- **Server policy** (rates, limits, counts, costs): typed settings with defaults in code and database overrides.
- **Client protocol** (hash ids, component order, stat keys, packet layouts): never in the settings database.
- **Algorithms** (damage, charges, level generation): only through a tested data-driven rewrite, not as a list edit.

### More system-setting ideas

The next safe numeric knobs, in rough order (the first batch is already in `system_settings`):

- drop multipliers: category weights, map chance, unique rarity (done); item quantity/rarity next;
- vendor and crafting costs: shop price multiplier, bench cost multiplier, gold respec multiplier;
- monster balance: life, damage, speed, pack density/size (done), magic/rare pack chance next;
- map policy: maximum tier, portal count, map device slots, instance lifetime;
- flask policy: charges per kill, charge recovery, effect duration, quality cap;
- experience: rate multiplier and maximum level (done); safe-zone width next;
- gold: auto-loot radius, amount multiplier, drop chance (done);
- limits: tattoo/jewel defaults and socket caps (done); per-entry overrides remain in `limit_overrides`;
- server quality of life: grace period (done), respawn delay, area reset time, raid/instance expiry.

Keep calculation order and stat semantics in code. A setting is safe when it scales or caps a number; moving damage
formulas or conditional rules into a generic table is a separate, tested engine.

### What "custom item" can mean

A custom item can be:

- an existing base type (`Metadata/Items/...`), with any mix of existing mod HASH16s, explicit values, rarity, item
  level, quality, sockets and links;
- a custom display name, sent through the item's custom-name bit (the server needs a `CustomName` field);
- existing art and existing word hashes (or none, when a custom name is used);
- a template in world.db (`item_templates`) so it can be recreated or handed out by a command or drop rule.

It cannot be a new base type with a new HASH32, a new mod with a new HASH16, or a new stat key. Those need a client data
patch, not a runtime server setting.

## Reloading

- **Mod ranges**: the provider holds an immutable dictionary; a reload rebuilds it and atomically swaps the reference. `ItemRolls` does not need clearing for ranges because its pool stores mod references,
  not rolled values. If an edit changes a mod's tags/weights, clear the pool cache (or key it by the snapshot revision).
- **Limits**: a `LimitSettings` snapshot with an atomic swap. The socket and tattoo checks read it per action, so no
  other cache is involved.
- **Item templates** (planned): an immutable dictionary keyed by template id; a reload swaps it. Item creation reads the current
  snapshot once and builds the item.
- **Reference catalogues**: `GameDataCatalog` loads the typed rows into arrays/dictionaries and swaps them together.
  `UniqueCatalog`, `RadiusJewelCatalogue`, `ThresholdJewelCatalogue`, `UniqueJewelCatalogue`, `TimelessJewelCatalog`,
  `TattooStats` and `UniqueJewelVisuals` read the current snapshot. Existing items keep their stored values; new items
  and new drops use the new definition. Do not mutate a `Lazy<T>` in place.
- **Threading**: readers take one snapshot reference (a `Volatile.Read`/`Interlocked` exchange).

The existing `Lazy<T>` fields are fine for the initial load, but they are not a reload mechanism. A reload should not
call `Lazy.Value` again; it should replace a snapshot field.
