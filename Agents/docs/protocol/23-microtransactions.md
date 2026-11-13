# 23 — Microtransactions: owned lists, the Cosmetics tab, cosmetics in the world

Every account owns every microtransaction. The Gateway serves the lists the client's Microtransactions panel (N), the
store's ALREADY OWNED marks and the Cosmetics tab (K) read, takes the Cosmetics tab's picks and shows what is worn on
the character. No web service is involved: everything below goes over the instance connection. The store itself
(products, prices, buying) is out of scope.

Session offsets are the instance session object's. In the dispatcher `0x142150ae0`, r13 is the session + 8, so its
`[r13+0xcd34]` is session `+0xcd3c` (see 13-items.md §6).

## 1. The lists and their "loaded" flags

The client keeps five lists. Each has a *requested* and a *loaded* flag; a panel shows "Loading…" until its list's
loaded flag is set, and asks for a list once per session.

| List | Bit in 0x1f2 | Requested | Loaded | Storage | Filled by |
|---|---|---|---|---|---|
| owned records (N panel, Cosmetics tab entries) | 0 | +0xcd3d | +0xcd3c | +0xd600 (400-byte records) | 0x1f4 flags 0 |
| the store's owned list (ALREADY OWNED) | 1 | +0xcd41 | +0xcd40 | +0xd618 | 0x1f4 flags 1 |
| unlocked hideouts | 2 | +0xcd45 | +0xcd44 | +0xd660 | 0x1f5 flags 0 |
| the guild's store list | 3 | +0xcd3f | +0xcd3e | +0xd630 | 0x1f4 flags 3 |
| guild hideouts | 4 | +0xcd43 | +0xcd42 | +0xd648 | 0x1f5 flags 1 |

**C→S 0x1f2** (3 bytes, vtable `0x1435caa38`, write `0x141fa2740`): u16 id, u8 bits. Senders: the Microtransactions
panel on open (`0x142173e20`, bit 0), the store (`0x142173e80`, bits 1/3), the hideout picker (`0x140bbaec0`, bits
2/4), the panel controller `0x140a24130`. Seen live: `01F201` (N), `01F206` (store owned + hideouts). Before
`KnownClientLength` knew it, the read loop stopped at it as unknown and lost the rest of the buffer.

**S→C 0x1f4** (dispatcher case 0x93 at `0x14215836c`, read `0x1421b1570`): u16 id, u32 BE count, records, u8 flags.
The case tests bit 0 first, then bit 1: 0 → records, 1 → store owned, 3 → guild store. It **replaces** that list, sets
its loaded flag and fires the list's event.

**S→C 0x1f5** (dispatcher case 0x94 at `0x142158576`, read `0x1421b1850`): u16 id, u8 flags (bit 0 guild, bit 1
read, not modelled), compact count, one byte per hideout: a `Hideouts.dat` row index. The Gateway sends both empty.

### Records

A record in wire order (`0x141f92390`): u8 (+0x08), u32 BE MtxTypes HASH32 (+0x04), u32 BE id (+0x00), raw u64
(+0x10), wstr name, wstr description, u8 (+0x09), u8 0 (no variation list), u8 0 (no variant). The id is the key the
model upserts by. The panels resolve the hash in **`MTXTypeGameSpecific.dat`** (`0x1408e1550` → `0x142228b00`): a hash
that is not there leaves a null and the row build crashes the client (live 2026-10-23: all 8411 `MtxTypes` rows
crashed it; 690 of them have no `MtxTypeGameSpecific` row). `MtxCatalog` sends the 7721 that have one, with id = the
`MtxTypes` row + 1 and empty names (the client names them from its catalogue). 7721 records are about 190 KB; the N
panel takes a moment to build its tiles the first time.

## 2. The Cosmetics tab (K)

Opening a slot's flyout sends nothing: it lists the records from §1. Picking sends **C→S 0x1f9** (8 bytes, vtable
`0x1435cb0c8`, write `0x141fa28c0`, senders `0x140cdce00` and, with record 0 to empty a slot, `0x140cdfb00`): u16 id,
u8 `MicrotransactionSlot` Id (20 = Portal), u32 BE record id, u8 option (0 in every pick seen). Live 2026-10-22, a
click on Arctic Portal Effect: `01F9 14 0000000B 00`.

The client wears nothing on its own. Its worn table is 50 entries of 24 bytes at session **+0xd118**, indexed by the
`MicrotransactionSlot` Id (1–49): u32 record id, u32 MtxTypeGameSpecific HASH32, u64, u8. Only **S→C 0x1f3** writes it
(dispatcher case 0x92 at `0x142157842`, read `0x1421b11a0`): u16 id, u8 count + records (appended to the owned list),
**u8 count + worn entries** (u8 slot, u32 BE id, u32 BE hash, raw u64, u8), u8 count + list +0x40 (u32 +
`0x1424b34d0`), u8 count + list +0x58 (u32 + `0x1424b2c30`), u8 flags (bit 0 → +0x70 … bit 2 → +0x72; 1 by default).
The case resets all 50 slots before filling them, so every 0x1f3 carries the whole worn table.

The Gateway saves picks per character (`Cosmetics` in the save: slot → record and option) and sends 0x1f3 with no
records and the worn table on every area entry and after every pick. A Portal pick also sets the portals' own
variation (21-hideouts.md §5).

## 3. Cosmetics in the world

The worn table alone draws what the client handles by itself: cursors, portals, flask skins. The rest comes from the
character's own data. `ItemisedVisualEffect.dat` has a row per cosmetic (`Mtx`: its MtxTypeGameSpecific row) that says
what it needs:

| Column | Meaning | Where it goes |
|---|---|---|
| `VisualIdentity` | a skin, attachment or character effect (ItemVisualIdentity) | the Inventories component entry's first reference |
| `VisualEffect` | a weapon effect (ItemVisualEffect) | the entry's second reference |
| `Stats` + `__col7` | stats and their values (footprints `footstep_effect_variation`, apparitions `imp_variation`, skills `skill_art_variation` …) | the player's stats |
| `__col8` | the slot groups it fits (`MicrotransactionSlot.__col9`) | |

**The model.** An Inventories component entry (13-items.md §6) has two cosmetic references before its own
ItemVisualIdentity: (u16 ItemVisualIdentity HASH16, u32 MtxTypeGameSpecific HASH32, u8) read by `0x1424fab70`, and
(u16 ItemVisualEffect HASH16, u32 hash, u8) read by `0x1424fae00`. `MicrotransactionSlot.__col7` is the model slot a
Cosmetics tab slot dresses: the item slots (Weapon1 0, Offhand1 1, BodyArmour1 2, Helm1 4, Gloves1 5, Boots1 6, Ring1
8, Ring2 9, Belt1 10, Amulet1 11) and slots only cosmetics use (armour attachment 3, character effect 12, footprints
13, helmet attachments 14 and 15, hip 16); 17 is none. The Gateway puts a skin in the first reference of the item's
entry, a weapon effect in the second, and sends the cosmetic-only slots as entries of their own (empty when nothing is
worn there). A skin needs an item in its slot, of a class its ItemisedVisualEffect `ItemClasses` lists (empty: any):
a sword skin is not drawn on a bow. A bow character's record has the bow in slot 1 and the quiver on the back in
slot 7 ([13](13-items.md) "What the model wears"), so with a bow the main hand's cosmetics (MicrotransactionSlot 1 to 3)
dress slot 1 and the off hand's (4 to 6) the worn quiver in slot 7: a quiver skin is an off-hand Weapon Skin with
`ItemClasses` Quiver (seen live 2026-11-12, Dragon Hunter Quiver).

**Stats.** The worn cosmetics' stats join the player's (0x1b3 and the player object).

**Derived stats.** The client keeps a calculator for every derived stat but runs none for a player the server
describes: it shows what it is sent ([04](04-player-entity.md) §4.1a). Of the 365 stats ItemisedVisualEffect rows give,
the client's own code reads 80 as they are, 196 through data (art variation tables, object tasks) or not at all, and 89
only inside a calculator: for those the server has to send the calculator's result, or the cosmetic does nothing. A
calculator's inputs sit in a list in `.rdata` beside it, and a derived stat is read like any other
(`mov edx, key`; a virtual call on the Stats). `CosmeticDerivedStats` sends the ones worked out so far:

| Derived stat | From | Rule |
|---|---|---|
| `mtx_virtual_morph_art_variation_index` | `mtx_goblin_morph` and the other morph stats | below |
| `virtual_endurance_charge_art_variation`, `virtual_frenzy_…`, `virtual_power_…` | `mtx_endurance_charge_art_variation` … (Alternate Charge Skins) | the skin's value, else a fixed 1000 to 1007 per other source, in the calculators' order (`0x1428ed890`, `0x1428edd70`, `0x1428ee4e0`) |
| `actor_scale_+%` | `base_actor_scale_+%` | [04](04-player-entity.md) §4.1a |
| choices of art (`DerivedArt`) | see below | the first input that is not 0 gives its number, else the fallback stat's value |

The choices of art, on the character and on every skill (a skill's calculators see its own stats, which hold its worn
Skill Gem Effect's, and the character's): `virtual_skill_surge_type_override` ← `skill_surge_type_override`;
`virtual_damage_hit_effect_index` ← `damage_hit_effect_index`, and its main-hand and off-hand forms (Cold Iron Point
66, the Cordycep bow 82); `virtual_ignite_visual_override` (the Demonic gloves 28, else `ignite_art_variation`), and
the same shape for shock (11, 14, 15), chill (2, 4, 6, 7, 8), freeze (3, 5, 6, 7), poison (2, 3) and bleed (3);
`virtual_mine_art_variation`, `virtual_ancestral_call_art_variation`, `virtual_triggered_summon_spider_variation`;
the rings' `virtual_onslaught_…` (2, 1), `virtual_arcane_surge_…` and `virtual_fortify_art_variation`;
`mtx_has_chest_effect`, `level_up_sound_disabled`, `virtual_has_item_drop_visual_replacement_stat`,
`virtual_mtx_breach_art_variation`, `virtual_covered_in_ash_art_variation`, and `is_floating` ←
`mtx_cosmic_armour_flying_player`. They are sent as the calculators give them; none was looked at on screen.

**Charge skins.** The derived stat alone changes nothing: the charge buff's 0x1c1 names the BuffVisuals row
(`BuffVisualsArtVariations` of `endurance_charge` / `frenzy_charge` / `power_charge` at that variation), as for every
buff whose art follows a variation ([11](11-skills.md) §12). Seen live 2026-11-12: Stygian Crystalline Charges.

**Morphs.** The Goblin Shaman Skin ("Transforms you into a Goblin Shaman when in a town or hideout") gives only
`mtx_goblin_morph` 1 (the Witchdoctor 2). The calculator of `mtx_virtual_morph_art_variation_index` (around
`0x1428d9cce`) gives 0 unless `is_in_town_or_hideout_or_heist_hub` is set, then the first of:
`mtx_morph_art_variation_index_from_polymorph`, `…_from_primary_pet`, `…_from_secondary_pet`;
`mtx_tencent_lunar_set_polymorph` 4 → 18 (19, 20 by `mtx_tencent_lunar_back_attachment` 1, 2);
`tencent_wuzhiqi_set_polymorph` 10 → 27; `mtx_goblin_morph` 1 → 28, 2 → 29; `mtx_skeleton_body_paint` 1 → 30;
`mtx_marionettes_revenge_skin_variation` 1 to 4 → 31 to 34. Nothing sets the in-town stat on the client and no
message carries it, so the server applies the rule in towns, hideouts and the Rogue Harbour (`HeistHub`, which
WorldAreas marks as neither). The forms are not meant for anywhere else: a transformation set has an idle, a run and
`Emerge`, maps a hit, death and casts to the idle clip and has no attack. About thirty client functions read the
result and every one only tests it for zero: a morphed character hides its weapons, flasks, footprints and stat-driven
visuals (`0x141fea550`) and takes the stance "default" (`0x141e9da30`). No client table or file maps the number to a
model: the model and the animation set arrive as paths. In the player's blob the Animated component's .ao
(`0x141e5afd0`) and the Actor's leading .act (`0x1421993f0`); on a pick, 0x2bc to the Animated component
(`0x141e5b9e0`) and **S→C 0x1b6** to the Actor (read `0x141fe4570`: object header, wstr .act path, u8 stance), then
the Inventories again. 0x1b6 only stores the set: the animation that was playing stays, so the new model stands frozen
in its bind pose until its next action (0x1b5, the stance, does not help; live 2026-11-12). The transformation sets
define `Emerge` (their idle with the pets' summon effect), so the server plays it (0x1d1, Animation row 43) and the
Actor idles on the new set afterwards. `WornMorph` knows the two goblins (28:
`Metadata/Pet/Goblins/GoblinShamanPet/GoblinShamanPet_01.ao` with `GoblinShamanTransform1.act`, 29: `_02.ao` with
`GoblinShamanTransform2.act`), the Skeleton Skin (30: `Metadata/Pet/SkeletonBodyPaint/SkeletonBodyPaint.ao` with
`SkeletonBodyPaintTransform.act`), the Lunar set (18 to 20) and the Wuzhiqi Potion (27), by the client's own
transformation rigs' names. Seen live 2026-11-12: the goblin on a pick in town and on entering town, the class's own
model outside and when the skin comes off, the skeleton, the Wuzhiqi form and the Lunar form (its four armour pieces).
A stat
sent alone (0x4336 = 28) changes nothing.

**Pets.** Pets have no ItemisedVisualEffect row; each `Pet.dat` row names its MtxTypeGameSpecific row and its object
type (`Id`, e.g. `Metadata/Pet/Kiwi/Basic`). `pet_slot1` / `pet_slot2` (a Pet row + 1, `0x1401096e0` → `0x14010f890`)
alone spawns nothing (live 2026-10-23): the server creates the pet (0x2bb, `PetObject`). Its .ot chain
`Metadata/Pet/Pet` deserializes Positioned, Stats, Pathfinding, Buffs, Life, Animated, Actor, PetAi; PetAi (slot 20,
`0x141e83b00`) is a u32, the owner's object id (→ +0x44), which the client resolves to the entity the pet follows
(`0x141e836a0`). About one pet type in seven adds StateMachine (its shared states and timers from object-types.json),
Functions or BaseEvents after PetAi; left out, the client reads past the blob ("Buffer underflow", live 2026-10-23
with a Daughter of Sin pet, `TencentGirl03`). The Gateway spawns the worn pets (slots 18 and 19) on area entry and
after a pick, one on each side of the player, and walks them after the player with 0x1ab Move (as monsters move)
when they fall more than 12 cells behind, to a spot 6 cells beside the player. Pets do not block walkers: their
Positioned sends flags1 0x08 with 1 (+0x1e6 bit 0x40), which the client's object collision test (`0x141e65ae0`)
skips, as the .ot's `blocking = false` intends.

**The Goblin Troupe's song** (`Metadata/Pet/GoblinBand/GoblinBandLeader`) is a sound event of its IdleRelaxed
animation: the .act maps IdleRelaxed (`Animation.dat` row 100) to `Idle_bassoon_01`, whose SoundEvents play
`Audio/Sound Effects/Pet/GoblinBand/Music/IdleRandom`. The client's PetAi only parses the idle-relaxed options
(`0x141e82970`: `disable_idle_relaxed_repeat` looks up the Actor's animation 100) and resolves the owner
(`0x141e836a0`); nothing found on the client starts IdleRelaxed, and the client has a `ServerPetAi` counterpart, so
the server is expected to start it. The leader's functions (`IdleRelaxed`, `EnterTown`, `LevelUp` …) also call
`band_members` / `band1` / `band2`, the Drums and Lyre goblins (`GoblinBandDrums`, `GoblinBandLyre`: no Pet.dat
rows), which the server spawns. Neither is done yet.

**The portrait.** MicrotransactionSlot 16; `MicrotransactionCharacterPortraitVariations.__col1` is the variation. In
the world: the Player component's u16 at +0x1aa (the player object, and 0x2bc with flags bit6, reader `0x141e85520`);
the character panel builds its art from it (`0x140820170` → `0x14242d610`). On the character selection screen: the
list entry's U46 (`0x140841260`), which Auth keeps per character (control op `set_portrait`).

**The portrait frame.** MicrotransactionSlot 17 ("Social Frame"); `MicrotransactionSocialFrameVariations.Index` is the
variation. The client draws `Art/2DArt/UIImages/Common/MTX/SupporterIconFrame{variation}` around a portrait, and
`Art/2DArt/UIImages/Common/DefaultIconFrame` for 0 or art that does not load. It appends the number and looks nothing up
in the table; `art/uiimages1.txt` lists 1 to 412, as `Index` runs (the shop icons' file names do not follow it: Index 131
uses `SupporterIconFrame128.dds`). Four rows have no such art, 269, 295, 327 and 376, all with a `BK2File`
(`Art/Videos/ChinaMTX/`, no reader of it found), so those four draw the default.

| Where | Drawn by | Read from |
|---|---|---|
| Character selection | `0x1408406b0` | the list entry's U44 |
| Character panel | `0x14081fdd0` | the Player component's u16 at +0x1a8 |
| HUD portrait | `0x140776570` | the same |
| Party portraits | `0x140209710` | the same (`0x14020c130`), or the known player's +0xb0 (`0x14020b5a0`) |
| Social panel rows | `0x140af7970` | the known player's +0xb0 |

+0x1a8 is the player object's field and 0x2bc's flags bit 2 (`0x141e85520`, which fires the component's listeners when
it changes). A known player's frame and portrait are 0xa7's second pair ([28](28-player-trade.md) §2).

The client tells the player itself, so the server sends no text. After a pick in slot 16 or 17 the sender's
`0x140cdd510` prints ClientStrings `PortraitFrameEquippedMessage` (row 0xb5c) to chat with the cosmetic's name, or
`DefaultPortraitFrame` / `DefaultCharacterPortrait` for an emptied slot: "You must log out and in again for the change
to take effect" (a 0x17 line of the same text showed twice, live 2026-10-31). So a pick is kept apart from what is shown. The account store's `PortraitFrame` takes it at once (control op
`set_portrait_frame`) and the character list shows it; the login session copies it at Play, tickets carry that copy
through area changes, and "resolve" gives it to the Gateway (`ClientState.PortraitFrame`). Live 2026-10-31 (a 0x1f9
through the bridge, watching the image loader `0x14186bfe0`): frame 3 asked for and loaded `SupporterIconFrame3` at
selection, in game and, through 0xa7, in a party member's client; +0x1a8 stayed 0 through an area change after the pick
and was 3 after the next Play.

**Skill effects.** The Cosmetics tab's skill slots are 0x1f3's second list (message +0x40 → session +0xd5c8, 40 bytes
each, sorted by key): raw u32 key, a target variant, u32 BE record id, u32 BE MtxTypeGameSpecific hash, raw u64. The
target's index 1 is one socketed gem (`0x1424b4510`): u8 Inventories.dat row → +4, u8 socket → +8, u8 effect slot type
→ +9, u32 BE the gem's BaseItemTypes HASH32 → +0. The panel looks the gem item up itself (`0x141f93130`: the player's
inventory of that row, the item whose socket holds a gem of that hash); that item names the slot, and without it every
effect is refused with "type mismatch" (`0x1424f2440`, 0xb27; live 2026-10-23 with row and socket sent as 0). An effect
fits the slot (`0x1424b2cb0`) when its MtxTypeGameSpecific `__col4` and ItemisedVisualEffect `__col8` hold 27, its
`EffectSlotType` is the slot's type (`MicrotransactionSkillGemEffectSlotTypes`: 0 PrimaryVisuals, 1 TotemSkin; a gem's
types are its SkillGems `MtxSlotTypes`), and its `BaseItemTypes` and `ItemClasses` lists hold the gem's or are empty.
**A skill effect's art.** The effect's `skill_art_variation` (its ItemisedVisualEffect stats) selects the skill's
art: `SkillArtVariations` names the variants (Righteous Fire: Default, NoRing, White, …, Zana), and
`MiscAnimatedArtVariations` / `BuffVisualsArtVariations` give the MiscAnimated object and the BuffVisuals row per
(Id, Variant). For server-made art the server picks them: Righteous Fire's ring (`righteous_fire_ring`: the AO, none
for NoRing) and its buffs' visuals (`righteous_fire_buff_visual`, `righteous_fire_aura_visual`), sent as 0x1c1's
BuffVisuals override (flags bit4, [11](11-skills.md) §12). **A pick in a skill slot** is not 0x1f9: it is **C→S 0x1fa** (10 bytes): u16 id, the slot's raw u32 key as 0x1f3
sent it, u32 BE the record id (0 empties the slot). Live 2026-10-23: `01FA 33000000 00000172` (key 51, record 370) and
`01FA 33000000 00000000`. Before the Gateway knew it, the read loop stopped at it as unknown and the pick was lost, so
no effect could be put on a gem. The Gateway sends one slot per active gem type and slot type (keys from 50), saves
picks per gem type and slot type (`SkillCosmetics`, `PickSkillCosmetic`), sends 0x1f3 again (the slot shows the effect)
and the skill list, which puts the worn effect's stats (`skill_art_variation` …) in that gem's skill only.
**Server-made art follows the variation:** Righteous Fire's ring and buff visuals (a burning fire is lit again at
once), and a projectile skill's projectile and explosion: `ProjectilesArtVariations` gives the projectile object for
(Id, Variant), found by its variant 0 (Fireball: `fireball` → `Metadata/Projectiles/StygianFireball` …; the object has
no .ot, the client builds it from its Projectiles.dat row and its AOFiles, sounds included), and
`MiscAnimatedArtVariations` the explosion in place of the default .ao (`fireball_standard`: `explode_new2.ao` →
`stygian/aoe.ao` …). A variation with no row there keeps the default.
**A skill's own variation.** Holy Sweep's (`sweep`: Default, NoEffect, Holy, Zana, Sirius, Abyssal) is 2: its hammer
(`sweep_hammer_fall`) has rows only at 2–5, and its effects' values (Originator's 1, Awakener's 2, Abyssal 3) land on
Zana, Sirius and Abyssal only added to 2 (`SkillArtBase`). Each 0x1af hit makes the client draw the hammer; at 0 it
logged "GetArtVariationItemInternal failed trying to access data from index 3 of skill art data (sweep:0, )" and
crashed (live 2026-11-08). In that line the index is the art's place in the row's `AnimatedArt` and the number after
the Id the variation: "index 2 … (ground_slam_ancestral:1, )" came with Blood Slam (1) worn, a slot that row lacks.

Verified live 2026-10-23: skins, attachments, character effects, footprints, portals, cursors and flask skins.

## 4. Open

- Pets, the portrait and skill effects are built as above and await live verification (skill picks: 0x1fa seen live,
  its handling not yet).
- Derived stats (§3). An ailment's or a buff's art also needs its buff to name the BuffVisuals row, as the charges'
  do: the ignite, poison and bleed the player causes (`AilmentArtVisual`, the art of the skill that last worked out its
  ailments) and the player's Onslaught and Fortification (`BuffArtVisual`) do. Seen live 2026-11-12: a Mystic Burning
  Arrow Effect's ignite burning teal on a monster; the others are sent the same way and were not looked at. The server
  has no shock, chill, freeze or Arcane Surge buff of its own to restyle yet. Not sent: `show_phasing_visual` /
  `virtual_phasing_art_variation` (`mtx_sunprism_phasing_ring`), and everything that needs state the server does not
  count yet: the
  `track_*` flags (kills, hits taken, blocks, skills used, Vaal souls), the kill-streak, hit-streak and kill-counter
  stacks, the Druidic gloves' charge intensity, and floating at 25 kill-streak stacks.
- Morphs without files: the Marionette's Revenge skins (31 to 34; no cosmetic gives their stat), the Ashbringer
  Wyvern (35), the polymorph wands and the pet morphs, whose numbers come from stats the server does not set.
- The Portal slot's two extra choices (`MicrotransactionSlotAdditionalDefaultOptions`: one random effect for every
  portal, a random effect per portal) arrive as 0x1f9 with record 0 and option 1 or 2. The server keeps the choice,
  lists the slot in 0x1f3 with record 0, hash 0 and the option (the client takes it; whether the panel then shows the
  choice was not seen) and picks each portal's variation itself (`Game.PortalVariation`; seen live 2026-11-12: two
  Portal Scroll portals with two effects). Sent by hand so far, not clicked in the panel.
- Issue [#306](https://github.com/Wrathen/LocalExile/issues/306): after Spark is replaced by Kinetic Blast, the icon
  can show KB while opening Spark's picker.
  The retained log reports Spark `0x900d0738`, record 122, a clear (record 0), then KB `0x174ecd0c` on key 50.
  These are packet and skill-bar observations, not a capture of the picker contents.
  The original file can be overwritten by service startup; the issue's excerpt is the retained evidence.
  Upstream `20b02f8` demonstrably reassigns keys by enumeration index and accepts any known record for that key.
  A delayed pick can therefore clear a removed A or save A's record on B; regression tests reproduce both defects.
  The fix assigns keys to full target lifetimes (gem hash, inventory, socket, effect slot type), retains unchanged
  targets, and never reuses retired keys during the connection, including A → B → A and empty → reappearance.
  Saved choices remain per gem hash and effect slot type; removing a target does not delete its saved choice.
  `SendWornCosmetics` samples inventory, saved effects and bindings inside the connection send gate, so queued work
  publishes current targets and cannot send an old skill snapshot after a later inventory mutation.
  Equipment/socket handlers send inventory changes before cosmetics, then skill stats and the skill bar.
  `RunHandlers` serializes those requests; no production send-order race was established from the captured log.
  A 0x1fa must name a published target still present in current equipment; nonzero records must fit group 27,
  effect slot type, BaseItemTypes and ItemClasses restrictions before any save mutation.
  Logs now include each key's gem, inventory, socket, slot type and record at the send gate.
  Read-only 3.29.3b disassembly (Client/Game.exe SHA-256
  `78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5`) confirms replacement/event paths:
  `0x142157935`–`0x142157957` exchanges the previous skill list; `0x142157b50`–`0x142157ba4` fills the new list.
  `0x142157db6`–`0x142157de3` emits removed-key events and `0x142157e20`–`0x142157e50` emits current-target events.
  The removed-set difference (`0x14217bc97`–`0x14217bce6`) compares only the key, not the gem or other target fields:
  replacing A with B under the same key skips target removal even when both have record 0.
  `0x140ce1010` forwards target updates and `0x140ce1060` forwards removal; `0x140cded10` looks up a full u32 key.
  A fresh key therefore exercises removal/addition instead of reusing an existing target's UI identity.
  These are static observations from `scripts/re/dis.py`, not observed execution or a complete picker trace.
  The REA full-program analysis was stopped before returning a decompilation; it adds no further protocol evidence.
  The client-side reason a same-key update can leave the picker stale remains separate from these server defects.
  Automated binding, save and queued-send tests cannot verify the native picker UI.
  User verification is still required on 3.29.3b: with Cosmetics open and reopened, test A with an effect and after
  clearing it, swap to B and inspect the picker, pick/clear B, then return to A and check its saved effect.
  Repeat with removed/reinserted gems, another skill pair, and primary/totem effect slots without relogging.
  Record both the visible picker and the new binding logs; none of this requires client file changes or a delay.
- The pets' idle-relaxed animations (the Goblin Troupe's song) and the troupe's band members: which server message
  starts IdleRelaxed is not found yet.
- Skill art for other skills: Righteous Fire's and the projectiles' server-made art follows the variation; skills
  whose art the client draws from the skill's stats need nothing more, others are untested. Other worn-effect stats
  (`ignite_art_variation`, `damage_hit_effect_index`, `skill_surge_type_override`) ride on the skill but are not acted
  on by the server.
- Skill slots are per gem type (the first socket holding it), not per socketed gem.
