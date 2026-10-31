# Modules

A module is a big overhaul of the game, such as a league. A [mod](modding.md) is a small modification.

| | Mod | Module |
|---|---|---|
| Is | a tweak: a command, drop or experience changes | an overhaul: a league, a game system |
| Entry point | `IMod`, `IModContext` | `IModule`, `IModuleContext`: all a mod has, and `AddLeague` |
| Folder | `Mods/Source/`, `Mods/Binaries/<id>/` | `Modules/<id>/`: source and build together |
| Manifest | `mod.json` | `module.json` |
| Off | `/mod_disable`, `enabled`, `mod-state.txt` | delete the folder, restart |
| Loads | any time, and unloads | once, when the Gateway starts; never unloads |
| Chat | `/mod`, `/mod_reload`, `/mod_enable`, `/mod_disable` | `/module`, `/module_reload` |
| Settings | `Game_MODS`, `Game_MODS_FOLDER` | `Game_MODULES`, `Game_MODULES_FOLDER` |

## A module's life

- **Off:** delete `Modules/<id>/` and restart Auth and the Gateway. There is no command or state file. A deleted folder has no effect
  on a running Gateway: the module stays until it stops, and a new folder waits for the next start.
- **Hot reload:** a new build in `Modules/<id>/bin/`, an edited `module.json`, a saved loose `.cs` file or `/module_reload [id]` reloads a
  loaded module and those that depend on it; their state starts over. Bad new files (a failed compile, a half-written build) leave
  the running module as it is, and a new build that throws while loading goes back to the assemblies it had. Saving a project's
  source does nothing until it is built: `dotnet build Modules\<id>`.
- `live.ps1` builds every project in `Modules\`.
- `dotnet build`, a `.csproj` and `"assembly": "bin/<id>.dll"`, or loose `.cs` files implementing `IModule` and no project.

Everything else works as for a [mod](modding.md): the manifest's other fields and the hooks. Hooks of modules run before those of
mods, and a module's command wins over a mod's of the same name. A mod cannot add a league, and `kind: league` in a `mod.json` is
refused.
Code: `ModHost.cs` (one class, two instances: `Program.ServerModules`, `Program.ServerMods`), `ServerHooks.cs`, `ServerLeagues.cs`,
`Features/`, `Modules/`.

To make one, copy `Modules/allflame/`, rename the folder and the `.csproj`, and set the id and `assembly` in its `module.json`. The
folder's name is the id, in lower case.

## Leagues

Every league is a module, Standard too, and an upcoming league changes
the framework only to add something generic that any league could use: a rule, a hook, a declaration field. Never its name.
What still says Ruthless in `src/` is the client's wire names (`LeagueEntry.RuthlessParent`, its flag) and the game tables' column
names (`IndexRuthless`, `ShopRuthless`, `RuthlessValue`).

A league has two halves, because the Auth server gives the client its list and does not load modules:

**In `module.json`, what Auth reads when it starts.**

```json
"league": {
  "bases": ["Necropolis"],
  "form": {
    "standardNames": { "plain": "Grim", "hardcore": "Hardcore Grim", "ssf": "SSF Grim", "hardcoreSsf": "SSF Hardcore Grim" },
    "challengeNames": { "plain": "Grim {base}", "hardcore": "HC Grim {base}", "ssf": "SSF Grim {base}", "hardcoreSsf": "HC SSF G {base}" },
    "description": "Added to the card text.",
    "permanentDeath": true
  }
}
```

- `bases` are base leagues, each with Hardcore and SSF variants. `Game_LEAGUES` replaces the offered bases.
- `form` is a family of variants of every base, reached with the client's one form check box. Its id is the module's id.

**In code, an `ILeague` registered with `module.AddLeague(league)`**, what the Gateway asks through `Program.LeagueFor(conn)`:

| Member | What it is |
|---|---|
| `Id` | lower case, as `IPlayer.LeagueId` says it; a form's characters are found by it, so it is the module's id |
| `ClientLeagueName` | the base league it takes, with that league's Hardcore and SSF variants |
| `Rules` | a `LeagueRules`, read at each use, so a tunables file hot-reloads: multipliers, saved life and mana, shops, vendors, rewards, flasks, gems |
| `RequiredFeatures` | modules that must be loaded (a built-in's id counts) |
| `Catalogs` | the game files' data set: `Standard` or `HardMode` |
| `Load(league)` | the league's hooks: an `IModContext` whose hooks run only for this league's players |

- A character whose module's folder was deleted, or whose module lacks a required feature, plays by the rules' defaults, which are
  Standard's, keeping its league name, stash and saves; nothing is converted, and it returns when the folder does. Its league's
  variants are no longer declared, so a Hardcore one is not Hardcore meanwhile. Deleting `standard` too leaves those defaults; the
  create screen then offers what the other modules declare.
- `Modules/ruthless/` holds Ruthless: its names, rules, gem lists and tunables. Its numbers stay in `config/ruthless.json`
  (`Game_RUTHLESS_RULES` moves it, `config/ruthless.example.json` shows it), reread at most once a second, keeping the last good
  ones through a bad edit. `Modules/allflame/` and `Modules/standard/` are the smallest leagues: a declaration, and Standard's rules.

## Built-in modules

`src/GameServer.Gateway/Features/` holds server features moved out of the static `Program`, each an `IModule` the Gateway registers
with `AddBuiltIn`: `harvest`, `heist` and `delve`. Each has a folder in `Modules/` with only a `module.json`, which is what turns it on. Their packets, object clicks, area setup and chat commands reach them through
the same hooks every module has, and `/module` lists them. They have no load context, so they do not reload.
Their bodies still call Gateway internals (`using static Program`, the connection from `ConnOf(player)`), so they cannot be
built as separate assemblies yet: that needs contracts for items, stash and objects. With `harvest`'s or `heist`'s folder gone the hideout
does not place the station or the locker.
