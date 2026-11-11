# Status: Items, currency and crafting

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/items-and-crafting.md](../roadmap/items-and-crafting.md).

## Works (verified with the real client)

- Ground items belong to the area instance: dropped items and loot stay where they lay across logouts, disconnects and
  client crashes until the instance expires; ids from 0x100000; one pickup per item.
  Fresh drops play native item/filter audio; existing-ground replay stays quiet (Divine, Mirror and quest alerts traced with NeverSink).
  Code: `InstanceStore.cs`, Gateway.
  Docs: [13](../docs/protocol/13-items.md) §5.
- Life and mana flasks: a flask key (C→S 0x70) spends the flask's charges and recovers Flasks.dat's life or mana over
  its time in the shared 16 ms recovery loop, the orb rising by +1, with the flask_effect buff.
  Quality, the flask's prefixes and suffixes and the character's flask stats count (amount, rate, instant and partial
  instant, low life, other-pool costs, mana at the end, not removed at full, extra life), as Path of Building does.
  Several recovery flasks queue, the highest rate first (poewiki).
  Checked live: the base recovery and the extra-life suffix; the other mods and queueing by tests.
  A successful use also sends S→C `0x263`; the native UI then looks the base type up and plays its use sound,
  `Audio/Sound Effects/ItemSounds/flaskdrink.ogg` (traced on the official 3.29.3b client and captured from it).
  Full pools refuse the flask before any charge or buff, with silent `0x263` result `0x4e2` and the authoritative pool;
  mana fullness uses the unreserved cap (full-pool refusals verified live locally).
  The client also pays the attack skill's mana for attack-moves the server only walks; the Gateway answers with the
  authoritative mana so the orb and the flasks agree (probe 2026-10-26).
  Code: `Flasks.cs`, `FlaskRecovery.cs`, Gateway (`UseFlask`, `RunResourceRecovery`).
  Docs: [13](../docs/protocol/13-items.md) §8.
- Flask charges: kills give 1 / 3.5 / 6 / 11 charges by rarity (Mods.dat monster rarity mods, poewiki), increased
  flask charges gained and charge recovery multiply, partial charges are kept.
  Charges on dealing a critical strike (one 0.132 s cooldown), when hit and every 3 seconds; charges used and maximum
  charges with mods.
  Ordinary-mode towns, hideouts and resurrection fill every flask; a flask put on the bar in a hostile area starts empty.
  `Game_FLASK_CHARGES=full` brings back "every kill fills every flask".
  Not yet: fewer charges in acts 6 to 10 and maps; charges on taking a critical strike (monsters deal none) and at
  maximum frenzy charges (none here).
  Checked live: kill and crit charges and the hostile-area rule; charges when hit and every 3 seconds by tests.
  Code: `FlaskCharges.cs`, Game module (`Modules/Core/Game/Flask/FlaskCharges.cs`).
  Docs: [13](../docs/protocol/13-items.md) §8b.
- Flask modifier composition (#75): the validation counts and Standard/Ruthless observations in this entry are
  historical LocalExile evidence. This branch ports ordinary-mode calculations; native checks are outstanding.
  Raw character charge-gain sources resolve into the native virtual once,
  then multiply local flask charge recovery with fractional carry. Generic and resource-specific recovery-rate
  factors multiply; hybrid Life/Mana keep independent rates, native buff rows 5/9, queues and lifetimes.
  Global duration now resolves `flask_duration_+%` plus the actual-player-level source once for both panel and use.
  Each use snapshots level/stat inputs before its charge send; a virtual display replacement does not change the
  actual duration. Item-local and global utility-duration factors multiply, followed by quality and local more.
  Recovery periods use the same global factor independently for Life/Mana; only over-time amount scales with it.
  Runtime global effect also resolves raw flat plus actual-player-level inputs once, without replacing raw published
  keys 1784/20244. Local and applicable magic-utility increases add to it; duration, quality, recovery rates, instant/
  low-life stages, flags and independent Onslaught stay separate. Existing active effects keep captured values.
  All 39 new pure/real-handler cases pass within the combined 53/53 focused batch, followed by full Gateway
  3710/3710 with no skips. Prior unchanged Core 648/Data 37 remain applicable (4395 total, not all newly rerun).
  Five actual disposed-stream task faults remain in the full-run diagnostics, with no new-calculation reference.
  No new native proof is claimed. Wide effect arithmetic, individual buff integer saturation and signed strongest
  comparison do not harden every downstream aggregate.
  The preceding panel/duration batch passed 55 new plus 80 existing focused checks and Gateway 3657/3657;
  unchanged prior Core 648/Data 37 runs remain applicable (4342 total). Combined native results cover 40 completed
  controls: 22 complete PASS (12 positive source, six utility and four Life), with 18 complete zero controls UNPROVED;
  panel-field verdicts total 138 VERIFIED/42 UNPROVED. Standard utility durations match 13.8/3/3.9 seconds and
  Ruthless 4.6/2/2.6 for level-10 white, level-100 white and level-100 local-duration uses respectively.
  Life gains are Standard160/34 and Ruthless161/34 at levels10/100 against fixed161/35, within the original
  one-integer publication boundary; this does not prove official rounding. Final recovery3 cleanup is clean,
  all769 frozen inputs held, canonical inventories/original saves were preserved and normal storage was restored.
  Earlier failed observations and all UNPROVED fields remain recorded. They do not validate the new effect resolver.
  Whole-unique native acquisition/tooltip and conditional operators, nonpositive-duration policy and official
  fractional rounding remain open.
  Earlier twenty-eight automated controls cover real handlers and both modes. Twenty-four native controls verify
  installed buffs, separate hybrid expiry, full/end/queue/death, actual recovery, charge gains and overrides.
  Ruthless spent charges persist in town; ordinary Nessa interaction refills them before further controls.
  Original saves and inventories held, apart from flask charges and native move IDs. Conditional acquisition
  and queued native timer/predictor presentation remain separate work.
  Code: `FlaskCharges.cs`, `FlaskRecovery.cs`, `FlaskEffectScaling.cs`, `Flasks.cs`, `UtilityFlasks.cs`,
  Gateway (`UseFlask`, `UseUtilityFlask`, `CharacterPlayerStatsCore`).
  Docs: [13](../docs/protocol/13-items.md) §8.

- Utility flasks: every utility flask spends its charges, shows its buff and the timer bar under it, and gives its
  buff's stats for its duration, all from the tables (Flasks.dat, BuffDefinitions, ComponentCharges).
  The stats count in the pools (the walk estimate, `/defences`, `/resistances`, `/hit`) and go to the client.
  Two flasks of one kind keep their own timers and only the strongest applies (poewiki); different effects stack.
  They end with the duration, at death, with the area and when the flask leaves its cell; a pause holds them.
  Quality, the flask's duration and effect mods, its "during effect" suffixes and the character's flask effect and
  duration count.
  Stibnite blinds monsters in its smoke cloud, Sulphur's consecrated ground regenerates life, Basalt taunts, Quartz
  phases through monsters, Gold raises the rarity of drops, Silver gives Onslaught.
  Checked live: every flask's use, stats on the client, bars, pause, death, area change; not the client walking faster
  or phasing (the control bridge cannot make it walk).
  Iron Flask now contributes to the authoritative Ward pool and restores broken Ward only after paid use.
  A native Antiquarian check spent 40 charges, restored 502 Ward immediately and returned to 22 after expiry.
  See [Ward and native evidence](../docs/game/antiquarian.md).
  Not yet: ailments, stun and curses on the player (Aquamarine, Corundum and the ailment
  suffixes reach the panel only), Aquamarine's chilled ground, the smoke cloud's art, leech suffixes.
  Code: `UtilityFlasks.cs`, `FlaskGrounds.cs`, the Game module (`UseUtilityFlask`, `RunFlaskGrounds`).
  Docs: [13](../docs/protocol/13-items.md) §8a.
- Items: the start kit (Characters.dat skill and support gems, two life flasks on keys 1–2 and a mana flask on key 5)
  in the inventories, and the class weapon with two linked sockets worn in the main hand (`Game_START_WEAPON=ground`:
  on the ground a few cells from the spawn until it is picked up);
  pick up (0x28), put down, swap and equip (0x2a, slot classes and attribute requirements checked by the client),
  one-hander off hand → main hand, socket and unsocket gems (0x30, 0x2f), drop to the ground outside town (0x29 →
  WorldItem) and pick up again (to the cursor with the inventory open, at the client's 22-cell range), Destroy in town
  (0x16 /destroy); stacks: merge a held stack onto one of its type up to the CurrencyItems StackSize (0x4a), split
  with Shift + click (0x7c), Shift + click while holding places one, full fragment / shard stacks become their item,
  pick-ups, `/item` and sale rewards fill existing stacks first; the equipped weapon on the model;
  active gems in equipment grant their skill; class attributes and the class on the character panel;
  `/item <row, Id or name> [count]`.
  Code: `InstanceItems.cs`, Gateway (`ItemCatalog.cs`, `PlayerInventories.cs`).
  Docs: [13](../docs/protocol/13-items.md) §4a.
- Item mods: rarity (normal, magic, rare, unique), identified, and the implicit, explicit, enchant, scourge and hidden
  mod lists in the Mods component; new items roll their BaseItemTypes implicits (Driftwood Wand 8-12% increased Spell
  Damage); magic names come from the affixes; rarity and mods are saved with the item (characters and stash);
  `/item_mod`, `/item_mod set`, `/item_mod remove`, `/item_mods`, `/rarity`, `/sockets` (colours and links, e.g. `/sockets RRGBBB`) on the held item;
  `/item_give <JSON>` makes a whole item (base, rarity, mods with values, states, sockets, rare name) for tools such as
  the launcher's item crafter; a JSON array grants items in order and reports a partial batch if placement stops
  ([item-crafter.md](../docs/dev/item-crafter.md));
  `Game_SOCKET_CAPACITY=1..6` (dev) lets `/sockets` give any socketable base that many, e.g. a 6-link wand (its own
  capacity is 3).
  Magic and rare items: affixes rolled from Mods.dat by domain, item level, the item's tags and families, rare names
  from Words.dat; equipment drops magic / rare and unidentified; a Scroll of Wisdom identifies (C→S 0x71);
  `/item <type> magic|rare`, `/roll`.
  Equipped items' mods count on the character: global stats like a passive's (life, resistances, attributes, spell
  damage …), the main hand's local crit, attack speed and accuracy, its local physical damage and quality,
  and its local added elemental/chaos damage (`ItemWeapons`). The third ring slot (Ring3, row 102, shown by The Unseen
  Hand's `enable_ring_slot_3`) counts like the first two.
  Armour/evasion/ES local flat, increased and hybrid mods and multiplicative quality feed the shared pools and panel
  (`ItemDefences`, [13](../docs/protocol/13-items.md) §2c).
  Armour, evasion and ES share a saved variable base roll, sent in the native Armour component.
  Fresh armour gets an ordinary roll once; legacy items keep their minimum, and ordinary crafting preserves the roll.
  Native checks verified ordinary/special base-roll states, quality composition, equipment refresh and relog.
  Sacred Orb rerolls and supported special ES rules are implemented with automated coverage (see Partly working).
  Other uniques and special mods still depend on their individual mechanics (#60).
  Mageblood's supported subset is below.
  Equipment uses the shared `ItemDefences.Calculate`, including Tabula and explicit no-ES overrides.
  Code: `InstanceItems.cs`, Gateway (`ModCatalog.cs`, `ItemRolls.cs`, `ItemDrops.cs`, `ItemModStats.cs`,
  `ItemWeapons.cs`, `ItemDefences.cs`, `ItemCatalog.cs`, `CharacterSaves.cs`).
  Docs: [13](../docs/protocol/13-items.md) §2a-§2c.
- Item states (verified live 2026-10-30): the six influences, split, synthesised, corrupted, mirrored and fractured,
  and crafted and enchanted mods show in the client's tooltip and Ctrl+C text, and survive a relog and a Gateway restart.
  `/item_state` switches them on the held item.
  An influence opens its class's influence mods to every roll and Harvest swap (`InfluenceTags.dat`), the uniques PoB marks Shaper or
  Elder carry it, and the Fracturing Orb refuses influenced and synthesised items without spending the orb.
  Nothing in play creates these states yet, and no mechanic reads influence.
  Code: `InstanceItems.cs`, Gateway (`ItemStates.cs`, `ItemRolls.cs`, `CharacterSaves.cs`).
  Docs: [13](../docs/protocol/13-items.md) §2f.
- Game content in world.db (2026-11-09, [#734](https://github.com/LocalExile/LocalExile/issues/734)): `mod_stat_overrides`,
  `limit_overrides`, the item and jewel catalogues, `system_settings`, `unique_weights` and the drop tables are world.db content
  built from `sql/` (Agents/docs/dev/databases.md); a change is an update file. The Gateway loads the rows at startup
  and atomically republishes them on `/reload`, so no restart is needed; a bad row keeps the previous snapshot.
  Limits currently cover the Historic timeless jewel, Impossible Escape, radius unique jewels and the tattoo total/group
  caps. Typed tables hold the unique-item, radius/threshold/unique/timeless jewel, tattoo-effect and jewel-art
  catalogues (`unique_items`, `radius_jewels`, `unique_jewels`, `timeless_jewels`, `tattoo_effects`, `jewel_visuals`;
  1,304 / 76 / 43 / 6 / 78 / 190 rows; the unique rows are the generated PoB catalogue with the 3.29
  corrections). `system_settings` holds 53 numeric knobs, checked on load: 21 socket caps by item class (wand 3, body 6, helmet/gloves/boots 4,
  shield 3, belt 0, two-handed 6), rare prefix/suffix caps (3/3 ordinary, 2/2 jewels), passive points per level, gold
  chance/amount/auto-loot, the 150-cell projectile maximum range, six drop multipliers, pack density/size, experience
  multiplier, maximum level, default tattoo/jewel limits and grace period. Live (before world.db, through the former
  `/game_data setting`): a wand set to six
  sockets accepted `RRGBBB` and was reduced back to three; `passive_points_per_level = 2` made the 0x33 client budget
  and server budget agree at 1198 points. The item-template schema exists, but custom item templates, custom names and
  the admin panel are not implemented yet.
  Code: `GameData.cs`, `GameDataCatalog.cs`, `GameDataStore.cs`, `ModRangeProvider.cs`, `LimitSettings.cs`, Gateway
  (`Program.Commands.cs`).
  Docs: [editable game data](../docs/dev/editable-game-data.md), [saves](../docs/saves.md).
- Sacred Orb: ordinary and Shift currency use reroll the saved shared defence base on eligible armour.
  Only the base roll changes; mods, quality, sockets, gems, links, rarity and identification remain.
  Inventory and stash pairings save the target and one-unit payment atomically, including unchanged roll outcomes.
  Worn-item rerolls refresh the character pools and panel.
  Native use verified raw 32 becoming ordinary, armour/evasion/ES updates, Shift repeat and same-value consumption.
  Corrupted, mirrored, non-armour and fixed-range targets refuse use without payment.
  Ward and the fifth native range count for eligibility; their character-pool mechanics remain separate work.
  Uniform ordinary roll selection is local policy; the official distribution is unmeasured.
  Native fixed/zero-base refusal and Ward-only rerolling matched; inventories and stash survived a Gateway restart.
  See [13](../docs/protocol/13-items.md), "Sacred Orb" for the verified values.
  Code: `CurrencyEffects.cs`, `ItemCatalog.cs`, Gateway (`UseItemOn`, `ItemMoves.UseAcross`).
- Special defence overrides and ES-to-Life conversion are implemented.
  Native full/50% conversion, independent ES, Life scaling, zero-ES controls and shield swaps matched.
  Both test inventories and their final pools survived a Gateway restart.
  Tabula's hidden override and explicit no-ES suppress only their item's ES before local scaling.
  Full and percentage conversion use final local ES per active armour slot, including the eligible active shield.
  Converted Life receives global Life increases; independent global ES stays ES.
  PoB imports restore the hidden override only for four recognised unique names with matching native bases.
  The importer recognises explicit no-ES and full/percentage conversion text and uses the shared local calculator.
  Conditional corrupted-item ES maps to a global ES increase for that corrupted source, preserving local tooltip ES.
  Shield-only increases now affect character defences without changing local item values or converted Life.
  Native application, clearing, weapon swaps and Gateway-restart checks passed with final half-up pool rounding.
  Ward conversions, ring-slot and jewel-radius mechanics remain separate work.
  Code: `ItemDefences.cs`, `Modules/Core/Game/Item/Equipment.cs`, `Modules/Core/Game/Passive/Progression.cs`.
  Docs: [13](../docs/protocol/13-items.md) §2, [20](../docs/protocol/20-damage.md), "Player energy shield".
- Gold: shared by the account's characters of one league (S→C 0x2aa), saved with that league's stash, sent on area
  entry; every league has its own stash and gold, Standard keeps the account's stash from before (`AccountStash.Key`);
  monsters drop piles of gold (normal 40%, magic 75%, rare and unique always;
  (3 + 1.2 × level) × 1/2/5/15, our own numbers, `ItemDrops.Gold`), and picking one up adds it to the gold;
  gold within 15 cells of the player is picked up by itself (player tick; the `gold_auto_loot_radius` setting);
  gold within the configured radius (default 20 cells) is picked up in full without late-drop ghosts;
  older per-character gold moves into the account at login; `/gold <±amount>`.
  Code: `InstanceEntities.Gold`, `AccountStash`, Gateway.
  Docs: [04](../docs/protocol/04-player-entity.md) §6.
- Loot: dying monsters drop currency, gems, flasks and equipment around the corpse, rolled in stages like the game's
  with our own numbers: how many (by monster rarity, times item quantity: the league's, a mod's, the player's
  increased quantity, no cap), the kind, for equipment the slot (armour often, jewellery seldom) and then any base the
  level allows, and last the rarity: unique (one of that base's uniques, else rare), rare, magic, each chance raised
  by item rarity. Bases tagged not_for_sale (unique-only bases like "Ring", Stygian Vise, Heist, Ritual, Expedition
  bases) are left out, the atlas bases kept. Currency runs from Scrolls of Wisdom down to the Mirror, each from its
  DropLevel. A unique's weight is the inverse of its disenchant value (`VillageUniqueDisenchantValues`, Ruthless has its
  own column), so a Mageblood is about 840 times as rare as a Wanderlust, and a base whose uniques add up to less than 1
  mostly gives a rare instead. The 1,527 weights are world.db's `unique_weights`, with `unique_weight_overrides` on top; the
  table's CHECK keeps a weight in 0..1. The client table is the fallback only while world.db is not open.
  The core currency weights also live in `drop_currency_weights` (written from the compiled table with the client's
  DropLevels); `ItemDrops.Build` prefers them and keeps the compiled table as the fallback, still gated by each
  base's DropLevel. Cards and scarabs follow the poedata.dev community weights (`drop_item_weights`: 422 cards, 109
  scarabs), and a map kill rolls our own base `drop_card_chance_pct`/`drop_scarab_chance_pct`; both settings are
  editable live. The boss and divination-card area tables are seeded (630 bosses, 108 boss items, 555 area/card pairs) but
  no roll reads them yet; [drop-data-schema](../docs/dev/drop-data-schema.md) has the schema and the next steps.
  Mod affix spawn/generation weights (`SpawnWeight_*`, `GenerationWeight_*`, families and tags in `Mods`) are still
  loaded from JSON; moving them would also require clearing the affix-pool cache on reload. Drops have the monster's level as item level, +1 from magic, +2 from rare and +3 from unique
  monsters. One drop event's stackable items of one type become stacks up to its `CurrencyItems` StackSize (twenty-one
  divine orbs lie as 10, 10 and 1), instead of one world item per unit (`ItemDrops.Stacked`).
  Code: `ItemDrops.cs`, Gateway (`DropLoot`, `CommitProjectileKill`).
  Docs: [15](../docs/protocol/15-progression.md) §3.

## Partly working

- The mod SDK validates native character-build previews and commits confirmed imports after session drain; the optional POB mod and converter are external.

- **On-kill explosions and attribute damage reduction (implemented 2026-11-04; native behavior checked with the
  official client 2026-11-05, server live check pending):** "Enemies you Kill Explode, dealing X% of their Life as
  Physical Damage" (the Crusader body-armour prefix and the synthesis implicit) deals X% of the killed monster's
  maximum life as physical damage to every hostile monster within 25 cells, mitigated by each target's resistances,
  and a kill by the explosion can trigger another one. The radius is a fixed 25 cells: measured with the official
  3.29.3b client, one explosion killed at 22.85 and 24.35 cells, and increased area of effect does not extend it.
  The explosion consumes the corpse (0x2bd at once) and plays the native `HolyExplosion` art (MiscAnimated 2877,
  `Metadata/Effects/Spells/holy_explode/holy_explode.ao`) for two seconds; its corpse was removed 0-6 ms after the
  death. The Eater of Worlds body-armour implicits "1% less Damage Taken per X
- Conditional item gates feed live pools and stat publication; Kintsugi incoming hits, expiry and lifecycle verified.
  Weapon/aura, tracked-event, numeric-threshold and supported runtime-state gates have automated coverage.
  The item mods' "when on low/full life or mana" spelling shares the "while" state flags and folds onto the plain stat.
  Untracked, counted-recent and compound operators remain partial ([13](../docs/protocol/13-items.md)).

- **On-kill explosions and attribute damage reduction (implemented 2026-11-04, automated verification; live client
  pending):** "Enemies you Kill Explode, dealing X% of their Life as Physical Damage" (the Crusader body-armour prefix
  and the synthesis implicit) deals X% of the killed monster's maximum life as physical damage to every hostile monster
  within 20 cells, mitigated by each target's resistances; increased area of effect scales the radius, and a kill by the
  explosion can trigger another one. The Eater of Worlds body-armour implicits "1% less Damage Taken per X
  Strength/Dexterity/Intelligence" reduce hits, ignite and the other damage over time taken as a final multiplier.
  Not modelled: the chance-based and conditional (ignited, shocked, bleeding) explode variants and secondary damage's
  scaling by global physical/area damage.
  Code: `KillExplosions.cs`, `IncomingResourceDefences.cs`, `IncomingDamage.cs`, `PlayerAilments.cs`, `Player.cs`.

- **Item modifier effect (implemented 2026-11-03, automated verification; live client pending):** the item-local
  increased-effect stats scale the item's own explicit modifiers — every explicit one
  (`local_explicit_mod_effect_+%`: Simplex Amulet 100%, Focused Amulet 100%, Subsume the Source 120–240%) or one side
  only (`local_prefix_mod_effect_+%` / `local_suffix_mod_effect_+%`: the Cogwork, Geodesic, Composite, Manifold,
  Ratcheting and Helical Rings, 25% or 50%). Implicits, enchants and the effect stats themselves stay raw, and values
  round down like The Adorned's jewel effect. The same implicits' `local_maximum_prefixes_allowed_+` and
  `local_maximum_suffixes_allowed_+` move the rare affix caps for currency and bench crafting
  (Composite 6/0, Manifold 4/1, Simplex 1/2, Helical 1/4 …).
  Not modelled: the weapon-tree damage-category effect stats (Mods.dat has no damage-type tags), unveiled/tincture
  effect, and the per-socketed-abyss-jewel variant.
  Code: `ItemModStats.cs`, `ItemRolls.cs`, `BenchCrafting.cs`, `ItemWeaponStats.cs`.

- Utility-flask orbs: Enkindling and Instilling use native weighted enchant pools, replacing previous enchantments.
  Enkindling coefficients and the active-effect charge-gain lock are wired; Instilling uses paid flask-use events.
  Automated checks pass. Native checks confirmed both currency transactions, enchant replacement, maximum-charge
  clamping, the active-effect charge lock and automatic use after hitting a rare monster.
  Incoming Bleed/Poison sources remain absent, although their enchantment conditions are represented.
  See [13-items](../docs/protocol/13-items.md), utility flask orb enchantments.


- Sacred Orb rerolls: ordinary and Shift-repeat currency routes replace only the shared defence base roll (0..31).
  Requires Armour/Mods components and a known variable native range.
  Corrupted, mirrored and fixed-range targets are refused.
  The same roll can recur and still costs one orb; affixes, quality, sockets, gems and other properties stay intact.
  Automated coverage checks eligibility, persistence, save rollback and worn-item pool refresh.
  Fourteen native captures recorded at `63c1039` verify ordinary/repeat use, refusals, inventory/stash combinations,
  unchanged outcomes and restart persistence; fresh checks of the merged branch remain open.
  Ward ranges count for eligibility, but Ward character calculations and recovery remain unimplemented.
  Code: `CurrencyEffects.Sacred`, Game module (`Modules/Core/Game/Item/Items.cs`).
  Docs: [13](../docs/protocol/13-items.md), "Sacred Orb".
- Special defence rules: local zero-ES modifiers override this item's base, mods and quality only for ES.
  Supported equipment ES-to-Life conversion truncates each item's contribution, removes all equipment ES and adds
  base Life before Life increases; independent global/aura ES remains ES and excluded shields grant no converted Life.
  The corrupted-item conditional ES increase enters the global ES bucket, leaving tooltips and converted Life alone.
  Hidden no-attribute-requirement modifiers retain the level gate.
  Local requirement percentages (`local_attribute_requirements_+%`, `local_strength_requirement_+%` and the dexterity
  and intelligence ones) change an item's attribute requirements, counting a row that is both implicit and explicit once;
  automated, no native requirement check yet.
  Automated coverage passes; historical native conversion and corrupted-item ES controls are recorded at `0bae37a`
  and `c9060e4` (see the protocol reference for values).
  Fresh real-client checks of the merged branch and other unique/conditional mechanics remain open.
  Code: `ItemDefences.cs`, `ItemModStats.cs`, Game module (`Modules/Core/Game/Item/Equipment.cs`).
  Docs: [13](../docs/protocol/13-items.md) §2-§2d.
- **Unique items and Mageblood (verified live 2026-10-26).** The client data links a unique's name (`Words` wordlist 6)
  and artwork (`UniqueStashLayout` → `ItemVisualIdentity`), but not its mods, so `UniqueCatalog` holds each definition:
  1,299 generated from Path of Building with their current base, implicits and rolls (`scripts/re/unique_items.py`),
  compared with poedb's item cards, with the differences that remain on the roadmap. Divine and Blessed Orbs reroll
  within a unique's own rolls, and a range in world.db's `mod_stat_overrides` wins over them.
  Ordinary uniques get default sockets; Tabula gets six linked whites and hidden overrides (tests; live check pending).
  Skin of the Loyal gets its six linked random-colour sockets, and the 3.29 hidden no-white-socket property is on it and
  Dialla's Malefaction (automated; live check pending).
  Fixed "Has X Sockets" counts (Loreweave, Voidfletcher, The Squire, crests and hats) and Malachai's all-white Unset Ring
  socket are generated from those rows too; link groups there use the Jeweller's approximation (automated).
  Every definition but the five tinctures creates, writes and reloads by test (the server cannot write the Tincture
  component). Seen in the client 2026-10-30, with their name, art, base, mods and item level: Tulfall, Shimmeron,
  Replica Atziri's Foible, Kalandra's Touch, Emperor's Vigilance, The Broken Elegy, Gorgon's Gaze, Grand Spectrum and
  Atziri's Reign.
  Abyss jewels fit allocated passive sockets and equipment Abyss sockets. Active hosts contribute socketed jewel stats
  and eye counts; Stygian Vise rolls its Abyss socket. Individual Gaze and Historic effects need their own consumers.
  `/item <name> <base name>` reaches one base of a unique with several (`/item Grand Spectrum Cobalt Jewel`).
  `/item Mageblood` creates the Heavy Belt unique with its name, artwork and fixed mods (the Strength is the base
  implicit); the client shows the unique tooltip.
  While Mageblood is worn, the leftmost 2–4 magic utility flasks in the flask bar apply their effects permanently without
  charges or a timer, and using them is refused (client mod, server backstop); the stats refresh when the belt or the
  flask bar changes. Its flasks keep their buffs on the client without a timer (not seen live yet).
  Inspired Learning grants one eligible rare affix for 20 s when its ordinary socket has four allocated notables in
  range. Duplicate stolen effects retain the longer lifetime.
  Bound By Destiny checks its 2/4/6 thresholds against active influenced equipment; dual-influenced items count
  once toward each matching influence. Qualified stats enter attributes and combat through the normal item sources.
  Headhunter: killing a rare gives each of its rolled affixes as a stack of character stats for 60 s (the same one resets
  its time), with one Headhunter buff showing the count and the longest time, and
  its rolled increased damage against rares (not seen live yet; they end on an area change, where the game keeps them).
  Not yet: other uniques' mechanics, chancing, the "effects cannot be removed" backstop, the Foulborn rightmost variant,
  Replica Headhunter and Enkindling (`UniqueCatalog`, `Game.Mageblood`, `StolenMods`, the UniqueBelts module;
  [13](../docs/protocol/13-items.md) §2e, §8a).

- Ruthless uses native mod/flask alternatives, reduced tunable drops and vendor returns, and refuses bench crafting.
  Entering town or a hideout does not refill its pools/flasks; supported vendors refill them instead.
  Active utility flasks cannot gain charges, and native Ruthless durations/Onslaught values apply.
  Automated pool-restoration and session-ownership checks cover area changes, reconnects and vendor refill persistence.
  Drop, recipe, bench and flask-rule calculations have automated coverage; full retail balance remains partial.
  See [league modes](../docs/game/league-modes.md).
- Essence crafting (2026-10-25, automated verification; real-client use pending): all 105 craftable essences resolve
  their exact class-specific guaranteed mod from Essences.dat, including tier-8 and Desolation.
  Normal/rare eligibility, random-mod level caps, fractures, metamod refusal and repeated consumption from the Essence
  tab are covered.
  Uses the existing currency path for ordinary and Shift application.
  See [13](../docs/protocol/13-items.md).
- Quiver eligibility fix (2026-10-25, awaiting client confirmation): active bows publish `main_hand_weapon_type = 9`,
  required by the client's inventory gate; every hand now sends its weapon type (see "Client weapon-type stats" in
  [13](../docs/protocol/13-items.md)).
- **Jeweller's Orb: verified live (2026-10-21).** Normal use and Shift share the currency handler; rerolls counts
  1/2/3/4/6 and link groups, limited by class, dimensions and item level, and consumes one orb.
  3.29: sockets the reroll keeps retain their colours and only the added sockets are white.
  Socketed gems, unsupported targets and maximum base capacity are refused without spending an orb.
  The live-found refusal of level-1 boots/gloves with two sockets is fixed and verified live.
  Count and link probabilities are approximate; special socket overrides are not modelled.
  See [13](../docs/protocol/13-items.md), "Jeweller's Orb".
- **Socket colours (3.29; verified live 2026-10-26).** Item sockets generate white and may roll a
  colour in proportion to the base's attribute requirements, with the chance rising with item level; a gem in a
  matching red/green/blue socket gets +10% quality (the stat path is in [25](../docs/protocol/25-gem-supports.md)).
  Chromatic Orbs force one socket non-white and roll the rest, with item quality raising their chance; the bench's
  "At Least Two/Three/Four Non-White Sockets" crafts (`NN`/`NNN`/`NNNN`) reroll until they guarantee that many; Vaal
  corruption colours one white socket. `/item`, drops and shops use the same generator, and the start weapon's two
  linked sockets are white.
  The probabilities are an approximation (5% non-white at item level 1, +0.25 points per level up to 30% at 100, and
  +0.25 points per 1% quality for Chromatic/bench); `SocketColours`, [13](../docs/protocol/13-items.md),
  "Socket colours (3.29)".
  Not modelled: Tainted Chromatic/Jeweller's Orbs, Orb of Binding, the Locus of Corruption, the 25-roll bench reward,
  special item overrides.
- **Orb of Alchemy:** a modifiable normal item becomes an identified rare with 4-6 weighted affixes (CoE's 2/3, 1/4,
  1/12; jewels 3-4 with two per side) and a rare name. Flasks are magic-only; unknown/unsupported/unmodifiable,
  non-normal, corrupted/mirrored and empty-pool targets are refused before spending.
  CoE count/jewel checks, ordinary/Shift inventory/stash dispatch and save reloads have automated coverage.
  The old live check (2026-10-21) predates these rules; native re-acceptance is pending.
  See [13](../docs/protocol/13-items.md), "Orb of Alchemy", and [evidence](../docs/re/alchemy-coe.md).
- **Chaos Orb:** a modifiable rare item's unprotected affixes are rerolled (CoE 4/5/6 counts, jewels 3-4 with two per
  side; new tiers, new rare name); prefix/suffix metamod locks keep their side. Flasks are magic-only; non-rare,
  unmodifiable/unsupported and empty-pool targets are refused before spending.
  CoE count/metamod checks, ordinary/Shift inventory/stash dispatch and save reloads have automated coverage.
  The old live check (2026-10-21) predates these rules; native re-acceptance is pending.
  See [13](../docs/protocol/13-items.md), "Chaos Orb", and [evidence](../docs/re/chaos-coe.md).
- **Divine Orb (verified live, 2026-10-21):** keeps the affixes, tiers and order and rerolls only values.
  Shift works. See [13](../docs/protocol/13-items.md), "Divine Orb".
- **Orb of Scouring (verified live, 2026-10-21):** a magic / rare / unique item becomes normal, only base mods stay.
  See [13](../docs/protocol/13-items.md), "Orb of Scouring".
- **Orb of Transmutation:** normal to identified magic, targeting 1-2 weighted affixes at equal probability.
  Unknown, unsupported, unmodifiable and empty-pool bases are refused before spending.
  CoE count/pool comparisons, ordinary/Shift inventory/stash dispatch and save reloads have automated coverage.
  Framed native wand/flask upgrades, repeat/unmodifiable refusal and relog passed on 2026-10-29.
  Fresh native Shift/stash and manual currency UI checks remain separate.
  See [13](../docs/protocol/13-items.md), "Orb of Transmutation", and [evidence](../docs/re/transmutation-coe.md).
- **Orb of Alteration:** a modifiable magic item gets 1-2 new weighted affixes (one prefix, one suffix at most),
  with the same admission as the other affix currencies: unknown/unsupported/`Unmodifiable` bases, corrupted/mirrored
  items and empty pools are refused before spending.
  CoE count/flask checks, ordinary/Shift inventory/stash dispatch and save reloads have automated coverage.
  The old live check (2026-10-21) predates these admission rules; native re-acceptance is pending.
  See [13](../docs/protocol/13-items.md), "Orb of Alteration", and [evidence](../docs/re/alteration-coe.md).
- **Orb of Augmentation:** +1 weighted affix on a magic item, up to one prefix and one suffix, with the same admission
  checks as Transmutation: a known, serializable, non-`Unmodifiable` base with Mods; corrupted/mirrored, full items
  and open sides without ordinary affixes are refused before spending.
  CoE beta pool and simulated-probability checks, ordinary/Shift inventory/stash dispatch and save reloads have
  automated coverage. The old live check (2026-10-21) predates these admission checks; native re-acceptance is pending.
  See [13](../docs/protocol/13-items.md), "Orb of Augmentation", and
  [evidence](../docs/re/augmentation-coe.md).
- **Regal Orb:** a modifiable magic item becomes rare, affixes kept, +1 weighted affix from the rare pool (max three
  of each side), rare name. Flasks are refused (magic-only), as are unmodifiable/unsupported bases and no-fit targets,
  without spending.
  CoE rare-pool/apply checks, ordinary/Shift inventory/stash dispatch and save reloads have automated coverage.
  The old live check (2026-10-21) predates these admission rules; native re-acceptance is pending.
  See [13](../docs/protocol/13-items.md), "Regal Orb", and [evidence](../docs/re/regal-coe.md).
- **Exalted Orb:** +1 weighted affix on a modifiable rare item, up to 6 (three per side; jewels two per side), name
  kept. Flasks, unmodifiable/unsupported bases, non-rare and full targets are refused before spending.
  CoE pool/jewel checks, ordinary/Shift inventory/stash dispatch and save reloads have automated coverage.
  The old live check (2026-10-21) predates these rules; native re-acceptance is pending.
  See [13](../docs/protocol/13-items.md), "Exalted Orb", and [evidence](../docs/re/exalted-coe.md).
- **Orb of Annulment:** removes one random unprotected affix from a magic or rare item; rarity, name, implicits and
  the other affixes stay. Metamod/fractured filters exclude protected affixes; corrupted/mirrored/unsupported/
  `Unmodifiable` and no-eligible-explicit targets are refused before spending.
  CoE rarity/metamod checks, ordinary/Shift inventory/stash dispatch and save reloads have automated coverage.
  The old live check (2026-10-21) predates these rules; native re-acceptance is pending.
  See [13](../docs/protocol/13-items.md), "Orb of Annulment", and [evidence](../docs/re/annulment-coe.md).
- **Armourer's Scrap:** quality scales with item level, up to 20%; the revised scaling is not yet verified live.
  See [13](../docs/protocol/13-items.md), "Armourer's Scrap".
- **Blacksmith's Whetstone:** quality scales with item level, up to 20%; the revised scaling is not yet verified live.
  See [13](../docs/protocol/13-items.md), "Blacksmith's Whetstone".
- **Gemcutter's Prism (no test, not checked live):** +1% gem quality, up to 20% ([13](../docs/protocol/13-items.md)).
- **Ancient Orb:** reforges an uncorrupted unique piece of equipment as another of its item class, by the uniques' drop
  weights, keeping item level, quality, sockets and links. Only uniques of the same inventory size; no corrupted or Replica
  results (`UniqueCatalog.Reforge`). Not checked in game yet.
- **Orb of Chance:** the documented local rule: a normal weapon / armour piece → rare (30%, 4-6 affixes) or magic
  (2-4 affixes, at most two per side), identified. Corrupted/mirrored/unsupported/`Unmodifiable` and empty-pool
  targets are refused before spending.
  CoE beta has no Chance handler, so official unique/rarity outcomes remain separate work.
  Ordinary/Shift inventory/stash dispatch and save reloads have automated coverage; native re-acceptance is pending.
  See [13](../docs/protocol/13-items.md), "Orb of Chance", and [admission](../docs/re/chance-coe.md).
- **Vaal Orb and corruption:** the local rule corrupts a modifiable weapon, armour piece or jewellery (Base +0xc7
  bit 0, saved); normal / magic → rare with 4-6 affixes, a rare item gets 1-5 or all affixes rerolled, one white socket
  is coloured. Corrupted/mirrored/unique, unsupported/`Unmodifiable` and empty-pool targets are refused before
  spending; a corrupted item takes no currency but Wisdom.
  CoE's official Vaal outcome table remains separate work; the admission and boundary are documented.
  Ordinary/Shift inventory/stash dispatch and save reloads have automated coverage; native re-acceptance is pending.
  See [13](../docs/protocol/13-items.md), "Vaal Orb and corruption", and [admission](../docs/re/vaal-coe.md).
- **Eldritch Ichors (Lesser, Greater, Grand, Exceptional):** add the tier's Eater of Worlds implicit to body armour,
  boots, gloves or helmet, replacing every other implicit except Searing Exarch ones; normal/magic/rare only.
  Unique/corrupted/mirrored, non-armour and unsupported/unmodifiable targets are refused before spending.
  CoE base-key/tier/presence checks, all four tier suffixes and ordinary/Shift inventory/stash dispatch have automated
  coverage. A separate Eldritch dominance state and the Ember/Conflict family remain separate, and native tooltip
  acceptance is pending.
  See [13](../docs/protocol/13-items.md), "Eldritch Ichors", and [evidence](../docs/re/eldritch-ichor-coe.md).
- **Eldritch Embers (Lesser, Greater, Grand, Exceptional):** the Searing Exarch mirror of the Ichors; add the tier's
  Exarch implicit to body armour, boots, gloves or helmet, keep Eater implicits and remove the other implicits;
  normal/magic/rare only. Unique/corrupted/mirrored, non-armour and unsupported/unmodifiable targets are refused.
  CoE base-key/tier/presence checks, all four tier suffixes and ordinary/Shift inventory/stash dispatch have automated
  coverage. A separate Eldritch dominance state and the Orb of Conflict remain separate, and native tooltip
  acceptance is pending.
  See [13](../docs/protocol/13-items.md), "Eldritch Embers", and [evidence](../docs/re/eldritch-ember-coe.md).
- **Cartographer's Chisel, Eldritch Chaos/Exalted Orb and Orb of Unmaking (added 2026-11-05, not checked live):** the
  chisels now reach the existing map-quality effect through their `CurrencyItems.Action` (`add_map_alt_quality`), so a
  chisel or a Maven's Chisel adds 20% of its AlternateQualityTypes row to a map. Eldritch Chaos and Eldritch
  Exalted Orb reroll an existing rare's explicits (as Chaos/Exalted do) only when the item carries an Eater of Worlds or
  Searing Exarch implicit, which stays. Orb of Unmaking, used from the bag (C→S 0x58), refunds every allocated Atlas
  passive of all three presets and is consumed; with nothing allocated it stays. The three local rules are not the
  official ones. Two Gateway tests cover the chisel's admission and the Eldritch admission.
  Code: `CurrencyEffects.Chisel/TryAction`, `EldritchChaos/EldritchExalted`, `Game.UseOrbOfUnmaking`.
- **Orb of Binding, the Eldritch Orb of Annulment and the Tainted (Scourge) family (added 2026-11-05, not checked live):**
  Orb of Binding turns a normal uncorrupted weapon or armour piece that can hold four sockets into a rare (as Alchemy) with
  four linked sockets whose colours come from the base's requirements. The Eldritch Orb of Annulment follows the two other
  Eldritch orbs: an Annulment on an item carrying an Eater of Worlds or Searing Exarch implicit, which stays. The seven
  Tainted currencies this class applies (Chaos, Exalted, Chromatic, Fusing, Jeweller's, Armourer's Scrap, Blacksmith's
  Whetstone) apply to a corrupted item and leave it corrupted; tainted quality currency adds 1%. The item is handed with its
  corruption cleared and the result is corrupted again, so a non-corrupted item is refused before spending, as is anything
  the ordinary effect refuses. Tainted Mythic, Tainted Divine Teardrop, Tainted Catalyst, Tainted Oil and Tainted Blessing
  are separate. Local rules, not the official ones. Two Gateway tests cover the Binding admission and the Tainted/Eldritch
  admission.
  Code: `CurrencyEffects.Binding/EldritchAnnulment/Tainted`, `Game.CurrencyResult`.
- **Enkindling Orb:** a utility flask of any rarity gets one of the five GenerationType 21 enchantments (increased
  effect / duration / Charge Recovery / maximum Charges, or reduced Charges per use, all with `Gains no Charges during
  Effect`) with the game's 250/100/100/100/100 weights. Life/mana, corrupted/mirrored, non-flask and
  unsupported/unmodifiable targets are refused before spending. An existing Enkindling is replaced; an existing
  Instilling and the flask's explicit affixes, quality, charges, rarity and name stay. Harvest/bench flask
  enchantments (GenerationType 3, family `FlaskEnchantment`) are removed. The `Gains no Charges during Effect`
  drawback is enforced by `FlaskEnchantments.BlocksGain` through `CanGainFlaskCharges` for kills, hits, crits, the
  every-3-seconds recovery, refills and projectiles while the effect runs; expired effects and pause are covered.
  CoE pool/weights and native coexistence checks have automated coverage; see
  [13](../docs/protocol/13-items.md), "Enkindling Orb and Instilling Orb", and
  [evidence](../docs/re/enkindling-coe.md).
- **Instilling Orb:** the Searing Exarch counterpart: a utility flask gets one of the fifteen spawnable GenerationType
  22 trigger enchantments (charges full, frozen/chilled/shocked/ignited/bleeding/poisoned, block, savage hit, guard or
  travel skill, rare/unique hit, adjacent flask, effect end, life flask), equal weight. The sixteenth row
  (`Used when you lose a Guard Skill Buff`) has spawn weight 0 in 3.29.3b and is excluded, as are the bench recipes.
  An existing Instilling is replaced; an existing Enkindling and the rest of the item stay. See
  [13](../docs/protocol/13-items.md), "Enkindling Orb and Instilling Orb", and
  [evidence](../docs/re/instilling-coe.md).
- **Visual client acceptance (2026-10-30):** the main successful uses of all implemented currencies were checked in
  the 3.29.3b client: Transmutation, Augmentation, Regal, Alteration, Alchemy, Exalted, Chaos, Vaal, Chance,
  Annulment, all four Eldritch Ichors and all four Eldritch Embers, Enkindling and Instilling. Vaal turned normal
  items rare (yellow) and the tooltip showed `Corrupted`; the Eater/Exarch implicit names, values and tiers were
  visible. Enkindling and Instilling both appeared on one flask, repeated use replaced only its own type, and magic
  explicit affixes stayed. No visual defects were found. Vaal on jewels remains outside the local rule; official
  jewel/Vaal outcomes are separate work. Stash/Shift and the remaining refusal paths stay under automated coverage.
- **Glassblower's Bauble:** quality scales with item level, up to 20%; the revised scaling is not yet verified live.
  See [13](../docs/protocol/13-items.md), "Glassblower's Bauble".
- **Fracturing Orb (verified live, 2026-10-21; rules rechecked 2026-10-30):** one random explicit mod of a rare item
  with 4+ explicit mods is fractured and kept by every other currency, Scouring included; shown fractured (magic
  properties u8 +0x7a). The admission checks now also require a supported non-`Unmodifiable` base whose
  `ItemClasses.CanBeFractured` is set, and refuse corrupted/mirrored/already-fractured items; metamod-protected mods
  stay eligible; influenced and synthesised items are refused. Veiled modifiers are not modelled. Native 2026-10-30: a four-mod rare wand
  was fractured, a second use was refused without spending. See [13](../docs/protocol/13-items.md), "Fracturing Orb",
  and [the game rules](../docs/re/fracturing-mirror-mechanics.md).
- Mirror copies non-unique equipment, flasks and T1–16 maps onto the cursor ([13](../docs/protocol/13-items.md)).
- **Hinekora's Lock (server rules verified live, 2026-10-31):** applying the Lock through C→S 0x71 to a rare item
  sets the Mods flag bit 8 (0x0100) and a non-zero seed; the client shows the purple glow and the Lock tooltip text. When a currency is
  used on the item the client sends C→S 0x29d (u16 key, currency/target ids) and S→C 0x2a0 answers with the preview
  item (keyA = request key, status 0 = preview, 0x04FA = failure); the next 0x71 commits exactly that preview,
  consumes the currency and clears the state. Normal, magic, gem, corrupted, mirrored, unmodifiable and
  already-foreseeing targets are refused without spending; bench/Harvest/essence and every successful currency clear
  the state. The client's
  preview branch is the item popup's foresight element (`HinekoraForesightPopupIcon`, `HinekorasLockFutureSight`);
  status 0 shows it and any non-zero status plays `HinekorasLockFail`. The committed result matches the preview byte
  for byte. See [13](../docs/protocol/13-items.md), "Hinekora's Lock", and
  [the evidence](../docs/re/hinekora-lock.md).
- **Orb of Fusing: verified live (2026-10-21).** Captured 0x71/0x72 packets feed the same handler.
  Only links change; sockets, colours, gems and item properties stay intact.
  Refuses fewer than two sockets and protects fully linked 5/6-socket items; 2-4 sockets remain rerollable.
  Quality favours longer groups without being consumed.
  Link/quality probabilities are approximate, and special overrides/corruption are not modelled.
  See [13](../docs/protocol/13-items.md), "Orb of Fusing".
- Currency metamods: Scouring/Chaos/Divine/Annul respect prefix/suffix locks;
  annul and affix additions respect attack/caster restrictions.
  Scouring retains quality and refuses uniques.
  Real-data regression tests cover both lock directions.

## Not working yet

- Most uniques and special mods (#60), and chancing; Mageblood and normal Crown of Eyes have definitions;
  currency other than Scroll of Wisdom, Chromatic Orb, Jeweller's Orb, Orb of Fusing, Orb of Alchemy, Chaos Orb,
  Divine Orb, Orb of Scouring, Orb of Transmutation, Orb of Alteration, Orb of Augmentation, Regal Orb, Exalted Orb,
  Orb of Annulment, Armourer's Scrap, Blacksmith's Whetstone, Orb of Chance, Vaal Orb, the four Eldritch Ichors and
  Embers, Enkindling Orb, Instilling Orb, Glassblower's Bauble, Fracturing Orb, Mirror of Kalandra, Hinekora's
  Lock, Gemcutter's Prism, Sacred Orb and Orb of Regret; support gems and links ([13](../docs/protocol/13-items.md)
  "Open").
