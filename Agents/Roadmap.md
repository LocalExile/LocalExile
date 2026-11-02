# Roadmap

What to build next, for anyone (person or agent) who wants to help. What already works is in [Status.md](Status.md).

The roadmap is split into one file per area, the same areas as the status (see the table in
[Status.md](Status.md)), so that pull requests in different areas don't edit the same lines:

- [Setup, tooling and tests](roadmap/setup-and-tooling.md)
- [World, movement and the network](roadmap/world.md)
- [Monsters](roadmap/monsters.md)
- [Quests](roadmap/quests.md)
- [NPCs, shops and chat](roadmap/npcs-and-chat.md)
- [Items, currency and crafting](roadmap/items-and-crafting.md)
- [Stash](roadmap/stash.md)
- [Hideouts, crafting stations and maps](roadmap/hideouts-and-maps.md)
- [Skills, auras, stats and combat](roadmap/skills-and-combat.md)
- [Gems](roadmap/gems.md)
- [Experience and the passive tree](roadmap/progression.md)

Each file keeps the priority groups (1. Quality of life, 2. The world, 3. Gameplay, 4. Server design, 5. Project) of
its items, and items are in rough priority order within each group.
"Known" says what is already found; "Start" says where to look.

## How to pick up an item

1. Open a GitHub issue named after the item (or comment on an existing one) so two people don't do the same work.
2. Read the docs the item links to. Most items start with reverse engineering: find how the client reads a message,
   then send it from the server. [docs/re/tooling.md](docs/re/tooling.md) has the workflow and recipes.
3. Verify with the real client through `.\scripts\dev\live.ps1`, and check the logs in `logs\`. Add tests for any
   message layout you implement (see `tests/GameServer.Core.Tests`).
4. In the same pull request: note what the code cannot tell in `docs/protocol/` (new message ids, layouts, client
   addresses in [address-index.md](docs/protocol/address-index.md)), keep it short, move the item to its area's file in
   `status/` as one line, and remove or update it in its area's file in `roadmap/`.

The editing rules in [Status.md](Status.md) ("Editing the status") apply to the roadmap files too: put an item in its
area's file and group, edit an item instead of adding a note about it, and start each sentence on a new line.
