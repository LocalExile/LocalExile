# The multi-beam barrage (8098/8099) and the clone beam (7031)

This is the skill the player has been chasing since m00631: the multi-beam barrage that follows the chant of "DIE".

> "Sirus leans back, brings both hands together in front of him and fires a multi-stage laser. This is the 'two-hand
> beam' that has never been fixed. The problem was always the same: the multi-stage laser has no effect, no real visible
> beam, or the beam comes out early, so it does not match the animation."

## In one line

The skill chain is **teleport (6809) → empty-action multi-beam barrage (8098 / final 8099)**. The animation slot of 8098 is
**`SpellProjectileFire`, bound to `beam_03` in the `.act`**; the clip events of `beam_03` are
`identify` @0.400 + `small/medium/large_beam_blast` @0.841 / 1.098 / 1.370 — **three beam blasts**.
The `final_beam_03` of stance2 **doubles** each of the three into six (.841/.991/1.125/1.264/1.389/1.516).

**The official three beams are three separate Effect entities** (`MiscAnimated` rows
`AtlasExileOrionSpammableBeamPhysical1/2/3` → `Orion/TripleBeam/triple_beam_01/02/03.ao`),
**not** rows 227/37 of `ServerBeamEffect`. Drawing them with `ServerBeamEffect` today means using one
9-cell-wide lava sheet in place of the official thin beams — that is the direct cause of the two "full screen"
reports, m01493 / m02031.

The `on_act_beam_03.ao` that comes with the clip only handles the **charge-up trail and the impact particles**; the
beams themselves are still the three Effects the server sends.

---

## Client data (observed)

| item | value |
|---|---|
| teleport | `TeleportOrion` = **6809**, ActiveSkill 1113, CastTime 1000, `Animation` **97 = `Teleport`** → `teleport_01` |
| multi-beam barrage | `EmptyActionSpellOrionMultiBeam` = **8098**, ActiveSkill **1130** (`empty_action_spell`), CastTime 2000, `Animation` **24 = `SpellProjectileFire`** |
| final | `EmptyActionSpellOrionMultiBeam2` = **8099**, CastTime 2500, `Animation` **25 = `SpellProjectileCold`** |
| the other effect in the same slot | `AtlasExileOrionSpammableBeamBlastRepeated` = the 7031 family (1242 / 2000 / 24) also goes through `SpellProjectileFire` |
| `.act` binding (stance1) | `SpellProjectileFire = "beam_03" effect ".../on_act/beam_03/on_act_beam_03.ao" "animate"`; `SpellProjectileCold = "beam_uber_03"` (same effect) |
| `.act` binding (stance2) | `SpellProjectileFire = "final_beam_03"`; `SpellProjectileCold = "final_beam_03"` |
| `beam_03` events | `identify` @0.400; `small_beam_blast` @0.841 (0.175, -6.351, -182.589); `medium_beam_blast` @1.098; `large_beam_blast` @1.370 |
| `final_beam_03` events | each of the three above, twice: .841/.9906, 1.1251/1.2643, 1.3895/1.5158 |
| on_act | `on_act_beam_03.ao` (hung on the effect of `beam_03`, played by the `.act` in the cast animation) |

The three blasts sit almost on top of each other (all 1.8 cells in front of the body, 0.06 cells above the ground), which
**means the three stages hit in the same distant direction, only getting thicker**, not three directions.

## What the official art looks like (`MiscAnimated` rows, observed)

`data/tables/MiscAnimated.json`:

| row | Id | AOFile |
|---|---|---|
| 3702 / 3703 / 3704 | `AtlasExileOrionSpammableBeamPhysical1/2/3` | `Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/TripleBeam/triple_beam_01/02/03.ao` |
| 3705-3707 | `…SpammableBeamCold1/2/3` | the same + `_ice.ao` |
| 3708-3710 | `…SpammableBeamFire1/2/3` | the same + `_fire.ao` |
| 3711-3713 | `…SpammableBeamLightning1/2/3` | the same + `_lightning.ao` |
| 7622-7633 | `DieBeamRing{Physical,Cold,Fire,Lightning}{1,2,3}` | `Metadata/Effects/Microtransactions/Rings/die_beam_ring/triple_beam_0{1,2,3}.ao` |

**This family, 7019–7030 (`…SpammableBeam{Physical,Cold,Fire,Lightning}1..3`), is the three impact effects of the
multi-beam barrage: three images per element variant = three stages.** When the official game fires three stages of one
element (physical, say), it uses `triple_beam_01/02/03.ao`.

`triple_beam_01.ao` (1841 characters, observed):

| section | content |
|---|---|
| `AnimationController` | `Art/Models/Effects/monster_effects/AtlasExiles/Orion/impact_beams/rig.amd`, `default_animation = "tripleBeam_01"` |
| `SkinMesh` | `Art/Models/Effects/monster_effects/AtlasExiles/Orion/impact_beams/spambeam.sm` |
| `BoneGroups` | `beam_length false jntBnd_beam aux_beam`, `beam_flash false jntBnd_beam aux_circle2` |
| `ParticleEffects` | `Metadata/Particles/monster_effects/AtlasExiles/Orion/impact_beams/triple_01.pet`, hung on bone_group `beam_length` |
| `SoundEvents` | `Audio/…/AtlasExiles/Sirus/Effects/Beam/TripleBeam1`, hung on `jntBnd_beam` |
| `RenderPasses` | `Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/TripleBeam/triple_beam_01_ao_pass0.mat` (Append) |
| `ScreenShake` | `tripleBeam_01` @0.0, duration 0.2, global, LinearDown |

Key points:

- **It shares the rig and the mesh with the ordinary beam's `spam_beam.ao`** (`impact_beams/rig.amd` +
  `impact_beams/spambeam.sm`); only the material (`triple_beam_01_ao_pass0.mat` vs `spam_beam_ao_pass0.mat`), the animation
  name (`tripleBeam_01` vs `beam_strike`) and the particles (`triple_01.pet` vs `beam_flash.pet`) differ.
  **The "ordinary beam" and the "multi-beam barrage" are two skins on the same model**, which also explains the
  "a single one is only 1/8~1/10 as wide" look in the reference image.
- **The length is data-driven**: the `beam_length` bone group decides how long the beam is, `beam_flash` decides the flash
  at the tip; dropping the Effect on its own only gives the model's default length (the official game writes it into the
  bone group from the action data).
- **`DieBeamRing*` is not a "ring"**: pull `die_beam_ring/triple_beam_01.ao` out of the GGPK, diff it line by line against
  `Orion/TripleBeam/triple_beam_01.ao`, and **the only difference is the sound**
  (`Microtransactions/SirusDieBeamRingMTX/TripleBeam1` vs `MonsterSounds/…/TripleBeam1`) — it is the microtransaction
  (MTX) version of the same image. Do not treat it as another visual.

## What the client draws by itself / what the server must draw

- **Client**: the animation of `beam_03` (leaning back, both hands together in front of it), the **charge-up trail and the
  impact particles** of `on_act_beam_03.ao`, and the three `*_beam_blast` events themselves (the events are the client's
  "it is time to explode" signal; the `.ot` binds nothing that draws a beam).
- **Server**: **three (six in the final) separate beam Effects** (`triple_beam_0N.ao`, hung on the hand bones) + hit
  detection. The three stages are **not** drawn by `ServerBeamEffect` — the official game uses no Beam component here; one
  blast is one effect entity.

### Two things that are confirmed

1. **The packet is right**: the layout of `BeamEffectObject` matches the client's `0x1416710c0` field by field (see
   [README](README.md)), and the live log shows `E400 D7 0C … 525F576561706F6E` (row 227 + `R_Weapon`).
2. **No `StartBone`, no beam**: in the flag `0x97` era the symptom was "sent in the packet, invisible on screen", because
   the client anchored the near end at the caster's origin (on the ground) and drew the whole mesh below the ground. After
   switching to alternating `R_Weapon`/`L_Weapon`, the live frame `logs/shots/k4/frame-0189` really does show red and white
   beams coming out of both hands.

## Three things with a similar name that are **not** this skill (important)

| name | what it really belongs to (observed) |
|---|---|
| `beam_barrage_01` | animation slot `SpellAreaOfEffect`(**22**) → **corridor** `AtlasExileOrionCorridorBlast`(10948). 8 × `beam_right/beam_left` in the clip (1.173–5.812 s) + `create_corridor` @0.461 |
| `standalone_beam_barrage_01` | animation slot `SpellProjectileChaos`(**27**) → `AtlasExileOrionDeathBarrage` (**1247**, a separate effect, not used in this fight) |
| `trap_beam_01` | animation slot `SpellAreaOfEffectChaos`(**35**) → clone `EmptyActionSpellOrionCloneBeam` (8100) |

In other words: in the beginning the 8 hand beams were taken for "this skill" on the strength of the name alone;
`beam_barrage_01` belongs to the corridor. The real multi-beam barrage is the three stages of `beam_03` (six in the final).

## Still open

1. **Length**: the length of the official beam is driven by the bone group `beam_length` out of the skill data; dropping
   `triple_beam_0N.ao` on its own only gives the model's default length. To match the official game exactly, we still have to
   chase how `sub_141E59F80` writes the attribute into the Effect's bone group. (**First step in the live client**: replace
   only the three stages from `ServerBeamEffect` with three Effects, and see whether the width and "it comes out of the
   hands" line up; dig further only if the length is off.)
2. **Number of stages and direction**: in the data all three stages are in the same direction, and there is no frame evidence
   of whether the official game fires "three parallel/fan-shaped" beams.
3. **`identify` @0.400** has no matching implementation: the official game may lock the target direction at 0.4 s; we draw
   straight at the player's position.
4. `hit_fx_beam.ao` draws once at the impact point for 0.5 s; whether the official `*_beam_blast` leaves a longer mark on the
   ground is unverified.
5. **Element variants**: the official game swaps the art by damage element (`_ice` / `_fire` / `_lightning`); our multi-beam
   barrage has no element yet, so the physical `triple_beam_01..03.ao` stands in.

## Client implementation (decompiled, 2026-11-17)

**First the conclusion**: the two skills `8098` / `8099` draw **no beam at all** on the client; the multi-stage laser on
screen comes from the `on_act` effect that ships with the animation clip (4 trail strips + 3 impact bursts). The server only
needs to "make the client play the clip" + "settle the damage on the blast events"; the extra `ServerBeamEffect` is only
gilding the lily.

### 1. Skill → client class

| item | value |
|---|---|
| GrantedEffect | `8098 EmptyActionSpellOrionMultiBeam` (`Animation` = **24** `SpellProjectileFire`, CastTime 2000) |
| GrantedEffect | `8099 …MultiBeam2` (`Animation` = **25** `SpellProjectileCold`, CastTime 2500) |
| ActiveSkill | **1130 `empty_action_spell`** (`ActionType` = 732) |
| client class | **`EmptyActionSpell`** (`HASH16 = 18594 = 0x48A2` in `ActionTypes.json`) |

### 2. The assembly chain of the class (observed)

- class name string `0x1434c7128 "EmptyActionSpell"` ← wraps `sub_14196EEA0`: `_crtInitOnceExecuteOnce("EmptyActionSpell", sub_1419BF0B0, sub_141271520, 0); return &unk_1447602A2;`
- factory `sub_1419BF0B0`: probes ctx attributes `876`/`332`/`858` to pick the variant, otherwise `sub_142C8CB50(840)` allocates **840 bytes**, `sub_1419CF930(obj, 18594, …)` (base ctor, the second argument is the class name hash), then writes the main vtable `off_1434BBFC8` and the secondary interface table `off_14323E408`.
- `EmptyActionSpell2/3/5` (8100, DeathBarrage, …) go through `sub_1419C6500 / sub_1419C66A0 / sub_1419C6840`: at runtime they concatenate `"EmptyActionSpell"` with a number into a string and compute an **FNV-1 variant 16-bit hash** (`v6 = byte ^ (16777619 * v6)`, then `v6 ^ HIWORD(v6)`), then call the same base ctor and **the same vtable**. ⇒ This family is one class with different type numbers.

### 3. What it actually does (the slots it overrides)

- **slot 31 start** `sub_1419CFAB0`: builds the string `*(a1+816) + "_start"`, hands it to `sub_1419CFCF0(a1+792, str)`, then goes to the base `sub_1419E9840`. In other words, "play one animation by name"; it creates no beam object at all.
- **slot 86 serialise** `sub_1419CF9B0` / **slot 87 deserialise** `sub_1419CFA20`: writes/reads a single byte `*(a1+472)` (a state flag), a minimal report at the 24-byte level.
- the other slots in vtable `off_1434BBFC8` all land in the common area of the base `Actions::Action` (`sub_1419E9D30` etc.), and `vtnear 0x1434BBFC8 0x1419A000 0x1419E000` returns **0** — this class has no drawing method of its own.

### 4. The visual of the beam lives in the `effect` clause of the `.act` (key)

`Metadata/Monsters/AtlasExiles/AtlasExile5.act`:

```
SpellProjectileFire = "beam_03" effect "…/Orion/on_act/beam_03/on_act_beam_03.ao" "animate";
SpellProjectileCold = "beam_uber_03" effect "…/Orion/on_act/beam_03/on_act_beam_03.ao" "animate";
   stance2:
SpellProjectileFire = "final_beam_03" effect "…/on_act/beam_03/on_act_beam_03.ao" "animate";
SpellProjectileCold = "final_beam_03" effect "…/on_act/beam_03/on_act_beam_03.ao" "animate";
```

Content of `on_act_beam_03.ao` (5854 B, decoded):

- `ClientAnimationController skeleton = …/on_act/beam_03/on_act_beam_03.ast`, `SkinMesh {}` **empty**
- `BoneGroups`: `curve_01..04` (bound to `aux_trail_curve3 / curve2 / curve0 / curve1`), `orbEmit` (`aux_orbEmit`), `grd_Emit` (`aux_mid aux_line`), `blast` (`aux_orbEmit aux_orbEmit1`)
- `ParticleEffects`: `orbEmit.pet` @0.617, `blast.pet` @0.831 / @1.094 / @1.363
- `TrailsEffects`: `on_act.trl` on `curve_01..04`, `time 0.0928 → end_time 1.9668`

`on_act.trl` (2902 B, two segments):

| segment | material | segment_distance_threshold | max_segment_distance | segment_duration | scale |
|---|---|---|---|---|---|
| 1 | `…/Orion/death_projectile/ground_bits.mat` | 1 | 800 | 0.21 | 1.165 |
| 2 | `…/Orion/body_fx/chargeup_trail.mat` | 30 | 1000 (=10 cells) | 0.3 | **8.32** |

⇒ The three "lasers" are really **two trail strips generated segment by segment along 4 curve bones**, each at most 10 cells
long, the width decided by scale 8.32 × material; the impact particles at 0.831 / 1.094 / 1.363 nearly coincide with the
clip's `small/medium/large_beam_blast` (0.841 / 1.098 / 1.370), which means **the damage and the effect happen at the same
moment**.

## What the server should send

1. Send the **animation** only: `AnimationUpdate(target, 0, 3, 1)` (stance1 = `beam_03`, global index 7 minus 4) or `12`
   (stance2 = `final_beam_03`, global 20 minus 8).
2. Settle the damage at the `contact/blast` moments (the current `contacts = [.841, 1.098, 1.370]`, 6 stages in the final) —
   this part of the code was right to begin with.
3. **Turn the beams into three separate Effects**: send one `EffectObject` at each `contact` moment
   (`Metadata/Effects/PermanentServerEffect`), `AnimatedObject` = `…/Orion/TripleBeam/triple_beam_01.ao` (the second stage
   `_02`, the third `_03`; in the final, send the set twice), `Parent` = the caster's id, `Bone` = alternating
   `R_Weapon`/`L_Weapon`, `AttachKind = 0`, `AttachFlags = 3` (keep the bone's orientation, the way Arrow Nova's arrow does),
   lifetime ~0.2–0.6 s. The fields of `EffectObject` are in
   `src/GameServer.Core/Protocol/Instance/InstanceEntities.cs:859-931`.
4. **Do not send `ServerBeamEffect` for 8098/8099 any more**: rows 37/227 cover the screen with "full-screen fire" (the
   direct reason for the two rejections in m01493 / m02031), and the official family does not use the Beam component at all.
5. The charge-up trail and the impact particles of `on_act_beam_03.ao` are played by the client itself (the `effect` clause
   of the `.act`), and **the server does not need to do anything**; if the charge-up is not visible in the live client, check
   that the client loaded the effect of the `.act` instead of adding an effect of its own.

## In the live client (2026-11-17, evening)

The change after the user decided on "the multi-beam barrage uses three `triple_beam_0N.ao`": the three stages of 8098/8099
no longer send `ServerBeamEffect` (row 37), and send one `EffectObject` each at the three contact moments instead:
`…/Orion/TripleBeam/triple_beam_01/02/03.ao`, `Parent` = Sirus, `Bone` = alternating `R_Weapon`/`L_Weapon`,
`AttachKind = 0`, `AttachFlags = 3`; the impact point sends `hit_fx_beam.ao` as well.

Measured (`SIRUS_ONLY_KIND=1`, the frame capture covers the cast at 17:49:13.531, screenshot
`logs/shots/sirus-kind1c/barrage3.jpg`):

- A beam really does appear on screen, coming out of Sirus's hands, pointing at the player, with a bright burst at the tip
  ⇒ the official `TripleBeam` art has been picked and drawn (no longer the fire wall of row 37).
- But it is **too wide and too molten** (about 6–7× the width of the thin line in row 364, ≈1.5–2 cells): nobody drives the
  `beam_length` bone group of `triple_beam_0N.ao` (there are no geometry endpoints), so the effect renders with the model's
  default length/width.
- ⇒ The "official thin line" look still has to wait for the geometry endpoint message; until then, if it is too wide, these
  three stages can be swapped for row 364 as well (a thin line, with controllable direction/length).
