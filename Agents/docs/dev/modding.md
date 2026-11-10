# Mods

Mods are custom content that each user opts into, for every league: `Mods/` is gitignored, and Waypoint installs them. A mod
changes how modules, leagues or the framework behave, or gives the user tools for development, testing and practice (GM
commands, teleports, every MTX owned). A GGG-like server needs no mod, and no league lists one: a replica of the real game is
a [module](modules.md). Which layer a change belongs in: [AGENTS.md](../../../AGENTS.md) ("What goes where").
A mod is a C# assembly, or loose `.cs` files, the Gateway loads from `Mods/Binaries/<id>/` while it runs, and reloads when its
files change, without a rebuild or restart of the Gateway. A mod reaches what a module reaches: `GameServer.Modding`, and the
Gateway, Core and the Game module, which are public, so it can alter the game as fully as a [module](modules.md). It runs in
every league.

What is built and what is planned: [status](../../status/setup-and-tooling.md) ("Mods"),
[roadmap](../../roadmap/setup-and-tooling.md) ("Mods").
Code: `src/GameServer.Modding/` (the loader contract and host services), `src/GameServer.Gateway/Modding/ModHost.cs`
(loading), `ModLoadContext.cs`, `ModScripts.cs` (scripts), `GameEvents.cs` (the hooks, and what each owner holds),
`Program.Mods.cs` (what the Gateway shows a mod).

## A mod's folder

```
Mods/Binaries/<id>/
  mod.json      { "id": "<id>", "version": "1.0.0", "kind": "feature", "depends": [], "assembly": "<id>.dll", "enabled": true }
  settings.json what can be tuned, when it has settings (Settings)
  <id>.dll      the mod (and its .pdb, for line numbers in its errors)
  ...           other assemblies it ships, its own files
```

- Every field of `mod.json` is optional. `id` must be the folder's name; `version` is free text (default `0`); `kind`
  is `feature` (default), `league` or `script`; `depends` lists the ids that load first; `assembly` defaults to
  `<id>.dll`; `enabled: false` keeps it unloaded until `/mod_enable`. Comments and trailing commas are allowed.
- A mod whose dependency is missing or failed does not load (`/mod` says which). It loads by itself once that
  dependency does.
- `Mods/Binaries/` is relative to the repository root, like `data/`, and gitignored. `Game_MODS_FOLDER` moves it;
  `Game_MODS=0` loads no mods.

## Scripts

Loose `.cs` files directly in `Mods/Binaries/<id>/` are compiled with Roslyn, in memory, when the mod loads, so saving one
reloads it under the rules of [Reloading](#reloading) (about 0.4 s, plus a second or two for the first compile).

```csharp
// Mods/Binaries/ping/ping.cs
public sealed class Ping : IMod
{
    public void Load(IModContext mod) => GameEvents.Command(mod.Manifest.Id, "ping", "/ping", (conn, args) => conn.Commands!.Reply("pong"));
}
```

- A script's folder needs no `mod.json`: without one its kind is `script`, its version `0`. With one, `depends` and `enabled`
  work as for any mod, and a `.dll` beside the scripts loads as well.
- Every file has the usings of `System`, `System.Collections.Generic`, `System.IO`, `System.Linq`, `System.Threading`,
  `System.Threading.Tasks` and `GameServer.Modding`, and nullable checks on. All the folder's `.cs` files are one assembly.
- They compile against the framework, `GameServer.Modding`, the Gateway (whose types, `GameEvents` among them, are in the
  global namespace), `GameServer.Core`, the `.dll` files in the folder and the assemblies of the mods in `depends`.
- A compile error is the mod's reason in `/mod`, in the log and in chat (`file.cs(line,column): CS0000 text`, the first five).
  The mod stays unloaded until the file compiles. A script runs with the Gateway's rights, like any mod's assembly.

## Writing one

The official mods live in [LocalExile-Mods](https://github.com/LocalExile/LocalExile-Mods) (its `AGENTS.md` has the
layout and the rules); Waypoint's Browse panel installs them. A project mod's source goes in `Mods/Source/<id>/` (Waypoint
puts it there when the mod has no release); `src/GameServer.Modding/Mod.props`, which the root `Directory.Build.props`
imports for a project under `Mods/Source/`, builds `<Name>.csproj` into `Mods/Binaries/<id>/` as `<id>.dll`, with the
`mod.json` and `settings.json` beside it, and compiles against `GameServer.Modding` without copying it. `Mods/` is
gitignored: what is in it is the user's.
To make a new one, copy one of LocalExile-Mods' examples (`example`, `hello`), rename the folder and the `.csproj`, and set
the id in its `mod.json` to the new folder's name in lower case.

```csharp
public sealed class MyMod : IMod
{
    public void Load(IModContext mod)
    {
        string id = mod.Manifest.Id;
        GameEvents.Command(id, "hello", "/hello (says hello)", (conn, args) => conn.Commands!.Reply($"Hello, {conn.Character}"));
        GameEvents.Drops.Add(id, d => { if (d.Monster.Rarity == MonsterRarity.Unique) d.Items.Add("Exalted Orb"); });
    }
}
```

Every public class that implements `IMod` is created and its `Load` called once. One that also implements
`IDisposable` is disposed when the mod unloads.

## Settings

A mod or [module](modules.md) with something to tune declares it in `settings.json`, beside its `mod.json` or `module.json`;
one with nothing to tune has no such file. The declarations are LocalExile's and stay as shipped: what a user changes is in
their own file, `data/mods/<id>.json` (a module's `data/modules/<id>.json`), holding only the values they changed, so
`git pull` and a release update keep it. An editor such as Waypoint draws its form from the declarations and writes that
file; the mod reads both only through the host.

```json
{
  "version": 1,
  "renamed": { "multiplyBy": "multiplier" },
  "settings": {
    "multiplier": {
      "type": "number", "default": 10, "min": 1, "max": 100, "format": "multiplier",
      "label": "Item quantity", "description": "How many times as many items every kill drops."
    }
  }
}
```

```csharp
sealed record Choices(double Multiplier);

public void Load(IModContext mod)
{
    double multiplier = mod.Settings<Choices>().Multiplier;
    GameEvents.Drops.Add(mod.Manifest.Id, drops => drops.Quantity *= multiplier);
}
```

- `Settings<T>()` reads the user's values over the defaults into `T` by name (case does not matter); every declared setting
  has a value. `SaveSettings(values)` writes them as the user's (a chat command that changes one, as LocalExile-Mods' `fastcampaign`'s),
  leaving out those equal to their default, and refuses a value that does not fit.
- A change to `settings.json` or to the user's file reloads the mod (Reloading), and the new load reads them.
- A value that does not fit its declaration is logged (`[mods] <id>: setting <name> … its default … is used`) and its
  default used; a value no longer declared is logged and kept in the user's file. A declaration that is wrong, its
  default included, fails the load with `settings.json: …` as the reason.
- `version` is the declarations' own number; `renamed` moves a user's value from an old name to its new one.

| Type | Value | Its attributes |
|---|---|---|
| `bool` | `true` or `false` | |
| `integer` | a whole number | `min`, `max`, `step` |
| `number` | a number | `min`, `max`, `step` |
| `range` | `[low, high]`, low not above high | `min`, `max` (both ends), `of`: `integer` or `number` (default) |
| `choice` | one of its `options`, or of a `source` | `options` or `source` |
| `choices` | a list of those, each once | `options` or `source`, `minItems`, `maxItems` |
| `text` | text | `minLength`, `maxLength`, `pattern` (a regular expression the whole text matches) |
| `list` | a list of `item` | `item` (a declaration with no default), `minItems`, `maxItems` |
| `map` | an object of key to `value` | `value` (a declaration with no default), and `options` or `source` for its keys (any key without) |
| `group` | an object of its `settings` | `settings` (declarations, as the file's); its default is theirs, so it has none of its own |

Every setting has `type` and `default` (a group excepted), and may have:

- `label` and `description`, an editor's text; `unit` (`s`, `min`, `%`, `cells`, …); `format`: `percent` (0.15 shown as
  15%), `duration` (seconds shown as minutes and seconds) or `multiplier` (0.25 shown as ×0.25).
- `options`: values, or `{"value": …, "label": "…"}` for an editor's text.
- `source`: the game's values of a kind, checked once the game module is loaded: `area`, `monster`, `item`, `gem`,
  `stat`, `npc` or `quest_flag` (what the chat commands complete, and what `GameServer.Gateway values <kind>` lists).
- `when`: `{"<setting>": value, …}`, shown only while those settings have those values.
- `applies`: `now` (default: the reload), `nextArea` (the reload, but the player sees it in the next area) or `restart`
  (read once when the Gateway starts).
- `advanced`: `true` hides it behind an editor's advanced view.

## Hooks

There is one hook system, `GameEvents`, for modules and mods alike. A subscription names its owner, the mod's or module's
id (`mod.Manifest.Id`): `GameEvents.Tick.Add(id, (conn, seconds) => …)`. It returns a handle, but nothing has to keep it:
when the mod unloads the host releases everything its id holds, its subscriptions and its switches. A module's run only
in the leagues that list it; a mod's run in every league, after the modules'.

| Event | When | Can change |
|---|---|---|
| `Command(id, name, usage, run)` | `/name args…` typed in chat; the Gateway's own commands win, then the first owner to take the name; `/commands` lists it | |
| `Tick` | the player tick, about 4 a second in an area (not while paused), with the game seconds since the last one | |
| `Packet(opcode)` | a C→S message with that opcode, before the Gateway's handler | `true` takes it: the Gateway's handler does not run |
| `AreaGenerated` | the area is ready: after the client's "area generated", with the monsters and objects placed | |
| `ObjectUse` | a click on an object the Gateway's own objects did not take | `true`: it was this owner's |
| `MonsterKilled` | a hostile monster died by the player's hand: before its experience and drops, right after them for a projectile's kill (a bow, a spell) | |
| `Drops` | its drops, before they roll | `Quantity`, `Rarity`, `Gold` (multipliers, on top of the league's), `Cancel`, `Items` (extra items, named as `/item` takes them) |
| `Experience` | its experience, after the level penalty, before the character and gems gain it | `Experience` |
| `Stats` | whenever the server rebuilds the character's stats | stats added by `Stats.dat` id, like `/god`'s: life and mana pools are not rebuilt from them, and the panel shows them at its next refresh |
| `PlayerLeaving`, `HideoutReady`, `EnterMessages`, `AreaHeader`, `NpcOption` | the connection closing, the hideout loaded, the messages sent on entering an area, the area header, a picked NPC option | |

The other `GameEvents` entries (kills, damage, skills, items, travel, …) are the game module's hand-overs to the other
modules; a mod subscribes to them the same way.

Switches are plain Gateway members, held under the owner's id until it unloads:

| Call | Does |
|---|---|
| `ChatRoutes.Use(id, host, port)` | global, trade and whisper chat go through this [chat server](../protocol/10-chat-and-commands.md) instead of the local one |
| `ExchangeRateStore.Use(id, options)` | the [Currency Exchange](../protocol/29-currency-exchange.md) trades by these options |
| `ExchangeRateStore.Publish(json)` | any time: new rates for the Currency Exchange, which the Gateway saves |
| `GameEventOwners.Own(id, disposable)` | anything else the owner wants released when it unloads |
| `CharacterBuildApi.For(mod).Capture(player)` / `Prepare(player, json)` | capture the admitted record or validate a version-1 native build; the disposable plan expires after five minutes; `plan.Confirm()` accepts once, then the server drains the old connection and atomically replaces build-owned data; unload cancels unaccepted plans |

A connection (`ClientState`) is one player in one area; `Program.PlayerOf(conn)` is its `IPlayer` and `Program.WorldOf(conn)`
its `IPlayerWorld`, both the Gateway's (character, account,
league, level, area, position, `Tell`, `Send`). An area change is a new connection.
A subscriber that throws is logged as `[events] <owner> <event> failed (skipped)` with its stack trace; a failed command
also tells the player. It does not stop the connection or the other owners.
Subscribers run on the players' connection tasks, several at once: keep shared state thread-safe, and keep the modifiers
cheap (`Stats` runs on every stat rebuild).

## Ground items and pets

`GameEvents.GroundLootFor(conn)` is optional: null when the game's rules do not expose loot controls. `Nearby(radius)` returns
snapshots; `Metadata` is the stable BaseItemTypes id, and `PlayerDropped` and `Quest` let mods exclude refused drops.
`GameEvents.WornPetsFor(conn)` is separately optional: cosmetic rules provide it only in leagues that enable them. Its `Nearby`
returns worn-pet snapshots. `MovePet(id, x, y, mod.Lifetime)` grants a one-game-second lease; repeating the same
current target only renews it. `ReleasePet` or stopping either the mod or cosmetic rules returns the pet to following.
These operations do not start a pickup loop or choose items. `Loot.PickUp(id, mod.Lifetime, petId)` uses the shared
saved gold/stacking path only when the cosmetic rules confirm that owner's worn pet within two cells (omit petId
for the player's own reach). It refuses hand drops, quest items, private items of other players, retired owners,
dead/paused/leaving players and a full inventory. The player must remain within 80 cells. No control bypasses an
obstructed walk. Put policy and filters in the mod.

## Reloading

The Gateway watches `Mods/Binaries/` and `data/mods/`. A change in a mod's folder (a build, an edited `mod.json` or
`settings.json`, a new or deleted folder) or to its user's settings reloads that mod and every mod that depends on it, about
0.4 s after the last file is written.
Players in game read `Mods: loaded <id> …`, `Mods: unloaded <id>` or `Mods: <id> not loaded: <why>` in chat, as the
Gateway's log does with `[mods]`.

- A reload starts the mod's state over: its fields and statics are new. What it saved (its settings, its own files) stays.
- The assemblies and symbols load from a private disk copy under the system temporary directory, so a build can
  overwrite the originals while the mod runs. Path-backed loading avoids the loader/finalizer failure reproduced
  with collectible `LoadFromStream` assemblies on Windows .NET 10. The copy is removed after its context is collected;
  a retained context or a failed file removal is logged. No client files are involved.
- `/mod` lists the loaded mods first, with their version, kind and hook count, then why the others did not load.
  `/mod_reload [id]` reloads one (with its dependents) or all.
- `/mod_disable <id>` unloads a mod and its dependents; `/mod_enable <id>` loads it and them again. The choice lasts across
  restarts and rebuilds, and is kept in `mod-state.txt` in `Mods/Binaries/`, over the manifest's `enabled`. A running
  Gateway also applies that file when another program (a launcher) rewrites it.
- After an unload the Gateway checks that the mod's load context was freed, and logs
  `an unloaded mod is still in memory` when it was not after 2 s: a thread, timer or task of the mod still runs, or
  something outside the mod holds one of its objects. Stop what the mod started in `Dispose`.

Suggestions while working on a mod with the client in game: `dotnet build Mods\Source\<Name>` and watch the chat line;
`live.ps1` builds every mod in `Mods\Source\` before it starts the Gateway (one that fails to build is left out with a
warning).

## The server, and growing it

A project mod compiles against the Gateway, Core and the Game module beside `GameServer.Modding` (`Mod.props`, and
LocalExile-Mods' `mods/Directory.Build.props`), and lists `"Game"` in `depends` when it uses the game's rules: it then
reloads whenever the Game module does. It subscribes to `GameEvents` with its id as the owner, as a module does with its
own, and those subscriptions run in every league while it is loaded; the host releases them when it unloads. A mod
breaks when what it uses in the server changes, as a module would, and is fixed in its own repository.

When a mod needs the framework to hand it something it does not, its LocalExile pull request adds a **general hook or
event**, one any mod could use ("before an item is rolled", "when currency lands"), and nothing else. The feature's
logic, settings and defaults stay in the mod. The mod's pull request in LocalExile-Mods waits for it ("Requires
LocalExile Update").

## Limits

- A mod can use the framework, the server's `GameServer.*` assemblies, the assemblies of the mods and modules it depends
  on and the files in its folder. A league never lists a mod, so `ProvideLeagueRules` is a module's only.
- Not built yet: a mod replacing rows of the catalogs (`Catalogs` only picks between the game files' two data sets), and the rest of
  the systems moved behind the API ([roadmap](../../roadmap/setup-and-tooling.md)).
