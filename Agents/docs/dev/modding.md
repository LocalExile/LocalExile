# Mods

Mods are small modifications, global, and the user's own: `Mods/` is gitignored, and another app will install them. A game system is a [module](modules.md), and only a
league lists one.
A mod is a C# assembly, or loose `.cs` files, the Gateway loads from `Mods/Binaries/<id>/` while it runs, and reloads when its
files change, without a rebuild or restart of the Gateway. A mod sees only the `GameServer.Modding` API; the Gateway, Core and Data stay
internal to the server.

What is built and what is planned: [status](../../status/setup-and-tooling.md) ("Mods"),
[roadmap](../../roadmap/setup-and-tooling.md) ("Mods").
Code: `src/GameServer.Modding/` (the API), `src/GameServer.Gateway/Modding/ModHost.cs` (loading, hooks),
`ModLoadContext.cs`, `ModScripts.cs` (scripts), `ServerHooks.cs` (dispatch), `Program.Mods.cs` (what the Gateway shows a mod,
where it calls the hooks).

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
    public void Load(IModContext mod) => mod.OnChatCommand("ping", "/ping", c => c.Player.Tell("pong"));
}
```

- A script's folder needs no `mod.json`: without one its kind is `script`, its version `0`. With one, `depends` and `enabled`
  work as for any mod, and a `.dll` beside the scripts loads as well.
- Every file has the usings of `System`, `System.Collections.Generic`, `System.IO`, `System.Linq`, `System.Threading`,
  `System.Threading.Tasks` and `GameServer.Modding`, and nullable checks on. All the folder's `.cs` files are one assembly.
- They compile against the framework, `GameServer.Modding`, the `.dll` files in the folder and the assemblies of the mods in
  `depends`. Not the server's: `GameServer.Core` is an unknown namespace to them.
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
        mod.OnChatCommand("hello", "/hello (says hello)", c => c.Player.Tell($"Hello, {c.Player.Character}"));
        mod.ModifyDrops(d => { if (d.Monster.Rarity == MonsterRarity.Unique) d.Items.Add("Exalted Orb"); });
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
    mod.ModifyDrops(drops => drops.Quantity *= multiplier);
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

Each `IModContext` call returns a handle; disposing it removes that hook, and unloading the mod removes them all.

| Call | When | Can change |
|---|---|---|
| `OnChatCommand(name, usage, run)` | `/name args…` typed in chat; the Gateway's own commands win, then the first mod to take the name; `/commands` lists it | |
| `OnTick(run)` | the player tick, about 4 a second in an area (not while paused), with the game seconds since the last one | |
| `OnPacket(opcode, run)` | a C→S message with that opcode, before the Gateway's handler | `true` takes it: the Gateway's handler does not run |
| `OnAreaGenerated(run)` | the area is ready: after the client's "area generated", with the monsters and objects placed | |
| `OnObjectUsed(run)` | a click on an object the Gateway's own objects did not take | `true`: it was this mod's |
| `OnMonsterKilled(run)` | a hostile monster died by the player's hand: before its experience and drops, right after them for a projectile's kill (a bow, a spell) | |
| `ModifyDrops(modify)` | its drops, before they roll | `Quantity`, `Rarity`, `Gold` (multipliers, on top of the league's), `Cancel`, `Items` (extra items, named as `/item` takes them) |
| `ModifyExperience(modify)` | its experience, after the level penalty, before the character and gems gain it | `Experience` |
| `ModifyStats(modify)` | whenever the server rebuilds the character's stats | stats added by `Stats.dat` id, like `/god`'s: life and mana pools are not rebuilt from them, and the panel shows them at its next refresh |
| `UseChatServer(host, port)` | until it unloads: global, trade and whisper chat go through this [chat server](../protocol/10-chat-and-commands.md) instead of the local one | |

A player (`IPlayer`) is one connection: character, account, league, its modes and the league it plays by, level, area id and seed, position,
`Tell` (chat lines) and `Send` (a raw S→C message). An area change is a new connection.
A hook that throws is logged as `[mods] <id> <hook> failed (skipped)` with its stack trace; a failed command also tells
the player. It does not stop the connection or the other mods.
Hooks run on the players' connection tasks, several at once: keep shared state thread-safe, and keep the modifiers
cheap (`ModifyStats` runs on every stat rebuild).

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

## Limits

- A mod can use the framework, `GameServer.Modding`, the assemblies of the mods it depends on and the files in its
  folder; any other `GameServer.*` assembly is refused when it loads.
- Not built yet: a mod replacing rows of the catalogs (`Catalogs` only picks between the game files' two data sets), and the rest of
  the systems moved behind the API ([roadmap](../../roadmap/setup-and-tooling.md)).
