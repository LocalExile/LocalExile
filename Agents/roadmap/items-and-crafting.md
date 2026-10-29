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
  Life/mana flask charges, recovery, native drinking sound and full-pool refusal are implemented (13 §8).
  Open issues: enchants / crafted / corrupted / influences / uniques (#60), area and monster mods (#61).
  The Mud Flats glyphs are quest items (`Metadata/Items/QuestItems/Glyphs/Glyph1..3`,
  [12](../docs/protocol/12-quests.md)).
- **Start:** other currency through C→S 0x71 (13 §4: Orb of Transmutation, Alteration, Augmentation, Regal, Alchemy,
  Chaos, Scouring can reuse `ItemRolls`); the other uniques and special mods (#60, 13 §2e: `UniqueCatalog` defines
  Mageblood; each remaining unique needs its base type, mod rows and ranges curated), unique drops/chancing, and
  Mageblood's follow-ups (buff icons, the "effects cannot be removed" backstop, the Foulborn rightmost variant,
  Enkindling).
- **Socket colours (3.29):** the 3.29 rules are in (white by default with an item-level colour chance, the
  Chromatic and non-white bench crafts, white sockets from Jeweller's/bench socket adds, Vaal colouring one white
  socket; [13](../docs/protocol/13-items.md), "Socket colours (3.29)", Status).
  Remaining: Tainted Chromatic/Jeweller's Orbs, the Orb of Binding, the Locus of Corruption, the milestone bench
  reward (25 reforges keeping the most non-white sockets), special item overrides, and fitting the exact colour
  probabilities from live captures (the current curve is an approximation).
- **Done when:** items' damage and defences count, and flasks and currency can be used.

## 5. Project

- Completed currency metacraft protection for supported explicit-affix currencies;
  specialized unsupported currencies and official Chance/Vaal outcome distributions remain separate work.
