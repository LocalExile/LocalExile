# World scheduling and retained projectile actions (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md).
All addresses refer to the pinned client; these are bounded static findings, not a runtime capture.
The World scheduler, action admission, local collision policy and authoritative results are separate contracts.

## 1. Concrete component bindings

Actor constructor `0x141e8c240` installs base table `0x1435aa5d0`.
Shared constructor `0x142192860` replaces it with `0x1435daa70`;
client factory `0x1416640c0` allocates `0x7b8` bytes, calls that constructor and installs `0x14346e558`.
All three concrete tables bind these slots identically:

| Actor slot | Target | Checked behavior |
|---|---|---|
| `+0x40` | `0x141e8ef50` | component update / Actor runner |
| `+0xf8` | `0x141e930e0` | retained-action removal |
| `+0x1f0` | `0x1400f0f00` | void no-op |
| `+0x230` | `0x141e91580` | result processing |

In particular, a call through Actor **virtual slot** `+0x1f0` does not prove action registration.
The Actor **field** `+0x1f0` is also the retained vector's end pointer; these are distinct uses of the offset.
The earlier rain-clone registration inference from that virtual call is withdrawn.

## 2. Root actions and the retained snapshot

Runner `0x141e8ef50..0x141e8f665` preserves its incoming float delta.
Root management queries World virtual `+0x1b0` and can branch on its value;
root ticking also requires Actor `+0x1b0 != null` and Actor byte `+0x218 & 2`.
Those checks must not be projected onto the separate retained loop.

At `0x141e8f399..0x141e8f415`, the runner copies Actor's `+0x1e8..+0x1f0` pointer vector into a snapshot.
At `0x141e8f420..0x141e8f466`, it walks that snapshot:

```text
if action.byte122 & 0x10:
    remove through Actor.slotF8; destroy action
else:
    action.slot118(incomingDelta)
```

This retained loop contains no raw network-mode or local/remote-owner test in the checked range.
An action appended during this loop cannot enter the already copied snapshot.
An action appended earlier during root processing can still precede snapshot creation;
there is no general conclusion that every child waits until the next frame.

The [accepted-root startup prefix](animation-and-release.md#36-accepted-root-request-production)
executes `0x141e94c60 -> 0x14193d870 -> initial +0xf8 -> common preparation -> main animation request`.
Its active-bit/depth changes are native. Successful controls stop after that request returns into preparation,
before the outer startup guard is unwound; their later explicit Animated updates are not actual frame scheduling.
The runner's earlier admission/root-management calls and successful startup tail remain separate boundaries.
The subsequent
[startup-tail composition](animation-and-release.md#37-successful-startup-return-and-outermost-guard-exit)
returns through preparation/action/Actor and executes the native outer guard before explicit Animated updates.
Only the outermost positive-depth exit drains a pending request: bit `4` precedes bit `8`, with distinct clearing
and request targets. Their effects, nonzero geometry and presentation providers remain boundaries; this does
not execute the earlier admission/root-management calls or establish a complete native Actor frame.

The later [KB Actor-snapshot composition](fireball-and-kinetic-blast.md#32-joining-the-generated-group-to-actor-snapshots-and-target-references)
executes this region on the same Actor that receives the original source-generated group. Registration before
the copy permits an immediate visit; an explicitly supplied post-copy interleaving defers that group until the
next pass. Following completion, one pass marks the group retired and a further pass removes its retained
pointer through actual `+f8`, before cleanup/deletion boundaries. Post-root registers/delta, allocation/memcpy,
scalar runtime-library CPU features and final cleanup are explicit inputs; this is not a full root/frame run.

The later [deferred target-removal/action-cleanup composition](fireball-and-kinetic-blast.md#33-deferred-target-removal-and-action-cleanup-are-separate-events)
extends the installed KB deletion path through target unsubscription. Action `0x14193d1f0` publishes its
own `+280` event once, latching `+126` before callbacks; the base destructor's second call is suppressed.
This is separate from Entity removal: request `0x141ebe680` queues once, manager `0x141ebfde0` detaches
the current removal batch, then `0x141ec01c0` publishes Entity `+c8` before setting its removed bit and
deleting it. Requests made while that batch is visited wait for another manager invocation.
The core's `0x142183dd5/0x142184411` calls use World `+a18`; concrete client World `+190` returns that
same manager. Root/frame inputs, nonempty component/task cleanup, spatial/resource storage and official
cadence remain explicit boundaries; the manager's batch policy is not a network Lockstep contract.

### 2.1 Native runner, current-root admission and startup ordering

**S:** a later composition starts at the installed client Actor `+0x40 = 0x141e8ef50`, rather than entering
the post-root snapshot region. It executes the current-root admission gate `0x141e94a30`, accepted startup
and its normal return before observing the root update call and executing the retained snapshot/loop.
Six shared-resource witnesses cover ordinary bow/wand, Fireball, KB, TS and BA. Their subsequent Animated
updates are explicit calls, not an asserted order between actual Actor and model-component updates.

The five final action tables share these concrete predicates:

| Slot | Native target | Rule |
|---|---|---|
| `+0x38` | `0x141ba2cd0` | selected `0x4f32 != 0 || 0x4f33 != 0` |
| `+0x40` | `0x1400ed4a0` | actual false-returning leaf |
| `+0x230` | `0x14123d650` | true unless `+0x238` is exactly binary32 `2*pi`; unordered compares true |
| `+0x238` | `0x14193ebc0` | selected `0x4f2f == 0` gives binary32 `2*pi`; otherwise float-converted degrees |
| `+0x340` | `0x1400ecd70` | actual integer-zero leaf |

The pinned Stats rows name `0x4f32` as `skill_is_non_pathing`, `0x4f33` as
`projectile_skill_is_non_pathing` and `0x4f2f` as `action_required_target_facing_angle_tolerance_degrees`.
Thus `+0x38` is a non-pathing predicate, not an unrestricted ability-readiness check. The nonzero tolerance
formula converts signed integer degrees to binary32, multiplies by binary32 pi and divides by binary32 180,
with rounding after each operation. In the finite controls, both zero and 360 degrees give the exact
full-circle sentinel. Testing only whether the stat is nonzero would change the caller's branch.

Facing helper `0x141e9e070` returns true immediately if `+0x230` is false. Otherwise it obtains the desired
direction from integer target-minus-owner coordinates, atan2 plus pi/2 and normalization to `[0, 2*pi)`.
Current facing comes from Animated's override or Positioned `+0x29c`, according to the checked flags.
The final comparison is `tolerance * 0.5 >= abs(normalizedDesired - currentFacing)`; the checked body does
not separately normalize that difference to the shortest circular distance. These controls supply only
positive-x-axis atan2/remainder outputs and the pose setter; arbitrary trig/pose-provider parity is not claimed.

With a current or pending root and Actor `+0x218 & 2` clear, this runner attempts startup only when
the selected root's `+0x230` and facing helper both succeed. For a current root it then calls `0x141e94a30`.
This is not identical to calling that gate directly: the gate itself requires a non-null current root and
`+0x38`, but permits the full-circle `+0x230 == false` case. Controls retain that entry-dependent difference;
they do not infer how every other caller reaches the gate or starts a pending root.

The gate can set facing before admission. With the installed false `+0x40`, a true `+0x230` additionally
requires facing success. It sets Actor `+0x219` bit `4` around the attempt, then calls `0x141e94620`:

- Ordinary skill metadata requires byte `+0xb0 != 0` and byte `+0xb1 == 0`.
- Nonzero metadata `+0xa4` replaces that predicate with an alternate provider's byte `+0xb0`.
- Actor `+0x219 & 2`, Actor `+0x218 & 0x40`, owner `+0x8c & 1` or false metadata reject before action `+0x130`.
- Otherwise action `+0x130` must succeed. Actor `+0x198` then receives the skill and `+0x340` result;
  its success leads to original accepted startup `0x141e94c60` and the previously reconstructed request.

Action `+0x130` binds `0x14193c9e0` for ranged witnesses and `0x1419eeee0` for Fireball;
Actor `+0x198` binds `0x141e9d930`. Their returned eligibility/policy values are explicit inputs to this join,
not bodies executed by it. The later [eligibility/policy subcontracts](root-eligibility-and-policy.md)
execute their ordered checks separately, including distinct cost/reservation and cooldown/soul routes;
their complete composition with this runner and actual input publication remain separate.
Rejection enters World `+0x90` and request `0x141e8f780` boundaries.
The gate clears its temporary bit `4` afterward. Ancillary request eligibility `0x141e94590` executes,
with a supplied selected row that rejects a further request after successful startup.

After startup returns, the runner reloads current root and active bit, increments guard depth and calls
root `+0x118` at `0x141e8f347` with the unchanged incoming binary32 delta. The observed call has start
latch `1` and depth `1`. Its inline guard then exits before snapshot construction at `0x141e8f399`.
Retained `+0x118` calls at `0x141e8f460` receive that same delta, without this root guard increment.
A supplied publication at the root-tick boundary is included in the subsequent native snapshot; this proves
ordering under that intervention, not that a particular skill naturally publishes a child there.
Root/retained update bodies remain observed boundaries. The inline guard gives pending bit `4` priority
over bit `8`, matching the separate startup guard; request effects remain boundaries.

The timer tail uses original helper `0x14013c620`: for ordinary ordered inputs, Actor fields
`+0x29c/+0x2a0/+0x278/+0x2a4/+0x2a8` become `max(0, previous - delta)` in binary32.
That Actor timer `+0x29c` is distinct from Positioned facing at the same offset. No universal fixed tick,
official scheduling, complete root management, full eligibility or child-update execution follows from this join.

Ignored round70 `audit_runner.py`, `runner-audit.asm/json`, `check_runner.py` and `runner-controls.json`
retain exact bytes/slots, 49 controls and disclosed boundaries. Queue/auxiliary management, idle admission,
alternate metadata, storage, loaded resources, hand/repeat/timing/config and callback effects remain supplied.

The later [TLS consumer composition](random-inputs.md#614-startup-orientation-and-pierce-tls-continuity)
executes this startup's additional-Chain roll, then explicitly invokes Animated releases and Pierce in one
supplied TLS state. Optional placement-orientation draws change subsequent Pierce outcomes while action-local
seeds and emitted motion inputs remain equal. This establishes a state dependency under the specified calls;
it does not establish actual component order, thread affinity, enclosing seed scopes or an official frame.

## 3. Mode scheduler and the concrete client wrapper

Outer World scheduler `0x142183930..0x142183cd5` branches on raw World `+0x984`:

- Zero: forward the incoming float delta to World `+0x360`, then `+0x368`.
- Nonzero: accumulate `delta * World.float98c + World.float988`, consume integer frame lengths from
  World `+0x9c0`, divide each by `1000.0f`, and invoke `+0x360` once for each affordable queued frame.
  World `+0x11c` supplies a fallback frame length when needed.
- `+0x368` has a separate accumulated budget; its existence does not identify an authoritative projectile tick.

[Protocol 07](../../protocol/07-latency-visibility-movement.md#6-lockstep-not-served-yet) supplies the raw-mode names.
The zero branch is Predictive; raw two is Lockstep.
The scheduler itself distinguishes zero/nonzero, not a recovered universal two-value mode enum.

The concrete client World table `0x14346e0c8` binds `+0x360` to `0x141655b20`, while shared table
`0x1435da318` binds it to `0x142183ce0`.
The client wrapper stores the World pointer and unchanged float delta in a `0x18`-byte global-queue item.
Consumer `0x141655a10` restores them and directly calls `0x142183ce0` at `0x141655ab4`.
Thus a different concrete slot does not remove shared simulation; it adds a queue boundary.

## 4. World core, object order and delta policy

Shared core `0x142183ce0..0x1421845a6` sets `D1 = incomingDelta` and
`D0 = World.byteB9c ? 0 : D1`, then traverses the World object tree at `+0xa28`.
It skips objects with object byte `+0x8c & 1`.
Predictive uses object helper `0x1421845b0`; the nonzero-mode path selects `0x142184680` or `0x142184870`
using object-ID top bits `0xc0000000`, with different pending-message insertion points.
That ID classification is not an established local-player/remote-owner classification.

All three helpers iterate object components and invoke component virtual `+0x40`.
For an Actor this reaches the runner, and retained actions then receive virtual `+0x118`.
The checked projectile final classes bind that slot to the shared projectile updater or a wrapper calling it.

The object helpers share a delta-selection policy. Let `P = World.slot158()`,
`Q = World.slot160()` and `U = World.slot168()`:

```text
if P and not Q:                          use D0
else if U and not (object.flags8c&0x40): use D0
else if not P or (object.flags8c&0x20):  use D1
else:                                   use D0
```

Concrete `+0x158/+0x168` read World bytes `+0xb9c/+0xb9d`.
Client `+0x160 = 0x14165dce0` also examines related World state; this cut does not name every flag.
Consequently the paths are shared, but each object is not guaranteed the same nonzero delta.
This is float-second action scheduling, not evidence for a dedicated integer projectile tick inside Actor.
It does not establish an official global 30 Hz cadence.

### 4.1 Joining the core's ordering production, component updates and queue cleanup

**S:** original core `0x142183ce0`, all three object helpers and the final World delta-query leaves now execute
with messages produced by the [wire intake](world-message-inputs.md#31-joining-wire-receipt-to-release-checkpoints-and-activation).
This removes supplied ordering-object/component-prefix values, active/suppression transitions and omitted core
cleanup from the earlier release composition. The object tree and component vectors are stable supplied inputs.
An explicitly supplied component-update callback can enter the original installed controller/event/release path
on a separate stack. It **does not execute native Actor runner/root/timeline production**; the component virtual
`+0x40 = 0x141e8ef50` is a declared callback boundary in these controls, not another recovered animation producer.

The raw-zero branch sets active and temporarily suppresses checkpoints, dispatches all initial nonnull records
in arrival order without ordering/checkpoint eligibility, calls their destructors and clears the vector/cursor
**before** traversing objects. The selected six-witness release callback then sees no queued World records.
A structurally supplied null is skipped, not a frame boundary in this branch; ordinary raw-zero `0x2b5` intake
does not create such a null. The structural control is not an official raw-zero message sequence.

The nonzero branch has multiple head-dependent application opportunities:

| Position | Original eligibility/order behavior |
|---|---|
| initial `0x142183f92` loop | consume leading nonnull records whose ordering key is zero; no checkpoint test |
| before ordinary object `0x142184730` loop | consume leading keys unsigned-less-than the object's ID, copying the record checkpoint before dispatch |
| ordinary component boundary `0x1421847a4` | write current object ID and `uint16(componentIndex << 12)`; consume matching-key records due at that prefix, then invoke component `+0x40` |
| inside eligible component callbacks | shared World `+0x90` can advance the low-12 checkpoint and apply due records, as documented in section 10 |
| first high-ID helper `0x142184920` loop | suppress checkpoints and consume leading keys at most `0xfffffffd`; then use shared key `0xc0000000` for this group of objects |
| `0x142184180` loop | set key `0xfffffffe`, checkpoint zero and consume matching due heads before the selected auxiliary updates |
| `0x142184360` loop | after auxiliary updates, consume nonnull heads until key `0xffffffff`, setting key/checkpoint from each record |
| `0x1421843d6` loop | with suppression set around manager processing, consume leading `0xffffffff` records without the ordinary checkpoint comparison |
| `0x142184440` loop | after that manager phase, consume remaining nonnull records, copying their key/checkpoint, until end or a null delimiter |

Each loop remains limited by its current head. This is not a sort or a promise that every special key always
executes in its named phase. For example, a result stamped after a release's two checkpoints can still be
processed by a later core loop in the same invocation. An earlier-order head can be applied before its next
object's components; an ordinary checkpoint's temporary head block is not indefinite Actor-result retention.
The original server's choice of these keys is not derived from the existence of the supported paths.

Object classification uses the actual ID top bits, not an inferred local/remote/monster role. IDs with top bits
`0xc0000000` enter helper `0x142184870`; its component checkpoints are suppressed and its ordering key is the
shared `0xc0000000`, not each object's full ID. Ordinary helper `0x142184680` writes each real object ID and
resets the component prefix. Seventeen component callbacks preserve the native word-width wrap at index 16.
The original successor traversal executes on a three-object stable tree; native insertion, deletion and
cross-callback tree mutation are outside that fixture's coverage.

Cleanup distinguishes end from delimiter. At end, original loops call nonnull-message destructors and clear
the vector. At a delimiter, they advance past that null, move subsequent pointers into the consumed prefix,
clear moved source slots, call displaced/tail nonnull destructors, shorten end and reset cursor to begin.
Two successive direct core invocations therefore preserve the next decoded batch and retire each message once;
the `0x2b5` control object was already separately destroyed during intake. Leading delimiters are also covered.
The earlier explicit end-cursor append without whole-core cleanup remains a local intake rule, not normal
core replay: these completed invocations remove the old consumed prefix. Reentrant destructor effects and
receipt during other core callbacks remain separate obligations.

The actual core sets/clears active and resets both ordering fields on normal return. With a supplied Actor
callback and an ordinary object, nonzero mode applies stamp-1 queued data before release and stamp-2 control
after movement preparation; raw zero applies both before the component callback. The latter reaches the
ProcessResult observation at Actor depth zero rather than inside the release guard. Skipping an object's update
does not drop its result: the selected tail route can still process it without executing that component.
No numerical resource effects are executed at that observing boundary.

The three native delta leaves also replace a supplied boolean-query assumption:

- `+0x158 = 0x14164dc40` reads this World byte `+0xb9c`; `+0x168 = 0x14164dc30` reads `+0xb9d`.
- `+0x160 = 0x14165dce0` is true only when World `+0x968` is nonnull, the World reached through that
  Instance's `+0xc278` has nonzero `+0xb9c`, and either Instance word `+0xc3e8` is zero or byte `+0xc3ea`
  is nonzero. Pointer validity is a premise; the linked World is not assumed identical in all real contexts.

Controls execute the resulting D0/D1 choice with stable supplied fields in both mode branches. No release is
injected in zero-delta controls. Using `1/30` as one test input is not evidence for an official 30 Hz cadence.

The four new core/helper bodies contain no direct GS memory operands. Their recorded direct calls and virtual
callbacks are retained, but auxiliary updates/manager cleanup/event delivery remain boundaries and may have
their own random consumers. The reached `+0x90` uses the separately documented checkpoint-depth TLS field;
this is **not** evidence of a projectile RNG seed/reset envelope or of transitive RNG absence.

Ignored round62 `audit_core_release.py`, `core-release-audit.asm/json`, `check_core_release.py` and
`core-release-controls.json` retain 189 fresh/composed ranges / 13158 decoded sites, four final-table slots and
24 anchors. Forty-one finite controls execute 426813 original-instruction steps / 4565 visited sites.
Auxiliary update/manager/event bodies, empty timer/query/listener inputs, object/component lookup, IO/allocators,
destructor observations, storage/ProcessResult and actual outer scheduler/mode/Instance/TLS production remain
explicit boundaries. This is offline instruction interpretation, not a native frame capture or an official schedule.

The subsequent [native-timeline composition](animation-and-release.md#31-joining-active-track-advancement-to-actual-release-events)
replaces that component fixture's direct event injection with original controller/active-track advancement,
event selection and next-index progression. Two wire/core controls retain the raw-zero/nonzero result ordering
around the generated release. The component callback still supplies the controller invocation; this does not
execute native Actor runner/root startup or establish their connection to the animation scheduler.

The later [native Animated composition](animation-and-release.md#32-native-animated-updates-and-model-component-membership)
removes that direct invocation fixture: a second owner component uses Animated's actual `+0x40` binding,
which traverses the model's natively built update-pointer list and reaches AnimationController `+0x10`.
The Actor component remains separately observed rather than serving as a supplied timeline callback. In the nonzero core controls,
Animated receives component prefix `0x1000`, with supplied stamps `0x1001/0x1002` around its real release.
Raw-zero mode still processes the selected records before updates. Loaded model/track/listener inputs, other
providers, root startup and outer/official schedule remain boundaries, not a complete native frame claim.
The subsequent [prepared-spec startup](animation-and-release.md#33-prepared-animation-inputs-first-main-track-production-and-subscription-insertion)
produces the ordinary main track and indirect Actor subscription before two wire/core controls use this same
Animated route. The selected specification/model relation and authored resources remain explicit inputs;
this does not execute the whole normal request, Actor runner or official frame scheduling.
The later [ordinary request prefix](animation-and-release.md#34-native-ordinary-request-selection-and-prepared-spec-production)
produces its selected spec through native lookup/selectors/mapping and first-track startup before those same
two mode controls. Post-prefix model matching and the explicitly entered subscription region remain separate;
catalog/model providers and authored resources are inputs. No new official scheduler or global TLS claim follows.
The later [request-tail join](animation-and-release.md#35-ordinary-request-model-matching-paired-subscriptions-and-native-return)
replaces that explicit registration entry with actual typed model matching, paired listeners and request return.
Both wire/core modes execute the real Animated event listener with empty authored script lists. Actor `+0x270`
effects and actual root/child-model scheduling stay separate; neither a full native frame nor official cadence follows.

## 5. Common clone admission: derived binding also appends the action

`0x14193daf0..0x14193dc33` does more than install a derived byte.
Its normal path sets action `+0x11e=1` and `+0x11c=derived`, then resolves the source Actor:

```text
context = action.e8
owner = context.qword8
index = sign_extend_i32(context.int10)
actor = owner.components[index]
append action to actor.vector1e8
if action.byte124 == 0: action.slot8()
```

The append is at `0x14193dbec..0x14193dbf9`, with growth through `0x14014c4b0` at `0x14193dc00`.
It precedes optional initialization, and the checked body has no raw-mode or local-owner test.
The [typed initialization contract](action-initialization.md) binds all five final `+8/+3a8` pairs,
the latch-before-startup-gate order and separate inherited wall-cache contribution path.
Actor construction populates the context with its owner and the index resolved by the literal component name `Actor`.
Action copy `0x1419381a4` preserves that context pointer.

| Path | Clone call | Actual registration call | Later step |
|---|---|---|---|
| Shared single projectile | `0x141bb1a94`, slot `+0x450` | `0x141bb1aa4 → 0x14193daf0` | bind flying entity through `+0x710` |
| Shared multiple projectile | `0x141bb26d9`, slot `+0x450` | `0x141bb26e9 → 0x14193daf0` | per-shot metadata, then entity binding |
| TS secondary emission template | `0x141a828bb`, slot `+0x450` | `0x141a828d2 → 0x14193daf0` | shared release creates separately registered flying children |
| Scheduled rain clone | `0x141ba4b6c`, slot `+0x450` | `0x141ba4b83 → 0x14193daf0` | state/delay setup, then notification |

For shared single/multiple release, the order is entity creation → clone → retained append → entity binding →
history/layout/motion setup; admission can reject a candidate before these steps.
Object owner association, retained action membership and action `+0x328` flying-entity binding are independent links.
The TS secondary template uses `uint8(source.derived+1)` at this call, not a fresh-allocator call;
after releasing the actual children it marks itself retired at `0x141a82b48..0x141a82b7c`.
It is not an additional visible flying arrow.

This closes creation-to-retention-to-update for these checked common clones.
It does not enumerate every remote-owner action creation entry or establish a collision/reporting authority policy.
The separate [0x1ce/0x1cf synthetic-start route](synthetic-start-and-server-commands.md) also uses
`0x14193daf0(action,1)` before concrete start/contact, then removes the temporary source through Actor `+f8`.
Its source removal does not itself establish cleanup of separately retained descendants.

## 6. A separate registrar can transfer/reidentify actions

Actual registrar `0x141e9e1c0` removes an action from its previous Actor, updates a 15-bit sequence counter,
writes action `+0x11a`, rebinds action `+0xe8`, selects initialization through action `+0x2c0/+0x2c8`,
then appends to Actor `+0x1e8..+0x1f0` at `0x141e9e333..0x141e9e34d`.
Capacity growth calls `0x14014c4b0`.

Its zero/nonzero test uses World **virtual `+0x1b0`**, not raw `+0x984`.
The concrete client getter returns `1 + (rawMode == 2)`, so the zero branch cannot simply be called Predictive.
Located direct callers include special actions; this is separate from the common `0x14193daf0` append above.
Rain release's call through Actor `+0x1f0` is a no-op and occurs **after** its actual retained append.

Shared scheduling proves that admitted retained actions can execute the same updater in both checked modes.
It does not prove identical creation/admission, contact authority, outgoing reports or server responsibilities.
No new per-projectile C2S contract follows from this scheduler.

## 7. Concrete World-drain callback registration

Besides its synchronous call to `0x141655a10`, update `0x14164e250` constructs a callable with table
`0x14346e098` at `0x14164e440`.
Its execution slot `+0x10` is `0x141677490`, a jump to that same World drain.
The callable is transferred through `0x140133540` and passed to dispatcher `0x140e44fc0` at `0x14164e491`
under key `0x9f23a453`.
This callable table is distinct from the nearby World vtable.

The complete checked dispatcher body hashes the key, selects its bucket and appends a `0x48`-byte record.
It preserves input u16 metadata, DWORD metadata at `+4`, and a callable at record `+8`.
Callable transfer at `0x140e45081` and end-pointer advancement at `0x140e45086` establish registration.
Transfer helper `0x140133540` uses move/destructor slots, not execution slot `+0x10`.
Thus this is a positive registration edge, not evidence that the closure has run or that a particular thread drains it.

A separate startup path `0x140103390` installs table `0x142f9cce0`, whose first entry is update wrapper
`0x1401027d0`, into a context passed through `0x140101680 → 0x1400f9f20`.
The construction path stores that context at carrier `+0x138`, through carrier `+0xa8` then offset `+0x90`.
Section 9 below now connects that carrier to the concrete frame constructor and consuming listener call.
No fixed OS-thread assignment or TLS `+0x170` seed/save/restore around projectile consumers follows from these stores.
Do not conflate the callback context, dispatcher bucket and World instance.

Local evidence: round14 `tls-envelope-followup.zh-CN.md`, `tls-envelope-table-context.asm`,
`tls-envelope-dispatch-focused.asm`, `tls-envelope-callback-carrier.asm` and `tls-envelope-carrier-copy.asm`.
Explicit entry-to-return reads in the carrier listing replace the dispatch listing's invalid unwind lookup for leaf thunks.

## 8. Dispatcher record invocation and remaining frame/thread boundary

The registration record is now connected to actual invokers by a checked address conversion.
Let `N` be the raw hash node returned for the u32 key and `S = N+0x18` its scheduling-node address.
Key execution helper `0x140e450a0` makes that conversion explicitly at `0x140e4513f`,
then tails `0x140e45280` at `0x140e45144`.
Registration's `N+0x30/+0x38` vector is consequently the invokers' `S+0x18/+0x20` vector.
Each `0x48`-byte record contains callable storage at `+8`; its callable object pointer is at `+0x40`.
The object table's `+0x10` slot performs invocation.

| Checked execution branch | Invocation |
|---|---|
| completed-node successor processing `0x140e44b00` | `0x140e44b7c`, traversing records by `0x48`; single-item branch at `0x140e44b5b` |
| directly selected node `0x140e45280` | `0x140e45321`, same traversal; single-item branch at `0x140e452d9` |
| batch task, table `0x1431fb780` | slot `+0x10 → 0x140e45ff0`, record calls at `0x140e4601f` |
| per-item task, table `0x1431fb7b0` | slot `+0x10 → 0x140e46090`, callable pointer at wrapper `+0x48` |

For the registered World callable type `0x14346e098`, these invocation slots bind to
`0x141677490 → 0x141655a10`.
This closes the static record-layout/type-to-executor chain; it does not observe an actual registered instance running.

Node scheduling `0x140e45420` can select a node for current-thread execution or submit a wrapper through `0x140e31310`.
The latter can enqueue through `0x140e2f550`, but its direct fallback calls wrapper slot `+0x10` at `0x140e313b4`.
Waiting helper `0x140e313e0` can obtain a task through `0x140e32c70` and execute its slot at `0x140e3145d`.
Completion counters and successor processing mean an individual wrapper return is not completion of the entire graph.
None of these static alternatives selects one fixed OS thread for World drain.

The checked frame function `0x140e666b0` executes root keys `0x3cf11c14`/`0x8eaeb57e`,
not a direct call with World-drain key `0x9f23a453`.
Section 9 now identifies these roots and the concrete dependency edges to the World-drain node.
It also calls listener slot zero with dispatcher `frame+0x298` at `0x140e6675b` before executing roots.
The constructor-input chain below binds this listener to the separately stored startup context;
this is a concrete static frame-to-World connection, not just a compatible signature.

The inspected direct invokers/submission/wait helpers reveal no positive TLS `+0x170` seed/save/restore edge.
Their identified TLS `+0x168` accesses are task context, and the singleton getter's TLS `+0x10` is an initialization guard.
This bounded absence does not exclude outer or deeper deterministic scopes around the actual Chain/TS wind consumers.
Remaining evidence needs are actual task/thread choice and decision-point TLS state with preceding draw order.

Local evidence: round15 `world-drain-callable-invocation.zh-CN.md`, `dispatcher-bucket-execution.asm`,
`dispatcher-root-and-worker.asm`, `dispatcher-execution-callers.asm`, `dispatcher-immediate-envelope.asm`
and `dispatcher-aligned-final.asm`.
The exploratory `0x1431fb8a8` read is UTF-16 data and is excluded from task-table conclusions;
the actual tables are `0x1431fb780/0x1431fb7b0`.

## 9. Concrete frame graph and listener construction

Node definition `0x140e44d00` hashes byte-string views with `0x140df8570`, seed `0xc58f1a7b`.
The aligned leaf body ends at `0x140df85f7`, independently of its misleading unwind lookup.
Its uint32 length-XOR/`0x5bd1e995` block/tail/final mixing gives:

| Authored node name | Frame root / registered key |
|---|---|
| `START` | `0x8eaeb57e` |
| `MsgProc` | `0x3cf11c14` |
| `Simulation` | `0x9f23a453` |

The World callable is therefore a record registered on `Simulation`, not a separately named World-drain node.
Frame constructor `0x140e61e20` initializes graphs at `F+0x180/F+0x208` and initially selects the former
through `F+0x298`. The frame later chooses a graph using `F+0x290`; these two graphs are not established
as Predictive/Lockstep modes.
The constructor's linear definition region `0x140e6270c..0x140e66650` supplies 143 complete node definitions.
The retained extraction recovers every name and dependency in this region; its synthetic graph addresses
`0x200180/0x200208` stand for the two frame-relative graphs, not live addresses.

| Graph | Definition | Immediate dependencies |
|---|---|---|
| `F+0x180` | `EngineUpdate`, `0x140e62a84` | `START`, `MsgProc` |
| same | `PreRender`, `0x140e639f0` | `EngineUpdate`, `PostInterpolateEngineUpdate`, `EntityUpdate`, `Physics` |
| same | `Simulation`, `0x140e63d38` | `PreRender`, `UIRender`, `WalkabilityTexture`, `AnimationUploadEnd` |
| `F+0x208` | `Simulation`, `0x140e661f0` | `PreRender` |

Both roots have dependency paths to `Simulation` in each graph. Dependencies are AND conditions;
a short root-to-Simulation path does not eliminate the other predecessors.
Node construction stores predecessor pointers in scheduling node `S+0x30`, reverse successor pointers
in each predecessor's `S+0x48`, and the initial dependency count used by the mapped completion/unlock code.
Both Simulation definitions set flags `+0x14=0`, `+0x15=1`.
Consequently they can use current-thread execution or a submitted batch wrapper in the checked scheduler,
but do not select its `flag14 != 0` per-record parallel-splitting branch.

The listener binding uses three distinct objects: startup context `T`, construction carrier `C` and frame `F`:

```text
0x140103390: [T] = 0x142f9cce0, table.slot0 = 0x1401027d0
  → 0x140101680 → 0x1400f9f20(C, ..., T) → carrier copy 0x1400fb120
  → C+0x138 = T
  → 0x1410048f0(singleton, C) at 0x140101fd9
  → 0x140e61e20(F, C+0xa8) at 0x1410074ca
  → [F+0xb0] = [(C+0xa8)+0x90] = T at 0x140e61fb9..0x140e61fc0
  → frame listener.slot0(T, [F+0x298], delta) at 0x140e6675b
  → 0x1401027d0 → 0x14164e250
```

The outer caller saves `C` at entry-RSP `+0x10`; with its established frame this is `rbp+0x318`.
It loads that value into preserved `r14` at `0x141005815`, then passes `r14+0xa8` to the frame constructor.
Later reuse of the spill slot does not replace that retained input.
The carrier copy explicitly copies the pointer at `+0xa8+0x90`; this is not an inferred whole-object memcpy.
The listener invokes the synchronous World drain and registers the later drain under Simulation on the selected graph.
The two drain opportunities do not imply that the queue contains work or that an action ticks twice.

This closes the construction/type/dependency chain. It does not choose a fixed OS thread,
establish subsequent listener replacement, or derive TLS RNG `+0x170` from action/network inputs.
Those remaining input-lifecycle questions do not invalidate the recovered graph and binding rules.
No complete render-pipeline reconstruction is needed for these execution edges.

Local evidence: round16 `world-frame-dependency-and-listener.zh-CN.md`, `extract_graph.py`,
`node-definitions-decoded.json`, `simulation-dependency-summary.json`,
`node-name-hash-aligned.asm`, `node-definitions-and-listener-construction.asm`,
`frame-constructor-callers.asm` and `listener-carrier-setup.asm`.
The earlier `node-name-hash.asm` uses the wrong unwind entry and is excluded as hash evidence.

Local evidence: `round8/mode-owner-findings.zh-CN.md`, `mode-owner-actor-concrete.asm`,
`mode-owner-verified-bindings.txt`, `mode-owner-world-helpers.asm`, `mode-owner-world-entry.asm`,
`mode-owner-world-wrapper-and-retain-refs.asm`, `mode-owner-retain-and-world-getters.asm`
and `mode-owner-retain-refs.json` under ignored projectile research logs.
`round8/clone-admission-findings.zh-CN.md`, `clone-admission-construct-register.asm`,
`clone-admission-confirmed-callers.asm` and `clone-admission-release.asm` close the common append chain;
they supersede the earlier mode report's unresolved-common-admission entry.

## 10. Component checkpoints can consume ordered incoming records

World virtual `+0x90` is **`0x142183840`** in both concrete tables `0x1435da318/0x14346e0c8`.
It advances a component-local ordering checkpoint, not a float-second clock or projectile tick.
The resource investigation at fixed revision `28def344a9d01e890b31c35bef151a6c8d3583b9`
(`Agents/docs/re/client-resource-read-order.md`) binds calls from recovery controller `0x141e6b42b`
and Life gain wrapper `0x141e69e78`. Their saved-rate/later-state rules are reused here;
this section resolves the World side, without repeating resource arithmetic.

| World field | Bound use |
|---|---|
| `+0x9b0`, u32 | current ordering object key |
| `+0x9b4`, u16 | component-index prefix and within-component checkpoint counter |
| `+0x9e0/+0x9e8` | pointer-vector begin/end for already-parsed incoming records |
| `+0x9f8` | current pointer-vector cursor |
| `+0xb78` | suppress this checkpoint's record drain |
| `+0xb7a` | active-dispatch gate, set/cleared by core `0x142183ce0` and initial activation `0x142183670` |

For ordinary object helper `0x142184680`, the scheduler writes the object ID to `+0x9b0` at `0x142184787`.
Before component `i` it writes `uint16(i << 12)` to `+0x9b4` at `0x1421847af`, attempts an eligible record drain,
then calls that component's virtual `+0x40` at `0x14218482a`.
The actual word-width truncation is retained; this is not an assertion that arbitrary component counts have unique keys.

The `+0x90` body implements this bounded contract:

```text
if (checkpoint & 0x0fff) == 0x0fff:
    return
++checkpoint                         // u16, after the low-12 saturation check
if suppressDrain:
    return
oldDepth = uint32_at(TLS[0] + 0x728)
if oldDepth >= 0x400:
    return
depth = oldDepth + 1
if coreActive:
    while cursor != end:
        record = *cursor
        if record == null: break
        if record.u32[+0x14] != orderingObjectKey: break
        if record.u16[+0x18] > checkpoint: break
        cursor += 8
        dispatchParsedRecord(World, record)   // 0x142187480
depth = oldDepth
```

The TLS access is through `GS:[0x58]`, its first pointer, then `+0x728`; it is distinct from projectile RNG TLS state.
Saturation preserves the whole checkpoint; suppression or depth rejection still occurs **after** its increment.
The record comparison uses unsigned u16 order. This helper never searches past a null, different-object or
not-yet-due head record. It advances the shared cursor before dispatch and reloads it afterward, allowing a
nested consumer to advance the queue without redispatching the same record. The original depth is restored.

These are parsed-object offsets. The [common header codec and queue publication](world-message-inputs.md)
bind them to distinct big-endian wire fields; their packet offsets differ from these object offsets.
Record `+0x14` orders its application; the generic dispatcher's independently read `+0x10` selects the object
being updated. Equating those two fields would incorrectly require every update to target its ordering object.

### Mode and dispatcher boundaries

The `+0x90` body does not itself read raw network mode. Its useful queue state comes from the surrounding core:

- Raw zero/Predictive enters `0x142183ce0`'s initial batch drain at `0x142183d47..0x142183d77`,
  destroys those records, empties the vector and sets `cursor=end` before object updates.
  The checkpoint call therefore does not by itself prove arbitrary mid-component network-message insertion in Predictive.
- Nonzero mode sets `cursor=begin`, applies the initial object-key-zero records, then uses ordered object/component
  insertion points. Helper `0x142184680` sets the prefix and drains before component update; its `+0x90` calls can
  consume further eligible records **inside** that update.
- The `0xc0000000`-class object helper `0x142184870` sets `+0xb78=1` across its component updates and clears it afterward.
  It still writes component prefixes, but checkpoint calls in that region increment without draining.
  That ID class is not relabelled as a known local/remote ownership category.

At core completion, `0x142184579..0x142184586` clears the active gate, ordering object key and checkpoint.
The generic dispatcher also returns immediately when the active gate is clear.
This is separate from the outer frame/task dispatcher described above.

An existing resource-record route, reused from the paired investigation's
`Agents/docs/re/client-buff-clock-producers.md` at the same fixed revision, supplies a concrete dispatcher witness:
`0x142187480` reads parsed kind at record `+8`; its original two-level switch maps **`0x1b1`** to `0x142188059`.
The generic route looks up record `+0x10` in the World's object store, resolves its Life component through
`0x14013dda0`, then uses record byte `+0x28` to select Life/Mana/ES pool offsets `+0x178/+0x1c8/+0x210`.
Each branch passes the same parsed record to existing assignment receiver `0x141e69fb0`.
The selected route is freshly checked here to join it to the checkpoint consumer, not presented as a new receiver discovery.
That receiver's numerical behavior belongs to the paired resource investigation; entering it is not evidence
that a projectile hit amount was calculated here.

Thus saved resource inputs can straddle a supported, specifically ordered update boundary. The static route
does not establish that the official server sends a particular record at that checkpoint, that a record was
actually queued, or that arbitrary packets may interrupt any instruction. The common header and concrete intake
and registered pump/factory/owner handoff are mapped in [World message inputs](world-message-inputs.md).
The [Instance frame-input cut](instance-frame-inputs.md) closes concrete state/tick callers, final table,
integer time production and argument forwarding. Actual driver-to-state callback membership, thread/TLS
history and official producer checkpoint choices remain open.

Round42 seals four complete unwind bodies, the dispatcher's executable prefix and its whole span including
inline switch data: six identity ranges / 2,212 decoded code instructions, 50 fresh anchors and two World slots.
Inline switch bytes are treated as data, not disassembled instructions. The 95 original-instruction controls
cover saturation/suppression/depth, ordered head blocking, advance-before-nested dispatch, component-prefix
reset and the three selected resource branches: 2,536 steps / 150 sites.
Already-parsed queues, TLS pointers, object/Life lookup results and the nested dispatch effect are explicit fixtures;
the selected resource controls stop before the existing assignment receiver. These are same-agent offline checks,
not native execution, official scheduling or a new accepted-Hit-to-pool contract.
Ignored local evidence: round42 `audit_checkpoints.py`, `checkpoint-audit.asm/json`, `check_checkpoints.py`
and `checkpoint-controls.json` under the projectile research log root.

### 10.1 Joining ordered result application to an actual release

**S:** the controller/Actor release composition now calls original World `+0x90 = 0x142183840`,
then original generic dispatcher `0x142187480`, Actor dispatcher `0x1421940a0` and result ingress/control
`0x141e90f50/0x141e90c30`. It continues the same current root through release and single/multiple motion
preparation. This replaces the earlier synthetic checkpoint callback; it does not execute the whole World core.

The two application opportunities have different positions:

| Checkpoint | Position within the checked release |
|---|---|
| Actor sites `0x141e939c5/0x141e93f3c` | inside the Actor guard, before reloading current action and forwarding its event |
| dispatcher site `0x141bb23df` | after the selected single/multiple construction and movement preparation, before action virtual `+0x538` |

The latter is conditional: action byte `+0x11e` must be zero and the dispatcher's saved byte
`rbp+0x1a8` must be nonzero. A zero-count release does not enter that dispatcher in these controls.
Setting `+0x11e` suppresses the second checkpoint while preserving three prepared motions.
This is one post-layout opportunity, not a checkpoint per emitted arrow or a second simulation tick.
The saved flag's complete upstream meaning and the post-dispatch skill callback remain separate obligations.

For both `0x1af/0x1b0`, the original World switch selects `0x142187f58`, looks up destination `record+0x10`,
resolves the Actor component index and calls its dispatcher. The original Actor switch then selects the
respective result handler. Ordering object `record+0x14` and destination `+0x10` remain distinct;
a due record with an unavailable destination advances the World cursor but does not enter Actor processing.
The component lookup results are supplied here; a valid selected Actor is a premise of the checked route.

With supplied ordering key 77, initial checkpoint zero and two ordered records:

1. A queued-kind `0x1af` stamped 1 enters the same Actor's result queue before release.
2. The ordinary current root, derived zero, prepares its requested one or three motions.
3. A matching processing `0x1b0` stamped 2 removes that result before reaching the observing ProcessResult
   boundary, while the outer Actor guard is still one.

Stamping both records 1 processes them before release; stamping an immediate-kind `0x1af` 2 processes it
after movement preparation without first queueing it. Duplicate queued results remain separate: one control
removes one. A control preceding arrival creates no cancellation state for the later result.
A processing-disabled control removes without ProcessResult. A missing derived action still allows queued
insertion and later processing with null action/empty stats; a separate supplied retained derived action can
receive its result while the current derived-zero root releases. These joins execute result matching and queue
mutation, not the numerical or presentation consequences of ProcessResult.

Other controls retain a future/different-order-object/null head across both checkpoints, low-12 saturation,
active/suppression/TLS-depth gates and destination lookup failure. The cursor advances before dispatch;
the two checkpoints restore the supplied TLS depth. No arbitrary instruction interruption is implied.

The queue, ordering prefix, TLS pointer and World state are explicit inputs. The ordinary composition supplies
raw nonzero mode and an active ordered queue, consistent with the supported ordered core branch above.
It does **not** establish mid-release intake in Predictive: that core branch normally drains the initial batch
before object updates. Parsed records are not a new wire decoder or observed official producer schedule.
Object/component lookup, empty-owned record storage, ProcessResult and inherited resource/model/terrain/
construction boundaries remain supplied. Actual root/timeline, whole-core production, external random
consumption, nonempty cleanup and wider owner/mode policy remain open.

Ignored round60 `audit_release_checkpoints.py`, `release-checkpoint-audit.asm/json`,
`check_release_checkpoints.py` and `release-checkpoint-controls.json` retain the original ranges, switch
bindings, executed sites and disclosed call boundaries. Inline switch bytes are retained as identity data,
separately from executable decoding.
The later [wire-to-checkpoint composition](world-message-inputs.md#31-joining-wire-receipt-to-release-checkpoints-and-activation)
replaces this join's supplied parsed records and queue publication with original pump/factory/readers/intake,
including actual vector growth and selected activation/frame controls. Mode/ordering/TLS and whole-core
production remain inputs. Activation's direct replay and an explicit append without core cleanup are kept
separate from ordinary checkpoint eligibility; neither supplies official scheduling policy.

Fresh audit retains 157 ranges / 10621 decoded sites, three final-table slots and five call/cursor anchors.
Thirty-eight finite controls execute 470072 original-instruction steps / 3709 visited sites, including CDQE
sign-extension and SETB at their original sites. This is offline interpretation, not execution of the native target.
