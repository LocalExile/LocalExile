# 04 — The player entity: object types, 0x2bb and the component blob

Code: `InstanceEntities.ObjectTypeHash`, `InstanceEntities.CreateObject`, `PlayerObject.Blob()`, `BlobWriter`
(`src/GameServer.Core/Protocol/Instance/InstanceEntities.cs`). Tests: `InstanceEntitiesTests`. The component order and
sizes were **traced live with Frida** (`scripts/re/frida/trace-player-blob.js`). Static reading alone got the order
wrong twice; see [06](06-debugging-log.md).

## 1. Object types need no registration message

`world+0x8d8` holds `Game::GameObjectTypeRegister`, the resource "Metadata/GameObjectRegister" (factory `0x141eebd10`,
cache lookup `0x141eea7d0`). Despite the name it is not a file: its loader `0x141ec8170` **builds it from the client's
own dat tables**. It walks the rows of several tables and registers each row's metadata path with
`0x1424fe0d0(register, path, base, …)`:

| Row size | Table (where identified) | Base type passed |
|---|---|---|
| 0x141 | | row field +0xc2 |
| 0xac | | row field +0x10 |
| 0xea | **Projectiles.dat** | row field +0x43 (`InheritsFrom`) |
| 0x264 | **Characters.dat** | `"Metadata/Characters/Character"` |
| 0x31a | | row field +0x48 (with a list at +0x2e3/+0x2eb) |
| 0x8a, 0x2c, 0x9c | | none |

The projectile row/name-to-retained-type and ordered component-construction path is traced in
[projectile object-type binding](../re/projectiles/object-type-binding.md); same-name source priority and generated
parent fallback are distinct branches. This identifies the table above without changing the registration protocol.

The register is a sorted vector of 0x30-byte entries keyed by a u32 under a critical section (lookup `0x141674420`,
insert `0x1424fe270`).

**Key = MurmurHash2, seed 0, over the path with each UTF-16 unit narrowed to a byte** (`0x14247b370` narrows, then
`0x140df6e10` hashes: `m = 0x5bd1e995`, `h = len`, 4-byte LE blocks, standard tail and finalizer).

| Path | Key |
|---|---|
| `Metadata/Characters/Int/Int` (Witch) | `0x9c41993e` |
| `Metadata/Characters/Str/Str` (Marauder) | `0xd7aa9065` |

Characters.dat rows (`data/tables/Characters.json`): 0 Marauder `Str/Str`, **1 Witch `Int/Int`**, 2 Scion
`StrDexInt/StrDexInt`, 3 Ranger `Dex/Dex`, 4 Duelist `StrDex/StrDex`, 5 Shadow `DexInt/DexInt`, 6 Templar
`StrInt/StrInt`. Note that the login 0x15 class byte uses a different enum (3 showed "Witch").

A key missing from the register makes the world handler log "Unknown object type serialized by server" and create
nothing.

## 2. S→C 0x2bb: create object

World group (factory `0x1421ccac0`, handler `0x14218ddf0`). Class `0x1435e81f8`, write `0x141fe6cf0`, read `0x141fe6d40`.

```
u16  0x02bb
u32  entity id       +0x10   ┐ common entity header (0x141f98850 / 0x141f988f0)
u32  ordering object +0x14   │ local server sends 0
u16  checkpoint      +0x18   ┘ local server sends 0
u32  object type    +0x20   MurmurHash2 key (section 1)
blob16 component data +0x28 u16 BE length + bytes (0x141fab040)
```

The common writer emits `u16 kind, u32 target, u32 ordering object, u16 checkpoint`, all big-endian;
the common reader consumes the ten bytes after kind. The last two fields place incoming updates at supported
World/component boundaries in nonzero mode; they are independent of the updated entity ID.
See [World message inputs](../re/projectiles/world-message-inputs.md) for the codec, embedded intake interface,
ownership/cursor rules and frame-admission gate. Zero values above describe the local server's use.

Handler (`0x14218ddf0`, id 0x2bb): look the type up, build the object with `0x141ec4a40(obj, type, id, &blob, world)`
(0x248 bytes), then if `id == world+0x980` (set by 0x22) the object becomes the **local player** (`world+0x970`,
`0x141ec9890`, event `0xe794fcda`). It is only processed when the world activates, i.e. on 0x21 (see
[03](03-area-and-world.md#4-world-messages-are-queued-until-the-world-is-active)).

`InstanceEntities.CreateObject(0x1000, ObjectTypeHash("Metadata/Characters/Int/Int"), witch.Blob())`.

## 3. The component blob

**Little-endian.** Every read is a bounds-checked memcpy on a reader `{u8* data; u64 size; u64 pos}`. Running short
throws (the out-of-range builder is `0x140e272d0`, the exception type `0x143a5c5c0`) and the client shows a
**"Buffer Underflow"** message box. `0x141ec4a40` reads, in order:

1. **u8 object flags**: bit0 → obj `+0x8c |= 0x80`, bit1 → `+0x8d |= 2`
2. **base data** (`0x141dedf80`): u8 n, then n × (raw u32, u8, sub-data `0x141dfe5a0`)
3. **each component's deserializer**, vtable slot 17 (+0x88), for the first `type+0x80` components in the type's
   component order
4. **tasks** (`0x141ee6950`): u8 n, then n × (u16 task id + task data; an unknown id logs "Unrecognised task serialised by
   server; missing registration?")

### Component order: traced, and now derived from the .ot files

Components are built from the type's component list (`0x14242dfe0`). The object-type constructor `0x141ec7c10`
creates **Positioned first, for every type**, then loads the `.ot` (`0x14247b5b0` → parser `0x14247c6a0`: `extends`
first via `0x14247cdd0`, then the file's sections via `0x14247d050`, a name already present keeping its place). The
`client { }` blocks are appended after that, and `type+0x80` (the count the read loop uses) is the number before them.
The parent `Metadata/Characters/Character.ot` lists `Stats, Positioned, Pathfinding, Buffs, Life, Animated, Player,
PlayerClass, Inventories, Actor` and `client { Render, Targetable }`; with Positioned moved first that is exactly the
live order below. `python -I scripts/re/ot_components.py --check` derives it and compares it with this table (and with
AreaTransition, [08](08-area-transitions.md)); [../re/object-types.md](../re/object-types.md) lists other types.
**The live object has 12 components:**

| # | Component (vtable) | +0x88 deserializer | Reads | Minimal bytes |
|---|---|---|---|---|
| 0 | Positioned `0x1435ab608` | `0x141e323f0` | yes | 16 |
| 1 | Stats (`ClientInternalStats` subclass) `0x1435e7f88` | `0x1421c98b0` | yes | 10 |
| 2 | Pathfinding `0x1435b3800` | `0x1400f1180` (ret) | no | 0 |
| 3 | Buffs `0x1435a7a20` | `0x141e14b30` | yes | 2 |
| 4 | Life `0x1435b0d98` | `0x141e72890` | yes | 55 |
| 5 | Animated `0x1435af9c0` | `0x141e5afd0` | yes | 1 |
| 6 | Player (`ClientPlayer`) `0x143470e78` | `0x141e84300` | yes | 61 for "DevWitch" |
| 7 | PlayerClass `0x1435aa590` | `0x1400f1180` | no | 0 |
| 8 | Inventories `0x1435a6cb8` | `0x1415daf60` → own slot 20 `0x141e22490` | yes | 2 |
| 9 | Actor (`ClientActor`) `0x14347a818` | `0x1421993f0` (+ own slot 20 `0x142199c20`) | yes | 17 |
| 10 | Render (client) `0x1434730b8` | (`0x1415fb0f0`) | not reached | 0 |
| 11 | Targetable (client) `0x143476978` | (`0x14162bd60`) | not reached | 0 |

Total for "DevWitch": 1 + 1 + 16 + 10 + 2 + 55 + 1 + 61 + 2 + 17 + 1 = **167 bytes**, consumed exactly (live).

How the component classes were found (they have no RTTI): each has a static initializer in `0x14006xxxx`
(`lea rdx,"Name"; lea rcx,pool; call 0x140e35f40`), listed in an initializer table (e.g. `0x142f536a0` for
Positioned). The pool (`0x1445c63b0` for Positioned) is used by exactly one other function, the **factory**
(`0x141e31ad0`), whose constructor sets the component vtable. Pools: Stats `0x1445c6860`, Positioned `0x1445c63b0`,
Pathfinding `0x1445c6fc0`, Life `0x1445c6ed0`, Animated `0x1445c68d0`, Player `0x1445c6e80`, Inventories
`0x1445c59c0`, Actor `0x1445c7780`. Factories: `0x141e44010`, `0x141e31ad0`, `0x141e81340`, `0x141e6d6b0`, `0x141e59070`,
`0x141e83bb0`, `0x141e22270`, `0x141e8e760`. The vtables found this way for Stats (`0x1435ab340`), Player
(`0x1435b0650`) and Actor (`0x1435b5088`) are not the ones on the live object; the trace is authoritative. The live
ones are subclasses that `scripts/re/ghidra/ComponentRegistry.java` now finds too (installed by functions that
also install or call the base constructor): see [../re/components.md](../re/components.md). Read sequences of every
deserializer: [schema.md](schema.md).

## 4. Each component

Offsets are inside the component object. "If bN" means that bit of the component's own flag bytes.

### 4.0 Positioned (`0x141e323f0`), 16 bytes minimal

| Field | Offset | Notes |
|---|---|---|
| (prefix) u32 + u32 | Sector component `+0x44` | **only if** the component type's `+0x21` flag is set ("Sector" link). Not for players |
| i32 x | +0x294 | **grid** x |
| i32 y | +0x298 | grid y. World position = `(grid + 0.5) * const` |
| f32 rotation | +0x29c | radians; turns the object's model (the Coast exit's arch uses π/2, [08](08-area-transitions.md) §5) |
| u16 | +0x1e0 | |
| u8 flags0, u8 flags1 | | |
| if f0 & 0x20: f32 +0x2b0, f32 +0x2a8, f32 +0x2ac | | +0x2b0 the uniform scale (× the object type's own, type `+0x5c`; "Scale" in the info dump `0x141e36650`; setter `0x141e39050`, skipped when `+0x1e6` bit 4 "locked scale"), +0x2a8 / +0x2ac an x / y stretch on top of it: the local-to-world transform `0x141e394f0` takes x × +0x2a8 × +0x2b0, y × +0x2ac × +0x2b0, z × +0x2b0. The constructor sets 1.0. Sending s in all three draws at s² in x and y (Righteous Fire's ring, live 2026-10-23) |
| if f1 & 0x04: f32 +0x2d8, +0x2dc, +0x2e0, +0x2e4 | | probably a rotation quaternion (copied as one block) |
| if f0 & 0x04: i32 +0x290, f32 +0x2b4 | | **fixed height**: whole steps of 7.8125 and the world z; when `+0x290` ≠ `0x7fffffff` the z is used instead of the terrain height (`0x141e38b40`) |
| if f0 & 0x40: f32 +0x2b8, f32 +0x2bc | else derived from x/y | world x, y (else `(grid + 0.5) × 250/23`) |
| if f1 & 0x01: u32, u32, u32, u8, u32, u8 | | movement (`0x141e37460`) |
| if f1 & 0x02: u8, u8 | | visibility toggles |
| if f1 & 0x08: u8 | | `+0x1e6` bit 0x40: the object blocks no walker (the collision test `0x141e65ae0` skips it; pets) |
| if f1 & 0x10: u32 +0x2a0 | | |

Other bits only set flags: f0 0x01 → `+0x1e5|0x10`, f0 0x80 → `+0x1e6|2` (the object keeps its rotation while it glides with 0x1ba, live 2026-10-21), f0 0x02 → mount/parent lookups (stat keys
`0x7700c879`, `0x31fb1f01`), f1 0x20 → `+0x1e7|2` (no static position: the walk grid gets the object's Hull only while its TriggerableBlockage is blocked, [labyrinth-trials](../game/labyrinth-trials.md#unblocking-a-door)). The object is then inserted into the spatial index
(23×23 buckets, no bounds check: see [03](03-area-and-world.md#6-spatial-index-where-a-bad-position-crashes)).

### 4.1 Component 1 (`0x1421c98b0`, stats-like), 10 bytes minimal

compact int n, then n × (compact int key `0x141e655b0`, value `0x141e4c7e0` → `0x1424d65c0(stats, key, value)`), then
u32 LE entropy `+0x28`, f32 LE reset timer `+0x2c`, u8 needs-initialization `+0x30`.
The trailer's direct little-endian loads overwrite constructor defaults; these are component state, not raw stat pairs.
[Local hit prediction](../re/projectiles/local-hit-prediction.md) connects those fields to target-owned attack entropy
and its three-second reset clock, with exact stores and validation boundaries.
Actual official initialization values and later correction routes remain separate questions.
Compact int: a first byte < 0x80 is the value, 0x80–0xbf is a 2-byte form, and so
on. The writer counterpart is `0x141faa680`.

### 4.1a Player Actor scale (#299)

The network Stats provider stores the supplied key/value pairs without evaluating derived actor scale.
Raw `base_actor_scale_+%` (720) alone therefore does not drive the inspected network player scale path.
Creation reader `0x1421c98b0` and update reader `0x141fe7b70` reach setter `0x1424d65c0`.
Getter `0x1424d6510` searches its override vector before its upstream fallback.
The setter changes and notifies only the supplied key through `0x1424d67d0`.
Actor setup `0x141ea32d0` and change observer `0x141ea3840` consume final `actor_scale_+%` (1973).
Applicator `0x141ea3730` writes Positioned `+0x2b0` from Actor `+0x1a8 * (1 + final1973 / 100)` for players.
Its optional coefficient lookup `0x14013dd20` is Monster-specific and defaults to one.
Positioned `+0x1e6` mask **0x04** (numeric four) locks this scale write.
Render `0x1415f6850` multiplies type scale by uniform scale, then the independent x/y stretches, and interpolates.
Player blobs retain their existing Positioned layout; no custom uniform-scale field is added.

The generic native final1973 formula is registered at `0x1400cb050` with evaluator `0x1427f37e0`.
Its other inputs are capped scale (1974), scale limit (1975) and metamorphosis permyriad scale (10778).
The native SSE order matters, including its signed-half addition before integer truncation:

```text
a = float(raw10778) / 100.0f
a = a + float(raw720)
a = a + float(clamp(raw1974, -50, 50))
v = max(-99, truncate(a + (a < 0 ? -0.5f : 0.5f)))
if raw1975 != 0: v = min(v, raw1975)
```

`Metadata/Characters/Character.ot` supplies a baseline limit100 that adds to ordinary size-limit sources.
An ordinary limit1975=-50 therefore yields limit50; an ordinary zero preserves limit100.
Cosmetic limit inputs also add before development replacements; an explicit development zero disables the upper cap.
A negative nonzero limit still applies after the lower floor, matching the native operation order.
The Gateway saturates size-source dictionary additions to signed int at each aggregation boundary.
It saturates an out-of-range float-to-int conversion before applying the formula bounds.
These deterministic domain-extreme rules avoid undefined conversion or integer wraparound.
Ordinary in-range values preserve the native float and rounding order.

`CharacterPlayerStatsCore` projects final1973 after passive, active equipment, aura, flask and cosmetic inputs.
Only size inputs720/1974/10778/1975 add across cosmetics and ordinary sources, including the metadata limit baseline.
Unrelated cosmetics retain their existing `TryAdd` precedence.
Development raw replacements apply to the whole composed input, including explicit zero.
An explicit development final1973 replaces the result and intentionally bypasses formula bounds.
Projection uses a fresh dictionary and never feeds derived1973 back into source state.
Creation, post-activation and full refresh packets share this snapshot and always include one final1973, even zero.
Their send gate reads current sources and rejects a closed or leaving owner.
Removing the last source sends final0 to restore baseline through the native observer.
Peer publication replaces only1973 in an immutable template and sends only its delta/zero to seen peers.
Peer creation, late joining and revival use the latest published scale at the observer's send gate.
Closed/leaving source sessions are rejected there without locking their simulation.
Other peer creation stats, equipment and skills retain their existing replication behavior.

Rampager graph49937 supplies raw720=10 alongside its four other stats; this does not enable Phrecia admission.
Separate flag9807 conditionally scales native melee range at `0x141c9e4e5` and `0x141c9e59a`.
Rampager and Character metadata do not grant that flag; this implementation adds no9807 mechanic.
Native collision `0x141e65ae0` uses Positioned `+0x1e3`, independently of uniform `+0x2b0`.
Server AoE, reach/contact, collision/navigation and projectile height/radius remain unchanged.
RF retains its separate skill-radius effect object and its `(s, 1, 1)` Positioned scale encoding.
No CharacterPanelStats scale row exists, and no extraction output or manifest version changes are needed.

Primary formula evidence is static 3.29.3b disassembly, not Path of Building.
The inspected executable SHA256 is `78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5`.
Eighteen native checks on an unascended Witch also measured Positioned uniform and actual Render scale axes.
The instance-only read probe observes `0x1415f6850` after its tick; its argument is the Render base+0x28 subobject.
The owner pointer is argument-0x20; published axes are Render base+0x380/+0x384/+0x388 after interpolation.
An item with raw720=20 plus real amulet/ring cosmetics20/3 gives final43 and scale1.43 on all three axes.
Removal gives1.23,1.20 and1.0; area recreation, relog and death/revival retain their surviving sources.
Raw720=10 alone leaves scale1.0; canonical final10 gives1.1, and an explicit final0 restores1.0.
The native checks also verify capped1974, metadata limit100, explicit limit0 and positive half-away rounding.
Object size remains1, type scale1 and x/y stretches1 throughout these controls.
Source `0d7a01f` was built with matching Gateway/Core/Host informational revisions and a checked listener CWD.
Its Gateway SHA256 is `94e7104156daeddf250fcecea4385b777768251ad76093e95df4fd9d4f16b37b`.
Local evidence: `logs/phrecia-completion/scale299-20261027-090346/{metadata,results}.json`.
The probe reads instance stats and render state only; it does not capture credentials or handshake buffers.
Automated send-gate and loopback-wire tests cover current/late peer creation, change, removal and revival.
A second-client visual check and native AoE, RF, reach and projectile comparisons were not performed.
Those authoritative consumers are unchanged and have regression coverage; this is not official-server geometry proof.
Phrecia class admission and full Rampage remain separate prerequisites.

### 4.3 Component 3 (`0x141e14b30`), 2 bytes minimal

u16 n, then n × (u16, u8, record `0x141e0a210`) → `0x141e15420`. Probably skills (the record carries a 0x75 default).

### 4.4 Life (`0x141e72890`), 55 bytes

Three pools at component `+0x178`, `+0x1c8` and `+0x210` (presumably life, mana and energy shield, from the HUD), each read by `0x141e72410`:

| Field | Pool offset | Meaning |
|---|---|---|
| u32 | +0x30 | **current** |
| u32 | +0x10 | **reserved** (flat; the HUD clamps it to the maximum) |
| i16 | +0x14 | probably reserved percent |
| u32 | +0x28 | |
| u32 | +0x40 | |

Then u8 flags (`0x141e72810`): bit0 → `+0x26a` (corpse usable), bit1 → `+0x26b` (prevent corpse sinking).
The **maximum is not in the blob**: the client derives
it from stats (Witch level 1: life 50, mana 40). Live 2026-10-16: (50, 50, …) showed "Life 50/50 Reserved 50" and
"Mana 50/40 Reserved 40"; with (current, 0, 0, 0, 0) and mana 40 the HUD shows "Life 50/50", "Mana 40/40" and full orbs.

The Life type constructor `0x141e28b50` installs vtable `0x1435b36d8`.
Its store at `0x141e28ca5` writes `0x01000000` to config `+0xe4`: bytes `00 00 00 01`.
Thus `corpse_usable` at `+0xe7` defaults to true and `prevent_corpse_sinking` at `+0xe4` defaults to false.
Boolean parser `0x141e6dcc0` maps `corpse_usable` (`0x1435b11e0`) and `prevent_corpse_sinking` to those fields.
The finalizer `0x141e6d460` looks up `Player` (`0x1435b189c`) in the object's component map.
It sets config `+0xe6` at `0x141e6d526` when that component exists; this is the ordinary corpse-use veto.
Factory `0x141e6d6b0`, through constructor `0x141e6de70`, copies config `+0xe7/+0xe4` to runtime `+0x26a/+0x26b`.
Deserializer `0x141e72890` unconditionally overwrites both runtime bytes from the trailing wire flags.
These findings come from static examination of the primary 3.29.3b `Game.exe`, SHA-256
`78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5`.

Predicate `0x141e72bb0` accepts the ordinary corpse path only with runtime `+0x26a`, no config veto and life below 1.
With a skill context, target `is_spectre_corpse` (20977) requires `skill_is_minion_skill` (6339) and no
`cannot_use_spectre_corpses` (20976); a non-minion skill or that restriction causes an early refusal.
The early refusal precedes the fallback; a null skill context does not apply the spectre restriction.
Otherwise target `is_usable_as_corpse_when_alive` (9525) provides a separate fallback, including on dead targets.
A staged boss at life 1 therefore fails the ordinary path; a living object carrying wire bit0 is not yet a corpse.

`ot_components.py` resolves inherited Life booleans and the component-derived veto in `object-types` manifest v4.
The combined format also retains inherited `positioned` footprints and `animated_object` collision models.
Version 4 supersedes the separate Life-only and collision-only v3 outputs; `hideouts` still depends on `object-types`.
Missing inheritance keeps unresolved fields null; explicit later overrides, including false, remain authoritative.
`MonsterCatalog.Variety.LifeData` binds this record through `BaseMonsterTypeIndex`.
Ordinary `SpawnMonster` passes the resolved record to `MonsterObject`, which writes the two bits unchanged.
`MonsterLifeData.IsUsableAsCorpse` mirrors the source predicate and has an optional owned-actor exclusion guard.
It does not establish final death, corpse identity, availability or consumption.

Integrated native evidence, recorded in source commit `53edc77` for the implementation at `dbfe717`, covers six cases.
A living Hungry Corpse has bit0 set but the primary predicate returns false; after actual death it returns true.
An explicitly ineligible Raised Zombie stays false after death, and Hillock's life-1 stage stays false.
A Strength Totem carries bit1 independently of corpse usability.
An owned Spectral Wolf receives bit0 false through the integrated source's separate summon runtime.
The predicate was called on the game thread in that integrated run.
This split preserves the same ordinary flag math and runtime fields; this is inherited evidence, not a native pass
on this independent branch.
The summon runtime is not included here, so the owned-wolf case does not verify standalone actor ownership or spawning.
No client, runtime probe or extraction was run for this split; an actual Raise Zombie consumer remains unfinished.

### 4.5 Animated (`0x141e5afd0`), 1 byte minimal

u8 flags. Bit0: an animated-object reference (bit1 selects a u32 index into a 0x2c-byte table, otherwise a
u32-length UTF-16 path). Bit2 (`0x141e5f300`): u32 parent object, two f32 fields, then for a nonzero parent a
u32-length byte-string bone name, u8 attachment kind, u8 flags and three 32-bit offset fields.
The stationary Crawler's wheels use zero values for these optional fields and the native axle attachment bones.
An empty bone name means `<root>` and `*` means `<lock>` (`0x141dc6820`). The attachment kind is a mask on the transform
(`0x142909a50`): 1 drops the scale, 2 the translation, 4 the rotation, and 0x08 / 0x10 / 0x20 the same for the bone alone.
Righteous Fire's ring rides on the player's `<root>` with kind 4, so it does not turn with the player.
Bit3: u32, f32, u32 (animation + speed). Bit5: two u32. Bit4: several
u32. With 0 nothing else is read.

### 4.6 Player (`0x141e84300`), 61 bytes for "DevWitch"

| Field | Offset | Notes |
|---|---|---|
| u32 n + n × UTF-16 | +0x168 | **name** (`0x140e27600`) |
| u8 | → `+0x158` of the player data | **class**: Characters.dat **IntegerId** (Marauder 1, Witch 3; the row put the panel on Marauder, [13](13-items.md) §6), bit5 and bits 6–7 ascendancy. Changing it fires `0x141e4aaf0` |
| u32 | +0x18c | **experience** (`0x141e85da0`). The level `+0x1ac` is derived from it (`0x14242d1d0`) |
| u32 | +0x188 | |
| u16 | +0x1a8 | the portrait frame ([23](23-microtransactions.md) §3) |
| u16 | +0x1aa | the portrait ([23](23-microtransactions.md) §3) |
| vec16⟨u8 + 8 bytes⟩ | +0x1b8 | `0x141e89be0` |
| vec16⟨u8 + 8 bytes⟩ | +0x1d0 | **league quest flags**: bit id − 1 of the client's built-in list ([21](21-hideouts.md) §1) |
| 9 raw bytes | +0x1e8 | |
| u16, u16 | → +0x1f8, +0x208 | Hideouts.dat keys by HASH16 (lookups that fail give null and are tolerated); the first is the character's hideout ([21](21-hideouts.md) §1) |
| u8 | +0x1b1 | non-zero → "WashedUp" state; Actor's initial stance must also be 68 (below) |
| u8 × 5 | +0x1ae, +0x1ad, +0x1b0, +0x1af, +0x238 | +0x1ae / +0x1ad: the major and minor **Pantheon** god, PantheonPanelLayout row + 1, 0 = none (`PlayerObject.PantheonMajor/Minor`; [pantheon](../game/campaign/pantheon.md) §5) |
| u32 n + n × u32 | → vtable +0x158 | |
| u8 n + n × (record `0x141e8a960`, u32) | → vtable +0x160 | MicrotransactionCounters.dat |

**Updates (S→C 0x2bc, component index 6):** the vtable +0xa0 reader `0x141e85520` (both Player vtables) reads a u16
flag word, then one part per set bit; every part is conditional. Bit 0 = u32 **experience**, through the same setter
`0x141e85da0`: it derives the level again and, when it changed, sets "levelled up" (+0x2e8 = new > old) and calls the
component's vtable +0x108. Other bits: 2 and 8 the two vec16 lists, 0x200 the 9 raw bytes, 4 → +0x1a8 (frame), 0x40 → +0x1aa (portrait),
0x10 the Hideouts keys, 0x20 the class byte, 0x1000 → +0x1b0, 0x80 → +0x1ae/+0x1ad, 0x100 → +0x1af, 0x800 a u32-counted
list. `InstanceEntities.PlayerExperienceUpdate` sends `0001` + the u32 (`/set_level`); `PlayerPantheonUpdate` sends
`0080` + the two Pantheon bytes after C→S 0x1ee.

### 4.8 Inventories (`0x141e22490`, through slot 17 → slot 20), 2 bytes minimal

u8 n, then n × (u8 slot type, record `0x1424fb090`: u8 flag, 0 = empty), then u8 n2, then n2 × u16 (looked up in a
sorted list at `+0x158` and applied with `0x141eee810`). Entries are registered with `0x141e23010`. This component is
not the inventory manager that UI uses; that one needs 0xc0 ([05](05-inventories-and-ui.md)).

### 4.9 Component 9 (`0x1421993f0`), 17 bytes minimal

| Field | Offset | Notes |
|---|---|---|
| u32 n + n × UTF-16 | | a metadata path, looked up with `0x141728230`. Empty for us |
| u16 | +0x21c | |
| u8 | → `0x141e9d3f0` | initial stance: 0 base, 68 WashedUp |
| u8, u8 | +0x230, +0x234 | **only if** a runtime value (`… +0x1a8 < 1`) holds. It does **not** for our player (traced: 15 bytes in the main body) |
| u32 | +0x26c | |
| u32 | +0x274 | |
| then own vtable slot 20 `0x142199c20` | | 2 flag bytes (f0, f1). f0 0x40 → `0x14219e480`, else f0 0x20 → `0x14219f170`; f0 0x01 → u16 + `0x14193e340` record; f0 0x04 → u16 + record + u8; f0 0x08, f0 0x10 and f1 0x01 → further sections; f1 0x02 → a flag. All 0 for us |

For the beach introduction, Player's `+0x1b1` flag selects WashedUp (call site `0x141e846af`), but Actor is read later
and replaces the stance (call site `0x1421995c0`). Both fields must agree or the character starts standing.
All seven character `.act` files put WashedUp at stance 68; a first ground click sends action skill `0x37d9` and plays
getup in place. Native checks with fresh Duelist and Witch characters confirmed both animations.
Any other move request while down gets the action from the server instead (`0x141e9b860`): skill `0x37d9` at the
player's own cell, flags 0, with the sequence's top bit set. That path has not been checked in the client yet.
The initial Positioned rotation uses the beach room's transformed `default` point angle, preserving its facing.
Both belong to the owner's own object: the server relays no actions to observers, so a peer created in WashedUp would
stay down on their screens. Peer creation clears the flag and the rotation, and the peer stands from the start.

## 5. Our blob

```
00                                     object flags
00                                     base data: 0 entries
73000000 45000000 00000000 0000 00 00  Positioned (x = 115, y = 69 in the test; the Gateway uses 197, 261, see 03 §7)
00 00000000 00000000 00                comp1
0000                                   comp3
32000000 00000000 0000 00000000 00000000, 28000000 00000000 …, (00…) × 1, 00   Life: current 50 / 40 / 0, reserved 0, flags
00                                     Animated
08000000 "DevWitch" UTF-16LE  01  00000000 00000000 0000 0000  0000 0000  00×9  0000 0000  00  00×5  00000000  00   Player
0000                                   Inventories
00000000 0000 00 00000000 00000000 0000   comp9
00                                     tasks
```

`InstanceEntitiesTests.Minimal_player_blob_layout` pins this byte for byte.

## 6. Gold

S→C **0x2aa** (class `0x1431767e8`, read `0x140911270`): u32 BE, the gold shown for the character. The instance dispatcher
(case 0xe2, `0x142154531`) keeps it at session +0xc40c (0 in the session constructor `0x14214b4a0`), where the gold
respec cost check (`0x1409996c0`) and the village gold label (`0x1404ffb90`) read it, and plays Gold_Spend.ogg when it
drops. Found with a hardware watchpoint on the field and the dispatcher's disassembly (its decompile fails). The
Gateway keeps gold per account, as in the real game where every character of an account (and league) shares it:
`AccountStash.Gold`, saved in the account's stash row of `data/saves.db` ([18](18-stash.md) §6). It sends that amount on every area
entry, on `/gold` and after a gold respec. A character save from before this still holding `Gold` gives it to the
account at the character's next login (`CharacterSaves.TakeGold`). Leagues are not kept apart yet. Gold coins on the ground are the item
`Metadata/Items/Currency/GoldCoin` (type hash 0xDDA0D6FB, `0x14031eda0` plays their pickup sound); picking them up is
not done yet.

## Open

- Real meanings of the unnamed Player fields and Life +0x14/+0x28/+0x40.
- What comp1 (stats) pairs the client expects for a working character (attributes, max life …).
- Components 10/11 and the `type+0x80` count: why the loop stops after comp 9.
- Base-data entries and tasks (empty for now).
