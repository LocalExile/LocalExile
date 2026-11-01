# Status

Where the project stands, against client **3.29.3b**.
What to build next is in [Roadmap.md](Roadmap.md).
How the protocol works is in [docs/README.md](docs/README.md).

The status is split into one file per area, so that pull requests in different areas don't edit the same lines.
Each file has the same three sections: Works (verified with the real client), Partly working, and Not working yet.

| Area | Status | Roadmap |
|---|---|---|
| Setup, tooling and tests | [status/setup-and-tooling.md](status/setup-and-tooling.md) | [roadmap/setup-and-tooling.md](roadmap/setup-and-tooling.md) |
| Login, characters and saves | [status/login-and-characters.md](status/login-and-characters.md) | |
| World, movement and the network | [status/world.md](status/world.md) | [roadmap/world.md](roadmap/world.md) |
| Monsters | [status/monsters.md](status/monsters.md) | [roadmap/monsters.md](roadmap/monsters.md) |
| Quests | [status/quests.md](status/quests.md) | [roadmap/quests.md](roadmap/quests.md) |
| NPCs, shops and chat | [status/npcs-and-chat.md](status/npcs-and-chat.md) | [roadmap/npcs-and-chat.md](roadmap/npcs-and-chat.md) |
| Items, currency and crafting | [status/items-and-crafting.md](status/items-and-crafting.md) | [roadmap/items-and-crafting.md](roadmap/items-and-crafting.md) |
| Stash | [status/stash.md](status/stash.md) | [roadmap/stash.md](roadmap/stash.md) |
| Hideouts, crafting stations and maps | [status/hideouts-and-maps.md](status/hideouts-and-maps.md) | [roadmap/hideouts-and-maps.md](roadmap/hideouts-and-maps.md) |
| Microtransactions and cosmetics | [status/microtransactions.md](status/microtransactions.md) | |
| Skills, auras, stats and combat | [status/skills-and-combat.md](status/skills-and-combat.md) | [roadmap/skills-and-combat.md](roadmap/skills-and-combat.md) |
| Gems | [status/gems.md](status/gems.md) | [roadmap/gems.md](roadmap/gems.md) |
| Experience and the passive tree | [status/progression.md](status/progression.md) | [roadmap/progression.md](roadmap/progression.md) |

## Editing the status

Update the area's file in the same pull request as any change that makes something work, or that finds something
broken.
These rules keep pull requests from conflicting with each other:

- Put the entry in its area's file, in the section for its state.
  Never add notes at the top or bottom of a file, or to this index.
- When a feature changes state, edit or move its existing entry instead of adding a new dated note about it.
- Keep an entry to one line: what works and where its doc is. Mechanics go in the doc, and evidence in the pull request.
- Start each sentence on a new line, and keep lines under 120 characters.
  Don't rewrap lines you didn't change.
- Don't add "last updated" dates or totals that every pull request would change.
- A new area gets its own file in `status/` (and `roadmap/`) and a row in the table above.
