# Important Notice
It's important to note that LocalExile is not an official Grinding Gear Games product, and it is not affiliated with or endorsed by Path of Exile or Grinding Gear Games. LocalExile does not in any case sponsor nor support illegal public servers. If you use this project to run an illegal public server and not for testing and learning it is your own personal choice.

This project is for fun and education only. It is intended for local-gameplay with no internet much like MMO Solutions (TrinityCore, AzerothCore, MangOS).
It is built by a community of contributors working with AI agents (such as Claude Opus 5.5). Pull requests are welcome
and encouraged.

[Join Discord](https://discord.gg/pd2Fd5M6Hd)

# To play
Download the zip from [Releases](https://github.com/LocalExile/LocalExile/releases), extract it, and run `LocalExile.exe`.
You need your own standalone Path of Exile 3.29.3b client, and nothing else to install.
Always start the game with `LocalExile.exe`: any other way can update the client, which breaks the setup.

Licensed under [GPL-2.0](LICENSE); bundled components are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

# To develop [Humans]
- Open up a Frontier Model (such as Claude Opus 5.5) and tell them to set-up this project on your machine. 
- Tell them to read all the Agents/ folder and all the MD files.

# LocalExile
A local, from-scratch server for the Path of Exile **3.29.3b** client. It is written in C# (.NET 10) from scratch to serve 
the client's network protocol. You play on your own machine against servers running on 127.0.0.1.

It contains **no game files, keys or game data**. You need your own installed copy of the client, and everything the
servers need is generated locally from it.

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

## What works

An overview; each area's full list (works, partly working, not working yet) is in [Agents/Status.md](Agents/Status.md).
"(partly)" marks features that work but are incomplete or not yet checked end to end with the real client.

- **Login and characters:** the client's own login, accounts and Remember Password; every class; character creation
  in Standard, Hardcore, Ruthless, SSF and Allflame leagues and their variants; deletion; Exit to Character Selection;
  characters, items and skill bars are saved.
- **World:** area transitions in every area, waypoints, Portal Scrolls and the Portal skill, and the Instance Manager
  (Ctrl+click an exit); instances keep their monsters and ground items until they expire; pausing outside towns.
  The client's level generator is ported (partly), as are the Labyrinth and the Trials of Ascendancy (partly).
- **Campaign:** the quest engine runs all of Act 1, with Hillock, Breaking Some Eggs and Mercy Mission played through;
  Acts 2–10 and the Epilogue, the bandits and the Pantheon have been played through with the campaign runner (partly).
- **NPCs and players:** the NPCs of every campaign town with their dialogue, vendors that keep their stock, chat lines
  and channels; trading, parties and friends between players in the same instance.
- **Monsters:** monster packs at the area's level, magic and rare monsters with their mods, monsters that fight back
  with their skills, buried monsters that emerge, hit reactions, and bosses from Hillock to Dominus.
- **Skills and combat:** the default attack, gem skills and bow attacks, spells, auras and buffs (Righteous Fire,
  Pride, Determination, Grace, Discipline), charges, and about 150 character-panel stats (life, mana, regeneration,
  resistances, armour, evasion, block, spell suppression, accuracy, movement speed).
- **Gems:** drops, quest rewards and gem vendors, gem experience and levels, requirements and linked supports (partly).
- **Progression:** experience and levels, the passive tree with masteries, jewel sockets, cluster, timeless and radius
  jewels, and ascendancies.
- **Items and crafting:** loot by monster rarity, gold, life, mana and utility flasks with charges, item mods by
  rarity, influences and other item states, unique items, currency (Orb of Alchemy, Chaos Orb, Jeweller's Orb, Sacred
  Orb, essences) and the crafting bench (partly).
- **Stash and hideouts:** personal and guild stashes with the special tabs; a hideout for every character with hideout
  editing, the crafting bench, Horticrafting Station, Heist Locker, Niko and Kirac; Delve's first steps and the first
  maps (partly).
- **Microtransactions:** every account owns every cosmetic the client can show.
- **Leagues, modules and mods:** leagues (including Survivors and Phrecia) are sets of game modules, and mods
  tweak the game and reload while it runs ([leagues and modules](Agents/docs/dev/modules.md),
  [mods](Agents/docs/dev/modding.md)).
- **Chat commands:** `/commands` lists them all, from `/set_level`, `/spawn_mob` and `/goto` to `/mod` and `/module`.
- **Releases:** a self-contained player zip with the `LocalExile.exe` launcher and a window (`LocalExileGUI.exe`),
  for Windows and Linux.

## Contributing

People and AI agents are both welcome. Start with [AGENTS.md](AGENTS.md): the rules, then
[Agents/Status.md](Agents/Status.md) (what works) and [Agents/Roadmap.md](Agents/Roadmap.md) (what to build next and
how to pick up an item). The protocol reference and the notes are in
[Agents/docs/](Agents/docs/README.md).

## Projects

| Project | Role | Port |
|---|---|---|
| `GameServer.Auth` | login server; also generates keys and patches the client to trust them | 20493 (login), 127.0.0.1:20495 (control) |
| `GameServer.Gateway` | instance (game) server | 6112 |
| `GameServer.Patch` | local patching server, so the client never contacts the real patch servers; also edits the GGPK gateway list | 12995, http 8080 |
| `GameServer.Host` + `GameServer.DevClient` | dev account service (register accounts, create characters) | 7000 |
| `GameServer.Core` | protocol, crypto, accounts, instance registry | |
| `GameServer.Data` | GGPK / bundle / `.dat` readers | |
| `GameServer.Storage` | account and save storage | |
| `GameServer.Modding` | the API that mods and modules build on | |
| `LocalExile` + `LocalExileGui` | the player launcher (command line and window) shipped in releases | |
| `Modules/` | game systems (NPC dialogue, Delve, Heist, Pantheon, ...), loaded by the Gateway | |
| `Leagues/` | leagues, each a set of modules | |
| `Mods/` | small user tweaks, reloaded while the Gateway runs | |

## Getting Started

**Full guide: [Agents/GettingStarted.md](Agents/GettingStarted.md).** It covers every step with its expected output, the edge cases
and a troubleshooting table. It is written for people and for AI agents setting this up.

In short (Windows, .NET 10 SDK, a standalone Path of Exile **3.29.3b** client; Linux is covered in the full guide):

```powershell
dotnet build GameServer.slnx
robocopy "<your PoE 3.29.3b folder>" Client /E; Copy-Item Client\PathOfExile.exe Client\Game.exe
dotnet run --project src/GameServer.Auth -- keygen Client/Game.exe
dotnet run --project src/GameServer.Auth -- patch Client/Game.exe Client/Game.patched.exe
dotnet run --project src/GameServer.Patch -- gateway --login 127.0.0.1:20493
# needs Node.js (+ npm install in Tools/ooz) and Python 3.9+: live.ps1 extracts the game data first (scripts/dev/extract-data.ps1)
# create an account + character: GameServer.Host and GameServer.DevClient (Agents/GettingStarted.md, step 8)
.\scripts\dev\live.ps1 -Walkmaps   # -Walkmaps is optional: needs `pip install frida`, captures walk maps
```

**Always start the client through `live.ps1`.** Started any other way, the client contacts the official patch servers
and may update itself.
