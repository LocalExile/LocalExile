# Status: NPCs, shops and chat

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/npcs-and-chat.md](../roadmap/npcs-and-chat.md).

## Works (verified with the real client)

- Chat commands `/commands` (`/com`; paged, `/com <name>` for one), `/raw <hex>`, `/set_stat`, `/set_speed`,
  `/set_level <1 to 100>` (saved to the account, sent in place), `/add_passive_points <n>`, `/add_passive <id>`
  (replies as chat lines).
  Code: `Program.Commands.cs` (`Commands`), `ChatCommands.cs`.
  Docs: [10](../docs/protocol/10-chat-and-commands.md).
- Chat lines and channels: what the player types comes back as a chat line from the character (S→C 0x17), local in
  the local colour, `#` global as "#Name: text" and `$` trade as "$Name: text".
  The global and trade channel joins (C→S 0x134, after entering an area and for `/global N` and `/trade N`) are
  answered with S→C 0x135 and "You have joined global chat channel N.", so they no longer time out and the Global and
  Trade tabs work.
  The client joins once per session, so every area entry sends 0x135 for the channels already joined.
  One player per server: nobody else sees the lines; party, guild and whisper lines are not echoed.
  Code: `Program.Chat.cs`, `ChatChannels.cs`.
- NPCs in every campaign town (acts 1-10, Oriath, the Karui Shores): each town room's own NPC lines, placed as the
  client places the room (checked against its walk-map capture of every town), with name, minimap icon, model and
  facing.
  League NPCs and later quest stages are not placed ([17](../docs/protocol/17-npcs.md) §2).
  Clicking walks to them and opens the dialogue window (S→C 0x84); talk options play (0x82 → 0x84 + index);
  Continue and Goodbye (C→S 0x85) are answered; Purchase Items opens the shop (S→C 0xc0 manager 1 + 0x8c) and buying
  (C→S 0x88) costs 1 Scroll of Wisdom; Sell Items: the sell window (S→C 0xb8) takes drops, Ctrl + click (first free
  cell) and Shift + click, Accept pays the quoted reward and Cancel returns them, and the player can walk
  afterwards; Respecialisation opens the character's passive tree (0x7b kind 0x5f), where Apply refunds (C→S 0x36) and
  Refund All (0x37) refund passives for the client's gold price;
  the normal tree's Apply refunds (0x36 with flag 0) spend saved refund points;
  Identify Items identifies the inventory for free, prints "N Items identified" in chat (S→C 0x17) and grays the
  option; `/npc`, `/npcs`, `/pos`.
  Dialogue options now enforce their required/excluded quest flags and save their start/finish flags.
  Code: `InstanceNpcs.cs`, `NpcCatalog.cs`, `TownNpcs.cs`, Gateway (`SpawnTownNpcs`, `TalkToNpc`).
  Docs: [17](../docs/protocol/17-npcs.md).

## Partly working

- Quest reward offers 0 and 8 open the native reward inventory and grant class-filtered normal level-one gems.
  The item and claimed flag are saved together, with duplicate, prerequisite, full-inventory and save-failure guards.
  Tarkleigh's Freezing Pulse and Nessa's Combustion Support claims were checked live on a disposable character.
  Other offers and offer 8's magic-armour choices remain unimplemented.
  See [quest rewards](../docs/protocol/17-npcs.md#quest-dialogue-and-first-rewards-2026-10-25).
- NPC sale visibility: offered items are hidden from the main inventory and shown only in Your Offer.
  Their original cells/items remain reserved in the durable save;
  take-back/cancel restores the view, disconnect/crash preserves items, and acceptance exchanges items/payment in one
  save.
  Player-to-player trade remains unimplemented.
  Recipe quotes cover rare sets, gem transformations, socket/quality salvage, upgrades and common equipment recipes.
  Acceptance pays the cached outputs atomically, refuses a full inventory and preserves ingredients on save failure.
  Exact affix prices and special league/unique recipes remain partial; real-client recipe verification is pending.
  See [vendor recipes](../docs/protocol/24-vendor-recipes.md) for supported cases and explicit limitations.
  Live visibility verification pending; see protocol/17-npcs.md.
- **Nessa's shop tabs:** Goods contains her supplies and the legacy MapWorlds catalogue, with automatic extra pages.
  Ordinary maps are white and deduplicated by name; 28 unique names have unique rarity, name and artwork.
  Full unique modifier sets and unique-area routing remain open.
  Gem tabs remain Red / Green / Blue Skills and Support and White Skills, 1 Scroll of Wisdom each.
  Shops send one inventory per tab and their names in 0x8c.
  See [17](../docs/protocol/17-npcs.md) §5.

## Not working yet

- NPC dialogue options, shops, league NPCs in towns, quest-stage variants of town NPCs, NPCs outside towns
  ([17](../docs/protocol/17-npcs.md) "Open");
  the 346 monster varieties with NPC, ControlZone, BlightTower … components.
- `data/room-points.json` gives angle 0 to the server objects (NPCs, stashes …) of version 35 and 36 rooms, every town
  room among them: `RoomFile` (`src/GameServer.Data/Terrain`) reads the third number of a line, which those versions
  fill with a 0, and `MonsterPacks.LoadObjects` passes it on.
  Town NPCs do not use it (`TownNpcs` copies the angles); the Twilight Strand's force walls are older rooms and keep
  theirs ([17](../docs/protocol/17-npcs.md) §2).
