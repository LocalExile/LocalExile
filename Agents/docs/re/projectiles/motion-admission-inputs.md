# Motion state and the Chain half-plane producer

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

This supplements [ordered Chain candidate admission](chain-candidate-admission.md) for the pinned 3.29.3b client.
The candidate predicate is already recovered; this chapter follows concrete producers of two of its inputs.
These are original-byte/data-flow findings, not observations from a running client.

## 1. The direction pair has a concrete special-contact writer

Positioned's captured functor table `0x14352d058` reaches invoke `0x141bbbf00`.
Its complete 12-byte leaf dereferences the incoming Positioned pointer, advances the captured object by eight bytes,
then tail-jumps to `0x141bad450`.
The callback retains the captured action-pointer address in R13 and the contacted Positioned in R15.
This binding distinguishes an actual projectile-action writer from an unrelated use of offset `+0x3c4`.

The function contains several special-object branches, including a separately identifiable Fire Wall path:
its original literals are `firewall_action_id`, `firewall_detach_id` and `parent_gameobject_id`.
The direction writer is in a later branch; those Fire Wall names do not name that later branch.
The later path requires:

1. A nonzero first integer from the contacted Entity's type-8 property `0xff0cc69a`.
2. Current action DWORD `+0x3c0 == 0`.
3. Equal low 15 bits of owner/contacted Positioned word `+0x1e0`.
4. Successful resolution using key `0x40f0fc9c`, followed by its Actor action list.
5. A retained action with type word `0x5cdb`, active byte `+0x11e != 0` and sequence word `+0x11a`
   equal to the earlier property value.

The property-lookup fallback chain is visible at `0x141badab5..0x141badc00`.
The [KineticBarrier contact bridge](kinetic-wall-contact.md) now binds type `5cdb` through its original
registration/final client table and identifies `40f0fc9c` as the UTF-16 `owner` hash.
The first hash's name, actual publication of both inputs and full provider semantics remain open.
This is a concrete special-object contact path, not proof that every terrain collision activates this constraint.

## 2. Geometry chooses the contacted object's projectile-facing side

At `0x141badcad..0x141badcdf`, the callback obtains the flying Entity from action `+0x328` and computes:

```text
D = flying.Positioned.gridXY - contacted.Positioned.gridXY
```

The integer subtraction wraps at 32 bits.
The existing length helper `0x14015d1e0` performs its wrapping squared-sum precheck.
If that length is zero, the callback skips the direction write entirely, preserving the previous pair and flag.
This includes the ordinary same-grid case and arithmetic overflow witnesses; actual extreme-coordinate reachability
is not established.

For an ordinary nonzero displacement, `0x141badd07..0x141badd29` builds `(0.0f,-1.0f)` and rotates it
by the contacted Positioned's float rotation `+0x29c`, using `0x140df8f90`.
That helper calls the [separate sine/cosine implementation](random-inputs.md#66-variance-sincos-finite-stat-witnesses-and-geometry-limits)
`0x140e2acd0`, then performs independent scalar float32 multiplications/addition/subtraction.
For supplied sine `s` and cosine `c`, its exact outer rotation is:

```text
rotated.x = f32(f32(input.x*c) - f32(input.y*s))
rotated.y = f32(f32(input.x*s) + f32(input.y*c))
```

The resulting pair is written to action `+0x3c4/+0x3c8` at `0x141badd3a`.
The callback sets `byte3cc=1` only if previously zero; a preexisting nonzero flag is preserved.
It then normalizes the saved integer displacement using the same vector helpers as candidate admission,
and dots that unit displacement with the newly written pair.
If the dot is negative, it multiplies both stored components by the original float constant `-1.0f`.
Dot zero leaves the orientation unchanged.

Thus for ordinary finite inputs the stored normal faces the side containing the flying projectile.
The later Chain predicate rejects candidate displacements on the opposite side.
It does not use the projectile's velocity as a generic forward-facing direction.
The producer's displacement is relative to the special object; the later predicate receives displacement
relative to the Chain search center. Do not silently substitute one origin for the other.

The write occurs **before** calling ordinary contact method `0x141ba7b30` at `0x141baddb4`.
If that method returns true, the caller writes `mode3c0=1` and invokes the located action's virtual `+0x4f8`
with the projectile action, contacted Entity and action `+0x130` address.
A false return does not undo the preceding direction write in this callback.
Consequently a successful final contact is not a prerequisite for this local state mutation.
The downstream callback may perform additional mutations and is not treated as inert.

Checked construction/copy paths initialize `byte3cc` to zero.
The existing no-selected-Chain-target terminal path changes mode 1 to mode 2, then clears the direction flag.
These are specific lifecycle edges, not an alias-complete census of every possible writer.

## 3. Positioned selected bytes have base and movement-specific sources

The constructor `0x141e2e960` binds the metadata pointer at Positioned `+0x158`.
At `0x141e2eb18..0x141e2eb2d`, metadata byte `+0x24` initializes **both** Positioned `+0x2c0` and `+0x2c1`.
This supplies a base byte and a currently selected movement byte.
[Movement configuration inputs](motion-configuration-inputs.md) subsequently binds the metadata parser:
`collision_hull_type = "walk"` writes 0 and `"projectile"` writes 1. This is a collision-layer choice, not height.

Straight-move setter `0x141e34400` receives a specification in RDX.
In its nontrivial-movement branch, `0x141e34785..0x141e347ab` does:

```text
Positioned.byte2c1 = spec.byte90 if spec.byte91 != 0 else Positioned.byte2c0
Positioned.byte2c2 = spec.byte92
```

Its earlier virtual movement-configuration call selects the same override/base byte at
`0x141e344e5..0x141e34513`.
Arc setter `0x141e34a70` instead writes its seventh and eighth ABI arguments to `+0x2c1/+0x2c2`
at `0x141e34e10..0x141e34e26`.
After five pushes and `sub rsp,0xf0`, these reads at current `rsp+0x150/+0x158` are entry `rsp+0x38/+0x40`.
They are supplied arguments, not unspecified uninitialized stack bytes.
The checked ordinary finalizer supplies base `+0x2c0`, while shared redirect supplies 1;
the [concrete caller/specification chain](motion-configuration-inputs.md#2-initial-placement-and-redirect-supply-different-inputs)
preserves that input into both movement setters with `+0x2c2=0`. The [object-type binding](object-type-binding.md) now connects the checked common metadata route;
final overlays, resource lifetime and other callers remain open.

The checked frame-completion/blocked/reset paths restore `+0x2c1` from `+0x2c0`.
So do the straight/arc zero-time reset branches and explicit stop helper `0x141e35250`.
This restoration can occur before subsequent notifications or callbacks.
Later callbacks can configure another movement; the restore is not an immutability guarantee.

## 4. The selector's sign bit has a concrete suppression transition

Recall the candidate rule:

```text
selected(P) = P.byte2c0 if P.byte1e6 has bit 7 else P.byte2c1
reject only selected(source)==0 && selected(target)==1
```

Helper `0x141e350e0` first clears/moves away both motion function objects.
When `stepTime240 != 0` and `byte1e6` is not already negative, it optionally dispatches virtual `+0xe0(0)`
under `byte2c2 & 1`, sets `byte1e6 |= 0x80`, clears the one-byte `+0x2c2` field and `+0x2d4`,
then performs the checked notification path.
The set occurs at `0x141e351c3`.
It leaves `+0x2c1` in place: candidate admission uses the base `+0x2c0` because the sign bit changed.

Straight/arc new-movement paths clear bit 7, and one frame stop branch clears it after restoring the base byte.
Explicit stop `0x141e35250` instead restores the base byte and zeros both motion step times.
These transitions establish why using only `+0x2c1`, or assuming the selector is a permanent object category,
can disagree with the recovered predicate.
The named parser and common finalizer/redirect specification values are now connected in
[movement configuration inputs](motion-configuration-inputs.md). Remaining indirect callers/writers,
final resource overlays/lifetime and encountered values remain open;
the checked common type/metadata route is bound in [object-type binding](object-type-binding.md).

## 5. Evidence and verification limits

Ignored round38 `extract_inputs.py`, `typed-motion-inputs.asm/json` and `input-literals.json` bind the
original PE ranges, callback leaf and literal bytes.
`discover_inputs.py` / `input-reference-discovery.json` are navigation indexes over explicit displacements;
they do not establish field types, alias completeness or absence of additional writers.
One additional Positioned owner contained undecoded bytes in a chained range and was excluded from the complete-body
audit; its offset matches are not promoted to conclusions here.

`check_direction.py` interprets the original nonzero/zero direction subrange, four original vector helpers
and the rotation helper against separate finite equations: 240 controls, including initial nonzero flags,
both sides, the perpendicular boundary, same-grid and wrapping-sum zero cases.
Sine/cosine **outputs are supplied fixtures**, not a claim of arbitrary-angle native-trig parity.
Surrounding lookup/type gates, Positioned transitions and reset callers are static data-flow checks,
not part of that interpreter's execution count.
No native execution, fresh traffic, independent worker review, server change or CI follows from this audit.
