# Contributing to LocalExile

People and AI agents are both welcome. Start with [AGENTS.md](AGENTS.md): the rules, then
[Agents/Status.md](Agents/Status.md) (what works) and [Agents/Roadmap.md](Agents/Roadmap.md) (what to build next and
how to pick up an item). The protocol reference and the notes are in
[Agents/docs/](Agents/docs/README.md).

## For people

- Open up a Frontier Model (such as Claude Opus 5.5) and tell them to set-up this project on your machine.
- Tell them to read [AGENTS.md](AGENTS.md) first, check open GitHub Issues (`Open To Overtake`, `help wanted`, or `bug`), and read only the specific `Agents/docs/` file for the area being modified—never bulk-read all files in `Agents/`.

## Getting Started

**Full guide: [Agents/GettingStarted.md](Agents/GettingStarted.md).** It covers every step with its expected output, the edge cases
and a troubleshooting table. It is written for people and for AI agents setting this up.

In short (Windows, .NET 10 SDK, a standalone or Steam Path of Exile **3.29.3c** or **3.29.3d** client; Linux is covered in the full guide):

```powershell
dotnet build GameServer.slnx
robocopy "<your PoE folder>" Client /E; Copy-Item Client\PathOfExile.exe Client\Game.exe
dotnet run --project src/GameServer.Auth -- keygen Client/Game.exe
dotnet run --project src/GameServer.Auth -- patch Client/Game.exe Client/Game.patched.exe
dotnet run --project src/GameServer.Patch -- gateway --login 127.0.0.1:20493
# needs Node.js (+ npm install in Tools/ooz): live.ps1 extracts the game data it needs first (scripts/dev/extract-data.ps1 -Servers)
# create an account: GameServer.Auth register, then the character in the game (Agents/GettingStarted.md, step 8)
.\scripts\dev\live.ps1 -Walkmaps   # -Walkmaps is optional: needs `pip install frida`, captures walk maps
```

**Always start the client through `live.ps1`.** Started any other way, the client contacts the official patch servers
and may update itself.

## Pull Request Policy

- **Batching & micro-PRs:** Non-maintainers are capped at 3 concurrent open PRs (including drafts). Standalone small PRs are accepted for reproducible bugs, crashes, protocol fixes, or build/CI breaks. Group other changes—such as doc updates, bug fixes, Acts, Combat, or multiple categories—into one batched PR (one commit per change); unbatched drive-by PRs may be closed with a request to batch.
- **Drafts & follow-through:** Keep track of open PRs, reply to review comments, and mark drafts ready once done. Do not open ready PRs as drafts (drafts are not merged and auto-close after 7 days of inactivity).
- **Evidence & accountability:** Gameplay, world, and protocol changes require verification evidence (live client via `scripts/dev/live.ps1`, logs, `scripts/dev/campaign.ps1`, or protocol/RE reference). Test changes in-game directly rather than relying solely on an AI model, check edge cases and performance regressions, and answer review questions directly. Blunt review feedback or closing a PR is about codebase health, never personal.
- **Issue-first & claiming:** Check open issues and PRs before starting work, comment on an issue to claim it (release the claim if dropped), and link `Closes #N` in the PR description.

## Projects

| Project | Role | Port |
|---|---|---|
| `GameServer.Auth` | login server; also generates keys and patches the client to trust them | 20493 (login), 127.0.0.1:20495 (control) |
| `GameServer.Gateway` | instance (game) server | 6112 |
| `GameServer.Patch` | local patching server, so the client never contacts the real patch servers; also edits the GGPK gateway list | 12995, http 8080 |
| `GameServer.Host` | dev account service over JSON lines (register accounts, create characters); Waypoint's Accounts tab uses it | 7000 |
| `GameServer.Chat` | optional public chat server that relays global, trade and whisper lines between Gateways | 6113 |
| `GameServer.Core` | protocol, crypto, accounts, instance registry | |
| `GameServer.Data` | GGPK / bundle / `.dat` readers | |
| `GameServer.Storage` | account and save storage | |
| `GameServer.Modding` | the loader contract mods and modules build on: `IMod`, `IModule`, `League`, the context | |
| `LocalExile` | the player launcher (command line) shipped in releases | |
| `Modules/` | game systems of the current game (`Core/`), loaded for the leagues that list them; a past league or custom content is a mod | |
| `Leagues/` | leagues, each a set of modules | |
| `Mods/` | the user's opt-in mods ([LocalExile-Mods](https://github.com/LocalExile/LocalExile-Mods)), reloaded while the Gateway runs | |

Which of these a change belongs in: [AGENTS.md](AGENTS.md) ("What goes where").
