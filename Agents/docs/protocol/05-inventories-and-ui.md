# 05 — Inventories and the other UI prerequisites (0xc0, 0x3d)

Code: `InstanceInventories.AddInventory`, `InstanceInventories.PlayerDefaults`, `InstanceEntities.PassiveSlots`
(`src/GameServer.Core/Protocol/Instance/InstanceEntities.cs`). Tests: `InstanceEntitiesTests`.

After 0x21 activates the world and binds the player, the listeners build the in-game UI. Several UI parts look up data
**without null checks**, so the server has to provide it before 0x21. Found one crash at a time with Frida
([06](06-debugging-log.md)).

## 1. The player inventory manager

`instance+0xc890` (dispatcher `this+0xc888`). `0x14216aa30(instance, sel)` picks a manager: 0 → `+0xc890` (the
player's), 1 → `+0xca10`, 2 → `+0xcb90`, anything else null.

| Manager field | Meaning |
|---|---|
| `+0x150..+0x158` | u32 array of **0x75 (117) entries: inventory id by Inventories.dat row**. Created on the first registration |
| `+0x168..+0x170` | sorted vector of 0x18-byte entries `{u32 id; ptr inventory; ctrl}` (id → inventory) |
| `+0x148` | a pointer the gem/socket walker (`0x142173fc0`) checks before using stats |

- Register `0x141d625f0(manager, msg)`: id = msg `+0x14`, row = `*(msg+0x18)+0x144`. If row < 0x75 and ≠ 0x1a:
  `ids[row] = id`. Then the id → inventory entry is inserted (`0x141d6b330`) and listeners are notified (`0x141d6c1c0`).
- Lookup `0x141d621a0(manager, &out, id)`: binary search, null if not found.
- UI pattern (example `0x14029dfc0` → `0x1402bafb0` → `0x1402d0500`, the flask frame "SapFlaskFrame"):
  `id = (ids.size > 11) ? ids[11] : 0; inv = lookup(id)`, then `inv->field` with **no null check**.
- A scan of all 409 call sites of the lookup found about 50 distinct rows used (0x03, 0x05, 0x07, 0x09, 0x0b flask,
  0x0c cursor (26 sites), 0x0d, 0x0e, 0x10, 0x17, 0x18, 0x1b, 0x21, 0x27, 0x28, 0x29, 0x2a, 0x2b, 0x2c, 0x37, 0x38,
  0x39, 0x3e, 0x42, 0x45, 0x48, 0x49, 0x4a, 0x4d, 0x52, **0x54** expanded main inventory, 0x55, 0x57, 0x58, 0x5c, 0x5d,
  0x65, 0x67, 0x68, 0x69, 0x6a, 0x6c, 0x72, 0x73, 0x74, plus some false positives above 0x74). So the Gateway registers
  **one empty inventory for every row**.

## 2. S→C 0xc0: add inventory

Dispatcher case 0x35. Class `0x1431ab2f0` (0x30 bytes, ctor `0x140b18270`), write `0x140b182a0`, read `0x140b18360`.

```
u16  0x00c0
u8   manager            +0x10   0 = player (0x14216aa30)
u32  inventory id       +0x14
inventory object        +0x18   write 0x1421f4a40 / read 0x1421f4cc0 → ctor 0x1421edc10
  u8  kind              +0x140  Inventories.dat column 1 (InventoryType enum)
  u8  row               +0x144  Inventories.dat row = the manager slot
  u8  width             +0x150
  u8  height            +0x154
  u8  flags             bit0 +0x148 single-item slot, bit1 +0x149 the character's own slot (13 §3), bit2 +0x14a, bit3 → raw u64 +0x158 follows,
                        bit4 → 4 × u32 BE follow (+0x1f0 block), bit5 +0x160, bit6 +0x161
  compact int n items   (0x14089e520 / 0x1421f4f80), then n × (u32 id, u8 x, u8 y, item 0x1421ec1d0; 13 §2-3)
u8                      +0x28   (bool)
u8                      +0x29   (bool)
```

`AddInventory(id, slot)` = `00C0 00 <id BE> <kind> <row> <w> <h> 00 00 00 00`. The Gateway sends ids 1..117, one per
row, after 0x2b9 and before 0x21. Live: all 117 registered, and no lookup returned null afterwards.

The other inventory messages: 0xc1 (case 0x36, `0x141d62700` → `0x141d623b0`, removes an inventory and notifies its listeners; used for stash tabs), 0xc2 (case 0x37,
`0x141d62710`), 0xc3 (case 0x38, `0x142173fc0`: walks inventories for socketed skill gems, "Sockets"/"SkillGem"),
0xc4 (case 0x39, a list of 4-byte entries (u8 at +0, u16 at +2) → `0x142174500` each), 0xc6 (case 0x3a, type 0xb lookup).

## 3. Inventories.dat (3.29.3b, `Data/Inventories.datc64`, 117 rows × 43 bytes)

Dumped with `scripts/re/ggdump`. The row layout: u64 Id string offset, u32 kind, u32 width, then 2 flag bytes, u8
height, …. Rows 0–15 are the character's own inventories (main, equipment, flasks, cursor, map, weapon swap).

| Row | Id | Kind | WxH | Row | Id | Kind | WxH |
|---|---|---|---|---|---|---|---|
| 0 | MainInventory1 | 0x00 | 12x5 | 59 | DONOTUSE2 | 0x0c | 3x2 |
| 1 | BodyArmour1 | 0x01 | 2x3 | 60 | DONOTUSE3 | 0x0c | 3x2 |
| 2 | Weapon1 | 0x02 | 2x4 | 61 | DONOTUSE4 | 0x0c | 3x2 |
| 3 | Offhand1 | 0x03 | 2x4 | 62 | MapDeviceStorage1 | 0x3d | 4x5 |
| 4 | Helm1 | 0x04 | 2x2 | 63 | RESERVED2 | 0x2a | 5x5 |
| 5 | Amulet1 | 0x05 | 1x1 | 64 | RESERVED3 | 0x2b | 3x1 |
| 6 | Ring1 | 0x06 | 1x1 | 65 | RESERVED4 | 0x0c | 64x5 |
| 7 | Ring2 | 0x06 | 1x1 | 66 | UltimatumCraftingItem | 0x2c | 2x4 |
| 8 | Gloves1 | 0x07 | 2x2 | 67 | RESERVED5 | 0x29 | 1x1 |
| 9 | Boots1 | 0x08 | 2x2 | 68 | RESERVED6 | 0x29 | 5x5 |
| 10 | Belt1 | 0x09 | 2x1 | 69 | ExpeditionStorage1 | 0x1d | 14x9 |
| 11 | Flask1 | 0x0a | 5x2 | 70 | HellscapeModificationInventory1 | 0x2d | 6x5 |
| 12 | Cursor1 | 0x0b | 4x4 | 71 | DONOTUSE6 | 0x0f | 1x1 |
| 13 | Map1 | 0x0c | 3x2 | 72 | SentinelDroneInventory1 | 0x30 | 6x2 |
| 14 | Weapon2 | 0x02 | 2x4 | 73 | SentinelStorage1 | 0x31 | 37x10 |
| 15 | Offhand2 | 0x03 | 2x4 | 74 | LakeTabletInventory1 | 0x32 | 1x1 |
| 16 | StrMasterCrafting | 0x0f | 2x4 | 75 | DONOTUSE7 | 0x0c | 5x1 |
| 17 | StrDexMasterCrafting | 0x0f | 2x4 | 76 | Relics1 | 0x34 | 5x4 |
| 18 | DexMasterCrafting | 0x0f | 2x4 | 77 | RelicStorage1 | 0x35 | 12x12 |
| 19 | DexIntMasterCrafting | 0x0f | 2x4 | 78 | SanctumSpecialRelic1 | 0x36 | 1x2 |
| 20 | IntMasterCrafting | 0x0f | 2x4 | 79 | RESERVED7 | 0x29 | 2x4 |
| 21 | StrIntMasterCrafting | 0x0f | 2x4 | 80 | RESERVED8 | 0x29 | 2x4 |
| 22 | PVPMasterCrafting | 0x0f | 2x4 | 81 | RESERVED9 | 0x29 | 1x1 |
| 23 | PassiveJewels1 | 0x0d | 60x1 | 82 | CurrentSanctumRun1 | 0x37 | 1x1 |
| 24 | AnimatedArmour1 | 0x0e | 10x4 | 83 | CraftPredictionScratch | 0x0f | 6x4 |
| 25 | GuildTag1 | 0x0c | 6x1 | 84 | ExpandedMainInventory1 | 0x00 | 4x5 |
| 26 | StashInventoryId | 0x11 | 1x1 | 85 | NecropolisStorage1 | 0x38 | 6x5 |
| 27 | DivinationCardTrade | 0x0f | 4x8 | 86 | MavenInvitations | 0x0c | 11x1 |
| 28 | Darkshrine | 0x0f | 2x4 | 87 | KalguurDisenchanting1 | 0x39 | 12x10 |
| 29 | TalismanTrade | 0x0f | 5x1 | 88 | KalguurMapping1 | 0x3a | 12x3 |
| 30 | Leaguestone1 | 0x10 | 3x1 | 89 | MapRelics1 | 0x3b | 6x7 |
| 31 | BestiaryCrafting | 0x0f | 2x4 | 90 | MapRelics2 | 0x3b | 6x7 |
| 32 | IncursionSacrifice | 0x0f | 2x5 | 91 | MapRelics3 | 0x3b | 6x7 |
| 33 | BetrayalUnveiling | 0x0f | 2x4 | 92 | RecombinatorInput | 0x0f | 8x4 |
| 34 | ItemSynthesisInput | 0x0f | 6x4 | 93 | RecombinatorOutput | 0x0f | 4x4 |
| 35 | ItemSynthesisOutput | 0x0f | 2x4 | 94 | MemoryLineMaps1 | 0x0c | 3x2 |
| 36 | BlightCraftingInput | 0x0f | 4x1 | 95 | Mercenary1 | 0x3c | 17x4 |
| 37 | BlightCraftingItem | 0x0f | 2x4 | 96 | Mercenary2 | 0x3c | 17x4 |
| 38 | DONOTUSE5 | 0x0f | 5x1 | 97 | Mercenary3 | 0x3c | 17x4 |
| 39 | AtlasUpgrades1 | 0x1c | 4x1 | 98 | DONOTUSE8 | 0x11 | 1x2 |
| 40 | AtlasUpgradesStorage | 0x1c | 4x1 | 99 | UNUSED1 | 0x11 | 1x1 |
| 41 | ExpeditionMapMission | 0x0f | 1x1 | 100 | DONOTUSE9 | 0x11 | 1x2 |
| 42 | ExpeditionDeal1 | 0x0f | 2x4 | 101 | BrequelStorage1 | 0x3f | 24x12 |
| 43 | HarvestCraftingItem | 0x0f | 2x4 | 102 | Ring3 | 0x06 | 1x1 |
| 44 | HeistContractMission | 0x0f | 1x1 | 103 | DeepwaterUnchartedMission | 0x0f | 1x1 |
| 45 | HeistNpcEquipment1 | 0x22 | 7x2 | 104 | DeepwaterChartedMissionStorage | 0x0f | 120x1 |
| 46 | HeistNpcEquipment2 | 0x22 | 7x2 | 105 | DeepwaterCraftingInput | 0x0f | 2x4 |
| 47 | HeistNpcEquipment3 | 0x22 | 7x2 | 106 | DeepwaterCraftingOutputStorage | 0x0f | 8x4 |
| 48 | HeistNpcEquipment4 | 0x22 | 7x2 | 107 | MercenaryTemporary | 0x3c | 17x4 |
| 49 | HeistNpcEquipment5 | 0x22 | 7x2 | 108 | LegionCocoonedItems | 0x0f | 10x4 |
| 50 | HeistNpcEquipment6 | 0x22 | 7x2 | 109 | DeepwaterActiveMission | 0x0f | 1x1 |
| 51 | HeistNpcEquipment7 | 0x22 | 7x2 | 110 | AbyssPinnacleExchangeInput | 0x0f | 4x1 |
| 52 | HeistNpcEquipment8 | 0x22 | 7x2 | 111 | AbyssPinnacleExchangeOutput | 0x0f | 1x1 |
| 53 | HeistNpcEquipment9 | 0x22 | 7x2 | 112 | DeepwaterLockerInventory1 | 0x44 | 12x79 |
| 54 | Trinket1 | 0x23 | 1x1 | 113 | DeepwaterExtractionInventory1 | 0x41 | 12x5 |
| 55 | HeistBlueprintMission | 0x0f | 1x1 | 114 | DeepwaterCollectionInventory1 | 0x45 | 12x40 |
| 56 | HeistStorage1 | 0x24 | 107x12 | 115 | DeepwaterCursedDucats1 | 0x46 | 3x1 |
| 57 | RitualSavedRewards1 | 0x25 | 100x4 | 116 | DeepwaterCursedDucatsStorage1 | 0x46 | 6x1 |
| 58 | DONOTUSE1 | 0x0c | 3x2 | | | | |

## 4. S→C 0x3d: passive slots

Dispatcher case 0x11. Class `0x1435dcd00` (0x38 bytes, ctor `0x14219eb10`), write `0x14219eb40`.

```
u16  0x003d
u16                       +0x28
u16                       +0x2a
u8   flags                bit0 +0x2c, bit1 +0x2d (no list), bit2 +0x2e, bit3 +0x2f, bit4 +0x30
vec16<u16> (raw)          +0x10   only if bit1 is clear (0x1421b8fe0), likely allocated passive node ids
```

Handler: bit2/3/4 → `0x142155bf0(…, 0/1/2)`, which **creates slot 0/1/2** at instance `+0xc2f8` (3 × 16 bytes). Then, if
the computed active index differs from `+0xc2f0`, it switches and notifies (`0x14019a1b0(+0xb1f0, old, new)`). UI code
`0x140785160` reads `slots[+0xc2f0]` without a null check (crash at `0x140785acf` when they are missing). The three
slots are the **Atlas** passive tree's three presets (their check `0x141eed000` logs "atlas passive skills allocated
but only allowed"; the character's tree is S→C 0x33, [15](15-progression.md) §2). The server sends one message per
preset (`flags` 0x04/0x08/0x10), with `u28` = that preset's allocated count and `u2a` = the point budget; the list is
the preset's PassiveSkillGraphIds. Allocation is **C→S 0x3e** (7 bytes: u8 preset, u32 BE node) and refund **0x3f**
(8 bytes: u8 preset, u32 BE node, u8 flag). The server saves the three lists and the point budget per character; map
completion does not award points yet (dev: `/atlas_points <n>`).

## 5. What the UI did not need (so far)

With 0x22 + 0x2b9 + 117 × 0xc0 + 0x3d + 0x21 the HUD came up: life/mana orbs, flask bar (5 empty slots), skill bar
(Q W E R T, mouse), minimap, clock, "MENU", "SHOP". The chat logged "Timeout while joining trade chat channel" and "Timeout while joining
global chat channel". There's no chat server, which is harmless. One C++ exception was
thrown and caught during the UI build; its backtrace went through `0x1421cc54e` → `0x140e4a5f2` → `0x14220d7c3` →
`0x14094193d`, probably the chat join, and it was not investigated.

## Open

- Items are in [13](13-items.md): the start kit goes into these 0xc0 messages, and S→C 0xbf changes an inventory later.
- What the 0x3d u16 fields and node list mean.
- Manager 1 (`+0xca10`) holds NPC shop and trade inventories ([17](17-npcs.md) §5), manager 2 (`+0xcb90`) the guild
  stash's; the personal stash's tabs are inventories of manager 0 on row 26 ([18](18-stash.md) §4).
