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
