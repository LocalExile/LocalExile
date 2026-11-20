# The clones' Die Beam (8100 `EmptyActionSpellOrionCloneBeam`)

## In one line

The body stands on one of the directions of a circle of radius 35 around the player, says "DIE" and plays `trap_beam_01` (the clip bound to GE 8100, 5330 ms).
That clip holds **6 `clone_beam_blast` events** (4.141 / 4.282 / 4.388 / 4.500 / 4.597 / 4.722 s, every event carries a model coordinate),
and the **12 clones** on the circle fire elemental beams at the player one after another.

The client action class `EmptyActionSpell2` (ActionType 811) **only plays the animation, it draws no beam** (observed: no drawing method anywhere in its vtable range);
the 6 `clone_beam_blast` have **no client art at all** (the `.act` binds no effect, the `.ot` listens for nothing) ⇒ both the clones and the beams must be drawn by the server.

**The twelve elemental beams are 7019–7030** (`AtlasExileOrionSpammableBeam{Physical,Cold,Fire,Lightning}{1,2,3}`), and
**7031 is a 13th one, `AtlasExileOrionCloneBeam`** (a separate effect, SkillID 359, StatSet1 7036). `clone_beam_01` /
`clone_beam_end_01` **exist in no `.ao` at all** (they are only bound in `atlasexile5.act` / `atlasexile5influencewild.act` ⇒ a
dead binding, the client cannot play them).

---

## Client data (observed)

| Item | Value |
|---|---|
| Skill | GE **8100** `EmptyActionSpellOrionCloneBeam` → ActiveSkill **1246**, CastTime **5330 ms**, `Animation` **35 = `SpellAreaOfEffectChaos`** → the body's `.act` `"trap_beam_01"`, StatSet1 8092 |
| Ultimate | GE **8101** `EmptyActionSpellOrionCloneBeamUber` (1246 / 5330 / slot **31 = `SpellProjectileUpwardChaos`** → `trap_beam_uber_01`, StatSet1 8093) |
| Spawning the clones | GE **10147** `AtlasExilesOrionCreateClones` (ActiveSkill 1449 / CastTime 2000 / slot **35** → `trap_beam_01`, SkillID 101) |
| Elemental beams | **7019–7030** `AtlasExileOrionSpammableBeam{Physical,Cold,Fire,Lightning}{1,2,3}` (ActiveSkill 1242, CastTime 1000, **`Animation` empty**); **7031** = `AtlasExileOrionCloneBeam` (same AS/CastTime, no animation slot) |
| Body GE list | the `GrantedEffectsKeys` of 8376 `AtlasExile5` hold **10147 / 8100 / 8101 / 8098 / 8099 / 7019–7031 / 10948–10953 / 10973 / 10141 / 10945 / 10991 / 10974 / 7013 / 7014 / 9851 / 9690 / 7008 / 7012 / 6809** |
| Body `.act` | `SpellAreaOfEffectChaos = "trap_beam_01"`, `SpellProjectileUpwardChaos = "trap_beam_uber_01"`, `SpellProjectileUpwardFire = "clone_beam_start_01"`, `SpellBuffChaos = "clone_beam_start_uber_01"`, `SpellProjectile = "beam_01" then "beam_02"`, `SpellProjectileFire = "beam_03" effect "…/on_act/beam_03/on_act_beam_03.ao"`, `SpellAreaOfEffect = "beam_barrage_01"` |
| Body `.ot` | `on_event_identify_for_EmptyActionSpell2 = { AddEffectPack('…/body_fx/Trap_Beam_Signal.epk'); PlayEffect('…/body_fx/flash_beam.ao'); }`, `on_end_EmptyActionSpell2 = { RemoveEffectPack('…/Trap_Beam_Signal.epk'); }`, `on_clone_beam_start_01 = { Call(start_clone_teleport); }` / `on_event_teleport_for_clone_beam_start_01` / `on_end_clone_beam_start_01` |
| Clip events | `atlasexile5.ao` → `trap_beam_01`: `start_face` @0.0 (`StartFaceEventType`, data `"0 0"`; `trap_beam_uber_01` is `"80 0"`), `dialogue` @2.0, `end_face` @3.74214, **`identify` @3.74214**, **6 × `clone_beam_blast`** @4.141 / 4.282 / 4.388 / 4.500 / 4.597 / 4.722; all six positions are x=0, y=−2.9576, z = −223.047 / −216.277 / −214.625 / −212.387 / −213.061 / −211.552 (model units ⇒ about 2.1–2.2 cells ahead, chest height) |
| The same events on the clone | `atlasexile5influence.ao` (**Character rig**) has the **same 6 `clone_beam_blast`** in its `clone_beam_start_01`, at exactly the same times; it also has `rocks_off/pointlight_off/eyes_off` @0.0, `contact`+`teleport` @0.5, `rocks_on/pointlight_on/eyes_on` @0.55125, `contact` @0.5667/0.6386/0.72807/1.45 ⇒ **those 6 blast events belong to the "clone" model in the first place** |
| Clone model | `atlasexile5_beamclone.ao`: **13** clips, `default_animation = "trap_beam_01"`, rig = `Art/Models/MONSTERS/AtlasExiles/TheOrion/Character/rig.amd` (the same rig as the body), SkinMesh `TheOrionExile.sm`; the contact position of `beam_01` is (−9.4614, −91.9149, −173.767), the body's `beam_01` is (−9.46, −106.94, −209.72) (**different**) |
| Candidate beam art | `…/monsters_effects/atlasexiles/orion/clone_trap_beam.ao`: a client effect object, rig `…/clone_trap_beam/rig.amd`, `default_animation clonetrap_beam`, SkinMesh `clonebeam.sm`, bone group `beam_length false jntBnd_beam aux_beam`, particle `clone_trap_beam_length.pet` on `beam_length`, sound `Audio/…/Sirus/Effects/Beam/TripleBeam3`, ScreenShake 13 / 0.4 s |

---

## Client implementation (decompiled)

### Class card (observed)

| Item | Value |
|---|---|
| Class | `EmptyActionSpell2` (ActionType 811, `HASH16` **7582 = 0x1D9E**, not 0x1F5B) |
| Family | `EmptyActionSpell`(732, 18594), `…3`(812, 7583), `…4`(813, 7576), `…5`(814, 7577) — **one shared class and one shared vtable `off_1434BBFC8`** |
| Where the type number comes from | the ctor `sub_1419C6500` **does not hardcode the type number**, it hashes at runtime: it builds the name `"EmptyActionSpell"` plus a number (`sub_1404D0650`, `sub_140148EC0`), hashes it with **an FNV-1 variant (basis 0, multiply 16777619, then the high and low 16 bits XORed together)**, and passes the result to the base ctor `sub_1419CF930` |

### State machine

**None**. This class has no state machine of its own and no drawing method of its own (observed: no function in `0x1419A000`–`0x1419E000` is referenced from the vtable `0x1434BBFC8`).

### Serialisation (what the client receives)

- Slots 86 / 87 `sub_1419CF9B0` / `sub_1419CFA20`: **they read and write a single byte at `+472`** (the same 1-byte payload as MoveDaemon).
- What the server sends is the **action plus the animation slot**; the client sends 1 byte back.
- Slot 31 starts `sub_1419CFAB0`: it appends the suffix `"_start"` to the name string at `+816` and has `sub_1419CFCF0` (on the `+792` member) **play the animation by name**, then goes to the base class `sub_1419E9840`. ⇒ it plays a clip **assembled by name**.

### Hits and damage

**None**. `EmptyActionSpell2` produces no hits and no damage; the 6 `clone_beam_blast` carry no damage on the client side either. All the damage is on the server.

### What the client shows

Only three things (observed):

1. The `trap_beam_01` animation itself (the body says DIE and raises its arm).
2. `identify` @3.742: the body's `.ot` hook `on_event_identify_for_EmptyActionSpell2` attaches `Trap_Beam_Signal.epk` and `PlayEffect('…/body_fx/flash_beam.ao')`; `on_end_EmptyActionSpell2` removes the effect pack ⇒ **this is the only client signal that the "clones are about to fire"**.
3. The voice line of `dialogue` @2.0.

What is **not** there: the display bodies of the 12 clones, the 6 beams, the art of the 6 `clone_beam_blast` (the `.act` binds no effect, the `.ot` does not listen for those events) — all of it has to come from the server.

## What the server should send

1. **Action**: send an `AnimationUpdate` for 8100 on the body (slot 35 → `trap_beam_01`), and fire `identify` at 3.742 s (the client flashes `flash_beam.ao` and attaches `Trap_Beam_Signal.epk` by itself).
2. **Clones**: 12 visible bodies. The official model `atlasexile5_beamclone.ao` (13 clips, `default_animation trap_beam_01`) is the first choice — but it has **no MonsterVarieties reference**, so how to spawn it has to be settled first; for now an effect object plus the 7th clip of the body's `.ao` is the fallback.
3. **Beams**: the client action class cannot draw them (`EmptyActionSpell2` has no drawing method), so the beams **must be drawn by the server**: `ServerBeamEffect` / `MiscBeams` rows, or a daemon entity. The model coordinates of the 6 `clone_beam_blast` (z = −223.047 … −211.552, that is 2.1–2.2 cells ahead, chest height) should be the **start/end points of the beams**, not the body's position.
4. **Elements**: the official picks at random among 7019–7030 (the twelve elemental beams); the code now uses the fixed 7031 (`AtlasExileOrionCloneBeam`) — the name is not wrong, but the element/colour variety is missing.
5. **Damage**: 6 resolutions (4.141 / 4.282 / 4.388 / 4.500 / 4.597 / 4.722 s), one beam each.

## Still open

1. **Which clip the client actually plays**: slot 31 builds the name at `+816` + `"_start"`; and the `Animation` slot 35 of 8100 binds `SpellAreaOfEffectChaos` → `trap_beam_01`. The source of the name at `+816` is not confirmed (if it is the binding name, the result would be `trap_beam_01_start`, and no such clip exists in the `.ao`) ⇒ **whether the client plays `trap_beam_01` or `clone_beam_start_01` is still open**.
2. **The official model of the 12 clones**: both `atlasexile5_beamclone.ao` (13 clips, `default_animation trap_beam_01`, Character rig) and `atlasexile5influence.ao` (Character rig, carries those 6 `clone_beam_blast`) look like the official clone, but **no MonsterVarieties row references them** (`AtlasExile5Influence.act/.ao` and `atlasexile5_beamclone.ao` neither do) ⇒ whether the clones are "a clip of the body's `.ao`" or an independent model is still open.
3. **Who spawns the 12 clones**: `AtlasExilesOrionCreateClones` (10147) has no count or radius parameter (CastTime 2000, Animation 35); the count 12 and the radius of 35 cells are estimates.
4. **The divergence angles**: in the official screenshots three huge beams diverge from different directions and cross close to the player; the data holds no angle parameter.
