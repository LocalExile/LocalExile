# Tornado Shot: concrete client stages (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md).
Evidence is bounded static inspection of the pinned executable, not a new game test or server change.
The earlier [secondary geometry reconstruction](tornado-geometry.md)
contains the eight-word context, ordinary equal-circle layout and coordinate quantization.
This chapter closes the concrete client factory and wind creation paths omitted from that base-class study.

## 1. Installed implementation

Client factory `0x1412d4890..0x1412d4aa0` allocates `0x550` bytes,
calls shared TS constructor `0x141a82240`, then installs vtable `0x143423898`.
The shared constructor installs `0x1434e3638`; stopping at that table misses the presentation overrides.
The client factory also constructs helper state at `+0x540` through `0x141378630`.

| Slot | Concrete client target | Bounded role |
|---|---|---|
| `+0x248` | `0x1414fbfa0` | forwards to the `+0x540` helper |
| `+0x558` | `0x1414fc170` | mover wrapper; then shared `0x141bacf60` and helper update |
| `+0x560` | `0x1414fc020` | primary local entity attachment/effect path |
| `+0x568` | `0x141a82bd0` | ordinary secondary origin correction to saved mother grid/height |
| `+0x590` | `0x1414fbd90` | shared secondary generation first, then primary wind creation |
| `+0x750` | `0x1414fc2d0` | shared `0x141bb4680`, plus behavior-variation-1 wind path |

Shared ctor sets `+0x530` from stat `0x1921 == 1` and chooses `+0x330` as 1 or 5.
Ordinary generated secondaries separately force `330=1,530=1` without changing that stat to 1.
Those two ways of entering secondary state are not interchangeable.
Final `+6b0` forces radial admission and `+6b8` returns zero for `530 != 0`, without ordinary stat reads;
for primary state they retain the shared getters. The complete first-RET leaf fallback and dispatch priority
are in [layout stat inputs](layout-stat-inputs.md#5-tornado-shots-primary-and-secondary-overrides).
An enabled nonempty random-direction vector still takes priority over radial in the shared dispatcher.
Client `+0x538` selects a presentation variant derived from the checked count/stat branch.
Its producer and copy behavior are given below; it is neither a network-mode flag nor the behavior variation itself.

## 2. Ordinary terminal: child generation then wind

`0x1414fbd90` calls shared TS `0x141a827c0` before its own presentation work.
For a primary (`+0x530 == 0`), it reads the current projectile's Positioned grid XY,
gets its effective height marker through `0x141e35ae0`, and calls `0x1414fc340` to create a wind effect.
That getter uses `Positioned+0x290` unless it is `INT_MAX`, then terrain-samples according to metadata `+0x22`.
This wind call occurs before the subsequent test of the third argument's byte.
The later conditional attachment effect must not be conflated with the wind object itself.
The shared callback's ordinary-secondary generation uses the already reconstructed per-group RNG and allocator.
Both child generation and this wind are client-executable paths.

Generated children first inherit the mother's selected Projectiles row in common copy, then set `530=1`
and call final high-level release at `0x141a82b3d`.
That release unconditionally refreshes the row through `0x141ba4946 → 0x141bb66a0 → +0x510`,
so the final TS selector chooses child association slot 1 before emission.
Count/origin preparation refreshes it again before count admission.
The intermediate `0x141bade00` call is an existing-flight subscription helper, not cache invalidation;
this new clone has no flight object yet and takes its early null return.
See the [selected-row lifetime](motion-speed-and-time.md#14-selected-row-lifetime-across-release-ts-children-and-return)
for the different low-level Return/redirect paths and their speed recomputation.

The `+0x750` override is a second caller of the same wind function:
after `0x141bb4680`, when virtual `+0x310()` returns 1, it terrain-samples stored XY at `action+0x384`,
adds six height-marker units and creates the wind there.
The concrete `+0x310 → 0x1419397d0` reads stat `0x1921`, `skill_behaviour_variation`.
Its checked caller is the separate `+0x374` scheduled-release state machine, described below.
Do not count them as two guaranteed winds per shot or mistake that variation selector for a network mode.

### 2.1 Joining final completion to actual child construction and release

The bounded final-to-child composition now interprets the original client callback `0x1414fbd90`,
shared generator `0x141a827c0`, installed copy `0x1414fbc30`, shared TS copy `0x141a82340`,
common copy chain, derived registration `0x14193daf0`, selected-row refresh, count override,
high-level release and radial emitter `0x141bb9500` in one supplied state.
It reaches each child's generic placement and the **input boundary** of client mover `0x1414fc170`.
This joins previously separate contracts; it does not newly execute terminal admission, the TS-specific
placement callbacks, that mover's body, the full frame or wind rendering.

| Transition | Recovered rule |
|---|---|
| Mother to intermediate template | Registration requests `u8(mother.derived+1)` without consuming the shared allocator; secondary state becomes `330=1,530=1` |
| Common copy | Preserves the shared identity allocator and sequence/stage; clears target-history pointers `438/440` and completed byte `332` |
| Group angle | Original initializer/sampler use `[sequence,motherDerived,ownerId,repeatDword,subReleaseByte,0,0,0]`; the intermediate or flying-child identity does not replace `motherDerived` |
| Child row and count | Secondary association slot 1 is selected twice before emission in these controls; native count/release and radial dispatch execute |
| Per-arrow construction | Each emitted arrow consumes the shared allocator; a template identity can equal a flying child's byte |
| Default history (`can_shotgun`, stat `0xb2b`, zero) | Children of one mother share a fresh group history; it does not include the mother's remembered targets |
| Nonzero `can_shotgun` | Each emitted child receives its own fresh history |
| Template retirement | The template retires after release, without retiring the flying child actions |
| Returning mother | Copy preserves returning byte `461`, while clearing completed byte `332`; final callback still generates children before invoking the wind boundary |
| Secondary final callback | `530=1` prevents another ordinary child group and primary wind |

For one supplied example, mother `derived=2,stage=9,sourceOrdinal=4,releaseOrdinal=1` and allocator low byte 2
produce template byte 3, then child bytes 3/4/5, source ordinals 4/5/6 and release ordinal 2.
Their three histories share one fresh object by default. The mother's `[101,202]` history remains unchanged.
With mother byte 255 and allocator low byte 255, the intermediate byte wraps to 0, while the shared allocator
emits 2/3/4 and advances its high byte. These are distinct identity operations, not one generic increment.

Two supplied mothers completing in opposite orders still consume the same consecutive child identity range,
but assign it to different mothers. Each group's angle retains that mother's seed byte; per-child identities
and object-seed inputs depend on the completion order. An implementation therefore needs both the recovered
arithmetic and the appropriate execution order; independent per-mother identity counters are not equivalent.

Twenty-eight controls cover counts 0/1/2/3/5/8 with both history modes, identity/stage/sequence wrapping,
two-mother order, repeat/subrelease inputs, completed returning mothers, secondary nonrecursion and the
three nearby-query cases below. They interpret 479,423 original instructions at 4,219 distinct sites.
The merged audit includes inherited release/copy ranges; its site count is not a count of newly discovered code.
Parent actions, selected providers, model/resource/terrain/storage inputs, activation, area cache and empty
birth contacts remain explicit fixtures. Wind is observed at its call boundary. No authoritative damage,
official server scheduling, actual first-update input or complete child trajectory is established here.

### 2.2 Nearby target selection does not yet establish child aiming

The generator optionally calls `0x141a3b440`, whose bucket visitor `0x141a3b660` can write an accepted
Entity ID into the intermediate template's `+0x520` at `0x141a3b868`.
Its query uses selected Projectiles row DWORD `+0x3d` plus one and a current-cell layer gate.
The retained filters include rank/layer/tag, body-inclusive Chebyshev range, mover/state bytes,
owner exclusion, live Stats and Actor components, and a relationship-table rejection.
It stops on an accepted candidate; this is not evidence of a global nearest-target search.

Three controls execute the actual query and visitor with supplied buckets/components/layers/relationships:
an enemy at the parent grid selects ID 101, while an empty query and a rejected friendly leave zero.
All three nevertheless produce identical group angle and emitted aim/identity/object-seed inputs in this
composition. Actual child copy clears `520` at `0x141a8239f`.
Most other controls explicitly bypass query enumeration with row `+0x3d=0xfffffffe` (incrementing to -1).
The follow-up below executes TS placement and movement through native straight initialization. Remaining
target-reference callbacks, actual inputs and later lifetime still prevent a universal homing conclusion.

### 2.3 Joining the dedicated origin callback to actual straight movement

The follow-up composition replaces the preceding TS placement/mover boundaries with original installed
`+560`, `+568` and `+558` bodies, the valid in-bounds Positioned setter `0x141e31200`, shared mover,
straight specification setter and integer initialization. It retains the R76 generator/copy/release chain.
Spatial membership/listener APIs, empty birth contacts, selected inputs/resources and the attachment helper
remain supplied; this is not full animation replay, child lifetime or authoritative server execution.

`+568 → 0x141a82bd0` returns without correction for primary state or behavior variation 1.
For nonzero secondary state with another variation, it overwrites the flight's height marker with saved
mother height `52c`, writes world Z as `f32(float(-height)*7.8125f)`, and calls the actual Positioned setter
using saved mother grid `524/528` and its cell-center world XY.
Thus generic placement is followed by a **second placement at the mother's saved grid**; generic origin alone
is not the actual ordinary child origin. The supplied Position component has the native table and an initially
unregistered spatial-cache sentinel. Its real setter performs bounds checks and commits XY/world XY; spatial
registration, position listeners and presentation remain boundaries.

Client `+558 → 0x1414fc170` preserves the passed aim. For the checked straight route it substitutes saved
mother height for the incoming target height only when **all** of these hold:

- the movement's supplied initial/phase byte is nonzero;
- secondary byte `530` is nonzero and a flight object exists;
- executed Chain `458` and Fork `45c`, byte `460`, and returning byte `461` are all zero.

The wrapper also sets `3bd` for an initial secondary, independently of whether those counters prevent
height substitution. Shared movement can still apply its own effective-stat height/trajectory rules.
Do not infer a universal flat-flight rule for all later redirects or rename byte `460` from this test alone.
The `531/534` animation-replay branch remains a separate supplied-input/presentation boundary.

Forty-six controls cover generated child groups at three mother heights/counts, native placement versus
primary/variation-1 skips, the initial/secondary/counter gates, absent flights, inherited continuation states
and nearby query. They interpret 373,959 original instructions at 5,032 sites. The generated ordinary groups
now reach actual straight initialization with start XY/height equal to the saved mother state. This joins
existing [straight-motion rules](motion-and-contact.md#8-joining-emission-to-installed-straight-motion-and-frame-advancement)
without claiming new frame samples or actual model/terrain inputs.

The empty/enemy/rejected-friendly query controls remain identical **through the resulting straight-move
start/end/height/speed**, although only the enemy writes template `520` and every emitted child clears it.
No aiming effect appears in this supplied chain. The bounded shared/client TS method audit records direct
field references; it does not exhaust indirect consumers, future updates or presentation callbacks and is not
a proof of global absence of targeting. Actual configuration, remaining callbacks and descendant lifetime
still need evidence before assigning a general homing rule.

### 2.4 Generated children through native frames and terminal history release

The next composition takes the **same children produced above**, rather than separately supplied flight
actions, through `0x141e32710`, integer samples, actual empty-bucket spatial queries, installed Positioned
notifications, shared terminal `0x141ba68c0`, the TS reason/stage callback and final `0x1414fbd90`.
It supplies clearance/height data, empty spatial buckets, native-table Actor/World records and an empty result
queue. Model/resource/stat/cache/component inputs, authored event listeners, storage and retirement delivery
remain explicit boundaries. This is not a target-contact, continuation or authoritative damage trace.

Fourteen controls cover one/three/five generated children, shared versus shotgun history, both supplied World
getter modes and a two-update sequence with inherited Return state. They interpret 1,755,283 original
instructions at 6,170 sites. Under the supplied geometry some paths reach their last sample and others stop
at bounds; both dispatch the actual installed terminal route. Every child advances stage 9 to 10, sets its
completed/retired state and clears its own `438/440` history handles. No secondary regenerates children or a
primary wind: the group's only wind remains the one emitted by the original mother final callback.
The initial `.001` update leaves these children incomplete; the later update completes them. These elapsed
values exercise native sampling/catch-up and are not measured frame cadence or official scheduling.

With ordinary shared history, the generated control block starts with one strong reference per child and
decrements once per child completion. Only the last release calls the installed payload destructor
`0x141a8baa0`, then reaches zero weak references and the control-storage deletion boundary. Shotgun children
instead hold distinct blocks and each takes that last-owner route. Native reference-count operations and
payload destructor execute; vector/control allocators remain storage APIs, with empty reservation vectors.
The mother and sibling identities are unchanged by these terminal updates.

The supplied absent flight-model path also executes the actual terminal-animation wrappers
`0x141baa850 / 0x141baaa50`. They are presentation helpers, not Entity deletion. For the checked secondary
blocked-final branch, an absent optional resolved source falls back to Actor's actual `+148` leaf
`0x141651830`, which returns the embedded record at `Actor+598`; its supplied kind is zero, so the special
kind-14 presentation call is skipped. No complete renderer is reconstructed.

After these controls the retired actions still retain their flight pointer `328`. Actual retirement delivery,
Entity unlinking/storage reclamation and full descendant continuations remain open. The following composition
adds nonempty target contacts and selected Pierce/Return paths on the same generated children.
Thus terminal completion, releasing contact history and destroying a flying Entity are separate contracts;
this composition does not justify treating completion as a universal immediate object-removal event.

### 2.5 Generated children through nonempty contacts, shared history and Return

This follow-up retains the final-to-child generation, origin correction and mover from sections 2.1--2.4.
Targets are supplied after generation, in native 23-cell spatial buckets; the original frame, query, capture,
contact, history, reason/stage and final bodies execute. Birth contacts remain the earlier empty fixture.
The numerical hit predictor is an explicit accepted/rejected API input, not authoritative damage.

For supplied one/three/five-child groups and one overlapping target 101:

| Input | Native contact consequence |
|---|---|
| Default shared history, accepted prediction | Only the first updated sibling reaches prediction, records 101 and ends at that contact. Later siblings reject it through shared history and continue flying. |
| Default shared history, rejected prediction | The first prediction still records 101, but the flight continues; other siblings do not retry prediction in that phase. |
| Nonzero `projectiles_can_shotgun` | Each generated child has its own history and independently reaches prediction for 101, with accepted/rejected consequences as above. |
| Reversed sibling frame order | The last emitted sibling becomes the first admitted one with shared history; independent histories admit siblings in the reversed order. |

The frame order here is deliberately supplied, not a recovered official scheduling order. The common history
is not a damage receipt: even the rejected local prediction can suppress another same-phase attempt.
All children subsequently complete through their installed callbacks, and the last history owner reaches the
original destructor. Its entry receives the control block; the actual history payload is `control+10`.
Snapshots are taken before destruction, rather than interpreting retained interpreter storage as a live object.

Two co-located target records, in explicit encounter order 101 then 202, exercise finite Pierce independently
of geometry. One Pierce grant produces reason sequence `2,0`, records both targets and advances TS stage
9 to 11; zero budget or `virtual_projectiles_cannot_pierce` stops at 101 with reason 0 and stage 10.

With a supplied effective Return grant, an actually generated secondary enters the real Return executor,
reconfigures the same flight and keeps its history. This is a capability test, **not** evidence that official TS
secondary stats grant Return. The combination with one Pierce gives a more discriminating sequence:

1. Outbound 101 pierces, then 202 starts Return: reasons `2,7`, outbound history `[101,202]`, return history `[202]`.
2. The returning path admits 101 again. Without extra Return, reason 0 ends the action; both phase histories
   become `[101,202]`, with TS stage 12 and no extra child group or wind.
3. With supplied extra-Return chance 100, that contact instead enters reason 7 and the extra phase remembers
   `[101]`. Target 202 can then be admitted in the extra phase before final completion. The cached extra chance
   is drawn once, not again for the second Return.

`skill_projectiles_cannot_return` selects final before the Return executor/draw. A separate one-target Return
control confirms that its turnaround target is already recorded in the new phase and is not predicted again.
These grants and frame deltas are explicit test inputs, not official grant publication or measured cadence.

The composition also removes R52/R53's weak-handle substitute for this chain. Original `141ebdd20` assigns the
real Positioned handle, `141ebde10` locates/unsubscribes its observer, and `140146560` removes the actual inline
record through table-bound size/type/destructor callbacks. This is necessary when multiple siblings contact
one live target. Original null assignment removes the pending subscription before clearing the handle; merely
zeroing the pointer left a stale observer in an early fixture and was corrected. Inline storage is sufficient
in these controls; dynamic observer storage remains outside them.

Twenty-one controls interpret 1,528,355 original instructions at 7,660 sites. New bounded audits cover the
weak assignment/removal bodies and actual table leaves; prior query/continuation arithmetic is reused rather
than rediscovered. History-vector growth is a labelled insertion/storage API, with original membership/order/
phase selection and in-capacity insertion still executed. Target/component/terrain/stat/resource inputs,
local prediction, empty authoritative-result queues, storage and retirement delivery remain boundaries.
No authoritative damage, official TS secondary grants, complete Entity unlinking or running-client observation
is established. Evidence: ignored `round79/check_ts_contacts.py`, its controls and native audit.

### 2.6 Generated children through Fork, Split and Chain

The next composition starts with the same actual final-to-child generator and nonempty contact path.
It executes native Fork/Split emitters, common/client/TS copies, history sharing or deep copying, descendant
movement initialization, and Chain query/ranking/reservation/redirect bodies. Twenty-eight finite controls
cover the combinations below. Effective grants, initialized targets/components,
path/terrain/resource/model inputs and frame order remain supplied; these are capabilities, not official TS
secondary grants, observed scheduling or authoritative damage results.

For one generated secondary contacting target 101, a supplied final Fork budget produces reason 4 and two
descendants, or three with the checked `2fd7=100` trident input. The source increments generation before copy;
descendants retain that generation, shared identity allocator, sequence and advanced stage. Budget two permits
another generation at target 202; it does not reset the inherited consumed generation. A zero effective Fork
budget takes ordinary final instead. Fork precedes terminal Chain in the combined-grant control.

Without shotgun, Fork descendants share the source's history. With nonzero shotgun, each gets an actual deep
copy of all three processed-target vectors; the trigger 101 remains remembered. On the supplied later contact
with 202, only one ordinary sibling reaches prediction, whereas the independent copies each do. Independent
history therefore does not erase ancestor contacts. History allocation/copy and vector element copying execute
original bodies; allocation and byte-copy storage primitives remain explicit APIs.

Supplied Split counts one and three execute reason 1, the original candidate wrapper/sorter, target-first
selection and random direction fill for missing slots. With candidate 202 at grid `(84,80)`, the first emitted
aim uses that exact grid; remaining slots use the existing fallback-angle contract. The emitter remembers
the original trigger 101, not the aim candidate. Descendants inherit the already-Split byte and shared/copied
history policy. Cannot-Split rejects the supplied count, and contact Split precedes a remaining Pierce budget
without spending that budget. Neither Fork nor Split re-enters the initial TS mother-origin correction:
the descendant movement calls have initial=false. No additional primary wind or recursive TS child group is
created by these secondary continuation paths.

Fork and Split mark their source retired without taking reason-zero final: the checked source remains
completed=false and retains its history reference. This composition does not execute its later retirement
delivery, destruction or Entity unlinking. Do not transfer the immediate final-history release contract to
these branches, or infer a leak from the bounded trace.

Chain keeps the action, derived identity, flight Entity and history, increments the executed-Chain counter,
and installs replacement movement. The reservation key is the derived byte. Two actually generated siblings
with shared history and two equal-position candidates select 303 then 404; independent histories select 303
twice. Reversing candidate encounter order reverses the initial choice. Between the two updates, the supplied
bucket replaces contact 101 with contact 202 while retaining the candidates; this controlled mutation is not
an assertion about official update order. Native filtering/ranking and reservations make the choices.

Reservation growth/replacement, allocation headers, later removal and history destruction execute original
`0x140ce8a40 / 0x140147570` and destructor bodies. Raw aligned allocation/free and ordinary 16-byte candidate
vector growth remain storage APIs. A failed exploratory bare-vector fixture lacked the native header and
failed during destruction; it was replaced by those original growth bodies, not a skipped destructor.
The allocation-accounting global is explicitly initialized to zero and locked additions are interpreted
serially; this does not establish concurrent allocator behavior or live accounting state.
The single-action Chain controls also distinguish query inclusion from accepted contact: with supplied
source hull bytes `1e3=254,1e4=0` (the first is signed -2), target body two reaches the extra Chebyshev check
and is rejected, while body six permits target 202 to reach prediction on the subsequent frame. This is a finite initialized-hull
case inherited from the supplied row sentinel, not a normal skill-hull calibration. Two additional controls
supply equal nonnegative effective hull pairs `(0,0)` and `(1,1)` after actual flight construction; target body
two then reaches prediction. These overrides are explicit component-state inputs, not evidence for native
metadata publication or authoritative hit geometry.

The TS secondary-copy animation-state read is supplied separately; the descendant replay target and an
optional reason-5 component are absent inputs. Rendering is not reconstructed. Actual effective-stat
publication/cache timing, other continuation combinations, deferred retirement/Entity lifetime, TLS/World
ordering and official result routing remain open.

### 2.7 Retired Fork/Split sources through Actor cleanup and history ownership

The next composition retains the actual generated secondary, its nonempty contact and Fork/Split descendants.
It enters the original post-root Actor snapshot region with declared registers/delta, then executes the installed
`Actor+0xf8 -> 0x141e930e0` removal, completion publisher and TS deleting receiver
`table 0x143423898[0] -> 0x1413754b0 -> 0x141ba27c0 -> 0x141240480 -> 0x14193a460`.
This is a bounded cleanup join, not a full World tick or an Entity-manager drain.

Sixteen controls cover Fork two/three descendants, Split one/three, shared/independent histories and completion
listener reentry. They interpret 1,841,542 original instructions at 8,175 sites. The retained snapshot contains
the already-retired intermediate child template, the retired source and its live descendants. Actual removal
compacts the retained vector before the source completion listener runs. The listener sees latch `+126=1`,
no retained source entry and the still-attached flight Entity. Reentering the publisher does not notify twice;
the later base destructor calls that same latched publisher without repeating the listener.

The shared projectile destructor removes only its matching flight observers at Entity `+148` and `+c8`.
An explicitly supplied unrelated observer remains installed. It calls `0x141ebe680` at `0x141ba29bf` to request
deferred flight removal, then clears action `+328`. The same original queue request remains idempotent.
The source's Entity is queued while its descendants remain live; deleting the source action is not deleting
its flight Entity synchronously. The deleting receiver reaches the storage-free API with size `0x550`,
`+328=0` and the completion-published latch set. State is sampled before that storage boundary, not afterward.

There are two distinct history operations: `0x141bae1d0` removes this derived byte's Chain reservation;
it does **not** decrement the history control's strong count. The decrement is the later original locked
`xadd` at `0x141ba2ab9`, with payload destruction and weak/storage release only for the final holder.
For default shared history and N descendants, source cleanup changes strong count from N+1 to N and leaves
the descendants' remembered trigger 101 intact. For shotgun copies the source goes from one to zero and
its independent history is destroyed while descendants keep their own copies. Completing those actual
descendants through native frames/final releases the remaining histories. Their later Actor destruction
finds the history handles already cleared and does not release them again. Final histories retain `[101]`;
no recursive TS child group or extra wind is generated by this cleanup.

The Actor update also frees the retired intermediate template. A later snapshot does not remove or delete
the source/template again. After descendant completion and Actor cleanup, the retained vector is empty and
the deferred queue contains source and descendant flight Entities in the supplied processing order.
The queue is deliberately **not drained** here: populated component teardown, actual World scheduling,
spatial detachment and Entity storage lifetime remain separate. The earlier
[Entity queue contract](fireball-and-kinetic-blast.md#33-deferred-target-removal-and-action-cleanup-are-separate-events)
is reused without replacing the generated flights with imaginary empty components.

Effective grants, target/component/terrain/model/resource inputs, explicitly initialized flight event containers,
empty authoritative result queues, the Actor post-root entry and update order remain supplied.
An absent rendering model and resource/container/allocation/free APIs are disclosed boundaries. This is
original-instruction offline interpretation, not native gameplay, official scheduling or authoritative damage.
It closes the source's retained ownership/history-release cut left by section 2.6; it does not close every
owner/transfer/cancellation path. Evidence: ignored `round81/check_ts_retirement.py`, its controls,
raw range/table audit, fresh replay and resource-limit records.

## 3. Wind object, position and randomness

`0x1414fc340..0x1414fc57e` loads UTF-16 `Metadata/Effects/Effect` at `0x143261720`.
It allocates a `0x248` local object through `0x141ebdf30` and registers it with the owner's world.
It resolves the animated resource through `0x141940f60(action,out,resourceSlot,1)` and applies it through `0x141e55a30`.
The requested resource slot is `bool(action+0x538)`; the factory computes and copies it as described below.
The resource helper reads the action's effect/stat selection chain through `+0x380`;
this is more specific than a fixed server-selected effect path, but does not prove every MTX combination.

The helper initializes an independent eight-word effect context:
`[sequence11a, derived11c, owner88, requestedResourceSlot, 0, 0, 0, 0]`.
That resource randomness is separate from the following explicit wind-rotation calculation.

Wind rotation directly updates the same TLS LCG family described in [random inputs](random-inputs.md):

```text
tls = u32(tls*0x41c64e6d+0x3039)
j = (tls>>16)&0x7fff
rotation = f32(f32(float(j)/32767.0f)*6.2831854820251465f)
```

The actual constants are at `0x1436a4824` and `0x1436a3674`.
This differs from the generic ten-bit metadata-orientation conversion.
It does not affect the previously reconstructed deterministic secondary-group angle by itself.
This formula is input to the orientation setter, not a measurement of the final stored/rendered rotation.
The [bounded wind observer](capture-contract.md#26-inline-tornado-shot-wind-randomness) captures this inline
draw independently of TLS helper calls, with exact terminal/scheduled caller labels and partial backtraces.
CPU and memory states are separate observations; no client capture, outer TLS envelope or cross-call
continuity is established by its offline validation.

Provided integer XY becomes world cell center `f32((float(cell)+0.5f)*10.8695650100708f)`.
The supplied height marker is stored at Positioned `+0x290`;
its world Z at `+0x2b4` is `f32(float(-heightMarker)*7.8125f)`.
The terminal path uses the projectile's current height; the other checked path uses terrain height plus six.
Do not apply a universal zero-Z or guessed bow-muzzle height to these distinct sources.

## 4. Primary/secondary continuation and true completion

Final table `0x143423898` uses shared `+0x638` Chain budget, `+0x640` Fork budget,
`+0x678` Pierce qualification, `+0x698/+0x518` Return qualification/execution.
Special `+0x680/+0x688/+0x690` hooks return false.
Reason callback `+0x530 → 0x1412e34e0` advances result stage; it does not create ordinary secondaries.
Primary/secondary state `action+0x530` and virtual-table slot `+0x530` are distinct uses of the same numeric offset.

Ordinary primary state is `action330=5, action530=0`.
After mode/cannot-pierce/chance-not-pierce early rejection, shared Pierce sees mask `action330 & 0x4` and returns true
before consuming finite Pierce budget `44c`.
Consequently an ordinary primary normally continues through enemies before terminal Fork/Chain can run.
Split is considered **before** Pierce, so that flag does not prevent primary Split.
Ordinary secondaries have `330=1,530=1`, lack this forced-Pierce bit and use the shared applicable modifiers.
Copying preserves both state fields.

The final class does not hard-disable primary Fork/Chain/Return.
Fork requires a retained target and remaining generation budget; a usual pierced enemy or targetless range end
does not provide that stop.
Shared Chain rejects targetless natural end, while a blocked terrain stop has its own probability gate.
Do not replace these priorities with a universal primary-mechanic ban.

Successful Return in `0x141ba6e00..0x141ba6e51` dispatches reason 7, redirects via `+0x518`, then exits terminal.
It does **not** call final `+0x590` at that turnaround.
Successful Split/Fork/Chain likewise take their own continuation/retirement path before ordinary finalization.
True final completion at `0x141ba6e56..0x141ba6e9a` dispatches reason 0 and invokes `+0x590`.
If a returning primary finally reaches it, `action530=0` still permits children then wind;
the checked callbacks do not suppress these solely because returning flag `461` is set.
Generated secondary `530=1` produces neither another ordinary secondary group nor the primary wind there.
Later successful continuations can change the true final location; static reachability does not guarantee visibility.

### 4.1 Separate scheduled release and secondary stored aim

The checked direct caller of `+0x750` is `0x141ba8fab` inside update helper `0x141ba8d20`.
Its `374` state machine skips 0/4, decrements positive timer `378`, advances state 1 to 2 with a 0.25-second timer,
then advances 2 to 3 and calls `750`; state 3 later advances to 4 and retires.
This is a separate scheduling path, not the Return reason-7 callback or a guaranteed second wind.
Its producer is the independent `projectiles_rain` release path, not ordinary primary completion.
The exact gate, clone state and timing are described next.

Ordinary secondary template `0x141a82a16..0x141a82a70` writes a stored aim from primary grid `P` and group angle:

```text
Q.y = P.y + trunc(f32(-cos(theta)*120.0f))
Q.x = P.x - trunc(f32( sin(theta)*-120.0f))
clone130/134 = Q
```

Subsequent common release may modify this aim.
It is not simply the source contact grid; the shared Chain selector later ranks using stored aim coordinates.
See [continuation selection](continuation-and-memory.md#6-chain-target-selection-and-sibling-coordination).

### 4.2 Scheduled-rain producer, cloned state and timing

Release `+0x4b8 → 0x141ba4930` checks rain gate `+0x4a0`.
TS binds it to `0x141a82730`: variation zero with `action530!=0` rejects;
otherwise it reads `projectiles_rain` (`0x29d4`).
Ordinary generated secondaries are thereby excluded from recursively arranging this rain path.
With rain enabled and `374==0`, release validates/prepares aim, clones through `+0x450`,
allocates a fresh derived byte, registers through `0x14193daf0` at `0x141ba4b83`,
then writes **the clone's** `374=1` at `0x141ba4b8e`.
It stores the `+0x748` delay in `378` and aim `130/134` in `384`, prepares presentation,
then calls `+0x538`, which invokes owner Actor `+0x1f0` before a separate tail helper.
The checked base/shared/client Actor tables bind that Actor hook to a void no-op;
registration has already occurred in `0x14193daf0`, whose tail appends to the source Actor's retained vector.
An already-scheduled nonzero state does not create another rain clone there.

Shared construction and copy clear `374/378/384`; a clone does not inherit the source rain phase.
The copy's qword-zero at `+0x370` covers `374`, rather than an explicit isolated dword write.
TS `+0x748 → 0x141b6f020` gives, for finite inputs and operation-by-operation float32:

```text
p = max(stat3952, -99)                  # arrow_rain_fall_speed_+%
c = clamp(1/(1+p/100), 0, 2.4f)
initialDelay = max(.6f*c, (.4f*get788())*get770())
```

That initial delay is distinct from the subsequent 0.25-second state-1 delay.
When timer is positive, update subtracts frame delta and returns even if it crosses zero;
phase transition occurs on a subsequent update, without multi-phase catch-up in one call.
State 2 calls `750` after first setting state 3; state 3 retires on a later update.
`750 → 0x141bb4680 → 0x141ba51a0` releases the payload without re-entering the `4b8` rain-clone branch.
The checked Return paths neither set state 1 nor call `750` as their turnaround callback.

Supporting extracted rows associate `TornadoShotAltX` with **Tornado Shot of Cloudburst**,
`SkillID=1`, and implicit `projectiles_rain`/`projectiles_nova`; ordinary TS has no rain flag.
The following producer trace connects those authored fields to stat contributions;
the rain/variation-1 interpretation no longer relies solely on asset-name agreement.
[Actual clone insertion and mode scheduling](world-scheduling.md) are now connected through the retained snapshot.
Remote-owner creation coverage, cancellation, indirect state writers and live ordering remain open.

### 4.3 Base behavior-variation and rain-stat producers

Skill-stat builder `0x141ee9090..0x141ee9465` copies its input's `+0x20` per-level wrapper.
The per-level record's first pointer is its GrantedEffects row.
At `0x141ee9383` it loads that row's DWORD `+0xbe`; when nonzero, it contributes the value to stat `0x1921`
through `0x1424fc480` at `0x141ee939b`.
The checked native table resolver uses `Data/GrantedEffectsPerLevel.dat` at `0x1433f04a0`,
and `0x1424a05f4` follows per-level `[0]` to the GrantedEffects row before selecting `+0xcb/+0xdb` stat sets.
The extracted schema gives that `0xbe` field the name **SkillID**, with row stride `0xeb`.
It is an authored behavior-variation integer, not the u16 skill-manager ID used in network messages.
The selected ordinary `TornadoShot` row has zero and skips this contribution;
`TornadoShotAltX` has one and contributes `skill_behaviour_variation=1`.

The same builder calls stat-set builder `0x141ee8ef0` at `0x141ee9114`.
Its implicit-stat loop resolves references through `0x14249f8e0`, converts each Stats row to its one-based key
through `0x142290090`, and contributes value one at `0x141ee8feb`.
The base reference array is selected StatSet row `+8/+0x10`, matching extracted `ImplicitStats`;
the wrapper also supports additional references.
Ordinary TS's base set lacks rain/nova, whereas Cloudburst's includes `projectiles_rain` and `projectiles_nova`.
The integer key for rain is `0x29d4`; its extracted zero-based Stats index is 10707.
Writer `0x1424fc480` adds contributions to an accumulated value at `0x1424fc63a` and retains source/value records.
These are base contributions, not evidence that the final effective value has no other source or override.

The eventual getter remains action-selected: `+0x310 → 0x1419397d0 → 0x14193b350(action, byte174)`.
It uses the selected override-stack interface when present, otherwise the retained skill's primary/additional stat view
through `0x141ee7e70`, then queries `0x1921` through that interface.
This connects producer and consumer roles without proving the complete composition, cache refresh or activation
of every concrete local/remote action. The separate startup gate on action computed overlays is not removed by this finding.

### 4.4 Wind-resource slot production and inheritance

The concrete client factory `0x1412d4890` initializes `538=0`, constructs the shared action, installs the final table,
then computes this byte from count inputs. With native 32-bit integer semantics:

```text
if variation == 0 and action530 != 0:
    N = effectiveStat(0x10df)           # tornado_shot_num_of_secondary_projectiles
else:
    N = genericCount(action)           # 0x141ba6070
    if action3fc > 1:
        (q, r) = signedDivMod(N, action3fc)
        N = max(0, q + (u32(action448 + action400) < u32(r)))
T = effectiveStat(0x3be)
if T > 2:
    N = truncTowardZero(i32(N*T) / 2)
action538 = signed(N >= 6)
```

The selected stat `0x3be` maps to `number_of_totems_allowed` in the pinned Stats table;
that name does not establish the purpose of this surprising multiplier, so it must not be silently renamed.
The generic count chooses total arrows/projectiles via virtual `+0x598` and respects its checked modifier/min/max gates.
It is not a constant three-secondary count.

The count partition is at `0x1412d4a52..0x1412d4a87`; threshold/write at `0x1412d4a44..0x1412d4a4a`.
Copy factory `0x1414fbc30` copies source `538` at `0x1414fbca5..0x1414fbcac` without recomputing it.
Later changes to a secondary template's `530/330` therefore do not imply a fresh resource-slot calculation.
Wind helper `0x1414fc340` consumes the resulting boolean as the animated-resource slot at `0x1414fc44b..0x1414fc471`.
The slot is separate from the resource directory's effective art variant, resource-choice RNG and TLS wind rotation.
These fields must not be collapsed into a single random or subtype input.

## 5. Server implications and unresolved cases

**I:** redundant server wind/child objects can explain duplicate presentation when both native and server paths run.
That consequence does not authorize deleting server damage stages or hiding all descendants.
Before changing ownership, align sequence/derived allocation, release/repeat context, terminal point/order,
secondary count, quantization, history and authoritative result keys.
Keep original parents, Split/Fork descendants and skill-created children separate.

Upstream effective-stat composition/cache activation, remote owners, Lockstep, cancellation,
remaining terrain/override combinations and MTX need further branch/runtime coverage.
The two local wind call sites establish creation capability, not every mode's final visible count.
No outgoing per-child collision report or authoritative damage computation is established by this chapter.

Local retained material: `round3/tornado-client-factory.asm`, `tornado-client-overrides.asm`,
`tornado-wind-fresh.asm`, `tornado-wind-resolver.asm` and `tornado-wind-constants.json`
under the ignored `logs/projectile-execution-model/` tree.
The guessed second table in `tornado-concrete.asm` is exploratory unrelated data and is not used here.
Round5 `ts-final-slots.asm`, `ts-eligibility-terminal.asm`, `ts-aligned-complete.asm`,
`ts-contact-split-scheduled.asm` and `ts-stat-map.json` retain the final continuation/terminal findings.
Round6 `ts-scheduling-findings.zh-CN.md`, `ts-scheduling-producer.asm`, `ts-scheduling-support.asm`,
`ts-scheduling-delay-and-owner.asm` and `ts-scheduling-selected-assets.json` retain the rain producer and asset cut.
Round15 `ts-variant-producers.zh-CN.md`, `ts-variant-final-witnesses.asm`, `ts-variant-final-audit.json`,
`ts-granted-effects-layout.json` and `ts-effective-stat-data-witnesses.json` retain the base-stat and resource-slot
producer/copy/consumer chain. Earlier candidate/unwind previews are exploratory; the final listing extends the
per-level resolver to its actual return at `0x1424a0819`. The writer is an explicitly bounded excerpt.
