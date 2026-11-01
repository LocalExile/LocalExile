# Roadmap: NPCs, shops and chat

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/npcs-and-chat.md](../status/npcs-and-chat.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 2. The world

### Social follow-ups

- **Known:** friends can be added, accepted and removed through the native Social UI.
  Relations are account-level and persisted; see [28](../docs/protocol/28-player-trade.md) §6.
- **Start:** friend notes, ignore lists and status editing; verify presence across separate Gateway processes.
- **Done when:** these remaining Social-panel actions persist and update the native UI for both accounts.

### NPCs: dialogue options, shops and the other towns

- **Known:** NPC objects, every campaign town's NPCs and the dialogue window (S→C 0x84, C→S 0x85) work
  ([17](../docs/protocol/17-npcs.md)).
  The client picks the NPCTalk row itself (`0x141e76d40`).
  Dialogue quest flags and the class gem choices in reward offers 0 and 8 are implemented and live-checked.
- **Start:** the remaining quest reward offers and offer 8's magic-armour choices.
  NPCDialogueWindow's option handler is `0x140723d60` / `0x14071f6d0`.
  Shops from NPCShop / ShopSets.
  Common vendor recipes and transactional quoted settlement are implemented.
  Verify quote/acceptance in the real client; complete special league/unique recipes and audit all affix sale prices.
  See [vendor recipes](../docs/protocol/24-vendor-recipes.md) for the remaining scope.
  Nessa now stocks the deduplicated legacy MapWorlds catalogue and unique map identities.
  Complete unique modifiers and unique-area routing remain separate follow-up work.
  Quest-stage variants of town NPCs (Dialla withered, Innocence in Bannon's place, Lilly in town) and the league NPCs
  of the town rooms (`TownNpcs`, [17](../docs/protocol/17-npcs.md) §2).
- **Done when:** talk options play their text, Nessa and Tarkleigh trade, and town NPCs follow quest progress.
