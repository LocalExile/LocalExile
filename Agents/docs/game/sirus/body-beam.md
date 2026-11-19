# The ordinary beam (7008/7012)

## In one line

The ordinary attack is `AtlasExileOrionSpammableBeamBlast` (7008, final form 7012), animation slot =
**`SpellProjectile`**, the `.act` binds `beam_01` **then** `beam_02` (final: `final_beam_01` then
`final_beam_02`).

**We have been using the official art all along**: in the client table `MiscAnimated`,
`AtlasExileOrionSpammableBeam` → `Orion/spam_beam.ao`, `AtlasExileOrionSpammableBeamFinal` →
`Orion/spam_beam_final.ao` (observed). The problem is not "which image", it is **where it is placed**: the official
one hangs on the **hand bone** and stretches out along the weapon's facing; ours puts "a flat grid on the ground
under the feet", so it is always a bit off.

---

## Client data (observed)

| Item | Value |
|---|---|
| GrantedEffects | `AtlasExileOrionSpammableBeamBlast` → 7008; `…BlastFinal` → 7012 |
| ActiveSkill | 1242 = `geometry_spell`, `ActionType 807`, **no projectile field** |
| CastTime | 1500 ms (7012: 1350 ms) |
| `Animation` | **20 = `SpellProjectile`** (`data/tables/Animation.json`) |
| `.act` binding | `SpellProjectile = "beam_01"; then "beam_02";`; stance2 is `final_beam_01` / `final_beam_02` |
| clip length | 1500 ms (`data/monster-contacts.json`) |
| clip events | `beam_01`: only `contact` @0.672, position (-9.46, -106.94, -209.72) (model units, 100 = 1 cell)<br>`beam_02`: only `contact` @0.6915, position (5.61, -107.72, -206.74) |
| client hooks | the body `.ot`'s `client.Animated` has **no** entries for `beam_01/02` |
| official art | `MiscAnimated` row `AtlasExileOrionSpammableBeam` (`spam_beam.ao`, HASH32 3850907589), `…SpammableBeamFinal` (`spam_beam_final.ao`, HASH32 1122204890) |

The two `contact` positions are almost the same (both about 1 m in front of the body and 1 m high), **which means
the "two parts" are two casting beats of the same pose**, not two beams in different directions. In combat
`then beam_02` means that after the clip ends the next clip of the same slot is played once more.

### What the official art looks like (the structure of `spam_beam.ao`, observed)

`Tools/re/out/orion/metadata/effects/spells/monsters_effects/atlasexiles/orion/spam_beam.ao` (UTF-16LE, 2004
characters):

| Section | Content |
|---|---|
| `AnimationController` | `metadata = "Art/Models/Effects/monster_effects/AtlasExiles/Orion/impact_beams/rig.amd"`, `default_animation = "beam_strike"` |
| `SkinMesh` | `skin = "Art/Models/Effects/monster_effects/AtlasExiles/Orion/impact_beams/spambeam.sm"` |
| `BoneGroups` | `beam_length false jntBnd_beam aux_beam`, `beam_flash false jntBnd_beam aux_circle2` |
| `ParticleEffects` | `Metadata/Particles/monster_effects/AtlasExiles/Orion/impact_beams/beam_flash.pet`, attached to bone_group `beam_flash` |
| `SoundEvents` | `Audio/…/Sirus/Effects/Beam/SpamBeam`, attached to `jntBnd_beam` |

The points (observed):

- `spam_beam.ao` and `TripleBeam/triple_beam_01.ao` **share the same rig and mesh** (`impact_beams/rig.amd` +
  `impact_beams/spambeam.sm`); only the material, the animation name and the particles differ
  (`spam_beam_ao_pass0.mat` vs `triple_beam_01_ao_pass0.mat`, `beam_strike` vs `tripleBeam_01`,
  `beam_flash.pet` vs `triple_01.pet`). **The "three-part beam" and the "ordinary beam" are two skins of the same
  model.**
- The two bone groups show that **the length and the tip flash are driven by the client from the data**
  (`beam_length` stretches the length, `beam_flash` flashes the endpoints); the length is not part of the model.
  Placing a bare Effect only gets the model's default length.
- `fusion_beam.ao` (`MiscBeams` row 227, the one we used before) is a **different** model
  (`fusion_beam/fusionbeam.sm`, a 9-cell-wide lava slab) — that is where the old "screen full" came from; the
  "TripleBeam" in that file is only the sound name `…/Sirus/Effects/Beam/TripleBeam2`, **not** a reference to the
  TripleBeam effect.

## What the client draws by itself / what the server must draw

- **Client**: plays the main `beam_01` animation (the motion with both arms stretched forward / the weapon
  thrusting forward) and the hit window at the `contact` moment. `beam_01`'s `.ao` has no `ParticleEffectType` at
  all, and the `.act` has no effect line → **the clip itself does not draw the beam** (observed).
- **The action class does draw**: `GeometrySpell` (this skill's action class) **news up a 584-byte Effect of its
  own** at startup and attaches it to the casting entity (see the next section). So "the client does not draw it"
  holds for the clip only.
- **Server**: the damage check + putting the beam where it belongs (hand bone, facing, length).

### The three drawing methods that were tried (measured)

| Method | Result |
|---|---|
| `Ghost: true` (draws nothing) | Confirmed in the live client on 2026-11-16: the whole thing is only the casting animation, not a single beam on screen |
| Row 228 `fusioned_nuke_beam.ao` (`len=150`, `life=1.5`) | An orange-purple lava pillar across the whole screen; the player rejected it (m00566 "take this big laser away, this is not the ordinary attack's beam") |
| Row 227 `fusion_beam.ao` | Still "screen full" (the art is 2.15 cells wide and 9.4 cells long; stretched it is a wall), the player rejected it |
| **`spam_beam.ao` static mesh (current)** | A mesh placed on the ground along the facing, 0.6 s; the player confirmed "it looks right now" (m00843), but it lies on the ground and has no source bone |

## Client implementation (decompiled)

### The action class: `GeometrySpell`

(observed) `ActiveSkills[1242]`'s `ActionType` is **807**; in `data/tables/ActionTypes.json` 807 = `GeometrySpell`,
`HASH16 = 52470 = 0xCCF6`:

| Item | Value |
|---|---|
| Wrapper | `sub_14126D130`: `_crtInitOnceExecuteOnce("GeometrySpell", sub_14128CD10, sub_14128CD00, 0)` |
| Factory | **`sub_14128CD10`** — it first probes the ctx attributes `876` / `332` / `858` to pick a variant; only when none of them matches does it allocate **1192 bytes** with `sub_142C8CB50(1192)` and call the ctor |
| Constructor | **`sub_141514B10`**: `sub_1419E9B10(this, 0xCCF6, actor, ctx)`, `sub_141940AA0(this, *(u8*)(this+372))` → `(**v4)(v4, 6433)` → `sub_141967050(this+792, v5)`; it first installs `off_14353BC00` and then swaps in the main vtable **`off_143444E98`**, `[+168] = off_14323E408`, `[+792] = off_143446C70` |
| Own slots | `0x141514AC0` (destructor), `0x1415148F0` (predicate `+286==0 \|\| +888!=0`), `0x141514910` (write), `0x141514920` (read), `0x141514940` (clone), `0x141517184` (subobject thunk), `0x141512B90` (destructor variant), `0x141512C40` (advance), `0x141512C10` (release the effect handle), `0x141513080` (start, reads the packet), `0x141512B10` (shadow copy), `0x141512D30` (start, does not read the packet) |

(note) The slot numbers are computed relative to the passed address and the vtable layout differs per class, **so
trust the function addresses and the behaviour, not the slot numbers**.

**Does it draw at all? Yes.** (observed)

```c
// build the Effect: 584 bytes, table off_143460C40
v = sub_142C8CB50(584);
sub_141EC47E0(v, &resource, actor, 0, 1);     // resource = L"Metadata/Effects/Effect" (23 characters, a fixed string)
sub_141F6E890(entityCtx, v);                 // attach it to the entity
sub_141EC9890(a1+888, v);                    // the handle is stored at +888
```

The effect's **content** has two possible sources (observed):

| Source | How it arrives |
|---|---|
| ActionData attribute **`0x7A39632A`** (2050581290) | `sub_14228BED0(&prop, 2050581290, &t)` → `sub_141E59F80(effect, &prop, 0)` |
| **2 resource strings + 2 u32** in the packet | the start slot `sub_141513080`: `flag = *buf++`; when `flag != 0` it reads two strings (`+856` / `+864`) and two u32 (`+872` / `+876`); when `flag == 0` it goes through `sub_141DC0BC0(...)` instead and reads the skill data |

Each frame does only one thing: it remaps the action time onto the effect's timeline (`+656` frame count, `+692` start
offset, `+680/+684` normalized progress, `+872/+880` are the duration and the start; dividing by 5.0 gives a
five-step easing).

⇒ **What this means for the server**: our `0x1ab` (`InstanceProtocol.ActionStarted`) carries only the skill id and
the coordinates, **not those two resource strings**, so the client takes the `flag == 0` branch and takes the effect
attribute out of its own copy of the skill data; if that attribute cannot be found, the client creates an **empty
Effect** (which explains why "the same action sometimes shows nothing at all"). On this machine the number
`0x7A39632A` also appears as the `HASH32` of the `MiscAnimated` row `IncursionNovasoulMovingCone` (**observed**),
but the attribute key and the MiscAnimated HASH32 are not the same numbering, so **this does not prove** which image
the ordinary attack uses; the official image is still the `MiscAnimated` row `AtlasExileOrionSpammableBeam`.

### Where the geometry data comes from: `Data/GeometryAttack.dat` and stat 6433 (decisive, 2026-11-17)

The `+792` subobject of `GeometrySpell` loads **`Data/GeometryAttack.dat`** at construction time
(`sub_141967050`) (not `Data/GeometryTrigger.dat`), and looks up one row with the value of **ctx attribute 6433**
as the key:

- `sub_1414C7DD0(v4, L"Data/GeometryAttack.dat", ...)` gets the file handle → `sub_1401297C0` gets the table →
  `sub_140215DA0(map, &key)` looks it up by the u32 key; if it is not found (the tail sentinel comes back), that row
  does not exist.
- On a hit the strings of the row are moved into the subobject (`+200`/`+208` is one pair, `+257`/`+265` another) —
  those strings are the **name and the art** of this beam.
- 6433 is **`skill_behaviour_variation`** in our table (`data/tables/Stats.json` row 6432; the client's stat id is
  1 higher than our row number).

⇒ **The official ordinary beam is not drawn "just any way" by the client: it is decided by one row of
`GeometryAttack.dat`, and that row is reached through the skill's own `skill_behaviour_variation`.** Our
7008 / 7012 StatSet (7013 / 7017) **does not set 6433** ⇒ the value is 0 ⇒ no row is found ⇒ the client cannot
draw this beam by itself (which matches what we saw in the live client: the beam has always been added by the
server).

⇒ Two ways forward: **(a)** give the skill a correct `skill_behaviour_variation` so that the client draws it
itself (closest to the official, but the value has to be found by trial); **(b)** the server places the official
`.ao` (`EffectObject` + Parent + Bone=`R_Weapon`), with `spam_beam.ao` as the art.

Two traps (observed):

- In the GGPK, `Data/GeometryAttack.dat` is `data/geometryattack.datc64` (432,112 B, first 4 bytes `0x550` = 1360
  rows), **pure binary, not a single readable string**; `.datcl64` is its localisation companion file. We scanned the
  whole file: **no `MiscAnimated` HASH32 at all** (the hashes of `spam_beam` / `triple_beam_*` /
  `corridor_slash_01` / `clone_trap_beam` all get 0 hits) ⇒ this table does not name a `MiscAnimated` art row
  directly.
- `GeometryTrigger` (10141 / 10145 / 10957, ActiveSkill 1449) looks up **another** table,
  `Data/GeometryTrigger.dat` (490 rows), also keyed by 6433 — and 6433 **never appears once** in that table ⇒
  skills that go through `geometry_trigger` (our meteor rain markers 10141/10145) never place anything. That
  explains the "the meteor rain marker is empty" we saw in the live client.

### The body `.ao`: the ordinary attack's two clips carry only sound, no particles and no trails

The `client` section of `Tools/re/out/orion/metadata/monsters/atlasexiles/atlasexile5.ao` (UTF-16LE, 54772
characters):

| Section (character offset) | Entries for `beam_01` / `beam_02` |
|---|---|
| `SoundEvents` | `beam_01`: a voice `…/Sirus/HumanFormVocals/Attack` @0.4544 (`head_jntBnd`) + a swing `…/Unarmed/New_Unarmed_$(#).ogg` @0.5868 (**`R_wrist_jntBnd`**); `beam_02`: voice @0.5096 + swing @0.5132 (**`L_wrist_jntBnd`**) |
| `ParticleEffects` (42903) | **no** `beam_01` / `beam_02` entries. The ones with particles are `beam_03`, `beam_barrage_01`, `beam_barrage_abort_01..03`, `final_beam_01/02/03`, `final_beam_barrage_01` |
| `TrailsEffects` (48279) | **no** `beam_01` / `beam_02` entries. The ones with trails are `beam_03` (`melee_trail.trl`, both arms 0.333→0.841), `beam_barrage_01` (`beamBarrage.trl`, R/L_arm_FX 0.0→6.6), `clone_beam_start_01` (`cloneBeam.trl`, four of them), `death_01` |

⇒ **The official ordinary attack's "beam" is not drawn by the clip**: the motion and the two swing sounds are in
the clip, the beam is drawn by the Effect that `GeometrySpell` builds.

The `.ao` also has two hand-related sockets (observed; they have **no** script attached so far — a full-text search
of the `.ot` for `aux_hand_orb` gets 0 hits): `aux_hand_orb_attach` is attached to `R_Weapon`,
`aux_hand_orb_attach_l` to `L_Weapon`, with the translations `0 -10 0` / `0 10 0` — they give the "palm position",
and are indirect evidence for the bone height the server's beam should use.

## The decisive conclusion: the art of this beam is decided by `SkillID` → the `GeometryAttack` row (2026-11-17, afternoon)

The chain is closed, and every link has local evidence:

| Link | Evidence |
|---|---|
| The client looks up the geometry table with **its own** `skill_behaviour_variation` stat | the comment on `SkillCatalog.BehaviourVariation` (`src/GameServer.Gateway/Skill/SkillCatalog.cs:328-336`): `a geometry channel its GeometryAttack row (by Id)`; stat id `0x1921` = 6433 = `Stats.json` row 6432 `skill_behaviour_variation` |
| Our server **already** sends this stat to the client | `Modules/Core/Game/Monster/Sirus.Native.cs:48` calls `WithBehaviourVariation(c, e, …)`; `Modules/Core/Game/Monster/Monsters.cs:24-27` adds `(StatKeys.SkillBehaviourVariation, the effect's own SkillID)`; `GrantedSkill.Stats` is written into the skill record by `WriteSkillRecord` in `InstanceEntities.cs` (`.Stats(s.Stats)`) |
| That value is the `SkillID` of the `GrantedEffects` row | `data/tables/GrantedEffects.json`: 7008 → **332**, 7012 → **333**, 7019/7020/7021 → 342/343/**344**, 7022-7024 → 345/346/347, 7025-7027 → 348/349/350, 7028-7030 → 351/352/353, 7031 → **359** (the final versions 7013/7014 → 344) |
| And `SkillID` is exactly the row Id of `GeometryAttack` | `data/tables/GeometryAttack.json` (1360 rows): Id **332** → `Animation [3667]` = `AtlasExileOrionSpammableBeam` = `spam_beam.ao` (`HitEffect [3701]` = `Hit_FX_beam.ao`); 333 → 3668 `spam_beam_final.ao`; 342/343/344 → 3702/3703/3704 `triple_beam_01/02/03.ao`; 345-347 → `_ice`; 348-350 → `_fire`; 351-353 → `_lightning`; **359** → 3729 `AtlasExileOrionCloneBeam` = `clone_trap_beam.ao` |

⇒ **The official beam should be drawn by the client in the first place**: it takes `skill_behaviour_variation`, finds
the `.ao` in `GeometryAttack`, builds the Effect itself, stretches the length itself through the bone group
`beam_length`, and attaches the hit effect `Hit_FX_beam.ao` itself. The server drawing a `spam_beam.ao` /
`fusion_beam.ao` on top is **a duplicate**, and because nothing is attached to a bone it lies on the ground — that
is the leading candidate for what the player kept calling "wrong".

The numbers in the geometry rows (every Sirus geometry row is the same): `__col4=3, __col5=3, __col6=230, __col7=10, __col9=6, __col10=3, __col11=100, __col15=3, __col17=4, __col24=7`. `__col6=230` matches the first number of the endpoint lengths `{23, 46, 69, 70}` cells in the `OrionBeamSweep` decompilation: **world unit = cells × 10 ⇒ the official beam is 23 cells long**, `__col7=10` = 1 cell wide. The other kind of shape (Id 330 `death_carpet_bomb_impact`) has `__col6=18`, `__col7=0`, `__col9=13`, `__col10=13` instead.

**So these three can be changed** (one variable per change):

1. Remove the ordinary beam that the server draws itself (the flat `spam_beam.ao` / `spam_beam_final.ao` effect for
   7008/7012 in `Sirus.Combat.cs`) and let the client draw it; **prediction**: the beam leaves the BOSS's hand, is
   23 cells long and 1 cell wide, and points at our aim target.
2. Do the same for the multi-beam (7019-7031): remove the `fusion_beam.ao` from `ServerBeamEffect` row 227;
   **prediction**: three `triple_beam_0N.ao` (3702-3704, physical) or the elemental variants (3705-3713) appear in
   turn, each 23 cells long.
3. The clone's two-hand laser (7031 → GeometryAttack 359 = `clone_trap_beam.ao`) goes to the client as well;
   **prediction**: one 23-cell beam from each of the clone's hands.

**The byte layout of the `.dat` (parsed locally, `logs/tmp/gg/data_geometryattack.datc64`, 432,112 B)**:
`u32 rowCount = 1360` → a fixed section of `rowCount × 305 B` → an `8 × 0xBB` marker → a variable section
(17,300 B). The fixed fields that reference variables (arrays, strings) store a **u32 offset** (relative to the start
of the variable section): one array column takes 16 B (`[u32 len][u32 pad][u32 varOffset][u32 pad]`). The first 36
bytes of row 332 = `Id 332 | Animation(len 1, var 4336) | HitEffect(len 1, var 4352)`; at 4336+8 in the variable
section there is **3701** (`Hit_FX_beam.ao`), at 4352+8 there is **3667/3668** (`spam_beam.ao` /
`spam_beam_final.ao`) — that is, `Animation` and `HitEffect` store **MiscAnimated row numbers**, not art paths (the
path is in `MiscAnimated.AOFile`). There is **not a single string** in the whole file (UTF-8 and UTF-16 both
scanned, 0 hits), so a geometry row cannot carry an `.ao` path of its own.

**How the client uses it**: `sub_141967050(a1+792, key)` uses **the value of stat 6433** as the u32 key to look up
this table, and moves the row's two field groups (+200/+208, +257/+265) and the numeric fields (`sub_142257C90`
copies them into the subobject at +320) into the `+792` subobject; **the final resolution of the art happens
elsewhere** — `sub_141512D30` (start without the packet) and `sub_141513080` (start with the packet) both first
build the `Metadata/Effects/Effect` template and then stuff **the hard-coded `2050581290`** (`MiscAnimated` 2169
`IncursionNovasoulMovingCone` → `moving_cone.ao`) in as the **default** content. The `flag != 0` branch that reads
the packet can override it with the two resource strings from the packet (+856/+864); the `flag == 0` branch goes
through `sub_141DC0BC0` and reads the skill data.

⇒ **The last step to find out**: in which function the geometry row's `Animation [3667]` becomes `spam_beam.ao` and
replaces that default cone (candidates: the `+792` subobject's resolve function `sub_141511F30`, the per-frame
advance `sub_141512C40`, or the hit-effect path). This decides whether "we do nothing and the client draws it
itself" holds.

**The numbers in the geometry row**: `__col4=3, __col5=3, __col6=230, __col7=10, __col9=6, __col10=3, __col11=100, __col15=3, __col17=4, __col24=7` (every Sirus geometry row is the same; the other kind of shape, Id 330, is `__col6=18, __col7=0, __col9=13, __col10=13`). `__col6=230` agrees with the first number of the endpoint lengths `{23, 46, 69, 70}` cells in the `OrionBeamSweep` decompilation: **world unit = cells × 10 ⇒ the official beam is 23 cells long**, `__col7=10` = 1 cell wide (**inferred, waiting for confirmation on the client side**).

**How the client uses this row (`sub_141967050`, read line by line by lead on the afternoon of 2026-11-17)**:
`sub_141967050(a1+792, key)` does four things — ① loads `Data/GeometryAttack.dat` (`sub_1414C7DD0`); ② uses
`sub_140215DA0` (an FNV-1a hash table) to find the row on **row Id = key**; ③ copies the row's **name string**
(the shared string at +200/+208 inside the row) into the subobject (`sub_1419E3570`); ④ `sub_142257C90(a1+320, key, &table)` decodes this row's **geometry data** into the
subobject's +320, then decodes the second group of arrays.
The subobject is then initialised (+96 flag, four dwords at +100..+112, the listener table at +216, two containers at
+276/+284, +296..+312).

In that "geometry data" object at `+320`: +20 = **the number of entries in m_HitEffects**, +28 = the address of the
array, +117 = the orientation mode of the hit effect (3/4/5/6/7/8 are ContactPoint / random / target / InFront /
RNG / SelfOnce). It is read by **slot 14 = `sub_141514EA0`** of the subobject vtable (`off_143446C70`), and the
function carries the string `"GetHitEffectOrientationReference"` itself — it picks the orientation by +117, then
picks a random entry from m_HitEffects and generates it. ⇒ **the hit effect comes from the client itself**
(`Hit_FX_beam.ao` is in `HitEffect [3701]`).

**What the beam itself looks like (both start paths agree)**:
- `sub_141512D30` (start without the packet) and `sub_141513080` (start with the packet) both first build the
  `Metadata/Effects/Effect` template, then set the content to **the hard-coded MiscAnimated `2050581290` =
  `IncursionNovasoulMovingCone` → `moving_cone.ao`**; the per-frame `sub_141512C40` reads the same row and uses
  `*(DWORD*)(row+36) != 0` to decide whether the scale interpolates over time.
- ⇒ **The official geometry beam = a "cone" effect (the shape) + the shape parameters from the geometry row
  (length/width) + the art in the geometry row's `Animation`**. So the **art** of "the official ordinary beam" can
  only come from the geometry row's `Animation [3667] = spam_beam.ao`, while the **shape** comes from the cone
  template.
- Inside `sub_141513080` the client has only two ways to get the art path: the two resource strings read by the
  `flag != 0` branch of the packet (+856/+864) or the two u32 (+872/+876); the `flag == 0` branch reads the **shared
  skill data section** (`sub_141DC0BC0`). ⇒ **Our 0x1ab carries neither payload**, so the client can only go through
  stat 6433 → the GeometryAttack row.

**Still to be found out**: after the client has built the Effect with 332, who fills the geometry's "start point +
target entity list" (the `+792` subobject) — the action's own target cell (0x1ab already carries `aim.X/aim.Y`), or
does the server have to send a separate action state? Until that is clear, do not remove the server's layer; put the
two side by side and compare frames first.

## What the server should send (conclusion)

> **Added on 2026-11-17**: the client's action classes pick the art by **`MiscAnimated`'s HASH32** (the mechanism is
> described in `README.md`, "The chain that picks the official art"). The hash of the ordinary attack's beam lives
> in the ActionData on the client, and our client tables do not have that sheet, so whether the client can draw this
> beam by itself is still **neither disproved nor confirmed**.
> Both ways stay open: ① first follow the "What the server should send" section below and hang the art on the hand
> bone with an `EffectObject` (the image is right, only the attachment point changes); ② if the live client turns
> out to draw it itself (a thin beam leaving the hand during the ordinary attack), switch our `EffectObject` off and
> keep only the damage.


1. **Animation**: `SpellProjectile` (`Animation 20`) → `beam_01`, with `contact` at 0.672 s (the clip of the final
   7012 is 1350 ms).
2. **Damage**: settled in one go at the `contact` moment (the existing `hits` already does this).
3. **Visuals**: **do not draw the ordinary attack with `ServerBeamEffect` any more** (the official ordinary attack
   does not use it; rows 227 and 228 are both lava slabs). Instead, hang the official asset **on the hand bone**:
   - the entity: `Metadata/Effects/PermanentServerEffect` (`EffectObject`),
     `AnimatedObject = …/Orion/spam_beam.ao` (the final `spam_beam_final.ao`);
   - `Parent` = the caster's id, `Bone` = `R_Weapon` (the final can alternate with `L_Weapon`),
     `AttachKind = 0`, `AttachFlags = 3` (the Arrow Nova combination: **the bone's facing is kept**, so the effect
     points wherever the weapon points);
   - the position can be the caster's cell coordinates (the parent object carries it along), lifetime 0.6 s.
   The `Parent` / `Bone` / `AttachKind` / `AttachFlags` fields of `EffectObject` are all in
   `src/GameServer.Core/Protocol/Instance/InstanceEntities.cs:859-931`.
4. **Length**: the official length is driven by the bone group `beam_length` from the skill data; placing a bare
   Effect only gets the model's default length. To match the official length, dig further into how `sub_141E59F80`
   writes the attribute into the Effect's bone group (**still open**).

## In the live client (2026-11-17 16:40–17:05, two rounds)

### Round one: remove the server's mesh

Change: the 7008/7012 branch in `Beam()` of `Sirus.Combat.cs` becomes a plain `return` (the server no longer places
`spam_beam.ao`).

Prediction: the beam the client draws itself will come out of the hand.

Result (frames grabbed from `logs/shots/sirus-kind0-1`, 31 frames): **there is a white ellipse ring at the hand, but
no beam body**.
⇒ The client did build `spam_beam.ao` (the `beam_flash`/`aux_circle2` ring), but **its length is zero**: there are no
endpoints.

### Round two (H1): 0x1ab carries the target object's id

Hypothesis: the endpoints are "target entities" and we did not send the target object's id.

Change: `Cast(...)` takes `uint? targetObjectId`, and the ordinary attack's call site passes `PlayerId` (the 0x1ab
flags bit 0x80).

Result (frames grabbed from `logs/shots/sirus-h1`, 38 frames; per-frame bright-pixel / red-orange-pixel counts):
**still no beam**, only the ring at the hand.
⇒ H1 rejected.

### The state now (objective)

| Symptom | Cause |
|---|---|
| The white ellipse ring at the hand | the `spam_beam.ao` effect the client built itself (the `beam_flash` bone group `aux_circle2`) |
| No beam body | missing endpoints ⇒ the length of the `beam_length` bone group = 0 |
| The wide lava beams on the clone's two arms | our `ServerBeamEffect` row 37 (`FireBeam`, `firebeam_beam.ao`), unrelated to the ordinary attack |

⇒ The official beam's **direction and length must be given by the server through another message** (the geometry
channel state); we do not send that message yet (the subtask `geomstate` is looking into it).

### A usable stand-in for the art: `MiscBeams` row 364

The 11 `MiscBeams` rows related to AtlasExiles have **none** of `spam_beam.ao`/`triple_beam_0N.ao`/
`clone_trap_beam.ao` ⇒ the three official images **cannot** be drawn with `ServerBeamEffect`.

But **row 364 `HeistRobotPyreKnightSpamBeam`** → MiscAnimated 4791 →
`Metadata/Effects/Spells/monsters_effects/League_Heist/robot/pyre_knight/spambeam.ao`:
rig `spam_beam/spambeam.amd`, skin `spam_beam/spambeam.sm`, `BoneGroups: line false root aux_beam_target`, the
line-shaped particle `pyre_knight/spambeam/line.pet`.
⇒ A **thin beam** whose direction and length are fully controllable; it reads much closer to the official one than
the rejected rows 37/227/228 (the lava wall), but the art is not Sirus's own.

### Bone groups and length (`logs/tmp/re/beamlength.md`)

A bone group is only a "name → bone index" index (`sub_1429432F0` text / `sub_142BBB680` binary, entries of 56 B =
{name, vector<int> boneIndices}); **there is no general "stretch bone group" API**; the length lives on the animated
object: `sub_141DCC410` (vtable 0x1435a1a80 slot 28) maps `beam_length`→+556 and `radius`→+552, and `sub_141E31CA0`
sets 0x7FFFFFFF to mean automatic; in the per-frame `sub_141E37DB0`, **+556 = +656** (the writer of +656 is not
solved).

### The clone's two beams (`logs/tmp/re/beamlength.md`)

The client has zero hits for `DaemonSpecialBeam`/`AtlasExiles5BeamTargetDaemon`/`clone_trap_beam`, none of the 121
action classes is a Clone/Beam class, and the `AtlasExile5` string has no code reference ⇒ **there is no
daemon→daemon beam code**; the clone's two beams = **two independent geometry beams** (the start point = the clone's
contact `beam_01`/`beam_02`, the end point = the entity list the server sends).


## In the live client (2026-11-17, evening)

The first batch of changes after the user decided "let row 364 hold the fort for now": the ordinary attack 7008/7012
drops the server's flat grid on the ground and uses `MiscBeams` row **364** instead
(`HeistRobotPyreKnightSpamBeam` → MiscAnimated 4791 → `League_Heist/robot/pyre_knight/spambeam.ao`, a line-shaped
particle, the thinnest beam there is), and the hit packet carries the `R_Weapon` bone.

- **The beam the client draws itself does exist**: a white ellipse ring at the hand (the `beam_flash`/`aux_circle2`
  of `spam_beam.ao`), but no endpoints ⇒ length 0 (frames grabbed from `logs/shots/sirus-h1`).
- **H1 failed**: after putting the target object's id (`PlayerId`) into 0x1ab, the client still only draws the ring.
- **The server beam of row 364 rendered successfully**: a **thin red line** shoots out of Sirus's right hand, crosses
  the player and reaches about 15 cells past the edge of the screen (screenshot
  `logs/shots/sirus-thin/beam-row364.jpg`, the frame at 17:36:15.386 in the log; the log line reads
  `[Sirus] beam: …6D01…`, `6D01` = 365 = row 364 + 1, bone name `R_Weapon`).
- ⇒ The ordinary attack is now "the official structure + someone else's thin-line art": the start point is on the
  hand, the direction points at the player, the length is controllable, the edge is crisp; the colour is on the red
  side (the official one goes white→orange→red), and once the geometry endpoint message is in hand it can go back to
  `spam_beam.ao`.

## Still open

1. **Which message gives the official beam's endpoints** (geometry channel state ⇒ target entity list) — the
   subtask `geomstate` is looking into it (`logs/tmp/re/geomstate.md`).
2. **The official beam has never been photographed in the live client**: the reference image
   `logs/shots/ref/wiki_Beam.png` (it leaves the hand/weapon, two red rings at the muzzle, floating in the air, a
   white-orange core with a red edge). The server no longer draws the ordinary beam (it has been removed), so the
   official shape can only come from the client drawing it itself ⇒ it depends on the endpoints of (1).
3. **Which geometry row the official ordinary attack uses**: 332 (`spam_beam.ao`) is the ordinary attack,
   342–344 (`triple_beam_0{1,2,3}.ao`) are the multi-beam, 359 (`clone_trap_beam.ao`) is the clone's two beams ⇒
   which row the official client uses is still unknown.
4. **The muzzle rings** (the two red rings of the official ordinary attack) are in `MiscAnimated` as
   `DieBeamRing{Physical,Cold,Fire,Lightning}{1,2,3}` →
   `Metadata/Effects/Microtransactions/Rings/die_beam_ring/triple_beam_0{1,2,3}.ao` (they differ from
   `TripleBeam/triple_beam_0N.ao` by one sound line each); **no `MiscBeams` row references them**, so
   `ServerBeamEffect` cannot reach them and they can only be sent as **a separate effect entity** (EffectObject).
5. Whether `then beam_02` really plays the second part depends on whether the official really does two beats in a
   row; the data says so, but the live client cannot be compared directly.
6. Which Effect asset `GeometrySpell` actually gets when it takes the `flag == 0` path (reading the client's local
   skill data): the live evidence is that **the client builds the effect itself and picks the official art** (the
   white ring at the hand = `spam_beam.ao`).
7. **Candidates for a permanent solution** (waiting for the user's choice): (a) keep digging for the geometry state
   message (the subtask is running); (b) the server switches to `ServerBeamEffect` row 364 (the Pyre Knight thin
   line, with a controllable direction/length); (c) keep the status quo (the ring only).
