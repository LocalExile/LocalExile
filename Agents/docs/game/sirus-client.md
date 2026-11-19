# Sirus in the client's code (Orion)

The data side is [Sirus, Awakener of Worlds](sirus.md): the effect rows, the arena, the clips, the storms. This page is
the code side — the client classes behind the five `Orion*` action types, how a skill reaches one, and what those
classes add. It closes the question [sirus.md](sirus.md) leaves open in *The clips*: Sirus's five action types appear in **no
`.act` file**, so the effect→animation binding (`GrantedEffects.Animation` → `Animation.Id`) cannot be what plays
them.

The code answers it. The client has a class per Sirus action type, selected by the action type's **`ActionTypes.HASH16`**
— a 16-bit hash of the type's `Id` string that the client recomputes in code — and each class declares the Sirus effect
rows (`MiscAnimated.HASH32`, `MiscObjects` object hashes, one projectile metadata path) that its action may use. The art
is bound by action type in code; the body animation is still the granted effect's, from data.

All addresses are the 3.29.3c release's code addresses (build 327984; they hold for 3.29.3d,
[tooling](../re/tooling.md) §7). Table values are from `data/client.db`.

## The registry: an action type's name hash → a class

`0x141947300` is the only writer, `register(name, constructor, collector, slots)`. It hashes the name and stores the
`(constructor, collector)` pair under that hash in slot 0, slot 1 or both, depending on its fourth argument.

- The hash starts at **0** (not FNV's basis), multiplies by `0x01000193`, xor's the byte in, and folds `hi ^ lo`. That
  reproduces the **`ActionTypes.HASH16`** column for every name tried: `OrionBeamSweep` 19042, `OrionMeteor` 28504,
  `OrionCorridorBlast` 40973, `OrionCircleMazeBlast` 42185, `OrionBeamBomb` 13449, `Teleport` 31245. The table is the
  contract between the code and the data.
- The map is the sorted vector at `0x1447603e8` (the function-local static of `0x1419475a0`), 24-byte records
  `{u16 hash, u8 slot, u16 pad, u64 constructor, u64 collector}`; `0x14197bd40` is its lower-bound find-or-insert.
- The fourth argument is which slot: **0 = both, 1 = slot 0 only, 2 = slot 1 only**. A name whose two slots hold *different*
  classes registers twice (`Melee`, `Puncture`, `Frenzy` do); a name whose slots hold the same pair registers once with
  0. All five `Orion*` names use one registration each with 0.

The slot is picked at creation time from the **actor's state**. `0x1419473e0` (`create(hash, actor, skill, flag)`)
asks the actor for a controller (its vtable `+0x148`) and reads that controller's state at `+0x14c`: state `0xe` or `9`,
or state `0x25` with `+0x150 == 9`, selects **slot 1**; anything else, or no controller, selects slot 0. It then finds
the `(hash, slot)` record (`0x14197bee0`) and calls the record's constructor with the actor, the skill and the flag.

`0x1419474d0` is the second entry point: it calls slot 0's collector, then slot 1's unless slot 1 holds the same
`(constructor, collector)` pair, so one pass touches every registered name once. In a record, `+8` is the constructor
and `+0x10` the collector.

## How a skill becomes an action

The whole decision is one field. `0x141ef0050` (the action factory the S→C `0x1ab` path goes through) finds the skill
instance by its 16-bit skill id, checks that the client may use it, and passes the u16 at **skill instance `+0x42`** to
`0x1419473e0` as the hash, with flag 0.

So: the server sends a skill id; the client resolves that skill's row (`ActiveSkills` → `GrantedEffects` →
`ActionTypes`), reads the action type's `HASH16` — the field at `+0x42` is in that key space, which is what ties the
skill to the registry — finds the class, and constructs it with the actor and the skill instance. Nothing else about
Sirus is needed on the wire — no effect name, no clip, no object path.

The other direction is the preload: `0x141ef0f00` (a skill instance added to an `ActiveSkillManager`) calls
`0x1419474d0`, which runs both slots' collectors for every registered action type the added skills use. That is when
the client resolves the effect assets below.

## The five classes

The live class is the one the **second** registration pass writes (next section); these are its constructors:

| action type | `ActionTypes` row | `HASH16` = ctor id | client ctor | class vtable | size | collector |
|---|---|---|---|---|---|---|
| `OrionBeamSweep` | 1034 | 19042 (`0x4a62`) | `0x14127ee70` | `0x14337c8e0` | 0x368 | `0x141416f90` |
| `OrionMeteor` | 1035 | 28504 (`0x6f58`) | `0x14127ed30` | `0x14337c358` | 0x338 | `0x14141a240` |
| `OrionCorridorBlast` | 1036 | 40973 (`0xa00d`) | `0x14127ec00` | `0x14337add8` | 0x338 | `0x141419ce0` |
| `OrionCircleMazeBlast` | 1037 | 42185 (`0xa4c9`) | `0x14127eab0` | `0x14337b8d8` | 0x3a0 | `0x141d1c130` |
| `OrionBeamBomb` | 1039 | 13449 (`0x3489`) | `0x1412d0520` | `0x1433801b8` | 0x3e0 | `0x1414151c0` |

The four base constructors they wrap are `0x141d1ac90`, `0x141d1f120`, `0x141d1de90` and `0x141d1be60`; each passes the
action type's `HASH16` on to the shared action constructor `0x1419e9b10` and then installs the class's vtables
(BeamSweep's base: hash `0x4a62` = `ActionTypes[1034].HASH16`, main vtable `0x143587090`, the one at `+0xA8`
`0x14323e408`).

`OrionBeamBomb` is the exception: it has no base class of its own, it constructs the shared action directly and swaps in
two vtables on the way (`0x1435885c8` → `0x1433801b8`). `0x1419e9b10` → `0x1419e5460` is the shared constructor
(vtable `0x14323e928`, a second one at `+0xA8` = `0x14323e408`); it reads the actor's components and skips one lookup
when the hash is `0x7b44`.

Every constructor then starts the same way, and this is the one branch that could keep the `Orion*` class from being
built at all. The first pass's BeamSweep constructor `0x141985ce0` (`(owner, actor, flag)`), when the flag is 0, asks
the actor for three properties in turn and hands off on the first non-zero answer:

| actor property | alternate class built instead | its vtable |
|---|---|---|
| `0x36c` | `0x1419e5860` | `0x1434c2db8` |
| `0x14c` | `0x1419e5900` (also reads property `0x2b07`) | `0x1434b9af0` |
| `0x35a` | `0x1419e59b0` | `0x1434c2350` |

Only when all three are 0 (or the flag is set) does it allocate `0x340` bytes (`0x142c8cb50`) and run the BeamSweep base
constructor `0x141d1ac90`.

The flag is the one the factory passes (0 on the `0x1ab` path, `0x141ef0050`). The three alternate classes are
**shared**: they take no `HASH16` and belong to no action type; the three property ids are not resolved. **Whether a Sirus actor
answers 0 to all three is assumed here, not proved** — if it does not, the action is one of those three shared classes
and the `Orion*` class contributes only the preload. That is the first question under [Still open](#still-open).

### What each class declares (its collector)

The collectors fill a resource list in three buckets — animated effects (`0x141e52b30`, `MiscAnimated.HASH32`), objects
(`0x141e53330`, `MiscObjects.ObjectHASH32`), metadata paths (`0x141e53420`) — plus `GroundEffectTypes` rows
(`0x141e544a0`, which is why that function carries the string `Data/GroundEffectTypes.dat`). Every `Orion*` collector
(except BeamSweep's) starts with the shared `0x1419e6d50`, which adds the *actor's own* effect components.

**`OrionBeamSweep`** — `0x141416f90`: it calls `0x1419e6d50`, reads actor property `0x15d`, then adds three
`MiscAnimated` hashes (key lookup `0x14228bed0`) and two 0x30-byte table rows, `0xe3` and `0xe4` (see below):

| `MiscAnimated` | `HASH32` | file |
|---|---|---|
| 3619 `AtlasExileOrionNukeBeamImpact` | `0xb2637bb2` | `.../Orion/nuke_beam_blast.ao` |
| 3679 `AtlasExileOrionPermanentGroundDamage` | `0xce94536b` | `.../Orion/destruction_assets/damage_NukeBeam.ao` |
| 3699 `OrionGlobalHitEffects` | `0x1fbc41d` | `.../Orion/Hit_FX.ao` |

**`OrionMeteor`** — `0x14141a240` (ground-effect type `17` = `OrionMeteor`, then five effects):

| `MiscAnimated` | `HASH32` | file |
|---|---|---|
| 3622 `AtlasExileOrionMeteorRain` | `0x7f254406` | `.../destruction_assets/meteors/Meteor_Rain.ao` |
| 3623 `AtlasExileOrionMeteorImpact` | `0xa56ea289` | `.../destruction_assets/meteors/impact_Large.ao` |
| 3714 `AtlasExileOrionMeteorPermaDamage1` | `0x6c79bae5` | `.../destruction_assets/damage_MeteorRain_01.ao` |
| 3715 `AtlasExileOrionMeteorPermaDamage2` | `0xcd4614d1` | `.../destruction_assets/damage_MeteorRain_02.ao` |
| 3699 `OrionGlobalHitEffects` | `0x1fbc41d` | `.../Orion/Hit_FX.ao` |

**`OrionCorridorBlast`** — `0x141419ce0`. Two objects by hash, then the projectile metadata path, then the impact:

| kind | id | asset |
|---|---|---|
| `MiscObjects` 352 `AtlasExileOrionBlocking1` | `0x6ff62d8a` | `OrionArenaObjects/OrionBlockingWall1` |
| `MiscObjects` 353 `AtlasExileOrionBlocking2` | `0x837d318` | `OrionArenaObjects/OrionBlockingWall2` |
| metadata | — | `Metadata/Projectiles/AtlasOrionCorridorBlast` |
| `MiscAnimated` 3814 `AtlasExileOrionCorridorProjectileExplode` | `0xa2ce450a` | `.../Orion/maze_meteor_impact.ao` |
| `MiscAnimated` 3699 `OrionGlobalHitEffects` | `0x1fbc41d` | `.../Orion/Hit_FX.ao` |

**`OrionCircleMazeBlast`** — `0x141d1c130` (nine animated effects, four in a loop, plus two objects):

| `MiscAnimated` | `HASH32` | file |
|---|---|---|
| 3779 `AtlasExileOrionMazeMeteor1Ring` | `0x5c9ae98e` | `.../Orion/meteor/meteor_100.ao` |
| 3780 `AtlasExileOrionMazeMeteor2Rings` | `0xf7e94e3b` | `.../Orion/meteor/meteor_150.ao` |
| 3781 `AtlasExileOrionMazeMeteor3Rings` | `0x798ae0d7` | `.../Orion/meteor/meteor_200.ao` |
| 3690 `AtlasExileOrionMazeMeteor` | `0x29d35ddc` | `.../Orion/maze_meteor.ao` |
| 3691 `AtlasExileOrionMazeMeteorImpact` | `0x5f3a578b` | `.../Orion/maze_meteor_impact.ao` |
| 3734 `AtlasExileOrionCircleMazeMeteorMarker` | `0xa0b5ffea` | `.../Orion/maze_meteor_incoming.ao` |
| 3777 `AtlasExileOrionCircleMazeTeleportFrom` | `0xdc4df815` | `.../Orion/warp_down.ao` |
| 3778 `AtlasExileOrionCircleMazeTeleportTo` | `0x62f1ece9` | `.../Orion/maze_warp.ao` |
| 3733 `AtlasExileOrionCircleMazeGateway` | `0xb845dc4d` | `.../Orion/ExitHere.ao` |
| `MiscObjects` 354/355 `AtlasExileOrionBlockingExplode1/2` | `0xc993552` / `0x70abfbd` | `OrionBlockingWallExpode1/2` |

**`OrionBeamBomb`** — `0x1414151c0`, the only collector that names object **types** by path, and the only one for an
action type that has no base class. It adds `GroundEffectTypes` 17 (`OrionMeteor`), reads actor property `0x1921`, and
names the two object types by their full metadata paths (66 and 61 characters):

| kind | id | asset |
|---|---|---|
| object type | — | `Metadata/Monsters/AtlasExiles/OrionArenaObjects/OrionBeamBombClone` |
| object type | — | `Metadata/Monsters/AtlasExiles/OrionArenaObjects/OrionBeamBomb` |
| `GroundEffectTypes` 17 `OrionMeteor` | — | buffs 1449 / 232 (the meteor's ground) |
| `MiscAnimated` 3774 `AtlasExileOrionBeamBombExplosion` | `0xd12f4149` | `.../Orion/maze_meteor_impact.ao` |
| `MiscAnimated` 3699 `OrionGlobalHitEffects` | `0x1fbc41d` | `.../Orion/Hit_FX.ao` |

Two entries per sweep and per bomb are row indices into a table of 0x30-byte rows (`0x14230aa10`, `0xe3`/`0xe4` and
`0xef`/`0xf0`, pushed with `0x141e52fb0` rather than `0x141e52b30`); that table is not identified here.

## Running: what the class adds

The shared action does the state machine; the `Orion*` classes add the effect handles and the per-frame work. On
`OrionBeamSweep`'s client vtable `0x14337c8e0` the slots it overrides are:

- **slot 4, `0x141419830`** — releases the three refcounted handles at `+0x340`, `+0x348`, `+0x350`.
- **slot 36, `0x141417120`** — the per-frame update, `(this, float dt)`. While the action's state (`+0x318`) is 1 it
  adds `dt` to the timer at `+0x360`, builds an effect (`0x141e643b0`) from the effect id at `+0x358` on the actor's
  owner (`+0xe8`), attaches it as two child effects (`0x141e671e0`), starts it (its vtable `+0xc0`) and sets bit 4 of a
  flag byte at `+0x8d` of the effect's second word.
- **slot 138, `0x141416e20`** — the copy constructor: keeps `+0x340`/`+0x348`/`+0x350` and copies
  `+0x358`/`+0x35c`/`+0x360`, so a cloned action carries the same effect ids.
- slots 87, 153, 158–162, 166, 193, 197 (`0x141417ec0`, `0x1414174f0`, `0x141419890`, `0x141419090`, `0x141418e50`,
  `0x141419510`, `0x14141bb00`, `0x141d20380`, `0x141d203b0`) are the remaining overrides; `0x141418e50` is a
  two-branch effect teardown.

None of them names an animation keyword, and none of them reads a position from an action packet: the class is handed
the actor and the skill instance and does the rest from data.

## The two registration passes (and the two functions that do not decompile)

Registrations are done by 2 156 tiny stubs, each one `register(name, ctor, collector, slots)`, called from two very
large functions. `0x1400fed40` is the `[STARTUP] Registration` step: it logs that name (`0x140144010`) and then calls
`0x14194c880`, `0x14124b4e0` and `0x141e0a9d0`, in this order.

| | `0x14194c880` | `0x14124b4e0` |
|---|---|---|
| instructions | 25 798 | 18 931 |
| stubs called | 1 131 | 1 025 |
| distinct names | 1 125 | 1 021 |
| names in both | 834 | |
| names only here | 291 | 187 |
| stub block | `0x14196e930`–`0x14197bd10` | `0x141264d30`–`0x141270d30` |
| `Orion*` names registered | four (`BeamSweep`, `Meteor`, `CorridorBlast`, `CircleMazeBlast`) | five (all, incl. `BeamBomb`) |

Neither function decompiles with less than ~6 GB of free commit (`NightRetry`'s memory guard stops it); on a machine
with 8 GB free, `NightSplit … 14194c880` is the command that would produce it. They do not need to decompile: a
census of every instruction settles what they are. All 1 131 and 1 025 call sites go to stubs in the two contiguous blocks above,
each wrapped in the MSVC thread-safe static pair (`0x142c8ad48` / `0x142c8acdc`, 1 136 and 1 025 times); the rest is a
handful of helpers (56 calls to `0x141940aa0`, 11 to `0x142c8cac0`, 10 and 6 to `0x140dfa000`/`0x140df9ed0`, and one
action construction through `0x1419e5460` in the first pass) plus the map allocation. They are flat lists of
registrations, 25 798 and 18 931 instructions of "call this stub once", and the stub list (2 156 rows of
`(stub, name, ctor, collector, slots)`) *is* their content.

**Both lists register with `slots = 0`**, so both write slot 0 *and* slot 1, and the second pass therefore overwrites
the first for the 834 names they share. The five `Orion*` names are in both passes except `OrionBeamBomb`, which only
the second has, so the classes in the table above (the second pass's) are the ones a lookup returns. The first pass's
four `Orion*` classes are the same shape one level down — its constructors build the same object and stop at the base
vtable (`0x143587090` etc.), and its collectors are the shared `0x1419e6d50` alone — so it is a base layer that the
client classes override. A live check would settle it (below).

## What the client does not decide

- **The clip.** No `Orion*` class carries an animation keyword, and none of the five action types is in a `.act` — the
  body animation is still the granted effect's `Animation` row, exactly as [sirus.md](sirus.md) §*The clips* says. The
  action type picks the **effect art**, not the animation.
- **Damage, cost, cooldown, range, targets.** Nothing in these classes computes damage; the `base_skill_effect_duration`,
  the stat set and the area come from the rows in `data/client.db`, and the hit resolution is the server's.
- **When.** The classes react to the action they are given; only the server decides that a beam sweep starts, on whom,
  and how often (the Sirus module's `EffectOf` skill base).
- **Where the spawns go.** The BeamBomb's `OrionBeamBomb`/`OrionBeamBombClone` object types are created by the client
  only as preloaded assets; the world objects that deal damage are spawned and owned by the server, and their positions
  come from the server's spawns, not from these classes.
- **What the client does decide, and the server must not fight:** the arena walls' and bombs' *art*, the maze's rings
  and teleport effects, the meteor rain and its perma-damage ground, and the sweep's nuke-beam ground. Changing the
  effect rows above is the only way to change them; there is no packet for it.

## Still open

Two questions this page cannot close statically, both cheap to observe in a live trace:

1. **Which actor-property branch the constructor takes.** Hook `0x1419473e0`, and for a skill whose instance `+0x42` is
   `0x4a62` (BeamSweep) log which constructor it returns — `0x14127ee70` (second pass, expected) or `0x141985ce0`
   (first pass) — and then hook the returned constructor's first instruction to see whether it is one of the three
   alternate classes (`0x1419e5860`, `0x1419e5900`, `0x1419e59b0`). Simpler, and it answers both the pass question and
   the property-branch question at once: hook `0x141ef0050` and log the vtable of the created action
   (`0x14337c8e0` if the `Orion*` class runs, `0x1434c2db8` / `0x1434b9af0` / `0x1434c2350` if an alternate does).
2. **What the actor is animated with** while one of these actions runs — log the animation keyword the actor's
   `AnimationController` is given during a sweep. That would confirm the body clip still comes from
   `GrantedEffects.Animation` for an action type that no `.act` names.

## Sources

- the code: the client in the repository's Ghidra project ([GettingStarted](../../GettingStarted.md)), scripts
  `scripts/re/ghidra/{Decomp,Callers,NightRetry,NightSplit}.java`;
- tables: `data/client.db` — `ActionTypes` (`HASH16`), `MiscAnimated` (`HASH32`), `MiscObjects` (`ObjectHASH32`),
  `GroundEffectTypes` 17, `BuffDefinitions` 1449/232;
- the data-side companion: [sirus.md](sirus.md) (the kit, the arena, the clips and their contacts);
  [tooling](../re/tooling.md) §9 for the two functions that do not decompile and §7 for the address rule.
