# Projectile row to object type and components

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [012 — projectile execution](../012-projectile-execution.md), for the pinned 3.29.3b executable.
This closes the resource/constructor connection left by [movement configuration inputs](motion-configuration-inputs.md).
Addresses and transfers below are original-byte evidence; configuration contents are read-only archive witnesses.

## 1. The selected row supplies the type lookup name

Shared emission `0x141bb13a0` reads the selected Projectiles row at action `+0x358` at `0x141bb15cd`.
It reads the row's first UTF-16 pointer, measures its null-terminated length, and constructs two views of that name.
At `0x141bb160f`, it calls `0x1401b4500` with the shared resource manager and these views.
The returned retained handle is saved at local `[rbp-0x48]` at `0x141bb1614`.
Thus the selected flight row supplies an object-type name, in addition to the speed/art fields already mapped in
[resource selection](resource-selection.md). It is not merely a visual label.

`0x1401b4500` hashes the UTF-16 byte span with `0x140df8600`, seed `0xde59dbf86f8bd67c`,
and selects one of 16 locked cache buckets. An existing entry reuses its retained resource;
the miss path calls resource factory `0x1401bbd60` at `0x1401b4615`.
That factory supplies payload constructor `0x1401d55f0`, destructor `0x1401d5690` and payload size `0x2b0`.
The constructor copies the name and calls object-type constructor `0x141ec1360` at `0x1401d563e`.
The destructor callback is not an alternative object-type producer.

Both ordinary emission forms retain this binding:

| Emission path | Transfer into Entity construction |
|---|---|
| Single object | `0x141bb1993` passes the address of `[rbp-0x48]` to `0x141ebdf30` at `0x141bb199a` |
| Multiple objects | emission context `+0x18` retains the handle reference; `0x141bb261b → 0x141939710` forwards it |
| Multi-object helper | `0x141939710` allocates `0x248`, preserves that reference, and calls `0x141ebdf30` at `0x141939761` |

The last constructor receives the owner's World, the separately selected seed and argument 5 equal to 1.
Single/multiple creation subsequently install the client Entity tables.
The seed-selection branch and type `+0x2a4` remain separate from the type-name lookup;
this connection does not make all creation seeds interchangeable.

## 2. The generated register supplies a fallback inheritance source

`0x141ec1360` requests the exact resource `Metadata/GameObjectRegister` at `0x141ec13f4`.
This is the same generated register documented in [player object types](../../protocol/04-player-entity.md#1-object-types-need-no-registration-message),
not a separate file or a new network registration message.

Its resource factory `0x141ee54b0` supplies constructor callback `0x141ee6620`;
that callback calls `0x141ec18c0` at `0x141ee6669`.
The previously unnamed `0xea`-byte registration loop is **Projectiles.dat**:

```text
register +78 producer 141ec24e0
    -> 14126ef80: exact literal Data/Projectiles.dat at 14325be60
    -> existing Projectiles loader/relocation chain

141ec1d50..141ec1d6a:
    register(row.Id, row[+43], empty extra-parent vector)
141ec1db0:
    next row += ea
```

Native `row+0x43` is the relocated `InheritsFrom` pointer, not a radius or component index.
The prior `0xac` loop belongs to another producer and must not be relabelled Projectiles.
Registrar `0x1424f7520` uses the existing narrowed-UTF-16/u32 hash and sorted `0x30`-byte records:
key `+0`, path `+8`, base path `+0x10`, extra-parent begin/end/capacity `+0x18/+0x20/+0x28`.
The equal-key branch replaces the record's paths/extra parents; this does not establish collision-free string identity.
This registrar path registers inheritance; it does not inject per-projectile component properties.

## 3. A configured common registry creates Positioned before parsing

The base register constructor initially leaves suffix/factory pairs empty.
A concrete configuration path exists in `0x1400fe9f0`:

- `0x1400fed01` requests the same `Metadata/GameObjectRegister` resource.
- `0x1400fed1f` constructs component registry `0x141e14fe0`.
- `0x1400fed35..0x1400fed4b` loads its payload and writes suffix `"ot"` to register `+0x30`,
  and the component registry pointer to `+0x38`.

The component registry inherits table `0x14345cb08`, whose consumed slot 0 is `0x1424f54c0`.
Its constructor registers ASCII `Positioned` at `0x141e15ae9..0x141e15b12` and installs factory table
`0x14359f400` at `0x141e15b50`; that table's slot 0 is metadata factory `0x141e24b70`.
The generic name resolver dispatches the selected factory at `0x1424f5523`.
That is the factory whose metadata table and collision-hull parser were proved in the previous chapter.

Object-type construction reads register `+0x38`, requests `Positioned` through its factory at `0x141ec14a3`,
retains the resulting metadata at type `+0x298`, and appends it with `0x142474d10` at `0x141ec1511`.
Only then does it call `0x142474ae0` to parse the named type and parents.
An existing component name reuses its entry, preserving its position; missing components use the supplied factory.

`0x142474ae0` runs the common pass using register `+0x30/+0x38`, records the common component count at type `+0x80`,
then conditionally runs a separate suffix/factory pass from register `+0x40/+0x48`.
This cut establishes that conditional overlay mechanism, not every installed overlay or its mutation effects.
The concrete configuration function also contains replacement cleanup which clears an older registry's suffix/factory;
the lifetime/order of every caller is not inferred from the write alone.

## 4. Same-name source and generated parent are different branches

For the ordinary loader path, `0x142475320` builds `currentPath + "." + suffix`.
When no `@` is present and availability predicate `0x1421cef90` succeeds, it loads that source through
`0x142475a10` at `0x142475502` and skips the generated-parent fallback.
The predicate checks the resource backend and supports a configured alternate prefix;
an archive-only inventory does not reproduce its entire search environment.

Otherwise the loader hashes the unextended current path and looks up the generated registration record.
It recursively loads the record's nonempty, different base path at `0x1424756f5`, then its extra parents at
`0x1424757c7`. Equal current/base names skip self-recursion. Source `extends` clauses are processed by
`0x142476300`; exact `nothing` terminates that parent branch.

The source loader appends `.tok` to its token-resource cache lookup name and calls `0x142478e20`.
This is not proof that a physical `.ot.tok` file must exist in GGPK: backing token IO/compilation is not expanded here.
The parsed token stream reaches `0x142475bd0 → 0x142476580 → 0x142476f40`.
For a string-valued property, the last helper loads metadata virtual `+0x30` at `0x1424775a4`,
strips surrounding single/double quotes when present, constructs the UTF-16 value and calls it at `0x1424775f9`.
For Positioned this is **`0x141e2dbb0`**, the checked `collision_hull_type` parser.
Its separate `+0x18` script-property callback is not that scalar-string parser.

## 5. Default six-witness configuration

These are zero-based extracted Projectiles indices, conditional on the previously proved default selectors/variant:

| Witness | Row / type basename | Common source route in the checked archive |
|---|---|---|
| Default bow | 60 / `DefaultArrow` | same-name `.ot` extends `Projectile` |
| Default wand | 62 / `WandProjectile` | same-name `.ot` extends `Projectile` |
| Fireball | 0 / `Fireball` | generated `InheritsFrom → Projectile` |
| Kinetic Blast | 62 / `WandProjectile` | default association uses the same wand type |
| Burning Arrow | 64 / `BurningArrow` | generated `InheritsFrom → Projectile` |
| TS mother / child, bow | 108 / 114 | generated `InheritsFrom → Projectile` |
| TS mother / child, wand | 109 / 115 | generated `InheritsFrom → Projectile` |

Vaal Burning Arrow row 65 is an additional identity witness, not a seventh ordinary-skill execution claim.
All nine checked rows' `InheritsFrom` is `Metadata/Projectiles/Projectile`.
A case-insensitive inventory of the ten exact basenames and `.ot/.otc/.ot.tok/.otc.tok` forms in the
1,214,937-path archive finds only `projectile.ot`, `defaultarrow.ot` and `wandprojectile.ot`.
It does not establish absence of external resource overrides or every possible art variation.

The common Projectile configuration sets `collision_hull_type = "projectile"`; the two same-name sources extend
it without overriding that key. On this configured common route, its metadata byte is therefore 1.
This is now connected through the actual type binding, rather than inferred from a similar filename.
Final overlays, reloads, later writers and actual encountered inputs remain separate obligations.

Entity base construction `0x142427360` retains the incoming type handle at Entity `+8` and sizes its component
vector from the loaded type. `0x141ebdf30 → 0x142427510` walks that vector in order:
metadata virtual `+8` creates each runtime component, whose pointer is written to the same index.
The Positioned creator `0x141e2e790` passes that metadata to `0x141e2e960`, which copies metadata `+0x24`
to base/current collision selectors `+0x2c0/+0x2c1` as already established.
This selects a collision hull; it does not specify numerical height, prove a hit, or assign damage authority.

## 6. Verification and remaining boundary

Retained local round41 evidence seals 31 complete unwind bodies plus four caller subranges,
37 ranges / 6,796 decoded instructions, 43 fresh instruction anchors, four consumed table slots and eight RIP bindings.
The same-agent original-instruction controls cover row-name views, single/multiple constructor arguments,
registry row inputs/configuration writes, component creation order, string-parser dispatch and loader branch gates:
76 cases / 2,430 instruction steps / 156 visited sites.
Resource predicates, loaded pointers, allocator, string construction and component factory results are explicit fixtures;
the controls do not execute the whole parser or resource backend.

Raw assets and executable bytes remain private ignored evidence; shared records contain identities and findings.
No running client, new capture, server implementation or CI is involved.
The remaining work is actual configuration lifetime/overlay selection, backing token resources, remaining aliases/writers,
ballistic arithmetic and the separate unresolved C/S scheduling/result edges.
