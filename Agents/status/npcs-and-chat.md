# Status: NPCs, shops and chat

Part of [Status](../Status.md); what to build next in this area is in
[roadmap/npcs-and-chat.md](../roadmap/npcs-and-chat.md).

## Works (verified with the real client)

- Ordinary vendors/Kirac persist stock; hideout Lilly serves new characters: [17](../docs/protocol/17-npcs.md).

- Chat commands `/commands` (`/com`; paged, `/com <name>` for one), `/raw <hex>`, `/set_stat`, `/set_speed`,
  `/set_level <1 to 100>` (saved to the account, sent in place), `/add_passive_points <n>`, `/add_passive <id>`
  (replies as chat lines).
  Code: `Program.Commands.cs` (`Commands`), `ChatCommands.cs`.
  Docs: [10](../docs/protocol/10-chat-and-commands.md).
- Chat lines and channels (the `Chat` module): local lines go to the players of the instance, `#` global, `$` trade and
  `@Name` whispers go through a chat server, so players of separate Gateways can talk.
  Each Gateway runs a local chat server; a mod (`UseChatServer`: `globalchat` in LocalExile-Mods) moves its players to another server, and
  disabling the mod moves them back. When that server fails a player is told in chat and stays local, trying again at an area change a minute later.
  `src/GameServer.Chat` is the server to host; it stores nothing.
  An item linked with Ctrl+Alt+click from an inventory or the stash shows as a link in the line (C→S 0x12, S→C 0x17).
  The global and trade channel joins (C→S 0x134, after entering an area and for `/global N` and `/trade N`) are
  answered with S→C 0x135 and "You have joined global chat channel N.", so they do not time out and the Global and
  Trade tabs work.
  The client joins once per session, so every area entry sends 0x135 for the channels already joined.
  Party and guild lines are not sent. A name already on a chat server gets a number there (`Name_1`); nothing proves a name.
  Code: `Modules/Core/Player/Chat`, `src/GameServer.Core/Chat`.
  Docs: [10](../docs/protocol/10-chat-and-commands.md) §6.
- NPCs in every campaign town (acts 1-10, Oriath, the Karui Shores): each town room's own NPC lines, placed as the
  client places the room (checked against its walk-map capture of every town), with name, minimap icon, model and
  facing.
  Their collision footprints now come from inherited OT metadata, including later-act NPC Id aliases.
  The shared routing and overlap correction covers spawned NPCs in towns and hideouts.
  Native walking respected the three NPCs and both stashes in Lioneye's Watch; the guild stash receives
  its native HaveGuild access flag when locally available, so its client collision matches the server.
  League NPCs and later quest stages are not placed ([17](../docs/protocol/17-npcs.md) §2).
  Niko also stands in Act 4 Highgate at the player's confirmed position and facing.
  Clicking walks to them and opens the dialogue window (S→C 0x84); talk options play (0x82 → 0x84 + index);
  Continue and Goodbye (C→S 0x85) are answered; Purchase Items opens the shop (S→C 0xc0 manager 1 + 0x8c) and buying
  (C→S 0x88) uses item-specific prices; Sell Items: the sell window (S→C 0xb8) takes drops, Ctrl + click (first free
  cell) and Shift + click, Accept pays the quoted reward and Cancel returns them, and the player can walk
  afterwards; Respecialisation opens the character's passive tree (0x7b kind 0x5f), where Apply refunds (C→S 0x36) and
  Refund All (0x37) refund passives for the client's gold price;
  the normal tree's Apply refunds (0x36 with flag 0) spend saved refund points;
  Identify Items identifies the inventory for free, prints "N Items identified" in chat (S→C 0x17) and grays the
  option; `/npc`, `/npcs`, `/pos`.
  Dialogue options now enforce their required/excluded quest flags and save their start/finish flags.
  Code: `InstanceNpcs.cs`, `NpcCatalog.cs`, `TownNpcs.cs`, Gateway (`SpawnTownNpcs`, `TalkToNpc`).
  Docs: [17](../docs/protocol/17-npcs.md).

- **Trade between players** in one instance (two clients, 2026-10-28): the request shows the requester's waiting box
  and the other player's notification, Accept opens the window on both, offered items appear on both sides, Accept
  greys the accepting side's offer for both players, and both accepting swaps the items in one transaction for both saves.
  Cancel, decline, a disconnect or leaving the area return every offered item. See [28](../docs/protocol/28-player-trade.md).
- **Party** between players (two clients, 2026-11-05): "Invite to Party", also from the Friends panel, reaches the
  player in any area; Accept puts both into the party, shown on both clients, and it survives area changes.
  A member who follows another into an area joins their instance.
  The party bonus follows the client's rules (monster life by rarity, currency items) and its icons (S→C 0x231).
  "Leave Party" removes the player, and a party left with one member is disbanded on both clients.
  Decline, kicks and disconnects are open.
  See [28](../docs/protocol/28-player-trade.md) §5.
  Mode integration refuses SSF, different leagues and terminal/departing owners at admission and settlement.
  Composed automated checks cover eligibility, item ownership and rollback when the second inventory save fails.
- **Friends:** the player context menu and Friends-panel name field add an account to the list.
  The other account receives an invitation with Accept/Decline; Unfriend removes the relation from both lists.
  Account GUIDs and invitations are saved transactionally, independently of character and league.
  Native two-client checks confirmed add, invitation display, Accept, Unfriend, offline display and restoration on login.
  Presence updates both ways when an entry finishes (a hideout's too) and at logout; an area change sends none.
  Persistence/reopen, rollback and native packet framing have automated coverage.
  Notes, ignore lists and status editing remain open.
  See [28](../docs/protocol/28-player-trade.md) §6.

## Partly working

- Direct vendor equipment purchases validate placement, preserve payment on failure and refresh equipped items; 3.29.3c belt purchase/swap protocol controls pass; rendered UI checks remain open.

- Faustus's Currency Exchange at official prices works through `/exchange` (orders, claims, cancelling; live 2026-11-08).
  Spreads by volume, a market of 6 steps, fills limited per hour, orders filling while away and with time are written and not yet played.
  Faustus stands in every hideout; his dialogue option there is not yet played.
  See [29](../docs/protocol/29-currency-exchange.md).
- Ruthless selects its native gem/reward restrictions and purchase costs; equipment uses local generation policy.
  Ordinary vendors offer no gems or jewellery; unlocked Siosa exchanges Uncarved Gemstones for eligible active gems.
  Support gems are excluded from deterministic rewards and the opening support chest.
  Supported flask vendors, Helena and Niko refill Ruthless pools and flasks.
  Automated checks cover native Ruthless shop rows, restricted stock and vendor resource/flask refills.
  Other vendor, quest-reward and pricing cases are covered by automated checks.
  See [league modes](../docs/game/league-modes.md).
- Quest rewards open the native reward inventory for every `QuestRewardOffers` row whose items the server can build,
  and grant the class-filtered choices (level-one gems, normal flasks; unbuildable magic armour rows are skipped).
  Eligible ordinary quest talks/rewards follow introductions automatically; Nessa, Bestel and Tarkleigh checked live.
  The item and claimed flag are saved together, with duplicate, prerequisite, full-inventory and save-failure guards.
  Tarkleigh's Freezing Pulse and Nessa's Combustion Support claims were checked live on a disposable character, and
  Tarkleigh's a1q4 Glyph Reward on 2026-10-26.
  See [quest rewards](../docs/protocol/17-npcs.md#quest-dialogue-and-first-rewards-2026-10-25).
- NPC sale visibility: offered items are hidden from the main inventory and shown only in Your Offer.
  Their original cells/items remain reserved in the durable save;
  take-back/cancel restores the view, disconnect/crash preserves items, and acceptance exchanges items/payment in one
  save.
  Recipe quotes cover rare sets, gem transformations, socket/quality salvage, upgrades and common equipment recipes.
  Acceptance pays the cached outputs atomically, refuses a full inventory and preserves ingredients on save failure.
  Exact affix prices and special league/unique recipes remain partial; real-client recipe verification is pending.
  See [vendor recipes](../docs/protocol/24-vendor-recipes.md) for supported cases and explicit limitations.
  Live visibility verification pending; see protocol/17-npcs.md.
- Captured gem grouping is restored; uncaptured classes/vendors use documented ordering, and Kirac refresh UI remains open.
- Nessa's development catalogue (awakened/exceptional gems, generic/originator/unique maps) is the optional
  `NessaDevelopmentTabs` module: after her progression tabs or replacing them, live-checked; no league lists it.
  Standard uses progression stock; full unique modifiers, unique-area routing and the updated development display remain partial.
  See [17](../docs/protocol/17-npcs.md#vendor-evidence-and-local-policy).
- **Player bots** (PlayerBots module, off by default): bots are created over time and visit the towns between quests.
  Players see them walk with their class model and gear.
  Each campaign town maintains 2–5 residents with native level display (Act 1: 1–3; Act 10: 59–75; intermediate acts interpolate the center with ±5).
  Invited bots keep their town-selected level across zones and grow through earned experience, without scaling to monster levels.
  Party followers use ordinary player combat: compatible linked gems, connected passive builds, hostile targeting,
  damage, resurrection and persisted kill/gem experience, shared for melee, projectile and owned kills.
  Observers receive earned experience, level stats and the default level-up flash; active-party reload retains earned XP.
  The native party-menu kick removes any selected bot; `/botinvite <name>` and `/botleave` do the invite and the leave
  from chat, and an invite also matches a bot standing in the player's town that has not been published to them yet
  (a module reload clears that flag until the bot's next update).
  Followers stand calmly within 60 cells of an idle leader, with rare short adjustments, and enter on clear cells ahead of the entrance.
  Moving followers aim ahead along the leader's ordered route and take reachable attacks without chasing off it or cancelling their own swing/cast.
  Gaps over 60 cells get a short catch-up grace before relocation; large jumps return followers immediately.
  Shared deaths hide corpse health bars on each observer, including kills made by controlled party members.
  Optional [MCP control](../../Modules/PlayerBots/MCP.md) exposes per-bot leases, observations and native tactical orders with ordinary following/fallback.
  Party state survives module reload while the leader remains online. Offscreen quests remain simulated;
  trade, chat, ascendancy builds and advanced tactical AI remain open.
  See [player bots](../docs/dev/player-bots.md).

## Not working yet

- Remaining NPC dialogue options, special league shops, league NPCs and quest-stage variants
  ([17](../docs/protocol/17-npcs.md) "Open");
  the 346 monster varieties with NPC, ControlZone, BlightTower … components.
- `data/room-points.json` gives angle 0 to the server objects (NPCs, stashes …) of version 35 and 36 rooms, every town
  room among them: `RoomFile` (`src/GameServer.Data/Terrain`) reads the third number of a line, which those versions
  fill with a 0, and `MonsterPacks.LoadObjects` passes it on.
  Town NPCs do not use it (`TownNpcs` copies the angles); the Twilight Strand's force walls are older rooms and keep
  theirs ([17](../docs/protocol/17-npcs.md) §2).
