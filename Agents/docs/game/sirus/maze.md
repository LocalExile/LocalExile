# The circle-maze sweep (`meteor_maze_01`, 10949 / 10950 / 10951; Action 42185)

## In one line

**The client has a full maze implementation**: `OrionCircleMazeBlast` (`ActionTypes.HASH16` **0xA4C9 = 42185**) **builds the
rings itself, one ring after another**, and it builds a warning object of its own named **`meteor_incoming`**; the number of
rings comes from the **skill stat `orion_circle_maze_number_of_circles` (client id 10845 = our row 10844)**, the total
duration is **(`skill_effect_duration` + N × `secondary_skill_effect_duration`) / 1000 s** (client ids 360 and 3250,
read at `0x141d1c4a0`), and the
centre comes from the position of the entity named **`apparition`**.

> **The stat id convention**: the rows in our `data/tables/Stats.json` are 0-based, and the
> `ConstantStats` arrays in `GrantedEffectStatSets.json` hold **our row numbers** (each meaning checked one by one:
> 1105 = physical to fire, 1107 = to lightning, 1108 = to chaos, 2748 = -50% against summons,
> 358 = `base_skill_effect_duration`, 10844 = `orion_circle_maze_number_of_circles`,
> 3248 = `base_secondary_skill_effect_duration`); and **the stat ids in the client binary are 1 higher than our row
> numbers** (6433 → row 6432 `skill_behaviour_variation`, 5521 → row 5520
> `total_skill_area_of_effect_radius_+%_including_final`, 349 → row 348 `skill_art_variation`, 215 → row 214
> `skill_area_of_effect_+%_`). Always write "client id = row number + 1" when quoting one.

`Cast(10949/10950/10951)` sends
`ActionStarted(actor, …, skillId = 0x4000 + the effect’s index in GrantedEffectsKeys, …)`, the client looks it up by
GEPL → GrantedEffect → ActiveSkill **1572** → ActionType **1037**, so **the action class is instantiated**.

The stat ids are as above (rings = client id 10845, base duration = client id 360 `skill_effect_duration`, duration per ring = client id 3250
`secondary_skill_effect_duration`) and the art hashes match (all four are `MiscAnimated` HASH32s, see the table below).
The rings still do not appear: **the centre needs an entity named `apparition`**, and our apparition is called
`AtlasExile5ApparitionDetached` — if the name does not match it falls back to `+304`.

⇒ Everything the maze has now (the rings, the walls, the exit, the warnings, the damage) is drawn by the server; the
client's own implementation is simply **not running**.

## Client data (observed)

| item | value |
|---|---|
| Skill | `AtlasExileOrionCircleMazeBlast1/2/3` = **10949 / 10950 / 10951**, ActiveSkill row 1572 (ActionType 1037), CastTime **1500**, animation slot **36 `SpellBuffFire`** → the body's `.act` `"meteor_maze_start_01"` |
| Action class | `OrionCircleMazeBlast`, 928 bytes, main vtable `0x1435875A0`, ctor `0x141D1BE60` |
| Mutually exclusive | **mutually exclusive** with `OrionBeamBomb` (HASH16 **13449**): the canStart predicate refuses to start when it sees 13449 |
| Number of rings N | **the skill stat `orion_circle_maze_number_of_circles`** (client id **10845** = our row 10844) → `+888` inside the client; the art attribute is one of four, picked by N=1/2/3/≥4, and **all four match a `MiscAnimated` row**: `0x5C9AE98E` → row 3779 `AtlasExileOrionMazeMeteor1Ring` → `meteor_100.ao`, `0xF7E94E3B` → row 3780 `…2Rings` → `meteor_150.ao`, `0x798AE0D7` → row 3781 `…3Rings` → `meteor_200.ao`, `0x29D35DDC` → row 3690 `AtlasExileOrionMazeMeteor` → `maze_meteor.ao` |
| Total duration | `+840` = (base + N × the duration per ring) / 1000 seconds. The `ConstantStats` of rows 10910-10913 in `GrantedEffectStatSets.json` = `[10844, 3248, 1105, 1107, 1108, 2748]`, the values `[N, 1000, 25, 25, 25, -50]` (that is, our rows: the number of rings / `base_secondary_skill_effect_duration` = the time per ring / to fire / to lightning / to chaos / -50% against summons); row 10914 (5 rings) has one more, `358 = 6000` (`base_skill_effect_duration`) |
| Damage | 1105/1107/1108 in the same row = 25/25/25 (physical to fire / lightning / chaos), 2748 = -50 (`monster_penalty_against_minions_damage_+%_final_vs_player_minions`); `BaseEffectiveness` 5.625 / 6.563 / 7.5 |
| Centre | the preparation phase looks for the entity named **`apparition`** and writes its position into `+880`; when it finds none it falls back to `+304` |
| Number of rings | the effect itself is the "number of rings": 10949 one ring, 10950 two rings, 10951 three rings (each row carries its own `orion_circle_maze_number_of_circles`) |
| `meteor_maze_01` | clip **23** of the body's `.ao` (`create_maze` @0.461, `contact` @2.006); **it has nothing to do with the rings** — the client does not build the rings with it (inferred: casting 10949 and then playing clip 23 builds no rings) |

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
| Factory | `0x14127EAB0` (there is also `0x141985A00`): it probes the ctx attributes 876 / 332 / 858 to pick a variant, and only when none of them hits does it allocate **928 bytes** (allocator `0x142C8CB50`) and call the ctor |
| ctor | `0x141D1BE60`: calls the base Action ctor `0x1419E9B10` with this action's HASH16 0xA4C9, the actor and the ctx; installs the vtable `0x1435875A0`, and the tables `0x14323E408` at `+168`, `0x14337C858` at `+792`, `0x14337BDF8` at `+800` |
| Main vtable | **`0x1435875A0`** (the `0x14337B8D8` that the factory overwrites is only an intermediate table, **not** the main one) |

The functions of its own that matter (**trust the address and the behaviour, not the slot number**: the "slot N" in the
notes is off by 2–3 from the vtable byte offset):

| address | behaviour |
|---|---|
| `0x141D1C420` | preparation: looks up `apparition` for the centre (`+880`), attribute 10845 for the number of rings (`+888`), computes the total duration (`+840`) |
| `0x141D1CB40` | the per-frame state machine (state `+808`) |
| `0x141D1CFA0` | the per-frame timer: `+820` accumulates dt, at ≥ `+816` it builds a ring, `+824` ≥ `+888` ends it |
| `0x141D1D060` | **builds one ring**: picks the art attribute by N → instantiates it through `0x141F6A910` (the caster, the attribute object, the centre) → the handle goes into `+896` |
| `0x141D1D4E0` | **the state advance** 0→1→2→3→4→5 |
| `0x141D1D430` | canStart: it refuses when a nearby action has 13449 (OrionBeamBomb) |
| `0x141D1D940` / `0x141D1DAB0` | serialisation / deserialisation |
| `0x141D1BE20` | clone (a new 928-byte allocation + the copy ctor `0x141D1BF30`) |
| `0x141D1C640` | cleanup: releases `+896` / `+904`, unhooks the `+912` / `+920` listeners |

### The state machine (observed)

`+808` is a u32 state, advancing linearly from 0 to 5 (`0x141D1D4E0`):

| state | what happens on entry (observed) |
|---|---|
| 0 | waits 0.5 s (`+844` accumulates dt every frame), then advances |
| 1 | the ring timer runs: interval **0.2 s** (`+816`), at every tick it builds a ring (`+824++`), and it advances once N rings are built |
| 2 | builds the **warning object**: attribute id **2696282090**, radius parameter = **20 × N**, position = the centre, and sets its state to the literal **`"meteor_incoming"`**; at the same time it runs one rectangular spatial query over the centre (half width `20×N + 0x143A6AE58 − 1`), and it advances only when (total duration − elapsed) ≤ 1.0 s |
| 3 | **clones itself**, sets the clone's `+849` to 1 (it carries its own position), and builds a ring for the clone |
| 4 | the interval becomes **0.15 s**, the warning object is released (`+904`) |
| 5 | sets bit 0x10 of `+290`, registers the callback, releases the last ring (`+896`) |

### Serialisation (what the client receives)

Slot 86 writes, slot 87 reads; the layout is perfectly symmetric (observed):

| order | type | field | meaning |
|---|---|---|---|
| 0 | u32 | state → `+808` | |
| 1 | u8 | flag → `+849` | not 0 = "carries its own centre" |
| 2 | u32 | duration → `+840` | seconds |
| 3 | u32 | elapsed → `+844` | seconds |
| 4 | u32, u32 | x, y → `+880` / `+884` | the centre's coordinate pair, only when flag≠0 |

A common entity state block follows (`0x141DC09A0` / `0x141DC0BC0`); when the state is 2 or 3 there is a second block (a
second entity).
In the deserialiser, when flag≠0 the client **builds a ring on the spot** (vtable +1264).

### Hits and damage

- In state 2 the client runs a rectangular query over the centre (`0x141D22D80`), but **no damage settlement path was
  found**: the hits and the damage remain the server's job (inferred).
- The exit condition of state 2 reads a byte `+848` ("it may wrap up"), and the known code never writes it ⇒ while it stays
  0, state 2 cannot leave (still open).

### What the client draws

- One ring = the effect object that `0x141F6A910` builds from the caster entity, the attribute object and the centre; its handle stays in
  `+896` (only the last one is kept).
- Before it builds a ring it steps the thread's LCG (`state = 1103515245·state + 12345`, TLS+368), but **the angle is not
  computed in this function**; the position comes straight from `+880`
  ⇒ every ring sits **at the centre**, and the randomness hides in the attribute object (inferred).
- The four art attributes are picked by the **total number of rings** (N=1/2/3/≥4), so all the rings of one cast are the
  same art.
- State 1→2 also builds a `meteor_incoming` object ⇒ **the client draws the warning meteor too** (the server does not have
  to draw it again).
- The assets related to the maze in the official folder
  `metadata/effects/spells/monsters_effects/atlasexiles/orion/`:
  `maze_meteor.ao`, `maze_meteor_fall.ao`, `maze_meteor_impact.ao`, `maze_meteor_incoming.ao`, `maze_meteor_loop.ao`,
  `maze_warp.ao`, `blocking_01.ao` / `blocking_02.ao`, `exithere.ao`.

## The big meteor and the crater at the centre of the rings

What lands in the middle of the rings is not a small meteor (that is the star rain's), it is **one very large meteor
dropping straight down and digging a big crater**:

- The warning: `maze_meteor_incoming.ao` (the `incoming` clip of the same rig, the AirRush sound + the ground circle of
  `meteor_incoming.pet`), placed at the centre of the rings, alive until the hit.
- The meteor: `maze_meteor.ao` (the `meteor_01` clip, `MazeMeteor/MeteorWhoosh` + `meteor_head.pet`/`meteor_wind.pet`),
  starting 1.92 s before the hit and alive 1.2 s past it.
- The crater: `patches` (the ground damage area of 10945) = the radius at the centre of the rings
  `17 + 15×(number of rings-1)`, for 8 s — a player caught inside keeps losing health.

## What the server should send

| # | what to send | field / value | when |
|---|---|---|---|
| 1 | one action of type `OrionCircleMazeBlast` (`ActionTypes.HASH16` **42185**, ActiveSkills row 1572 / ActionType 1037) | the skill stat **10844** = the number of rings N (10949/10950/10951 ⇒ N=1/2/3, the value follows the stat set 10910/10911/10912) | at the moment of the cast, right after the existing `Cast(...)` |
| 2 | the same | the skill stats **3248** `base_secondary_skill_effect_duration` = 1000 ms per ring and, on the 5-ring row, **358** `base_skill_effect_duration` = 6000 ⇒ the client's total duration = (`skill_effect_duration` + N × `secondary_skill_effect_duration`)/1000 s | the same |
| 3 | the action's payload | u32 state = 0, u8 flag = **1**, u32 duration = 0, u32 elapsed = 0, u32 x, u32 y = the maze's centre (in cells) | the same |
| 4 | the damage | still through the server's `hits.Add(...)`: the landing point = the centre, the damage = `17 + (rings-1)*15`, the trigger moment = the body's contact | unchanged from today |
| 5 | the art the server still has to draw | the wall `blocking_02.ao` (clip 2 loop, scale 1.5, one every 2.6 cells), the exit `ExitHere.ao`, the centre meteor `maze_meteor.ao` (`MiscAnimated` 3690), the teleport `maze_warp.ao` | unchanged from today |
| 6 | the art that can be dropped | the warning meteor `maze_meteor_incoming.ao` — in state 1→2 the client builds a `meteor_incoming` object itself; compare a frame capture first, then remove the server's | after the action is sent |

The points:

- In client ids the action's attributes are **10845** = the number of rings, **3250** = the milliseconds per ring,
  **360** = the base milliseconds.
- **Do not send the rings one by one**: the client builds them itself, one every 0.2 s (`0x141D1CFA0` →
  `0x141D1D060`), the server only has to give the number of rings.
- Redraw the wall's radius with the client's **20 × N + 3** (`0x143A6AE58`, if it is 4) instead of today's 12 / 20 / 28
  — note that this is **one radius for the whole maze**, not one per ring (the unit is still open, see still open 1).
- `OrionBeamBomb` (13449) and the maze are mutually exclusive: only one of them can exist at a time.
- The warning circle, the big meteor and the crater at the centre are the server's to draw: the client's
  `OrionCircleMazeBlast` only builds the rings when the action's state carries flag=1 and the centre.

## Still open

1. The unit of the radius: which coordinate system `20 × N + 0x143A6AE58 − 1` uses (the client's internal unit vs
   cells; 1 cell = 10 world units), and the value of `0x143A6AE58`.
2. **Who is `apparition`**: the entity the client looks up by name; how the server should create it (or how to bypass it
   with flag=1 and the coordinates).
3. Who writes the byte `+848`, and the real exit condition of state 2.
4. Whether the `+840` / `+888` that the preparation function computes or the `+840` in the server's payload is written
   last (the order is not confirmed; they may overwrite each other).
5. Whether the client's rings **can block the player**: what the client builds are effect objects, and the collision is
   still the server's job (the walls still have to be drawn by the server).
