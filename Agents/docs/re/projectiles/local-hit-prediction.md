# Local attack entropy and spell avoidance (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md).
**S:** local contact acceptance has its own state and random-input contracts, separate from projectile layout,
contact history, continuation probabilities and [authoritative result processing](results-and-protocol.md).
This chapter closes a bounded entropy lifecycle, target-provider selection and creation-data transport.
The full stable-input accuracy expression has its own chapter; the official server's numerical hit resolver is separate.
Binary identity and retained evidence are in [the ledger](evidence-and-open-questions.md).

## 1. Concrete callers and bool direction

The final ranged Default Attack, KB, TS and BA tables bind `+0x570` to `0x141b6f950` and `+0x778` to
the false leaf `0x1400ed4a0`.
Their selected path is:

```text
141b6f950 -> 14193a8c0 -> 141ebcff0 -> 141ebc990
                                      -> callback 141ee4920 -> 141ebd1a0
```

`0x141b6f9ea` passes true as the projectile-classification argument, but `0x141b6f9f1` passes **false**
as the separate random-mode argument.
These are different arguments; the true classification byte does not select TLS sampling.
`0x14193a8c0` also carries the actual action sequence `+0x11a:u16` to the chance/entropy context.
The `+0x570` wrapper inverts the acceptance bool at `0x141b6fa00`: true at this outer slot means ignore/avoid.

The complete retained `0x141ebcff0` body first handles Chest, optional target-provider redirection through
stat `0x4b5e`, target never-hit `0x4a66` and target always-hit `0x4a65` shortcuts.
After a successful chance-builder result it checks target dodge-all `0x3cc7`.
An early return can bypass the entropy trial; geometric contact does not unconditionally consume its state.
The [ordered attack probability chapter](attack-hit-chance.md) now reconstructs the complete stable-input builder
and the finite nonnegative native power interface used here.

The ordinary Fireball final table `0x14333ac40` instead binds `+0x570` to `0x1419eee20`, with
`+0x770` bound to the same false leaf.
It selects stats through `0x14193b350` and calls `0x141ebd2f0` with target, sequence u16, derived byte u8,
stage byte u8 and an explicit **false** random-mode byte at `0x1419eee9f`.
It also inverts acceptance before returning.
Other concrete classes/overrides require their own binding checks.

The later [frame/query/contact join](motion-and-contact.md#9-joining-frame-queries-to-the-installed-ordinary-contact-and-history)
executes these final wrappers after actual ordinary capture and history admission. The deeper numerical acceptance
is supplied, so it checks argument transfer/inversion and subsequent remember/Pierce/stop behavior without claiming
a new entropy/probability computation or authoritative damage. Rejected later overlaps skip the prediction call.

## 2. Attack entropy belongs to the selected target Stats component

`0x141ee4920` loads a supplied f32 probability and tail-calls `0x141ebd1a0` with a context containing
pointers to the random-mode byte, selected target Stats component and action sequence.
The false-mode body uses these fields of that **component**, not of the stat provider or projectile:

| Offset | Native type | Meaning and lifecycle |
|---|---|---|
| `+0x08` | Entity pointer | selected component's owner; entity `+0x88:u32` enters initialization |
| `+0x28` | u32 | entropy accumulator, updated by each reached false-mode trial |
| `+0x2c` | f32 | remaining reset timer, set to exactly `3.0f` by each such trial |
| `+0x30` | u8 | nonzero requests initialization on the next such trial |

Both base Stats constructor `0x141e40a20` and client constructor `0x1421c2ad0` install accumulator zero,
timer `3.0f` and reset byte one.
The constructors write a word at `+0x30`; only the low reset byte is assigned this meaning here.
Client construction later installs its raw network provider at `+0x20/+0x40`; those are separate pointers.

Define `u32` as wrapping to 32 bits, `i32` as interpreting those bits as signed, and:

```text
ParkSample(seed):
    s = i32(u32(seed))
    q = truncTowardZero(s / 127773)
    r = s - q * 127773
    t = 16807 * r - 2836 * q
    if t < 0: t += 2147483647
    return t % 100
```

The retained native division is implemented through multiplication/shift sequences.
For a signed 32-bit seed this is algebraically
`((16807 * i32(u32(seed))) mod 2147483647) mod 100`, using a nonnegative mathematical modulo.
This is one step, not a library RNG initialized with unspecified normalization.
In particular zero and signed `2147483647` both produce zero; signed `-1` produces 40.

The false-mode entropy body is:

```text
increment = truncTowardZero(f32(probability * 100.0f))
if Stats.reset != 0:
    seed = u32(Stats.owner.entityId * 436 + u16(actionSequence))
    Stats.entropy = ParkSample(seed)
    Stats.reset = 0
Stats.timer = 3.0f
sum = u32(Stats.entropy + increment)
Stats.entropy = sum
if sum >= 100:                     // unsigned comparison
    Stats.entropy = u32(sum - 100) // subtract once, not arbitrary modulo normalization
    return true
return false
```

Probability is a boundary input to this entropy body; [its producer](attack-hit-chance.md) is documented separately.
Finite controls use the relevant f32 rounding/truncation.
The arbitrary transported-state controls do not establish that every u32 value is valid official state.
An unsuccessful trial still refreshes the timer.
Changing action sequence or attacker does not reinitialize an already active accumulator.
Several actions selecting the same target Stats component therefore share its current entropy state;
provider redirection can select a different component before this step.
The seed uses that component owner's ID, not a per-arrow object ID or the attacker's ID.

### 2.1 Original target, redirected provider and entropy owner are distinct

The complete outer attack body `0x141ebcff0` reads target `shaman_voodoo_pole_target_id` (`0x4b5e`)
from the original target's Stats provider.
For a nonzero value it obtains the original target's World through Entity `+0x78`, calls World `+0x190`,
then resolves that value as an object ID through `0x141ebefd0`.
If lookup returns null, the original Stats component remains selected.
If lookup succeeds, the referenced entity's Stats component replaces the selected component.
This is one lookup, not recursive redirection through the referenced provider's own `0x4b5e`.
The selected entity/component must satisfy the caller's component-availability premises.

The original target Entity pointer is **not** replaced:

| Consumer | Selected identity |
|---|---|
| chance-builder target Entity argument `R8` | original geometric target |
| chance-builder defensive provider argument `R9` | selected Stats component `+0x20` |
| outer never-hit, always-hit and post-success dodge-all | selected defensive provider |
| false-mode entropy accumulator, timer and reset byte | selected Stats component |
| initialization Entity ID | owner at selected component `+0x08`, then Entity `+0x88` |

Consequently the builder's target-position, Buff-list and Monster-component queries still use the original Entity,
while defensive stat reads and target entropy can belong to a different entity.
Do not collapse these identities into one target object in a later port.
The original Chest shortcut precedes selection; selected never-hit/always-hit can bypass the trial.
Selected dodge-all is checked after a successful builder result, so it can reject after entropy was already updated.

Spell avoidance `0x141ebd2f0` uses the corresponding one-step provider selection/fallback for dodge stats.
It still does not modify either entity's attack entropy; identity-derived samples remain a separate random family.
This establishes selection behavior given supplied fields and lookup outcomes, not the official lifetime/publication
of the redirection stat or successful component availability in every object class.

## 3. Component update and creation-data synchronization

Both base Stats table `0x1435a0af8` and client Stats table `0x1435dd4e8` bind component update `+0x40` to
the leaf `0x141e40bd0..0x141e40bf1`:

```text
if reset != 0: return
timer = f32(timer - callerDelta)
if timer <= 0.0f: reset = 1
```

Equality expires the timer; expiry preserves the old accumulator until a later trial overwrites it.
An already set reset byte skips countdown entirely.
These are finite-input rules; the COMISS/JB instruction also skips resetting on unordered comparison,
without establishing that NaN is a valid timer/delta input.
[World scheduling](world-scheduling.md) supplies the existing component-update invocation boundary.
The timer leaf does not establish a new fixed tick or its order relative to every contact in a frame.

Client table `+0x88 → 0x1421c2f40` first reads raw stat pairs and then **directly loads** this trailer:

| Wire order | Encoding | Store site | Destination |
|---|---|---|---|
| 1 | u32 little-endian | `0x1421c2fd3` | component entropy `+0x28` |
| 2 | f32 bits little-endian | `0x1421c2ff6` | component timer `+0x2c` |
| 3 | u8 | `0x1421c301a` | component reset `+0x30` |

It is nine bytes after the compact-count/pair prefix; with zero pairs the component requires ten bytes.
This is the formerly unnamed trailer in [entity creation](../../protocol/04-player-entity.md#41-component-1-0x1421c2f40-stats-like-10-bytes-minimal).
There is no byte swap on these direct loads, and they do not become raw stat key/value entries.
Creation data overwrites constructor defaults: reset zero continues the supplied accumulator/timer;
reset nonzero defers initialization to the next reached trial and its current action sequence.

**I:** this is an explicit C/S input for local contact prediction, not evidence that the client independently
chooses an unconstrained initial evasion roll.
Initialization bytes, later correction routes, actual update/contact ordering and official server state policy
remain separate from the closed reader/state contract.

### 3.1 Property updates are not an explicit entropy-state payload

The existing `0x1b3` property route has a different contract from the nine-byte creation trailer.
Reader `0x141fe1350` calls shared header reader `0x141f92000` for network-order u32 object ID at message `+0x10`,
u32 header field at `+0x14`, and u16 header field at `+0x18`, then decodes unsigned-key/signed-value pairs.
The latter two header fields are not constants merely because the current server sends zero.
This investigation does not assign them entropy/timer/reset semantics.

World handler `0x142187480` requires its existing `+0xb7a` gate and a successfully resolved object.
For the typed Stats branch at `0x142188162..0x1421881eb`, it resolves that object's Stats component,
loads the network provider at component `+0x40` and submits each pair to `0x1424cfaf0` in wire-vector order.
Its selected pair loop does not read header `+0x14/+0x18` or explicitly write component `+0x28/+0x2c/+0x30`.
Changing network evasion/accuracy inputs and supplying the entropy state are therefore distinct operations.
Getter fallback and provider notifications reuse the separate stat investigation's contract.
This bounded branch observation does not exclude indirect subscriber side effects or another correction route.

The previously documented lack of a Stats `0x2ba` update handler is reverified:
client Stats table `0x1435dd4e8 + 0xa0` points to exact `ret 0` leaf `0x1400f0f00`.
Creation `+0x88` and per-component update `+0x40` retain their distinct reader and timer bindings above.
No general absence of later entropy correction is inferred from these two selected routes.

## 4. Fireball spell avoidance uses identity-derived samples

The complete retained `0x141ebd2f0` body does not update the target entropy fields.
Chest and attacker stat `0x493f` pass before target-stat lookup.
After optional target-provider redirection through `0x4b5e`, target dodge-all `0x3cc7` fails.
Otherwise it computes:

```text
effectiveDodge = truncTowardZero(i32(u32((100 - attacker[0xa24]) * target[0x346])) / 100)
if effectiveDodge <= 0: return true
firstSeed  = u32((u16(sequence) << 16) | (u8(derived) << 8) | u8(stage))
secondSeed = u32((u8(derived) << 16) | (u16(sequence) << 8) | u8(stage))
first = ParkSample(firstSeed)
extra = clampSigned(target[0x14f8], -1, +1)
if extra == 0: draw = first
else:
    second = ParkSample(secondSeed)
    draw = min(first, second) if extra > 0 else max(first, second)
return draw >= effectiveDodge
```

Subtraction/multiplication use native wrapping 32-bit arithmetic; the effective chance is not capped to 100 here.
`extra` clamps magnitude, so a value of five still causes only one additional sample.
The second expression retains the whole u16 sequence: its high byte overlaps the shifted derived byte through OR.
It is not a permutation of three nonoverlapping bytes, and the two draws need not be independent.
For sequence/derived/stage all one both seeds equal `0x10101` and both samples equal 51.
For `(0x1234,0x56,7)`, first seed `0x12345607` samples 51, second seed `0x563407` samples 37.
Neither deterministic recipe includes owner or target ID.

A true random-mode byte instead rejection-samples seven-bit TLS values until each is below 100.
The entropy helper's true mode compares `trunc(f32(probability*100f)) > draw` without touching component state;
the spell helper uses one/two such samples with the same extra-roll selection rule above.
These alternative branches do not alter the explicit false modes bound to the selected ordinary witnesses.
The [TLS lifecycle](random-inputs.md) and actual execution-thread order remain independent obligations.

## 5. Evidence, reuse and remaining scope

Round31 `gate-audit.json` retains fourteen functions/leaves, twenty code ranges, 1195 instructions,
fourteen concrete bindings and seventeen anchors against the pinned executable.
`gate-paths.json` records 3270 offline interpreted controls / 399944 steps / 372 covered instruction sites.
They cover entropy state/initialization, rejected TLS samples, finite timer expiry, successful creation trailers,
spell identity/extra rolls, wrapping modifiers, shortcut precedence and a same-memory creation/trials/expiry/reseed sequence.
Component/stat/Chest getters, empty compact-count prefixes and seven-bit sample calls are explicit boundary fixtures.
Redirected-target lookup, full accuracy probability computation and failed I/O are not interpreted by those controls.

`gate-review.json` independently rereads all retained ranges/bindings with direct pefile/Capstone,
checks 27 additional anchors and compares a separately written signed quotient/remainder expression over 1033 seeds.
Eight literal spell paths and an entropy/miss/expiry sequence reuse the disclosed instruction interpreter;
they are not independent native execution or gameplay validation.

This extends the earlier spell recipe and attack call-chain research with reproducible arithmetic, component
lifecycle and positively identified creation transport; those older findings are reused rather than claimed anew.
Accepted-result numerical/resource processing remains the separate damage investigation's responsibility.
At R31, fixed R15 `258f79c2f367fefcced6b8b9c93d5aa8f827efc4` supplied formula and clock classifications;
R16 drafts were not fixed evidence. The later [attack probability cut](attack-hit-chance.md) pins published R16,
without duplicating its granted-value/Hit-listener work.

Round33 `sync-audit.json` adds the complete selected outer gates, property header/reader and World dispatcher:
sixteen functions/leaves, eighteen code ranges and 3160 instructions, including reused probability/power bodies.
The dispatcher's 508 bytes after its return are table data, excluded from instruction counts.
`sync-paths.json` records 54 offline cases / 13773 steps / 1024 visited sites: redirect/fallback and selected
shortcuts, full builder-to-entropy, separate spell gates, property dispatch and successful fixed-header/pair decoding.
Object/component/type lookup, stat getters, pair setter/notifications and compact primitives are fixtures;
reader controls use preallocated empty vectors and do not prove growth, transitive notifications, pool reuse or failed I/O.
The pinned R32 interpreter gains read-only entry observations and the dispatcher's sign-extension instruction.
Fresh independent PE/Capstone review rereads every range/binding/constant and checks 21 further semantic anchors.

The stable-input probability expression and finite native power call are now mapped separately.
Still open: remaining actual state producers/resynchronization; real supplied entropy/timer/reset values; target-provider and cross-world
update/contact ordering; further classes/overrides; official server result selection and numerical hit application.
No server implementation, client execution/attach, official capture or CI change accompanies this chapter.
