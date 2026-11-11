# Contributing to LocalExile

People and AI agents are both welcome. Start with [AGENTS.md](AGENTS.md): the rules, then
[Agents/Status.md](Agents/Status.md) (what works) and [Agents/Roadmap.md](Agents/Roadmap.md) (what to build next and
how to pick up an item). The protocol reference and the notes are in
[Agents/docs/](Agents/docs/README.md).

## For people

- Open up a Frontier Model (such as Claude Opus 5.5) and tell them to set-up this project on your machine.
- Tell them to read all the Agents/ folder and all the MD files.

## Getting Started

**Full guide: [Agents/GettingStarted.md](Agents/GettingStarted.md).** It covers every step with its expected output, the edge cases
and a troubleshooting table. It is written for people and for AI agents setting this up.

In short (Windows, .NET 10 SDK, a standalone Path of Exile **3.29.3c** client; Linux is covered in the full guide):

```powershell
dotnet build GameServer.slnx
robocopy "<your PoE 3.29.3c folder>" Client /E; Copy-Item Client\PathOfExile.exe Client\Game.exe
dotnet run --project src/GameServer.Auth -- keygen Client/Game.exe
dotnet run --project src/GameServer.Auth -- patch Client/Game.exe Client/Game.patched.exe
dotnet run --project src/GameServer.Patch -- gateway --login 127.0.0.1:20493
# needs Node.js (+ npm install in Tools/ooz) and Python 3.9+: live.ps1 extracts the game data first (scripts/dev/extract-data.ps1)
# create an account + character: GameServer.Host and GameServer.DevClient (Agents/GettingStarted.md, step 8)
.\scripts\dev\live.ps1 -Walkmaps   # -Walkmaps is optional: needs `pip install frida`, captures walk maps
```

**Always start the client through `live.ps1`.** Started any other way, the client contacts the official patch servers
and may update itself.

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
| `GameServer.Modding` | the loader contract mods and modules build on: `IMod`, `IModule`, `League`, the context | |
| `LocalExile` | the player launcher (command line) shipped in releases | |
| `Modules/` | game systems, loaded for the leagues that list them: `Core/` (the current game), `Legacy/` (GGG's past leagues), `Custom/` (custom leagues) | |
| `Leagues/` | leagues, each a set of modules | |
| `Mods/` | the user's opt-in mods ([LocalExile-Mods](https://github.com/LocalExile/LocalExile-Mods)), reloaded while the Gateway runs | |

Which of these a change belongs in: [AGENTS.md](AGENTS.md) ("What goes where").
