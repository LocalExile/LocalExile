# 13 — Items: serialization, inventories, moving, sockets, stacks, the ground

Code: `src/GameServer.Core/Protocol/Instance/InstanceItems.cs` (`Item`, `VisibleItem`, `WorldItemObject`,
`InstanceItems`), `InstanceInventories.AddInventory` (`InstanceEntities.cs`); in the Gateway `ItemCatalog.cs`,
`PlayerInventories.cs` and the item handlers in `Program.Items.cs`. Tests: `InstanceItemsTests`. Live trace:
`scripts/re/frida/common.js` + `scripts/re/frida/trace-items.js`. Verified with the real client on 2026-10-18.

## 1. Item types

An item's type is a **BaseItemTypes row**. The client's `Metadata/ItemTypeRegister` files each row under its `HASH32`
column, which is MurmurHash2 of the row's Id: the same hash as object types (`InstanceEntities.ObjectTypeHash`;
`Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1` → 3652280879). Lookup: `0x1421ec390` → `0x141fa5290` (sorted array of
0x30-byte entries keyed by the u32).

Components come from the `.it` files, merged like `.ot` files ([04](04-player-entity.md), `scripts/re/ot_components.py`)
with two differences: nothing is prepended (no Positioned), and most leaf rows have no `.it` of their own, so their
chain is the row's `InheritsFrom`. `client { }` blocks (RenderItem) are never read. Generate every row's list with:

```bash
python -I scripts/re/ot_components.py --items --json data/item-types.json
```

`scripts/dev/extract-data.ps1` runs it (entry `item-types`; a change to the output bumps the entry's version).

| Type | Components the reader reaches |
|---|---|
| Wand1 (any weapon) | Base, LocalStats, Mods, AttributeRequirements, Weapon, Quality, Sockets |
| FlaskLife1 / FlaskMana1 | Base, Quality, LocalStats, Charges, Mods, Usable, Flask |
| Fireball / Arcane Surge (gems) | Base, SkillGem, Quality |
| Glyph1 (quest item) | Base, Quest |
| CurrencyIdentification | Base, Usable, Stack |

## 2. The item data

Write `0x1421ec1d0` → `0x1421ec010`, read `0x1421ec2c0` → `0x1421ec390`. Wrapped in a **u16 big-endian length**; inside,
everything is little-endian (memcpy):

```
u32  type hash (BaseItemTypes HASH32)
u16  flags            bits 0-8 → item +0x28..+0x2f (bit 4 also to the ctor 0x1421ebf10); bit 9 → u64 follows
...  each component's data, in the type's order: vtable slot 5 (+0x28) reads, slot 4 (+0x20) writes
u16  only for the four type hashes in 0x1421ebe90 (0x192c7aab, 0x365f206a, 0x503da850, 0x9aff2df6)
```

Item component vtables (slot 4 write / slot 5 read). The ones with a bare `ret` (`0x1400f0f00`) carry nothing:

| Component | Vtable | Read | Data |
|---|---|---|---|
| Base | `0x1435e0168` | `0x1421e69c0` | u16 ItemVisualIdentity **HASH16** (unknown → "Attempt to set visual identity to NULL"), u8 +0xc0, u8 +0xc7 flags, u16 flags (low bit0 → u8, bit1 flavour text u16, bit2 wide string, bit3 u32, bit4 wide string, bit5 u8 n × (u32, i32), bit6 nested item, bit7 u8 n × (u32 item type, compact); high bits 0-2 → +0xc4, bit4 → 2 × u32), u8 +0xc6 |
| Mods | `0x1435df838` | `0x1421df6e0` | u8 +0x2b8 (item level), u8 +0x2bc (the level requirement, kept as sent: §2d), u16 flags (bit6 incubator u16 u16 u8, bit4 → u8, bit15 → 0x1c8 record `0x1424b70c0`), base magic properties `0x142201d70` (rarity, identified, mod lists: §2a), u8, u8 |
| Quality | `0x14363acd8` | `0x1424aec50` | u8 |
| Charges | `0x143639a48` | `0x14249d4b0` | signed compact (`0x141e490e0`) |
| Stack | `0x143634b10` | `0x1423f4b30` | u16 stack size (§4a) |
| SkillGem | `0x143637298` | `0x14246eb00` | i32 experience (-1: one level over the cap), u32 +0x3c the variant, u8 +0x10 flags (bit 2: level locked), u16 +0x40 |
| Sockets | `0x1435df9a0` | `0x1421e2e20` | u8 = bit0/bit1 (special: nothing more) \| socket count << 2 \| link group count << 5; per socket u8 colour << 1 \| has gem, then the gem's item data **without** the length prefix; then a u8 per link group (its socket count). Colours: 1 red, 2 green, 3 blue (seen), 4 white (assumed) |
| Armour | `0x14363ad40` | `0x1424affc0` | u8 0..0x20 (stored at +0x28; 0x20 also sets +0x2a) |
| LocalStats, AttributeRequirements, Weapon, Usable, Flask, Quest, Shield, RenderItem | `0x14363ab88`, `0x1435dfb80` … | `ret` | nothing |

Minimal item the server sends: flags 0, Base with only the visual identity, Mods identified and normal with no mods.

**Base defences:** an `ArmourTypes.dat` row is keyed by `BaseItemTypesKey` (the BaseItemTypes row) and gives
`ArmourMin`/`ArmourMax` and `EvasionMin`/`EvasionMax`. The client interpolates between them with the item's base
percentile; our items send the Armour component's default `0`, so the server uses the minimum for the character's
armour and evasion. Only the base body armour, helmet, gloves and boots are counted for now; mods, quality, local
defence rolls and shields are not.

## 2a. Mods: rarity and mod lists

The Mods component's base magic properties are ObjectMagicProperties, read by `0x142201d70` (decompiled:
`Tools/re/out/omp.c`). Code: `Item.MagicProperties` (`InstanceItems.cs`), Mods.dat in the Gateway's `ModCatalog.cs`.

```
u16  flags   bits 0-2 rarity (0 normal, 1 magic, 2 rare, 3 unique), bit 3 identified,
             one bit per mod list (below), bit 7 custom name, bit 8 Words.dat name, bit 9 alternate quality
u8           (+0x7a, unknown; 0)
bit 9:  u8 AlternateQualityTypes row, u8
bit 4:  implicit list      bit 6: enchant list      bit 10: scourge list      bit 11: hidden list
identified only:
  bit 7: u32 length + UTF-16 name, else bit 8: u8 n × u32 Words.dat HASH32 (a rare name: prefix, suffix)
  bit 5: explicit list
```

A list is u8 count + mods. A mod (`0x142430380`): **u16 Mods.dat HASH16**, then one signed compact value
(`0x141e490e0`) per stat of the row. The client counts the stats itself (`0x142430710`, StatsKey1..8 up to the first
empty one; no Mods.dat row has a gap), so the count must match. An unknown hash throws "Deserialised a mod that we don't
recognise". After reading, `0x1421ff3c0` adds the "magic" / "rare" tag and gives each list to `0x1421ff8f0` with its
kind (implicit 1, explicit 2, enchant 3, scourge and hidden without one).

What the tooltip shows (live, 2026-10-20, an Iron Ring with `/mod <list> IncreasedLife1`): implicit under the
properties, enchant above the implicits, explicit below a separator, scourge as orange lines among the explicits
(Hellscape), hidden not at all. Magic items get their name from the affixes (Healthy Iron Ring); a rare item shows its
two Words.dat words above the base name (Corruption Gyre / Iron Ring; verified 2026-10-20 that the key is HASH32), or the
base name without them. Unidentified items show "Unidentified" and their implicits only.

New items roll their BaseItemTypes `Implicit_ModsKeys` (Driftwood Wand: `SpellDamageOnWeaponImplicitWand1`, 8-12%).
Saves keep rarity, identified and each mod by Mods.dat Id, list and values; an item saved before mods gets its type's
implicits again. Dev commands on the held item: `/mod [implicit|explicit|enchant|scourge|hidden] <Mods row, Id or part>
[values…]` (missing values rolled, the list defaults from GenerationType), `/mod clear [list]`, `/mods`,
`/rarity <normal|magic|rare|unique> [unid]`.

## 2b. Rolling magic and rare items

### Essence crafting (2026-10-25)

`EssenceCrafting.cs` reads the existing `tables/Essences.json`: BaseItemTypesKey identifies
the consumed currency, and each equipment-class ModsKey resolves the exact guaranteed
Mods.dat row. Quivers use Display_Quiver_ModsKey (there is no separate Quiver_ModsKey in
this schema); Rune Daggers use Dagger, Warstaves use Staff, and thrusting swords use
OneHandThrustingSword. All 105 craftable essences are included, including the four tier-8
essences and tier-0 Essence of Desolation. Remnant of Corruption has no equipment mods
and is deliberately not handled as item crafting currency.

Normal equipment becomes identified rare. Tiers 5–8 and Desolation can also reforge rare
equipment; magic, unique, corrupted, mirrored, incompatible classes/domains and the four
standard metamods are refused without consuming currency. The guaranteed mod bypasses
ordinary spawn weights and item-level requirements. Random affixes retain their normal
base/tag weights and use min(item level, ItemLevelRestriction), when a restriction is set;
the item's actual level stays unchanged. This is a cap on random properties, not a maximum
target item level. Desolation's cap is 15 in this client's table.

`ItemRolls.Roll` inserts the guaranteed affix before filling remaining prefix/suffix slots.
Family conflicts are excluded, the usual three-prefix/three-suffix limits apply, fractured
mods keep their values and positions, and a conflicting fracture refuses the craft. Existing
implicit/enchant mods, sockets, gems, links and quality remain. The random affix-count
distribution is the existing server distribution documented below, not a newly verified
official distribution. Conditional combat effects still depend on the server's implementation
of the corresponding stats; this change supplies their exact item modifiers.

UseItemOn dispatches essences through the existing 0x71 / Shift 0x72 currency path,
including currency in the Essence stash tab, item targets in stash/inventory, atomic saves,
one-unit consumption and 0xbf item replacement. Failures never consume a stack unit.
The extraction manifest explicitly checks Essences.json, and GameDataFiles warns if absent.

Tests exercise every essence across supported equipment classes at item levels 1 and 100,
raw-table class mappings, value ranges, random-mod caps, rarity/protection restrictions,
fractures/metamods and repeated use directly from the Essence tab. Real-client application
awaits the user's test after Gateway restart.

References: [PoEDB essences](https://poedb.tw/us/Essence),
[Desolation](https://poedb.tw/us/Essence_of_Desolation),
[PoE Wiki mechanics](https://www.poewiki.net/wiki/Essence). Prefer this client's extracted
rows over later website data when modifier values differ by game version.

### Ordinary random affixes

`ItemRolls.cs`. An affix can roll when its Mods.dat row has the base's BaseItemTypes **ModDomain** (1 items, flasks
their own), GenerationType 1 (prefix) or 2 (suffix), Level at most the item level, is not essence-only, and has a
weight above 0 for the item's **tags**: the first of its SpawnWeight tags the item has gives the weight, the first
matching GenerationWeight tag multiplies it by weight / 100. An item's tags are the `tag = "…"` lines of the Base
sections along its `.it` chain (`ot_components.py --items` writes them to `data/item-types.json` as `tags`)
plus the row's own TagsKeys: Driftwood Wand → default, weapon, onehand, wand, ranged, one_hand_weapon,
wand_can_roll_caster_modifiers.

Each affix is drawn by weight from all allowed mods (prefixes and suffixes compete), skipping mods that share a
ModFamily with one already chosen. Magic: 1-2 affixes, at most one prefix and one suffix. Rare: 4 (7/12), 5 (4/12) or
6 (1/12) affixes, at most three of each, and a name: one word from Words.dat list 1 (prefixes) and one from list 2
(suffixes, with a leading space), drawn by the same tag weights. Values are rolled uniformly in each stat's range.
These counts and chances are our own; the game's are not known.

Drops: equipment from a normal / magic / rare / unique monster is magic 25 / 35 / 40 / 30% and rare 5 / 10 / 25 / 50%
of the time, at the monster's level as item level, and drops **unidentified**. Dev: `/item <type> [count] magic|rare`
(at the character's level), `/roll <magic|rare> [item level]` on the held item.

**Identifying** (C→S 0x71, §4): right-click a Scroll of Wisdom, then click an unidentified item. The server marks it
identified, sends it again (its explicit list and name) and takes one scroll from the stack; an identified target
changes nothing. Nessa's **Identify Items** identifies the whole main inventory for free ([17](17-npcs.md) §3).

## 2c. Mods on the character

`ItemModStats.cs`. The items in the worn equipment rows (Inventories.dat 1-10: body armour, the first weapon set,
helmet, amulet, rings, gloves, boots, belt; not the second weapon set or the flasks) count, with every mod list. The
client does not let the player equip an unidentified item, so only identified items reach these rows. A stat that
Stats.dat marks **IsLocal** belongs to its item; every other stat is the character's: it is added to the allocated
passives' stats (`CharacterStatSources`), so it reaches the pools (`PassiveBonus` → `CharacterStats.For`: life, mana,
attributes, resistances, block, suppression, speeds, crit, accuracy …) and the stats the client shows (0x1b3), the same
way a passive's would. They are sent again when equipment changes (`SendEquipment`).

Local mods on the main hand (row 2, else 14) change the weapon profile: `local_critical_strike_chance_+%` scales the
WeaponTypes base critical chance, `local_attack_speed_+%` multiplies the attack rate (like a more, before the global
increases) and `local_accuracy_rating` adds flat accuracy. Weapon damage, armour, evasion and energy shield of items are
not modelled yet, so their local mods change nothing. Verified live 2026-10-20: +20 maximum Life, +30% fire resistance
and +25 strength on a ring, a local attack speed mod on the wand, and spell damage on the wand raising Fireball's
damage (11-skills.md §9).

## 2d. Requirements and inactive items

**Attribute requirements are computed by the client from the item type and the mods; the level requirement is sent.**

- **Attributes** (`0x1424a6d90`, per attribute, read by the equip check `0x1402ce5f0`): the type's
  AttributeRequirements value plus the item's flat requirement stats (`local_strength_and_intelligence_requirement_+` …),
  × (100 + `local_attribute_requirements_+%` + the attribute's own %) / 100, at least 0;
  `local_no_attribute_requirements` and `local_unique_tabula_rasa_no_requirement_or_energy_shield` make it 0. The check
  compares it × (100 + the player's `global_item_attribute_requirements_+%`) / 100 with
  `virtual_*_for_requirements`, and the client refuses the move itself.
- **Level** (Mods component +0x2bc). **This one is sent:** the Mods reader `0x1421df6e0` stores the byte it receives,
  and the client only recomputes it (`0x1421e03d0`) when an item's mods change on its side (crafting paths
  `0x1421e0600`, `0x1421e00d0` …). The tooltip shows that byte, and the client's use check `0x1421eca50` refuses an
  item when it is above the player's level (Player +0x1ac, derived from the experience). With 0 (as we used to send)
  nothing shows and nothing is checked. The client's formula, which the Gateway uses (`ItemModStats.LevelRequirement`,
  sent through `Item.LevelRequirementOf`): 0 for types with the flag at type data +0xcb; else
  `local_level_requirement_override` if non-zero; else max(the type's level, Mods.dat Level × 4 / 5 for every mod in all
  five lists) + `local_level_requirement_+`, at least 0. Gems send 0: their requirement comes from the SkillGem
  component (`0x14246fff0` for the level, `0x14246fc40` per attribute; [19](19-gems.md) §2). The server also refuses
  0x2a / 0x4a into equipment rows below it (`LevelTooLow`).

**Inactive items** (`ItemModStats.Inactive`): a worn item whose requirements stop being met (an attribute item taken
off, a lower level) stays on but does nothing: no mod stats, no local weapon mods, no gem skills (their skill ids are
kept). After each change, items that fall short are switched off one at a time, counting the attributes of every
active item including their own, until all active items are satisfied; an inactive item comes back when the character
and the other active items meet its requirement without its own attributes.

A gem socketed in a worn item whose level or attribute requirement the character no longer meets makes that item
inactive as well (`CharacterStatSources`: the gem's requirements are checked per socket with `GemRequirementsUnmet`),
so the item counts for nothing and the Actor skill list leaves its gems out: with no skill, there is nothing for the
client to cast ([19](19-gems.md) §2 "Requirements"). An inactive main-hand weapon is not there for the attack or for
the weapon shape either: the default attack falls back to the class's unarmed hit (`WeaponFor`) and the shape passives
read as unarmed (`WeaponProfileFor`).

Open: the client still draws an inactive item on the character (a red wand stays in hand), and the server leaves it
where it is. The state is reachable in the real game — a ring's +intelligence makes a gem's requirement met, the gem
goes in, the ring comes off — the server now reproduces it (attributes included, checked live 2026-10-23 with
`/set_stat intelligence` above and below a gem's requirement: the client reds the gem and the wand's mods stop
counting, [19](19-gems.md) §2 "Requirements"). What the official client draws then is still to check there (the gem
red, the wand still in hand). Moving the item out of its row is the only way to take its visual away (Roadmap "Gems 1").

**Stale stats:** S→C 0x1b3 only sets the stats it lists, so every full player stats send also sends 0 for each stat
sent before that is gone now (`PlayerStatsWithCleared`).

## 3. Inventories with items (S→C 0xc0) and changes (S→C 0xbf)

0xc0 ([05](05-inventories-and-ui.md) §2) carries the items after the inventory header: compact count, then per item
(`0x1421f5680`) **u32 id, u8 x, u8 y, item**. Ids identify items within an inventory (the client keeps a map and drops
duplicates); the Gateway gives every item a character-wide id and keeps it when the item moves.

The 0xc0 **flags** byte: bit 0 (+0x148) = Inventories.dat column 3, a single-item slot drawn as one box (equipment, the
cursor; without it an equipment slot is a grid of cells); bit 1 (+0x149) = column 4, the character's own slots
(equipment, flask bar). With bit 1 the client checks what may go in: the slot's item classes (a wand is red over the body
armour slot) and the attribute requirements (§6).

**S→C 0xbf** changes one inventory (dispatcher case 0x34, class `0x1435dbd28`, write `0x1421a37f0` → `0x1425079a0`,
handler `0x141d62550` → `0x1421f5160`):

```
u8   manager (0 = the player's)
u32  inventory id (unknown → fatal "Got an inventory set change for an inventory that isn't in the set")
u32  revision (→ inventory +0x1e8)
u32  removed count (0xffffffff = remove everything) + that many u32 item ids
u32  added count + (u32 id, u8 x, u8 y, item)
u8   1 → 4 × u32 follow (the 0xc0 flag-bit-4 block)
```

Removal runs before addition, so "remove id N, add id N" replaces an item in place (sockets). The other inventory
messages: 0xc1 removes an inventory ("…inventory set remove…", class `0x1435c0898`: u8 manager, u32 id), 0xc2 sets
+0x160/+0x161 ("…permissions change…", class `0x1435dc660`: u8, u32, u8, u8).

## 4. Moving items (C→S)

All senders sit together at `0x14215f300`… and queue through `0x14216e940`.

| Id | Bytes | Class / write | Meaning | Server answer |
|---|---|---|---|---|
| 0x28 | 13 | `0x1435bf5f0` / `0x141fa0d00` | click on an item: pick it up. u32 inventory id **with HNC side-channel bits 21-29** (mask `0xc01fffff`, reader `0x141fa0ea0`), u32 item id, u8 (3), u8, u8 | 0xbf remove from the inventory, 0xbf add to the cursor (row 12) at (0, 0) |
| 0x2a | 18 | `0x1435bf710` / `0x141f8feb0` | put the held item down: u32 inventory id, u32 x, u32 y (0, 0 for equipment), u8 Shift (1: one of a held stack, §4a), u8 (3), u8, u8. Sent for a click while holding on a cell; a click on an item sends 0x4a | 0xbf target (remove the displaced item, add the held one), 0xbf cursor (remove the held one, add the displaced one) |
| 0x30 | 16 | `0x1435c0838` / `0x141f90650` | put the held gem into a socket: u32 inventory id, u32 item id, u32 socket index, u8, u8 | 0xbf cursor, 0xbf replacing the item |
| 0x2f | 17 | — | take a socketed gem onto the empty cursor: u32 inventory id, u32 item id, u32 socket index, 3 bytes | 0xbf replacing the item, 0xbf cursor |
| 0x31 | 14 | not traced | the **"+"** on a socketed gem ([19](19-gems.md) §4): u32 inventory id, u32 item id, u32 socket index | 0xbf replacing the item with the experience at the next level's threshold (the client derives the level), the 0x20 Actor update |
| 0xd7 | 15 | not traced | a right-click on a socketed gem toggles its **level lock** ([19](19-gems.md) §4): u32 inventory id, u32 item id, u32 socket index, u8 locked | 0xbf replacing the item with SkillGem +0x10 bit 2 set or cleared |
| 0x29 | 2 | `0x14316c930` (id only), sender `0x140117b80` | drop the held item into the world | 0xbf cursor, then a WorldItem (§5) |
| 0x16 | 5 + n | `0x1435bff00` / `0x141f91480` | a Commands.dat command: u8 command (row + 1), u16 byte count + UTF-8 argument. The town popup's Destroy sends **9** (row 8 DestroyCursorItem, `/destroy`) | 0xbf cursor (the item is gone) |
| 0x2d | 8 | `0x1435c02b8` / `0x141f90340`, senders `0x14215f520` / `0x14215f570` | inventory request, not mapped: u8, u32, u8 flag (0 / 1 by sender) | not handled |
| 0x58 | 10 | `0x1435bf8a8` / `0x141f926a0` | use an item that needs no target (right-click): u32 inventory id, u32 item id, both big-endian | an Orb of Regret loses one item and adds one refund point (0xbf the stack, 0x33 the tree) |
| 0x71 | 18 | not traced | use an item on an item (right-click it, then click the target): u32 inventory id, u32 item id of the used item, then of the target, all big-endian (live 2026-10-20); the first inventory id is a stash tab (e.g. 2053) when the currency is right-clicked in the open stash (live 2026-10-23): one is taken off that tab's stack; the target inventory id can be a stash tab too, e.g. the currency tab's crafting slot (live 2026-10-23: currency in its slot used on the item in the crafting slot, both inventory ids 2053). `ItemMoves.UseAcross`: the currency and the target saved together, one 0xbf per place changed (one for both when they share it) | a Scroll of Wisdom identifies the target (§2b); a Chromatic Orb gives every socket a new colour (`CurrencyEffects.Chromatic`: red / green / blue weighted by the base's strength / dexterity / intelligence requirement + 12, our own weights; never white, gems and links stay, always different from before; live 2026-10-21 the client sent it for boots and a helmet): 0xbf with the stack and the target |
| 0x72 | 23 | not traced | the held currency used again with **Shift** (the client keeps it on the cursor while the inventory has more, live 2026-10-21): u32 its BaseItemTypes HASH32 (the type, not an item), u32 inventory id and u32 item id of the target, all big-endian, then u32 0, u8 1, u8 0, u8 7, u16 2 (unknown) | as 0x71, with a stack of that type from the inventories (the main inventory first), then from the stash tabs |
| 0x4a | 14 | `0x1435bf6d0` / `0x141fa08d0`, sender `0x14215f5c0` | the held item clicked **on an item** (§4a): u32 inventory id with HNC bits from bit 20 (mask `0xc00fffff`), u32 the clicked item's id, u8 flags (bit 2 = Shift; HNC bits 3-7), u8 (3), u16 (low byte first, differs every click, unknown) | a merge (0xbf the stack, 0xbf cursor), else a swap as 0x2a |
| 0x7c | 17 | `0x1435bfdd8` / `0x141f90870`, sender `0x142165640` | split a stack (§4a): u32 inventory id, u32 item id, u32 amount, u8 (3), u16 (unknown); no HNC bits | 0xbf the stack (same id, smaller), 0xbf cursor (a new item with the amount) |

Dropping: `0x140117b80` sends 0x29 at once outside town. In town (and for quest items) it opens the Keep / Destroy popup
instead and sends nothing unless Destroy is chosen (0x16 command 9).

Rules the Gateway applies (`PlayerInventories.Place`): grids need the item inside the grid. When exactly one item is in
the way it is swapped onto the cursor; two or more refuse. Single-item slots take the item at (0, 0) when its class fits
(`Accepts`: the ItemClasses Id per equipment row; flasks only in the flask bar). A one-handed weapon put into an empty
main hand's off hand goes to the main hand. Taking the main hand off moves the off hand's one-hander there. The two
hands of a weapon set must fit together (`PlayerInventories.HandConflict`, 2026-10-23): a bow goes with an empty off hand
or a quiver, another two-handed weapon only with an empty off hand; a quiver goes with a bow or an empty main hand, never
with a one-handed weapon; a shield or a one-handed weapon not with a two-handed weapon. A drop that breaks this is
refused (the item stays held). Before, only the class per row was checked, so a bow could go next to a shield or a
sword and a one-handed weapon next to a quiver.

### Client quiver eligibility

`FUN_1421eca50` checks that a quiver (ItemClasses row + 1 = 21) sees player stat 3,
`main_hand_weapon_type`, equal to **9**. This is a separate native enum: Bow's ItemClasses row + 1 is 14,
and its model stance is 8. Sending the visual alone leaves inventory quivers red and prevents placement
requests. `FinalPlayerStats` supplies 9 for an active, usable bow in row 2 (a value /set_stat set wins); other
weapons send nothing, and taking the bow off sends 0 through the cleared-stat list (`PlayerStatsWithCleared`).
Weapon swaps physically exchange rows 2/3 with 14/15. Other weapon-stat enum values remain to be mapped.
Automated coverage passes; in-client confirmation pending.

### Bow model records

A bow is not held in the main hand. The live server (Frida 2026-10-25) sends the bow's art in model slot 1 (left
hand; type `0x68`, stance `0xff`), an arrow in slot 0 (right hand; type 14 `Bows`, stance 5 `Bow`, art
`DefaultArrow` `0xB2F6`), and the quiver on the back in slot 7 (type `0x68`, stance `0xff`, art `DefaultQuiver`
`0x328B`). `BowSetup` reproduces these entries; `ItemCatalog.Visual` maps the ItemVisualIdentity ids to their
HASH16. Without them the bow appeared in the wrong hand and the arrow/quiver were missing or misplaced.

## 4a. Stacks

Stackable types have a Stack component (its u16 is the stack size) and a CurrencyItems.dat **StackSize** above 1
(`ItemCatalog.BaseType.MaxStack`: Orb of Transmutation 40, Scroll of Wisdom 40 …; `CurrencyTab_StackSize` is the
currency stash tab's limit). The client draws the size and the tooltip's "Stack Size: n/max" from the item and its own
tables; it changes nothing itself, the server's 0xbf does.

- **Merge** (C→S 0x4a, the held stack clicked on a stack; live 2026-10-19 the client sends 0x4a, not 0x2a, for a
  click on an item): a stack of the same type with room fills up to the max and the rest stays held. The answer is a
  0xbf on the target (remove and add the stack's id, larger) and one on the cursor (remove the held id, add it again
  smaller, or not at all when all of it went). A full stack, or another type, is swapped as 0x2a on that item's cell
  (`PlayerInventories.MergeOnto` → `Place`). Stash tabs do the same (`AccountStash.Merge`, [18](18-stash.md)).
- **One of the held stack** (Shift + click while holding): 0x2a with its first flag byte 1 puts one into an empty cell
  as a new item; 0x4a with flag bit 2 adds one to a stack of its type (refused on anything else). The rest stays held.
- **Split** (Shift + click on a stack, then the split window's amount): C→S 0x7c. The stack shrinks in place and the
  split-off part is held as a new item (`PlayerInventories.Split`). Refused with something held, an amount of 0 or the
  whole stack. On a stash tab the tab's stack shrinks (`AccountStash.Shrink`, written first) and the held part gets a
  character item id.
- **Full stacks become their item:** CurrencyItems `FullStack_BaseItemTypesKey` (28 types: 5 Scroll Fragments → a
  Scroll of Wisdom, 20 shards → their orb, 100 splinters → their Breachstone or Emblem …). A stack that reaches its max
  by a merge or an add turns into one of that item in the same cell, with the same id (`PlayerInventories.Completed`).
- **Adding** (`PlayerInventories.AddStacked`): items picked up from the ground into the main inventory, `/item <type>
  <count>` (count = how many, up to a full main inventory: 60 stacks; the rest is reported as no room) and the sell window's reward go onto the stacks of their type first
  (fullest first), then as new stacks of at most the max in free cells. A ground stack is picked up only when all of it
  fits (`Fits`).

The 0x4a writer `0x141fa08d0` packs the HNC side-channel bits into the inventory id from bit 20 and into the flags byte
from bit 3, like 0x28. The framing table had 0x4a at 12 bytes (it is 14) and did not know 0x7c; both would have split
the next message. Verified live on 2026-10-19: merge, split (10 off 35), Shift one into a cell and onto a stack, 5
Scroll Fragments → a Scroll of Wisdom.

## 5. Items on the ground

`Metadata/MiscellaneousObjects/WorldItem` (`WorldItemObject`): Positioned, BaseEvents (nothing), Animated (u8 0),
WorldItem, InteractionAction (u8 0). WorldItem `0x141ea10a0`: u32 a (non-zero → one more u32), u8 (+0x50), u32
ItemVisualIdentity HASH16 override (0 = the item's), then the item data without its length prefix. Created with 0x2b9 +
wake zone 0x1a9 at the player's estimated cell, removed with 0x2bb.

Clicking it sends C→S 0x24 on the object (skill 0x0266, Interaction). **Flag 0x0002 = the inventory panel is open**
(0x040a vs 0x0408): the item goes to the cursor instead of the first free cell. The Gateway echoes the interaction at
once (0x1ab aimed at the object, from the player's current cell) and picks the item up when its walk estimate is within
**22 cells**: WorldItem.ot `InteractionAction interaction_distance_override = 20` is in grid cells, plus both radii
(object_size 2). Measured with Frida (Positioned grid position at `+0x294/+0x298`): the client stopped 20.9 cells from
the item, on the first step inside the range.

Dying monsters drop items as WorldItems around the corpse (`DropLoot`, [15](15-progression.md) §3).

**Ground items belong to the instance** (`InstanceStore`, `InstanceGround`), as in the real game. Dropped items and
loot are kept per area instance, by Auth's instance id and its creation time (`Created` in the `resolve` answer: Auth's
ids start at 1 again after a restart, so an old instance's items never reach a new one). Re-entering the instance sends
them where they lay (`SendGroundItems`), after a logout, a disconnect or a client crash. Their object ids come from
their own range, **0x100000** up, the same for every connection; the client accepts them (live 2026-10-18). A pickup
takes the item out of the store first, so two connections can never both get it. The store is in the Gateway's memory:
it is wiped like Auth's instance, once no connection has been in it for `InstanceRegistry.Lifetime` (6 minutes), and
lost when the Gateway restarts.

### Jeweller's Orb (initial local implementation, 2026-10-21)

`CurrencyRerollSocketNumbers` uses the existing 0x71 handler and the shared Shift 0x72 path. One successful use
spends one orb and replaces the target through 0xbf, rerolling socket count, RGB colours and link groups. Equipped
targets also refresh equipment. The existing character save stores the result. Verified live with the 3.29.3b client: repeated rolls on gloves, boots, a helmet, weapons and body armour; inventory updates and currency consumption are visible in the Gateway log.

Requested local counts are **1, 2, 3, 4, 6** (five excluded). Class caps: body armour, bows and two-handed melee
weapons 6; helmets/gloves/boots 4; one-handed weapons/shields 3. Item-level caps: below 2 -> 2, 2-24 -> 3,
25-49 -> 4, 50+ -> 6; inventory dimensions also cap the count. Other classes and missing Sockets components are
refused. Items already at the base's capacity (class and dimensions, not the item-level roll cap) or containing gems
are refused without consuming currency; remove gems first. Live failure 2026-10-21: level-1 BootsStr1/GlovesStr1
with two empty sockets were incorrectly refused at the level cap. They can now reroll between one and two sockets;
the level cap limits the result, not eligibility. A regression test covers repeated use on both bases.
The new count differs from the old count. Colours and links are independently rerolled and may repeat.

This is deliberately an initial local rule set, not exact 3.29 socket crafting: weights for counts 1/2/3/4/6 are
50/120/100/30/1 (filtered by eligibility and old count), RGB uses attribute requirement + 12, each adjacent link has
a 50% chance. Quality bonuses, white-socket 3.29 rules, special unique/mod socket overrides and corruption are not
modelled. No new extracted data is needed.

C→S 0x72 is 23 bytes: u16 id, u32 currency BaseItemTypes HASH32, u32 target inventory, u32 target item,
then nine currently uninterpreted bytes. All integer fields above are big-endian. The Gateway resolves a stack
of that currency (main inventory first) and reuses the 0x71 handler.

### Orb of Alchemy (2026-10-21, verified live)

`CurrencyUpgradeToRare` goes through the same 0x71/0x72 handler. `CurrencyEffects.Alchemy` takes only a normal item with
a Mods component and makes it rare and identified: implicits kept, 4-6 explicit affixes rolled by `ItemRolls.Roll`
(weighted by Mods.dat spawn weights, each mod's values rolled within its tier), and a Words.dat rare name. Anything
else is refused and nothing is spent.

### Chaos Orb (2026-10-21, verified live)

`CurrencyRerollRare`, same handler (0x71, and 0x72 with Shift for repeated use). `CurrencyEffects.Chaos` takes only a
rare item with a Mods component: every explicit affix is replaced by a fresh `ItemRolls.Roll` (4-6 affixes, new tiers
and values), a new rare name, implicits kept; the item ends identified. Anything else is refused and nothing is spent.

### Divine Orb (2026-10-21, verified live)

`CurrencyModValues`, same handler (0x71, 0x72 with Shift; one orb spent per use). `ItemRolls.Reforge` rerolls only
the values of each explicit mod within its own tier; the mods, their order, implicits, rarity and name stay. A new tier
would be a different mod, and the client reorders the tooltip by mod, so the lines would jump. An item without explicit
mods is refused.

### Blessed Orb (2026-10-23, not verified live yet)

`CurrencyRerollImplicit`, same handler (0x71, 0x72 with Shift; one orb spent per use). Before, the Gateway logged it as
"not handled" (live 2026-10-23, item 221). `ItemRolls.Bless` rerolls only the values of each implicit mod within its own
tier, as the Divine Orb does for explicits; explicit, enchant and other mods, rarity and name stay. An item without an
implicit mod is refused (nothing spent), and like every currency here but the Scroll of Wisdom it does nothing to a
corrupted or mirrored item.

### Orb of Scouring (2026-10-21, verified live)

`CurrencyConvertToNormal`, same handler (0x71, 0x72 with Shift). `CurrencyEffects.Scouring` makes a magic, rare or
unique item normal: explicit mods and the rare name are removed, implicits and enchants stay. A normal item is refused
and nothing is spent.

### Orb of Transmutation (2026-10-21, verified live)

`CurrencyUpgradeToMagic`, same handler (0x71, 0x72 with Shift). `CurrencyEffects.Transmutation` makes a normal item with
a Mods component magic and identified: 1-2 affixes (at most one prefix and one suffix) rolled by `ItemRolls.Roll`, each
tier drawn by weight; implicits kept. Anything else is refused and nothing is spent.

### Orb of Alteration (2026-10-21, verified live)

`CurrencyRerollMagic`, same handler (0x71, 0x72 with Shift). `CurrencyEffects.Alteration` rerolls a magic item's
affixes as the official orb: 1-2, at most one prefix and one suffix, each tier drawn by weight (`ItemRolls.Roll`).
Anything but a magic item with mods is refused, nothing spent.

### Orb of Augmentation (2026-10-21, verified live)

`CurrencyAddModToMagic`, same handler (0x71, 0x72 with Shift). `CurrencyEffects.Augmentation` adds one affix to a magic
item (`ItemRolls.AddAffix`): drawn by weight from the base's pool, no family already on it, at most one prefix and one
suffix (the magic limit, as the Orb of Alteration). Old affixes and rarity stay. A full item, or anything
but a magic item, is refused and nothing is spent.

### Regal Orb (2026-10-21, verified live)

`CurrencyUpgradeMagicToRare`, same handler (0x71, 0x72 with Shift). `CurrencyEffects.Regal` makes a magic item rare:
its affixes stay, one more is added (`ItemRolls.AddAffix`, at most three prefixes and three suffixes) and it gets a rare
name. Anything but a magic item, or one where no affix fits, is refused and nothing is spent.

### Exalted Orb (2026-10-21, verified live)

`CurrencyAddModToRare`, same handler (0x71, 0x72 with Shift). `CurrencyEffects.Exalted` adds one affix to a rare item
(`ItemRolls.AddAffix`, at most three prefixes and three suffixes); its affixes and name stay. Anything but a rare item,
or a full one, is refused and nothing is spent.

### Orb of Annulment (2026-10-21, verified live)

`CurrencyRemoveMod`, same handler (0x71, 0x72 with Shift). `CurrencyEffects.Annulment` removes one random explicit
affix from a magic or rare item; implicits, the other affixes, rarity and name stay. Anything else, or an item without
explicit affixes, is refused and nothing is spent.

### Armourer's Scrap (2026-10-21, verified live)

`CurrencyArmourQuality`, same handler (0x71, 0x72 with Shift). `CurrencyEffects.ArmourerScrap` adds 1% quality (the
Quality component's u8) to an item with an Armour component, any rarity, up to 20%; the client shows the raised
defences from the quality. On this server the Orb of Scouring also sets an armour piece's quality back to 0 (and so
accepts a normal armour piece with quality). Other items, or 20%, are refused and nothing is spent.

### Blacksmith's Whetstone (2026-10-21, verified live)

`CurrencyWeaponQuality`, the same as Armourer's Scrap for items with a Weapon component (`CurrencyEffects.Whetstone`):
+1% quality, any rarity, up to 20%, Shift included; the Orb of Scouring sets a weapon's quality back to 0 as well.

### Orb of Chance (2026-10-21, verified live)

`CurrencyUpgradeRandomly`, same handler (0x71, 0x72 with Shift). A server rule, not the official one (which can also
make uniques): `CurrencyEffects.Chance` turns a normal weapon or armour piece (Weapon or Armour component) rare with a
30% chance (4-6 affixes, rare name) and magic otherwise (2-4 affixes, at most two prefixes and two suffixes, a server
rule above the normal magic limit); the item ends identified. Anything else is refused and nothing is spent.

### Glassblower's Bauble (2026-10-21, verified live)

`CurrencyFlaskQuality`, same handler (0x71, 0x72 with Shift). `CurrencyEffects.Glassblower`: +1% quality on an item with
a Flask component, any rarity, up to 20%. The Orb of Scouring makes a flask normal, removes its explicit mods and sets its
quality back to 0, as for armour and weapons.

### Gemcutter's Prism (2026-10-22, being tested)

`CurrencyGemQuality`, same handler (0x71, 0x72 with Shift). `CurrencyEffects.Gemcutter`: +1% quality on an item with a
SkillGem component (active or support) that is not corrupted, up to 20%. Anything else is refused and nothing is spent.

### Fracturing Orb (2026-10-21, verified live)

`CurrencyFractureRare`, same handler (0x71, 0x72 with Shift). As the official orb: a rare item with at least four explicit
mods, not corrupted and not fractured yet, has one random explicit mod fractured (`CurrencyEffects.Fracture`). The mod
moves to the front of the explicits and `Item.FracturedCount` (saved) counts it. Every other currency keeps fractured
mods: Chaos, Vaal and the other rerolls roll around them (they count towards the affix limits), Annulment and Divine
skip them, Augmentation / Exalted insert after them, and Scouring removes everything else (the item stays magic so the
mod still shows).

The client is told through the magic properties' u8 `+0x7a` (the byte right after their u16 flags, sent 0 before):
the fractured explicit count, the first explicits being the fractured ones. Verified live: those mods show fractured.
Tried first and wrong: the Mods component's flag bit 4 with a u8 after the flags shifted the item (it showed magic).

### Mirror of Kalandra (2026-10-21, verified live)

`CurrencyDuplicate`, same 0x71 / 0x72 messages (`MirrorItem`). As the official mirror: an item that is neither
corrupted nor mirrored is copied with every property (mods and values, fractured mods, quality, sockets, links,
socketed gems, name); the copy is **mirrored** and goes onto the cursor (S→C 0xbf on the cursor inventory) for the
player to place, the original stays unchanged. A held item,
a corrupted or mirrored target, or an item without mods is refused and nothing is spent. A mirrored item takes no
currency but a Scroll of Wisdom.

`Item.Mirrored` (saved) is sent as the Mods component's u16 flags bit 1 (`+0x2df`, a plain bool with no data after
it): the client's crafting check refuses an item with it (`0x141d68f9b`); the tooltip shows
"Mirrored".

### Vaal Orb and corruption (2026-10-21, verified live)

**Corrupted** is bit 0 of the Base component's u8 `+0xc7` (the second u8 after the visual identity): the client's
crafting checks read it ("input_is_corrupted" at `0x141d68e64`, `test byte ptr [base+0xc7], 1`). `Item.Corrupted` writes
it and is saved with the item. A corrupted item takes no currency but a Scroll of Wisdom (there is no corrupted
currency yet), and nothing removes corruption.

`CurrencyCorrupt`, same handler (0x71, 0x72 with Shift). A server rule, not the official one: `CurrencyEffects.Vaal`
takes a weapon, armour piece or jewellery (ring, amulet, belt) that is not corrupted or unique, and always corrupts it.
A normal or magic item becomes rare with 4-6 new affixes and a rare name. A rare item has 1, 2, 3 or 4 random affixes
replaced (20% each), 5 (10%) or all of them rerolled (10%: 4-6, new name) (`ItemRolls.ReplaceAffixes`: the replaced
ones come back through `AddAffix`, at most three of each kind, so the count stays).

### Orb of Fusing (2026-10-21, verified live)

`CurrencyRerollSocketLinks`, HASH32 `0xC71BF58D`, uses the same 0x71/0x72 currency handler and 0xbf inventory update.
Captured normal use on item 229: `0071000000010000005B00000001000000E5` (currency stack 91).
Captured Shift use on that item: `0072C71BF58D00000001000000E5000000000100060000`.
The nine trailing bytes of 0x72 vary by currency; do not validate them against the earlier Chromatic capture.
Tests parse both, split consecutive messages and keep a partial 0x72 until all 23 bytes arrive.

`CurrencyEffects.Fusing` replaces only link groups. Socket count, colours (including white), gems and their experience,
quality, rarity and mods stay unchanged. At least two sockets and a supported equipment class are required; the
item-level roll cap does not apply to existing sockets. Fully linked 5/6-socket items are protected; 2-4 sockets
may be unlinked. A refused use spends nothing. Normal and Shift uses consume one orb, update the target, refresh
equipped items and persist through the existing inventory path. The new link layout differs from the old one;
the number of links may repeat with different positions. This does not implement support-gem effects.

Initial **approximate** probability model: enumerate all partitions of the socket chain, assign total weights
500/3500/4500/1400/90/10 to largest groups 1/2/3/4/5/6, divide each weight among that group's layouts, then exclude
the current layout. Quality multiplies a layout's weight by `(1 + quality / 100)^(largestGroup - 1)` and is not
consumed. This is a local approximation, not an official probability table; exact chances, corruption and special
item overrides remain open. No extracted-data changes are required.

## 6. Equipment on the character: visuals, attributes, gem skills

**What the model wears** is the player's Inventories component (index 8, reader `0x141e1ee30`, also as a 0x2ba update):
u8 n × (u8 slot, record `0x1424f44e0`), then u8 m × u16. The slot is Inventories.dat column 7 (Weapon1 0, Offhand1 1,
BodyArmour1 2, Helm1 4, Gloves1 5, Boots1 6, Ring1 8, Ring2 9, Belt1 10, Amulet1 11). The record: u8 type (**0 = empty**,
nothing more; else the item's ItemClasses row + 1, the weapon type skills check, 11-skills.md §10a), two (u16 ItemVisualIdentity HASH16, u32 TieredMicrotransactions key, u8) references (`0x1424f3fc0`,
`0x1424f4250`), u8 ItemStances row (`0x1424f4b10`; the row's ItemStance, else its class's: Wand = 2), u16
ItemVisualIdentity HASH16 (`0x1424f4730`), u8. The Gateway sends every visible slot, empty ones with tag 0: a slot left
out keeps what the model showed before (the unequipped wand stayed in the hand).
The player object's own list does not set how a two-handed weapon is held: after every area load it was held one-handed
(live 2026-10-22). The Gateway sends the same list again as a 0x2ba update right after the player and the skill bar, as
after an equip (verified live 2026-10-22).

**Weapon swap (verified with a live trace, 2026-10-22).** The client sends C→S 0xdd (3 bytes, `0x1421690f0`): u8 the
set it wants (1 the second, sent as "the current set is the first"), then waits (session +0xcfa4 = 1): no other 0xdd
and no skill input (`FUN_14011d660`, `FUN_14215f700`) until the server answers. **S→C 0xde** (u8 flags, dispatcher case
0x42 at `0x14214eb65`) answers: bit 0 is the active set (session +0xcd31, which the inventory panel shows,
`FUN_1408f52e0`; a change tells the listeners), bit 1 keeps the wait (+0xcfa4 = 2), else it ends (0). S→C 0x21 carries
the set on entry as its flag bit 0. Watch out: in the dispatcher `0x14214a190` r13 is the session object + 8, so its
"[r13+0xcd29]" is the session's +0xcd31 and "[r13+0xcf9c]" is +0xcfa4 (a Frida trace of both places showed it; reading
the offsets as they stand had suggested only a reconnect could swap). The panel always shows the hands' rows (Weapon1 /
Offhand1, rows 2 and 3) as the active set and rows 14 / 15 as the other one, under the I / II tab the flag selects
(live 2026-10-22: with only the flag switched, the second tab showed the first set's weapons). So the Gateway answers
0xdd by trading the items of rows 2 ↔ 14 and 3 ↔ 15 (`PlayerInventories.SwapWeaponSets`, saved, ids and cells kept), a
0xbf with the new contents of each of the four, then 0xde with the set and the equipment update.

**Attribute requirements** are checked (`0x1402ce5f0`) against `virtual_strength_for_requirements`,
`virtual_dexterity_for_requirements` and `virtual_intelligence_for_requirements` (keys 0x3d1b-0x3d1d), cached on the Player
component by `0x141e80340`; `global_no_attribute_requirements` (0x2ee3) skips the check and
`global_item_attribute_requirements_+%` (0x51a) scales it. The character panel shows `strength` / `dexterity` /
`intelligence` (575 / 581 / 578), which a UI cache (`0x141e802d0`) reads when it is built. With only those three the
panel said 32 Int while "Requires 14 Int" stayed red. The Gateway sends all six from Characters.dat (BaseStrength …), in
the creation blob and again as 0x1b3 after entering. A gem's attribute requirement is not the item curve: it is the
client's own formula from the SkillGem component (`0x14246fc40`, [19](19-gems.md) §2), checked against the same three
stats (the socket check at `0x1402ce738`).

**The class byte** of the Player component is Characters.dat **IntegerId** (Marauder 1, Witch 3), not the row: with row
1 the panel showed Marauder.

**Gem skills:** each active gem socketed in an equipped item grants its skill (SkillGems → the gem's GemEffects variant →
GrantedEffect at the gem's level, `ItemCatalog.GemEffects`; [19](19-gems.md) §3), sent in the Actor skill list after the
default attack. **Support
gems must not be granted:** a support's effect in the list crashed the client in the skill reader (`0x141ee795a`, via
`0x142197b59` → `0x141eea955`). Supports and links do nothing yet. Gem skills carry their level's stats and can be cast
([11](11-skills.md) §9–10).

## 7. Start kit, saves and dev command

A new character gets Characters.dat StartSkillGem and StartSupportGem in the main inventory, and in the flask bar two
Small Life Flasks (cells 0 and 1: flask keys 1 and 2) and a Small Mana Flask (cell 4: key 5). Its StartWeapons[0], with two linked sockets in the start gems' colours, is worn
in Weapon1. With `Game_START_WEAPON=ground` it lies on the ground instead, as on the real game's Twilight Strand: `PlayerInventories.StartWeapon` keeps it (in the save as
`StartWeapon`) until it is picked up, and on entering an instance outside towns the Gateway puts it once per character, weapon
and instance (`InstanceState.PlaceStartWeapon`) on the first walkable cell 6 to 20 cells from the spawn
(`AreaLayout.Near`), as a WorldItem like loot. Picking it up (C→S 0x24) clears it in the same save as the item's new cell,
so a crash never keeps it both waiting and in the inventory. A new instance or a Gateway restart before the pickup puts
it there again; so does a new character with a deleted one's name in the same instance (its weapon is another item).
`Game_START_ITEMS=0` starts empty.

The kit is built once, when the character has no save, and saved at once. Without `data/tables` (`Characters.json`,
`SkillGems.json`) and `data/item-types.json` it is empty, the Gateway logs `start kit is empty`, and the character keeps
the empty kit: remove its row from the `characters` table of `data/saves.db` (with the Gateway stopped) to give it the kit
again.

**Saves.** Every inventory change writes the character's items to its row in `data/saves.db` (`CharacterSaves`,
git-ignored): per Inventories.dat row the item ids, cells and items by BaseItemTypes HASH32 with level, quality, charges,
stack size, sockets (with their gems), links and gem experience. A saved character loads from it instead of the start
kit, so items and equipment survive Gateway restarts and logins (live 2026-10-18). The save also holds the skill bar
([11](11-skills.md) §11). How a change is written (SQLite transactions, moves between the character and the stash in
one transaction, what happens when a save fails, saves that can't be read or hold unknown items) is in
[saves](../saves.md).

Each move is one change and one save, so a crash at any moment leaves an item in exactly one inventory: picking up
(`PlayerInventories.PickUp`, C→S 0x28: off its row and onto the cursor), placing or swapping (`Place`, 0x2a), socketing
and unsocketing (0x30, 0x2f) each change everything under one lock and then write the file once. Items on the ground are
not in the save ("Open"), so dropping one takes it out of the save, and a disconnect loses it (#5). Tests:
`tests/GameServer.Gateway.Tests`.

`/item <BaseItemTypes row, Id or name> [count]` adds items to the main inventory; types with a Sockets component get two
linked sockets in the colour of their highest attribute requirement (ComponentAttributeRequirements).

## 8. Life and mana flasks

**C→S 0x70** (6 bytes, live 2026-10-20): a flask key. u32 big-endian, the flask-bar cell (0 for key 1 … 4 for key 5).
Inferred from presses of the start kit's flasks (cells 0 and 1 sent 0 and 1), not read from the client's serializer.
The client changes nothing by itself: no charges, no recovery until the server answers.

The Gateway (`UseFlask`, `Flasks.cs`) takes the flask in that cell of the flask bar (Inventories row 11). Its use comes
from the tables (`ItemCatalog.FlaskOf`): Flasks.dat LifePerUse / ManaPerUse over RecoveryTime (tenths of a second) and
ComponentCharges PerCharge of MaxCharges. Small Life Flask: 70 life over 3 s, 7 of 21 charges; Small Mana Flask: 50
mana over 3 s, 6 of 24. A use with fewer charges than PerCharge, or while dead, does nothing. Otherwise:

1. The charges are spent and saved, and the flask goes to the client again (S→C 0xbf: the item removed and added with
   its new Charges, same id).
2. The flask's buff goes on the player (S→C 0x1c1): BuffDefinitions row 5 `flask_effect_life` ("Healing") or row 9
   `flask_effect_mana` ("Regenerating Mana"). Both are invisible, cancelled on death and removed once the pool is full.
   Both rows are `IsRecovery` (+0x7c), so 0x1c1 carries one u32 per GrantedStats entry: the recovery per minute
   (`base_life_recovery_per_minute_from_flask`, 70 × 60 / 3 = 1400 for the Small Life Flask). Live 2026-10-20: without
   that value the client misread the message and showed no life change from then on, although the server sent every
   0x1b1. `Game_FLASK_BUFFS=0` recovers without the buffs.
   Being tested: the buff's duration, from which the orb may draw the recovery still to come. `Game_FLASK_BUFF_DURATION`
   picks the field: `ms` (default) flags bit1, u32 +0x68 = 3000; `f6c` flags bit7, f32 +0x6c = 3.0; `0` none.
3. `RunResourceRecovery` adds the recovery evenly from the use to its end, by the clock, every 16 ms while a flask
   recovers, and sends each whole point at once (S→C 0x1b1): the orb rises by +1 about every 43 ms for a Small Life
   Flask, not in the player tick's 250 ms steps of 6 (live 2026-10-20: those looked jerky). The buff comes off
   (S→C 0x1c2) when the time is up, the pool is full, or the player dies. Using a flask whose effect still runs
   restarts it (the rest of the old one is lost); two flasks recover together.

Charges come back: every kill fills every life and mana flask in the bar (a choice for playing locally; with
`Game_FLASK_KILL_CHARGES=rarity` a kill gives 1 normal, 2 magic, 5 rare, 10 unique, our own numbers: the real game's are
not known yet), and they fill up on entering a town or a hideout and on resurrection.

## Open

- Flasks: the real game's charges per kill (a kill fills every flask for now), flask quality and mods, utility and hybrid flasks' effects (a hybrid flask
  recovers both pools but shows only the life buff), instant recovery, and the effects across an area change (they end).
- Gem levels and experience, support gems and links.
- The game's own affix counts, rarity chances and drop rates; other currency on items (0x71); local weapon damage and
  armour / evasion / energy shield mods (items have no base defences or damage on the server yet), unique / corrupted / crafted / influenced items (#60), ObjectMagicProperties +0x7a and bit 9; quality
  effects; the meaning of Base +0xc0 / +0xc6 / +0xc7.
- C→S 0x2d and 0x12 (5 bytes, seen once); 0x4a's flag bit 1, its u16 and its other callers (`0x140b34140`,
  `0x14031c0a0`: probably currency use on an item).
- Currency use other than the Orb of Regret. Ctrl + click of a stack between inventories; a ground stack that only partly fits stays
  on the ground whole (the real game takes what fits).
- Another connection in the same instance sees items dropped meanwhile only when it re-enters (no broadcast; the
  Gateway shows each client only its own player).

### Currency metamod protection

Currency mutations snapshot positive explicit metamod stats before changing the item. Scouring, Annulment, Divine and Chaos preserve existing prefixes/suffixes protected by their respective locks, as well as fractured affixes. Scouring removes the opposite-side protecting craft, retains quality, sockets, links and non-explicit mods, refuses uniques, and derives rarity from retained explicit count (0 normal, 1 magic, 2+ rare). Annulment retains rarity and also excludes attack/caster-tagged affixes when their cannot-roll craft is present. Exalt, Regal and Augment allow additions to a locked side, but filter cannot-roll attack/caster tags. Reforges use cannot-roll restrictions from retained crafts; an unprotected craft removed by Chaos does not restrict the new pool. Bench selective replacement uses the same protection and generation rules. Essence rejects the four outcome-changing metamods (multimod remains allowed). Socket/quality/implicit currencies do not alter explicit affixes.

Regression coverage: MetacraftTests, real data prefix/suffix lock removal, Divine/Chaos preservation, annul tag protection and added-affix tag exclusion. These fixes do not implement currencies absent from UseItemOn, or replace the documented local Chance/Vaal outcome distributions with official probabilities. References: https://www.poewiki.net/wiki/Metamod and https://www.poewiki.net/wiki/Orb_of_Scouring.
