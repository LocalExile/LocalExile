# The corridor "Die" (`beam_barrage_01`, 10948 `AtlasExileOrionCorridorBlast`)

## In one line

The official "build the corridor" skill is **10948 `AtlasExileOrionCorridorBlast`** (ActiveSkill **1571**, CastTime **7500 ms**,
`Animation` **22 = `SpellAreaOfEffect`** → the body's `.act` `"beam_barrage_01"`).
That clip carries three things at once: `create_corridor` @**0.461 s**, the hand glow on and off (`addhandglowepk` @0.945 /
`removehandglowepk` @1.425), and **8 × `beam_right` / `beam_left`** (1.1734 / 1.8246 / 2.4850 / 3.1556 /
3.8177 / 4.4966 / 5.1671 / 5.8124). The walls of the corridor and the fireball in the middle are **not drawn by the client**, the server places all of it.

> This one used to be mistaken for "the body's multi-segment beam". The real multi-segment beam is `beam_03` (8098), see [body-barrage.md](body-barrage.md).

---

## Client data (observed)

| Item | Value |
|---|---|
| Skill | `AtlasExileOrionCorridorBlast` = **10948**, ActiveSkill **1571**, CastTime **7500**, `Animation` **22 `SpellAreaOfEffect`** |
| Body binding | `atlasexile5.act`: `SpellAreaOfEffect = "beam_barrage_01"` |
| Action class | `OrionCorridorBlast` (one of the 5 in the Orion family), **it hangs on its own action type**; `Cast(10948)` really instantiates it (10948 → ActiveSkill 1571 → ActionType 1036 works) |
| `beam_barrage_01` events | `cloak_off`/`pointlight_off`/`rocks_off` @0, `teleportstart` @0.100, `teleportend`+`rocks_on`+`pointlight_on`+`eyes_on` @0.400, `create_corridor` @0.461, `addhandglowepk` @0.945, `beam_right` @1.1734 / `beam_left` @1.8246 / `beam_right` @2.4850 / `beam_left` @3.1556 / `beam_right` @3.8177 / `beam_left` @4.4966 / `beam_right` @5.1671 / `beam_left` @5.8124, `removehandglowepk` @1.425, `finish` @6.536 |
| Hand coordinates | `beam_right` uses `(-60, 0, -104.29)`, `beam_left` uses `(+60, 0, -100)` model units ⇒ **0.6 cells to either side, 1.0 cell in front** |
| `.ot` hooks | `on_event_addhandglowepk` → `AddEffectPack('…/on_act/beam_barrage_01/handGlow.epk')`, `on_event_removehandglowepk` removes it |
| Ultimate form | `beam_barrage_abort_01/02/03` (`SpellAreaOfEffectFire/Cold/Lightning`), no `addhandglowepk` |

## What the client draws by itself / what the server must draw

- **Client**: the `beam_barrage_01` action (the teleport, the raised arm, the 8 hand waves) and the hand glow epk.
  The `create_corridor` event itself builds nothing — **the class is instantiated, but it only changes state, it makes no art** (see "the corridor's structure" below).
- **Server**: the walls on both sides (collision + the picture), and the fireball in the middle that rolls from the body towards the player.

## Client implementation (decompiled)

**observed · identity**: `ActiveSkills.Id = orion_corridor_blast` (1571), `ActionType 1036` → `OrionCorridorBlast` in `ActionTypes.json` (Hash 0xA00D = 40973). The class name string is at `0x143267cf8`; it is registered once through a one-time-init wrapper with the factory at `0x141985AE0`. The factory first probes the ctx attributes `876(0x36C)` / `332(0x14C)` / `858(0x35A)` to pick one of three variant ctors, and only if none of them matches does it allocate **824 bytes** and call the ctor at `0x141D1DE90`. The ctor passes the hash 0xA00D to the base Action ctor (`0x1419E9B10`) as the type number, installs the main vtable **`0x143588048`** and the secondary interface table `0x14323E408` at `+168`, and zeroes `+792/+800/+808/+812/+814/+816`.

**observed · the main vtable has 420+ slots** (the interface of the base class `Actions::Action` is extremely wide). This class has 37 slots of its own (31/35/38/40/86/87/96/138/157–173/176/180/207/211/217/250/262/263/314/329/335–338/378); its own methods cluster in `0x141D1D090–0x141D1F2xx`, and the slots inside that address range are: 31 `0x141D1DF90` (start), 35 `0x141D1E1E0` (every frame), 38 `0x141D1D430`, 40 `0x141D1E3D0` (animation event), 86 `0x141D1EC20`, 87 `0x141D1EDB0`, 96 `0x141D1E660`, 138 `0x141D1DE50`, 157 `0x141D1E280`, 158 `0x141D1E460`, 159 `0x141D1E8F0`, 160 `0x141D1E9F0`, 161 `0x141D1EA00`, 163 `0x141D1EFA0`, 180 `0x141D19160`, 211 `0x141D19020`, 250 `0x141D191B0`, 262 `0x141D1A520`, 263 `0x141D1A780`, 378 `0x141D1F220`.
**The range 176–338 shares the same functions with `OrionBeamBomb`** (the `0x141D18C90` set) ⇒ the two classes have the same source, it is the base class of the "arena beam".
Note: **you cannot find the overridden slots by diffing the two vtables slot by slot** (`OrionBeamSweep`'s base vtable `0x143587090` has a different slot count, the diff gives 256+ differences); use "clusters of function addresses + member offsets" instead.

**observed · slot 31 start `0x141D1DF90` (how the direction is decided)**: it first packs the cast target (the u16 at `+282`, the u8 at `+284`, the caster's int at `+136`, the ints at `+720` and the u8 at `+696`) and hands it to `0x1419C73C0` / `0x1404CE9B0`. Then there are two cases:

- **If the target cell at `+792` or `+796` is not 0 ⇒ it is taken as it is** (the server can set it, see row 8 of "What the server should send").
- Otherwise the client picks it itself: in steps of **10° (0.17453294 rad)** from 0 to 2π, each step computes one cell (its own position + the offset at `+304`/`+308`),
  and requires the room check `0x141D888F0` to return ≥ 3 (the room allows it) and a squared distance to the target actor `< 0xE10 = 3600` (**60 cells**);
  on a hit it stores the cell at `+792` and stops; finally it sets `+820 = 1`.

⇒ **the direction in which the ground spikes appear is picked by the client itself**: the first allowed angle within 60 cells, in steps of 10°.

**observed · slot 40 animation event `0x141D1E3D0`**: it reads the event hash (a u16 at `+56` of the event) and dispatches `0xA37A` (`create_corridor`) → slot 158 (vtable `+1264`), `0xC1A4` (that is the hash 0x41A4 of `beam_left`) → slot 161 (`+1288`, side 0), `0xDBED` (`beam_right`) → slot 161 (side 1), `0xD9CF` → slot 160 (`+1280`); everything else goes to the base class `0x1419E9D30`. ⇒ **the two animation events `beam_left` / `beam_right` are consumed by the corridor class itself**, and the 8 moments of the hand beams (1.173–5.812 s) are bound to them.

**observed · slot 158 `0x141D1E460` (event `-23686` = `create_corridor`)**
The origin is the position of the entity at `+296`; when the byte `+812` is 0, **the direction vector `+804 / +808` = origin − BOSS position**.
A direction length > 0.01 ⇒ `+813 = 1`, **the timer `+816 = 0.3 s`** (the raw bit pattern 1050253722 = float 0.3),
**the corridor length `+800 = |direction| + 75 cells`**.

**observed · slot 161 `0x141D1EA00` (the two sides)**
The animation events **`-15964` = `beam_left` (side 0)**, **`-9235` = `beam_right` (side 1)**
⇒ **`beam_left` / `beam_right` are animation event ids, not stats** (`0xC1A4` / `0xDBED` are their signed 16-bit values).
It does a **segment query** (`0x141D235B0`) from the BOSS position to "origin + direction × length", and on a hit it calls slot 159 (vtable `+1272`) with **33 / 34**
(one damage id per side), and **derives a child action** at the corridor origin (the child's `+304` = BOSS position + the direction `+804` / `+808`),
and that child action draws the slash ⇒ **the official art `corridor_slash_01.ao` (MiscAnimated 3692) is drawn by that child action**.

**observed · slot 157 `0x141D1E280` (every frame)**
When the timer reaches 0 ⇒ it does a **segment query** (`0x141D22FC0`) from the BOSS to the far end of the corridor (half width 1.0: the raw values `-1082130432` = `-1.0f`, `1065353216` = `1.0f`),
on a hit it calls slot 159 with **32** ⇒ **the whole corridor is swept every 0.3 s**.

**observed · slot 159 `0x141D1E8F0` (resolution)**
When the byte `+814` is 0 it sends the damage packet (`0x141EA0C60`, the packet carries the half width 1.0 / -1.0), then sets `+814 = 1` ⇒ **it resolves only once**;
slot 160 `0x141D1E9F0` only sets `+814 = 1` (no resolution).

**Conclusion (observed)**: apart from the slash of the derived child action, none of `OrionCorridorBlast`'s methods creates an effect/particle/mesh; it only "picks a direction + records state + consumes the beam_left/right events", and the `.act` gives `SpellAreaOfEffect = "beam_barrage_01"` **no** `effect "…" "animate"` clause, so there is no on_act art.
**The hit shape of the corridor = a segment from the BOSS towards the target cell, `|direction| + 75` cells long, about 1 cell wide, swept every 0.3 s;
the two sides land one extra hit each at the start of the corridor (damage ids 33 / 34)**. Which way the corridor points is **picked by the client itself** (10° steps, within 60 cells, what the room allows),
and to match the official the server has to set the target cell itself and put it into the action state (the same trick as the maze: the serialised state of the action).

## Official art (observed)

The rows in `data/tables/MiscAnimated.json` that belong to the corridor:

| Row | Id | AOFile | What it is |
|---|---|---|---|
| 3692 | `AtlasExileOrionCorridorNuke` | `Orion/corridor_slash_01.ao` | **the body of the corridor's beam/slash** |
| 3814 | `AtlasExileOrionCorridorProjectileExplode` | `Orion/maze_meteor_impact.ao` | **the burst of the corridor's fireball** |
| 3619 | `AtlasExileOrionNukeBeamImpact` | `Orion/nuke_beam_blast.ao` | the burst of the nuke beam (**not** the corridor's) |

- `corridor_slash_01.ao`: rig `corridor_slash_01/blackGuardslash.amd`, the particle `corridor_slash/lightning.pet` on the bone group `line`, the sounds CorridorWhoosh/CorridorSlash; it lands at the **mouth of the corridor** (the client derives a child action and puts it at the corridor origin).
- The **big red burning ball** in the middle of the corridor is an official projectile: `data/tables/Projectiles.json` row 858 `Metadata/Projectiles/AtlasOrionCorridorBlast` → AOFiles `Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/death_projectile/death_projectile.ao`, LoopAnimationIds `["proj"]`, ImpactAnimationIds `["impact"]`, ProjectileSpeed 800.
- `death_projectile.ao`: default_animation `proj` (the burning ball itself + the `idle.pet`/`length` trail), the `impact` clip is `xplod.pet` (the explosion); mesh `Orion_deathProjectile.sm`.

⇒ The `nuke_beam_blast.ao` we give the corridor's fireball today **picks the wrong row**: the corridor should use `maze_meteor_impact.ao` (3814).
The corridor's 8 beams are **not drawn at all** (today `ServerBeamEffect` rows 37/227 stand in, the official has no Beam component step).

`corridor_nuke.ao` (`AtlasBosses.SirusCorridorArt`) has **no row** in `MiscAnimated`,
it is a different set of assets (the folder also holds a `corridor_nuke_ao_pass0.mat`), do not use it yet.

## What the server should send

| # | What to send | Field / value | When |
|---|---|---|---|
| 1 | Animation | `Cast(10948)` (skill id = `0x4000 + 2`) ⇒ the client plays `beam_barrage_01` | at the moment of the cast |
| 2 | The 8 beam effects | `EffectObject`, `AnimatedObject = …/Orion/corridor_slash_01.ao` (MiscAnimated 3692), `Parent` = the body, `Bone` = `R_Weapon`/`L_Weapon` alternating (in the clip `beam_right` uses `(-60,0,-104.29)`, `beam_left` uses `(+60,0,-100)` model units ⇒ 0.6 cells to either side, 1.0 cell in front), `AttachKind=0`, `AttachFlags=3` | at the 8 event moments of the clip: 1.1734 / 1.8246 / 2.4850 / 3.1556 / 3.8177 / 4.4966 / 5.1671 / 5.8124 s |
| 3 | The fireball and its burst | 8 `death_projectile.ao` one after another along the centre line (spaced out ⇒ it looks like one ball flying); replace `nuke_beam_blast.ao` with **`maze_meteor_impact.ao`** (3814) for the burst, the position still interpolated along the centre line by `(i+1)/8` | when each fireball arrives (placed 0.75 s before the contact, lifetime 0.3 s) |
| 4 | The walls | `blocking_02.ao` (clip 2 loop, scale 1.5) + `Tutorial_Blocker` collision, one segment every 6 cells, one on each side, as long as the corridor | right after the cast |
| 5 | Damage | **one 7-cell circle per fireball** (the centre moves over time), not 8 lines of the same `at → corridorEnd` | per fireball |
| 5b | The hit of the corridor body | the official is **a segment from the BOSS towards the target cell**, `|direction|+75` cells long, about 1 cell wide, **swept every 0.3 s**, resolved once; the two sides land one extra hit each at the start (ids 33/34) | every 0.3 s |
| 6 | The direction of the ground spikes | by default **left to the client**: the action class picks the first direction the room allows within 60 cells, in steps of 10° (`0x141D1DF90`); a direction the server computes itself is redundant | the client does it by itself |
| 7 | The ultimate form | the three variants `beam_barrage_abort_01/02/03` are not implemented yet | — |
| 8 | The direction of the corridor (optional) | **the server can set the target cell in the action state**: when slot 31 starts and `+792` or `+796` is not 0, the client takes it as it is and does not sweep for a direction. To match the official it has to decide itself | at the moment of the cast |

Key points:

- **Do not send a `ServerBeamEffect` for the corridor any more**: the official corridor beam is an effect entity (row 3692), not a stretched Beam row.
- Leaving the direction to the client only holds if the action we send is really instantiated — the corridor's ActiveSkill 1571 → ActionType 1036 works.

## Still open

1. The spacing of the walls (6 cells) and the offset (11 cells) are estimates; the official corridor runs through the arena from one side to the other.
2. While the body casts, `body.LifePhases.DamageTakenPercent = 20` (`Sirus.Combat.cs` `Corridor()`); this is a guess that "the body takes 20% less damage while it builds the corridor", with no data behind it.
