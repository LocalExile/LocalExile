# Important Notice
It's important to note that LocalExile is not an official Grinding Gear Games product, and it is not affiliated with or endorsed by Path of Exile or Grinding Gear Games. LocalExile does not in any case sponsor nor support illegal public servers. If you use this project to run an illegal public server and not for testing and learning it is your own personal choice.

This project is for fun and education only. It is intended for local-gameplay with no internet much like MMO Solutions (TrinityCore, AzerothCore, MangOS).
Everything here is made by Claude Opus 5.5. You can pull-request and are encouraged to. I'm running only on two pro subs
and Im broke and unemployed, so help out folks.

[Join Discord](https://discord.gg/pd2Fd5M6Hd)

# To play
Download the zip from [Releases](https://github.com/LocalExile/LocalExile/releases), extract it, and run `LocalExile.exe`.
You need your own standalone Path of Exile 3.29.3b client, and nothing else to install.
Always start the game with `LocalExile.exe`: any other way can update the client, which breaks the setup.

Licensed under [AGPL-3.0](LICENSE); bundled components are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

# To develop [Humans]
- Open up a Frontier Model (such as Claude Opus 5.5) and tell them to set-up this project on your machine. 
- Tell them to read all the Agents/ folder and all the MD files.

# LocalExile
A local, from-scratch server for the Path of Exile **3.29.3b** client. It is written in C# (.NET 10) from scratch to serve 
the client's network protocol. You play on your own machine against servers running on 127.0.0.1.

This project is not affiliated with or endorsed by Grinding Gear Games. It contains **no game files, keys or game data**.
You need your own installed copy of the client, and everything the servers need is generated locally from it.

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

- **Login:** the client's custom TCP login (Diffie-Hellman key exchange, DSA-signed server key, Salsa20), accounts and
  the character list.
- **Instance server (Gateway):** encryption, area load, player object, inventories, latency acks, visibility and
  click-to-move.
- **Lioneye's Watch → The Coast:** the exit loads the area. Ctrl+click opens the Instance Manager (list, join or create
  instances). Towns have one instance, and empty instances expire after 6 minutes.
- **Chat commands:** `/help`, `/set_speed <percent>`, `/set_stat <key> <value>`, `/raw <hex>`,
  `/spawn_mob <monster>[@level] [friendly] [rarity] [name]` (a monster next to you that chases you), `/despawn`.

Not done yet: skills and combat, server text as chat lines, exit to character selection, and exits
beyond town. Details: [Agents/Status.md](Agents/Status.md).

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
| `GameServer.Storage` | JSON account store | |

## Getting Started

**Full guide: [Agents/GettingStarted.md](Agents/GettingStarted.md).** It covers every step with its expected output, the edge cases
and a troubleshooting table. It is written for people and for AI agents setting this up.

In short (Windows, .NET 10 SDK, a standalone Path of Exile **3.29.3b** client):

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
