# Campaign automation

`scripts/dev/campaign/` plays the campaign on the local server with the real client, quest by quest, through the
[native client bridge](client-control.md). Movement is by teleport within an area (`/tp`) and, between areas, only by
waypoints, area transitions, portals and NPC transports (never `/goto`: a missing way is a server bug, and the run fails
loudly on it), and bosses die through `/kill`, which uses the normal death path, so quest progress comes from the
server's [quest engine](../game/campaign-quests.md) reacting to real kills, pickups, object use and NPC talk. Each
quest is a small step file; quests without one are reported as not implemented and skipped.

`order.py` lists the whole campaign (Acts 1–10 and the Epilogue, 82 entries). Step files exist for the acts that are
on the quest engine: Act 1 (`a1q1` … `a1q9`), Act 2 (`a2q1` … `a2q11`, `a2q8a3` the arrival in Act 3), Act 3
(`a3q1` … `a3q13`), Act 4 (`a4q1` … `a4q6`, `a5q1a` the Resonator), Act 5 (`a5q1b` … `a5q7`), Act 6 (`a6q1` …
`a6q7`; their `pantheon` steps choose the gods Sin's rows allow) and Acts 7–10 with the Epilogue (`a7q1` … `a10q6`,
`A11q0` Lilly's voyage to Karui Shores, `a10q3a11` Lani's reward there). An act's step files come with its tables
([campaign quests](../game/campaign-quests.md) "Adding an act"); until then its quests are "not implemented", which
does not fail a run, and `steps.py` already has the verbs and town list the later acts use (`pantheon` needs the
Pantheon module's `/state` field).

## The whole order in one command

From the repository root, on a copy of the saves, with this checkout's own ports and client session:

```powershell
Copy-Item data\saves.db data\campaign-saves.db        # once, or any copied database
$env:Game_SAVES_DB = "$PWD\data\campaign-saves.db"
.\scripts\dev\campaign.ps1 -PortBase 25000 -Nullifier  # new level-1 Witch, the whole order
```

What it does, unattended: builds and starts this checkout's servers when its ports are not listening
(`live.ps1 -NoClient`, with `-PortBase` when given), opens its own session client through the control bridge
(`game:open`: `live.ps1 -ClientOnly -Session campaign`, a `-Nullifier` client without a GPU when asked), logs in,
creates a fresh character, turns `/god immune` on and plays every quest of `order.py` that has a step file: kills
through `/kill`, travel through waypoints, exits, portals and NPC voyages only, quest rewards picked into the
inventory, multi-item hand-ins in one click, story passive points granted and left unspent (`--spend-passives`
spends them).

At the end it prints a short summary (also in `logs\campaign-<time>.summary.json`, the full log beside it): the
character and where it ended, the quest counts (done, already done, failed, not implemented, blocked), the failed
quests with their step, passive and refund points, the Pantheon and Kitava's penalty, which finished act's
CharacterStartQuestState flags (`Act1Complete` …) are missing, and the time.

- **Its own servers and client.** The ports are the ones `live.ps1` gives this checkout: `-PortBase N` (remembered in
  `data\stack.json`), else `Game_PORT_BASE`, else the base `data\stack.json` records, else the default ports. When a
  server on those ports is not this checkout's (another checkout's stack, the user's servers, another program), the run
  stops before anything happens: the run would otherwise log in to it and create its character in that save database.
  The client is the `-Session` one (default `campaign`): the bridge attaches only to the client whose PID record
  (`data\clients\<session>.json`) and executable path are this checkout's, and reads only the chat replies sent to that
  client's connection ([live testing](live-testing.md), "Several stacks and clients"). A session client takes neither
  of the two machine-wide client slots ([headless client](headless-client.md)).
- **Memory.** A `-Nullifier` session client commits about 1.6 GB (no GPU memory), the cheap DirectX 11 preset about
  4.3 GB, a normal client about 7.5 GB ([headless client](headless-client.md)).
- **Resume:** run it again with `-Character <name>` (the name the first run printed): quests whose `DONE` flag is set
  are skipped, and inside a quest every step with a set `skip_if` flag; it continues where the run stopped (for
  example after closing the client or a failure). `-From <quest>` / `-Only <quest>[,<quest>]` restrict the order.
- **Flags:** `-Class`, `-Name` (`--replace` deletes an existing character of that name first), `-NoGod`,
  `-RandomSeeds` (default: every new instance at seed 1, as tested), `-NoBuild`, `-Preset <CharacterStartQuestState>`
  for a quick start in a later act (sets flags by hand: not a real playthrough), `-PortBase`, `-Session`,
  `-Nullifier`. Runner flags pass through (`--stop-on-fail`, `--retries N`, `--spend-passives`, `--no-auto-rewards`,
  `--dry-run`, `--list`).
- **Its own save database.** Without `Game_SAVES_DB` the script stops before anything starts: the run creates
  characters, puts items into the stash and (`--replace`) deletes characters, which must not happen in the user's
  `data\saves.db` by accident; `-AllowDefaultSaves` plays there on purpose. Servers it reuses must have logged the same
  database (the Gateway's `[gateway] saves: <path>` line in `logs\live-gateway.log`), and so must the servers it starts;
  otherwise it stops and prints the path the Gateway really uses.
- **Afterwards:** the client and servers keep running, so the character can be played on; `live.ps1 -Stop` (this
  checkout's own processes only) ends them. Servers already running are reused with the seed setting they were started
  with (start them fresh to switch databases or seeds).

## Prerequisites

- The normal live setup ([GettingStarted](../../GettingStarted.md), [live testing](live-testing.md)) and the
  repository `.venv` with `frida` (as for `client_control.py`).
- The local account's password in `data/offline-login.txt` (untracked; a `Password: <x>` line). Without it the runner
  asks once. The password is never logged.
- A copied save database: set `$env:Game_SAVES_DB` before starting (required unless `-AllowDefaultSaves`). The
  scripts never set or change it.
- An account has 24 character slots; each fresh run creates one. Delete old campaign characters, or reuse a name with
  `-Name <name> --replace` (the old one goes to `deleted_characters` in the save database).

## Running

```powershell
.\scripts\dev\campaign.ps1 -PortBase 25000                  # new Witch, the whole order
.\scripts\dev\campaign.ps1 -Character CampAbc               # continue a character; done quests are skipped
.\scripts\dev\campaign.ps1 -Only a1q4                       # quick check of one quest on a new character
.\scripts\dev\campaign.ps1 -Preset Act1Complete -From a2q11 # start in Act 2
.\scripts\dev\campaign.ps1 -Class Ranger -Name CampRanger -To a1q9 --replace
```

`campaign.ps1` checks this checkout's Patch, Auth login and Gateway ports: all this checkout's servers, they are reused;
one free, it starts the servers with `live.ps1 -NoClient` (`-NoBuild` and `-PortBase` passed through); one held by
anything else, it stops. It sets `Game_RANDOM_SEEDS=0` unless `-RandomSeeds` (new instances use seed 1, whose captures
the quest objects are placed from; servers that are already running keep their setting), `Game_CLIENT_SESSION` and,
with `-Nullifier`, `Game_CLIENT_NULLIFIER=1` for the client `game:open` starts, and gives those three their previous
values back when it ends. It checks the save database as above, runs the runner and exits with its code. Unknown
arguments go to the runner unchanged.

The runner itself, from the repository root:

```text
.venv/Scripts/python.exe scripts/dev/campaign/runner.py
    [--character NAME | --new [--class Witch] [--name NAME] [--replace]] [--league Standard]
    [--only ID[,ID...] | --from ID | --to ID] [--preset CharacterStartQuestStateId]
    [--account offline] [--password-file data/offline-login.txt] [--session NAME]
    [--god | --no-god] [--retries 3] [--seed 1] [--done-timeout 15] [--stop-on-fail] [--no-auto-rewards] [--spend-passives]
    [--dry-run] [--list]
```

- Session: `game:open` (opens the session's client through `live.ps1 -ClientOnly -Session` if needed; `--session`,
  else `Game_CLIENT_SESSION`, else the default client); login only when the client is on the login screen; `--new`
  creates the character (an existing one of that name is deleted first only with `--replace`, else the run stops) and
  plays it; `--character` plays that character; with neither, the character already in game is continued. The
  default new name is `Camp` + the time as letters (e.g. `Campbadaajafah`). One run per session of a checkout: the
  runner holds a named mutex (`Local\LocalExile-campaign-<checkout>-<session>`), and a second run on the same session
  stops with exit code 2 before it sends anything.
- `--preset` sends `/quest_preset <Id>` after entering the game: the preset's quest flags (and the earlier acts'), the
  flags that complete its quests, the waypoints of their acts' campaign areas (a preset character starts on The
  Twilight Strand with no other way to travel), the preset's own waypoints (CharacterStartQuestState Areas) and the
  quest items it holds. God mode (default on) sends `/god immune` when `/state` reports `god` or `immune` false;
  `--no-god` sends `/god off` when it is on.
- `--list` prints the order with implemented/not implemented; `--dry-run` prints the selected quests and their steps
  without touching the client.
- Exit code: 0, 1 when an implemented quest failed, 2 for a session or argument error.

## Quest order and resume

`order.py` `ORDER` lists the Quest.json Ids in the usual levelling route, side quests where their area is passed
(Labyrinth trials and league tutorials are left out). `implemented()` lists those with a step file. For each quest:

- **skipped (already)** when its `DONE` flag is in `/state` `flags`; a second run of the same selection does nothing.
- **not implemented** without `quests/<Id>.py` (note "no step file"); not fatal. The shipped step files require only
  shipped quests (`test_campaign.py`), so a quest of a later act never blocks one that has a file.
- **blocked** when a quest in its `REQUIRES` has no file, a broken file, or its `DONE` flag is not set.
- **failed** when a step fails all retries, the file is invalid, or the steps ran but `DONE` did not appear within
  `--done-timeout`. The run goes on with the next quest unless `--stop-on-fail` (the rest are then "not run").

After each quest that ran, the runner claims every NPC option with category `QuestReward` in the current area
(reward heuristic below; `--no-auto-rewards` turns this off), spends passive points (only with `--spend-passives`),
and checks the inventory. `make_room()` keeps 16 cells free: below that the carried non-quest items go into a Stash
(`stash` below). A `reward` whose item finds no free cell also makes room and asks again. Of the campaign towns only
Lioneye's Watch has a Stash so far (`StashCatalog`), so elsewhere the driver takes the waypoint there and comes back
by waypoint or route. Nothing is thrown away: the items are in the account's stash.

## Retries and diagnostics

Every `tp` next to something is `/tp <id>`; when the Gateway refuses the id the runner goes to the thing's cell
(`/tp x y`) instead.

A failing step is retried `--retries` times (default 3), each after a recovery. First, when the client is no longer in
game or `/state` does not answer (the Gateway closes a connection whose stream no longer decodes), it waits up to 15 s
for the connection to close, logs in again, plays the character and restores god mode. Otherwise: 1) re-run as is, 2)
and later: close any dialogue, leave the area by its nearest exit (or by waypoint to another activated one) and come
back the usual way, which makes the area anew (never `/goto`). Each failed attempt logs the step and error, a compact
`/state` (area, seed, instance, position, level, life, dialogue state, the quest's own flags, nearby
monsters/NPCs with options/objects/items/transitions, inventory fill) and the last 40 lines of
`logs/live-gateway.log`.

## Logs

Everything goes to the console and to `logs/campaign-<yyyyMMdd-HHmmss>.log` (the file also has every bridge request
with its time, the password redacted). Every step logs its time in ms; the run ends with a table of quest, status,
time and note, a table per act, and the total time.

## Step files

`scripts/dev/campaign/quests/<Quest Id>.py`:

```python
QUEST = "a1q1"                  # the Quest Id, equal to the file name
DONE = "a1q1-..."               # the QuestFlags Id that means the quest is finished
REQUIRES = []                   # optional: quest Ids that must be done first
STEPS = [
    {"goto": "1_1_1"},
    {"pickup": ["Wand", "Sword", "Bow", "Claw", "Dagger", "Sceptre", "Mace", "Axe", "Staff"]},
    {"equip": ["Wand", "Sword", "Bow", "Claw", "Dagger", "Sceptre", "Mace", "Axe", "Staff"]},
    {"verb": "kill", "target": "Hillock", "skip_if": "a1q1-..."},
    {"verb": "reward", "npc": "Tarkleigh", "option": "Hillock", "pick": ["Fireball"]},
]
```

A step names its verb under `verb` (aliases `do`, `action`), or in the compact form the first key is the verb mapped
to its main argument (`goto` area, `tp` target, `kill` target, `pickup` item, `equip` item, `interact` target,
`talk`/`reward` npc, `use_item` item, `wait_flag` flag, `command` text); `use` is another name for `use_item`. Every
step may have:

- `skip_if`: a flag, or a list of flags; the step is skipped when (any of) it is set (resume inside a quest).
- `optional`: tried once; a failure is only a warning. For steps a resumed character may have done already without a
  flag to show it (a1q1's opening encounter is kept in the save's tutorial state, not in flags).
- `area`: `goto` this area first when elsewhere.
- `timeout`: seconds for the step's wait (defaults below).
- `expect_flag`: a flag to wait for after the action (the step's `timeout`, default 30 s).

| verb | fields | behaviour |
|---|---|---|
| `goto` | `area`, `via` (`waypoint`/`transition`/`route`), `timeout` 60 | Nothing when already there. Otherwise tries in order: waypoint travel (destination in `waypoints`: tp to this area's waypoint when it has one, `waypoint:travel`; the Gateway takes 0x4d from anywhere), the nearest transition with `to == area`, a route (the shortest way by waypoint to an activated one, then exit by exit along WorldAreas `Connections`, through areas whose gate the character passes, each hop a `goto` of its own; an exit the area does not show is left out and the route planned again). Never `/goto`: without a waypoint, exit or route the step fails with "NO ROUTE" (`via: "command"` is refused too). Each waits for the area (`area:wait`, else polling `game:status` + `/state brief`) and steps up to its waypoint when it is not activated yet. |
| `tp` | `target` (as `/tp`; an int or `0x..` is an object id) + `index`, or `x`,`y` | `tp` bridge request; replies are logged. |
| `kill` | `target`: id, name/variety substring, `boss`, `all`, `near [radius]`; `timeout` 20, `appear_timeout` 5 | Waits up to `appear_timeout` for a matching monster (fails if none). Done when all matches are dead. Else tp next to the nearest living one (not for `all`/`near`, which only count hostile monsters), `/kill <target>`, waits until none of them is alive. |
| `pickup` | `item`: base Id or substring of base/name/class, or a list (any); `timeout` 20, `appear_timeout` 5 | Done when a match is already in the inventory. Else tp to the ground item's cell, `item:pickup` (on error `object:interact`), waits until it left the ground and is in the inventory. |
| `equip` | `item` (as `pickup`), `slot` `weapon` (default) / `offhand`; `timeout` 10 | Done when a match is equipped (`/state equipped`). Else `inventory:pickup` from the main inventory (1), `inventory:place` into inventory 3 (weapon) or 4 (offhand) at 0,0, waits for `equipped.<slot>.id`. |
| `interact` | `target`: object type/name/id, else transition name/destination, else NPC; `timeout` 20, `no_wait` | Prefers unused, then nearest matches. tp next to it, `object:interact`; then waits until the object's `used` is true (or a used object of the same type at the same cell: an opened Rhoa Nest comes back under a new id) (unless it was already used, `expect_flag` is given, or `no_wait`), the transition's area is entered, or the NPC dialogue opens. |
| `talk` | `npc` (name or NPCs Id substring, or id), `option` (text substring, optional), `index` (the option's client index) and/or `row` (its NPCTalk row) for rows with the same text; every given one must match; `timeout` 20, `option_timeout` 3 | NPC not here: goes to the step's `area`, else its town (`NPC_TOWNS`, the current act's town first). tp, `object:interact`, waits for `talking == id`; a talk text that starts (`inTalk`) is finished with `npc:close` until `inTalk` is false (this sets its finish flag). Picks the option via `npc:option index` (of rows with the same text the one not heard yet), waits briefly for its effect, finishes talk texts the same way, then one more `npc:close` ends the dialogue. |
| `reward` | `npc`, `option` (default: the NPC's first `QuestReward` option; `index`/`row` as `talk`), `pick` (name/base substrings in order); `timeout` 30, `offer_timeout` 8 | As `talk` up to the option, then waits for `/state reward`. Chooses the first item matching `pick`, else the most useful: gems whose main attribute is one of the class's (actives before supports), flasks, other gems, anything else. Finds a free main-inventory cell for its w×h (column by column), `reward:claim`, waits until `reward` is null and the item is in the inventory, closes the dialogue. Without a panel, the reward counts as given when the option disappeared (skill books). |
| `use_item` | `item` (as `pickup`), `timeout` 10 | Right-click use of a main-inventory item: `item:use` (C→S 0x58), then waits until that item left the inventory. A quest book sets its use_flag (its passive/refund points follow). With `expect_flag` already set and no match carried, nothing to do; else no match fails. |
| `wait_flag` | `flag`, `timeout` 60 | Polls `/state brief` until the flag is set. |
| `command` | `text`, `wait` 1.5 | Sends the slash command, logs the replies. |
| `pantheon` | `major`, `minor` (optional: an Id or name substring, or a list in preference order), `timeout` 10 | For the later acts: per slot, the first unlocked god of the step's list, else the runner's order, among `/state` `pantheon.unlocked`; `pantheon:select <row>` (C→S 0x1ee), waits until `/state` shows it. Fails without a `pantheon` field in `/state` (a league without the Pantheon module), when `pantheon.canChoose` is false, and when the choice does not show within `timeout`. |
| `stash` | `area`, `timeout` 10 | In the area's Stash (towns, hideouts; without one, Lioneye's Watch's by waypoint and back afterwards): tp next to it, open it (`object:interact`), then Ctrl + click every carried non-quest item into tab 0 (`stash:deposit`, C→S 0xe0: the server takes the tab's first free cell), what a tab refuses into tabs 1–4. Quest items stay. |
| `passives` | `timeout` 5 per point | Only with `--spend-passives`. While points are unspent: `passive:allocate` the first candidate (with `expansion` false), wait for `unspent` to drop; a refused node is skipped. Quiet with no points, candidates or `passives` field. |

To add a quest: write `quests/<Id>.py`, check it with `--dry-run --only <Id>`, then run `--only <Id>` (with
`--preset` for a later act), and add regression cases to `test_campaign.py` when a verb changes. The quest order is
owned by the driver; tell it where a new quest belongs if it is not in `ORDER`.

## Server interface

The dev commands are in the Commands module (`Modules/Core/Player/Commands/Rules.Campaign.cs`, `CampaignState.cs`;
[10](../protocol/10-chat-and-commands.md)). `/state` answers `STATE {json}`; a long reply continues on lines starting
`STATE+ ` (the runner joins them in order). Schema v1: `area`, `areaName`, `town`, `seed`, `instance`, `character`,
`classRow`, `level`, `xp`, `life`, `maxLife`, `dead`, `god`, `immune`, `pos` [x,y], `passives` {`unspent` (level,
quest and granted points not allocated), `quest` (the quest books' points), `refund` (passive refund points),
`allocated`, `candidates` (the connected ordinary passives one more point can take)}, `resistancePenalty` (Kitava's:
0, −30 or −60), `flags` (QuestFlags Ids as the client has them: the character's own and the visit and access flags it
is sent, e.g. `Visited-1_2_town`), `waypoints` (WorldAreas Ids), `talking` (NPC id or 0), `inTalk`, `reward` (null or
{`offer`, `items`[{`id`, `base`, `name`, `class`, `w`, `h`}]}), `stash` (null or {`id`, `pos`}: the area's Stash),
`equipped` {`weapon`}, `monsters` [{`id`, `variety`, `name`, `rarity`, `alive`, `hostile`, `life`, `pos`, `boss`}],
`npcs` [{`id`, `npc`, `name`, `pos`, `options` [{`index`, `row` (NPCTalk row), `text`, `category`, `reward`, `heard`
(its QuestFlag3 flags all set)}]}], `objects` [{`id`, `type`, `name`, `pos`, `used`}], `items` [{`id`, `base`, `name`,
`class`, `pos`}], `transitions` [{`id`, `to`, `name`, `pos`}], `waypoint` (null or {`id`, `pos`, `active`}),
`inventory` {`w`, `h`, `items` [{`id`, `base`, `name`, `class`, `x`, `y`, `w`, `h`, `quest`}]}. `/state brief` leaves
out monsters, NPCs, objects, items, transitions, the waypoint and the inventory. Monsters are the bosses and the living
ones within 120 cells (at most 60); `/state all` lists every monster. The objects are the server's interactive ones:
the tutorial chest, Rhoa Nests, Glyph Wall, and the quest engine's chests and objects (`StateObjects`); `/tp` finds
them by these ids, and the room objects quest rules watch (`On.Near`, `On.SpawnsNear`: `PietyBlockage`) by type.
In a league with the Pantheon module /state has `pantheon` {`major`, `minor` (the chosen gods' PantheonPanelLayout Ids
or null), `unlocked` [{`id`, `name`, `major`, `row`}] (majors first), `canChoose` (a town, a hideout, the Rogue Harbour
or `CanChangePantheon`)} (`GameEvents.StateBuilding`: a module adds its own fields).

Dev commands used: `/state`, `/kill <target>` (`boss`: the unique monsters and those with the big boss life bar or a
boss stage; a lethal hit of the monster's life through the skill-hit path: death,
experience, drops, quest flags; a boss's stage is skipped), `/quest_preset <Id>`, `/quest_flag <flag> [off]`,
`/tp …`, `/pantheon [<god> …]` (the Pantheon module), `/god immune` / `/god off` (immune: no damage from hits or ailment ticks; kept per character like `/god`).

## Checks

```powershell
.venv/Scripts/python.exe -m unittest discover -s scripts/dev/campaign -p "test_*.py"
.venv/Scripts/python.exe -m unittest discover -s scripts/dev -p "test_client_control.py"
```

The tests drive the runner against a simulated server (no client): `/state` joining, goto method choice, kill,
pickup, equip, talk texts, reward choice and free cells, skip/resume, selection, not implemented/blocked, the shipped
step files, retries with diagnostics and recovery (including logging in again), passives, the session and password
file parsing. `CampaignCommandTests` checks `/state`'s JSON and reply lines and `/quest_preset`'s flags.

Live history: the same driver (then in `Program.Campaign.cs`, before the game moved into modules) played all 82
entries of `order.py` from a fresh level-1 Witch unattended in 1 h 00 min on 2026-10-31 (seed 1, copied save
database, Acts 1–10 and the Epilogue, no `/goto`; per act 4–8 minutes).
Acts 1–10 on the module quest engine, through this checkout's own ports and session client, are still to be run live.
A run of Acts 7–10 alone starts `-Preset E3StartOfAct7 -From a6q1`: the preset leaves the character on The Brine King's
Reef, and a6q1's step file has Weylam's voyage to The Bridge Encampment.
