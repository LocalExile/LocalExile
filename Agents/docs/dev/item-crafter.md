# Item crafter hook

What a tool needs to let a player compose an item and get it in game: the desktop launcher's item crafter is one. The
server gives the data and makes the item; the tool owns the window, the choices and what it saves.

Code: `src/GameServer.Gateway/Item/ItemCrafter.cs` (catalog and build), `StatDescriptions.cs` (mod wording),
`Program.cs` (`crafter` mode), `/item_give` in `Modules/Core/Player/Commands/CommandsFeature.cs`.
Tests: `tests/GameServer.Gateway.Tests/ItemCrafterTests.cs`.

## The catalog: `GameServer.Gateway crafter`

`dotnet src\GameServer.Gateway\bin\Debug\net10.0\GameServer.Gateway.dll crafter` prints one JSON object per line and
exits; nothing starts. Lines that do not start with `{` are not part of it. About 12,500 lines, 4 MB, 5 s.

| `type` | Fields |
|---|---|
| `class` | `id` (ItemClasses Id, what a base's `class` names), `name` |
| `pool` | `id`, `mods` (Mods.dat Ids) |
| `mod` | `id`, `name` (the affix name: "Virile"), `generation` (Mods.dat GenerationType: 1 prefix, 2 suffix, 3 implicit, 5 corruption, 28/29 Eldritch …), `domain`, `level` (the item level it needs), `families`, `tags`, `stats` (`id`, `min`, `max`: the roll range) |
| `base` | `id` (BaseItemTypes Id), `name`, `class`, `width`, `height`, `dropLevel`, `requirements` (str, dex, int), `sockets` (the most it can have, 0 for none), `quality` (has the Quality component), `implicits` (mod Ids), and pool ids: `pool` (prefixes and suffixes), `influence` (`shaper` … `warlord`: what each influence adds), `crafted` (the bench's mods for its class), `corrupted`, `eldritch` |
| `desc` | `stats`, `variants`: `when` (one condition per stat), `text`, `handlers` (`[name, stat]`, stat 1-based) |
| `word` | `list` (1 prefix, 2 suffix), `key` (Words.dat HASH32, what `/item_give` takes), `text` |

Bases are the modifiable ones with mods: equipment, flasks, jewels, maps; no gems, stackables or placeholder rows.
`pool` is `ItemRolls.Pool` at item level 100, the rule drops and currency use (domain, prefix or suffix, a spawn weight
above 0 for the base's tags, not essence-only); a tool compares each mod's `level` with the item level it shows. Bases
with the same domain, class and tags share their pools.

### Stat descriptions

`data/stat-descriptions.txt` is the client's `Metadata/StatDescriptions/stat_descriptions.txt` with only its English
lines (`extract-data.ps1` entry `stat-descriptions`, `game-pack stat-descriptions`). A `desc` covers one or more stats;
a mod's lines are the descriptions of its stats, each used once, a stat no description covers shows nothing. The first
variant whose every condition holds is the line:

- `#` any value, `5` exactly 5, `1|#` at least 1, `#|-1` at most -1, `1|5` 1 to 5, `!0` anything but 0.
- In the text, `{0}` (or `{}`, the next one) is the value of stat 1, `{1}` of stat 2; `:+d` adds a plus sign to a
  positive value. The client's link markup is already gone (`[Physical|Physical Damage]` is `Physical Damage`).
- Handlers change a value before it is shown: `negate`, `divide_by_one_hundred` (and `_2dp`), `per_minute_to_per_second`
  (/60), `milliseconds_to_seconds` (/1000), `divide_by_ten_1dp_if_required`, `locations_to_metres` (/10), `double` …
  Conditions test the value before its handlers.

A range is shown as the client's bench does: each value as `(min-max)` when they differ.

## Giving it: `/item_give <JSON>`

The item as one JSON object, on one line, after the command (a chat command: the player must be in game; the
launcher sends it through the [client control](client-control.md) bridge). It goes into the main inventory.

```json
{"base": "Metadata/Items/Rings/Ring1", "rarity": "rare", "itemLevel": 84, "quality": 0, "identified": true,
 "states": ["corrupted", "shaper"], "fractured": 1, "name": [4053401100, 1222507149],
 "mods": [{"id": "AddedPhysicalDamageImplicitRing1", "section": "implicit", "values": [1, 4]},
          {"id": "IncreasedLife7", "values": [114]}]}
```

- `base`: a BaseItemTypes Id or name (as `/item`). The only field it needs.
- `rarity`: normal (default), magic, rare, unique. `itemLevel` defaults to the character's level.
- `states`: the names `/item_state` takes, each switched on: corrupted, mirrored, split, synthesised, shaper, elder,
  crusader, redeemer, hunter, warlord.
- `fractured`: how many of the first explicit mods are fractured.
- `name`: a rare's two Words.dat keys (here "Dire Abyss"); without it a rare gets a random name.
- `sockets`: as `/sockets` takes them; `""` for none; without it, the sockets `/item` gives.
- `mods`: every mod of the item, implicits included, in order. `section` (implicit, explicit, enchant, scourge, hidden)
  defaults to the mod's own list; missing `values` are rolled. Any mod goes on any base, values outside the roll range
  too, as with `/item_mod`.

An unknown base, mod, rarity or state is refused with the reason in chat, and nothing is given.
