# Mods

A mod is a C# assembly the Gateway loads from `Mods/Binaries/<id>/` while it runs, and reloads when its files change, without a
rebuild or restart of the Gateway. A mod sees only the `GameServer.Modding` API; the Gateway, Core and Data stay
internal to the server.

What is built and what is planned: [status](../../status/setup-and-tooling.md) ("Mods"),
[roadmap](../../roadmap/setup-and-tooling.md) ("Mods", phases 3 to 5).
Code: `src/GameServer.Modding/` (the API), `src/GameServer.Gateway/Modding/ModHost.cs` (loading, hooks),
`ModLoadContext.cs`, `Program.Mods.cs` (what the Gateway shows a mod, where it calls the hooks).

## A mod's folder

```
Mods/Binaries/<id>/
  mod.json      { "id": "<id>", "version": "1.0.0", "kind": "feature", "depends": [], "assembly": "<id>.dll", "enabled": true }
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

## Writing one

The source of a first-party mod lives in `Mods/Source/<Name>/` (in the solution); `Mods/Source/Directory.Build.props` builds
`<Name>.csproj` into `Mods/Binaries/<name>/` as `<name>.dll`, with the `mod.json` beside it, and compiles against
`GameServer.Modding` without copying it.
To make a new one, copy one of the examples, rename the folder and the `.csproj`, and set the id in its `mod.json` to the
new folder's name in lower case.

| Example | Shows | On by default |
|---|---|---|
| `Example` | `/example`, counting kills and areas: state that a reload starts over | yes |
| `Hello` | `/hello [name]`: a chat command with arguments, the player's details | no |
| `ItemQuantity` | 10 times as many items | no |
| `ItemRarity` | 10 times the chance of magic, rare and unique equipment | no |
| `DoubleExperience` | twice the experience from kills | no |
| `BossLoot` | an Exalted Orb from every unique monster: an extra drop by name | no |

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

## Hooks

Each `IModContext` call returns a handle; disposing it removes that hook, and unloading the mod removes them all.

| Call | When | Can change |
|---|---|---|
| `OnChatCommand(name, usage, run)` | `/name args…` typed in chat; the Gateway's own commands win, then the first mod to take the name; `/commands` lists it | |
| `OnTick(run)` | the player tick, about 4 a second in an area (not while paused), with the game seconds since the last one | |
| `OnPacket(opcode, run)` | a C→S message with that opcode, before the Gateway's handler | `true` takes it: the Gateway's handler does not run |
| `OnAreaGenerated(run)` | the area is ready: after the client's "area generated", with the monsters and objects placed | |
| `OnMonsterKilled(run)` | a hostile monster died by the player's hand: before its experience and drops, right after them for a projectile's kill (a bow, a spell) | |
| `ModifyDrops(modify)` | its drops, before they roll | `Quantity`, `Rarity`, `Gold` (multipliers, on top of the league's), `Cancel`, `Items` (extra items, named as `/item` takes them) |
| `ModifyExperience(modify)` | its experience, after the level penalty, before the character and gems gain it | `Experience` |
| `ModifyStats(modify)` | whenever the server rebuilds the character's stats | stats added by `Stats.dat` id, like `/god`'s: life and mana pools are not rebuilt from them, and the panel shows them at its next refresh |

A player (`IPlayer`) is one connection: character, account, league and its modes, level, area id and seed, position,
`Tell` (chat lines) and `Send` (a raw S→C message). An area change is a new connection.
A hook that throws is logged as `[mods] <id> <hook> failed (skipped)` with its stack trace; a failed command also tells
the player. It does not stop the connection or the other mods.
Hooks run on the players' connection tasks, several at once: keep shared state thread-safe, and keep the modifiers
cheap (`ModifyStats` runs on every stat rebuild).

## Reloading

The Gateway watches `Mods/Binaries/`. A change in a mod's folder (a build, an edited `mod.json`, a new or deleted folder)
reloads that mod and every mod that depends on it, about 0.4 s after the last file is written.
Players in game read `Mods: loaded <id> …`, `Mods: unloaded <id>` or `Mods: <id> not loaded: <why>` in chat, as the
Gateway's log does with `[mods]`.

- A reload starts the mod's state over: its fields and statics are new. What it saved (its own files) stays.
- The assemblies are loaded from a copy in memory, so a build can overwrite them while the mod runs.
- `/mod` lists the mods with their version, kind and hook count, and why the others did not load.
  `/mod_reload [id]` reloads one (with its dependents) or all.
- `/mod_disable <id>` unloads a mod and its dependents; `/mod_enable <id>` loads it and them again. The choice lasts across
  restarts and rebuilds, and is kept in `mod-state.txt` in `Mods/Binaries/`, over the manifest's `enabled`.
- After an unload the Gateway checks that the mod's load context was freed, and logs
  `an unloaded mod is still in memory` when it was not after 2 s: a thread, timer or task of the mod still runs, or
  something outside the mod holds one of its objects. Stop what the mod started in `Dispose`.

Suggestions while working on a mod with the client in game: `dotnet build Mods\Source\<Name>` and watch the chat line;
`live.ps1` builds every mod in `Mods\Source\` before it starts the Gateway (one that fails to build is left out with a
warning).

## Limits

- A mod can use the framework, `GameServer.Modding`, the assemblies of the mods it depends on and the files in its
  folder; any other `GameServer.*` assembly is refused when it loads.
- Not built yet: catalog overrides, loose `.cs` scripts compiled on save, leagues as mods with Standard as the
  fallback, and the existing systems moved behind the API ([roadmap](../../roadmap/setup-and-tooling.md)).
