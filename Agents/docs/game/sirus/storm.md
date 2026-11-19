# The death zones and the shockwaves (`InvisibleFireOrionDeathZone` / `deathzone.ao`)

## In one line

In the official game the storm / black circle is **not an effect, it is a monster entity**: all four variants of `InvisibleFireOrionDeathZone` use `Metadata/Monsters/AtlasExiles/OrionArenaObjects/OrionDeathZone.ao`, and the client draws it by itself (5 particles + 3 sounds). The body's `create_deathzone` event (`Emerge2` = `throne_activate_01` @**2.3439 s**) **has no handler in the client at all** ⇒ it draws nothing even when it receives it. The storm aura (ActiveSkill 1116) goes through the `AddBuffToTarget` action class, **zero overrides, no object creation** ⇒ **applying the buff produces nothing visible**; the aura's damage and slow are entirely the server's.
⇒ The right way: the server spawns the `InvisibleFireOrionDeathZone*` monster entity, publishes its 12 effects in the original `GrantedEffectsKeys` order and sends the shockwave actions one level at a time; `Fx(... "deathzone.ao")` is a detour (see the end of the page).

---

## Client data (observed)

| Item | Value |
|---|---|
| monster variants | `data/tables/MonsterVarieties.json` rows **3282–3285** (0-based index) |
| art | `Metadata/Monsters/AtlasExiles/OrionArenaObjects/OrionDeathZone.ao` (a `.act` of the same name) |
| base type (decides the size and the script) | 3282 = `AtlasExile5DeathZoneMobile`, 3283 = `AtlasExile5DeathZone`, 3284 = `AtlasExile5DeathZoneApparition`, 3285 = `AtlasExile5DeathZoneMobileSupporter` |
| effects (`GrantedEffectsKeys`, original order) | 3282: `6833, 6834, 10981, 10982, 7074–7081`; 3283: `6833, 6834, 10981, 10982, 4858`; 3284: `6833, 6835, 7074–7081`; 3285: `6836, 6837, 10983, 10984, 7074–7081` |
| aura effects | `AtlasOrionDeathZoneAura`(6833) / `…AuraCore`(6834) / `…AuraCoreApparition`(6835) / `…AuraSupporter`(6836) / `…AuraCoreSupporter`(6837) + `…Minion`(10981–10984) = ActiveSkill **1116** `add_buff_to_target_triggered`, ActionType **720** = `AddBuffToTarget`, CastTime 2000, **Animation empty** |
| shockwaves | `AtlasOrionDeathZoneShockwave`(7074) … `…Shockwave8`(7081) = ActiveSkill **1242** `geometry_spell` (ActionType 807), CastTime 1000, `active_skill_area_of_effect_radius_+%_final` 0, 50, … 350 (growing step by step) |
| summon | `SSMOrionApparitionSummonDeathZone`(3629) = ActiveSkill 499 `summon_specific_monsters_on_self` |
| geometry action animation slot | `data/tables/Animation.json` row **22** = `SpellAreaOfEffect` |
| size | `deathzone.sm` BoundingBox `±1500.5` (100 model units = 1 cell) ⇒ at scale 1 a **diameter of 30 cells**, 4.6 cells deep underground |
| room props | `Tools/re/out/sirusnum/arena/fountain.arm` places **6** `InvisibleFireOrionDeathZoneStationary`: (375,463) (513,223) (374,273) (223,350) (181,538) (536,441) |

The aura's damage per minute (one figure per element, see the storms section of `Agents/docs/game/sirus.md`): 6833 = 35,476 (591/s ×4), 6834 = 354,762 (5,913/s ×4), 6835 = 570,087 (9,501/s ×4), 10981/10982 = 83/s / 887/s (×4).

### The two deathzone arts (observed)

| | monster version `OrionDeathZone.ao` | effect version `deathzone.ao` |
|---|---|---|
| path | `Metadata/Monsters/AtlasExiles/OrionArenaObjects/` | `Metadata/Effects/Spells/monsters_effects/AtlasExiles/Orion/` |
| rig / mesh | `…/death_zone/rig.amd` + `…/death_zone/deathzone.sm` (the same set) | same |
| `default_animation` | none (the animation comes from the `.act`'s `Idle = "idle"`) | **has** `"idle"` |
| sound | `DeathCloud.loop.ogg`(root), `EarthCrumble.loop.ogg`(aux_up_circle), `DeathCloudStart.once.ogg`(aux_cyl2) | **none** |
| bone groups | many `root` / `frameupper` | none |
| particles (all at t=0) | `cloud.pet`(line), `death_core.pet`(sphere_core), `lightning_circle.pet`(up_circle), `grd_rocks.pet`(center_circle), `grd_shockwaves.pet`(shockwave) | the same five |

⇒ Only the monster version has sound and the root bone; the effect version can play on its own but **has no sound**. `…DeathZone.act`: `Idle = "idle"`, `Taunt`/`Run` = `@Idle`, `Death = "idle"`, `movement_speed 10.0`, `action_set minion`.

### Corrections to the old notes

1. The storm's circle is the **monster version** `OrionDeathZone.ao` (with sound); `deathzone.ao` is the effect version (no sound) — the old notes mixed the two up.
2. The `InvisibleFireOrionDeathZone` we spawn uses the **Mobile** base type (`base_actor_scale_+% = -80`) ⇒ not the 30-cell circle; the full-size one is `…DeathZoneStationary` (its base type `AtlasExile5DeathZone` has no -80).
3. `…Supporter` is also a Mobile base type (it carries -80 as well) plus `ModelSizeMultiplier 50`; how the two multipliers combine is **unresolved**.
4. The room holds **6** `…DeathZoneStationary` (the old notes said 4).
5. The shockwaves are 8 levels, `7074–7081`, named `…Shockwave`…`…Shockwave8` (there is no `Shockwave1`).
6. `Fx(... "deathzone.ao", scale: 2f)` is a **detour**; the right way is to spawn the monster entity (see the end of the page).

## What the client draws by itself / what the server must draw

- **The client draws**: ① the monster entity's own `OrionDeathZone.ao` (particles + sound + an `idle` loop of 250 ms); ② the shockwaves (a `geometry_spell` action, the client builds its own 584-byte Effect); ③ the body's `Emerge2` action and the `flashbang` white flash; ④ the aura texture on the player (the body's `.ot` `client.Preload` preloads `preload_buff = "atlas_orion_death_zone"` / `"atlas_orion_death_zone_core"` and `preload_animated_object = "…/dz_debuff.ao"`).
- **The client does not draw**: ① the `create_deathzone` event (searching the whole of the body's `atlasexile5.ot` for `deathzone` gives only two `preload_buff` lines, **there is no client handler**); ② the buff itself (`AddBuffToTarget` creates nothing).
- **The server has to do**: spawn the storm entity (with the official stats), publish its 12 effects, send the 8-level shockwave actions on time, and compute the aura's damage and slow.

## Client implementation (decompiled)

### The action class `AddBuffToTarget` (the aura 1116 uses it)

**Class card** (observed)

| Item | Value |
|---|---|
| Class name / HASH16 | `AddBuffToTarget` / **0x6DE2 (28130)** = `ActionTypes.json` row 720 |
| Factory / allocation | `sub_14198E350` / **792 bytes** (one of the smallest Actions) |
| Main vtable | `off_1434CB088` |
| Overrides | **zero**: all 28 slots land in the base class's code area `0x14193xxxx`/`0x14194xxxx` |
| ctx attributes read | **12731** (the animation playback speed multiplier); the base class ctor reads 2322/48/12674/9080/876/22504/2495 as well |
| Object creation | **none**: no `sub_142C8CB50(584)` + `Metadata/Effects/Effect`, no `sub_141F6A910` |
| dat table / target selection | **none** / **none** (it does not walk the targets and does not take position arguments) |

**State machine**: the class has no state of its own; all the behaviour is the base class's start-up `sub_1419E9840` (712 B):

1. `sub_1419E71C0()` resets; if `+488 != 0` (already "preparing") it returns straight away.
2. Choosing the animation number: virtual slots 528/1232/1248 → `sub_14193E130(a1,&anim,v5)`; `idx = (anim != 0) ? (anim - *table)/77 : 179` — **179 is the default when nothing is found**, and `Animation.json` has only 179 rows (index 0–178) ⇒ 179 is out of range (inferred: nothing is played; whether the client's animation table is the same table as this extraction was not checked).
3. Filling in the arguments: `args[0]=idx`, `args[1]=vtable[792]` (the action id), `args[2]=*(u32*)(a1+720) + *(u16*)(a1+282)` (start frame + duration), `args[3]=sub_141944680(a1)` (facing).
4. Speed: if ctx 12731 is not 0 ⇒ `vtable[1176]/100 + 1`; otherwise, if `vtable[1192]` is true then `vtable[1184]`; otherwise 1.0.
5. Playing: `sub_141EA0C60(ent,args)` (when `anim+27 != 0` it takes `sub_141EA2420` instead) — **it plays one animation on the caster entity**, and that is its only side effect.

**Serialisation (what the client receives)**

- This action **reads no resource string out of the packet** (compare `GeometrySpell`: it reads 2 resource strings + 2 u32). The client only gets an ActionStarted with "skill id + coordinates".
- Conclusion (observed): the server sends `add_buff_to_target_triggered`, the client at most "raises its hand once", **it creates no new object**.

**Hit tests and damage**

- The client **does not decide hits**: no target list, no radius, no `contact`; the targets are resolved entirely by the server. The aura's damage is 100% the server's: the `base_{physical,fire,lightning,chaos}_damage_to_deal_per_minute` of 6833/6834/6835/10981/10982.
- That is what we do (`Sirus.Damage.cs:62-82`): a 38-cell rim (6833 / minion 10981), a 13-cell core (6834 / minion 10982), nothing outside the circle; inside it `action_speed_-% 25`.

**What the client shows**

- The visible part of the official storm is only three things: ① the monster entity's `OrionDeathZone.ao`; ② the Effect the shockwave action builds itself; ③ the aura texture on the player (the buff's own effect / the preloaded `dz_debuff.ao`).
- **Conclusion (observed)**: applying the buff creates nothing visible; **when the storm aura 1116 applies its buff, the client creates nothing visible either**.

### Only two entry points create anything visible (client-wide)

| Entry point | What it does |
|---|---|
| `Effect` | a **584-byte** instance; the resource entry is the fixed string `Metadata/Effects/Effect` (23 characters); the content is filled in by the ActionData attribute `0x7A39632A` (2050581290) or by the two resource strings in the packet |
| `sub_141F6A910(owner, attrId, pos, mode2)` | instantiates an object from an attribute |

⇒ To decide whether "the client will draw something by itself": first check whether the skill is a geometry action (807 `GeometrySpell` ⇒ the client builds the Effect itself), then whether there is any attribute-driven instantiation. `AddBuffToTarget` **takes neither entry point**.

### Ground markers: `GeometryTrigger` (an alternative path, not used by the official storm)

`GeometryTrigger` = HASH16 **0x27ED (10221)**, 968 bytes, main vtable `off_1434480E8`; it uses ctx attribute **6433** to pick the row in `Data/GeometryTrigger.dat`; the start slot `sub_141516100` builds a 584-byte Effect itself + **picks a random `GroundMarker`** + **a random yaw** + plays `start`/`loop`; the wrap-up `sub_1415159E0` plays `end` and releases it.
⇒ To have the storm draw ground markers, **the server only has to send this action with attribute 6433 and the client draws it** (details in `logs/tmp/re/GeometryTrigger.md`). But the official storm's shockwaves go through `geometry_spell` (1242), **not** `GeometryTrigger`.

### What the server should send (summary)

1. Spawn the `InvisibleFireOrionDeathZone` entity and give it the official stats (`base_actor_scale_+% = -80` and the rest) — see the end of the page.
2. Publish these 12 effects as its skills in the `GrantedEffectsKeys` order (`0x4000` + the index) — right now there is not one.
3. Send the 8 shockwave actions one level at a time (`0x4004..0x400B`), radius 0 → 350 %.
4. The aura's damage and slow are all computed by the server, **do not** expect anything from the client.

### Still open

See the "## Still open" section at the end of the page; the most important line here is: **there is no live frame that proves the client drew the storm entity**.

## What the server should send

1. **The entity**: `Actor(c, Storm, pos)` (already at `Sirus.cs:274`), but it needs the stats of the official `.ot` (`atlasexile5deathzonemobile.ot`):
   - `base_actor_scale_+% = -80` (the Mobile base type; **without it the circle is 30 cells wide**; the server sends `base_actor_scale_+%` as `actor_scale_+%`, see `src/GameServer.Gateway/Monster/MonsterMods.cs:183-184`; `ModelSizeMultiplier` is given separately by `src/GameServer.Gateway/Monster/MonsterCatalog.cs:354`).
   - `base_cannot_be_damaged`, `base_cannot_be_stunned`, `cannot_be_knocked_back`, `untargetable_by_monster_ai`, `immune_to_curses`, `immune_to_auras_from_other_entities`, `set_item_drop_slots = 0`, `corpse_usable = false`; client state `Targetable { no_highlight = true; targetable = false }`.
   - Lifetime: `DiesAfterTime {}` is empty ⇒ **the official duration is not decided by the data** (inferred: it is controlled by the caster / the skill) ⇒ the server decides it itself (our intermission is 8 s).
2. **Publishing the skills**: number these 12 effects in the **original** `GrantedEffectsKeys` order (`SkillBase = 0x4000`, `Sirus.Native.cs:35`): `0x4000=6833`, `0x4001=6834`, `0x4002=10981`, `0x4003=10982`, `0x4004..0x400B = 7074..7081`. Right now `Effects(v)` returns nothing for the storm (`Sirus.Native.cs:33`) ⇒ the storm has no skills at all, so the client has no way to run the shockwaves.
3. **The shockwaves**: one `geometry_spell` action per level, drawn by the client itself (a geometry action builds its own Effect). The radius grows step by step, 0, 50, … 350, following `active_skill_area_of_effect_radius_+%_final`; the interval between the levels is not in the data, so our 0.2 s (`Sirus.Combat.cs:782-789`) is a guess ⇒ it has to be checked in the live client.
4. **The aura**: the official route is "the monster casts 1116 → the server puts the `atlas_orion_death_zone` buff on the player"; right now we have a **pure server-side DoT** (`Sirus.Damage.cs:81-82`) + `action_speed_-% 25` (`:106-110`), and the player has none of the official circle's texture. To get the official look back the player has to get the buff (the visuals then run through the client's buff effect).
5. **Drop the detour**: replace the `Fx(... "deathzone.ao", scale: 2f)` at `Sirus.cs:337-338` with points 1–4 above. The detour has three hard faults: the effect version `deathzone.ao` **has no sound** (only the monster version has `DeathCloud.loop.ogg` / `EarthCrumble.loop.ogg` / `DeathCloudStart.once.ogg`); it has none of the entity's 5 particle bindings; it does not go away with the entity and is not cleared by distance through `ClearNearbyStorms` (`Sirus.cs:304`). `Fx` can stay during the transition, but the commit message has to say that it is a temporary measure.

## Still open

1. **Did the client draw the storm entity at all**: the server already spawns `InvisibleFireOrionDeathZone` at `Sirus.cs:274`, but **there is no live screenshot** proving the client drew `OrionDeathZone.ao`. The old note's "the floor is empty" is a conclusion about `create_deathzone`, not about this entity ⇒ the next step is a live frame.
2. **The model's real diameter**: how `base_actor_scale_+% = -80` (20 %) and `ModelSizeMultiplier` (Supporter = 50) combine is unresolved; the estimate is Mobile = 6 cells, Supporter = 3 cells, and it has to be measured in the live client.
3. **The shockwave rhythm**: `active_skill_area_of_effect_radius_+%_final` only gives the radius; there is no data on the interval between the levels ⇒ 0.2 s is a guess.
4. **Which Effect asset a geometry action takes**: when `GeometrySpell` takes the `flag == 0` branch it reads the client's local skill data, and which asset it ends up with is **unresolved** (`body-beam.md` records the same line) — the shockwaves use exactly that.
5. **The storm's wall**: officially there is a wind wall that keeps the player inside the arena; all four `.ot` have `collision_hull_type = "projectile"` (the Supporter has `"walk"`) and `blocking = false` ⇒ the storm itself blocks nobody ⇒ that wall is something else, unresolved.
6. **The summoned variant**: the `alternate_minion` **611** of `SSMOrionApparitionSummonDeathZone`(3629) does not match any row in `MonsterVarieties` (row 611 there is a Bonewarped Archer) ⇒ which variant the clone summons is unresolved (the storms section of `Agents/docs/game/sirus.md` records the same dead end).
7. **The 6 StationaryStorms in the room**: they are there from the start (`fountain.arm`), and `Sirus.cs:181-184` spawns them now; whether they should already exist when the fight starts has to be confirmed with the player.
