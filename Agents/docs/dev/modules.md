# Leagues and modules

The Gateway is the framework: connections, packets, the message loop, the hosts of mods, modules and leagues, game data, saves and the connection's state. **Modules** do everything else, **leagues** are sets
of modules, and [mods](modding.md) are small modifications, global and the user's own.

| | Mod | Module | League |
|---|---|---|---|
| Is | a tweak | a game system: NPC dialogue, Delve, Pantheons | what a character lives in, shown in game |
| Folder | `Mods/` (gitignored as a whole; installed by the user) | `Modules/<any folders>/<id>/` | `Leagues/<Name>/` |
| Entry point | `IMod` | `IModule` | a class deriving `League` in `Source/`, `league.json` and `modules.json` |
| Loads | any time | when the Gateway starts, if a league enables it | when the Gateway starts, if its folder exists |
| Hot reload | yes | yes | its code, yes |

Built output is gitignored everywhere. `live.ps1` builds every project in `Modules\` and `Mods\Source\`. The Gateway compiles each league's `Source/*.cs` into its `Binaries/` when it starts; a league with only `Binaries/` runs from those.

## Leagues

`Leagues/` decides what exists. Leagues and their module lists are read once when a server starts: nothing is loaded, unloaded, added or
removed while it runs. A connection is in one league, and only that league's modules run for it.

```json
{
  "name": "Ruthless Allflame",
  "base": "Allflame",
  "parent": "Ruthless",
  "entryOf": "Allflame", "entryKind": "form",
  "form": "ruthless",
  "description": "The text on its card.",
  "dataSet": "HardMode"
}
```

| Field | Means |
|---|---|
| `parent` | where its characters, stash and gold go if it ends. Not inheritance: nothing else passes on. Must lead to `Standard` or `Void`, else it does not load |
| `entryOf`, `entryKind` | a check box (`hardcore`, `ssf`, `form`: the client's Ruthless box) on that league's card. With none, it has its own card, as `Hardcore Allflame` does |
| `hardcore`, `ssf`, `form` | its modes. A Hardcore league's parent is the non-hardcore one |
| `ended` | no new characters, and the migration list offers it |
| `creatable: false` | not on the create screen (Void) |
| `dataSet` | `HardMode`: the game files' HardMode tables |

Beside `league.json`, `modules.json` lists the modules the league loads, Core ones too, as paths under `Modules/`. Nothing is implied: what is not listed does not run for its players.

```json
{
  "modules": ["Core/Game", "Core/Player/Commands", "Core/NPC/Vendors", "Ruthless"]
}
```

A league with no `modules.json`, or a module listed twice, does not load. Every league that can be played lists `Core/Game`. The Gateway logs a listed path with no module.

- **Standard** is required: the Gateway refuses to start without it. Its modules change like any league's.
- **Void** has no modules and cannot be created in. Its characters are listed and cannot be entered. A custom event league can have it as parent,
  so that nothing it drops reaches Standard.
- **A deleted league's** characters (no `Source/` or `Binaries/`) are listed, marked, and cannot be entered. Nothing is converted, and they return with the folder.
- **An ended league's** characters cannot be entered until they migrate (not built: see the roadmap).
- **A Hardcore death:** the death screen appears, and the character stays where it is until the player leaves by its Exit (or any other way). The connection closes first; then the character alone, with what it carries, moves to the parent league. Stashes and gold stay. A saved mark covers a crash in between.
- **A league's code** is a public class deriving `League` in its `Source/`, with the hooks of a module for its own players, and `Rules`. Saving it hot-reloads.

## What stays in the Gateway

The framework, and what the game's rules read and write: connections and packets, framing and the message loop (`Connection/Program.Connection.cs`), the hosts (`Modding/`), game data and its catalogs, saves and inventories, and the
connection's state (`ClientState`), with the types its fields have. State stays so that a reload of the rules leaves a player in game.
Its files are in folders by subject: `Connection/`, `Modding/`, `Data/`, `Saves/`, `Combat/`, `Flask/`, `Item/`, `Monster/`, `NPC/`, `Passive/`, `Player/`, `Quest/`, `Skill/`, `World/`.

The framework reaches the rules through `IGameLogic` (`Modding/GameLogic.cs`), which the `Game` module gives when it loads, and `GameEvents`, whose subscribers run per league. Without `Game`, connections are refused.
A catalog type holding some rules stays when `ClientState` has a field of it (`ChargeEffects`, `AuraCatalog`, `UniqueItemSeed`): cutting that field is what lets it move.

## Modules

A module has no knowledge of leagues, and is not changed by one. A league needing even a small difference uses a module of its own, which may share code.
One copy of a module runs for every league listing it, since one connection is in one league; its hooks run per player. A module no league lists does not load.

- **Core** (`Modules/Core/`) is ordinary modules, in folders by subject: `Game`; `NPC/` `NpcDialogue`, `Vendors`; `World/` `Portals`, `QuestObjects` (the campaign's quests: the act tables of the quest engine, [campaign quests](../game/campaign-quests.md)), `HideoutEditing`, `Atlas`; `Content/` `Harvest`, `Heist`, `Delve`; `Item/` `Bench`, `Tattoos`, `UniqueBelts`, `Cosmetics`; `Player/` `Social`, `Commands`, `Pantheon` (the god choice and its powers, [pantheon](../game/campaign/pantheon.md)). A folder holding a `module.json` is a module, at any depth; its id is the folder's name, and ids are unique.
- **`Game`** is the rules: casting, hits, monsters, items, passives, travel, in subfolders by subject (`Combat/`, `Monster/`, `Skill/`, `Item/`, `Passive/`, `Projectile/`, `Flask/`, `World/`, `NPC/`, `Player/`). Its parts call one another, so they are one assembly, and a league cannot turn one part off. Every other module depends on it; rebuilding it reloads them all, and its in-memory per-connection sessions (leech, charges, fortification, projectiles) start over.
- **Hot reload:** a new build in `bin/`, an edited `module.json`, a saved loose `.cs` file, or `/module_reload [id]` reloads a module and those that depend on it; their state starts over.
  Bad new files leave the running one as it is; a build that throws while loading goes back to the assemblies it had. Saving a project's source does nothing until it is built.
- **Without a module:** its hooks do not run, so a hideout has no Horticrafting Station (its reserved spot stays free), its messages reach no handler, and an NPC option nothing answers does nothing. Saved data stays.
- **To make one:** copy `Modules/Core/Content/Heist/`, rename the folder and `.csproj`, set the id and `assembly`, add the id to `Modules/Directory.Build.props` and `GameServer.Gateway.csproj` for internals, and list it in the `modules.json` of the leagues that want it.

## What a module sees

`IModuleContext` includes a host-issued `Lifetime` for each load. Stopping cancels its token and releases
resources registered through `Own`; it does not drain arbitrary tasks already running.
The optional [Phrecia Ascendancies](../game/phrecia-modules.md) family uses this ownership for admission and reload.
`IModuleContext` is what a mod has, and `ProvideLeagueRules`, `World(player)`, `OnHideoutReady`, `OnEnterMessages`, `OnAreaHeader`, `OnNpcOption`.
`IPlayerWorld` is objects, blockers, walking up, hideout stations, windows, stats, travel, NPC actions, crafting in a slot (with payment), item rules
(`IItemCrafting`), and saved text per account and league or per character (`ISavedData`).
A module can alter the game fully: it compiles against the Gateway and its internals (add its assembly to `InternalsVisibleTo` in `GameServer.Gateway.csproj`), with `using static Program`, `using static Game` (both global in a module) and `ConnOf(player)`. Mods see only `GameServer.Modding`. Per-player state in a module's own
`ConditionalWeakTable<IPlayer, …>` starts over with a hot reload, and objects it placed stop answering until the player's next area; state in `ClientState` survives.

A Core module reaches the framework through the hooks and `GameEvents` (`Modding/GameEvents.cs`), the Gateway's own hand-over points, with `GameEvent.For(conn)` running only the modules of that connection's league. Add a new hand-over there, in general terms, instead of naming a module in the framework.

Code: `GameEvents.cs`, `ModHost.cs` (one class, three instances in `Program.Mods.cs`), `ServerHooks.cs`, `ServerLeagues.cs`, `Program.ModuleWorld.cs`, `LeagueCatalog.cs`.
