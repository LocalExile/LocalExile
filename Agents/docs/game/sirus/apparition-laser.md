# The apparitions and their beams (10941 `AtlasOrionBeamSweep` + 8614/8615 `move_daemon`)

## In one line

The two-hand beam of the apparition (`AtlasExile5Apparition`) is **two pieces stitched together** in the official
implementation:

1. the skill itself = 10941 `AtlasOrionBeamSweep` (client action class `OrionBeamSweep`, ActionType 1034) — it **cannot draw
   any light** (`state` is never set to 1, see §3.2);
2. two `AtlasExiles5BeamTargetDaemon` / `…Daemon2` (MonsterVarieties 8387/8388) are raised by **GE 8614/8615
   `AtlasExileBoss5BeamTargetDaemon1/2`** (ActiveSkill 1174 `move_daemon`, ActionType **758 `MoveDaemon`**) — `MoveDaemon`
   **only translates and turns these two invisible daemons**, by the dat row and the offset at ActionData `+496` (±90°); it
   creates nothing visible of its own.

⇒ **Neither `OrionBeamSweep` nor `MoveDaemon` draws the two beams, and `ServerBeamEffect` does not draw them either.**
The daemon's own art `InvisibleFire.ao` draws nothing (only the `cyl`/`mid` bone groups), and the generic daemon model
`Metadata/Monsters/Daemon/Daemon.ao` that its script animates **has not been extracted** ⇒ the beams themselves are still
unresolved. Drawing the apparition beam with `ServerBeamEffect` (`Sirus.Combat.cs`) is a stand-in, not the official path.
The HASH16 of `MoveDaemon` is **35726 = 0x8B8E** (not 35982).

---

## Client data (observed)

| item | value |
|---|---|
| the skill itself | GE **10941** `AtlasOrionBeamSweep` → ActiveSkill **1569** `orion_beam_sweep`, CastTime **4200 ms**, `Animation` **51 = `HeavyStrike`**, StatSet1 10902 |
| same family | 10967–10972 `AtlasOrionBeamSweepNoFinalExplosion{Short,Medium,Long}{,Uber}` → slot 15 = `DoubleStrike` (in the apparition it is `double_beam_hands_small_01`) / slot 51 |
| who can cast it | GE **10941** appears only in the `GrantedEffectsKeys` of 8378 `AtlasExile5Apparition` (8379 `AtlasExile5ApparitionDetached` does not carry 10941; it carries 10967–10972); the GEs of `AtlasExile5ApparitionDaemon` (8383) are [10141, 10945, 10957, 10946, 10142, 10143] (meteor-field kind), **unrelated to the beams** |
| the two daemons | GE **8614** `AtlasExileBoss5BeamTargetDaemon1` (SkillID **53**, StatSet1 8603), **8615** `…Daemon2` (SkillID **54**, StatSet1 8604) → ActiveSkill **1174 `move_daemon`**, CastTime 1000, **`Animation` is empty** |
| `move_daemon` | `ActiveSkills.json`: `ActionType 758`, `ActiveSkillTargetTypes [1,2,4]`, `ActiveSkillTypes [21,31]`, `SkillTotemId 22`, `IsManuallyCasted true` |
| `MoveDaemon` class | `ActionTypes.json` row 758 → `HASH16` **35726 = 0x8B8E** |
| daemon monsters | `MonsterVarieties.json` row **8387** `Metadata/Monsters/InvisibleFire/AtlasExiles5BeamTargetDaemon` / row **8388** `…Daemon2`: `MonsterTypesKey 1232`, `MovementSpeed 18`, `ObjectSize 1`, `ModelSizeMultiplier 100`, `Name "Invisible"`, `ACTFiles [Metadata/Monsters/InvisibleFire/InvisibleFire.act]`, `AOFiles […/InvisibleFire.ao]`, `BaseMonsterTypeIndex …/InvisibleFireMoveDaemonOrion` / `…Orion2`, `GrantedEffectsKeys []`, `AISFile NoAi.ais` |
| dat rows | `data/tables/MoveDaemon.json` (103 rows, 35 columns `__col0..__col34` + `MonsterVarietiesKeys`): **row 52** id=53 → MonsterVarieties **8387**; **row 53** id=54 → **8388**; both rows have `__col5` = **+90 / −90** (the angles in the whole table are multiples of 15), `__col11` = 0, `__col25` = true, `__col27` = 3 |
| apparition clip events | `atlasexile5apparition.ao` → `double_beam_hands_01`: `daemon1` @0.91027 (−559.025, −160.465, 0), `daemon2` @0.91027 (+559.0, −160.465, 0), `contact` @0.91027, `stop_beam` @2.72873, `blast` @3.43070 (0, −753.937, 0). `daemon1`/`daemon2`/`stop_beam` **appear in only this one `.ao` in the whole orion tree** |
| event types | all three events are `StandardEventType`, **with no data field** (observed) — there is no evidence that "the client attaches an entity automatically on the `daemon` keyword" |
| apparition rig | the apparition's `rig.ast` (268 231 B) contains **`L_Weapon` / `R_Weapon`**, L/R_wrist_jntBnd, hip_jntBnd; it **does not contain** `shoulder_l`/`l_arm`/`arm_r` |
| apparition `.ot` | `atlasexile5apparition.ot`: `define_shared_state = "beaming; x_pos; y_pos; z_pos;"`; `on_double_beam_hands_01 = { PlayAttachedEffect('…/Orion/apparitionSurge.ao', hip_jntBnd); }` |
| `apparitionSurge.ao` | `SkinMesh { }` is empty, the only bone group is `PTEmit false aux_mid aux_line`, particle `surgeEmit.pet` @0.0, `Lights` is empty ⇒ **a blob of light at the waist, not a beam** |
| daemon art | `InvisibleFire.ao`: `SkinMesh { }` is empty; bone groups `root`, `frameupper`, **`cyl`(R_jnt01 R_jnt02)**, **`mid`(R_jnt01 R_jnt03)**; `ParticleEffects`/`Lights`/`SoundEvents` are all empty |
| daemon script | `invisiblefire.ot`: `collision_hull_type "projectile"`, `blocking false`, `base_cannot_be_damaged 1`, `untargetable_by_monster_ai 1`, `corpse_usable false`, `Animated{animated_object "Metadata/Monsters/Daemon/Daemon.ao"}`, `DiesAfterTime{}`, client `Targetable{no_highlight true; targetable false}`; `invisiblefiremovedaemonorion.ot` / `…orion2.ot` = `extends InvisibleFire` + `DiesAfterTime{}` + `Positioned{serialise_position = true}` (**no client block**) |

`±559` / `−160.465` / `−753.937` are all **model units** (100 = 1 cell) ⇒ the two daemons are 11.2 cells apart and 1.6 cells
in front; `blast` is 7.54 cells straight ahead.

---

## Client implementation (decompiled)

### 3.1 `MoveDaemon` (ActionType 758) — it only places, it draws nothing

**Class card (observed)**

| item | value |
|---|---|
| class name / type number | `MoveDaemon` / **0x8B8E (35726)** |
| factory | `sub_1412B3F90` → thunk **`sub_1412D6A80`** (410 B, three branches 876/332/858) |
| allocation / ctor | **808** bytes; the shared action ctor `sub_1419E9B10` with type 0x8B8E |
| main vtable | **`off_14356DC78`**; type name string `Utility::TypeIDImpl::PrettyName<class Loaders::MoveDaemon>` @ `0x14356f61f` |
| dat table | `Data/MoveDaemon.dat` (19 characters) → cache `sub_141CC1550` → `sub_141CC15F0` → row selection `sub_142257C90` into `+792`, the row id read from ctx attribute 6433 |
| dat row handle | stored at `+792` (**not** the vtable) |

**Overridden slots (observed)**: 153 `sub_141CBC0A0` (start), 314/315 `sub_141CC17A0` (returns `off_14356E648`), 317
`sub_141CC1790` (RTTI descriptor, the name contains `CreateStampedeObject_MTXQuicksilverBestiaryStampede_Actions` ⇒ same
origin as the stampede), 351 `sub_141CBDFF0` (translation + turn). Base class slots: 85 `sub_141245F80`
(tests that the byte at `+286` is 0), 86 `sub_1419CF9B0` (writes 1 byte at `+472`), 87 `sub_1419CFA20` (reads 1 byte at `+472`).

**State machine (observed)**: this class has no state machine of its own; that 1 byte at `+472` is its only state.

**Serialisation (what the client receives)**: **only 1 byte** (slots 86/87 read/write `+472`). Position, target and resources
**are not in the packet** — all of them come out of the local dat + ActionData.

**Slot 153 (start)**: it moves only when the byte at `+76` of the dat row ≠ 0. It copies the entity's position into `+304`
(the transform component at `entity+152`, field `+660` = **2×int32 world coordinates**, not a timestamp); **ctx attribute
13701 must be 0**, otherwise the whole MoveDaemon is a no-op; then `sub_141E355A0` sets the animation from that position.

**Slot 351 (translation + turn)**: the offset comes from **ActionData `+496`** (two ints, `sub_140DF5100`); when the entity's
mirror flag `entity+485 & 0x10` is set, **X is negated**; the target = the current transform position + the offset; when there
is displacement, `sub_140E2DD60` computes the angle and `sub_141E35920` sets the orientation (the same function
as GeometryTrigger's random yaw). The function contains **no** `sub_142C8CB50` / `sub_141F6A910` / Effect.

**Hit and damage**: **none**. MoveDaemon does no damage and raises no hit event.

**Client appearance**: **zero**. The whole client has only two entry points that "make something visible" —— ① `Effect` (a
584-byte instance, the resource entry is the fixed string `Metadata/Effects/Effect`) ②
`sub_141F6A910` (creates an object for an owner from an attribute id at a position); MoveDaemon uses neither. The payload is only 1 byte, and a search of
the client binary hits `AtlasExileBoss5` / `BeamTargetDaemon` **0 times**, and three independent lines of evidence all point
to "it only places".

### 3.2 `OrionBeamSweep` (ActionType 1034) — the skill itself, it cannot draw either

**Class card (observed)**: class name string `0x143267c88`; the factory `sub_141985CE0` first probes ctx attributes
876/332/858 to pick a variant, and only if none of them matches does it allocate **832** bytes (`sub_142C8CB50`) and call the
ctor `sub_141D1AC90` (the shared action ctor `sub_1419E9B10` with type 0x4A62); main vtable **`0x143587090`** (the base's: the second-pass constructor `0x14127EE70` runs the same base and then
installs `0x14337C8E0`, [sirus-client](../sirus-client.md)), secondary interface
`[+0xA8] = 0x14323E408`, `[+0x310] = 0`. **The constructor takes no target coordinates at all.**

**7 overridden slots (observed)**: 31 `0x141D1AD80` (start: computes the rate, plays animation **349**, re-anchors itself as
"caster + direction×L", where L comes from the first virtual of the component, keyed by 349: 0→70.0, 1→23.0, 2→46.0, 3→69.0 cells, constants
`0x1436AE80C/608/730/808`), 35 `0x141D1AFF0` (0.1 s fixed step per frame), 40 `0x141D1AF20` (animation events: recognises
`0xA32E`=`stop_beam` → state 2/4, recognises `0x8A6A`=`blast` → state 3), 86 `0x141D1B100` (writes 24 B: a
**client→server** report of `state/t1/t2/posX/posY/rate`), 153 `0x141D1AF10` (only sets `state` at `+0x318` to 1), 157
`0x141D1B230` (solves the two arm endpoints every 0.1 s), 161 `0x141D1BC40` (sweeps after `blast`, radius = the stat value / 100 × 35).

**Endpoint solving (observed)** `sub_141D1B6E0`: each tip is the action's own position turned about the caster by
±75° × (1 − elapsed/rate), constants `0x1436AE828 = 75.0`,
`0x1436ADA7C = π/180` ⇒ the two arms start from "the apparition's hands" and the opening angle closes from ±75° to 0° (a
converging beam).

**Why it cannot draw (observed)**: `state` (`+0x318`) has no initialising write anywhere; the only place that writes 1 is
slot 153, and it has no direct callers (it can only be called virtually), and it waits for the animation event `0x9B8D`, while
`0x9B8D` **does not exist** in any of 4783 monster `.ao`, 45 character `.ao` or 23362 stats ⇒ the state stays 0 and the
endpoints are never solved. The client assertion string `.rdata 0x1435DE800` says the same: "MakeBeam: Must specify either a
to_variable or a non-zero offset, or both".

**Hit and damage**: slot 157 does a ray/box enumeration (it finds the entities near the endpoints), but **the damage decision
is on the server**; the client only reports 24 bytes of state.

**Serialisation (what the client receives)**: the server sends the **skill action + animation + target**; the client sends
back 24 bytes of `state/t1/t2/posX/posY/rate`.

### 3.3 Who draws the two beams (observed + inferred)

`Metadata/Monsters/InvisibleFire/InvisibleFire.ao` (1130 B in the GGPK): rig = `Art/Models/Effects/totems/rig.amd`,
`SkinMesh {}` **empty**, `ParticleEffects {}` **empty**, `SoundEvents {}` **empty**, `Lights {}` empty, `ScreenShake {}`
empty; only `BoneGroups`: `root`, `frameupper` (both bound to `root head_chunk_jnt`),
**`cyl` (`R_jnt01` → `R_jnt02`), `mid` (`R_jnt01` → `R_jnt03`)**.
⇒ **The daemon does not draw a single pixel**: it is a pure anchor (three bones, there for others to connect to). The beam is
either ① **an effect entity** (`clone_trap_beam.ao` = `MiscAnimated` 3729 `AtlasExileOrionCloneBeam`, or
`TripleBeam/triple_beam_0N.ao`), or ② **a line drawn by a geometry action** — the ActiveSkill of `7031 AtlasExileOrionCloneBeam`
is **1242 `geometry_spell`**, and the serialisation of the `+792` child object of `GeometrySpell` carries a **list of target
entities** made of "**a 4-byte entity id + a 1-byte flag**" (`sub_14196E1D0` writes / `sub_14196E530` reads), whose meaning is
"connect from the start point to a set of target entities" — the two daemons would be the two ends of the two beams.
⇒ The most likely official flow (**unverified**: how the server puts the target entity table into the action packet is
unknown): cast `7031` (the geometry action) on the apparition + spawn two daemons as anchors + send those two entity ids to the
client as the target table ⇒ the client draws the two lines with the art that the attribute `0x7A39632A` names.

- **observed**: the client binary has no `AtlasExileBoss5` / `BeamTargetDaemon` string; the apparition `.ot` only hangs
  `apparitionSurge.ao` (a particle with an empty SkinMesh); `daemon1`/`daemon2` are animation events without data;
  `MoveDaemon` creates nothing.
- **inferred**: the official flow = the server spawns two `AtlasExiles5BeamTargetDaemon`(`Daemon2`) entities → casts
  `move_daemon` on them (GE 8614/8615, SkillID 53/54) → `MoveDaemon` uses the dat row (`__col5` = ±90°) and the offset at
  ActionData `+496` to place the two on the left and right hand of the apparition and to turn them. Other daemons of the
  same family draw through their script:
  `invisiblefiremovedaemonignorancebeam.ot` and `maven/invisiblefiremovedaemonmaventargetbase.ot` both hang a client effect
  object in `on_construction_complete` with `AddAttached('…/eyelaser_aoe.ao', <root>/<lock>)`.
- **but**: the scripts of the two Orion daemons, `invisiblefiremovedaemonorion.ot` / `…orion2.ot`, **have no client block**
  (only `DiesAfterTime` + `serialise_position`) ⇒ their visuals can only come from the generic daemon model
  `Animated{animated_object = "Metadata/Monsters/Daemon/Daemon.ao"}`, which **has not been extracted**.

### 3.4 `clone_trap_beam.ao` is the clones' beam art, not the apparition's (observed)

- GrantedEffects 7031 `AtlasExileOrionCloneBeam` has `SkillID` 359, and `GeometryAttack` 359's `Animation` is
  `MiscAnimated` 3729 below. The apparition's own sweeps (10941, 10967) have `SkillID` 1, whose `GeometryAttack` row
  names no art, and the apparition's model is `AtlasExile5Apparition.ao` (`MonsterVarieties` 8378/8379).

- `MiscAnimated` row 3729 `AtlasExileOrionCloneBeam` → `Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/clone_trap_beam.ao`.
- Its structure: rig `clone_trap_beam/rig.amd`, `default_animation = "clonetrap_beam"`, mesh `clone_trap_beam/clonebeam.sm`,
  **`BoneGroups: beam_length false jntBnd_beam aux_beam`** (the same as `triple_beam_0N.ao`: the length is pulled by the
  client from the data), particle `clone_trap_beam/clone_trap_beam_length.pet` hung on the `beam_length` bone group, sound
  `…/Sirus/Effects/Beam/TripleBeam3` hung on `jntBnd_beam`, `ScreenShake` 13 / 0.4 s.
- The clone model `atlasexile5_beamclone.ao` ([clones](clones.md)) carries the contact events **and the
  positions** of `beam_01` / `beam_02`: `beam_01` contact@0.672 position `(-9.46, -91.91, -173.77)`, `beam_02` contact@0.6915
  position `(5.61, -96.42, -179.80)` ⇒ the start point of each hand (model space, 1 unit = 0.01 cell ⇒ about 0.09 cell to
  each side and 1.7 cells in front).
- But its bone groups hold **no beam bone group** (only root / frameupper / eyes / arms / legs / hands / aura) ⇒ **it does not
  draw the beam either**.

⇒ The data names no art for the apparition's two beams; §3.5 is how the client builds them.

### 3.5 The apparition's two beams: there is no daemon → daemon beam code (observed)

- The client hits **zero** times for `DaemonSpecialBeam` / `AtlasExiles5BeamTargetDaemon` / `clone_trap_beam`; among the 121
  action classes there is no Clone/Beam class; the `AtlasExile5` string (0x143443330) has **no code reference**
  ⇒ **there is no daemon → daemon beam code**.
- The apparition's two beams = **two independent geometry beams**: the start point = the contacts `beam_01` / `beam_02` of the
  apparition's clip, the end point = **the entity list the server sends** (the list in the geometry channel state).
- A bone group is only a "name → bone index" index: `sub_1429432F0` (text `bone_group = "name bool bone name…"`),
  `sub_142BBB680` (binary), each entry is 56 B: a name string and a list of bone indices, the lookup by name is
  `sub_1429431E0` (56 callers), bone name → index is `sub_14291B360`
  ⇒ **there is no general "stretch bone group" API**, the length has to be driven by data.
- The length lives on the animated object: `sub_141DCC410` (vtable 0x1435a1a80 slot 28) maps `"beam_length"` to **+556** and
  `"radius"` to **+552**; `sub_141E31CA0` sets it to 0x7FFFFFFF (=automatic); every frame in `sub_141E37DB0` **+556 = +656**
  (the writer of +656 is unresolved).
- The data comes from the **geometry channel**: the format of `sub_14196E530` (read) / `sub_14196E1D0` (write) is
  u32×4 → u32 +240 → list@216 → u8 count → per entry **u32 entity id + u8 flag**; stat 6433 → the Id of the GeometryAttack row.

## What the server should send

1. **Action**: send the apparition an `AnimationUpdate` that plays `double_beam_hands_01` (10941, animation slot 51 =
   `HeavyStrike`).
2. **The two daemons (they place, but draw no light)**: the client has **no daemon → daemon beam code** (§3.5), so the daemon
   entities can only serve as **bone anchors**; spawn `Metadata/Monsters/InvisibleFire/AtlasExiles5BeamTargetDaemon` (8387)
   and `…Daemon2` (8388), take the positions from the clip constants `daemon1` (−559.025, −160.465, 0) / `daemon2`
   (+559.0, −160.465, 0) (model units → 5.59 cells to each side, 1.6 cells in front), and cast GE **8614/8615**
   (`move_daemon`, SkillID 53/54) on them.
3. **The beams themselves**: the official path is **two independent geometry beams** (start point = the apparition's two
   contacts, end point = the entity list the server sends), not a daemon drawing its own beam; the server has to send that
   geometry state (its layout is unresolved). Until then the only option is `ServerBeamEffect` as a fallback (the near end
   must be hung on `L_Weapon`/`R_Weapon`); row 227 is `fusion_beam.ao`, a 9-cell-wide lava wall (§3.4).
4. **Damage**: the server settles twice, at **0.910 s** (the two beams sweep past) and at **3.431 s** (the 7.54-cell `blast`
   area straight ahead); the client's `OrionBeamSweep` only reports 24 bytes of state and is not responsible for the damage.
5. **Beam off**: `stop_beam` @2.729 should switch the two beams off.
6. **Shared state**: `beaming / x_pos / y_pos / z_pos` of the apparition `.ot` are still writable (the client does not read
   them at the moment, but that is the entry point the official game left ready, and it is worth verifying once on its own).

## Still open

1. **Who brings the two daemons in**: the **Skills table** that the `SkillID 53/54` of GE 8614/8615 points at **has not been
   extracted** (there is no `Skills.json` under `data/tables/`, none in `sql/base/client/` either), so there is no evidence for
   "who spawns these two daemons in the official game".
2. **The beams themselves**: the daemon draws nothing (§3.3). Two roads remain: an effect entity (`clone_trap_beam.ao` /
   `triple_beam_0N.ao`) or the geometry action's "target entity lines". `Metadata/Monsters/Daemon/Daemon.ao` (the generic daemon
   model that `Animated` points at) **has not been extracted**; if it is a visible model, the beams may be on it.
3. **The meaning of the dat columns**: in `data/schema/schema.min.json` all 35 column names of `MoveDaemon` **are null** (only
   the types: 0–15 i32, 16–18 bool, 19–24 i32, 25 bool, 26 string, 27 i32, 28–29 bool, 30 i32, 31 bool, 32 f32, 33 i32, 34
   bool; column 6 = `MonsterVarietiesKeys` → a MonsterVarieties foreign key). So which column the gate byte at `row+76`
   corresponds to, and whether `__col5` (±90) and `__col11` are "the heading" or "the beam's start angle", **cannot be
   determined from the repository**.
4. **±90° vs 75°**: the `__col5` of the dat = ±90, the client's endpoint solving uses **75°**; if the first is "the daemon's
   heading" and the second "the beam's convergence angle", they are consistent, but no data proves it.
5. **How the `daemon1`/`daemon2` event names are used**: no code was found for how the client turns these two event names
   into "spawn two daemons" (observed: the events have no data field; `.rdata 0x1435a90c8` has a "daemon" string, but there is
   no evidence tying it to the clip event names).
