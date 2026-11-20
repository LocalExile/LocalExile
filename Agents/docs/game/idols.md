# Idols

Legacy of Phrecia's Idols are items with map modifiers.
While they lie in the Idol inventory they affect every map the character opens, and they are not used up.
The module is `Modules/Legacy/Idols`; the Phrecia league lists it beside
[the ascendancies](phrecia-modules.md), and neither depends on the other.

**The 3.29.3 clients have the Idols' data and rules but no Idol panel.**
So the Idol inventory is not the client's own: it is the map device's storage grid in the Atlas screen's side panel
(the arrow at the screen's left edge), 4 by 5 cells and one loadout.
What that leaves out is under [Differences from the real league](#differences-from-the-real-league).

## What the client has

| | |
|---|---|
| Item class | `ItemClasses` row 94 `AtlasRelic`, "Idols" |
| Bases | `Metadata/Items/Relics/AtlasRelic1x1` Minor, `1x2` Kamasan, `2x1` Noble (drop level 68), `1x3` Totemic, `3x1` Burial (75), `2x2` Conqueror (81) |
| Modifiers | `Mods` domain 37: 208 prefixes and 344 suffixes (levels 68 to 78), and 98 more for the implicits and the unique Idols; the spawn tags are the sizes, `atlas_relic_small` to `atlas_relic_very_large`; each base has an implicit "increased Maps found" |
| Inventories | rows 89 to 91, `MapRelics1` to `3`: kind 0x3b, 6 by 7, one per Atlas preset |
| Cells | `MapRelicInventoryLayout`: 32 cells of the 6 by 7. Its `UnlockQuest` is a **league quest flag** id ([21](../protocol/21-hideouts.md) §1): 350 `UnlockedMapDevice` opens 20 cells; 4 need `AtlasIdolSlotsUnlocked1`, `3`, `5` or `6` (1464 to 1469), 5 name 361 to 365, which this client calls `RECLAIM_ME7` to `11`, and 3 name 1, `NEVER_SET` |
| Uniques | `UniqueMapRelicLimits`: 41 unique Idols, one of each; three unique images (`ItemVisualIdentity` `AtlasRelicUnique1` to `3`), all 1 by 1 |
| League stat | `Stats` row 21146 `league_enable_atlas_relics` (area stat 0x529b), the `LeagueAtlasRelics` mod |

What the client does with them:

- **The league stat hides the Atlas passive tree.** `0x1407b2500` shows the Atlas screen's tree button only while the
  stat is 0, and `0x140d77a00` and `0x140dc41c0` refuse to open the tree screen with it (S→C 0x7b kind 0x11 does nothing).
  Without the stat the tree works as elsewhere. Nothing else the Idols need reads it.
- **S→C 0x3d binds the Idol inventories.** With the stat set, the handler of each Atlas preset
  ([05](../protocol/05-inventories-and-ui.md) §4) `0x14215c540` looks up row 89 + preset and hands that inventory to
  the preset's object (`0x141ef5e80`), which reads the items' stats: the client computes the loadout's effect itself.
- **Kind 0x3b has the Idols' placement rules.** In the inventory check `0x141d65f80` an item goes into an inventory of
  kind 0x3b only when it is an `AtlasRelic` and every cell it covers is a `MapRelicInventoryLayout` row whose flag is set.
- **Nothing shows those inventories.** The Ctrl + click handler `0x14030e970` asks the HUD which Idol loadout is open
  (virtual `+0x10c8`, at `0x140310f63`) before it sends C→S 0xe8. The HUD's answer is a stub, `0x140d96930`
  (`mov byte [rdx+1], 0; ret`: none), so 0xe8 is never sent. The executable names no panel image or layout for Idols,
  only the tables and two texts (`ItemDescriptionAtlasRelic`, `AtlasRelicsInfoHover`). No message opens what is not there.

## The side panel

The Atlas screen's side panel shows Inventories row 62, `MapDeviceStorage1` (kind 0x3d, 4 by 5; built by `0x1407946a0`).
Live, with a Conqueror Idol held over it and a Minor Idol put into it:

| Row 62 registered as | The client |
|---|---|
| 0x3d, its own | refuses the Idol (red; no message) |
| 0x3b, the Idols' | takes it only on the layout's cells: 11 of the panel's 20 with `UnlockedMapDevice`, and red elsewhere |
| 0x0f, the crafting slots' generic kind | takes any item in any cell |

The module registers the row as 0x0f (`GameEvents.InventorySlot`) and refuses what is not an Idol itself
(`GameEvents.InventoryRefusal`).
The panel binds to the inventory object of the row's first S→C 0xc0 in an area: an inventory registered again later
(0xc1, then 0xc0) holds its items but is not drawn until the next area. So the kind is chosen as the player enters.

## What the module does

- **Effect.** When the map device is activated (`GameEvents.MapOpening`), the stats of the identified Idols in row 62
  are added to the map, unscaled, as the device's fragments are.
  They reach the instance's area stats (S→C 0x1d) and whatever reads a map's stats; a stat the server has no consumer
  for does nothing yet, as on a map item.
- **Drops.** Monsters in maps drop Idols unidentified, magic or rare, of a base whose drop level they reach
  (`GameEvents.Drops`), scaled by the map's item quantity (`MapMods.QuantityFactor`) and the module's `dropRate` setting.
  GGG does not publish the rates: the module's are its own (`IdolRules`).
- **Rolls.** Idols have the jewels' affix caps, two a side and three or four on a rare (`ItemRolls`).
- **Unique Idols.** One Idol drop in 50 is a unique one, each as likely as the next, and the Idol inventory takes one
  of each (`UniqueMapRelicLimits`; the client's tooltip says "Limited to: 1" by itself).
- **Gambling.** Faustus's gamble window (Kingsmarch and the hideout; [17](../protocol/17-npcs.md) §5) has an Idols
  tab: the client's six `…Gamble` Idol rows ("Random Minor Idol" …), each offered once the character reaches its base's
  drop level. A purchase gives an identified Idol: one in 50 a unique of that base (only the Minor Idol has any), else two
  in three rare. The price is the 3.29 ring gamble price at the window's pricing level (respecting the league's alt price
  floor) times the size's `GoldPriceMultiFromTag` multiplier (1.2 to 2.5): 4,926 to 10,263 gold at level 83 and above.
- **Settings** (`settings.json`): `enableAtlasTree` (off, the default: the league stat is sent and the client hides the
  Atlas passive tree, as in the real league; on: Idols and the tree together) and `dropRate`.

## The unique Idols

The client has no table that says which `Mods` rows a unique has, so the 41 are world.db rows
(`unique_items` and its tables), all on the Minor Idol and kept out of the ordinary unique drops.
The client data is enough to tell them, because **a unique Idol is an Atlas passive made an item**:

- 36 have the name of an Atlas passive (`PassiveSkills`).
  The ten `atlas_disable_league_N` notables (Black Thumb: no Harvest; Loved by the Sun: no Abyss; …) each have exactly
  one `UniqueMapRelicDisable…` row with their three stats.
  The 26 keystones (Eye of the Djinn, Singular Focus, …) have the row of their name, `UniqueMapRelic<Name>`; 19 of them
  carry the keystone's own stats, the others an Idol version of them.
- Five have no Atlas passive and rows of their own: Smash and Grab (`UniqueMapRelicSmashAndGrab`), Well Supplied,
  Perceived Greatness and Twisted Wish (`UniqueRelic<Name>`), and Crystallised Power, which is left with
  `UniqueMapRelicEssenceBoss`.
- Twisted Wish also rolls three of the 17 `UniqueMapRelicTwistedWish…` rows.
  That count and the images' groups are not in the client data: they are PoEDB's (the "disable" Idols and Refiner's
  Bargain the first image, the keystones the second, the five without a passive the third).
  Eye of the Djinn and Lone Adventurer were not on the pages read and take the keystones' image.

## Differences from the real league

- **20 Idol cells instead of 32, and none of them gated.** The real league's Idol inventory has 32 cells, 12 of them
  unlocked by Atlas objectives, and three loadouts; the side panel has one loadout of 20 cells, all open from the start.
- Ctrl + click does not move an Idol: pick it up and put it down.
- The two Ruthless versions of unique Idols (`UniqueMapRelicHardMode…`) are not there.
- Gambling prices and odds for Idols are the server's own: GGG does not publish them.
- How often an Idol is unique is the server's own number, like the other drop rates.
- Hiding the tree does not switch it off on the server: Atlas passives allocated while the tree was enabled, or
  allocated through a dev command, can still affect maps after the tree is disabled.
