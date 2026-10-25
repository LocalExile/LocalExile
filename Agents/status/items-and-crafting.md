# Status: Items, currency and crafting

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/items-and-crafting.md](../roadmap/items-and-crafting.md).

## Works (verified with the real client)

- Ground items belong to the area instance: dropped items and loot stay where they lay across logouts, disconnects and
  client crashes until the instance expires; ids from 0x100000; one pickup per item.
  Code: `InstanceStore.cs`, Gateway.
  Docs: [13](../docs/protocol/13-items.md) §5.
- Life and mana flasks: a flask key (C→S 0x70) spends the flask's charges and recovers Flasks.dat's life or mana over
  its time in a 20 ms loop, the orb rising by +1, with the flask_effect buff;
  every kill, town, hideout and resurrection fills the flasks.
  Code: `Flasks.cs`, Gateway (`UseFlask`, `RunFlaskRecovery`).
  Docs: [13](../docs/protocol/13-items.md) §8.
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
  `/mod`, `/mods`, `/rarity` on the held item.
  Magic and rare items: affixes rolled from Mods.dat by domain, item level, the item's tags and families, rare names
  from Words.dat; equipment drops magic / rare and unidentified; a Scroll of Wisdom identifies (C→S 0x71);
  `/item <type> magic|rare`, `/roll`.
  Equipped items' mods count on the character: global stats like a passive's (life, resistances, attributes, spell
  damage …), the main hand's local crit, attack speed and accuracy on the weapon.
  Not yet: local weapon damage and defence mods, uniques and special mods (#60).
  Code: `InstanceItems.cs`, Gateway (`ModCatalog.cs`, `ItemRolls.cs`, `ItemDrops.cs`, `ItemModStats.cs`,
  `ItemCatalog.cs`, `CharacterSaves.cs`).
  Docs: [13](../docs/protocol/13-items.md) §2a-§2c.
- Gold: shared by the account's characters of one league (S→C 0x2a9), saved with that league's stash, sent on area
  entry; every league has its own stash and gold, Standard keeps the account's stash from before (`AccountStash.Key`);
  monsters drop piles of gold (normal 40%, magic 75%, rare and unique always;
  (3 + 1.2 × level) × 1/2/5/15, our own numbers, `ItemDrops.Gold`), and picking one up adds it to the gold;
  gold within 15 cells of the player is picked up by itself (player tick, `GoldAutoLootRange`);
  older per-character gold moves into the account at login; `/gold <±amount>`.
  Code: `InstanceEntities.Gold`, `AccountStash`, Gateway.
  Docs: [04](../docs/protocol/04-player-entity.md) §6.
- Loot: dying monsters drop currency, flasks and equipment (our own table) around the corpse.
  Code: `ItemDrops.cs`, Gateway (`DropLoot`).
  Docs: [15](../docs/protocol/15-progression.md) §3.

## Partly working

- Essence crafting (2026-10-25, automated verification; real-client use pending): all 105 craftable essences resolve
  their exact class-specific guaranteed mod from Essences.dat, including tier-8 and Desolation.
  Normal/rare eligibility, random-mod level caps, fractures, metamod refusal and repeated consumption from the Essence
  tab are covered.
  Uses the existing currency path for ordinary and Shift application.
  See [13](../docs/protocol/13-items.md).
- Quiver eligibility fix (2026-10-25, awaiting client confirmation): active bows now publish `main_hand_weapon_type =
  9`, required by the client's inventory gate; see [13](../docs/protocol/13-items.md).
- **Jeweller's Orb: initial local implementation verified live (2026-10-21).** Normal use and Shift share the currency
  handler; rerolls counts 1/2/3/4/6, RGB colours and link groups, limited by class, dimensions and item level.
  Socketed gems, unsupported targets and maximum base capacity are refused without spending an orb.
  The live-found refusal of level-1 boots/gloves with two sockets is fixed and verified live.
  Probabilities are approximate; quality and special socket overrides are not modelled.
  See [13](../docs/protocol/13-items.md), "Jeweller's Orb".
- **Jeweller's Orb (2026-10-21).** Initial implementation verified live: normal and Shift use rerolls empty sockets,
  colours and links, consumes one orb, updates inventory and saves the result.
  Class, dimensions and item level limit socket rolls; only base capacity blocks further use.
  Local counts are 1/2/3/4/6.
  Probabilities are approximate; quality, white-socket 3.29 rules and special item overrides remain open.
  See protocol/13-items.md, Jeweller's Orb.
- **Orb of Alchemy, Chaos Orb, Divine Orb (verified live, 2026-10-21):** Alchemy makes a normal item an identified
  rare with 4-6 random affixes and a rare name; Chaos rerolls all of a rare item's affixes (4-6, new tiers);
  Divine keeps the affixes, tiers and order and rerolls only values.
  Shift works for all three.
  See [13](../docs/protocol/13-items.md), "Orb of Alchemy", "Chaos Orb", "Divine Orb".
- **Orb of Scouring (verified live, 2026-10-21):** a magic / rare / unique item becomes normal, only base mods stay.
  See [13](../docs/protocol/13-items.md), "Orb of Scouring".
- **Orb of Transmutation (verified live, 2026-10-21):** a normal item becomes magic with 1-2 random affixes.
  See [13](../docs/protocol/13-items.md), "Orb of Transmutation".
- **Orb of Alteration (verified live, 2026-10-21):** a magic item gets 1-2 new affixes (one prefix, one suffix at
  most).
  See [13](../docs/protocol/13-items.md), "Orb of Alteration".
- **Orb of Augmentation (verified live, 2026-10-21):** +1 affix on a magic item, up to one prefix and one suffix.
  See [13](../docs/protocol/13-items.md), "Orb of Augmentation".
- **Regal Orb (verified live, 2026-10-21):** magic → rare, affixes kept, +1 affix, rare name.
  See [13](../docs/protocol/13-items.md), "Regal Orb".
- **Exalted Orb (verified live, 2026-10-21):** +1 affix on a rare item, up to 6 (three of each kind).
  See [13](../docs/protocol/13-items.md), "Exalted Orb".
- **Orb of Annulment (verified live, 2026-10-21):** removes one random affix from a magic or rare item.
  See [13](../docs/protocol/13-items.md), "Orb of Annulment".
- **Armourer's Scrap (verified live, 2026-10-21):** +1% quality on armour, up to 20%; Scouring resets it to 0.
  See [13](../docs/protocol/13-items.md), "Armourer's Scrap".
- **Blacksmith's Whetstone (verified live, 2026-10-21):** +1% quality on weapons, up to 20%; Scouring resets it.
  See [13](../docs/protocol/13-items.md), "Blacksmith's Whetstone".
- **Orb of Chance (verified live, 2026-10-21):** a normal weapon / armour piece → rare (30%, 4-6 affixes) or magic
  (2-4 affixes).
  See [13](../docs/protocol/13-items.md), "Orb of Chance".
- **Vaal Orb and corruption (verified live, 2026-10-21):** weapons, armour and jewellery are always corrupted (Base
  +0xc7 bit 0, saved); normal / magic → rare with 4-6 affixes, a rare item gets 1-5 or all affixes rerolled;
  a corrupted item takes no currency but Wisdom.
  See [13](../docs/protocol/13-items.md), "Vaal Orb and corruption".
- **Glassblower's Bauble (verified live, 2026-10-21):** +1% flask quality up to 20%; Scouring resets it.
  See [13](../docs/protocol/13-items.md), "Glassblower's Bauble".
- **Fracturing Orb (verified live, 2026-10-21):** one random mod of a rare item with 4+ mods is fractured and kept by
  every other currency, Scouring included; shown fractured (magic properties u8 +0x7a).
  See [13](../docs/protocol/13-items.md), "Fracturing Orb".
- **Mirror of Kalandra (verified live, 2026-10-21):** a mirrored copy (Mods flag bit 1) of a non-corrupted,
  non-mirrored item goes onto the cursor; mirrored items take no currency.
  See [13](../docs/protocol/13-items.md), "Mirror of Kalandra".
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

- Uniques and special mods (#60), items' base damage and defences;
  currency other than Scroll of Wisdom, Chromatic Orb, Jeweller's Orb, Orb of Fusing, Orb of Alchemy, Chaos Orb,
  Divine Orb, Orb of Scouring, Orb of Transmutation, Orb of Alteration, Orb of Augmentation, Regal Orb, Exalted Orb,
  Orb of Annulment, Armourer's Scrap, Blacksmith's Whetstone, Orb of Chance, Vaal Orb, Glassblower's Bauble,
  Fracturing Orb, Mirror of Kalandra, Gemcutter's Prism and Orb of Regret; utility flasks;
  support gems and links ([13](../docs/protocol/13-items.md) "Open").
