# Agent instructions

LocalExile is a local server for the Path of Exile 3.29.3b client, built by people and AI agents working together.
All agent and contributor documentation is in [Agents/](Agents/). Read these first:

1. [Agents/GettingStarted.md](Agents/GettingStarted.md): setup from a fresh clone, the rules for running the client,
   the third-party tools, troubleshooting.
2. [Agents/Status.md](Agents/Status.md): what works, what is partial, what does not work yet (one file per area in
   `Agents/status/`).
3. [Agents/Roadmap.md](Agents/Roadmap.md): what to build next, and how to pick up an item and hand it back (one file
   per area in `Agents/roadmap/`).
4. [Agents/docs/README.md](Agents/docs/README.md): the protocol reference and reverse-engineering notes.

## Rules

- **Start the client only through `scripts/dev/live.ps1`, and only one at a time.** Any other launch contacts the
  official patch servers and can update the client, which breaks the setup.
- **Run commands from the repository root.**
- **Keep production game-data paths relative to the repository root (`data/`).** Do not add parent-directory
  searching, an absolute `DataFolder`, or catalog-path changes to accommodate tests or one machine.
  Gateway tests use the shared `tests/GameServer.Gateway.Tests/TestEnvironment.cs` module initializer.
  Do not change the process working directory in individual tests; fix the shared test setup if it fails.
- **Write all commit messages and pull request titles and descriptions in English.**
- **Do not add automatic client-archive writes to standard startup.** Feature-specific client data changes need
  separate maintainer review; keep read-only extraction separate from any explicit client maintenance tool.
- **Never commit game files or third-party programs:** `Client/`, `data/` (except `data/schema/`), Ghidra, the JDK,
  `Tools/ghidra-projects`, `Tools/re/out` (decompiled client code). `.gitignore` keeps them out; do not force-add.
- **Never commit secrets:** generated keys (`data/login-keys.json`, `data/instance-secret.bin`), accounts, or
  credentials of the official services.
- **Game data comes from `scripts/dev/extract-data.ps1`** (`live.ps1` runs it). A change that adds a game-data file
  under `data/`, or changes what a generator writes, adds or bumps that file's entry in the script's manifest (and in
  the Gateway's `GameDataFiles.cs`) in the same PR, instead of a "regenerate after pulling" note.
- **Keep the docs current:** when you find or build something, update `Agents/docs/` and move the item from its
  area's file in `Agents/roadmap/` to the one in `Agents/status/` in the same change. Put entries where they belong,
  never at the top or bottom of a file, and edit an existing entry instead of adding a dated note; the rules are in
  [Agents/Status.md](Agents/Status.md) ("Editing the status").
- **Verify with the real client and the logs** (`logs\`), and keep `dotnet test tests/GameServer.Core.Tests`,
  `tests/GameServer.Data.Tests` and `tests/GameServer.Gateway.Tests` passing.
