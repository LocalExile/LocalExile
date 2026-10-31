# Modules

A module is a big overhaul of the game, such as a league. A [mod](modding.md) is a small modification.

| | Mod | Module |
|---|---|---|
| Is | a tweak: a command, drop or experience changes | an overhaul: a league, a game system |
| Entry point | `IMod`, `IModContext` | `IModule`, `IModuleContext`: all a mod has, and `AddLeague` |
| Folder | `Mods/Source/`, `Mods/Binaries/<id>/` | `Modules/Source/`, `Modules/Binaries/<id>/` |
| Manifest, choices | `mod.json`, `mod-state.txt` | `module.json`, `module-state.txt` |
| Chat | `/mod`, `/mod_reload`, `/mod_enable`, `/mod_disable` | `/module`, `/module_reload`, `/module_enable`, `/module_disable` |
| Settings | `Game_MODS`, `Game_MODS_FOLDER` | `Game_MODULES`, `Game_MODULES_FOLDER` |

Everything else works the same: the [manifest's fields, hooks, scripts and reloading](modding.md), and a `.cs` file implementing
`IModule` in `Modules/Binaries/<id>/` is a module script. Hooks of modules run before those of mods, and a module's command wins
over a mod's of the same name. A mod cannot add a league, and `kind: league` in a `mod.json` is refused.
Code: `ModHost.cs` (one class, two instances: `Program.ServerModules`, `Program.ServerMods`), `ServerHooks.cs`, `ServerLeagues.cs`,
`Features/`, `Modules/Source/`.

To make one, copy `Modules/Source/Allflame/`, rename the folder and the `.csproj`, and set the id in its `module.json`.

## Leagues

The framework knows no league but Standard. Every other league is a module in `Modules/Source/`, and an upcoming league changes
the framework only to add something generic that any league could use: a rule, a hook, a declaration field. Never its name.
What still says Ruthless in `src/` is the client's wire names (`LeagueEntry.RuthlessParent`, its flag) and the game tables' column
names (`IndexRuthless`, `ShopRuthless`, `RuthlessValue`).

A league has two halves, because the Auth server gives the client its list and does not load modules:

**In `module.json`, what Auth reads.** A module that is off is not offered; its saved characters keep their league.

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

- `bases` are challenge leagues, each with Hardcore and SSF variants. `Game_LEAGUES` replaces the offered bases.
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

- A character whose module is missing, off, or lacks a required feature plays by Standard's rules and data, keeping its league
  name, Hardcore and SSF, stash and saves; nothing is converted, and it returns when the module does. A league name no module
  declares any more keeps its name, without Hardcore (with no declaration its variants cannot be told apart).
- Disabling a module mid-session leaves a connection on its rules until its next area, because its items and skills came from
  that league's data.
- `Modules/Source/Ruthless/` holds Ruthless: its names, rules, gem lists and tunables. Its numbers stay in `config/ruthless.json`
  (`Game_RUTHLESS_RULES` moves it, `config/ruthless.example.json` shows it), reread at most once a second, keeping the last good
  ones through a bad edit. `Modules/Source/Allflame/` is the smallest league: a declaration, and Standard's rules.

## Built-in modules

`src/GameServer.Gateway/Features/` holds server features moved out of the static `Program`, each an `IModule` the Gateway registers
with `AddBuiltIn`: `harvest`, `heist` and `delve`. Their packets, object clicks, area setup and chat commands reach them through
the same hooks every module has, and `/module` and `/module_disable` treat them as modules. They load even with `Game_MODULES=0`, and have no
folder or load context, so they do not reload.
Their bodies still call Gateway internals (`using static Program`, the connection from `ConnOf(player)`), so they cannot be
built as separate assemblies yet: that needs contracts for items, stash and objects. With `harvest` or `heist` off the hideout
does not place the station or the locker.
