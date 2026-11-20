# The meteor and the star rain ("Rain of stars!", 10957 / 10145 / 10141 / 10942)

## In one line

One official meteor = **one object on the client**: `Meteor_Rain.ao` (`MiscAnimated` row `AtlasExileOrionMeteorRain`,
HASH32 **2133148678** (= `0x7F254406`, the HASH32 of `MiscAnimated` row 3622; the client looks it up through
`0x14228BED0` with that hash). This attribute id is **hard-coded** in
the `OrionMeteor` action class, **each action instance builds exactly one** of them (slot 157 `0x141D1F330` news it
only once), and it lands on the action's landing point. ⇒ **The "whole rain" does not grow out of the client on its
own**: the rain = N action instances, and the N actions have to be sent by the server one at a time.
Today the server sends only 1 `10145` (ActiveSkill 1449 ⇒ `GeometryTrigger`, and its row has no ground marker ⇒ **the
client draws nothing**), so today there is **damage but no rain**.

- `10141` places nothing in the live client because its dat row (Id 91) has an empty `GroundMarker`, not because it is a
  trigger.
- The ActiveSkill of `10942` is **1570 `orion_meteor`** (ActionType 1035 = `OrionMeteor`), and so are 10943-10946 (the influence, uber and ground-effect versions):
  **every effect on ActiveSkill 1570 builds the `OrionMeteor` class and its `Meteor_Rain.ao`**; having no animation slot only means it carries no clip.

## Client data (observed)

| item | value |
|---|---|
| The action classes | `OrionMeteor` = `ActionTypes.json` row **1035**, HASH16 **28504 = 0x6F58**; `GeometryTrigger` = row **959**, HASH16 **10221 = 0x27ED** |
| ActiveSkill | **1570** `orion_meteor` → ActionType 1035; **1449** `geometry_trigger` → ActionType 959 |
| `10957 …CreateMeteorsSmallDaemon` | AS 1449, CastTime **2170**, Animation **34**, SkillID **139** |
| `10956 …CreateMeteorsSmall` / `10145 …MeteorRectangle` / `10146 …RectangleInfluence` | AS 1449, CastTime 2170, Animation 34, SkillID 134 / 100 / 103 |
| `10141 AtlasExilesOrionMeteorArena` | AS 1449, CastTime 2000, Animation **22**, SkillID 91 |
| `10942 AtlasOrionMeteor` / `10943`, `10944` / `10945 AtlasOrionMeteorGroundEffect` / `10946 …Temp` | AS 1570, CastTime 1000 (10942's Animation is empty, SkillID 1) |
| The animation slots | 34 = `SpellAreaOfEffectLightning`, 22 = `SpellAreaOfEffect` (`Animation.json`) |
| The apparition's / the body's `.act` | the apparition: `SpellAreaOfEffectLightning = "meteor_shower_01"`, `SpellAreaOfEffect = "carpet_bomb_01"`; the body has `beam_barrage_abort_03` / `beam_barrage_01` in the same slots |
| The art | `MiscAnimated` row 3622 `AtlasExileOrionMeteorRain`, HASH32 **2133148678** (`0x7F254406`) → `…/AtlasExiles/Orion/destruction_assets/meteors/Meteor_Rain.ao` |
| The scorched ground | BuffDefinitions 1449 `atlas_orion_meteor_ground` → GroundEffectTypes 17 `OrionMeteor` → GroundEffects 159 `meteor_ground.ao` (`["loop"]`, the end is `fadeout`) |

### Ground markers (`GeometryTrigger.dat`) — the meteor family is nothing but empty rows

| the dat row Id | the granted effect it belongs to | `GroundMarker` | `__col3`/`__col4` |
|---|---|---|---|
| 91 / 100 / 103 / 134 / 139 | 10945 / 10942 / 10943 / 10946 / 10946 | **all empty** | 60/10000, 20/2000, 20/2000, 12/800, 8/800 |

Only 38 of the 490 rows carry a marker, and **not one of the meteor family does** ⇒ on the `GeometryTrigger` path the
client draws nothing.

## Client implementation (decompiled)

### The class card: `OrionMeteor`

| item | value |
|---|---|
| The factory / its size | `0x14127ED30` (the registration wrapper `0x14126EED0`, the initialisation thunk `0x14127ED20` → `0x14141A240`) / **824** |
| The ctor | **`0x141D1F120`** (114 B): calls the base Action ctor `0x1419E9B10` with HASH16 0x6F58, the actor and the ctx; installs `0x143588AE8`, puts the listener table `0x14337B358` at `+792`, clears `+800`, resets the handle at `+808` (`0x141EC9890`), clears `+816` |
| **The main vtable** | **`0x14337C358`** (at runtime; the ctor installs `0x143588AE8` first and the factory overwrites it at the end). The siblings: BeamBomb `0x1433801B8`, CircleMazeBlast `0x1435875A0` |
| The copy ctor | `0x141D1F1A0` (it keeps `0x143588AE8`); the clone slot `0x14141A120` puts the copy back to `0x14337C358` |
| The members | `+304`/`+308` = **the landing point** (2 × int32, in **cells**); `+800` = the byte "did the server give a position"; `+808` = **the meteor entity handle** (`0x141EC9890`); `+816` = the callback |
| Its own slots (the runtime table) | 0 the destructor `0x14141A190` / **38** `0x141D1F220` the landing-point test / **86** `0x141D1F720` the serialisation / **87** `0x14141A3F0` the deserialisation / 138 `0x14141A120` clone / **153** `0x141D1F6C0` the activation / **157** `0x14141AA00` the build + the orientation / **158** `0x14141AB20` the hit effect / **159** `0x14141A660` the area query + the damage + the meteor crater; 160 `0x141414890` the destructor (only in the ctor's table) |
| The second table | `0x14337B358` is installed at `+792` = the **animation event listener**: slot 9 `0x141D1F630` calls back into the host's slot 159 when the event `0x9B8D`(39821) arrives |

### The state machine

**Observed**: `OrionMeteor` **has none of the state/count/index fields the common action classes have** — `+808` holds the
meteor entity handle that `0x141EC9890` stores, `+816` is the callback, and the class has no float timers in
`+840..+904` either. It is an **event-driven** chain:
network deserialisation → build the meteor entity → the meteor entity's own animation events → the hit settlement.

| stage | the condition to enter | the code | what it does |
|---|---|---|---|
| ① parse | the server hands the action to the client | slot 87 `0x14141A3F0` | it reads the u8 flag into `+800`; when flag≠0 it reads 8 bytes (x,y) into `+304/+308` (in **cells**), **immediately** calls `0x141D1F330` to build the meteor entity and computes the orientation, and finally reads the shared animation block |
| ② activate | the action is activated | slot 153 `0x141D1F6C0` | when `+800 == 0` (the server gave no position) it **clones itself** (slot 138) → the copy is bound to the target → the copy's `+800 = 1` → the copy does the building |
| ③ build | — | slot 157 `0x14141AA00` → `0x141D1F330` | it builds the meteor entity (the effect id **2133148678**), the position = `+304/+308` |
| ④ orient | right after the build | the same | `stat 349 != 0` → the orientation is 0; otherwise the orientation = `1.5707964 − the jitter + atanf(300/the distance)`, the jitter ±0.25 rad (LCG) |
| ⑤ flight | the meteor entity's animation is playing | the client entity itself | the client **does not tick**; the landing point is fixed, and the settlement only happens when the animation reaches the impact frame |
| ⑥ hit | the animation event `0x9B8D`(39821) arrives | `0x141D1F630` (slot 9 of `0x14337B358`) | three gates: ① the listener's 8th field is not 0; ② the event type ∈ {0} ∪ [17,27]; ③ the event id is 0x9B8D ⇒ it calls the host's slot 159 (vtable `+1272`) |
| ⑦ settle | ⑥ | slot 159 `0x14141A660` | the radius query → settle the damage one target at a time → blow the meteor crater (see below) |

- **Observed**: slot 38 `0x141D1F220` (266 B) runs the base class's preliminary checks and then walks the global entity
  table `0x1445BF224`, **comparing the distances to the landing point** to find the entities
  ⇒ the landing point is fixed before the start; this class does not choose a spot of its own. Slot 157 is the **only
  place in this class that produces anything visible**.
- **Observed**: slot 159 is `0x14141A660` in the runtime table and `0x141D1F7F0` in the ctor's table (**it does not blow
  the crater**) — two versions of the same logic.

### Serialisation (what the client receives)

**Observed**: the write side = slot 86 `0x141D1F720`, the read side = slot 87 `0x14141A3F0`. Field by field:

| order | type | meaning | the condition |
|---|---|---|---|
| 0 | `u8` | `hasPosition` → `+800` | always present |
| 1 | `i32` | the landing point's cell x → `+304` | only when flag≠0 |
| 2 | `i32` | the landing point's cell y → `+308` | only when flag≠0 |
| 3 | `u32` | N = the length of the skill name in characters (the number of UTF-16 code units) | the shared block, always present |
| 4 | `byte[2N]` | the skill name (UTF-16LE) | the shared block |
| 5 | `i32` | the skill id (−1 = none); the client builds the skill instance from it, the scale = field 7 | the shared block |
| 6 | `i32` | **the client skips it and does not read it** (the write side is the animation object `+420`) | the shared block |
| 7 | `f32` | the scale parameter of the skill instance | the shared block |
| 8 | `i32` | the animation object `+472` | the shared block |
| 9 | `i32` | the id of the first entry of the animation object's `+512/+520` vector (−1 when it is empty) | the shared block |

- **Observed**: the unit of the coordinates is the **cell** (the conversion between world coordinates and the packet is
  ×10). The write side (`0x140E273F0`) writes the u8 `+800` → when it is not 0 it writes `+304`/`+308` → it takes
  the animation of the **meteor entity** at `+808` (`0x14013E1B0`), and the caster's own animation when there is no
  entity → writes that animation block (`0x141DC09A0`).
- **Observed**: the read side takes the u8 → `+800`; when it is not 0 it reads 8 bytes into (x,y) → it **builds the
  meteor on the spot** → computes the orientation → reads the shared block.
- What the client really receives first is the **0x1ab `ActionStarted`**: the caster's cell coordinates + the **target
  cell** + skillId + sequence + flags
  (`InstanceProtocol.ActionStarted`), and it picks the action class by skillId → granted
  effect → ActiveSkill → ActionType.
- **skillId has two numbering schemes (easy to trip over)**: `AtlasExilesBoss5` is driven by its own encounter, and the
  skill ids it publishes are **`0x4000` (`Sirus.Native.cs` `Skill()`) + the index in the client's `GrantedEffectsKeys`**;
  the `0x4020 + index` of `AtlasBosses.Skill()` belongs to the **framework-driven** route (the open-world / the endless
  variant) and is a different set of ids; the two must not be mixed.
  ⇒ **The skillId has to land in the table the body has already registered**, otherwise the client cannot resolve it
  (that is exactly why 10942 goes out as `0x3FFF` on the body).

### Hits and damage

- **Observed**: the radius r = 20 × (stat 5521 + 100) / 100, truncated to an integer, **cells** (stat 5521 = 0 ⇒ 20 cells); the query
  rectangle = the landing point ± (r + the global at `0x143A6AE58`(=5) − 1) / + 5,
  `0x1400F0CF0` (entity kind 23) + `0x141D23BA0` collect it, and each target goes through `0x14193F690`
  (it pairs up with the monster damage list by the action's hash16 at `+282`, the values at `+284` and `+288`, and the target id,
  takes the entry out and applies the damage through vtable `+560`), and finally `0x14193F8C0` settles it.
- ⇒ **The hit test is on the client, the damage numbers come from the server**: the client only answers "who is inside
  the circle", and the server still has to send a damage packet for every one of them.
- **Observed**: this class **has no own slot at `vtable+1304`** (+1304 = slot 163, an empty stub in the base class), the
  hit goes through `+560`.
- **The damage data**: 10942 → ActiveSkill 1570 → StatSet 10903 `{ImplicitStats [620 is_area_damage, 22890], ConstantStats [1105,1107,1108] = 25/25/25(physical turned into fire / lightning / chaos), BaseEffectiveness 1.875}`.
- The `contact` of `Meteor_Rain.ao` @ **1.92 s** is the hit moment of this meteor; the server should line up with it.
  The radius is set by stat 5521 and does not depend on the cast time.

### What the client draws

- **One object** (observed): slot 157 calls `0x141F6A910` with the owner, the attribute 2133148678, the position and mode 2 —
  it instantiates one client object by the **hard-coded attribute id** (not the 584 B `Metadata/Effects/Effect` kind),
  stores the handle in `+808` and registers a 16 B destruction listener. `2133148678` is the HASH32 of `MiscAnimated` row
  3622 `AtlasExileOrionMeteorRain`
  ⇒ **the art is `Meteor_Rain.ao`; the server cannot change the skin, it can only control the landing point**.
- **Three more client effect ids** (observed): the hit effect **33276957** (slot 158 `0x14141AB20`; row 3699
  `OrionGlobalHitEffects` → `Hit_FX.ao`), the meteor crater **2775491209** (slot 159, the scale = the radius; row 3623
  `AtlasExileOrionMeteorImpact` → `impact_Large.ao`), and the `GeometryTrigger` ground geometry **3902695543** (the scale =
  32 × stat / 100 cells, truncated) — **note: 3902695543 = `MiscAnimated` row 2827 `CascadingTriangleSlamEffect` →
  `triangleSlam.ao`, the Synthesis Guardian's triangular slam, and not Sirus's art**; it is the **fallback ground marker**
  the client uses when it has nothing better.
  ⇒ **The client blows the crater itself**, so the server does not have to pay for it again.
- `Meteor_Rain.ao` (UTF-16LE, 3291 characters) carries the whole presentation (observed): the rig
  `…/destruction/meteor_rain/rig.amd` + the skin `meteor.sm`, the clip `meteor_01`; the `contact` @1.92 s; the sounds
  `MeteorWarning` @0.0 (the bone `aux_mid`) and `MeteorWhoosh` @1.5439 (the bone `aux_meteor_direction`); the particles
  `meteor_incoming.pet` @0.0 (the bone group `impact`), `meteor_wind.pet` @0.3464, `meteor_head.pet` @0.3472,
  `meteor_grd.pet` @1.9166; the trail `meteor_trl.trl` @0.3464→1.9481; the screen shake @1.901 s (0.115 s).
  ⇒ **The warning sound, the falling trail, the landing circle, the landing dust and the screen shake are all in this one
  object** — not drawn separately by the server, and not assembled out of markers.
- The apparition's `meteor_shower_01` (`atlasexile5apparition.ao`): only the `contact` @0.2404 s, `AttackWhoosh` @0.7309
  and a hand trail `orion_app_handcast_trails.trl` @0.118→1.559, and **no particles** ⇒ this clip does not draw the rain
  itself; its contact is the hit moment, not the moment the rain is built.
  The scorched ground is not in the action class either: that is a buff visual (`atlas_orion_meteor_ground` →
  `meteor_ground.`, loop→fadeout).

### The sibling class: `GeometryTrigger` (all the logic of the landing-point marker path)

| item | value |
|---|---|
| The factory / its size | `0x141284980` → (the thunk) **`0x1412D1E00`** (579 B) / **968**; there is no ctor of its own, the factory constructs it in place (the base Action ctor `0x1419E9B10` with HASH16 0x27ED) |
| **The main vtable** | **`0x1434480E8`** (`0x14353A728` is only the intermediate step, the factory overwrites it at the end) |
| The members | `+792` = a vector of 16-byte elements (the factory first pushes one `{the geometry id = the value of stat 6433, the .dat file handle}`); `+960` = the Effect handle; `+832` = the "finished" flag |
| The overridden slots | 11 of them: 0 / 74 / 85 / 86 / 110 / 138 / 142 / 159 / 160 / 161 |

- **What `Data/GeometryTrigger.dat` is** (observed): = the GGPK's `data/geometrytrigger.datc64` (113890 B, the first 4 bytes
  `ea 01 00 00` = **490 rows**, **pure binary, 0 strings**).
  The columns are defined in `data/schema/schema.min.json` (48 columns) and `sql/base/client/GeometryTrigger.sql`: column 1
  `GrantedEffect`, column 2 `Animation`, **column 25 `GroundMarker[]` → MiscAnimated**, column 34 `MiscAnimated`, column 39
  `Stats[]`, column 40 `StatValues[]`.
  The client class's initialisation `0x141515790` looks a row up in the table with the value of **stat 6433**, and
  **registers that row's geometry entries only when it hits one; when it finds nothing it registers not a single entry**.
- ⚠️ **stat 6433 never appears once in the whole .datc64** ⇒ when the client takes the flag=0 branch it finds no row
  for 6433 ⇒ **it places nothing** (observed + inferred).
  That is the real reason why "`10141` places nothing in the live client", not "because it is a trigger".
- Slot 160 `0x141516100` (the start): from the row's `+115` length / `+123` pointer (16 B elements) it **picks 1
  marker at random** → builds an Effect (584 B, `Metadata/Effects/Effect`) → applies the marker's values → **a random yaw
  0..2π** (`0x141E35920`) → submits it; it only plays `"start"` / `"loop"` when the row's `+131` is not 0.
- Slot 159 `0x1415159E0`: at the end it plays `"end"`, fires the event **56827** and releases `+960`. Slot 142
  `0x141515B70`: bone anchoring (`"BoneGroups"` / `"frameupper"`) — some triggers hang on a bone instead of the ground.
- ⇒ **For a row whose GroundMarker is empty the client draws nothing** (the meteor family is all empty rows, see the
  table above).

**The serialisation of `GeometryTrigger` (observed)**: a u8 `flag` → when flag≠0: it reads (i32 x, i32 y) → **it spawns
the effect 3902695543 right away, scaled 32 × stat / 100 cells, truncated** → then it reads the three dwords skillId /
scale / id → sets the "finished" flag `+832` to 1; when flag=0: it takes the caster's own animation block + (x,y) + a u32 count of the
entries + the list of 16-byte geometry entries.
⇒ **To make it draw something, send the flag≠0 + coordinates branch**; with flag=0 it can never find the .dat.

## Meteor vs star rain

| | the meteor (the big one of this fight) | the star rain |
|---|---|---|
| The picture | **one very large meteor** drops straight down and digs **one big crater** | **many small projectiles** come down one after another |
| The official art | `maze_meteor.ao` (MiscAnimated 3690 `AtlasExileOrionMazeMeteor`, the rig `blocking_slams/meteor/rig.amd`, the mesh `meteormaze.sm`), the warning circle is `maze_meteor_incoming.ao` of the same rig (the `…CircleMazeMeteorMarker` next to 3690, MiscAnimated 3735) | `destruction_assets/meteors/meteor_rain.ao` (MiscAnimated 3622 `AtlasExileOrionMeteorRain`; the clip `meteor_01`, **with its own contact @1.92 s**, the warning sound `MeteorWarning`, the falling trail `meteor_wind.pet`, the landing dust) |
| Where it appears | in the middle of the maze's rings (also the one of the first phase) | the apparition's `Rain of stars!` (10954/10956), one landing point per meteor |

`maze_meteor.ao` has no contact event.

## What the server should send

1. **Through the client (the right way)**: for every landing point send a 0x1ab whose granted effect has **ActiveSkill
   1570** ⇒ the client builds the `OrionMeteor` class → it builds **1** `Meteor_Rain.ao`. The skillId comes from the
   body's skill table: **10945** (`AtlasBosses.SirusMeteorGroundEffect`) ⇒ `0x4000 + 29 = 0x401D`; **10942 is not in the
   table**, and `Skill()` gives it a pseudo id outside the table ⇒ the client cannot resolve it (**inferred**).
   The target cell = the landing cell (`+304`), and the times staggered by `contact - 1.92 s`; 6 meteors are 6 packets.
   The client builds `Meteor_Rain.ao` itself (the warning + the fall + the landing + the screen shake) **and blows the
   meteor crater itself** (effect 2775491209, the scale = the radius) ⇒ **the server must not paint the same picture
   again, nor the crater again**. The landing point distribution can only be laid out by the server (the client does not
   spread a rain by itself).
2. **The hit area**: the server computes the radius the same way the client does (20 × (stat 5521 + 100) / 100, truncated,
   cells, stat=0 ⇒ 20 cells; the query has ±5 to spare), and sends a damage packet **per target**; the damage numbers use
   the StatSet 10903 figures (BaseEffectiveness 1.875, 25/25/25 physical to fire / lightning / chaos), timed to `contact`
   @1.92 s.
3. **The arena's ground marker has to be sent with flag≠0 + the coordinates**: the client's `GeometryTrigger` only spawns
   the effect 3902695543 when flag≠0 (= `CascadingTriangleSlamEffect` → `triangleSlam.ao`, **Synthesis's fallback marker,
   not Sirus's art**; the scale 32 × stat / 100 cells, truncated);
   with flag=0 it can never find the .dat for stat 6433 and places nothing ⇒ **10145 / 10141 on the flag=0 path place
   nothing**, and to draw anything they have to go out with the branch that carries the coordinates.
4. **Do not use 10141/10145/10146/10956/10957 to draw the rain**: ActiveSkill 1449 ⇒ `GeometryTrigger` ⇒ their rows have
   no ground marker and the client will certainly draw nothing; they can only serve as "damage + timing".
5. **Do not mix the two plans**: if the server keeps placing `meteor_rain.ao` itself (the current state), then it
   **must not** also send a 1570 0x1ab, or there will be two of everything. Placed by the server, `meteor_rain.ao` carries
   its own 1.92 s landing rhythm, so placing it 1.92 s early and letting it live past the hit is enough; `maze_meteor.ao`
   has no contact event, so its landing moment has to be aligned with the hit frame (the same 1.92 s for now).
6. The **scorched ground** needs a separate buff (10945/10946 → `atlas_orion_meteor_ground` → `meteor_ground.ao`), or the
   server paints the decal itself.

## Still open

1. **Where the landing point `+304` comes from**: is it the 0x1ab's targetX/targetY — it is stuck at the step "where is the
   action instance's ActionData filled in". Send an action with AS 1570 and see whether the client's `Meteor_Rain.ao`
   lands on the 0x1ab's target cell — this one decides whether the "through the client" route can work.
2. **The server's counterpart of stat 5521**: 5521 in `Stats.json` is `melee_splash_off_hand_only`, which does not match
   the meaning of a "radius factor"; the client's stat id space is not fully isomorphic with the server's (5744 →
   `total_skill_area_of_effect_+%_including_final` and 436 → `base_skill_area_of_effect_+%_` do match).
3. **The origin of the event id `0x9B8D`(39821)**: it is in none of the `.ao` files and in none of the client's tables (it
   may be an engine-internal number).
4. The entity type `0x1445BF224` of `0x141D1F220` and its distance threshold are not located yet.
5. The two qwords of the 16-byte geometry entries in the flag=0 branch of `GeometryTrigger` are unexplained; the meaning of
   `__col3..__col47` in the 48 columns of the `.dat` is unexplained.
6. Whether the area query in slot 159 sends a "hit" packet upstream (does the server have to wait for it to settle) is not
   confirmed — but **the damage numbers have to come from the server anyway**, so it does not change what to do.
7. **What the "big blue shell" really is**: does the client's object stop at the `impact` (the warning shell)?
8. **The official landing point distribution, how many per wave and the spacing** — there is no data for that, it can only
   be compared in the live client.
