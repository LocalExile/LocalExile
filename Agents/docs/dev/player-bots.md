# Player bots (the PlayerBots module)

`Modules/PlayerBots/` makes a new server look lived in: server-driven characters that are created over time, play
through the campaign, visit the towns and leave again. Off by default: no league lists it.

## Turning it on

List it in a league, as `data/league-modules.json` does with
`{"Standard": {"add": ["PlayerBots"]}}` ([modules](modules.md)), then restart the Gateway. Its settings are in
`settings.json` beside it, and a user's values in `data/modules/PlayerBots.json` ([modding](modding.md), "Settings"); a
change reloads the module, so the bots start over from `state/bots.json`.

Each setting's default, limits and meaning are in its `settings.json`.

## What a bot is

- **An account and a character** in the save database: account `<name>_bot`, a random Russian or English character name,
  a random class. Its progress (level, last town) is written to the character record.
- **A body in towns.** A bot is a published player without a connection (`InstancePlayer` with no owner) of every live
  instance of its town (`Program.InGame` lists the connections in game). Each client there replicates it as it
  replicates another player (`LivingCollision.SyncInstancePlayers`): created with its class model and gear, walking by
  the Move action (0x1ab, skill 0x2909) toward its destination, which the client paths and animates.
  It is only shown where a player is; nobody collides with it, as players do not collide in the game.
- **Gear** (`BotGear`): real base items of the game data for its class and level (weapon by class, armour by the class's
  attributes, a belt), drawn as the server draws a player's equipment. Better bases every four levels.
- **The route**: `story-main.md` (the main story, 52 quests) and `story-side.md` (side quests, 30), act by act, main
  quests first. Both are tables the module reads; the order is the campaign driver's (`scripts/dev/campaign/order.py`).
  A quest is simulated out of sight for `questMinutes`; the zone, kills, deaths and gear changes are logged.
- **Town visits**: after a quest, a quick visit (out of its own portal by an NPC, hands the quest in, leaves by portal)
  or a stay (by the stash and the guild stash, a few things to do). At most five of each per town at once; a bot that
  finds no room goes on to its next quest. Places to stand are walkable and 20 cells apart.
- **The end**: after every quest of both guides the character and its account are deleted, and a new bot follows.

A restart resumes every bot where it was (`state/bots.json`: phase, town, place, target, time left, gear). A bot account
carries a mark in place of its password hash (`PlayerBots:`, which no base64 hash holds); a marked account the file
does not know is deleted, and no other account ever is, whatever its name.

## Files

| Path | What |
|---|---|
| `logs/bots-<date>.log` | one line per event: `date time \| account \| character \| what` (errors too) |
| `logs/status.txt` | every bot, its level, act and what it is doing, rewritten every few seconds |
| `state/bots.json` | the bots' state for a restart (local, not committed) |
| `economy/<league>/` | a poe.ninja snapshot (prices in chaos, divine, alchemy, regal; exchange turnover and daily history) for trading bots, made locally by `tools/collect_economy.py` (not committed) |
| `tools/trace-stance.js` | Frida: which animation set the client picks per actor (`0x141e97190`) |

## Client findings

- **Walking**: a glide (0x1ba MoveTo) slides a standing body; a Move action (0x1ab) makes the client walk it.
- **Stance**: the client picks an actor's animation set (`FUN_141e97190`: `"Town"` in a town or with `use_town_stance`,
  else the weapon's) when its Inventories component changes, not when the object is created (Frida, 2026-11-01).
  An observer therefore gets the peer's equipment (0x2ba) right after creating it, for the town stance (not yet verified live).

## Not done yet

Bots do not fight, loot, craft or trade for real: quests are timed, and the log tells what such a quest would hold.
They do not chat or join parties.
