# Shared projectile actions and emission (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [012 — projectile execution](../012-projectile-execution.md).
All addresses refer to the [pinned executable](evidence-and-open-questions.md), not recovered source symbols.
This chapter reconstructs shared paths and selected concrete bindings; a skill can override their virtual slots.

## 1. Release is a chain, not an object-spawn packet

**S:** the traced release chain is:

```text
0x141ba4930: owner, facing and release-event input
  → 0x141ba4db0: animation contact / model offset
    → 0x141ba51a0: count, origin, aim, terrain height and swap preparation
      → action slot +0x618: emission dispatcher
        → 0x141ba99d0: final position, observer, overlap and motion
```

Tornado Shot's constructor at `0x141a82266` installs table `0x1434e3638`, whose `+0x618` binds to
`0x141bb13a0`. The final client table `0x143423898` retains that binding; see [the TS chapter](tornado-shot.md).
This establishes a concrete use of the shared dispatcher, not a universal skill binding.
`0x141ba5390` in an earlier preview is inside an instruction: the preparation entry is `0x141ba51a0`,
and the nearby valid instruction starts at `0x141ba5391`.

The dispatcher separates count 1 from count greater than 1.
The multi-projectile path builds a profile at `0x141bb0f20` and chooses:

| Condition, in order | Layout helper |
|---|---|
| `slot +0x6b8` returns nonzero | Volley `0x141bb89d0` |
| profile `+0x18` has random-direction seed words | randomized layout `0x141bb9280` |
| `slot +0x6b0` returns true | radial `0x141bb9500` |
| otherwise | ordinary fan `0x141bb9730` |

Multi-layout helpers reach per-arrow admission/construction `0x141bb2480`.
Count 1 uses a separate direct creation path inside the dispatcher; do not impose the multi-layout profile,
side-origin admission or variance draw order on that path.

### 1.1 Root creation, local echo suppression and release events are different gates

Local input `0x141e8d770` and the actual S2C `0x1ab` creation branch both use
`0x141ee97f0 → 0x141941c90` to resolve a skill's action factory.
They then enter common admission `0x141e8cf10`, which can queue the candidate at Actor `+0x1c0`.
Admission success does not guarantee immediate activation: `0x141e8e060` checks resource/state gates,
and some queued candidates wait or are destroyed.
The skill's full u16 ID, its action-type hash and its equipment-specific factory binding are separate inputs.

Actor constructor `0x141e8c240` clears byte `+0x1c8`.
Client setup `0x142156d80` resolves the local Actor and, at `0x1421571e1..0x1421571f8`,
sets this byte to 1 only when World raw mode `+0x984` is not 2.
The checked Predictive local context therefore sets it; the checked Lockstep context leaves it zero.
For S2C `0x1ab`, a nonnegative signed sequence combined with this byte being nonzero skips root creation;
the branch can correct a sufficiently distant owner position instead.
Negative sequences bypass this echo-suppression condition even for that local Actor.
Default-zero remote/monster Actors and the checked local Lockstep path enter the creation branch,
still subject to skill and Actor admission conditions.
This is positive evidence for a creation route, not proof that every monster's resources activate it successfully.

Do not confuse that byte with the active action pointers:
queued action slot `+0x38` chooses the Actor `+0x1b0` versus `+0x1b8` route.
The ordinary projectile binding `0x141ba2cd0` reads `skill_is_non_pathing` (`0x4f32`)
and `projectile_skill_is_non_pathing` (`0x4f33`).
These are non-pathing/pathing routes, not authoritative/predicted ownership slots.

The ordinary Fireball final table `0x14333ac40` binds event slot `+0x140` to `0x1419eec90`.
When event `+0x38:u16` equals `0x9b8d`, that handler calls action `+0x4b8 → 0x141ba4930`,
closing the concrete event-to-shared-release edge.
Tornado Shot uses a different event wrapper `0x1414fbd00`, with the same checked release slot.
The [animation request/selection and listener timeline chain](animation-and-release.md)
now connects those endpoints through the real Actor event dispatcher.
Subscription, resource/descriptor and event-forwarding gates remain conditional;
this static route does not prove an unconditional or runtime-observed first launch.
No universal attack-versus-spell prediction switch was identified in this bounded admission path.
The five final actions also expose a [synthetic-contact startup wrapper](release-and-repeat.md#21-temporary-synthetic-contact-context)
through `+0x268`; its temporary `1e8` flag is separate from network mode.
[Concrete server-command callers](synthetic-start-and-server-commands.md) now bind `0x1ce/0x1cf` to that wrapper,
with transported sequence/flags/target and an optional animation event; other callers remain unexhausted.

Round12 `root-action-admission-findings.zh-CN.md`, `root-action-activation-full.asm`,
`root-owner-local.asm`, `root-start-slots.asm` and `root-release-listener.asm` retain the concrete gates.

## 2. Effective count and release count

**S:** preparation first reads `number_of_projectiles_override` (`0x1884`); a nonzero value wins,
otherwise it calls `+0x5f0`. Zero count exits.
That virtual getter is skill-overridable.

The generic count getter `0x141ba6070`:

1. Suppresses extra counts from action `+0x2c0` and optional `+0x23c` when
   `modifiers_to_projectile_count_do_not_apply` (`0x46d9`) or
   `modifiers_to_number_of_projectiles_instead_apply_to_splitting` (`0x29c7`) applies.
2. Uses `+0x598` to choose `virtual_total_number_of_projectiles` (`0x4b4b`) or
   `virtual_total_number_of_arrows` (`0x4b4c`).
3. Applies the conditional minimum of 1 (`0x4a37`) and maximum of 1 (`0x56ee`).

`0x141ba61c0` also distributes a total across releases using quotient/remainder and action
`+0x3fc`, `+0x400`, source ordinal `+0x448`.
The divisor is a conditional count of eligible animation contacts, not the inner-repeat counter.
See [native count inputs](count-stat-inputs.md) for the four upstream formulas and
[release/repeat lifecycles](release-and-repeat.md) for the exact distribution, initialization and final bindings.
Skill total, count in this release, count at a Volley point and count of skill-created children are distinct inputs.
Publishing only one generic projectile-count stat is insufficient for arrow-based client prediction.

## 3. Action identities and random contexts

**S:** action sequence is `+0x11a:u16`, derived byte `+0x11c:u8`, result stage `+0x120:u8`.
Clones share the allocator reached through action `+0x160`.
`0x141421a10 → 0x14193dc60` allocates derived bytes.
`0x14193daf0` installs one on a clone **and appends the clone to the source Actor's retained-action vector**.
Shared single/multiple release and scheduled rain call it; TS also registers a temporary secondary-emission template.
Its later actual flying children are separately cloned/registered and the template retires after release.
See [the exact admission/update chain](world-scheduling.md#5-common-clone-admission-derived-binding-also-appends-the-action).
The source ordinal at `+0x448` and Volley slot at `+0x450` are separate fields.
They must not be substituted for the derived byte or result stage.
Normal common start resets source ordinal to zero; each admitted projectile reads/copies it before
binding and increments the source afterward. The separately preincremented release ordinal starts at one.
Inner animation rewinds retain the source ordinal; an outer normal restart conditionally resets it.

The multi-emitter obtains the derived byte before creating the object.
Unless a resource flag requests seed zero, `0x14193a720(action, derivedByte, sourceOrdinal)` supplies its seed.
Angle/distance variance consumes samples only when the corresponding variance is nonzero.
Terrain rejection before allocation changes which subsequent launch receives which identity.
Registration also has its own [initialization latch and conditional contribution contract](action-initialization.md).
The copied `124` latch resets independently of the preserved synthetic flag/source ordinal;
Fire Wall/Kinetic Wall cache inheritance does not imply ordinary-client native damage recomputation.

The [Tornado Shot RNG case study](tornado-geometry.md)
records the allocator and a reconstructed eight-word terminal initializer, with an
[offline reference](../../../../scripts/re/projectiles/native_tornado_rng.py).
The [shared random-input chapter](random-inputs.md) reconstructs the separate four-/five-/seven-word initializers,
object-seed hash, batch variance, randomized layout and motion-speed contexts.
That terminal context cannot substitute for them. Remaining initialization sites, skipped draws and stage
creation order still need reconciliation before claiming deterministic client/server parity.

## 4. Ordinary fan: aim distance changes spread

**S:** for normal finite inputs and `n > 1`, the shared fan has the following float32 computation.
Let `L = length(aim-origin)` and `spread` default to 60.0:

```text
m = (100 + multiple_projectiles_projectile_spread_+%) / 100
cap = min(2*pi, m * 2.094395160675049)
if fixedSpread != 0:
    step = max(fixedSpread, 0) * (pi/180) / (n-1)
else:
    step = max(min(atanf(m * spread / L), cap/(n-1)), 0.14279967546463013)
theta[i] = -(n-1)*0.5*step + i*step
```

`total_projectile_spread_angle_override` (`0xa0a`) supplies `spread` when nonzero.
Despite its name, this branch uses it inside `atan(spread/L)`; the default 60 is not the final fan angle in degrees.
`fixed_projectile_spread` (`0x76d`) supplies `fixedSpread`.
`projectile_remove_default_spread` (`0x2186`) sets the latter to -1, producing zero step.
The two constants above are approximately 120 degrees and 8.18 degrees, respectively.
Preserve the native `max(min(...), minimum)` order; it is not an ordinary clamp if its bounds cross.

**I:** the common fan supports the observed wider spread for a nearby cursor.
Fixed spread, Volley, radial layout and skill overrides need not follow that distance response.

The [layout-input contract](layout-stat-inputs.md) binds these inputs to actual selected-stat getters,
ordered short circuits and the dispatcher ABI. It distinguishes the per-call profile from underlying
Stats caches and gives the concrete TS primary/secondary overrides; actual grant/publication remains separate.

Ray construction also has a default length of 150; certain metadata/reverse branches preserve `L`.
This constant is not established as the final projectile lifetime or maximum range.
`additional_projectiles_fired_with_distance_offset` (`0x2185`) can start ray length at
`R - n*offset*0.5`, enforce a lower bound of approximately 54.347824 when active, then increment per ray.
Height is adjusted by the ratio of ray length to original `L`.
Native atan-like code at `0x142d75ccc` has been identified, but its complete bit-exact port and exceptional
zero-distance/NaN behavior are unresolved.

### 4.1 Radial uses a separate full-circle step and caller phase

Shared radial helper `0x141bb9500` normalizes the incoming XY ray using reciprocal length and
multiplies each component, then uses these separate float32 operations for positive finite inputs:

```text
step = f32(6.283180236816406f / f32(count))
theta[i] = f32(f32(f32(i)*step) + callerPhase)
R = both checked control bytes zero ? 150f : originalLength
height = f32(f32(f32(f32(targetHeight-originHeight)/originalLength)*R) + originHeight)
rotX = f32(f32(unitX*nativeCos(theta)) - f32(unitY*nativeSin(theta)))
rotY = f32(f32(unitY*nativeCos(theta)) + f32(unitX*nativeSin(theta)))
worldXY = f32(originXY + f32(rotXY*R))
```

The first ray starts at caller phase; radial has no fan-centering term and divides tau by `count`,
not `count-1`. Its tau bits `0x40c90fd0` differ from the random-direction branch's fuller float approximation.
The helper itself makes no random-layout draw. Its two control branches and phase are caller inputs;
this does not establish which special skills enable radial layout.
The actual calls to native sin/cos and independent outer multiply/add are covered by the
[bounded radial comparison](random-inputs.md#612-radial-rotation-and-an-explicit-grid-boundary-counterexample).
That comparison injects world positions into a separate grid oracle and does not bypass the real
per-arrow admission/finalizer contract to claim final motion parity.

### 4.2 Nondefault fan branches and sequential distance offsets

The complete fan **`0x141bb9730..0x141bb9a9b`**, including its three chained unwind spans,
confirms the branch equations above beyond the earlier default-spread comparison. The helper receives
spread, signed fixed-spread input, modifier, signed distance offset, two preserve-length bytes and count;
these controls supply caller-frame inputs, not proof that every combination is published by a skill.

For finite noncoincident rays, preserve each float32 operation. The spread multiplier first wraps
`modifier+100` to 32 bits, interprets that as signed i32, converts to float32, then divides by 100.0f.
The cap is `min(6.2831854820251465f, f32(multiplier*2.094395160675049f))`.
For count greater than one, a **nonzero** fixed input bypasses atan and does not use the ordinary minimum/cap
to choose its step. The earlier cap setup and count-dependent division still execute before this test:

```text
step = f32(f32(f32(max_signed(fixedSpread,0))*0.01745329238474369f) / f32(count-1))
center = f32(f32(f32(count-1)*0.5f) * (-step))
theta[i] = f32(f32(f32(i)*step) + center)
```

A negative fixed input therefore produces zero step rather than falling back to distance-sensitive spread.
For fixed zero, native atan followed by min then max uses the ordinary formula; these operations must not be
replaced with a clamp that presumes ordered bounds. The helper's count-one branch uses zero step and center,
independently of fixed input. Count zero still performs setup/branch arithmetic but never calls the per-arrow
emitter. These are helper contracts; the ordinary count-one dispatcher may bypass this helper.

Distance staggering has a distinct machine-width conversion at **`0x141bb98c7/98cb`**:

```text
baseRadius = preserveByteA != 0 or preserveByteB != 0 ? originalLength : 150.0f
product = u32(count * offset)                  // low 32 bits from IMUL EAX,R14d
radius = f32(baseRadius - f32(f32(product)*0.5f))
if offset != 0:
    radius = max(54.34782409667969f, radius)    // bits 0x4259642c; once before the loop
increment = f32(signed32(offset))              // CVTDQ2PS, not the earlier zero-extension
for i = 0 .. count-1:
    candidateXY = origin + rotatedUnit(theta[i]) * radius
    candidateHeight = f32(f32(f32(heightDelta/originalLength)*radius) + originHeight)
    perArrowEmitter(candidateXY, candidateHeight, ...)
    radius = f32(radius + increment)
```

`candidateXY` also uses separate float32 multiplications/additions, as in the earlier rotation comparison.
Radius advances by repeated addition, not a recomputed `initialRadius+i*offset`; the lower bound is not
reapplied per arrow. For nonoverflowing nonnegative offsets this reduces to `R-count*offset/2`, rather than
`R-(count-1)*offset/2`. With zero fixed step, base 150, count five and offset ten, the five candidate X
distances are **125, 135, 145, 155, 165**, not a set centered on 150.
Synthetic negative/overflow controls demonstrate the two conversion rules: count four and offset
`0x40000000` yield wrapped product zero, so the first radius remains 150 despite a nonzero offset.
This does not establish an obtainable item/stat combination or an observed gameplay error.

Round46 checks **2,550 finite cases / 8,902 candidate attempts**: 1,944 spread/fixed cases, 600 distance/height
cases and six literal/overflow controls. Counts 0/1/2/3/5/9, modifiers -200/-100/-99/0/50/300, spread
1/60/240, fixed -1/0/1/30/180/360, both preserve bytes and finite positive ray lengths are exercised.
The full fan and eight actual vector/min/max helper CFGs execute **1,011,930 interpreted instructions / 263 sites**.
Scalar atan/sincos calls use cached ABI output bridges to the earlier original-instruction interpreters:
46/142 distinct inputs, 10,889 further steps; 305 reached trig sites and 26 constant records are freshly
compared with the PE. The bridge does not reproduce arbitrary callee register or floating-environment effects.
Outer layout formulas and six literal controls are separately written; transcendental outputs reuse those
original-code interpreters rather than an independent second implementation of atan/sincos.

Fresh audit seals **11 ranges / 268 instructions, 18 anchors and seven constants**. The helper CFG includes
the normalizer's zero arm; the finite execution cases require nonzero ray length and do not exercise that arm.
Ignored evidence: round46 `audit_fan.py`, `fan-audit.asm/json`, `check_fan.py` and `fan-controls.json`.
Nearest/even is assumed. NaN/infinity, degenerate rays, arbitrary count/angle domains and actual input/cache
publication remain outside these controls. Per-arrow emission is an observation fixture: recorded attempts
are not admitted objects, damage hits or final motion endpoints. Downstream admission, batch variance,
terrain/finalization and authoritative results retain their own contracts.

## 5. Volley translates both ends of the ray

**S:** let `V = slot +0x6b8` and available points `P = 2*V+1`.
For nonnegative ordinary counts, `0x141bb89d0` distributes `N` as:

```text
q = N / P; r = N % P                       # integer quotient / remainder
centerCount = q + (r % 2)
eachSideCount[k] = q + (k <= floor(r/2) ? 1 : 0)    # k = 1..V
forward = normalize(aim-origin)
side = (forward.y, -forward.x)
sideOrigin = origin ± side*k*xDist + forward*k*yDist
sideAim = sideOrigin + (aim-origin)
```

`xDist` comes from `virtual_additional_projectiles_fire_parallel_x_dist` (`0x18b2`),
`yDist` from `additional_projectiles_fire_parallel_y_dist` (`0x18b3`).
`0x141bb8f00` shifts origin and aim together.
With one projectile per point these base rays are parallel; they do not converge at the original cursor.
When a point emits multiple projectiles, its subgroup can still use randomized, radial or fan layout.
Emission visits the center then paired side points in increasing order, affecting ordinal/identity allocation.
The side-point terrain admission below can suppress a launch before allocation.
Randomized side groups also cumulatively mutate a seed word by 529; see [the exact recipe](random-inputs.md#5-correction-profile-vector-contains-seeds-not-explicit-directions).

### 5.1 Float32 translation and subgroup boundaries

The dispatcher reads signed `18b3` then `18b2` from the current selected view; each distance converts
to float32 before multiplying the side index. There is no further world-unit or AoE scale in this helper.
The finite direction uses reciprocal length then component multiplication; a zero ray gives a zero vector
here, without proving that later subgroup/finalizer code supports that ray.
With `d=f32(aim-origin)`, normalized `u`, lateral `s=(u.y,-u.x)`, and independent per-axis rounding:

```text
lat = f32(s*f32(f32(k)*f32(signed32(xDist))))
forward = f32(u*f32(f32(k)*f32(signed32(yDist))))
positiveOrigin = f32(f32(origin+lat)+forward)
negativeOrigin = f32(f32(origin+f32(lat*-1f))+forward)
sideAim = f32(sideOrigin+d)
```

Only lateral displacement reverses sign; both sides retain the same forward displacement.
Do not replace the last line with an algebraically regrouped `aim+offset` or contract the adds into FMA.
Origin/target heights pass unchanged to these subgroup calls; later per-arrow side-height sampling is separate.
Both center and side choose nonempty random seeds before radial before fan. Their random/radial phase is `+0`.
Zero-count side groups still update seed word 3 by wrapped `+529` and call their subgroup;
skipping such groups can therefore change later directions even though they allocate no arrows.

Side admission quantizes the side origin to grid, but its query source is action `+0x138` or owner Positioned
`+0x294`, selected by action word `+0x122` bit 13. It is not unconditionally the quantized layout origin.
The typed line predicate `0x141d87160(...,1,true)` returns nonzero to reject **before** derived allocation.
Its internal map/status semantics are a separate boundary.
Round24 `volley-read.py`, `volley-check.py` and `volley-audit.py/json` retain 23 spans / 2412 instructions,
677 interpreted addresses, 210 formula fixtures / 720 subgroup calls, 18 typed admission controls
and 15 explicit expected controls. Subgroup execution, allocation and the terrain predicate are modeled
call boundaries; these tests do not execute full motion, native allocation or the map query implementation.
The later [layout-to-placement composition](emission-admission.md#6-joining-real-layouts-to-placement-and-the-movement-callback)
executes those ordinary fan/Volley subgroup calls through the actual emitter and finalizer, retaining explicit
terrain/construct/clone/presentation/overlap boundaries. It verifies parallel side rays, admission/variance
compaction and a shared stop across subgroups, rather than assuming layout attempts are created arrows.

## 6. Per-arrow construction and state inheritance

**S:** the multi-emitter `0x141bb2480` has this observed order:

1. Optional side-origin admission: `0x141d87160(originalSourceGrid, sideOriginGrid, 1, true)`;
   rejection exits before identity allocation and object creation.
2. Optional distance-budget admission from profile `+0x0c`: a previously set shared stop flag exits.
   A current over-budget candidate sets that flag but is still created; later candidates are suppressed.
   Its [producer and deferred-stop contract](#64-profile-distance-budget-and-deferred-stop) is mapped below;
   actual effective inputs and full terrain semantics remain separate.
3. Allocate derived byte, then object seed (or the resource-selected zero).
4. Create the `0x248`-byte object through `0x141939710`, register it with the world and associate owner via `0x141de8600`.
5. Populate Positioned layer, metadata extents and optional scale.
6. Clone action via `+0x450`, set derived byte, copy source ordinal and store absolute Volley slot.
7. Bind entity via `+0x710`; the shared binding `0x141bab560` writes action `+0x328`.
   Increment the source ordinal afterward.
8. Explicitly share, deep-copy or create contact history according to shotgun/preservation policy.
9. Apply optional target, side-origin terrain recalculation and enabled variance draws.
10. Call `0x141ba99d0` to finish placement/observers/motion.

The [per-arrow composition](emission-admission.md) now interprets this original emitter with retained
allocator/hash and native float-distribution arithmetic across rejected/admitted candidate sequences.
It connects identity/ordinal advancement to shared batch-variance consumption and finalizer arguments;
supplied terrain/construct/clone/bind/history and finalizer boundaries remain explicit.

History inheritance is described in [continuation and memory](continuation-and-memory.md#5-shotgun-and-history-inheritance).
It is not determined by the object ID or derived byte alone.
The single-count dispatcher likewise creates the object before cloning/binding and setting up motion,
but follows a separate path.

The clone copies primitive counters/budgets while retaining metadata/stats/owner references.
Later paths still obtain action stats via `0x14193b350(action, action+0x174)`.
This is **not evidence of an immutable snapshot of every effective stat** at projectile birth.
Stat mutation and owner replacement need separate traces.

### 6.1 Selected stat views and cached values have different lifetimes

Shared getter `0x14193b350..0x14193b40f` selects an action's stat view on each call.
The checked projectile callers pass the stat-set selector byte at action `+0x174`, not result-stage byte `+0x120`.

| Checked condition | Returned source |
|---|---|
| action `+0x1b8/+0x1c0` storage exists and the selected 32-byte record has a nonempty stack | last shared-pointer object in that record's stack |
| no selected override, selector 0 | action's retained skill `+0xf0`, then skill `+0x138/+0x140` through `0x141ee7e70` |
| no selected override, selector n > 0 | same skill, additional stat-set shared-pointer entry n-1 at skill `+0x108` |

`0x141ee7e70..0x141ee7fa9` returns shared ownership of the selected object; it does not copy every numeric stat.
The outer `0x14193b350` releases its temporary shared pointer before returning a raw `Stat*` to its caller.
Its indexed override branch assumes a valid selector; the getter itself has no corresponding bounds check.
The primary source is not a direct read of skill `+0xf8`, which the S2C
[`0x1b4` handler](../../protocol/11-skills.md#31-s2c-0x1b4-update-one-skills-stat-set) updates.
However, skill construction `0x141ee75cf..0x141ee75f1` copies the input stats pointer into `+0xf8/+0x100`,
and `0x141ee779d..0x141ee77bc` moves that same input into `+0x138/+0x140`.
These slots therefore initially alias; they must not be assumed either permanently unrelated or permanently identical.
This alias is established at constructor `0x141ee77bc`; subsequent script/event callbacks are not fully traced.
Later slot replacement remains open; do not assume an active raw-to-computed bridge solely from shared-library code.
The independently traced inheritance/transaction mechanisms and their client gate are described below.

Base action construction `0x141937cc0` retains the skill and starts override storage empty.
Base copy `0x141938020` retains that same skill reference, copies selector `+0x174` and initializes its own
`+0x1b8/+0x1c0/+0x1c8` to zero at `0x14193848a..0x14193849a`.
It does not directly copy the source override-vector contents.
Subsequent binding/notification hooks and concrete action setup remain separate;
this is not proof that every finished descendant permanently has no override.

The checked overlay factory `0x1424fbb80` retains a parent stat object and registers itself in that parent's child list.
Retaining that relationship does not establish a frozen numeric snapshot or the complete cache invalidation policy.
Scope setup `0x14193be90` materializes per-index groups and pushes a derived view when needed.
The checked cleanup `0x14193c690` removes marker-associated changes and decrements group depth;
it pops a base layer when that depth becomes zero, with a separate identity-checked pop for an explicit overlay.
Nested scopes therefore cannot be modeled as an unconditional one-push/one-pop pair.
These are conditional shared-library paths: the ordinary startup-written gate below skips their establishment.
Separately, already-mapped projectile code caches speed variation at `+0x3a4`, Pierce budget at `+0x44c`
and the additional-chain roll at `+0x478`; a later getter does not automatically recompute these fields.

The reconstruction must distinguish raw network updates, selected/overlaid stat objects and explicit action caches.
The checked initial alias gives a conditional path from a raw update to a later read;
it does not prove that every in-flight outcome immediately changes, or that all inputs freeze at launch.
Evidence and outstanding mutation boundaries are retained in round10 `action-stat-lifetime.zh-CN.md`.

### 6.2 Raw notifications and computed transactions are separate propagation mechanisms

Raw setter `0x1424cfaf0` writes its key/value pair and tail-calls signal dispatcher `0x1424cfd00`
with `(raw+8, raw, key)` at `0x1424cfbd0..0x1424cfbee`.
The dispatcher synchronously visits registered observers through trampoline `0x1402fffbc`,
which calls each observer's first virtual slot.
It tracks reentrancy and compacts removed registrations after the outer dispatch returns.
The checked setter does not skip notification merely because the same key already has the same value.
This is a notification chain, not by itself a universal derived-cache invalidation contract.

Raw constructor `0x1424cf8d0` retains an optional parent at `+0xe0/+0xe8`
and registers its embedded `+0xd8` observer in that parent's signal.
Raw getter `0x1424cfa40` first looks for a local pair, then falls back to the parent's first virtual getter, else zero.
Observer `0x1424cfc00` suppresses a parent notification when a local pair already owns that key;
otherwise it forwards the key through the child's own signal, with the child as source.
A local zero value therefore shadows an inherited value; absence and explicit zero are different.

The previously mapped computed wrapper is a different class and layout.
Its getter `0x1424fc1e0` uses a local materialized value when present, otherwise follows parent `+0xf0`.
Selecting that view on each call does not mean recomputing every formula on each numeric read.
Transaction begin `0x1424fd240` and outermost end `0x1424fd390` process accumulated changes and dependencies.
Computed descriptor functions run through `0x1424fcc80`; changed results update materialized entries
and enter dependency/changed-key lists.
The complete descriptor table and all formulas have not been reconstructed.

Parent transaction end also walks children at `+0x220/+0x228`:

1. Retain each live child and begin its transaction.
2. For each changed parent key, call `0x1424fcfb0(child,key)`.
   A materialized child entry re-reads parent detail through `child+0xf0`, then restores its own local deltas.
   The child also queues the key's dependencies and change notification.
3. Cleanup helper `0x141937a40` dereferences each child slot and directly calls `0x1424fd390`.
   This closes the child transaction and permits recursive descendant propagation.

Thus retained computed views can update after construction while preserving local modifiers.
Neither mechanism proves the complete bridge from every `0x1b4` raw update to every selected computed view.
For the conditional case where a later action read selects the same updated raw object and locally present key,
the setter's write and raw getter's read do establish that the next numeric read sees the stored value.
Actual object identity, read timing, overlays and explicit action caches still have to match.

### 6.2.1 Successful startup disables the checked action computed-overlay paths

Application startup `0x140103390` calls `0x140101680` and proceeds on its successful return.
Inside that function, `0x140101e50` writes global byte `0x1445b38cd = 1`.
In its checked direct-control-flow graph, this setter dominates the successful `AL=1` return;
call-internal side effects and exceptional control flow are outside that graph.

The same byte directly gates modifier addition `0x14193b540`, scoped modification `0x14193bae0`
and overlay materialization `0x14193be90`.
When nonzero they skip the corresponding modification/transaction/stack-establishment bodies.
Cleanup `0x14193c690` can exit when the scope's view pointer is empty.
Under this normal startup-written state, the checked action path therefore does not require a raw-to-computed bridge.

This corrects the stronger interpretation that every client action necessarily builds these computed overlays.
Raw network stats have a `0x110`-byte allocation; the computed parent accesses a child list at `+0x220`.
They are different layouts, and the guarded factory's existence does not show that an ordinary raw object reaches it.
The raw setter/getter themselves store/read pairs rather than execute computed descriptors.
For a selected raw object with a locally present final-radius key, its pair supplies that value;
whether actual server updates carry the relevant keys still needs capture evidence.

The flag's original semantic name and runtime value were not observed.
A bounded exact-RIP scan found the startup write and no other confirmed direct write;
indirect, wider and external writes remain unexhausted.
Other computed-library callers and special action paths are not excluded by these three gates.
Round12 `raw-effective-bridge-and-client-gate.zh-CN.md`, `probe_stat_bridge.py`,
`startup-dominance.json` and `action-guard-sites-extra.asm` retain the positive chain and search limits.

### 6.3 Current queries, cached outcomes and consumed state coexist

| Checked consumer | Current view query | Separate retained state |
|---|---|---|
| Pierce eligibility `0x141ba96a0` | reselects action `+0x174` view for keys including `0x2247/0x3438/0x2194/0x28f3`; positive `0x3438` calls the uncached TLS chance gate before always-pierce checks | finite budget `+0x44c` is read/decremented only in the final success branch; copied independently of executed `+0x454` |
| Chain budget `0x141bb6530` | queries `0x223f`, with conditional `0x2241/0x3065` reads | cached extra-chain roll `+0x478`, action conditions and `+0x47c` also contribute |
| Fork budget `0x141bb6600` | queries `0x2a0f`, with conditional `0x223d/0x3063` reads | executed generations and phase conditions remain action state |
| speed variance | initialization branch reads its relevant inputs | nonzero `+0x3a4` is reused and copied by clones |

Re-reading a chance stat cannot replace the already cached random outcome;
re-reading a budget-related stat cannot automatically restore consumed Pierce state.
This mixed lifetime exists within a continuation mechanism, not only between different skills.
No server update policy follows without identifying the exact property's query/cache point.

The bounded post-construction search has not found a direct skill `+0x138` replacement in the checked
`0x141ee0000..0x141f20000` and `0x142192000..0x14219d000` neighborhoods.
The additional basic-action constructor `0x14219ac00` does initialize that slot;
`0x142195b8a` instead writes an action's `+0x138`, not a skill's.
Indirect/base-adjusted/out-of-range writes remain unexhausted, and updating a raw object's content needs no slot replacement.
Round11 `action-stat-invalidation.zh-CN.md`, `rawstat-forward-exact.asm`, `wrapper-parent-propagation.asm`
and `projectile-cache-vs-getter.asm` retain the typed chains and search boundaries.

### 6.4 Profile distance budget and deferred stop

Profile builder `0x141bb0f20` stores stat `2185 additional_projectiles_fired_with_distance_offset`
at profile `+8` and initializes budget `+0x0c` to `-1f`. Zero offset skips budget construction.
For nonzero offset, let `O` be the owner's Positioned float XY at `+0x2b8` and `A` the fifth
profile argument, the same aim XY passed by the shared dispatcher to the layout helpers.
For finite nonzero length and stable inputs, preserve each float32 operation:

```text
D = f32(A-O)
L = sqrt32(f32(f32(D.x*D.x)+f32(D.y*D.y)))
U = f32(D/L)                           # separate component divisions
P = f32(U*L)
P = f32(P*f32(signed32(count)))
P = f32(P*f32(signed32(offset)))
P = f32(P*10.8695650100708f)
Q = f32(O+P)
budget = norm32(f32(Q-O))
```

The normalize-then-remultiply sequence cannot be algebraically removed without changing rounding.
Its last scale constant is `10.8695650100708f`, not a half. These are actual instruction operations,
not a claim that every synthetic count/offset combination is available to every skill.

The builder floor-quantizes `Q*0.09200000017881393f`, then calls integer-line walker `0x141bb8620`.
Its start is action `+0x138` when action word `+0x122` bit 13 is set, otherwise owner Positioned `+0x294`.
The walker queries each visited cell through `0x141d85260(...,1)` and continues for byte result at least one.
At the first smaller result it replaces the budget with distance from `O` to that cell's world center
`f32(f32(cell+0.5f)*10.8695650100708f)`. The caller does not cancel the release based on its returned bool.
Grid/status interpretation, exceptional math and all other profile callers remain bounded separately.

After optional side-origin admission, per-arrow `0x141bb2480` checks a **positive** budget:
an already set shared flag rejects this candidate; otherwise a strictly larger distance from the candidate
aim to the current owner float XY sets the flag. It then still allocates the current derived byte/object.
The dispatcher initializes one flag shared across subgroups; it does not reset the flag at every Volley point.
With budget 5 and candidate distances 5, 10, 0, the first two are admitted and only the third is rejected.
A nonpositive budget bypasses this guard, including its flag check.
The guard precedes object allocation and variance, while randomized layout samples occur before invocation.

Round24 `profile-budget-audit.py/json` retains nine functions / nine explicit spans / 1718 instructions,
26 anchors and seven constant reads. `profile-budget-numeric.py/json` checks 620 finite producer fixtures,
30 guard controls and the three-candidate sequence using decoded original fragments.
Terrain clipping and allocation are explicit numerical-test boundaries; neither native execution nor a complete
terrain/finalizer simulation is claimed. Actual offset publication, zero normalization and overflow remain open.

## 7. Origin and height use animation contact, model and character scale

**S:** the release wrapper reads:

| Key | Stat |
|---|---|
| `0x1c2e` | `use_scaled_contact_offset` |
| `0x1230` | `projectile_uses_contact_position` |
| `0x1b3e` | `projectile_uses_contact_direction` |
| `0x1738` | `maintain_projectile_direction_when_using_contact_position` |

The wrapper can derive vertical offset from the animation-contact third component or Positioned metadata
`+0x158 → +0x5c`, combined with owner Positioned `+0x2b0` scale.
Preparation samples source/target terrain heights via `0x141d84be0`.
`projectile_swap_from_to_locations` (`0x1b25`) swaps coordinates and their heights.

The `xmm1` input of `0x141ba99d0` is the vertical offset, **not duration**.
The traced register chain carries it from the wrapper; division by `-7.8125f` and truncation writes action `+0x334`.
That offset contributes to source/target height; `projectiles_fire_at_ground` (`0x39fa`) can remove it on the target side.
Shifted side origins get another terrain-height lookup.
Thus native bow placement has animation/model/scale-dependent inputs, beyond a universal fixed height lift.
No particular character/bow offset has been calibrated against running-client frames in this batch.

### 7.1 Actual release event through preparation and dispatch

**S:** the ordinary event adapters of all five final tables (six equipment witnesses) reach the same
`+0x4b8 = 0x141ba4930`, then `0x141ba4db0 → 0x141ba51a0 → +0x618 = 0x141bb13a0`.
Bow/wand and Burning Arrow use `0x1412df000 → 0x141ba4620`, KB uses `0x141ba4620`,
Fireball uses `0x1419eec90`, and TS uses `0x1414fbd00 → 0x141ba4620`.
The release event's word `+0x38` is `0x9b8d`. Startup hand cache `+0x518` and the post-release
`+0x2b8` intensity/repeat branch are separate inputs; the composed ordinary witnesses supply one and zero.

Release preincrements `+0x3f8`, refreshes its selected row and evaluates installed predicates before choosing
ordinary or placed/special origins. In the checked ordinary branch it obtains a **nonnull owner Model** and
copies the event's float triple at `+0x48/+0x4c/+0x50`. Positioned byte `+0x1e5 & 0x10` negates the first
component. If Model pointer `+0x1b8` is nonnull, it changes the third component to
`f32(eventZ - f32(signed32(Model[+0x1f8]) * 7.8125f))`. The original multiply constant is **positive**;
the later negative height conversion must not be applied here a second time.

The event is retained at action `+0x488` while emission runs; the ordinary path destroys it and clears its
`+0x4e8` latch afterwards, including when effective count is zero. Source grid is action `+0x138` when
word `+0x122 & 0x2000` is set, otherwise owner Positioned `+0x294`. Each component becomes
`f32(f32(cell + 0.5f) * 10.8695650100708f)`. Positioned `+0x29c` is supplied as the **facing angle**
to the wrapper, not as a source height.

| Wrapper branch | XY contact offset | Vertical offset |
|---|---|---|
| `use_scaled_contact_offset == 0` | native float32 rotation of event XY by facing | event Z, or enabled signed action `+0x144` low DWORD times `-7.8125f` |
| nonzero | Positioned transform result minus current Positioned float XY `+0x2b8` | transformed Z minus `0x141e35800`, or enabled height override multiplied by `-7.8125f` and metadata/character scale |

The height override enable is the low byte of action `+0x144`'s upper DWORD.
The three horizontal channels passed to preparation stay distinct:

- `projectile_uses_contact_position` selects the position offset.
- `maintain_projectile_direction_when_using_contact_position` copies that **already gated** offset.
  It does not recover raw contact XY when the position gate is off.
- `projectile_uses_contact_direction` independently selects the direction offset.

Preparation refreshes the selected row again. Nonzero `0x1884` bypasses the installed `+0x5f0` count getter;
otherwise the original generic distribution or Fireball/TS override executes. The latter include Fireball
variation `0x1921 == 2` forcing one, and ordinary TS secondary state `+0x530` selecting `0x10df`.
Event distribution uses source ordinal `+0x448`, not the just-incremented release ordinal.
The [count-input contract](count-stat-inputs.md) covers aliases, bounds and conversion gates.

With source and target in the same grid cell, preparation initializes a separate five-word stream from
`[sequence, derived, action+0x458, remaining+0x2d0, ownerID]`, samples direction and writes back the saved aim
grid. Its float distribution over approximately `0..2*pi` uses bound **7001**; the range constructor's
rounding makes this different from a guessed 7000-way draw. This precedes the independent multi-emission stream
`[sequence, derived, stage+0x120, remaining+0x2d0, releaseOrdinal+0x3f8]`.
The variance distribution uses bound **3001**. Equal scale endpoints consume no scale draw.

Preparation samples terrain separately for source and target, applies installed source-height adjustment,
and conditionally swaps **both coordinates and their separate heights** for `0x1b25`.
The actual dispatcher takes its single-object branch for one; multiple projectiles build the original profile
and call real fan/Volley traversal, per-arrow admission and final placement. Volley translates source and aim
together. Two releases advance release seeds and source ordinals independently; reset/copy is not implicit here.

Round58 `audit_release.py`, `release-audit.asm/json`, `check_release.py` and `release-controls.json` retain
135 ranges / 6328 decoded sites, 85 slot reads and 21 anchors; 74 controls execute 488850 original-instruction
steps / 3625 visited sites. Six event-to-movement-ABI witnesses cover single, multiple and Volley paths;
other controls cover contact gates, signed model correction, scaled/unscaled overrides, saved source grid,
height swap, count bounds/overrides/distribution, same-cell fallback and successive release RNG order.
A nonrelease descriptor-type-19 control reaches the original base event handler; arbitrary unmatched events
are not declared no-ops. Signed CDQ/IDIV and XCHG support has separate original-site controls.

This replaces a supplied layout caller ABI with the original release/preparation chain. It still supplies
action/equipment/effective-stat objects, selected row/resource, model/Positioned transform and terrain providers,
construction/clone/bind/history policy, birth-query and presentation/subscription/post-dispatch boundaries.
The five-word core and trig use sealed offline references; no native target runs.
Root/timeline production, complete flight/contact/result composition, actual TLS scheduling and official server
policy are not established by this join. Model values are test inputs, not calibrated character muzzle offsets.
The later [controller/Actor composition](animation-and-release.md#41-joining-the-controller-dispatcher-actor-gates-and-real-release)
feeds this release from the original two-vector event dispatcher and installed forwarding gates. Descriptor/event/
resource inputs, World checkpoints and deferred action cleanup remain declared boundaries there.

## 8. Placement, callbacks, overlap, then movement

**S:** finalizer `0x141ba99d0` establishes:

```text
final direction/range/speed/height and explicit-height policy (+0x548)
→ source-grid fields +0x338/+0x340
→ entity initial placement (0x141e31e90)
→ register action +0x310 observer / replace token +0x500
→ configuration and optional +0x568 / always +0x560 callbacks
→ optional model callback
→ birth-overlap query (0x141bb83c0)
→ if entity survives: install movement via +0x558
→ if overlap retained a target: invoke +0x4d0
```

The checked TS `+0x558 = 0x141a82c90` reaches shared straight/arc setup `0x141bacf60`.
Its secondary `+0x568` placement override is a skill case, not the universal origin rule.
Birth overlap and the later per-sample queries are both relevant to contacts.
See [motion and contact](motion-and-contact.md) for their clocks and observer semantics.
This chain establishes local execution; it does not establish a C2S contact-report contract.
The [finite composed witness](emission-admission.md#62-placement-changes-both-heights-and-potentially-the-aim)
now connects real layout/emitter outputs to original grid/height/range/speed preparation and the final `+558`
arguments. Observer registration uses supplied valid storage; presentation and birth queries remain fixtures.
The finalizer's post-query surviving-object gate is executed, while the movement wrapper/contact path is not.
