# Roadmap: Quests

Part of the [Roadmap](../Roadmap.md); what already works in this area is in
[status/quests.md](../status/quests.md).
Items keep the Roadmap's priority groups, and are in rough priority order within each group.

## 3. Gameplay

### Quests

- **Known:** the quest engine with declarative tables per quest, Acts 1–10 and the Epilogue
  ([12](../docs/protocol/12-quests.md) §5–19), the oracle (`QuestSimulator`, with no test running it yet); adding an act is a
  file of tables ([campaign quests](../docs/game/campaign-quests.md) "Adding an act").
  Eligible ordinary NPC introductions, quest continuations and rewards share that engine and its saved flags;
  native checks cover Nessa, Bestel and Tarkleigh, while contextual later-act dialogue still needs coverage.
- **Start:** a run of the whole campaign on the modules with the client: Acts 2 to 10, the Epilogue and the Pantheon
  have not had one yet (check the Pantheon panel highlight, Divine Vessel soul capture and Sin's "Unlock Power":
  [pantheon](../docs/game/campaign/pantheon.md) §1). Wire campaign league introductions and
  encounters (Vaal Side Areas, Bestiary/Einhar, Blight/Cassia, Delirium Mirrors) once their league modules land.
  The Strand's gate animation ([12](../docs/protocol/12-quests.md) §6) and a1q4's waterfall animation (§4) are low priority.
- **Done when:** the campaign's quests can be played through, with their world changes, logs and rewards.
