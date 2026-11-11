[Join Discord](https://discord.gg/pd2Fd5M6Hd)

# LocalExile
A local, from-scratch server for the Path of Exile **3.29.3c** client. It is written in C# (.NET 10) from scratch to serve 
the client's network protocol. You play on your own machine against servers running on 127.0.0.1.

It contains **no game files, keys or game data**. You need your own installed copy of the client, and everything the
servers need is generated locally from it.

## How to play?
Download the zip from [Releases](https://github.com/LocalExile/LocalExile/releases), extract it, and run `LocalExile.exe`.
You need your own Path of Exile 3.29.3c client (standalone, or Steam's 3.29.3c), and nothing else to install.
Always start the game with `LocalExile.exe`: any other way can update the client, which breaks the setup.

Prefer a launcher? [Waypoint](https://github.com/LocalExile/Waypoint) is a Windows workspace manager for LocalExile:
it downloads the release (or sets up a source checkout), starts and stops the servers and the client, and manages mods,
backups and accounts. It is a separate project, and it starts the game the safe way.

## What works

This overview focuses on gameplay. Feature coverage and remaining limitations are detailed in
[Agents/Status.md](Agents/Status.md).

### Core gameplay

- **Characters:** account login, character creation and selection, character classes, and saved equipment and inventory.
- **World:** movement, area transitions, waypoints, portals and separate area instances.
- **Monsters and combat:** monster packs, magic and rare enemies, attacks, spells, damage and loot drops.
- **Character stats:** Life, Mana, Energy Shield, recovery, resistances, Armour, Evasion and Block, with basic auras
  and buffs.
- **Progression:** experience and levels, the passive skill tree, masteries and jewels. Skill gems gain experience,
  level up and enforce character requirements.
- **Items:** picking up, equipping, moving and stacking items; rarity, basic modifiers and stat recalculation.
  Life, Mana and utility flasks restore resources or grant temporary effects.
- **NPCs and trading:** dialogue, buying and selling items, and vendor stock.
- **Multiplayer:** parties, friends, chat and player-to-player trading.
- **Stash and hideouts:** personal and guild stashes, specialized tabs and personal hideouts.

### Partly implemented

- Quest chains across Acts 1–10 and the Epilogue, scripted story events and individual multi-phase bosses.
- Additional skill and support-gem mechanics, special item effects, currency crafting and the crafting bench.
- Maps and the Atlas, the Labyrinth and Ascendancy mechanics, the Pantheon and the Azurite Mine (Delve).

## Showcase (Most recent to older)
https://github.com/user-attachments/assets/e20dd43d-4453-40d4-8f81-eb902a87817f

Items, NPCs, Stash, Waypoints [(youtube)](https://youtu.be/-QDRFEWCmpU)

https://github.com/user-attachments/assets/fead9d6a-b2ce-4908-9e73-ccaaae78a08a

Mobs, Loot, RF, Level/XP, Passive Tree (better quality showcase is on [youtube](https://www.youtube.com/watch?v=rfnCaXQaxBo))

https://github.com/user-attachments/assets/7b732013-2d04-4e81-a2fa-5437099f4b6d

Spawning mobs in A1 Town

https://github.com/user-attachments/assets/ebb49e70-93aa-464f-a6cd-c6c130dd8c5a

Character select, Lioneye's Watch, taking The Coast exit,
and `/set_speed` in The Coast, all against the local servers.

## Contributing

Want to help build it? See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Licensed under [GPL-2.0](LICENSE); bundled components are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Important Notice
It's important to note that LocalExile is not an official Grinding Gear Games product, and it is not affiliated with or endorsed by Path of Exile or Grinding Gear Games. LocalExile does not in any case sponsor nor support illegal public servers. If you use this project to run an illegal public server and not for testing and learning it is your own personal choice.

This project is for fun and education only. It is intended for local-gameplay with no internet much like MMO Solutions (TrinityCore, AzerothCore, MangOS).
It is built by a community of contributors working with AI agents (such as Claude Opus 5.5). Pull requests are welcome
and encouraged.
