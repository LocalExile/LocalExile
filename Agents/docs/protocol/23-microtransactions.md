# 23 — Microtransactions: owned lists, the Cosmetics tab, cosmetics in the world

Every account owns every microtransaction. The Gateway serves the lists the client's Microtransactions panel (N), the
store's ALREADY OWNED marks and the Cosmetics tab (K) read, takes the Cosmetics tab's picks and shows what is worn on
the character. No web service is involved: everything below goes over the instance connection. The store itself
(products, prices, buying) is out of scope.

Session offsets are the instance session object's. In the dispatcher `0x14214a190`, r13 is the session + 8, so its
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

**C→S 0x1f2** (3 bytes, vtable `0x1435c0000`, write `0x141f9bd10`): u16 id, u8 bits. Senders: the Microtransactions
panel on open (`0x14216d470`, bit 0), the store (`0x14216d4d0`, bits 1/3), the hideout picker (`0x140bba230`, bits
2/4), the panel controller `0x140a23a40`. Seen live: `01F201` (N), `01F206` (store owned + hideouts). Before
`KnownClientLength` knew it, the read loop stopped at it as unknown and lost the rest of the buffer.

**S→C 0x1f4** (dispatcher case 0x93 at `0x142151a1c`, read `0x1421aac00`): u16 id, u32 BE count, records, u8 flags.
The case tests bit 0 first, then bit 1: 0 → records, 1 → store owned, 3 → guild store. It **replaces** that list, sets
its loaded flag and fires the list's event.

**S→C 0x1f5** (dispatcher case 0x94 at `0x142151c26`, read `0x1421aaee0`): u16 id, u8 flags (bit 0 guild, bit 1
read, not modelled), compact count, one byte per hideout: a `Hideouts.dat` row index. The Gateway sends both empty.

### Records

A record in wire order (`0x141f8bac0`): u8 (+0x08), u32 BE MtxTypes HASH32 (+0x04), u32 BE id (+0x00), raw u64
(+0x10), wstr name, wstr description, u8 (+0x09), u8 0 (no variation list), u8 0 (no variant). The id is the key the
model upserts by. The panels resolve the hash in **`MTXTypeGameSpecific.dat`** (`0x1408e4fb0` → `0x142222170`): a hash
that is not there leaves a null and the row build crashes the client (live 2026-10-23: all 8411 `MtxTypes` rows
crashed it; 690 of them have no `MtxTypeGameSpecific` row). `MtxCatalog` sends the 7721 that have one, with id = the
`MtxTypes` row + 1 and empty names (the client names them from its catalogue). 7721 records are about 190 KB; the N
panel takes a moment to build its tiles the first time.

## 2. The Cosmetics tab (K)

Opening a slot's flyout sends nothing: it lists the records from §1. Picking sends **C→S 0x1f9** (8 bytes, vtable
`0x1435c0690`, write `0x141f9be90`, senders `0x140cdc2b0` and, with record 0 to empty a slot, `0x140cdef80`): u16 id,
u8 `MicrotransactionSlot` Id (20 = Portal), u32 BE record id, u8 option (0 in every pick seen). Live 2026-10-22, a
click on Arctic Portal Effect: `01F9 14 0000000B 00`.

The client wears nothing on its own. Its worn table is 50 entries of 24 bytes at session **+0xd118**, indexed by the
`MicrotransactionSlot` Id (1–49): u32 record id, u32 MtxTypeGameSpecific HASH32, u64, u8. Only **S→C 0x1f3** writes it
(dispatcher case 0x92 at `0x142150ef2`, read `0x1421aa830`): u16 id, u8 count + records (appended to the owned list),
**u8 count + worn entries** (u8 slot, u32 BE id, u32 BE hash, raw u64, u8), u8 count + list +0x40 (u32 +
`0x1424aca00`), u8 count + list +0x58 (u32 + `0x1424ac160`), u8 flags (bit 0 → +0x70 … bit 2 → +0x72; 1 by default).
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
ItemVisualIdentity: (u16 ItemVisualIdentity HASH16, u32 MtxTypeGameSpecific HASH32, u8) read by `0x1424f3fc0`, and
(u16 ItemVisualEffect HASH16, u32 hash, u8) read by `0x1424f4250`. `MicrotransactionSlot.__col7` is the model slot a
Cosmetics tab slot dresses: the item slots (Weapon1 0, Offhand1 1, BodyArmour1 2, Helm1 4, Gloves1 5, Boots1 6, Ring1
8, Ring2 9, Belt1 10, Amulet1 11) and slots only cosmetics use (armour attachment 3, character effect 12, footprints
13, helmet attachments 14 and 15, hip 16); 17 is none. The Gateway puts a skin in the first reference of the item's
entry, a weapon effect in the second, and sends the cosmetic-only slots as entries of their own (empty when nothing is
worn there). A skin needs an item in its slot.

**Stats.** The worn cosmetics' stats join the player's (0x1b3 and the player object).

**Pets.** Pets have no ItemisedVisualEffect row; each `Pet.dat` row names its MtxTypeGameSpecific row and its object
type (`Id`, e.g. `Metadata/Pet/Kiwi/Basic`). `pet_slot1` / `pet_slot2` (a Pet row + 1, `0x1401093a0` → `0x14010f530`)
alone spawns nothing (live 2026-10-23): the server creates the pet (0x2b9, `PetObject`). Its .ot chain
`Metadata/Pet/Pet` deserializes Positioned, Stats, Pathfinding, Buffs, Life, Animated, Actor, PetAi; PetAi (slot 20,
`0x141e7d410`) is a u32, the owner's object id (→ +0x44), which the client resolves to the entity the pet follows
(`0x141e7cfb0`). About one pet type in seven adds StateMachine (its shared states and timers from object-types.json),
Functions or BaseEvents after PetAi; left out, the client reads past the blob ("Buffer underflow", live 2026-10-23
with a Daughter of Sin pet, `TencentGirl03`). The Gateway spawns the worn pets (slots 18 and 19) on area entry and
after a pick, one on each side of the player, and walks them after the player with 0x1ab Move (as monsters move)
when they fall more than 12 cells behind, to a spot 6 cells beside the player. Pets do not block walkers: their
Positioned sends flags1 0x08 with 1 (+0x1e6 bit 0x40), which the client's object collision test (`0x141e61540`)
skips, as the .ot's `blocking = false` intends.

**The Goblin Troupe's song** (`Metadata/Pet/GoblinBand/GoblinBandLeader`) is a sound event of its IdleRelaxed
animation: the .act maps IdleRelaxed (`Animation.dat` row 100) to `Idle_bassoon_01`, whose SoundEvents play
`Audio/Sound Effects/Pet/GoblinBand/Music/IdleRandom`. The client's PetAi only parses the idle-relaxed options
(`0x141e7c280`: `disable_idle_relaxed_repeat` looks up the Actor's animation 100) and resolves the owner
(`0x141e7cfb0`); nothing found on the client starts IdleRelaxed, and the client has a `ServerPetAi` counterpart, so
the server is expected to start it. The leader's functions (`IdleRelaxed`, `EnterTown`, `LevelUp` …) also call
`band_members` / `band1` / `band2`, the Drums and Lyre goblins (`GoblinBandDrums`, `GoblinBandLyre`: no Pet.dat
rows), which the server spawns. Neither is done yet.

**The portrait.** MicrotransactionSlot 16; `MicrotransactionCharacterPortraitVariations.__col1` is the variation. In
the world: the Player component's u16 at +0x1aa (the player object, and 0x2ba with flags bit6, reader `0x141e7ee30`);
the character panel builds its art from it (`0x14081e730` → `0x142426b00`). On the character selection screen: the
list entry's U46 (`0x14082e210`), which Auth keeps per character (control op `set_portrait`).

**The portrait frame.** MicrotransactionSlot 17 ("Social Frame"); `MicrotransactionSocialFrameVariations.Index` is the
variation. The client draws `Art/2DArt/UIImages/Common/MTX/SupporterIconFrame{variation}` around a portrait, and
`Art/2DArt/UIImages/Common/DefaultIconFrame` for 0 or art that does not load. It appends the number and looks nothing up
in the table; `art/uiimages1.txt` lists 1 to 412, as `Index` runs (the shop icons' file names do not follow it: Index 131
uses `SupporterIconFrame128.dds`). Four rows have no such art, 269, 295, 327 and 376, all with a `BK2File`
(`Art/Videos/ChinaMTX/`, no reader of it found), so those four draw the default.

| Where | Drawn by | Read from |
|---|---|---|
| Character selection | `0x14082d660` | the list entry's U44 |
| Character panel | `0x14081e390` | the Player component's u16 at +0x1a8 |
| HUD portrait | `0x140774b20` | the same |
| Party portraits | `0x1402093a0` | the same (`0x14020bdc0`), or the known player's +0xb0 (`0x14020b230`) |
| Social panel rows | `0x140af6c20` | the known player's +0xb0 |

+0x1a8 is the player object's field and 0x2ba's flags bit 2 (`0x141e7ee30`, which fires the component's listeners when
it changes). A known player's frame and portrait are 0xa7's second pair ([28](28-player-trade.md) §2).

The client tells the player itself, so the server sends no text. After a pick in slot 16 or 17 the sender's
`0x140cdc9c0` prints ClientStrings `PortraitFrameEquippedMessage` (row 0xb5c) to chat with the cosmetic's name, or
`DefaultPortraitFrame` / `DefaultCharacterPortrait` for an emptied slot: "You must log out and in again for the change
to take effect" (a 0x17 line of the same text showed twice, live 2026-10-31). So a pick is kept apart from what is shown. The account store's `PortraitFrame` takes it at once (control op
`set_portrait_frame`) and the character list shows it; the login session copies it at Play, tickets carry that copy
through area changes, and "resolve" gives it to the Gateway (`ClientState.PortraitFrame`). Live 2026-10-31 (a 0x1f9
through the bridge, watching the image loader `0x1418667d0`): frame 3 asked for and loaded `SupporterIconFrame3` at
selection, in game and, through 0xa7, in a party member's client; +0x1a8 stayed 0 through an area change after the pick
and was 3 after the next Play.

**Skill effects.** The Cosmetics tab's skill slots are 0x1f3's second list (message +0x40 → session +0xd5c8, 40 bytes
each, sorted by key): raw u32 key, a target variant, u32 BE record id, u32 BE MtxTypeGameSpecific hash, raw u64. The
target's index 1 is one socketed gem (`0x1424ada40`): u8 Inventories.dat row → +4, u8 socket → +8, u8 effect slot type
→ +9, u32 BE the gem's BaseItemTypes HASH32 → +0. The panel looks the gem item up itself (`0x141f8c860`: the player's
inventory of that row, the item whose socket holds a gem of that hash); that item names the slot, and without it every
effect is refused with "type mismatch" (`0x1424eb7b0`, 0xb27; live 2026-10-23 with row and socket sent as 0). An effect
fits the slot (`0x1424ac1e0`) when its MtxTypeGameSpecific `__col4` and ItemisedVisualEffect `__col8` hold 27, its
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

Verified live 2026-10-23: skins, attachments, character effects, footprints, portals, cursors and flask skins.

## 4. Open

- Pets, the portrait and skill effects are built as above and await live verification (skill picks: 0x1fa seen live,
  its handling not yet).
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
  `0x142150fe5`–`0x142151007` exchanges the previous skill list; `0x142151200`–`0x142151254` fills the new list.
  `0x142151466`–`0x142151493` emits removed-key events and `0x1421514d0`–`0x142151500` emits current-target events.
  The removed-set difference (`0x142175327`–`0x142175376`) compares only the key, not the gem or other target fields:
  replacing A with B under the same key skips target removal even when both have record 0.
  `0x140ce0490` forwards target updates and `0x140ce04e0` forwards removal; `0x140cde190` looks up a full u32 key.
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
