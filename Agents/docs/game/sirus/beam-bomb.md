# The beam bomb (10973 `AtlasExileOrionBeamBomb` / 10974)

## In one line

The body bursts out four **continuously rotating** beams at the centre of the arena. The effect is **10973 `AtlasExileOrionBeamBomb`** (ActiveSkill **1574**, CastTime **3000 ms**, `Animation` **28 = `SpellProjectileUpwardFire`** → stance1's `clone_beam_start_01`; the ultimate form `…BeamBombUber` = **10974**, slot **39 = `SpellBuffChaos`** → `clone_beam_start_uber_01`).

**The client action class `OrionBeamBomb` does not draw the beams itself** (**observed**): none of its 15 override slots builds an effect or a mesh, and the "client object handle" at `+880` is only released, never created ⇒ **the beam entities have to come from the server**. The only dynamic quantity it gives a definite value for is the **sweep angular velocity, 10°/s** (`0.17453292f` rad/s). What the client draws by itself is what hangs off the cast clip: `clone_beam_start_01` carries **4 arm/hand trails** (`cloneBeam.trl`) ⇒ the official "four arms" = **the client's trails (normal form only) plus the server's beams**, two layers stacked; the Uber clip has no trails, so nearly all of the light comes from the server.

## Client data (observed)

| Item | Value |
|---|---|
| GrantedEffects | `AtlasExileOrionBeamBomb` = **10973** (1574 / 3000 / Animation 28); `…BeamBombUber` = **10974** (1574 / 3000 / 39); `…BeamBombKitavaGauntlet` = **10975** (no animation) |
| ActionType / ActiveSkill | `ActionTypes.json` row **1039** `OrionBeamBomb`, **HASH16 = 13449 (0x3489)**; ActiveSkill **1574** `orion_beam_bomb` (Types [1,7,6,71,30,27,41]) |
| `.act` binding | `atlasexile5.act`: slot 28 `SpellProjectileUpwardFire = "clone_beam_start_01"` (**no effect clause**); slot 39 `SpellBuffChaos = "clone_beam_start_uber_01"` |
| clip events | `clone_beam_start_01`: `contact` @0.5 / 0.566667 / 0.638597 / 0.728070 / 1.45, `teleport` @0.5; rocks·pointlight·eyes off @0, on @0.55125. Uber: `contact` @0.82999 / @0.94344 (**the contact point coordinates were not taken**) |
| clip trails | `clone_beam_start_01` **4** `Metadata/Particles/monster_effects/AtlasExiles/Orion/on_act/beam_03/cloneBeam.trl` (`L_arm_FX`/`R_arm_FX` 0.144→0.5221/0.5228; `R_hand_FX`/`L_hand_FX` 0.65775→1.38); Uber **no trails** |
| beam art rows | `MiscBeams` **240** `AtlasExileOrionBeamBombBeam` → `Orion/orb_beam.ao` (`__col2` 700); **239** `…ChargeBeam` → `Orion/fusion_beam.ao` (900); `MiscAnimated` 3774 `…Explosion` → `Orion/maze_meteor_impact.ao` |
| duration / damage | StatSet 10934 (the numbers below are **our `Stats.json` row numbers**; the client id = the row number + 1): 358 `base_skill_effect_duration` = **13000 ms**; 3248 `base_secondary_skill_effect_duration` = **5000 ms**; 1105 physical→fire **25%**; 1108 physical→chaos **25%**; 2748 **-50%** against the player's summons; BaseEffectiveness 0.325 (10936 = 0.40) |
| debuff | BuffDefinitions **1525 `orion_beam_bomb_slow`**: each stack is "damage taken +10%, speed -6%", **BuffLimit 8**; visual 1268 (`Art/2DArt/BuffIcons/CorruptionDegenDebuff.dds` + `player_aura.epk` / `essence_corruption.epk`) |

Searching the whole client data set for `beambomb` only hits the rows listed above — **the two arena object templates are not in the client tables** (see below).

## What the client draws by itself / what the server must draw

- **The client**: the cast animation (3 s wind-up, `clone_beam_start_01`) plus 4 arm/hand trails (`cloneBeam.trl`, normal form). The hooks for 10973 in the `.ot` **do exist**: `on_clone_beam_start_01 → Call( start_clone_teleport )`, `on_event_teleport_for_clone_beam_start_01 → Call( do_clone_teleport )`, `on_end_clone_beam_start_01 → Call( finish_clone_teleport )`, and there is also `preload_buff_visual = "orion_beam_bomb_slow"`.
- **The server**: **the four rotating beams themselves** (the action class does not draw them, the `+880` handle is only released, never created), the angular velocity, the hit tests, and the `orion_beam_bomb_slow` stacks (buff 1525). The ultimate form (10974) has no trails in the client clip ⇒ the server has more to add.

## Client implementation (decompiled)

**observed** = read straight out of the disassembly; **inferred** = derived from the call relationships.

### Class card

| Item | Value |
|---|---|
| Class name / HASH16 | `OrionBeamBomb` / **0x3489 (13449)** |
| Factory / allocation | `sub_14127E960` → (thunk) **`sub_1412D0520`** (550 B, three branches 876/332/858) / **992 bytes** (allocated by `sub_142C8CB50`) |
| ctor | no separate ctor; constructed in place by the shared action ctor `sub_1419E9B10` with type 0x3489 |
| vtable | main table **`off_1433801B8`**; `+792` → `off_14337DAE0`; `+800` → `off_14337DA80`; intermediate table `off_1435885C8` (overwritten as soon as it is written) |
| dat table / ctx attributes | **no** dat table (no `Data/` string in the ctor); it **does not read ctx attributes** (no attribute read through `sub_141940AA0`, only the base class's generic 2322/48/12674/876/22504) |

The sibling tables `OrionCircleMazeBlast` = `off_1435875A0` and `OrionMeteor` = `off_143588AE8`: **the slot numbers can be compared across them, the function addresses have to be looked at one by one**. Members (all zeroed, **observed**): `+808` state (0/2/3/4) · `+812` nearby count in a word · `+864` animation clock timestamp · `+872` main countdown · `+880` client object handle · `+896` second countdown · `+900` rotation angle. Override slots (first block 0..164, code cluster `0x141D18xxx–0x141D1Axxx`): 0 deleting destructor `sub_141D18B30` · 4 destructor `sub_141D19160` · 31 scan nearby entities `sub_141D18C90` · 35 per-frame state machine `sub_141D19020` · 41 state transition `sub_141D18DA0` · 74 release +880 `sub_141D191B0` (**inferred**) · 86 serialise `sub_141D1A520` (**inferred**) · 87 deserialise `sub_141D1A780` (**inferred**) · 138 clone `sub_141D18AF0` (allocates 912 bytes) · 153 start `sub_141D18EA0` · 159 cycle action A `sub_141D19710` · 160 wrap-up `sub_141D19BC0` · 161 intermediate phase `sub_141D19F10` · 162 cycle action B `sub_141D1A130`.

### State machine

`+808` takes the values 0/2/3/4. **state 2 = charge** (**observed**): a countdown on `+872`; when it expires, slot 159 (cycle action A) is called every frame; at start-up, if `+812 == 0` (no entity nearby carries the tag `0x9B8D`), it **is invalidated straight away** and ends early. **2→3 = fire** (**observed** + **inferred**): slot 41 sets `+808` to 3, `sub_141D19910` computes the target position and orientation of this beam, and then the entity at `+296` is sent "event 29" (an event record with tag 29, through `sub_141EA0C60`) ⇒ **inferred**: this is an entity-level event that makes the monster play an action or an effect of its own; **this class news up no object**. **state 4 = rotate** (**observed**), each frame:

- the angle at `+900` grows by 0.17453292 rad/s (10°/s);
- when the second countdown `+896` runs out, slot 162 (cycle action B, vtable offset 1296) runs and `+896` is reloaded from `+872`; otherwise `+896` counts down;
- after that, if `+896` is at or below 0, the action sets bit `0x10` at `+290` (the base class's "invalidation" bit, as in the other Actions) and calls the end notification (vtable offset 592).

**The 3 → 4 transition point was not located**.

### Serialisation (what the client receives)

- **observed**: slot 86 `sub_141D1A520` (1222 B) and slot 87 `sub_141D1A780` (1222 B) mirror each other in length, the latter is the reader ⇒ **inferred** that they are a serialise/deserialise pair; **the exact field layout was not worked out**.
- **observed**: `+880` (8 bytes, the client object handle) is only released in this class (the cleanup paths of slots 4/74), **it is never created**; the clone in slot 138 allocates **912** bytes again (≠ 992), so the clone is smaller than the body.
- **Conclusion**: sending only the Action itself leaves the client with the timing and the rotation rate and **no beam on screen**. For the picture to appear, the "beam/projectile" entity has to be sent to the client as well, and its handle has to be put into this Action's serialisation (slot 86). When the server sends no entity the client **shows nothing and does not crash** (with `+880` at 0 the release path is skipped).

### Hit tests and damage

- **observed**: the client action class **does no damage**. Slot 31 does two things: it stores the entity's animation clock (field `+660` of the component at `entity+152`) into `+864`; it counts the nearby entities whose 16-bit tag at `+56` is `0x9B8D` and stores the count in the word at `+812`.
- **observed**: the constant **`0x9B8D` (39821)** is this class's only external dependency. It is **not** `hash("release")` (that is 0x0746): 0x9b8d has **0 hits** across the 6191 event names of the game's monster `.ao` files, the 798 event names of the character `.ao` files and the 23362 attribute names of the Stats table. **inferred**: it is a pseudo-tag internal to the framework, roughly meaning "an entity a beam can lock onto / that a beam can be hung on".
- **observed**: all the damage data is in the granted effect's StatSet (see the "Client data" table); the client only plays the animation and the hit window, the resolution is always the server's. The debuff is BuffDefinitions **1525**, +10% damage taken and -6% speed per stack, **8 stacks at most**, and each stack's duration is set by `base_secondary_skill_effect_duration` = 5000 ms.

### What the client shows

- **observed**: in the action class's 15 override slots there is **no** `Metadata/Effects/Effect` string, **no** 584-byte Effect allocation through `sub_142C8CB50`, **no** `sub_141F6E890` (attach entity), **no** `sub_141F6A910` (create an object from an attribute) ⇒ **it creates nothing visible**. The whole client has only two entry points that create something visible: `Effect` (a 584-byte instance, the resource entry is the fixed string `Metadata/Effects/Effect`) and `sub_141F6A910` (creates an object for an owner from an attribute id at a position); this class touches neither.
- **observed**: the two arena object templates (**who places them is still open**): `Metadata/Monsters/AtlasExiles/OrionArenaObjects/OrionBeamBomb.ot` (`Animated { animated_object = "Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/beam_core.ao" }`, `on_end_start → PlayAnimation(idle)`, `on_end_end → Delete()`; `no_serialise = true`, `object_size = 0`, `blocking = false`) and `…/OrionBeamBombClone.ot` → `Metadata/Monsters/AtlasExiles/AtlasExile5_BeamClone.ao` (the same rig as the body, **no TrailsEffects/ParticleEffects**, default_animation `trap_beam_01`; on spawn it plays `teleporting/orion_teleport_movement.ao` plus three epks, and `Delete()` after the `fade_out` state). `beam_core.ao` (3330 B) is a **"core" stretched between two endpoints** (bone groups `root`/`aux_line`, particles `beam_core/idle.pet` + `start_once.pet`, sound `…/Sirus/Effects/Beam/Core.loop.ogg`), **no contact at all, all of it at t=0** ⇒ the damage is still computed by the server.

## What the server should send

1. **The cast**: send the granted effect **10973** (normal) / **10974** (ultimate) to the player's target; the client plays `clone_beam_start_01` / `clone_beam_start_uber_01` accordingly. CastTime **3000 ms**; the start position is the body's current position (`Position(now)`). Keep the existing `Cast(c, id, 10973, at, now)`.
2. **The beam entities (required, one per beam)**: `ServerBeamEffect` (`InstanceEntities.BeamEffectObject`, the client parses it at `0x1416710c0`): `U16 row+1` = **240** (`AtlasExileOrionBeamBombBeam` → `Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/orb_beam.ao`, `__col2` 700 ≈ 7 cells wide), **239** during the charge (`…ChargeBeam` → `Orion/fusion_beam.ao`, 900); start `I32 x, I32 y` (world unit = cells × 10) + `U32 startObject` (hung on the body) + **the bone name** (flag `0xD7`; **without a bone the start point sits on the ground**); end `I32 x, I32 y` + `U32 endObject`, the end = the start + the arm length × (cos a, sin a), `a = starting angle + i·π/2` (i = 0..3); put the handle into the Action's serialisation (slot 86). Alternative (unresolved): use the arena object `Metadata/Monsters/AtlasExiles/OrionArenaObjects/OrionBeamBomb.ot` instead (it holds the stretchable `Orion/beam_core.ao`) — but who places it, and whether the client can have the server spawn it, are both unconfirmed.
3. **Rotation**: angular velocity **10°/s = 0.17453 rad/s** (the only quantity the client's data backs, 36 s per revolution); the starting angle can be random.
4. **Duration**: `base_skill_effect_duration` = **13000 ms** (read from the data, not hard-coded); the beams run from "the cast + 3000 ms of charge".
5. **Damage**: the hits are computed by the server (one line segment per arm, the player is hit by standing in an arm; the client's only feedback is the number of nearby entities carrying the `0x9B8D` tag); each debuff stack is BuffDefinitions **1525**: **+10% damage taken, -6% speed** per stack, **8 stacks at most**, each stack lasting `base_secondary_skill_effect_duration` = **5000 ms**; damage conversion **25% physical→fire + 25% physical→chaos** (1105/1108), **-50%** against the player's summons (2748), BaseEffectiveness 0.325 (10934) / 0.40 (10936). The 5 `contact`s the official client has on the clip (@0.5 / 0.566667 / 0.638597 / 0.728070 / 1.45) are contact windows, their coordinates were not taken ⇒ use the server's own hit test first and align afterwards.
6. **Do not draw it twice**: in the normal form the client already has 4 `cloneBeam.trl` arm/hand trails (inside the cast clip only), so the server stacking the same arm trails on top is "drawing it twice"; in the ultimate form (Uber) the client has no trails and the server has to supply them.
7. **The ultimate form**: use 10974 + slot 39 (`clone_beam_start_uber_01`) and distinguish the parameters.
8. **Do not expect the action class to draw for us**: when the client has no entity handle it simply shows nothing and does not report an error; conversely, do not treat the fields other than `+880` as required.

## Still open

1. **The arm length and the hit radius**: the client data has no field for them.
2. **How the four arms are drawn officially**: in the normal form the client already draws 4 trails; `clone_trap_beam.ao` is the particle piece of the clone's Die Beam, only a stopgap.
3. **Who places the two arena object templates**; the `SirusFight.cs` `Bomb()` comment calls 10973 the "doom geyser under the player's feet" — one effect id, two meanings; which one is the official behaviour is unconfirmed.
4. What the tag `0x9B8D` actually is (the name does not resolve; "release" has been disproved).
5. Who creates the object `+880` points at, and what type it is; the field layout of the serialisation in slots 86/87 (the two 1222 B functions were not taken apart line by line).
6. The state 3 → 4 transition point was not located; the exact meaning of `+872` in state 4 (the cycle length) is unconfirmed.
7. What the client does with "event 29" is unverified; the radius of slot 31's "nearby list" is unknown.
