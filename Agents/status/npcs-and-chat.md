# Status: NPCs, shops and chat

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/npcs-and-chat.md](../roadmap/npcs-and-chat.md).

## Works (verified with the real client)

- Chat commands `/commands` (`/com`; paged, `/com <name>` for one), `/raw <hex>`, `/set_stat`, `/set_speed`,
  `/set_level <1 to 100>` (saved to the account, sent in place), `/add_passive_points <n>`, `/add_passive <id>`
  (replies as chat lines).
  Code: `Program.Commands.cs` (`Commands`), `ChatCommands.cs`.
  Docs: [10](../docs/protocol/10-chat-and-commands.md).
- NPCs in Lioneye's Watch: Tarkleigh, Nessa and Bestel at their real-game cells, with name, minimap icon and idle
  model; clicking walks to them and opens the dialogue window (S→C 0x84); talk options play (0x82 → 0x84 + index);
  Continue and Goodbye (C→S 0x85) are answered; Purchase Items opens the shop (S→C 0xc0 manager 1 + 0x8c) and buying
  (C→S 0x88) costs 1 Scroll of Wisdom; Sell Items: the sell window (S→C 0xb8) takes drops, Ctrl + click (first free
  cell) and Shift + click, Accept gives 1 Scroll Fragment per item and Cancel returns them, and the player can walk
  afterwards; Respecialisation opens the character's passive tree (0x7b kind 0x5f), where Apply refunds (C→S 0x36) and
  Refund All (0x37) refund passives for the client's gold price;
  the normal tree's Apply refunds (0x36 with flag 0) spend saved refund points;
  Identify Items identifies the inventory for free, prints "N Items identified" in chat (S→C 0x17) and grays the
  option; `/npc`, `/npcs`, `/pos`.
  Dialogue options now enforce their required/excluded quest flags and save their start/finish flags.
  Code: `InstanceNpcs.cs`, `NpcCatalog.cs`, Gateway (`SpawnTownNpcs`, `TalkToNpc`).
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
  Live visibility verification pending; see protocol/17-npcs.md.
- **Nessa's shop tabs (2026-10-21; the gem tabs verified live, the maps and the tabbed window flag being tested):**
  Goods (her stock, one Beach Map, then every map of tiers 1-16) and Goods2 for the rest, then every shop gem in Red /
  Green / Blue Skills and Support and White Skills, 1 Scroll of Wisdom each, filled row by row.
  Shops send one inventory per tab and their names in 0x8c.
  See [17](../docs/protocol/17-npcs.md) §5.

## Not working yet

- NPC dialogue options, shops, NPCs in other towns and outside towns ([17](../docs/protocol/17-npcs.md) "Open");
  the 346 monster varieties with NPC, ControlZone, BlightTower … components.
  "Emerge" monsters (Gravel Eater …) are created but stay hidden until an Emerge action, which the server does not
  send.
- Chat lines: server text only appears as a message box.
  The chat-line message and the global channel join are unknown (the client logs "Timeout while joining global chat
  channel").
