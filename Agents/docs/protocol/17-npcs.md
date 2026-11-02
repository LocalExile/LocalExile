# 17. NPCs

State of 2026-10-19: Tarkleigh, Nessa and Bestel stand in Lioneye's Watch. They show their name on hover and on the minimap, and
clicking one walks the player to them and opens the NPC dialogue window. Verified live with the real client. The
window's options come from the client itself (NPCTalk); choosing one is not handled yet.

```mermaid
sequenceDiagram
    participant C as Client
    participant G as Gateway
    G->>C: 0x2b9 create NPC (type = NPCs.dat Id), 0x1a9 wake zone
    C->>G: 0x24 on the NPC (skill 0x0266, as for the waypoint)
    Note over C,G: the client walks into range
    G->>C: 0x84 NPC object id, 0xff
    Note over C: NPCDialogueWindow opens
    C->>G: 0x85 (dialogue closed)
```

## 1. The NPC object

An NPCs.dat row's `Id` (`Metadata/NPC/Act1/Tarkleigh`) is an .ot type, created with S→C 0x2b9 like any object
(type = `ObjectTypeHash(Id)`). `Metadata/NPC/NPC.ot`: Positioned (`object_size = 5`, blocking), Animated, BaseEvents
(`server_only`), InteractionAction, NPC, MinimapIcon (`icon = 3`), client Render (`ObjectDisplayTextNPC`) + Targetable.
The child .ot only sets its `animated_object`.

Of the NPC types in `data/object-types.json`, 190 have exactly that list; 54 add Transitionable, 31 StateMachine, and a
few ControlZone, Functions, Stats or Brackets (not written yet).

| Part | Bytes |
|---|---|
| Positioned | as a monster's ([10](10-chat-and-commands.md) §5): x, y, f32 rotation, u16 team 0, flags `00 02 00 01` |
| Animated | `00` (the type supplies the model) |
| BaseEvents | nothing |
| InteractionAction | `00` |
| NPC | two u8 (slot 17 `0x141e75050`): `+0x20` has dialogue (`0x141e76d40` lists NPCTalk rows only when set), `+0x21` index of a component that must be alive, `0xff` none. Sent `01 ff` |
| MinimapIcon | `00` |
| Transitionable, StateMachine | as for monsters |

The NPC component's debug dump (`0x141e751d0`) prints the NPC and the NPCTalk row the client picks for the player
(`0x141e76d40`): the client chooses the dialogue itself.

Bosses that speak have an NPC component too, after their Transitionable (Captain Fairgraves' `SkeletonBossEmerge`,
Merveil, the Siren's `seawitchillusion/Merveil`; Piety, General Gravicius and the Sceptre of God's three lieutenants,
Kaom, Daresso, Utula, Malachai and the three in The Harvest): the monster blob writes it as `00 ff` (no dialogue, no
component that must be alive). Without it these monsters could not be created at all ([12](12-quests.md) §9–10).
Act 2's bandit lords (Kraityn, Alira, Oak) add InteractionAction before their NPC, written `00` as for an NPC: they
are created as hostile unique bosses; their dialogue (help or kill, the bandit choice) is not offered this way.

NPC collision uses the same inherited Positioned metadata as the client, including NPCs.dat Id aliases whose
OT is the row's Metadata. Server routing and overlap escape register every spawned NPC with its own footprint,
in both towns and hideouts; moving an NPC replaces its registered footprint rather than adding a second one.
NPCs explicitly marked nonblocking stay nonblocking. Their model hull is used when the type is static.
The common implementation is `StationaryCollision` / `Program.StationaryCollision.cs` (21, installed furniture).

## 2. Where town NPCs stand

Each campaign town has one fixed town room (.arm), and the room lists its NPCs among its doodads, one line each:
`x y 0 angle qx qy qz qw a b flag [z] scale "model.ao" "Metadata/NPC/…"` (room cells; the angle in radians, the quaternion
is the same turn about z; z only when the flag is 1). That is a version 35 or 36 room; version 31 rooms (Oriath's
`entrance_04.arm`) have no 0 before the angle. Lioneye's Watch's room `beach_town_unique_normal.arm` has
`230 389 0 -0.155698 … "Metadata/NPC/Act1/Tarkleigh.ao" "Metadata/NPC/Act1/Tarkleigh"`. The client creates the plain
doodads of those lines itself (type `Metadata/MiscellaneousObjects/Doodad`) and leaves the others (NPCs, stash, notice
boards …) to the server.

The room is placed like a tile ([08](08-area-transitions.md) §6, `TilePlacement`): the town's .dgr node gives the
rotation (Lioneye's Watch R270, code 3: room (x, y) → grid (y, 413 − x), angle + 3π/2), plus the grid cell of the
room's origin. The walk-map recorder (`scripts/walkmaps`) captured every town on 2026-10-25 (seed 1): the placed room
(`<area>_seed1.rooms.json`: path, origin, size, code) and the client's doodads. In all twelve towns every plain doodad
of the room stood at exactly its line's placed cell (40 of 40 in Lioneye's Watch, 829 of 829 in the Karui Shores) and
angle, so the server's lines go through the same placement. In Lioneye's Watch that gives Tarkleigh (389, 183), Nessa
(346, 270), Bestel (362, 247), within one cell of the cells measured earlier on the real game's minimap ((388, 182),
(346, 271), (361, 246)). `TownNpcs` copies the NPC lines of every town with its room's code and origin;
`/npc_face <name> <degrees>` still turns one live, and `/npc_place <name> <x> <y> <degrees>` moves and turns one (the
NPC is removed and created again there).

The league hubs have NPCs of their own (`NpcCatalog.Towns`): Niko in the Mine Encampment, Einhar in the Menagerie
(495, 485, 290°; the hideout row `EinharHideout`, because `EinharMenagerie` has no `.ot` in the GGPK and the client
drew it without a model), Divinia (`SanctumNPCTown`) in the Forbidden Sanctum, Johan, Tujen, Gwennen, Rog, Dannig,
Faustus, Isla, Sonja and Raulf in Kingsmarch, Kurai, Faustus, Adiyah, Whakano and the nine rogues in the Rogue Harbour,
Ailith (`ChayulaFarmerHub`) in the Monastery of the Keepers. Kingsmarch, the Rogue Harbour and the Monastery have not
been placed live yet: their NPCs, stash and guild stash are offsets from the arrival (`StashCatalog.FromArrival`).
Every hideout also gets Einhar (`EinharHideout`).

Several town rooms do not start at (0, 0): the area has a border around the room. The atlas's grid placement for
fixed layouts ([08](08-area-transitions.md) §6, step 10) assumes (0, 0), so its exits in those towns are probably off
by the same amount.

| Town | Room (.dgr rotation, origin) | NPCs placed |
|---|---|---|
| Lioneye's Watch (1_1_town) | `Act1/Town/Rooms/beach_town_unique_normal.arm` (R270, 0, 0) | Tarkleigh, Nessa, Bestel |
| The Forest Encampment (1_2_town) | `Act2/Town/Rooms/forest_town.arm` (R270, 0, 92) | Yeena, Eramir, Greust, Silk, Helena |
| The Sarn Encampment (1_3_town) | `Act3/Town/Rooms/city_town.arm` (I, 69, 69) | Hargan, Grigor, Maramoa, Clarissa |
| Highgate (1_4_town) | `Act4/Town/Rooms/town.arm` (R270, 0, 0) | Kira, Oyun, Petarus and Vanja, Lady Dialla, Tasuni |
| Overseer's Tower (1_5_town) | `Act5/Town/Rooms/town_01.arm` (I, 138, 138) | Utula, Bannon, Lani, Vilenta |
| Lioneye's Watch (2_6_town) | `Act6/BeachTown/Rooms/part2_beach_town.arm` (R270, 0, 0) | Tarkleigh, Bestel, Sin, Lilly Roth |
| The Bridge Encampment (2_7_town) | `Act7/Town/Rooms/broken_bridge_town.arm` (I, 437, 207) | Eramir, Yeena, Helena, Sin, Weylam Roth |
| The Sarn Encampment (2_8_town) | `Act8/Town/Rooms/city_town.arm` (I, 69, 69) | Hargan, Maramoa, Clarissa, Sin |
| Highgate (2_9_town) | `Act9/Town/Rooms/town.arm` (R270, 0, 0) | Petarus and Vanja, Irasha, Tasuni, Sin |
| Oriath Docks (2_10_town) | `Act10/Town/Rooms/dock_town.arm` (I, 0, 0) | Sin, Bannon, Weylam Roth, Lani, Lilly Roth |
| Oriath (2_11_town) | `Act11/Town/Rooms/Unique/fountain.arm` (node "fountain" I among generated nodes, 299, 299) | Zana, Innocence, Sin, Weylam Roth, Lilly Roth, Lani |
| | `Act11/Town/Rooms/Unique/entrance_04.arm` (node "entrance_04" I, 161, 851) | Helena |
| Karui Shores (2_11_endgame_town) | `Act11/Camp/Rooms/camp.arm` (I, 92, 92) | Lilly Roth, Weylam Roth, Lani, Helena |

- **Types without an .ot.** Half of the NPCs.dat Ids have no .ot of their own (`Metadata/NPC/Act6/Tarkleigh`,
  `Act9/Irasha`, `Epilogue/Lilly` …). The object-type register's 0xac-byte table ([04](04-player-entity.md) §1) is
  NPCs.dat: it files every row under its Id on the .ot of its `Metadata` column (row +0x10; Act 6's Tarkleigh on
  `Metadata/NPC/Act1/Tarkleigh`, Irasha on `Metadata/NPC/NPC`). So 0x2b9 still uses `ObjectTypeHash(Id)`, and the
  Gateway takes the components of the Metadata .ot (`NpcCatalog`).
- **Model.** A line's .ao is sent in Animated (bit0 + path) when the type has none (Irasha, Act 6's Lilly) or another
  one (Weylam's `Act6/RotTooth.ao` instead of `Act7/Weylam.ao`, Lilly's poses `Act5/Lilly.ao` / `Act6/LillySitting.ao`,
  the Karui Shores' Helena); otherwise Animated stays 0 (the type's model).
- **Transitionable.** Helena in Oriath and the Karui Shores (`Epilogue/Helena`, an empty `Transitionable {}`) hides herself
  while untransitioned (`create_untransitioned = { DisableRendering(); … }`): with state 1 she was invisible live
  (her dialogue still opened), with state 2 she shows. Act 10's Bannon at state 1 is Bannon (his `create_transitioned`
  hides him for Innocence). So an empty `Transitionable {}` has two states, not the one `object-types.json` reports.
- **Height.** A line with flag 1 carries a world z, sent as Positioned's fixed height (flags0 bit2, before flags1
  0x02's two bytes): i32 −z / 7.8125 and f32 z, the live server's convention for a fixed height (its projectiles,
  [11](11-skills.md) "Sign"; world z points down). Live the NPCs looked the same with either sign of the i32 (Nessa,
  Kira, Oyun, Act 6's Lilly on her pier, 2026-10-25: same pose and name labels at the same height), so for a standing
  object the client seems to use the f32. The area transitions and waypoints still send +z / 7.8125
  ([08](08-area-transitions.md) §5); aligning them is left for a later change.
- **Seed.** The origins other than (0, 0) were captured with seed 1, the seed the Gateway gives towns; `TownNpcs`
  stores it with those rooms and leaves a room's NPCs out (with a log line) in an instance of another seed. Oriath's
  rooms are generated around fixed nodes, so their origins could depend on it; the single-room towns' borders
  probably do not.
- **Blocked cells.** Several NPCs stand on cells the client's walk map blocks (stalls, platforms, piers: Hargan in both
  Sarn Encampments, Grigor, Petarus and Vanja and Tasuni in both Highgates, Act 6's Sin and Lilly, Act 7's and Act 8's
  Sin) or on the edge of walkable ground. They are placed as the room has them, and the Gateway's "not walkable"
  warning skips town-room placements (`Placement.FromRoom`).
- **Not placed:** league NPCs of the same rooms (Einhar, Niko, Jun, Alva, Gwennen, Tujen, Rog, Dannig, Divinia,
  Faustus, Kurai, Cassia, Tane, Johan), the Tutorial NPC and Letters of Exile, Tolman (act 8, no NPCTalk rows), Kirac
  and the Karui Shores' `HelenaIntro` at (510, 173) (a trigger by the tunnel that plays "HelenaThroughTheTunnel" and
  then hides; both need ControlZone, which is not serialized) and later quest stages of the same NPC: Highgate's `DiallaTownWithered` at (250, 553),
  Oriath Docks' `InnocenceTown` in Bannon's cell, Act 6's Lilly sitting by Bestel at (148, 343) (she is placed by her
  ship, `QuestObjects/Act6/LillyShip`). Which stage the real server shows depends on quest progress; the Gateway places
  the first one.
- **Verified live** (2026-10-25, OfflineWitch, `/goto` into each town): every placed NPC (Oriath's Helena added
  afterwards and checked the same way) has its name and minimap label, clicking walks up and opens its dialogue (0x84), option 0 plays (Act 6's Tarkleigh shows Act 6's rows, so the
  client resolves the Act 6 Id), and Goodbye closes it (0x85). Purchase Items and Sell Items open the buy and sell
  windows for these NPCs too (§5); only the NPCs `NpcShops` stocks have goods.
- `data/room-points.json` lists these lines with `angle` 0: `RoomFile` (GameServer.Data) reads the third number, which
  is the angle in version 31 and older rooms (the Twilight Strand's force walls have theirs) but a 0 in version 35 and
  36 rooms (every town room), where the angle is the fourth. `TownNpcs` copies the angles from the lines itself.

## 3. Messages

### C→S 0x24: talk

A click on an NPC sends 0x24 (use skill on object, [09](09-area-change.md)) with skill `0x0266`, the same interaction
as the waypoint. The client walks to the NPC first. The Gateway follows the walk (`NpcReach` 8 cells, not measured)
and then answers with 0x84.

### S→C 0x84: open the dialogue (13 bytes)

World group, class `0x1435dd7d8`, read `0x141fddcb0`: the object header (u16 id, u32 BE object id → `+0x10`, u32, u16),
then u8 → `+0x20`. The world handler `0x142187480` calls `0x142149db0(instance, object id, u8)`:

- object id 0: closes the dialogue (clears instance `+0xd7c0`).
- u8 `0xff`: sets instance `+0xd7c4`/`+0xd7c8` to the NPC and fires the listeners at instance `+0x13f8` →
  `0x140dcc720` (in-game UI, registered by `0x140dbcd40`) → `0x14071f6d0` → `0x140723d60` (NPCDialogueWindow setup).
- other values: another path that looks at the NPC component's data (`+0x66`), not mapped.

### C→S 0x85: dialogue closed (2 bytes)

Id only. Live 2026-10-19 it came after every opened dialogue, when the window closed (Goodbye, Esc or walking away).

### C→S 0x82: option (3 bytes)

u8 = the chosen NPCTalk row's index among the NPC's rows (column 1). The client (`0x140722960`) handles NPCTalkCategory 5
(Topic), 0x19 and 0x26 itself and sends 0x82 for the others. The Gateway answers by category:

| Category | Answer |
|---|---|
| Gossip, Quests, QuestReward | S→C 0x84 with the index: the window plays the row's text and audio (verified) |
| BuyItems | shop tab (S→C 0xc0, manager 1) + S→C 0x8c (verified: the buy window shows the stock) |
| SellItems | two offer inventories (0xc0, manager 1) + S→C 0xb8 (verified: drops, Ctrl + click, Accept, Cancel; §5) |
| RespecPassivePointsForGold | S→C 0x7b kind 0x5f with 1: the character's passive tree (UI +0x650, listener 0x140dc8750; a non-zero u32 shows it in mode 2, `0x1409de680`), the script's `SendOpenScreen( CharacterPassiveTreeScreen, 1 )`. Not kind 0x11 (Atlas tree, UI +0x668) or 0x45 (UI +0x678). "Apply refunds" (C→S 0x36 per node) and "Refund All" (0x37) are paid in gold ([15](15-progression.md) §2) |
| IdentifyItems | script `triggerer.IdentifyItems()`: every unidentified item with mods in the main inventory becomes identified, free (0xbf removes and re-adds the same ids), then a chat line "{0} Items identified" / "{0} Item identified" (ClientStrings `IdentifyItemsChatMessage(Single)`, S→C 0x17, [10](10-chat-and-commands.md) §2) and 0x84 0xff to list the options again (verified 2026-10-20). The client grays the option itself (`0x141e754b0` case 0x3b: no unidentified item in the inventory → ClientStrings id 0x1f07 "No items in your inventory can be identified", drawn gray by `0x14071e8d0`), but only when the options are listed. The real game probably charges Scrolls of Wisdom (ClientStrings `IdentifyItemsNotEnoughScrollsOfWisdom`); ours is free. The IdentifyAll sound effect is not sent |

C→S 0x85 during a talk = Continue → 0x84 0xff (back to the options); otherwise the Gateway answers 0x84 with object 0,
which ends the dialogue (without an answer the client stays in its dialogue state and the player cannot move).

### NPCTalk scripts

A row's `Script` / `Script2` (NPCTalk columns) can do more than set flags. The Game module runs these calls
(`NpcCatalog.Talk`, Game `World/Quest/NpcScripts.cs`; [campaign quests](../game/campaign-quests.md) E5):

| Call | When | What the Gateway does |
|---|---|---|
| `SetQuestFlag(f)`, `ClearQuestFlag(f)`, `SetSessionQuestFlag(f)` | on choosing | the flags ([12](12-quests.md) §5); a session flag (SessionQuestFlags.dat) is never saved and is gone on the next area |
| `DirtyTransition();` (top level) | on choosing | the NPC's own Transitionable steps one state on (S→C 0x2ba; Dialla at the Rapture Device withers, 534) |
| `ForEachOfType("<type>", { DirtyTransition(); })`, `ForEachInRange(r, { IfType("<type>", { Transition(); }) })` | on choosing | every object of the type steps on: NPCs, the quest objects the engine created, and area transitions of that tile type its quest flag still hides are shown on this instance (Dialla's 521 opens `AscentTransition`; the row's own flag keeps it open, `On.Exit`) |
| `DoNPCChat(n)` | when the talk ends (0x85 Continue) | the NPC's option `n` plays next (0x84 with its index), with its own flags and script: talks in pages (Sin's "Kitava" 882 → 883 → 884) |
| `FinishNPCChat()` | when the talk ends (at once for a row without text) | the dialogue closes (0x84 object 0) instead of listing the options again |
| `SendOpenScreen(PantheonScreen)` | with FinishNPCChat | S→C 0x7b kind 0x14 (the Pantheon panel) |
| `AreaTransitionTo(<area>, <index>, <delay>)` + Script2 `FadeToBlack(…)` | with FinishNPCChat | after the delay (seconds) the player moves to the area's arrival point, as `/goto`; the fade is the client's own. The index (0/1) is probably the arrival point (not decoded); every arrival is the area's default point |

Not implemented: `UnlockPantheonPowerFromItem`, `RemoveQuestItem`, `DispatchRulesetEvent`, `EnableScion`.

## 5. Shops

- **S→C 0x8c** (read `0x1421a1f60`): u32 BE NPC, u16 BE (NPCShop row), u8, u16 n + n × u32 BE inventory ids (one tab
  each, looked up in inventory manager 1, instance +0xca10, `0x14216aa30`), u16 m + m × (compact uint, wstr name, u8)
  tab names. Listener `0x140dcccf0` → VendorBuy (UI +0x7b8, vtable `0x1431356b8`, +0x380 = `0x14073d570`).
- **C→S 0x88** (write `0x141f95380`): u32 BE shop item, u32 BE destination inventory (masked like 0x28's), u8 x, u8 y,
  u8, u8 flags, compact uint. The Gateway moves the item (0xbf on the tab, manager 1, and on the inventory) and takes the
  price from the main inventory's stacks and then the stash's (any tab, as 3.29 vendors do), in one transaction. Not
  enough: nothing happens, no message (the client shows the price in red itself, below). The client counts only the
  currency it knows of: the inventory's and the stash tabs it has the contents of. So on entering an area the Gateway
  sends the contents of every stash tab with stackable items (S→C 0xc0, no 0xc8; `SendPlayer`); before that, a price paid
  from the stash was red and no 0x88 came until the stash had been opened in that area (verified live 2026-10-23, Nessa
  in the hideout, the stash not opened).
- **C→S 0x8a** (dispatcher case 0x8a): Ctrl + click on a shop item. Read from live bytes (2026-10-20, Nessa): u32 BE shop
  item, u8, u8 flags, compact uint (as 0x88's), u16 token (raw); `008A 00000061 00 00 00 A777`, 11 bytes. No cell: the
  Gateway puts the item into the main inventory (a stack onto stacks of its type, then the first free cell) and takes the
  price from the inventory and then the stash, in one transaction (`ItemMoves.BuyToInventory`); with no room or not
  enough to pay, nothing happens.
- **Prices**: item Base flag bit 7: u8 n + n × (u32 item type hash, compact uint amount), stored in the Base's +0xb8 list.
  Ours: 1 Scroll of Wisdom for the ordinary stock; a gem vendor's unlevelled gems cost the currency of their level-1
  character level requirement (`GemVendorStock.Price`): 1–7 Scroll of Wisdom, 8–15 Orb of Transmutation, 16–27 Orb of
  Alteration, 28–37 Orb of Chance, 38+ Orb of Alchemy, one of it. The tooltip shows it under Cost, in red when the player
  can't pay it: the client decides that itself, no server message needed (live 2026-10-20).
- **Stock** is the server's (NPCShop only names shops): `NpcShops.Stock`.
- **Several tabs**: one manager-1 inventory per tab (S→C 0xc0, ids from 0x400), all listed in 0x8c with their names
  (the m × (compact uint index, wstr name, u8) list). `NpcShops.Tabs`: the NPC's stock in "Goods", and a gem vendor
  (`GemVendorStock.Sells`: Nessa, Yeena, Clarissa, Petarus and Vanja, Lani, Lilly Roth, Siosa) also sells gems in seven
  tabs by SkillGems GemColour and IsSupport: Red Skills, Red Support, Green Skills, Green Support, Blue Skills, Blue
  Support, White Skills (2026-10-21; 3.29 progression 2026-10-26). 1 Scroll of Wisdom each, as everything.
  The gem stock is `GemVendorStock`: the level-1 gems of every quest reward offer the character has completed (the
  offer's reward is shown or was taken), filtered to the character's class, restricted to `ItemCatalog.ShopGems`
  (creatable, dropping, not Vaal or awakened). Siosa (after `a3q12-DeliveredPages`) and Lilly Roth (after
  `UnlockedLillyGemShop`) ignore the class restriction, and Lilly also the quest completion; the 3.29 cross-class
  additions put Faster Attacks on Nessa after a1q2 and Faster Casting on Yeena after a2q4. Live 2026-10-26: Nessa
  listed the Witch's a1q1 and a1q4 gems, then the a1q2 gems plus Faster Attacks after the flag; Lilly is unit-tested
  only (the Act 6 town has no walk-map capture and spawns the player in water).
  Nessa's goods use exact MapWorlds base identifiers, one ordinary white item per display name, ordered by tier/name.
  UniqueMaps supplies one identified unique item per name, with Words HASH32 and ItemVisualIdentity HASH16.
  These are catalogue identities; complete unique modifier sets and unique-area device routing remain unimplemented.
  No MapAtlas, MapTier or generic MapKey entries are mixed into this stock.
  Goods2, Goods3 and later pages are created automatically when needed.
  Tabs fill row by row. 0x8c's u16 is the window kind (`0x14073ee60`, +0x5f0), not an NPCShop row: 0 buy, 1-4 haggle,
  deal, exchange and expedition, 9 Azmeri, 10 black market, 11 unset; the Gateway sends 0. The window is chosen by the
  NPC's DialogueStyle (`0x14072b160`: not LEGACY → UI +0x7b8), not by the u8. The u8 is the async trade mode (title
  "Merchant", 78 more height, a report popup on the tabs; `0x14073d570`, `0x14073fdd0`); the Gateway sends 0. A tab's
  compact value is its colour, not an index (+0x24: ARGB, 0 the default brown; a small number has alpha 0, which drew no
  tab background). Tabs have 11 rows: 12 pushed the tab bar over the "Purchase" title (live 2026-10-31). The window's own
  "Select Items To Buy" (`0x14073bee0`) stays visible behind the first tabs; no server value removes it.
- **Sell**: S→C 0xb8 (write `0x141d6acc0`): u32 BE other party (0 = NPC sale → VendorSell, UI +0x7c8), u32 BE your offer,
  u32 BE their offer (both manager-1 inventories, grids `0x140317440` / `0x140c605f0`), u8 flags, wstr name, u32 BE
  NPCs row, u16, u32 BE TradeWindowVisuals row, u16 (+0x54, shown as an error text when set), u16. The offer grids
  must use an Inventories.dat row that accepts items (row 83 refused every drop; row 0 now). C→S 0xb1 (u32, u8): Cancel
  sent u8 0; Accept assumed u8 1. S→C 0xb9 (u32, u8 accepted) closes the window. The NPC offer is calculated by VendorRecipes from the complete ingredient set; see 24-vendor-recipes.md.
- **0xb8 flags** (struct +0x50): bit 0 (0x01) = the active trade: the UI state's listener `0x140d62ec0` keeps the first
  u32 (struct +0x08, the other party; 0 for an NPC sale) at +0x984 and sets +0x988 = 1. Ctrl + click on an inventory item
  (`0x14030e3e0`, from the grid click handler `0x1402ddbc0` through the grid's vtable +0x4d0) sends 0xb2 only when that
  is set (branch at `0x14030e4dd`); without it the click runs through the other panels' checks and sends nothing. S→C
  0xb9 with the same id clears it (`0x140d62e90`). Bit 4 (0x10) drops the "Mouse over each item to enable accept" rule
  (`0x140c37140`). The Gateway sends 0x11.
- **C→S 0xb2** (16 bytes, write `0x141f95b40`): an item into "Your Offer": u32 BE source inventory (13 = cursor),
  u32 BE item id, u16, u8 x, u8 y, u16 (changes per request). Ctrl + click sends the item's own inventory with
  x = y = 0xff (`0x142166660`): the Gateway puts it in the offer's first free cell, scanned like a pickup into the
  inventory (column by column from the top left). **C→S 0xb3** (12 bytes, `0x142166750`): u32 BE item id, u32 BE grid
  inventory, u16: Shift + click, the item moves to the other side (a click on an offered item sends inventory 0).
  **C→S 0xb5** (12 bytes, `0x142166830`): the same for Ctrl + Shift + click.
- **Accept**: C→S 0xb6 (6 bytes, `0x140c378d0`): u32 BE the revision of the other side's offer inventory, so the
  server knows the player accepted what it showed; C→S 0xb7 (4 bytes) un-accepts. The Gateway accepts when the revision
  is the last one it sent, else treats it as Cancel. **Cancel**: C→S 0xb1 (u32, u8 0).
- **Closing**: S→C 0xb9 (u32 other party, u8 accepted) closes the window (`0x140dcd370`). The client is still in its
  NPC talk afterwards (session +0xd7c0 = 2, +0xd7c4 = the NPC object; live 2026-10-19) and the player cannot move until
  S→C 0x84 with object 0 ends it. Cancel also sends C→S 0x85 for it, Accept sends nothing, so the Gateway sends 0x84
  object 0 after every 0xb9. Verified live 2026-10-19: drops, Ctrl + click (19 items into the first free cells),
  Accept (the NPC's offer lands in the inventory) and Cancel (the items come back), walking afterwards.
- **Offered items keep their server/save cells but are hidden from the client's inventory.** On offer send manager-0
  0xbf removing the original item (or cursor item), then manager-1 0xbf adding it to the player's offer. This provides
  an unambiguous offer-only view without relying on an unreliable native grey imprint. The reserved server cell
  prevents reuse/collisions and keeps the durable item safe across disconnects and crashes. Taking back or cancelling
  sends manager-0 additions with the original ids/cells; confirming sends only reward inventory changes, since offered
  items were already hidden. Closing dialogue or reopening/switching vendor windows cancels the previous sale first.
  Acceptance persists item removal and payment together; a refused sale leaves the offer open. NPC reward previews
  are generated UI items, not paid inventory, and are never granted on cancellation. Player-to-player trade is not
  implemented by this NPC sale path. Coverage: SaleImprintTests (duplicates, currency exclusion, cancel, restart,
  held-item recovery and atomic accepted sale). Live visibility verification pending.
- S→C 0x87 opens the quest reward window (u16 = QuestRewardOffers row, `0x140c2b690`); a wrong row crashes the client.

## 6. Dev commands

`/npc <NPCs row, Id or name> [rotation°]` creates an NPC next to the player, `/npcs` lists this connection's NPCs, and
`/pos` prints the player's cell (the server's estimate) for picking places.

## Open

- Selling: real sell prices; remaining quest reward offers and scripts (above); Respecialisation with refund points;
  real buy prices.
- The NPC component's two bytes, and 0x84's other mode values.
- NPCs outside towns, league NPCs in towns, and quest-stage variants of town NPCs (§2).

## Quest dialogue and first rewards (2026-10-25)

NPCTalk QuestFlag1/2 are required/excluded flags. QuestFlag4 applies when a talk is chosen; QuestFlag5 applies after
Continue. QuestFlag3 controls read/highlight state, not eligibility. QuestFlags and QuestRewardOffers are zero-based:
row 0 is valid, including `a1q1-KilledHillock`. `NEVER_SET` remains an eligibility sentinel but is never applied as an
effect. Eligibility uses the same projected visited/access flags sent to the client, plus saved character flags.

Implemented offers are limited to 0, 1, 2 and 8: their class-filtered level-one gem choices, and offer 1's normal
flasks at their row's item level (RewardLevel, QuestRewards' rarity column 0; [12](12-quests.md) §7). Offer 8's magic
armour choices need item construction not implemented here. Nessa's Brutus conversation sets `NessaChatBrutus`,
enabling her prison reward; claiming sets `a1q2-NessaRewardTaken`. Tarkleigh's Hillock reward sets
`a1q1-QuestRewardTaken`. Inventory addition and the taken flag commit in one save transaction, with a durable duplicate
check and rollback on failure/full inventory. The server removes all choices and ends dialogue after claiming.
Live verified with OfflineWitch: Combustion Support from Nessa and Freezing Pulse from Tarkleigh, including the saved
completion flags. Other quest scripts are not thereby implemented.

## Community stock cross-check (2026-11-02)

Research snapshot: [community-stock.json](../../../scripts/re/vendor-stock/community-stock.json).
Each row identifies vendor, quest, class and gem, with separate source-presence flags.
These are factual community observations, not runtime configuration or verified 3.29.3b behavior.
Sources: [PoE Wiki vendor rewards](https://www.poewiki.net/wiki/List_of_vendor_rewards) and
[PoEDB Quest Vendor Rewards](https://poedb.tw/us/QuestRewards).
PoEDB has quest/class columns but no NPC column; vendor names come only from the wiki.
Counts below count gem/class entries, not distinct gems. Disputed means present in only one source.
Not covered means PoEDB has no matching quest/class group; it is not evidence of unavailability.

| Vendor | Quest condition reported by wiki | Agree | Disputed | Not covered |
|---|---|---:|---:|---:|
| Clarissa | Lost in Love | 155 | 26 | 0 |
| Clarissa | Sever the Right Hand | 214 | 22 | 0 |
| Lilly Roth | Fallen from Grace | 0 | 0 | 3038 |
| Nessa | Breaking Some Eggs | 108 | 14 | 0 |
| Nessa | Enemy at the Gate | 133 | 8 | 0 |
| Nessa | Mercy Mission | 57 | 8 | 0 |
| Nessa | The Caged Brute | 215 | 15 | 0 |
| Nessa | The Siren's Cadence | 183 | 11 | 0 |
| Petarus and Vanja | Breaking the Seal | 104 | 4 | 0 |
| Petarus and Vanja | The Eternal Nightmare | 192 | 4 | 0 |
| Siosa | A Fixture of Fate | 215 | 2530 | 0 |
| Yeena | Intruders in Black | 93 | 68 | 0 |
| Yeena | Sharp and Cruel | 165 | 11 | 0 |
| Yeena | The Root of the Problem | 1 | 205 | 0 |

Total: 7,799 entries covering 468 gem names; 1,835 agree, 2,926 differ, 3,038 lack a PoEDB comparison.
An agreement covers membership only, not quest timing, price, gem level, NPC identity or client-version compatibility.

| Condition | Finding | Confidence / remaining check |
|---|---|---|
| Ordinary campaign gems | Class and quest progression determine stock; quantity is unlimited. | Both stock tables distinguish classes; wiki describes replenishment. |
| Town gem levels | Acts 1–4 scale gem level with character level; wiki gives an approximate required-level ceiling of 50–54. | Approximation only; no exact XP formula established. |
| Siosa | A Fixture of Fate unlocks all-class stock, at gem level 1. | His page says completed-quest stock; Gem page instead describes required level at most 31. Resolve before implementation. |
| Lilly | Fallen from Grace unlocks her gem shop, including hideout access. | Exact exceptional/drop-only exclusions require per-gem review. |
| Equipment stock | Wiki describes item level as player level + 1, capped. | Cap and generation distributions not established here. |
| Equipment refresh | Since 3.21, old items rotate out and new ones arrive on level-up; stock persists for multiple levels. | Do not implement a full reroll on every level-up from older guidance. |
| Purchase versus use | Stock eligibility and item level/attribute requirements are separate concepts. | No blanket attribute-based purchase restriction established by these sources. |
| Prices | Existing flat goods prices and gem tiers are not certified by this stock comparison. | Exact price/level matrix remains unverified. |

Condition sources: [Vendor](https://www.poewiki.net/wiki/Vendor),
[Gem](https://www.poewiki.net/wiki/Gem), [Siosa](https://www.poewiki.net/wiki/Siosa),
[Lilly Roth](https://www.poewiki.net/wiki/Lilly_Roth).
Siosa’s page also says inventory-only payment; this needs version-specific review against local stash-payment behavior.

Reproduce with `python scripts/re/vendor-stock/snapshot.py` from the repository root
(Python packages: requests, beautifulsoup4). The script reads public HTML and writes only the research snapshot.
No extracted game assets, runtime data, quest flags or client archives are changed.
The snapshot is standard campaign research; Ruthless, league vendors and random equipment inventories are not enumerated.

### Official-session inventory observations (2026-11-02)

One level-91 character, user-reported Luminary; native Ascendancy.Name maps it to Characters row 2, Scion.
Exact league name/mode remains unconfirmed.
The user initially reported all quests completed, then clarified Siosa was not unlocked; no Siosa capture exists.
The official-install executable matched the local reference SHA-256 before attachment.
`scripts/re/vendor-stock/capture.py` records 0xc0 and 0x8c reads; `decode.py` validates saved snapshots offline.
The message readers use stream `0x140dfb630`, not the component reader's data/size/position structure.
Discard attempts with stream `+0x252` set: a short read retries the message and its partial buffer is not stock.
Incomplete stream read attempts occur before a successful retry; the final selected payloads passed framing checks.

| Vendor | Items per tab, in received order | Gems |
|---|---|---:|
| Nessa, Act 1 | 44, 108, 48 | 156 |
| Tarkleigh, Act 1 | 18, 27, 16 | 0 |
| Yeena, Act 2 | 45, 71 | 71 |
| Clarissa, Act 3 | 50, 90 | 90 |
| Petarus and Vanja, Act 4 | 50, 61 | 61 |
| Lilly, hideout | 79, 54, 92, 53, 103, 10, 66, 2 | 459 |
| Kirac, hideout | 19 | 0 |

All 21 tabs were sent without selecting each tab: manager 1, inventory kind 71, row 117, grid 12 x 11, flags 0x60.
All seven 0x8c messages have an empty custom-name list; displayed tab labels are not established by that list.
All 1,106 item records passed length, item-count, type-hash, grid-bound and non-overlap checks.
All 837 gem components decoded fully: Lilly's are level 1; campaign gems have nonzero experience and higher levels.
Only one item has explicit Base prices: Kirac's Kalguuran Scarab costs 4 Chaos Orbs.
The other 1,105 prices are not explicit in these payloads; absence of an explicit price does not mean free.
All item components now decode to their exact payload boundaries, including modifiers, rarity, sockets and links.
Equipment records are generated instances, not fixed vendor templates: their bases, rolls and placement are observations.
Their derived prices and requirements describe those rolls; a newly generated item needs its own calculation.
Unlock/refresh behavior remains unverified.
Raw captures and decoded item tables remain under ignored `Tools/re/out/vendor-research/live-2026-11-02/`.
`compare.py` lists differences against every candidate class; membership alone cannot resolve quest or league rules.

Default price resolver `0x1421e8f80` first accepts an explicit Base list, then considers native table costs.
`0x1421e8d70` selects the highest ItemCostPerLevel.Level no greater than Mods item level, or 1 without Mods.
Gems have no Mods component, so their cost row uses level 1 even when the offered gem has experience.
`0x1421ea0b0` selects Cost1/2/3/4 for normal/magic/rare/unique; `0x1421e9fd0` multiplies positive amounts by
stack count, while negative amounts contribute their absolute value without multiplication.
An unidentified magic item adds one Wisdom; an unidentified rare item adds one Chance (`0x1421e9129` onward).
Stack getter `0x1401a3840` resolves the Stack component; all captured non-explicit currency stacks are size 1,
below their CurrencyItems.StackSize, so the full-stack conversion branch does not apply to this sample.
All non-explicit captured items resolve a nonempty ItemCostPerLevel cost; none needs the map fallback.
`prices.py` derives normal-mode prices for 837 captured gems and 268 other items, including the unidentified map.
Full-stack conversion and map fallback behavior remain outside this offline decoder's supported cases.
These prices have static native evidence, but no tooltip or purchase verification in this session.

The captured Scion stock contains every wiki-listed gem for all four campaign gem vendors.
Additional observations: Nessa has Volcanic Fissure; Yeena has Mana-Infused Staff;
Clarissa has Conflagration and Holy Hammers; Petarus and Vanja match the wiki's 61 gems exactly.
These are observed membership differences, not evidence of when each gem unlocks.
`requirements.py` joins every captured item to its use requirements, price evidence and community quest candidates.
809 gem entries have a candidate quest row; 28 have none. Neither case proves an unlock condition.

### Generated equipment and refresh evidence

[Official 3.21 notes](https://www.pathofexile.com/forum/view-thread/3361403) establish partial rotation on level-up:
some older items leave, new items enter, and surviving items remain for multiple levels; rare stock is possible.
This supports retaining generated item instances across openings/partial refreshes, not rerolling every shop open.
Exact retention age, replacement count, base weights, rarity odds and socket/link distributions are not established.
Kirac's refresh policy is separate and was not measured in this session.

All 220 captured campaign equipment/flask items match `item level = town level + 2 - rarity`,
with normal=0, magic=1, rare=2. This is a high-level-character observation, not a verified low-level formula.
The user approved this as the local campaign item-level rule; ordinary generated shop goods now use it.
Explicit templates retain their levels; generated stock applies the local rule in both league modes.
Nessa/Tarkleigh use town level 13; Yeena 23; Clarissa 33; Petarus/Vanja 40 (WorldAreas rows).
Normal items are respectively level 15, 25, 35 and 42; magic items are one lower.
Tarkleigh's and Yeena's one rare item each are level 13 and 23.
This contradicts treating the wiki's town-level cap as a strict cap on every generated item regardless of rarity.
`equipment.py` records counts as sample observations only; none is a configured probability or guaranteed stock size.
The captured roll values must not be imported as permanent shop definitions.

### Client generation helpers versus received stock

Static inspection of the captured 3.29.3b executable found a real mod-value roller at `0x14242e480`.
It counts the selected Mods row's stats through `0x142430710`, gets each min/max pair through `0x142430860`,
and calls inclusive integer RNG helper `0x1404d03d0` with the RNG pointer at context `+0x20` when the bounds differ.
Equal bounds use the fixed value without drawing. The context's byte `+0x18` selects alternate range handling.
The diagnostic at `0x143635840` is "Rolling mod values for mod \"{}\" which is meant to be disabled!".
Direct call instructions were verified at `0x14016e76d`, `0x141d5d7af` and `0x141ea79c1`;
the last is in a function referencing DelveBiomes and biome tags, so this helper is not specific to vendor items.
The caller at `0x141d5ccb0` inspects an existing item's Mods, prefix/suffix parameters and candidate mod rows.
Its full operation and the callers' RNG initialization remain unverified; these are not a recovered vendor generator.

In contrast, received mod instances use `0x142430380`: it reads a 16-bit mod hash, resolves the row,
then reads each rolled value with signed compact reader `0x141e490e0` at `0x1424304d3`.
The official vendor captures contain rarity, item level, visible mod values, socket colours and link groups directly.
They do not require the client to roll those properties from a vendor seed; unidentified hidden affixes are omitted.
This establishes server-supplied item state, not the official server's internal generation implementation.
The selected inventory bodies total 37,539 bytes for 1,106 items across 21 tabs; Tarkleigh's 61 items use 2,464 bytes.
These are decoded message-body sizes, excluding message IDs and transport overhead, not measured network bandwidth.
Base/mod hashes refer to installed data instead of transmitting art, descriptions or full table rows.

The mod-value helper is a reference for porting and differential checks; direct invocation needs native table handles,
allocator-owned output and initialized RNG/context. No headless native ABI or complete vendor-generation path is verified.
Keep vendor authority in the server; do not substitute this helper for unknown base-selection, rarity or refresh policies.

### Local ordinary-vendor policy (3.29.3b)

`vendor-stock.json` joins the reviewed wiki snapshot to native quest offers and NPC shop sets.
Captured Scion membership resolves four missing wiki entries, but their unlocks are assumptions:
Volcanic Fissure at The Caged Brute, Mana-Infused Staff at Sharp and Cruel,
Conflagration and Holy Hammers at Sever the Right Hand.
The captured 459-gem Lilly list is treated as all-class after Fallen from Grace; Siosa uses A Fixture of Fate.
Later campaign gem vendors use the union of the first four acts' unlocked class stock.
Act 1–4 vendors level gems against character level capped at 52; Siosa/Lilly sell level-one gems.
These later-vendor and gem-level policies have not been measured across progression.
Buying does not require the attributes/level needed to equip or use the item.

Unmeasured generation choices are local policy: weapon vendors have 60 finite slots, magic vendors 40;
normal/magic/rare odds are 68/30/2%, with no rare flasks. Classes rotate through eligible native bases,
then a base is selected within 15 drop levels of that class's highest eligible base.
The low-level extension is `max(1, min(character level, town level) + 2 - rarity)`.
Quality has a 10% chance of a uniform 1–20 roll. Socket weights are 50/120/100/30/1 for 1/2/3/4/6,
subject to the existing item socket cap; each neighboring link has 50% chance.
Affixes, implicit ranges and socket colours use the existing server item generator and native data.
One third of finite slots expires per level, retaining each offer for three levels after replacement.
Purchases remain sold until that slot expires. Reopening/relogging does not reroll surviving instances.
Currency and gem offers replenish; Siosa accepts inventory currency only.
Prices come from native item costs, including all currencies and unidentified surcharges, with explicit prices first.
Tabs use the existing generic vendor grid; precise official placement is not reproduced.

KiracLevels.dat contains 16 progression rows: MapAreaLevelOffered, MapCompletion, ShopRerollCost.
The first rows are (68,0,1000), (69,1,1500), (70,3,2000), (71,6,2500), (72,12,3000).
This supports native thresholds and costs rather than an assumed highest-tier ±1 rule.
Interpreting MapCompletion as distinct completed layouts is a local assumption, not a recovered server algorithm.
Account/league storage records those layouts and highest completed tier independently; pre-existing character Atlas
records do not contain reliable tier history and are not migrated into the new progression record.
Completion currently follows the local unique-monster approximation in the owner's active map.
Kirac starts with tier-one maps and refreshes when a completion threshold unlocks a higher offered tier.
Stock is shared per account/league: 17 maps (at least one of each unlocked tier), the captured four-Chaos
Kalguuran Scarab and replenishing Unmaking orbs. Map item level 82 comes from the capture.
The map assortment/automatic threshold refresh are assumptions; exact official weights remain unknown.
`/vendor_refresh` while Kirac's shop is open charges the native progression-based Gold cost atomically.
The native refresh-button packet is not mapped yet; the command is the local entry point.
