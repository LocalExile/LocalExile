# Sirus in the client (Orion)

This folder is the reverse-engineered reference for **Sirus, Awakener of Worlds** (`AtlasExile5`, "Eye of the
Storm") in the Path of Exile 3.29.3c client: what each of his skills is made of in the client's own data, what the
client draws by itself, what only the server can produce, and what is still unknown.

The two pages beside this folder are the summary:

- [../sirus.md](../sirus.md) — the data side: the effect rows, the arena, the clips, the storms.
- [../sirus-client.md](../sirus-client.md) — the code side: the client classes behind the `Orion*` action types.

This folder goes one skill at a time: one page per skill, each with the class card, the state machine, the
serialisation, the hit test and what the server should send. Everything here is English. The evidence markers are
the repository's:

- **observed** — read straight out of the client's code or data, or seen in a live client.
- **inferred** — follows from the context, without direct evidence.
- **measured** — ran on this server, with a log line or a frame.

## How the reference was produced

The classes were read with a disassembler (Ghidra's read-only project works: [tooling](../../re/tooling.md)) and the
tables from `data/client.db`. Nothing is written back to the client.

The per-skill pages keep the class cards (hash, factory, constructor, vtable, own slots), the state machines, the
serialisation field by field, and the hit tests, because that is what a server implementation has to match.

## The six ways a Sirus visual can appear

Almost every "the beam looks wrong" question lands in one of these six.

1. **`ServerBeamEffect`** — a server-sent entity that stretches one `MiscBeams` row's art between two points.
   Serialisation: `src/GameServer.Core/Protocol/Instance/InstanceEntities.cs` (`BeamEffectObject`); the client parses
   it in `0x1416710c0` (the `Beam` component, vtable slot 20). The near end takes its height **only** from a bone
   (`0x97` = no bone, `0xD7` = bone name), so a beam without a bone is drawn into the ground. The art's width is
   fixed (`MiscBeams.__col2`), so the distance between the ends decides whether it reads as a thin line or a wall.
2. **`.ao` attachments and client events** — the client plays the clip and draws its particles, trails and sounds.
3. **The action class builds its own Effect** — `GeometrySpell` (`HASH16 52470`, the class behind the ordinary beam
   7008/7012) creates a 584-byte `Metadata/Effects/Effect` on the caster and animates it; the art comes from the
   ActionData attribute `0x7A39632A`, or from two resource strings and two u32 in the packet. Our `0x1ab`
   (`InstanceProtocol.ActionStarted`) carries neither, so the client takes the `flag == 0` path and reads its own
   skill data.
4. **The official art lives in `MiscAnimated`** — that table is the list of art by name, and the client's action
   classes pick a row by its `HASH32`.
5. **The server can place an official `.ao` itself** — `EffectObject`
   (`Metadata/Effects/PermanentServerEffect`) takes a `Parent`, a `Bone`, `AttachKind` and `AttachFlags`, so an
   effect can hang on a caster's hand bone (the way Arrow Nova's arrow does).
6. **Most action classes draw nothing** — they compute endpoints and leave the art to the server.

### The chain that picks the official art (observed)

A geometry action does not name its art. It looks a row up:

```
GrantedEffects.SkillID  →  stat 6433 (skill_behaviour_variation)  →  GeometryAttack row (by Id)
                          →  GeometryAttack.Animation (a MiscAnimated row)  →  MiscAnimated.AOFile
```

The server already sends that stat: `SkillCatalog.BehaviourVariation` (`src/GameServer.Gateway/Skill/SkillCatalog.cs`)
reads the `SkillID` column, `Monsters.WithBehaviourVariation` adds it to the skill record, and `Sirus.Native.cs`
builds its skills with it. So the client can find the official art by itself — but the shape's length is not a
network field: the client takes it from the shape table at the **target cell**, and without a target cell the beam
has length zero and only the muzzle ring is drawn.

`Data/GeometryAttack.dat` holds 1360 rows of 305 bytes plus a variable section, and contains no strings at all; the
`Animation` and `HitEffect` columns are `MiscAnimated` row numbers.

## The official art for Sirus (observed)

`MiscAnimated` rows (1-based), the rows Sirus' geometry and effects use:

| Row | Name | Art |
|---|---|---|
| 3622 | `AtlasExileOrionMeteorRain` | `.../Orion/destruction_assets/meteors/Meteor_Rain.ao` (the small meteor, 1.92 s) |
| 3690 | `AtlasExileOrionMazeMeteor` | `.../Orion/maze_meteor.ao` (the big meteor) |
| 3691 | `AtlasExileOrionMazeMeteorImpact` | `.../Orion/maze_meteor_impact.ao` (the corridor orb's burst) |
| 3692 | `AtlasExileOrionCorridorNuke` | `.../Orion/corridor_slash_01.ao` (the corridor's slash) |
| 3701 | `OrionBeamHitEffects` | `.../Orion/Hit_FX_beam.ao` |
| 3623 | `AtlasExileOrionMeteorImpact` | `.../Orion/destruction_assets/meteors/impact_Large.ao` |
| 3734 | `AtlasExileOrionCircleMazeMeteorMarker` | `.../Orion/maze_meteor_incoming.ao` (the warning circle) |
| 3779-3781 | `AtlasExileOrionMazeMeteor1Ring/2Rings/3Rings` | `.../Orion/meteor/meteor_100.150.200.ao` (the maze's ring walls) |

And the geometry rows that select them:

| `GeometryAttack` Id | `GrantedEffects` row | Art |
|---|---|---|
| 332 | 7008 (ordinary beam), 7012 (final) | `Orion/spam_beam.ao`, `Orion/spam_beam_final.ao` (23 cells long, 1 cell wide) |
| 342-344 | 7019-7021 (physical) | `Orion/TripleBeam/triple_beam_01.02.03.ao` |
| 345-347 / 348-350 / 351-353 | 7022-7024 / 7025-7027 / 7028-7030 (cold / fire / lightning) | the same three, with `_ice` / `_fire` / `_lightning` |
| 359 | 7031 (the clone's beam) | `Orion/clone_trap_beam.ao` |

`spam_beam.ao` and `triple_beam_0N.ao` share a rig (`impact_beams/rig.amd`) and a mesh (`impact_beams/spambeam.sm`);
they differ in material, default animation and particles. Both carry the bone groups `beam_length` (`jntBnd_beam` →
`aux_beam`) and `beam_flash` (`jntBnd_beam` → `aux_circle2`), so the length and the muzzle flash are client-driven.

## The per-skill pages

| Page | Skill |
|---|---|
| [body-beam.md](body-beam.md) | the ordinary beam (7008/7012) and the geometry chain above |
| [body-barrage.md](body-barrage.md) | the multi-beam barrage (8098/8099) and the clone beam (7031) |
| [apparition-laser.md](apparition-laser.md) | the apparitions and their beams |
| [clones.md](clones.md) | the clones and their beams |
| [corridor.md](corridor.md) | the corridor "Die" (10948) |
| [maze.md](maze.md) | the circle-maze sweep (10949-10953) |
| [meteor.md](meteor.md) | the meteor and the star rain |
| [beam-bomb.md](beam-bomb.md) | the beam bomb (10973/10974) |
| [storm.md](storm.md) | the death zones and the shockwaves |

Each page ends with **What the server should send**: the messages, the effect entities and the hit shapes that make
the skill behave like it does on the official server.

## Scope

The client findings, the tables, the class cards and the hit shapes are what this folder is for; the repository's
own code is the reference for what the server does today.

## Still open

- The beam's endpoints: the geometry channel rides in the S→C `0x2bc` (`ComponentUpdate`) packet's actor-component
  action packet; the exact bytes and the `actionId` source are not confirmed.
- `beam_length`'s writer: the bone group's length comes from an animated-object field, and the writer is not found.
- The maze's centre: the client looks the centre up in the GEAL globals under the name `apparition`, which only a
  script can fill.
- The `Skills` table the client uses for its action data is not in our extracted tables at all.
