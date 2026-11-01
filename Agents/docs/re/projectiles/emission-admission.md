# Per-arrow composition: admission, identity and variance consumption

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md). **S:** fixed 3.29.3b PE,
SHA-256 `78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5`.
This extends the earlier observation-only per-arrow boundary through the original
shared emitter **`0x141bb2480..0x141bb2ada`**, stopping at the final motion-setup
call's supplied arguments. It combines previously recovered rules; it does not
claim that re-decoding those rules discovers new mechanics.

The [layout-input chapter](layout-stat-inputs.md) supplies the selected profile
and dispatcher ABI. Layout geometry, this emitter, final placement/motion,
local hit prediction and authoritative results are distinct execution stages.
The new controls supply candidate rays/profile and do not execute a full
animation-to-layout-to-contact chain.
The subsequent [installed-motion/frame composition](motion-and-contact.md#8-joining-emission-to-installed-straight-motion-and-frame-advancement)
continues the actual movement wrappers, straight setter and integer frame samples with separately declared
world/query/listener fixtures. The observation boundaries below describe the earlier cuts.

## 1. Admission precedes identity and batch-variance consumption

The original multi-emitter has this ordering:

```text
optional side-origin terrain predicate
    blocked -> return
if profile.distanceBudget > 0:
    if sharedStop != 0: return
    if distance(candidateAim, ownerCurrentXY) > budget:
        sharedStop = 1                 // this candidate still continues
derived = allocateSharedDerivedByte(source)
seed = resourceSeedZero ? 0 : objectHash(source, derived, sourceOrdinal)
object = createProjectile(seed, resource)
clone = cloneAction(source)
registerDerived(clone, derived)
clone.sourceOrdinal = source.sourceOrdinal
clone.volleyAbsoluteSlot = machineAbs(signedSlot)
bind(clone, object)
source.sourceOrdinal += 1              // uint32 wrap
select/share/copy/create contact history
optional target and side-origin terrain-height handling
if angleHalfWidth != ±0: consume batch sample and rotate
if distanceVariance != ±0: consume next batch sample and rescale
finalizePlacementAndMotion(clone, adjustedOrigin, adjustedAim, heights, ...)
```

Actual side predicate `0x141d87160` returns nonzero for rejection at this call
site. It receives layer `1` and the explicit extra true byte. Its internal map
semantics remain a supplied oracle in the new controls.
Side rejection skips even the distance-budget check, so it cannot set the
shared stop flag. Budget zero/negative bypasses both the old-flag test and
distance comparison. Equality does not set the flag. The first over-budget
candidate still allocates, binds, advances ordinal and consumes enabled
variance; only later candidates reaching the positive-budget gate exit.

The [upstream random-direction layout](random-inputs.md#4-batch-variance-and-admission-consume-different-streams)
has already consumed its layout sample before calling this emitter. Therefore
candidate layout position and admitted-arrow ordinal are not interchangeable.
That upstream draw is prior evidence, not a newly executed part of these
candidate-supplied controls. Fan/radial do not acquire such a layout draw merely
because this emitter later applies variance.

## 2. Derived identity, pure seed hash and source ordinal

Call **`0x141bb25dc → 0x141421a10 → 0x14193dc60`** advances the source's retained
shared derived-byte allocator. The wrapper lazily creates a counter if absent;
these controls use an already-retained counter, so its allocation, ownership
and diagnostic-overflow paths are not claimed executed.

The checked ordinary counter path increments its low byte before returning it.
At low byte `255`, it first resets low to `1`, increments the auxiliary high
byte, then increments low to return `2`. Auxiliary-byte exhaustion has a
diagnostic path; it is outside the new execution controls. This arithmetic
does not resolve simultaneous coexistence/lookup of wrapped action identities.

When selected resource metadata `+2a4` is nonzero, the emitter uses seed zero
and does not call the hash. Otherwise it calls the real **`0x14193a720`** with
the newly allocated byte and the source ordinal **before its increment**.
The [object seed](random-inputs.md#2-object-seed-is-a-hash-not-a-stream-draw)
is a wrapping integer hash of sequence, owner ID, source derived byte, new
derived byte and source ordinal. It consumes no batch/layout/TLS random sample.
It must not be modeled as another item drawn from a shared RNG queue.

The original emitter copies that ordinal to the clone before binding and
increments the source only afterward at `0x141bb2722`.
It separately stores the absolute signed Volley slot at clone `+450`.
The ordinary negative slot maps to its positive magnitude; native i32
`INT_MIN` retains bits `0x80000000` through NEG/CMOVS, rather than producing
a representable positive i32. That edge is a synthetic machine-width control,
not evidence that a normal Volley group can reach such a slot.

This establishes three distinct values: layout candidate/slot, admitted source
ordinal, and derived action byte. Even if small unblocked examples happen to
make them advance together, admission and byte wrapping separate them.

## 3. Two variance gates share the batch stream

The emitter reads profile angle half-width `+10` at `0x141bb2912` and distance
variance `+14` at `0x141bb29b8`. Both compare against the zero established
earlier in XMM8; positive and negative floating zero skip their corresponding
sample. Angle is processed first. When both finite nonzero inputs apply,
distance takes the next sample from the **same batch state** passed through
the emitter descriptor. There is no per-arrow reseeding here.

Both call the real float distribution **`0x1404ce3f0`** through separate call
sites `0x141bb293c` and `0x141bb2a1a`. For the supplied ordinary `[-1,+1]`
distribution, it requests `bounded(3001)` and performs the native float32
conversion/scaling. The bounded integer sampler in the controls reuses the
previously sealed seeded reference, including its state advancement. It is
not newly executed native sampler code in this composition.

Angle multiplication, native finite sin/cos bridge and the outer f32 ray
rotation precede the distance calculation. For the finite nonnegative
distance-variance domain used by the new geometry controls:

```text
delta = f32(adjustedAim - origin)
L = f32(sqrt(f32(f32(dx*dx) + f32(dy*dy))))
base = max(1.0f, f32(L - f32(v*0.5f)))
newLength = f32(f32(f32(f32(U+1.0f)*v)*0.5f) + base)
scale = f32(newLength/L)
aim = f32(origin + f32(delta*scale))
targetHeight = f32(f32(f32(oldTargetHeight-originHeight)*scale) + originHeight)
```

The height rescale is part of the original emitter; replacing only XY does
not preserve its finalizer inputs. Finalizer `0x141ba99d0` can still alter
range, height, speed and motion configuration afterward. These values are
not claimed to be the final trajectory samples or projectile lifetime.
NaN, infinite and coincident-ray semantics are outside these controls.

## 4. A constructed rejection witness

The control supplies four identical candidate rays `(0,0) → (150,0)` to isolate
admission effects, heights `11 → 29`, angle half-width bits `0x3e5f66f3`,
distance variance `17`, and batch words `[0x1234,0x56,3,2,7]`.
Source owner ID is `0x31415926`, source ordinal starts at zero, allocator low
byte starts at one, and resource seed-zero is disabled.

| Candidate admission | Emitted candidate | Absolute slot | Derived byte | Source ordinal | Raw angle/distance samples | Object seed |
|---|---|---|---|---|---|---|
| all admitted | 0 | 0 | 2 | 0 | 751 / 232 | 2722407559 |
| all admitted | 1 | 1 | 3 | 1 | 34 / 239 | 4043047239 |
| candidate 0 rejected | 1 | 1 | 2 | 0 | 751 / 232 | 2722407559 |
| candidates 0 and 2 rejected | 3 | 3 | 3 | 1 | 34 / 239 | 4043047239 |

For example, the first emitted ray in both the unblocked and first-rejected
cases reaches pre-finalizer XY approximately
`(141.96807861328125, -15.527137756347656)` and target height
`28.137760162353516`. Its layout slot differs while its admitted identity,
hash inputs and batch samples match. Actual geometry need not match when
different upstream candidates have different rays; identical inputs here are
deliberate fixtures. This is not a recorded official cast or an error rate.

The implementation consequence is a stateful ordered emission contract:
preserve candidate traversal, apply admission, then advance the admitted
identity/ordinal and enabled variance samples. Assigning every candidate a
precomputed per-projectile sample/ID package can change subsequent admitted
results when a prior candidate is rejected.

## 5. Verification and remaining boundary

R49 ignored evidence under the projectile execution log includes
`audit_emitter.py`, `emitter-audit.asm/json`, `check_emitter.py` and
`emitter-controls.json`. Fresh byte identity covers **16 ranges / 612 decoded
sites / 21 anchors / five known final-table slots**: five complete unwind
bodies and seven direct-call-bound leaf CFGs. These include diagnostic and
allocation branches that the controls do not execute.

**333 finite controls / 238741 original-instruction steps / 445 visited sites**
compose the actual emitter, retained derived allocator, object hash, float
distribution and small vector helpers. They check five supplied final classes,
zero signs, rejection patterns, side/budget ordering and equality, deferred
stop, zero-seed bypass, ordinal/derived wrapping and signed slot assignment.
Binding sees the preincrement ordinal; enabled variance occurs afterward.
The five tables' previously known `+540 → 0x1400ecd70` zero suffix is rechecked,
not presented as a new RNG mechanism. The reused finite trig bridge separately
checks the reached 60 original instruction sites and nine constant locations
against the fixed PE.

Boundaries are explicit: supplied candidate rays/profile/distribution/batch
state and separate object-scale/vertical-offset inputs; sealed reference
bounded sampler; earlier original-instruction finite
sin/cos bridge; grid conversion, terrain predicate and side height oracles;
runtime association key, resource retention, object allocation/construction,
clone/registration/binding, absent optional target, shared-history policy and
observation-only finalizer. The controls do not prove concrete skill history
policy just by using its final class, nor complete motion/contact/damage.

Remaining work includes composition through actual layout traversal and final
placement, concrete terrain/resource/clone inputs and runtime ownership,
other history-policy branches, exceptional arithmetic, actual effective-stat
publication and thread/TLS consumers. Earlier closed arithmetic/state rules
remain usable evidence without runtime recording. Actual official values,
ordering and client/server parity are separate requirements.

## 6. Joining real layouts to placement and the movement callback

R50 replaces two earlier observing boundaries in a finite ordinary composition:
fan/Volley traversal actually calls the original emitter, and that emitter
actually calls the full final-placement body. This joins previously recovered
rules; it does not discover new mechanisms by counting decoded instructions.
The entry still supplies a caller frame, profile, source state and resource.
It is not an animation-to-release or runtime-stat-publication witness.

The executed chain is:

```text
fan 141bb9730
or Volley 141bb89d0 -> subgroup 141bb8f00 -> fan
 -> per-arrow 141bb2480
 -> retained allocator/hash/ordinal and enabled batch variance
 -> final placement 141ba99d0
 -> selected Stats, optional range rewrite, source/target height conversion
 -> original shared speed getter 141bb38d0 -> modifier 141bb35f0
 -> actual true +548 leaf, initial placement, observer registration
 -> configured presentation callbacks and birth-overlap boundary
 -> surviving-object gate -> final-class +558 movement-call arguments
```

The speed modifier's original two-rounding helper `0x140664f10` executes
in this composition; it is not replaced by a Python rounding formula.
Selected-view/raw-provider bodies also execute. The source supplies a nonzero
cached speed multiplier, no return/context/chain increase and no fresh
weapon-variation branch. Equipment eligibility is explicit input at
`0x141b6fa10`, not a newly recovered equipment-object constructor.
The five final tables all bind the checked shared speed and true-height paths;
their actual movement implementations remain distinct as documented in
[movement configuration](motion-configuration-inputs.md).

### 6.1 Candidate geometry remains separate from admitted identity

With supplied origin `(100,-30)`, aim `(250,-30)`, five arrows, two points per
side, X spacing 23 and Y spacing 19, the original Volley/subgroup bodies give:

| Traversal | Signed slot | Emitted origin | Pre-variance aim |
|---|---:|---|---|
| center | 0 | `(100,-30)` | `(250,-30)` |
| first positive | 1 | `(119,-53)` | `(269,-53)` |
| first negative | -1 | `(119,-7)` | `(269,-7)` |
| second positive | 2 | `(138,-76)` | `(288,-76)` |
| second negative | -2 | `(138,16)` | `(288,16)` |

These rays are parallel before variance; neither side is aimed back at the
original cursor. Signed layout slots are supplied to the emitter; clone slots
store their absolute values. An eight-arrow witness executes unequal subgroup
counts as well, rather than assuming one arrow per point.

Rejecting the first positive and negative candidates at the supplied terrain
predicate leaves actual candidates `0,3,4` with admitted ordinals `0,1,2`,
derived bytes `2,3,4`, and angle/distance raw pairs `751/232`, `34/239`,
`504/1432`. Unlike R49's identical supplied-ray example, these come from the
actual distinct Volley rays and shifted origins. Their final geometry differs;
the admitted stream compaction is the invariant.

The shared stop flag also crosses subgroup calls. With owner-current XY
supplied as `(0,0)` and positive budget 270, center and first positive launch;
the latter sets the stop, so the first negative and later points do not launch.
If the first two side candidates are terrain-rejected instead, center and
second positive launch. The second positive is the first over-budget accepted
candidate and still completes placement. Budget 200 allows only the center,
which itself exceeds that budget. These are constructed input witnesses,
not official skill ranges or map observations.

### 6.2 Placement changes both heights and potentially the aim

The finalizer's incoming `XMM1` is a **vertical offset**, independently supplied
from object scale and selected flight speed. For the ordinary explicit-height
path used here, original instructions compute:

```text
sourceWorldHeight = f32(sourceTerrainHeight + verticalOffset)
targetWorldHeight = f32(targetTerrainHeight +
                       (projectiles_fire_at_ground ? 0 : verticalOffset))
storedOffset334 = trunc_f32(verticalOffset / -7.8125f)
sourceHeightMarker = trunc_f32(sourceWorldHeight / -7.8125f)
targetHeightMarker = trunc_f32(targetWorldHeight / -7.8125f)
gridXY = trunc_f32(floor_f32(f32(worldXY * 0.092f)))
```

Source `Positioned+2b4` receives the world-height value; its `+290` receives
the height marker. They are separate fields, not a universal constant lift.
The floor operation remains an explicit finite ABI oracle in these controls;
negative XY does not use truncation toward zero. Values shown here use the
same supplied float terrain-height domain on both sides, not integer terrain
markers silently reinterpreted as world coordinates.

With supplied heights 11 and 29 and offset `-39.0625`, the source world-height
value is `-28.0625`, source marker 3 and stored offset 5. With preserved aim,
the target marker is 1 normally and -3 with `projectiles_fire_at_ground`.
This illustrates the sign/conversion chain; it does not measure a bow muzzle
or establish a default character/skill offset.

Aim preservation is another input: row `+3c`, `reverse_projectile_spread`
and the ordinary range path affect subsequent coordinates. With row `+3c=0`,
reverse zero and supplied `projectile_maximum_range_override=30`, the original
range getter and finalizer rescale a nontrivial ray to that range. They also
rescale its vertical delta before converting the target marker. For the
parallel example above, motion targets become `(39,-3)`, `(40,-5)`,
`(40,-1)`, `(42,-7)`, `(42,1)` and target marker -1. Preserved aim instead
gives `(23,-3)`, `(24,-5)`, `(24,-1)`, `(26,-7)`, `(26,1)` and marker 1.
Thus a layout or variance endpoint is not necessarily the movement target.
Reverse-nonzero, same-cell random fallback and distance-based angle branches
are decoded but not executed by this composition's controls.

### 6.3 Registration and survival precede movement

The successful observer-registration path executes original stores into a
supplied allocation and spare-capacity listener vector. Configuration,
presentation callbacks and birth overlap remain explicit external boundaries.
An empty overlap returns no target. A separate mutation fixture removes the
flying object during that call; the original subsequent `+328` check then
skips movement. Its admitted identity and ordinal remain consumed, and later
sibling emissions still proceed. This is a caller-survival witness, not an
implemented collision query or proof of a particular target's hit behavior.

For surviving objects, the actual finalizer passes prepared grid XY, target
height, selected speed, `Positioned.byte2c0` and the supplied phase byte to
the installed `+558`. The observation stops there: it does not execute the
skill movement wrapper, integer mover, per-sample contacts or damage.

### 6.4 Evidence and limits

R50 `audit_join.py`, `join-audit.asm/json` and `check_join.py` /
`join-controls.json` retain **61 byte ranges / 2788 decoded sites / eight
anchors / 40 final-table bindings**, plus thirteen named Stats.
**44 composition controls / 377595 original-instruction steps / 1648 visited
sites** pass. The reused finite atan/sincos bridge additionally checks 107
reached sites and sixteen constant locations against the PE. Outer equations
and literal identity/stream examples are separately checked; trig output uses
the previously recovered original-instruction interpreters.

Five final tables are supplied; that alone does not prove each concrete skill's
history policy, equipment, constructor fields or specialized callbacks.
The controls supply descriptor/profile/caller inputs, copied clone state,
retained allocator, nonzero speed cache, empty random-layout vector and optional
target, metadata/scale/layer, valid observer storage and callback policies.
The bounded sampler reuses the sealed seeded reference. Terrain admission and
side height, grid floor, native object/resource/buffer lifecycle, equipment,
minimum-source-extension predicate, orientation, presentation and birth overlap
remain declared fixtures. Near-zero/degenerate and exceptional floating-point
domains, source/clone lifetime and upstream grant/cache publication remain open.
No native code, running client, new capture, server or CI was executed.
