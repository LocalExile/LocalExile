# Roadmap: Items, currency and crafting

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/items-and-crafting.md](../status/items-and-crafting.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 1. Quality of life

Essence item crafting is implemented (105 currencies, class-specific guaranteed modifiers;
see Status and protocol/13-items.md).
Imprisoned essence encounters, Remnant of Corruption on monsters and stash-tab essence upgrading remain separate
features.

## 3. Gameplay

### Items: mods, rarity, using flasks and currency

- **Jeweller's Orb:** initial local 0x71/Shift 0x72 implementation rerolls counts, colours and links with class and
  item-level caps; the live-found refusal at the item-level cap is fixed (only base capacity blocks use), verified
  live.
  See 13-items.md, "Jeweller's Orb" for the requested local rules and remaining exact-game behaviour.
- **Orb of Fusing:** normal/Shift handling and link-only rerolls are implemented and verified live.
  Exact link probabilities, quality scaling and special item overrides remain open (13-items.md, "Orb of Fusing").
- **Known:** items serialize, move, socket and drop ([13](../docs/protocol/13-items.md));
  rarity, implicits and every mod list are sent and saved (13 §2a);
  magic and rare items roll, drop unidentified and a Scroll of Wisdom identifies them (13 §2b);
  equipped items' mods count on the character (13 §2c).
  Open issues: enchants / crafted / corrupted / influences / uniques (#60), area and monster mods (#61).
  The Mud Flats glyphs are quest items (`Metadata/Items/QuestItems/Glyphs/Glyph1..3`,
  [12](../docs/protocol/12-quests.md)).
- **Start:** other currency through C→S 0x71 (13 §4: Orb of Transmutation, Alteration, Augmentation, Regal, Alchemy,
  Chaos, Scouring can reuse `ItemRolls`); items' base weapon damage and armour / evasion / energy shield (then their
  local mods, 13 §2c).
- **Socket colours (3.29 rules):** any gem goes into any socket, whatever the colours, and a gem in a red, green or
  blue socket of its own colour gets +10% quality.
  Sockets are white by default; each has a small chance to be red, green or blue instead, in proportion to the item's
  attribute requirements, and that chance rises with item level.
  Chromatic Orbs force one socket to be coloured and roll the rest normally (more quality, more coloured sockets).
  Jeweller's Orbs, Orbs of Binding and the Crafting Bench add white sockets.
  Vaal Orb corruption turns a white socket coloured, and the Locus of Corruption turns all white sockets coloured.
  Today `ItemCatalog.DefaultSockets` makes every socket the item's main attribute colour, which is the pre-3.29 look.
- **Done when:** items' damage and defences count, and flasks and currency can be used.

## 5. Project

- Completed currency metacraft protection for supported explicit-affix currencies;
  specialized unsupported currencies and official Chance/Vaal outcome distributions remain separate work.
