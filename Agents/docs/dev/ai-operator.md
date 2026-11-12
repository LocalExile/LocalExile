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
`game+0xc7f4/+0xc7f8` back to see what its own click resolved to. A monster or a ground item is aimed at its projected
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

## f) Interfaces (draft)

Bridge, line-delimited JSON over stdio — `{"command":"act","kind":"walkTo","x":512,"y":740}`,
`useSkill` `{slot,target}`, `loot`/`interact` `{object}`, `stop`; each answered `{ok, result, elapsedMs}`.

Mod, chat commands shaped like gm-commands: `/ai world [radius]` (area, pos, life, monsters, ground, exits),
`/ai reach <x> <y>` (reachable, steps, blockedBy), `/ai item <id>` (dist, pickupRange, ok), `/ai why <id>` (one
sentence). Every command carries a `session` field for later; v1 serves one character.

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
