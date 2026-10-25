# Roadmap: Quests

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/quests.md](../status/quests.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 2. The world

### Quests: Breaking Some Eggs (a1q4), the Submerged Passage

- **Known:** the whole chain is in [12](../docs/protocol/12-quests.md): three Rhoa Nest chests drop three glyphs, the
  Glyph Wall takes them, quest flag `a1q4-PlacedGlyphs` removes the client-side `SunkenPassageBlocker` wall (the
  invisible wall in front of the cave today) and the wall and waterfall play their Transitionable animations.
- **Needs:** items (Roadmap "Items and inventories"), the Chest component's blob, a Transitionable state change.
  The S→C quest flag message is known: 0x2ba to the Player component, update flag 0x02
  ([16](../docs/protocol/16-waypoints.md) §3).
- **Start:** `/quest_flag a1q4-PlacedGlyphs` (it reaches the client's quest flags, [12](../docs/protocol/12-quests.md)
  §5): check that it alone opens the passage.
  Then a quest script for a1q4.
- **Done when:** the glyph quest can be played through and the Submerged Passage opens.

## 3. Gameplay

### Quests

- **Known:** the quest engine and a1q1 "Enemy at the Gate" ([12](../docs/protocol/12-quests.md) §5–6): quest flags
  saved per character, one script per quest (events → flags; flags → exits), S→C 0x2ba steps a Transitionable.
- **Start:** the next quests act by act, each checked against the real game's behavior.
  The Strand's visual pieces (the gate's animation, the Doorman;
  [12](../docs/protocol/12-quests.md) §6) are low priority. a1q4's Glyph Wall (§1–4) is the next world-changing one.
- **Done when:** the campaign's quests can be played through, with their world changes, logs and rewards.
