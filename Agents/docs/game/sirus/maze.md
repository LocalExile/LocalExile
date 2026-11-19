# The circle-maze sweep (`meteor_maze_01`, 10949 / 10950 / 10951; Action 42185)

## In one line

**The client has a full maze implementation**: `OrionCircleMazeBlast` (`ActionTypes.HASH16` **0xA4C9 = 42185**) **builds the
rings itself, one ring after another**, and it builds a warning object of its own named **`meteor_incoming`**; the number of
rings comes from the **skill stat `orion_circle_maze_number_of_circles` (client id 10845 = our row 10844)**, the total
duration comes from **`base_secondary_skill_effect_duration` (client id 3249 = our row 3248, the base in ms)**, and the
centre comes from the position of the entity named **`apparition`**.

> **The stat id convention (calibrated on 2026-11-17)**: the rows in our `data/tables/Stats.json` are 0-based, and the
> `ConstantStats` arrays in `GrantedEffectStatSets.json` hold **our row numbers** (each meaning checked one by one:
> 1105 = physical to fire, 1107 = to lightning, 1108 = to chaos, 2748 = -50% against summons,
> 358 = `base_skill_effect_duration`, 10844 = `orion_circle_maze_number_of_circles`,
> 3248 = `base_secondary_skill_effect_duration`); and **the stat ids in the client binary are 1 higher than our row
> numbers** (6433 → row 6432 `skill_behaviour_variation`, 5521 → row 5520
> `total_skill_area_of_effect_radius_+%_including_final`, 349 → row 348 `skill_art_variation`, 215 → row 214
> `skill_area_of_effect_+%_`). Always write "client id = row number + 1" when quoting one.

**We have already walked half of that path**: `Cast(10949/10950/10951)` sends
`ActionStarted(actor, …, skillId = 0x4000 + the effect’s index in GrantedEffectsKeys, …)`, the client looks it up by
GEPL → GrantedEffect → ActiveSkill **1572** → ActionType **1037**, so **the action class is instantiated** (the old
document's claim that "the server never sent Action 42185 and the client never instantiated it" is **wrong**).

Then why do the rings not appear? Three suspects — **the first two were cleared on the afternoon of 2026-11-17**:
① ~~the attribute id was looked up wrongly~~ → calibrated (see the stat id convention above): rings = client id 10845,
base duration = client id 3249, duration per ring = client id 3250 `secondary_skill_effect_duration`;
② ~~the art hash did not match~~ → **it matches** (all four are `MiscAnimated` HASH32s, see the table below);
③ **the centre needs an entity named `apparition`**, and our apparition is called `AtlasExile5ApparitionDetached` — if the
name does not match it falls back to `+304` ⇒ **that is now the only suspect left**.

⇒ Everything the maze has now (the rings, the walls, the exit, the warnings, the damage) is drawn by the server; the
client's own implementation is simply **not running**.

## Client data (observed)

| item | value |
|---|---|
| Skill | `AtlasExileOrionCircleMazeBlast1/2/3` = **10949 / 10950 / 10951**, ActiveSkill row 1572 (ActionType 1037), CastTime **1500**, animation slot **36 `SpellBuffFire`** → the body's `.act` `"meteor_maze_start_01"` |
| Action class | `OrionCircleMazeBlast`, 928 bytes, main vtable `off_1435875A0`, ctor `sub_141D1BE60` |
| Mutually exclusive | **mutually exclusive** with `OrionBeamBomb` (HASH16 **13449**): the canStart predicate refuses to start when it sees 13449 |
| Number of rings N | **the skill stat `orion_circle_maze_number_of_circles`** (client id **10845** = our row 10844) → `+888` inside the client; the art attribute is one of four, picked by N=1/2/3/≥4, and **all four match a `MiscAnimated` row**: `0x5C9AE98E` → row 3779 `AtlasExileOrionMazeMeteor1Ring` → `meteor_100.ao`, `0xF7E94E3B` → row 3780 `…2Rings` → `meteor_150.ao`, `0x798AE0D7` → row 3781 `…3Rings` → `meteor_200.ao`, `0x29D35DDC` → row 3690 `AtlasExileOrionMazeMeteor` → `maze_meteor.ao` |
| Total duration | `+840` = (base + N × the duration per ring) / 1000 seconds. The `ConstantStats` of rows 10910-10913 in `GrantedEffectStatSets.json` = `[10844, 3248, 1105, 1107, 1108, 2748]`, the values `[N, 1000, 25, 25, 25, -50]` (that is, our rows: the number of rings / `base_secondary_skill_effect_duration` / to fire / to lightning / to chaos / -50% against summons); row 10914 (5 rings) has one more, `358 = 6000` (`base_skill_effect_duration`) |
| Damage | 1105/1107/1108 in the same row = 25/25/25 (physical to fire / lightning / chaos), 2748 = -50 (`monster_penalty_against_minions_damage_+%_final_vs_player_minions`); `BaseEffectiveness` 5.625 / 6.563 / 7.5 |
| Centre | the preparation phase looks for the entity named **`apparition`** and writes its position into `+880`; when it finds none it falls back to `+304` |
| Number of rings | the effect itself is the "number of rings": 10949 one ring, 10950 two rings, 10951 three rings (each row carries its own `orion_circle_maze_number_of_circles`) |
| `meteor_maze_01` | clip **23** of the body's `.ao` (`create_maze` @0.461, `contact` @2.006); **it has nothing to do with the rings** — the client does not build the rings with it (inferred: that is why the last attempt, "cast 10949 and then play clip 23", failed — a cast only plays the animation, it does not instantiate the action class) |

## What the client draws by itself / what the server must draw

- **The client (if we send Action 42185)**: the rings (one of the four art attributes, position = the centre), the
  `meteor_incoming` warning object, the action class's own timing and state advance.
- **The client (today)**: only the wind-up of `meteor_maze_start_01`; not one pixel of the maze itself.
- **The server**: the rings' **collision/walls**, the gap, the exit, the damage settlement, the player's movement — the
  spatial queries the client does only drive the presentation, they do not settle damage (inferred).

## Client implementation (decompiled)

### The action class: `OrionCircleMazeBlast`

| item | value |
|---|---|
| Class name | `"OrionCircleMazeBlast"` @ `0x143267ce0` (a plain C string in `.rdata`) |
| Factory | `sub_14127EAB0` (there is also `sub_141985A00`): it probes the ctx attributes 876 / 332 / 858 to pick a variant, and only when none of them hits does `sub_142C8CB50(928)` allocate **928 bytes** and call the ctor |
| ctor | `sub_141D1BE60`: `sub_1419E9B10(this, 0xA4C9u, actor, ctx)` (the second argument is this action's HASH16); `*this = off_1435875A0`; `[+168] = off_14323E408`; `[+792] = off_14337C858`; `[+800] = off_14337BDF8` |
| Main vtable | **`off_1435875A0`** (the `off_14337B8D8` that the factory overwrites is only an intermediate table, **not** the main one) |

The functions of its own that matter (**trust the address and the behaviour, not the slot number**: the "slot N" in the
notes is off by 2–3 from the vtable byte offset):

| address | behaviour |
|---|---|
| `sub_141D1C420` | preparation: looks up `apparition` for the centre (`+880`), attribute 10845 for the number of rings (`+888`), computes the total duration (`+840`) |
| `sub_141D1CB40` | the per-frame state machine (state `+808`) |
| `sub_141D1CFA0` | the per-frame timer: `+820` accumulates dt, at ≥ `+816` it builds a ring, `+824` ≥ `+888` ends it |
| `sub_141D1D060` | **builds one ring**: picks the art attribute by N → `sub_141F6A910(caster, attribute object, centre)` → the handle goes into `+896` |
| `sub_141D1D4E0` | **the state advance** 0→1→2→3→4→5 |
| `sub_141D1D430` | canStart: it refuses when a nearby action has 13449 (OrionBeamBomb) |
| `sub_141D1D940` / `sub_141D1DAB0` | serialisation / deserialisation |
| `sub_141D1BE20` | clone (`sub_142C8CB50(928)` + copy ctor `sub_141D1BF30`) |
| `sub_141D1C640` | cleanup: releases `+896` / `+904`, unhooks the `+912` / `+920` listeners |

### The state machine (observed)

`+808` is a u32 state, advancing linearly from 0 to 5 (`sub_141D1D4E0`):

| state | what happens on entry (observed) |
|---|---|
| 0 | waits 0.5 s (`+844` accumulates dt every frame), then advances |
| 1 | the ring timer runs: interval **0.2 s** (`+816`), at every tick it builds a ring (`+824++`), and it advances once N rings are built |
| 2 | builds the **warning object**: attribute id **2696282090**, radius parameter = **20 × N**, position = the centre, and sets its state to the literal **`"meteor_incoming"`**; at the same time it runs one rectangular spatial query over the centre (half width `20×N + dword_143A6AE58 − 1`), and it advances only when (total duration − elapsed) ≤ 1.0 s |
| 3 | **clones itself**, sets the clone's `+849` to 1 (it carries its own position), and builds a ring for the clone |
| 4 | the interval becomes **0.15 s**, the warning object is released (`+904`) |
| 5 | sets bit 0x10 of `+290`, registers the callback, releases the last ring (`+896`) |

### Serialisation (what the client receives)

Slot 86 writes, slot 87 reads; the layout is perfectly symmetric (observed):

```
u32  state      → +808
u8   flag       → +849      (flag != 0 means "carries its own centre")
u32  duration   → +840      (seconds)
u32  elapsed    → +844      (seconds)
[flag≠0] u32 x, u32 y → +880 / +884   (the centre's coordinate pair)
```

A common entity state block follows (`sub_141DC09A0` / `sub_141DC0BC0`); when the state is 2 or 3 there is a second block (a
second entity).
In the deserialiser, when flag≠0 the client **builds a ring on the spot** (vtable +1264).

### Hits and damage

- In state 2 the client runs a rectangular query over the centre (`sub_141D22D80`), but **no damage settlement path was
  found**: the hits and the damage remain the server's job (inferred).
- The exit condition of state 2 reads a byte `+848` ("it may wrap up"), and the known code never writes it ⇒ while it stays
  0, state 2 cannot leave (still open).

### What the client draws

- One ring = the effect object that `sub_141F6A910(caster entity, attribute object, centre)` builds; its handle stays in
  `+896` (only the last one is kept).
- Before it builds a ring it steps the thread's LCG (`state = 1103515245·state + 12345`, TLS+368), but **the angle is not
  computed in this function**; the position comes straight from `+880`
  ⇒ every ring sits **at the centre**, and the randomness hides in the attribute object (inferred).
- The four art attributes are picked by the **total number of rings** (N=1/2/3/≥4), so all the rings of one cast are the
  same art.
- State 1→2 also builds a `meteor_incoming` object ⇒ **the client draws the warning meteor too** (the server does not have
  to draw it again).
- The assets related to the maze in the official folder
  `Tools/re/out/orion/metadata/effects/spells/monsters_effects/atlasexiles/orion/`:
  `maze_meteor.ao`, `maze_meteor_fall.ao`, `maze_meteor_impact.ao`, `maze_meteor_incoming.ao`, `maze_meteor_loop.ao`,
  `maze_warp.ao`, `blocking_01.ao` / `blocking_02.ao`, `exithere.ao`.

### What the server should send (conclusion)

To activate the client implementation, send **one** Action 42185 with the attributes and the fields in place (the list is
at the end of this page):
the action's attributes **10845** = the number of rings, **3250** = the milliseconds per ring, **360** = the base
milliseconds; the payload = u32 state / u8 flag / u32 duration / u32 elapsed / (when flag=1) the centre's x,y.
**No** per-ring entities are needed — the client builds them itself, one every 0.2 s.

### Still open

1. ~~which .ao the four art attribute ids point at~~ → **solved (the afternoon of 2026-11-17)**: they are the
   `MiscAnimated` HASH32s — `0x5C9AE98E` = row 3779 `meteor_100.ao`, `0xF7E94E3B` = row 3780 `meteor_150.ao`,
   `0x798AE0D7` = row 3781 `meteor_200.ao`, `0x29D35DDC` = row 3690 `maze_meteor.ao`.
2. The unit of the radius: which coordinate system `20 × N + dword_143A6AE58 − 1` uses (the client's internal unit vs
   cells; 1 cell = 10 world units), and the value of `dword_143A6AE58`.
3. **Who is `apparition` (now the prime suspect)**: the entity the client looks up by name; how the server should create it
   (or how to bypass it with flag=1 and the coordinates). A subtask is already on the name resolution mechanism, see
   `logs/tmp/re/entname.md`.
4. Who writes the byte `+848`, and the real exit condition of state 2.
5. Whether the `+840` / `+888` that the preparation function computes or the `+840` in the server's payload is written
   last (the order is not confirmed; they may overwrite each other).
6. Whether the client's rings **can block the player**: what the client builds are effect objects, and the collision is
   still the server's job (the walls still have to be drawn by the server).

## What the server should send

| # | what to send | field / value | when |
|---|---|---|---|
| 1 | one action of type `OrionCircleMazeBlast` (`ActionTypes.HASH16` **42185**, ActiveSkills row 1572 / ActionType 1037) | the skill stat **10844** = the number of rings N (10949/10950/10951 ⇒ N=1/2/3, the value follows the stat set 10910/10911/10912) | at the moment of the cast, right after the existing `Cast(...)` |
| 2 | the same | the skill stat **3248** = the base milliseconds (1000) ⇒ the client's total duration = (3248 + N×the duration per ring)/1000 s (the 5-ring row has another stat 358 = 6000) | the same |
| 3 | the action's payload | u32 state = 0, u8 flag = **1**, u32 duration = 0, u32 elapsed = 0, u32 x, u32 y = the maze's centre (in cells) | the same |
| 4 | the damage | still through the server's `hits.Add(...)`: the landing point = the centre, the damage = `17 + (rings-1)*15`, the trigger moment = the body's contact | unchanged from today |
| 5 | the art the server still has to draw | the wall `blocking_02.ao` (clip 2 loop, scale 1.5, one every 2.6 cells), the exit `ExitHere.ao`, the centre meteor `destruction_assets/meteors/meteor_rain.ao`, the teleport `maze_warp.ao` | unchanged from today |
| 6 | the art that can be dropped | the warning meteor `maze_meteor_incoming.ao` — in state 1→2 the client builds a `meteor_incoming` object itself; compare a frame capture first, then remove the server's | after the action is sent |

The points:

- **Do not send the rings one by one**: the client builds them itself, one every 0.2 s (`sub_141D1CFA0` →
  `sub_141D1D060`), the server only has to give the number of rings.
- Redraw the wall's radius with the client's **20 × N + 3** (`dword_143A6AE58`, if it is 4) instead of today's 12 / 20 / 28
  — note that this is **one radius for the whole maze**, not one per ring (the unit is still open, see still open 2).
- `OrionBeamBomb` (13449) and the maze are mutually exclusive: only one of them can exist at a time.
- The order of work: send Action 42185 first and capture frames in the live client, confirm what the rings and the warnings
  look like, and only then decide how much the server still has to draw and whether to switch to the official radius.

## Still open (server side)

1. The minimal implementation that sends Action 42185 (the action's attributes + the payload fields) — the table at the
   end of this page is the specification.
2. Switch the radius to the official 20 × N + 3, and confirm the unit (cells vs world units).
3. `TraceMaze` (`Sirus.Combat.cs:474-491`) is a debug print and has to go before the hand-over.
4. The number and the radius spread of the warning meteors are guesses for now; once the client brings its own
   `meteor_incoming`, that whole part can be deleted.

## The big meteor and the crater at the centre of the rings (implemented on the evening of 2026-11-17)

What lands in the middle of the rings is not a small meteor (that is the star rain's), it is **one very large meteor
dropping straight down and digging a big crater**:

- The warning: `maze_meteor_incoming.ao` (the `incoming` clip of the same rig, the AirRush sound + the ground circle of
  `meteor_incoming.pet`), placed at the centre of the rings, alive until the hit.
- The meteor: `maze_meteor.ao` (the `meteor_01` clip, `MazeMeteor/MeteorWhoosh` + `meteor_head.pet`/`meteor_wind.pet`),
  starting 1.92 s before the hit and alive 1.2 s past it.
- The crater: `patches` (the ground damage area of 10945) = the radius at the centre of the rings
  `17 + 15×(number of rings-1)`, for 8 s — a player caught inside keeps losing health.

**What the server should send**: all three effects at the centre of the rings (the warning circle, the big meteor, the
crater) are sent by the server: the client's `OrionCircleMazeBlast` only builds the rings when the action's state carries
flag=1 and the centre (see "why the maze has no rings" below), and we never send such a state ⇒ the centre can only be
drawn by the server too.
