# KineticBarrier identity and the projectile contact bridge

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [012 — projectile execution](../012-projectile-execution.md), for the pinned 3.29.3b executable.
This follows the concrete direction writer in [motion admission inputs](motion-admission-inputs.md).
Original bytes establish the registration, final-class callback and local state transfer below.
Actual object publication/encounters and numerical damage authority remain separate questions.

## 1. Type 5cdb has a concrete registration and final client class

The original ASCII literal at `0x14325cbe0` is `KineticBarrier`.
Registration helper `0x141941bb0` starts its accumulator at zero, multiplies by `0x01000193`,
truncates the product to 32 bits, then XORs each sign-extended input byte.
For this ASCII name, its DWORD hash is `0x10e94c32`; XOR-folding high/low words gives `0x5cdb`.
This is an action-type hash, not the property-name hash in section 3.

Both concrete registration callers pass that literal, mode zero and shared resource factory `0x14126f190`:

| Registration | Concrete action factory | Final primary table | Consumed slot `+4f8` |
|---|---|---|---|
| `0x141977090` | `0x1419781f0` | `0x143317600` | `0x1400f0f00`, complete `ret 0` leaf |
| `0x14126e5b0` | `0x141270c40` | `0x1433194b8` | `0x1413a62b0`, Kinetic Wall cache adapter |

Mode zero writes both registry discriminators 0 and 1 with the same factory/resource pair.
The base initializer calls the first registration at `0x1419482bb` and the client initializer calls
the second at `0x1412497e3`, each under its own original static-initialization guard.
The checked startup sequence calls the base initializer and then the client initializer without an
intervening branch, as already recovered for [Fireball's startup selection](fireball-and-kinetic-blast.md#1-registry-and-concrete-factories).
For that successful guarded startup pass, the later client registration replaces the same keyed records.
This does not exhaust all later registry writers or establish an observed runtime installation history.
The alternate table is present in the client binary; its existence does not identify a separate official server.

The ordinary branches of both factories allocate `0x318`, pass type word `0x5cdb` into `0x1419e7100`,
then replace the primary table **after** that shared constructor returns.
That constructor retains the type argument across its selected-stat read and forwards it to the base constructor;
it also initializes byte `+310` to zero.
Mine/totem/trap wrapper branches precede ordinary allocation, so these final tables are not universal proxy bindings.
The shared resource factory is a caller-bound five-byte tail-jump to `0x1419e4340`; this alone does not link
every similarly named packaged object to this action.

## 2. Successful contact reaches the previously recovered wall cache

The direction writer searches the resolved owner's Actor vector for an action with type `5cdb`,
active byte `11e != 0` and sequence `11a` equal to the supplied property integer.
It writes the oriented normal first, calls ordinary projectile contact, then on true sets projectile
mode `3c0=1` and calls the located action's `+4f8`.
Arguments include the projectile action in RDX and contacted Entity in R8.
The normal write remains even when ordinary contact returns false; the mode/callback edge does not occur then.

With the final client table above, `+4f8` is the [concrete adapter already recovered](action-initialization.md#3-these-cached-fields-describe-walls-not-continuation-counts):

```text
if projectileAction == null or contactedEntity == null: return
value = wallAction.selectedStats[5643]
projectileAction.cacheKineticWall(value)  # 141bb70a0
```

Key `5643` is `kinetic_wall_damage_+%_final_for_projectiles_chaining_off_wall`.
The receiver sets projectile byte `430` when zero and replaces DWORD `42c` regardless of its old flag.
It performs that cache write before the startup-global publication gate.
When the gate permits, native contribution key `568f`, `damage_+%_final_from_kinetic_wall`, is submitted;
an amount of zero is cached but not submitted as a nonzero contribution.
Copy/initialization and contribution consequences retain the earlier chapter's contracts.
This cut binds the **caller qualification and concrete adapter**, not a new numerical damage formula,
an authoritative hit or the complete lifetime of previously published contributions.

Two other consumed slots confirm the client-side copy bridge: `+450 -> 0x1413a61f0` allocates a copy,
calls the base copy, transfers byte `310`, and installs `0x1433194b8` again;
`+4c8 -> 0x1413a6280` calls virtual `+450`, derives the registration byte and tail-calls `0x14193daf0`.
Consecutive executable pointers beyond the selected slots are not evidence of vtable length or extra methods.

## 3. Owner lookup and the still-unidentified sequence property

The complete no-unwind leaf `0x1406db330..0x1406db3a4` hashes UTF-16 code units.
For nonnegative lengths it is the Murmur2 byte algorithm over little-endian code units,
initialized with `byteLength XOR suppliedSeed`.
With seed zero, `owner` hashes to **`0x40f0fc9c`**.
Original UTF-16 literals at `0x1431433d8` and `0x14336f5d0` corroborate the name.
Hash equality does not prove uniqueness or make every consumer a wall-specific owner relation.

In the already mapped direction callback, lookup tries a TLS property provider, the contacted Entity's
property store, a `+50` provider and virtual `+28` fallback before converting the selected result.
These ordered fallback sources remain distinct; a local store cannot simply replace them all.
Selected native callers at `0x14165f6ac`, `0x141a4c355` and `0x141e7d01e` also pass this key to
`0x141de8600`, preparing a pointer payload and byte tag 1; `0x141e7d073` reaches the removal helper
with the same key on its null-input branch.
These are concrete native property operations, **not yet the actual KineticBarrier object's publisher**.
Their complete caller/object classification and transitive property notifications remain open.

The other key, **`0xff0cc69a`**, remains unnamed.
Helper `0x1402bd370` requests dynamic type 8, accepts a first record tagged 8, copies each record's DWORD
in `0x28`-byte strides into an integer vector, and rejects an empty/wrong-first-type result.
A later wrong tag reaches the diagnostic path rather than silently converting it.
The contact callback uses the first integer, requires it to be nonzero and compares it to the retained
KineticBarrier action sequence; it is not just a generic boolean collision predicate.

Missing/zero input, or a nonzero current `3c0`, instead reaches a different checked branch reading stat
`3a86`, **`is_tornado_owner_id`**, with further component/owner tests.
That alternative does not create the normal or invoke this wall callback here.
It does not by itself identify Tornado Shot, prove a gameplay skill interaction or close the alternative's
component/continuation semantics. Do not fold it into the wall branch merely because both live in one function.

## 4. Verification and remaining inputs

Ignored round39 `audit_kinetic.py` / `kinetic-contact-audit.asm/json` seal thirteen complete unwind bodies,
seven caller subranges and three explicit caller-bound leaves: 23 ranges, 1657 decoded instructions,
four consumed slots, seven RIP bindings and original literals.
`check_hashes.py` compares interpreted original hash instructions with independent byte equations:
466 controls, 77,636 steps and 61 visited sites. UTF-16 lengths are nonnegative and action names ASCII;
the extra named-owner check is not included in those counts.
These are same-agent offline checks, not native execution or independent worker review.

Earlier immediate/string scans are navigation only; unmatched ASCII candidates cannot prove that a
property name or writer is absent. The packaged KineticBarrier object was read offline, but its filename
is not promoted to a native action/resource binding and no extracted game asset is committed.
Actual publication of the sequence property and owner reference, all alias writers, contact encounter order,
remaining movement-spec values and C/S routing stay open.
Reuse the damage thread's fixed resource/Stats findings after this boundary; do not repeat its numerical audit.
No server implementation, client session, new traffic or CI is part of these findings.
