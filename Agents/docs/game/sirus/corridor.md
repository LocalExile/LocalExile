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
  The `create_corridor` event itself builds nothing — **the class is instantiated, but it only changes state, it makes no art** (see "the corridor's real structure" below).
- **Server**: the walls on both sides (collision + the picture), and the fireball in the middle that rolls from the body towards the player.

## Still open

1. **The hit semantics are wrong**: now all 8 `Hit` calls use the same `at → corridorEnd` line with radius 7 ⇒ a player standing in the corridor is hit 8 times;
   the official resolves only the moment the **fireball rolls onto him**. It should become "one 7-cell circle per fireball", with the centre moving over time.
2. Whether the `create_corridor` event builds anything on the client (a void / a warning) is not verified; for now we place the walls ourselves.
3. The spacing of the walls (6 cells), the offset (11 cells) and the length (100 cells) are estimates; the official corridor runs through the arena from one side to the other.
4. While the body casts, `body.LifePhases.DamageTakenPercent = 20` (`:499`); this is a guess that "the body takes 20% less damage while it builds the corridor", with no data behind it.
5. The three variants of the ultimate, `beam_barrage_abort_01/02/03`, are not implemented at all.

## Client implementation (decompiled)

**observed · identity**: `ActiveSkills.Id = orion_corridor_blast` (1571), `ActionType 1036` → `OrionCorridorBlast` in `ActionTypes.json` (Hash 0xA00D = 40973). The class name string is at `0x143267cf8`; the wrapper `_crtInitOnceExecuteOnce("OrionCorridorBlast", sub_141985AE0, …)`; the factory `sub_141985AE0`: it first probes the ctx attributes `876(0x36C)` / `332(0x14C)` / `858(0x35A)` to pick one of three variant ctors, and only if none of them matches does `sub_142C8CB50(824)` allocate **824 bytes** and call the ctor `sub_141D1DE90`. In the ctor `sub_1419E9B10(a1, 0xA00D, …)` writes the hash in as the type number, the main vtable is **`off_143588048`**, the secondary interface table is `[+168] = off_14323E408`, and `+792/+800/+808/+812/+814/+816` are zeroed.

**observed · the main vtable has 420+ slots** (the interface of the base class `Actions::Action` is extremely wide). This class's own methods cluster in `0x141D1D090–0x141D1F2xx`; the slots inside that address range are: 31 `0x141D1DF90` (start), 35 `0x141D1E1E0` (every frame), 38 `0x141D1D430`, 40 `0x141D1E3D0` (animation event), 86 `0x141D1EC20`, 87 `0x141D1EDB0`, 96 `0x141D1E660`, 138 `0x141D1DE50`, 157 `0x141D1E280`, 158 `0x141D1E460`, 159 `0x141D1E8F0`, 160 `0x141D1E9F0`, 161 `0x141D1EA00`, 163 `0x141D1EFA0`, 180 `0x141D19160`, 211 `0x141D19020`, 250 `0x141D191B0`, 262 `0x141D1A520`, 263 `0x141D1A780`, 378 `0x141D1F220`.
Note: **you cannot find the overridden slots by diffing the two vtables slot by slot** (`OrionBeamSweep`'s `off_143587090` has a different slot count, the diff gives 256+ differences); use "clusters of function addresses + member offsets" instead.

**observed · slot 31 start `sub_141D1DF90`**: it first takes a target object, packs `{u16 [a1+282], u8 [a1+284], int [caster+136], int [a1+720], u8 [a1+696]}` and hands it to `sub_1419C73C0`, `sub_1404CD900` takes the object, `sub_1404CE9B0(obj, packed)` lands it. Then: if `*(a1+792)` or `*(a1+796)` is not 0 → `*(a1+820)=1`; otherwise it **sweeps from 0 to 2π in steps of 0.17453294 rad (10°)**, and for every angle it computes one cell `own + (*(a1+304), *(a1+308))`, and takes it when `sub_141D888F0(room, &cell, 0) >= 3` (does the room allow it) and the squared distance to the target is `< 0xE10 = 3600` (**60 cells**); on a hit it writes `+792 = cell` and breaks; finally `+820 = 1`.
⇒ **the direction in which the ground spikes appear is picked by the client itself**: the first allowed angle within 60 cells, in steps of 10°. The server should not compute a direction itself (the `aim` the server computes now is redundant).

**observed · slot 40 animation event `sub_141D1E3D0`**: it reads the event hash `*(_WORD*)(a2+56)` and dispatches `0xA38A` → virtual call `+1264` (slot 158), `0xC1A4` (that is the hash 0x41A4 of `beam_left`) → `+1288` (slot 161, third argument 0), `0xDBED` (`beam_right`) → `+1288` (slot 161, third argument 1), `0xD9CF` → `+1280` (slot 160); everything else goes to the base class `sub_1419E9D30`. ⇒ **the two animation events `beam_left` / `beam_right` are consumed by the corridor class itself**, and the 8 moments of the hand beams in `BeamList` (1.173–5.812 s) are bound to them — somebody still has to draw them (see below).

**Conclusion (observed)**: `OrionCorridorBlast` **creates no visible art** — none of its methods creates an effect/particle/mesh, it only "picks a direction + records state + consumes the beam_left/right events". The `create_corridor` of `beam_barrage_01` (0.461 s) and the 8 `beam_left/right` (1.173–5.812 s) only change state on the client, **the beams must be sent by the server**; and the `.act` gives `SpellAreaOfEffect = "beam_barrage_01"` **no** `effect "…" "animate"` clause, which confirms that there is no on_act art.

**What the server should send**: the animation (`beam_barrage_01`) + the `ServerBeamEffect` of the 8 beams (8 for the ultimate) + the damage check for the ground spikes.

## The corridor's real structure (2026-11-17 afternoon, lead's decompilation)

**Class**: `OrionCorridorBlast`, HASH16 **0xA00D = 40973**, ctor `sub_141D1DE90` (**824 bytes**, main vtable **`off_143588048`**).
37 slots of its own (31/35/38/40/86/87/96/138/157–173/176/180/207/211/217/250/262/263/314/329/335–338/378),
of which **the range 176–338 shares the same functions with `OrionBeamBomb`** (the `sub_141D18C90` set) ⇒ the two classes have the same source, it is the base class of the "arena beam".

**observed · slot 31 start `sub_141D1DF90` (how the direction is decided)**
It first packs `{u16 [a1+282], u8 [a1+284], int [caster+136], int [a1+720], u8 [a1+696]}` for `sub_1419C73C0` / `sub_1404CE9B0` and sets it as the cast target.
Then there are two cases:

- **If `*(a1+792)` or `*(a1+796)` is not 0 ⇒ it is taken as the target cell as it is** (the server can set it, see row 8 of "What the server should send").
- Otherwise the client picks it itself: in steps of **10° (0.17453294 rad)** from 0 to 2π, each step computes one cell `own + ([a1+304], [a1+308])`,
  and requires `sub_141D888F0(room, &cell, 0) >= 3` (the room allows it) and a squared distance to the target actor `< 0xE10 = 3600` (**60 cells**),
  on a hit it writes `+792 = cell` and breaks; finally `+820 = 1`.

**observed · slot 158 `sub_141D1E460` (event `-23686` = `create_corridor`)**
The origin is the position of the entity at `a1+296`; when `a1+812 == 0`, **`a1+804 / a1+808 = origin − BOSS position`** (the direction vector).
A direction length > 0.01 ⇒ `a1+813 = 1`, **the timer `a1+816 = 0.3 s`** (the raw bit pattern 1050253722 = float 0.3),
**the corridor length `a1+800 = |direction| + 75 cells`**.

**observed · slot 161 `sub_141D1EA00` (the two sides)**
The animation events **`-15964` = `beam_left` (third argument 0)**, **`-9235` = `beam_right` (third argument 1)**
⇒ **`beam_left` / `beam_right` are animation event ids, not stats** (`0xC1A4` / `0xDBED` are their signed 16-bit values).
It does a **segment query** `sub_141D235B0` from the BOSS position to "origin + direction × length", and on a hit it calls slot 159 (vtable+1272) with **33 / 34**
(one damage id per side), and **derives a child action** at the corridor origin (`v20+304 = BOSS position + [a1+804] / [a1+808]`),
and that child action draws the slash ⇒ **the official art `corridor_slash_01.ao` (MiscAnimated 3692) is drawn by that child action**.

**observed · slot 157 `sub_141D1E280` (every frame)**
When the timer reaches 0 ⇒ it does a **segment query** `sub_141D22FC0` from the BOSS to the far end of the corridor (half width 1.0: `-1082130432` = `-1.0f`, `1065353216` = `1.0f`),
on a hit it calls slot 159 with **32** ⇒ **the whole corridor is swept every 0.3 s**.

**observed · slot 159 `sub_141D1E8F0` (resolution)**
When `a1+814 == 0` it sends the damage packet (`sub_141EA0C60`, the packet carries the half width 1.0 / -1.0), then sets `a1+814 = 1` ⇒ **it resolves only once**;
slot 160 `sub_141D1E9F0` only does `a1+814 = 1` (no resolution).

**Conclusion (observed)**: **the hit shape of the corridor = a segment from the BOSS towards the target cell, `|direction| + 75` cells long, about 1 cell wide, swept every 0.3 s;
the two sides land one extra hit each at the start of the corridor (damage ids 33 / 34)**. Which way the corridor points is **picked by the client itself** (10° steps, within 60 cells, what the room allows),
and to match the official the server has to set the target cell itself and put it into the action state (the same trick as the maze: the serialised state of the action).

Raw dumps: `logs/tmp/re/_raw/lead-corridor-1.txt` (the slot table + the start), `lead-corridor-2.txt` (slots 157/158), `lead-corridor-3.txt` (slots 159/160/161 + the segment query).

## Official art (`MiscAnimated` rows, observed)

The rows in `data/tables/MiscAnimated.json` that belong to the corridor:

| Row | Id | AOFile | What it is |
|---|---|---|---|
| 3692 | `AtlasExileOrionCorridorNuke` | `Orion/corridor_slash_01.ao` | **the body of the corridor's beam/slash** |
| 3814 | `AtlasExileOrionCorridorProjectileExplode` | `Orion/maze_meteor_impact.ao` | **the burst of the corridor's fireball** |
| 3619 | `AtlasExileOrionNukeBeamImpact` | `Orion/nuke_beam_blast.ao` | the burst of the nuke beam (**not** the corridor's) |

⇒ The `nuke_beam_blast.ao` we give the corridor's fireball today **picks the wrong row**: the corridor should use `maze_meteor_impact.ao` (3814).
The corridor's 8 beams are **not drawn at all** (today `ServerBeamEffect` rows 37/227 stand in, the official has no Beam component step).

`corridor_nuke.ao` (the `SirusCorridorArt` at `AtlasBosses.cs:102`) has **no row** in `MiscAnimated`,
it is a different set of assets (the folder also holds a `corridor_nuke_ao_pass0.mat`), do not use it yet.

## What the server should send

| # | What to send | Field / value | When |
|---|---|---|---|
| 1 | Animation | `Cast(10948)` (skill id = `0x4000 + 2`) ⇒ the client plays `beam_barrage_01` | at the moment of the cast |
| 2 | The 8 beam effects | `EffectObject`, `AnimatedObject = …/Orion/corridor_slash_01.ao` (MiscAnimated 3692), `Parent` = the body, `Bone` = `R_Weapon`/`L_Weapon` alternating (in the clip `beam_right` uses `(-60,0,-104.29)`, `beam_left` uses `(+60,0,-100)` model units ⇒ 0.6 cells to either side, 1.0 cell in front), `AttachKind=0`, `AttachFlags=3` | at the 8 event moments of the clip: 1.1734 / 1.8246 / 2.4850 / 3.1556 / 3.8177 / 4.4966 / 5.1671 / 5.8124 s |
| 3 | The burst of the fireball | replace `nuke_beam_blast.ao` with **`maze_meteor_impact.ao`** (3814), the position still interpolated along the centre line by `(i+1)/8` | when each fireball arrives (placed 0.75 s before the contact, lifetime 0.3 s) |
| 4 | The walls | `blocking_02.ao` (clip 2 loop, scale 1.5) + `Tutorial_Blocker` collision, one segment every 6 cells, one on each side | right after the cast |
| 5 | Damage | change to **one 7-cell circle per fireball** (the centre moves over time), do not use 8 lines of the same `at → corridorEnd` | per fireball |
| 5b | The hit of the corridor body | the official is **a segment from the BOSS towards the target cell**, `|direction|+75` cells long, about 1 cell wide, **swept every 0.3 s**; the two sides land one extra hit each at the start (ids 33/34). Our implementation has none of this "sweep the corridor" check | every 0.3 s |
| 6 | The direction of the ground spikes | by default **left to the client**: the action class picks the first direction the room allows within 60 cells, in steps of 10° (`sub_141D1DF90`) | the client does it by itself |
| 7 | The ultimate form | the three variants `beam_barrage_abort_01/02/03` are not implemented yet | — |
| 8 | The direction of the corridor (optional) | **the server can set the target cell in the action state**: when slot 31 starts and `a1+792` or `a1+796` is not 0, the client takes it as it is and does not sweep for a direction. To match the official it has to decide itself | at the moment of the cast |

Key points:

- **Do not send a `ServerBeamEffect` for the corridor any more**: the official corridor beam is an effect entity (row 3692), not a stretched Beam row.
- The direction of the ground spikes that our `Corridor()` computes itself is redundant (the client picks it), but **that only holds if the action we send is really instantiated**
  — the corridor's ActiveSkill 1571 → ActionType 1036 works, so this should be possible (to be confirmed in the live client).
- The walls, the length and the offset of the corridor are all estimates today: the official corridor **runs through the arena from one side to the other** (still open, see below).

## The corridor's orb (2026-11-17, implemented in the evening)

The **big red burning ball** in the middle of the corridor is an official projectile, not an effect we placed ourselves:

- `data/tables/Projectiles.json` row 858 `Metadata/Projectiles/AtlasOrionCorridorBlast` → AOFiles `Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/death_projectile/death_projectile.ao`, LoopAnimationIds `["proj"]`, ImpactAnimationIds `["impact"]`, ProjectileSpeed 800.
- `death_projectile.ao`: default_animation `proj` (the burning ball itself + the `idle.pet`/`length` trail), the `impact` clip is `xplod.pet` (the explosion); mesh `Orion_deathProjectile.sm`.
- The official burst art = MiscAnimated 3814 `AtlasExileOrionCorridorProjectileExplode` → `maze_meteor_impact.ao` (the server draws it on a hit, in the non-beam branch).

The corridor body has an official slash too: MiscAnimated 3692 `AtlasExileOrionCorridorNuke` → `corridor_slash_01.ao` (rig `corridor_slash_01/blackGuardslash.amd`, the particle `corridor_slash/lightning.pet` on the bone group `line`, the sounds CorridorWhoosh/CorridorSlash) — it lands at the **mouth of the corridor** (the client's way: it derives a child action and puts it at the corridor origin).

**What the server should send** (the current implementation):
1. The corridor length = the distance from the BOSS to the target cell **+75 cells** (the client's own rule, `Extend(at, aim, Distance(at,aim)+75)`); the walls are laid out that long too.
2. The 8 contacts (1.173/1.825/…/5.812 s): one `corridor_slash_01.ao` at the mouth of the corridor; 8 `death_projectile.ao` one after another along the centre line (spaced out ⇒ it looks like one ball flying).
3. Damage: the corridor body is a **one-cell wide line** (the client sweeps it every 0.3 s and resolves it once, `DrawBeam: false` draws no laser); what is really lethal is the **7-cell circle** where the orb passes (the burst of `maze_meteor_impact.ao` is there too).
