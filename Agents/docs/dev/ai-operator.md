# AI operator (reconnaissance)

The plan is to let an LLM play a character the way a person does: it looks at what the server knows, decides one thing
at a time, and the character really walks, really swings and really picks things up. Three layers, kept apart:

| Layer | Runs where | Is |
|---|---|---|
| **hands** | `scripts/dev/` bridge, in the client | primitives only: walk to a cell, use a skill on a target, loot, interact. No judgement, no memory |
| **world model + gate** | a server mod (`ai-operator`) | what is really true in the world, and whether one asked-for action is possible now. Read-only: it never moves the player |
| **brain** | outside, an LLM | one intent at a time, from a compressed snapshot; never keystrokes |

This file records what reconnaissance proved before any of it is written. Sections (a)-(d) are the four questions that
had to be answered first. Nothing below is implemented yet.

## a) Does synthetic input get in at all?

**Walking: yes, proved live. Skill: not yet proved — un-run, and the reason is a machine failure, not a negative
result.** The evidence and every raw excerpt are in `Tools/re/out/ai-operator/input-spike.md` (scratch beside it).

| Mechanism | Result |
|---|---|
| `PostMessageW` + `Interceptor.replace` of the window and cursor readers (`scripts/re/frida/drive-input.js`, driven through the **same** Frida session as `client-control.js` — `sync_drive.Driver` + `keep_agent`, so only one agent is ever attached) | **works.** The client's own sender (`0x1421660b0`) fires with the clicked cell, the packet it emits carries that cell, and the character walks there |
| `SendInput` (driver level) | **does not reach this client on this desktop.** The API works — the real cursor landed exactly on the computed point — but `WindowFromPoint` over the client's whole rect returns the DeepSeek Harness window at 616/616 sample points, so the events land on the wrong window. Untested for a client that is not covered |

One posted click, predictive mode (what this checkout ships: `[NETWORKING] networking_type=predictive`): the client hook
`{"q":"…","click":[206,273]}` → `<= 0x0025 16 B: 0025000000CE00000111290910010408` (X=206, Y=273) →
`=> 0x01ab UseSkillAtLocation { X = 206, Y = 273, SkillId = 10505, Slot = 4097 }` → `[sync]` 197,261 → … → 206,273 →
`/pos` (206, 273). The supplementary lockstep run has zero `0x0025` and exactly one
`<= 0x018e 15 B: 018EFFFFFFF3000000112909040800` (dx −13, dy +17 ⇒ the clicked cell (184, 278)) plus `<= 0x0195`;
`/pos` (184, 278).

Two facts that shape the hands:

- **The packet id follows the client's networking mode.** Predictive sends `0x0025` — the very id
  `client_control.py move` writes by hand — while lockstep sends `0x018e`/`0x0192`/`0x0195`. So in predictive mode the
  packets alone cannot separate "the client computed it" from "our bridge wrote it"; the separation is the client's own
  sender firing with the clicked cell. Both modes run the client's own pipeline, and in neither does the hands' code
  write a walk packet.
- **Buttons arrive as window messages** — this build never calls `GetAsyncKeyState`, and the polled keys are nine
  `GetKeyState` codes — and the clicked point must lie inside the viewport rect.

The skill half is where the gap is. Presses are delivered (the client's pump counts `WM_RBUTTONDOWN/UP`,
`WM_KEYDOWN/UP`, `WM_CHAR`) but nothing fires, because nothing is bound: the bar is built server-side from gems
**socketed in equipped gear** (`Modules/Core/Game/Skill/Skills.cs:852` `FillSkillBar`), and a gem in the inventory
grants no skill. The decisive test that remains is the **default attack** (bar slot 0, skill `0x0102`): click a
monster's body pixel and show the client's sender emitting an attack, not a Move. It is un-run: at 13:49 the machine
ran out of memory (it killed both checkouts' gateways) and the Windows management service has been wedged since —
`live.ps1` goes through WMI, so it can no longer start or stop a stack, and restarting `Winmgmt` or rebooting is the
user's call. The recipe is in the spike's file; the aim is the click maths in (b).

## b) What the bridge has to expose

`scripts/dev/client-control.js` today exposes exactly one RPC, `command(request)`, executed inside the client's own
per-frame hook; one request is in flight at a time, with a 5-second frame timeout. Its `interaction` op writes
whitelisted packets through the client's own socket writer — that is the path the hands must *not* use for movement or
aim, because the aim is the client's business (see (d)).

The hands need, each returning `{ok, result, elapsedMs}` with a short reason the brain can use as-is:

| Primitive | Means | Fails as |
|---|---|---|
| `walkTo(x, y)` | put the cursor on that cell and click; keep the button down while the path is long | blocked, unreachable, timed out |
| `useSkill(slot, targetId)` | select the slot, put the cursor on the target's body, click | out of range, target gone, on cooldown |
| `loot(objectId)` | click the ground item (the server walks the body into reach) | out of range, gone |
| `interact(objectId)` | click a transition, waypoint, NPC or chest | out of range, gone |
| `stop()` | release everything held | — |

Every line carries `session` so the same bridge can serve more than one client later; v1 drives one.

How a primitive is really carried out (from `Tools/re/out/ai-operator/world-to-screen.md`): the clicked target is **not**
computed from a camera ray. The client turns the screen vector measured from the player's own pixel into a distance
(`min(|v|, 1.0f) * DAT_1436ae9e4 + DAT_1436ae664`), yaw-rotates it and adds the player's world position, and publishes
the result at `game+0xc7f4` / `game+0xc7f8`. So a click reaches about one screen radius at most, and a distant `walkTo`
is a series of clicks with the position re-read in between — which is also what a person does; the bridge can read
`game+0xc7f4/+0xc7f8` back to see what its own click resolved to. Measured on the Twilight Strand: a click resolves a
cell **about 16 cells** from the character at most, whatever the skill's own reach says — `/aiop why` reported Hillock
"in reach (79.0 of 125)" while the bridge refused with `the closest the cursor reaches is (1386, 336), 27 cells short of
[1359, 318]`, so the 120-cell wand range is the server's reach for the swing, not what one click can point at. A monster or a ground item is aimed at its projected
body pixel (`0x140fd3b40`, camera from `**(longlong**)(FUN_140fcb430() + 8)`); objects have no click rect of their own,
they are resolved screen point → UI element → game object.

## c) Can the server mod see walkable cells in process?

**Yes, and it needs no framework change.** A mod reaches the connection's own layout —
`ClientState.Layout` (`src/GameServer.Gateway/Connection/ClientState.cs:412`) — and, for any other area of the same
seed, `AreaLayouts.For(conn.Layout.Hash, conn.Seed)` (`src/GameServer.Gateway/World/AreaLayouts.cs:121`). The walk
predicate the movement code itself uses is `Layout.WalkableFor(cellX, cellY, size)` (real cells for areas the Gateway
generates; permissive `true` when a layout has no cells, so callers must not read that as "a wall is absent") combined
with the live blockers: `Barrier`, `Blockers`, `StationaryBlockers` and `MonsterBlockers(id)`
(`Connection/ClientState.LivingCollision.cs`). Pathing is `ClientPathfinder.FindPath`
(`src/GameServer.Core/World/ClientPathfinder.cs`), the 1:1 port of the client's own A*, so a server-side answer agrees
with what the client would walk.

Server-generated areas are the ones whose cells exist; a client-side generated map is refused with
`ServerLevels.UnsupportedMessage`. `scripts/re/levelgen/gate-baseline.json` lists 1673 supported captures, including
`1_1_town`, `1_1_1`, `1_1_2`, `1_1_2a` and `1_1_3` — town, the Strand, the Coast, the Tidal Island and the Mud Flats.
Combat/ground data the mod also reaches: `conn.Monsters` (under `lock (conn.Monsters)`) and `conn.DormantMonsters`
with `Label`, `Life`, `MaxLife`, `Hostile`, `Asleep`, `Retired`, `Walk.Position(now)`; ground items from
`conn.Instance.Ground` with `GridX`/`GridY` and the base type from `Program.Items.ByHash(item.TypeHash)`; exits from
`layout.AreaTransitions`.

A mod references `src/GameServer.Modding/Mod.props`: GameServer.Modding, GameServer.Gateway, GameServer.Core and
`Modules/Core/Game/Game.csproj`, all `Private="false"`. (Referencing only GameServer.Modding reaches almost nothing —
`IPlayer`, `IPlayerWorld`, `ClientState`, `AreaLayout` and `GameEvents` all live in GameServer.Gateway.) The extra
assemblies are deliberately not copied beside a mod (`Modding/ModLoadContext.cs`), so a mod binds to the host's own
types.

## d) Movement is the client's, not the server's

`Modules/Core/Game/Player/Lockstep.cs:9-14` and `src/GameServer.Core/World/LockstepHold.cs:5-7`: a lockstep client
sends 0x18e on the click, 0x192 every 69 ms while the button is held, and 0x195 on release; the aim is the client's
cursor cell plus an offset (median 0.8 cells off over 5300 requests, measured), and the newest request wins. So the
aim cannot be handed to the client as a packet: the hands must produce real cursor and button input, and the client
computes the target itself. `scripts/dev/client_control.py move`, which writes a C→S 0x25 directly
(`client_control.py:823`), is on the wrong side of this line — it is fine for a scripted teleport walk used in
testing, and it is not the hands' path.

The lockstep description is only half the picture: this checkout's client ships **predictive**
(`[NETWORKING] networking_type=predictive`), and there its own click-to-move order is `0x0025` — the same id the bridge
would write by hand. The invariant that matters is the same in both modes: the client computes the aim from its own
cursor and sends it; the hands only produce the cursor and button input, and never a packet.

## e) Two things that block a local run (found while doing this)

- **`data/` is shared between checkouts.** `/f/lx-pr-pantheon/data` and `.worktrees/base-check/data` are symlinks to
  `/f/local exile/data`. A newer checkout migrated our `data/characters.db` (the `updates` row names
  `updates/characters/2026_11_11_181109_relational_saves.sql`, which this tree does not have): `account_characters` is
  now a relational table and the JSON rows sit in `account_characters_json`. This tree's `SqliteAccountStore` still
  runs `SELECT character FROM account_characters` (`src/GameServer.Storage/SqliteAccountStore.cs:42`), which raises
  `no such column: character` inside the character-list step of a login, and `GameServer.Auth/Program.cs:255` catches
  only I/O and crypto exceptions — so the exception escapes unlogged, the connection task dies, and the client drops
  back to the login screen with no error anywhere. It happens for every account. Until the schema difference is sorted
  out, run this checkout's stack against a private pair of databases:
  `Game_CHARACTERS_DB=<scratch>/characters.db` (a snapshot whose JSON table is named `account_characters`) and
  `Game_AUTH_DB=<scratch>/auth.db` beside it (`SaveDatabase.AuthPathFor`). Never write under `data/` while another
  checkout is live on it.
- **Ports.** Another checkout's stack was already on 21900-21904, so run with `-PortBase 21950` (or let `live.ps1`
  pick a free block — it avoids the blocks other checkouts recorded in their `data/stack.json`).
- **The Windows management service is wedged (13:49 onward).** The machine ran out of memory and killed both
  checkouts' gateways; since then `Get-CimInstance` and `tasklist` never return, and because `live.ps1` uses WMI for
  every start and stop, no stack can be started or stopped until `Winmgmt` is restarted or the machine reboots. That is
  a system change, so it is the user's call; there is no script-side workaround worth having.
- **A stale dev mod can kill the connection, not just fail to load.** `gm-commands` 1.1.1 loads (2 hooks) but its
  `/set_level 20` throws `MissingMethodException: Method not found: 'UInt32 CharacterStats.ExperienceFor(Int32,
  System.String)'` inside the handler for chat `0x0015`; the gateway logs
  `DISCONNECT … handler error handling 0x0015` and drops the client to the character screen. It looks exactly like
  "the bridge is broken" (`Chat requires an active area` afterwards), so read the gateway log for
  `handler error handling` before blaming the rig. Do not use `/set_level` on this base.
- **`logs/` is shared with the other checkout too.** With `/f/lx-pr-pantheon/data` symlinked here and both stacks
  writing `logs/live-gateway.log`, another session's areas and characters (a level-18 map, `BossVerify`) appear in the
  same file. Filter by the client's remote port or by the character's name before concluding anything about a run.

## f) Interfaces (draft)

Bridge, line-delimited JSON over stdio — `{"command":"act","kind":"walkTo","x":512,"y":740}`,
`useSkill` `{slot,target}`, `loot`/`interact` `{object}`, `stop`; each answered `{ok, result, elapsedMs}`.

Mod, chat commands shaped like gm-commands, all read-only: **`/aiop world [radius]`** (`area`, `seed`, `pos`,
`life`, `monsters`, `ground`, `exits` with the cell each exit stands on), **`/aiop reach <x> <y>`** (`reachable`,
`steps`, `exact`, `standAt`/`standDist` when the cell itself cannot be stood on, `goalWalkable`, `blockedBy`,
`basis`), **`/aiop item <id>`** (`dist`, `pickupRange`, `ok`, `why`), **`/aiop why <id>`** (one sentence plus the
numbers behind it: `weaponRange`, `monsterBody`, `reach`, `canAttackNow`). The command is `aiop`, not `ai`:
gm-commands owns `/ai` (the monster AI switch) and a command name belongs to one mod, so `/ai world` answered the
switch and this mod never ran.

Every answer is derived from the server's own state. `reach` says so explicitly in `basis`, because the server's
walk grid is a capture and the client keeps its own: a `false` means the server sees no route, which the routine
loop treats as advice — the client's own result decides what happened. v1 serves one character; the session is the
bridge's concept, not the server's.

## g) Routine scripts, model operations, and what decides between them

The operator is not "an LLM pressing keys". Two kinds of work share the same hands and the same gate:

| Work | Who runs it | Examples |
|---|---|---|
| Routine | a plain script loop, no model call | walk to a cell, engage the nearest hostile, pick up drops, clear an area, walk back |
| Model operation | the LLM, one intent per turn | talk to an NPC, trade, take a transition, fight a boss, decide what to farm |

The split is a budget rule, not a style choice: a model call costs a round trip of hundreds of milliseconds to
seconds, and the world changes every frame. Anything whose next step is a pure function of the snapshot (distance,
life, an exit index) must not wait for a model. Anything that needs an interpretation the snapshot does not contain
(what the NPC is offering, whether the boss phase looks survivable, which of two exits leads to the quest) must not
be guessed by a script.

The routine loop, in the shape the brain already needs:

1. read `/ai world` once, and again after every primitive that could change it materially (area, a kill, a pickup);
2. pick the routine goal: the nearest hostile in reach, else the nearest ground item within the gate's pickup range,
   else the exit the plan names;
3. hand the primitive to the gate (`/ai reach`, `/ai item`, `/ai why`) and act only on a yes;
4. report the result before the next primitive; two failures of the same kind hand the turn to the model.

The model is called on exactly four events, and otherwise never: a routine goal is finished or impossible; the world
changed in a way the routine cannot classify (a new quest object, an NPC, a transition the plan did not name); the
same primitive failed twice; or the character died. Each model turn gets the compressed snapshot and returns one
intent; the local loop compiles it into 1-5 primitives and executes them frame by frame.

Both paths go through the same gate and the same bridge. A routine script that skips the gate is a bug, not an
optimisation: the gate is what keeps "walk to the item, then take it" from becoming "press pickup at the air forty
cells away".

Layout for the brain and the routines (PR6, `tools/ai-operator/` in the mods repository): `hands.py` (the bridge's
line protocol), `gate.py` (`/ai world`, `/ai reach`, `/ai item`, `/ai why` over the chat channel, parsed), `routine.py`
(the deterministic loop above), `brain.py` (the model adapter: snapshot in, one intent out, the intent schema
validated), `session.py` (the record of every intent, every primitive and its duration, and every refusal).
`session.py` writes the three record types the acceptance needs; nothing else may write them.

## h) Acceptance (the v1 run)

The original wording ("Lioneye's Watch → The Twilight Strand → kill Hillock → pick up his drop → back to town")
does not describe this server, and the run has to be judged against what the server actually is:

- **Lioneye's Watch has exactly one area transition: The Coast** (`AreaLayouts.TownFallback`, id
  `AreaTransitionCatalog.FirstId`, cell `(388, 231)`; `/aiop world` in town lists no other exit).
- **The Twilight Strand is a one-way arrival into town.** `AreaLayouts.cs:51-54` keeps
  `OneWayArrivals[(LioneyesWatch, 0xf00a /* 1_1_1 The Twilight Strand */)] = (123, 308)`: The Strand's own
  transition leads into Lioneye's Watch, and the town layout's door back to the beach is the one its `.dgr` places
  as `entrance1disabled`. There is therefore no walk from town to The Strand, and Hillock can never be reached from
  town on foot.

So the acceptance run, unchanged in spirit and honest about the map, is:

> **With no human keystrokes, the operator starts on The Twilight Strand, finds and kills Hillock, picks up his
> drop, and then walks into Lioneye's Watch through The Strand's own transition.**

The starting cell comes from the character's own save (`area_id = 1_1_1`), not from a `/tp` and not from a packet
the bridge writes: the character is put there the way a fresh character is, and every step of the route is walked.
A new character is created on The Strand by the server itself, so seeding the private save is setup, and it is
stated as such in the run's report.

What has to survive the run, in `tools/ai-operator/` records (written by `session.py` and nothing else):

1. **every round's intent JSON, in time order** — the LLM's own decisions, including the ones a script then
   executed, so the run can be read as a sequence of decisions rather than as a trace of packets;
2. **every bridge action with its duration** — kind, arguments, `ok`, the client's own `order`, and `elapsedMs`;
3. **at least one refusal by the gate** — a primitive the routine wanted and the gate refused, with the reason, so
   the gate is shown to be doing work rather than decorating the loop.

When the run stalls, the records have to say which of the three it was: the hands failed (the bridge's answer),
the gate refused (the refusal), or the model chose wrong (the intent next to the world snapshot it chose from).
Hillock's own numbers matter for judging a "failed" kill: 311 life, but a first-kill script refills him at the
three-second cast (`BossFights.cs:23 ["Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal"] = new(50, 2612, 3.0, 2)`),
so the honest route is hit-and-run with the starting flasks rather than standing in his slam.

## i) What a run's rig has to get right (found live, 2026-11-13)

Four things cost a session each when they are wrong, and none of them is the operator's own logic.

- **The character's row must be readable, or the login dies silently.** A character made by copying a save
  (`Tools/re/out/ai-operator/make_character.py`) works only with `hardcore_death_league`, `hardcore_death_utc` and
  `hardcore_death_permanent` left **NULL**. Writing the string `'None'` there (what some existing rows look like in a
  dump) makes `SqliteAccountStore.Characters` do `Date(r.GetString(13))` on a NULL, throw `UnreadableSaveException`,
  and — because `GameServer.Auth/Program.cs`'s catch covers only I/O, sockets and crypto — the connection task dies
  with nothing in `logs/live-auth.log`. The client just drops back to the login screen on its 0x18 (character list):
  `0x04 LoginResult { Result = 0x0 }`, then `<= 0x18`, then silence. A character list that never answers means
  "a row is unreadable", never "the password is wrong".
- **The area seed has to be the seed the walk grid was captured at.** `data/walkability/` holds one dump per
  `(area, seed)` and, for generated areas, seed 1 is the one everybody has. An area generated at another seed gives
  the gate a *different map*: `/aiop reach` then answers "the goal cell is not walkable and no walkable cell around it
  can be reached" for cells the character is standing next to, and every walk stops after a few cells. Measured on
  `1_1_1` at seed 1325937496 against `data/walkability/1_1_1_seed1.bin`. Pin it (`Game_SEED=1`, or
  `Game_RANDOM_SEEDS=0`) before believing a refusal, or the gate is answering about a map nobody is on.
- **A dead character's clicks do nothing, and `/heal` does not undo that.** `/heal` (gm-commands) restores the
  server's life — `/aiop world` then shows full life — but the client keeps its own death state: presses reach the
  client (its click counter moves) and no order goes out, no walk, no skill, and the run reads it as "the click landed
  on the ground". Live: leaving to the character screen and playing again fixed it at once (a click moved the
  character 8 cells). So a death in a run has to end in a character re-select, not in a heal, and the run's records
  must say which happened.
- **The client's own object readback needs the client to be the focused window.** `game:objects` answered `[]` while
  another window had the focus and filled (three rows, monsters with their screen pixel) once the client's window was
  brought forward; the label pass it reads runs only then. A run that wants to aim at what the client draws has to
  keep the game in front for its whole length — which is also the honest picture of a person playing.

Two more, from the same session, about pressing and about skills:

- **A press has to be move → frame → down → frame → up.** The bridge's `click` moved the cursor, pressed and released
  inside one call (`client-act.js`), and the client picks what is under the cursor once per frame, so every press
  landed on whatever had been under the cursor *before* it moved. Split that way the same pixel is what the client
  picks; a click aimed at a monster's own cell then resolves to exactly that cell instead of to the ground beside him.
  The same split is what a key press needs: move the cursor to the target first, then press the bar key.
- **A left click sends the bar's slot-0 skill, and for this character that is "Move" (`SkillId = 10505`, the id the
  gateway logs as `walk ends at`).** Clicking a monster's cell with the left button is therefore a walk, however well
  it is aimed, and no attack packet ever goes out. What the server sends with `0x0053` is built by `SendSkillBar`
  from the character's saved bar plus the gems it finds in the equipment (`GemSkillEffects`), and this character's bar
  arrives with slot 0 only, although its save names `Fireball` (slot 2) and `LightningTendrils` (slot 3): the saved
  names do not resolve to a skill of the equipment, so the bar keeps just the default entry. Until the character has
  an attack the bar can fire — a gem the server resolves, or the default attack on a weapon it recognises — an
  operator cannot kill anything, and "the click landed on the ground" is the honest report of that, not a bug in the aim.

And one that changes what the operator can do at all:

- **The screen can be read without taking the focus.** `PrintWindow` gives a D3D client's title bar and a black client
  area, and a screen grab gives whatever is on top, so "look at the game" used to mean "bring the game forward" — which
  is not something an operator sharing a machine with a person can do. Windows Graphics Capture (`game:screen`, the
  `windows-capture` package) asks DWM for the window's own composited surface: one frame in 0.3 s with the game neither
  focused nor foreground, 804x647. That is what the death panel needs: when the character dies, the client waits for a
  click on *Resurrect in Town*, `/heal` leaves it waiting, and the operator can now photograph the panel and press the
  button instead of logging the character out and in again. The hands still only produce cursor and button input; the
  picture is a readback like any other.

The death panel, measured (`19` above is what made this possible):

- `/death_screen` (gm-commands) shows the panel without dying, so the whole path can be exercised on a live character.
- The panel is centred and its two buttons sit at **half the client width**, at **0.283** and **0.325** of the client
  height: RESURRECT IN TOWN above RESURRECT AT CHECKPOINT. On an 800x600 client that is `(400, 170)` and `(400, 195)`,
  which is what was pressed.
- The picture comes from `game:screen` (the window is neither focused nor in front) and the press from `act click`,
  and the panel is gone afterwards: two round trips, one press. `/heal` is not a substitute — it restores the server's
  life and leaves the panel up, and every later press goes to a dead character (nothing in the gateway log at all).

Where it lives now: `Routine.resurrect` (`tools/ai-operator/routine.py`) is an intent of the routine's own —
`{"kind": "resurrect"}` — produced by `routine_step` whenever the snapshot's life is 0 and refused to a brain
(`brain.ROUTINE_ONLY`). It photographs the panel into the run's folder, presses RESURRECT IN TOWN, re-reads the life
from the gate, and only then falls back to RESURRECT AT CHECKPOINT; `run.py`'s declared `--heal` is the last resort
and records itself as a dev setup. Live: photographed + pressed + read back in one intent, one primitive.
