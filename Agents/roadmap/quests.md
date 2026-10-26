# Roadmap: Quests

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/quests.md](../status/quests.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 3. Gameplay

### Quests

- **Known:** the quest engine, a1q1 "Enemy at the Gate" and a1q4 "Breaking Some Eggs"
  ([12](../docs/protocol/12-quests.md) §1–6): quest flags
  saved per character, one script per quest (events → flags; flags → exits), S→C 0x2ba steps a Transitionable.
- **Start:** the next quests act by act, each checked against the real game's behavior.
  The Strand's visual pieces (the gate's animation, the Doorman;
  [12](../docs/protocol/12-quests.md) §6) and a1q4's waterfall animation (§4) are low priority.
- **Done when:** the campaign's quests can be played through, with their world changes, logs and rewards.
