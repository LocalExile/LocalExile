# Shared projectile motion and contact (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [012 — projectile execution](../012-projectile-execution.md).
Labels and pinned binary identity are defined there and in [the evidence ledger](evidence-and-open-questions.md).
Names below describe observed roles; they are not recovered source symbols.

## 1. Motion belongs to Positioned and action callbacks

**S:** the small Projectile component does not implement the flight/contact model by itself.
The traced shared action configures a local object's Positioned motion, installs captured action callbacks and registers
an observer for completed/blocked motion.
The concrete skill determines which setup and action callbacks apply.

| Entry | Role |
|---|---|
| `0x141bacf60` | shared action motion setup; reached from the traced TS movement callback |
| `0x141e34220 → 0x141e34400` | straight-move wrapper and specification-based setter |
| `0x141e34a70` | arc-move setter |
| `0x141e32710` | Positioned frame update with float delta |
| `0x141e4d180` | integer path initialization |
| `0x141e4d390` | next integer sample, optional parabolic height, last-sample flag |
| `0x141e4d510` | separate world-coordinate/render interpolation setup |

Do not infer the simulation trajectory from a rendered model position alone.
The integer path and the separate float interpolation state coexist.
The [movement configuration inputs](motion-configuration-inputs.md) separately bind collision-hull layers,
initial versus redirect calls and the straight/arc specification; those layer bytes are not height compensation.

## 2. Integer path initialization and tie behavior

**S:** `0x141e4d180` chooses a dominant axis, initializes two error accumulators and stores endpoint/time state.
For an ordinary finite straight path without overflow:

1. Choose X as primary when `abs(dx) >= abs(dy)`; otherwise swap X/Y and set path `+0x09`.
2. If height is explicit and `abs(dh)` exceeds the horizontal dominant delta, swap the primary axis with height
   and set `+0x0a`.
3. Set each minor-axis error to `-floor(dominantLength / 2)`.
4. At each sample add the minor absolute delta; advance that minor coordinate only when the error is **strictly > 0**,
   then subtract the dominant length.
5. Advance the primary coordinate by its sign; restore axis order for output.

The first sample advances beyond the source; initialization/birth-overlap is handled by a different action query.
The ordinary endpoint is included.
The half-cell tie is directional: a path traversed backward need not visit exactly the reverse of the forward samples.

Example for `(0,0) → (4,2)`:

```text
samples: (1,0), (2,1), (3,1), (4,2)
reverse: (3,2), (2,1), (1,1), (0,0)
```

The start-height sentinel `0x7fffffff` suppresses the ordinary height-axis contribution.
Arc initialization uses that route and installs a separate height polynomial.
Do not replace this path with an unspecified Bresenham implementation: a different half-tie rule changes contact cells.

An [offline reference](../../../../scripts/re/projectiles/native_projectile_raster.py) covers only the finite,
nonzero-length, nonoverflow ordinary integer recurrence.
It rejects sentinel-height and overflow cases; it does not simulate time, collisions or the client process.

## 3. Frame time and sample time are different fields

Path begins at Positioned `+0x1e8`, so these aliases refer to the same storage:

| Positioned | Path-relative | Observed role |
|---|---|---|
| `+0x238` | `+0x50` | accumulated raw motion time |
| `+0x23c` | `+0x54` | initialized full path duration |
| `+0x240` | `+0x58` | time per dominant-axis step; zero also marks inactive movement |
| `+0x244` | `+0x5c` | residual progress-time budget |

For the ordinary initializer, full duration is `f32(sqrt(f32(dx²+dy²+dh²)) / speed)`;
sentinel height omits `dh²`.
Step time divides that duration by dominant length.
Units here are those of the integer-path helper's inputs; this is not permission to pass an unconverted world speed.
The native integer arithmetic has overflow/degenerate cases outside the offline tool's scope.

At `0x141e32794`, `0x141e33c40` computes a progress delta.
Its simple branch returns the input delta; another branch integrates a referenced cumulative-progress curve.
The checked shared straight and arc setups explicitly clear this optional profile,
so their initial motion uses raw delta; a specialized later override is a separate case.
The [speed/time supplement](motion-speed-and-time.md) reconstructs the profile arithmetic,
shared speed units and residual rescaling when a redirect changes time per step.
The frame adds that result to `+0x244` and raw delta to `+0x238`.
The update then loops through due samples:

```text
while movement is active:
    if residual < stepTime and not (specialEndMode and rawTime >= fullDuration):
        stop sampling for this frame
    sample, height, last = NextIntegerSample(path)
    check bounds, terrain, nearby entities and action callbacks
    if blocked: stop current movement and notify its blocked observer
    else: commit sample; if last, notify completed observer
    # callbacks may replace movement; re-check current state
```

`NextIntegerSample` normally subtracts step time from the residual.
It also has an end-mode branch that can finish without another coordinate advance.
The full branch behavior must be preserved when porting; the pseudocode is a control-flow summary.

**I:** sampling only the visible/frame endpoint can skip intermediate target or terrain cells that this client checks.
Conversely, this does not prove the client solves continuous relative-motion intersections against moving targets.
It queries local objects as the due samples are processed; exact entity-update ordering remains unresolved.

## 4. Arc height and world height

**S:** when path `+0x44` contains a polynomial base height instead of the sentinel, `0x141e4d446..0x141e4d48a`
uses `t = min(path.+0x50, path.+0x54)` and calculates an integer height marker:

```text
height = base + trunc_f32(v * t + 0.5 * a * t * t)
```

The instruction order uses scalar float32 operations and truncation toward zero.
The time is the accumulated field read by this sampler, not an independently reconstructed exact time of contact.
Multiple due XY samples processed in one frame can therefore share the same height time.
Arc setup at `0x141e34f0c..0x141e34f1a` writes the polynomial fields after initializing the horizontal path.
Exact arc parameters remain the responsibility of the action/skill setup.

Positioned's integer height marker, rendered world Z and sampled terrain height are separate quantities.
The existing [protocol height evidence](../../protocol/11-skills.md#10-projectiles-experimental) establishes the
explicit marker-to-world-Z factor/sign and the terrain sentinel; it does not establish every native action's origin offset.
The float interpolation path has its own height parameters.
Adding a fixed absolute lift cannot substitute for reconstructing arc state, surface checks or native attachment placement.

## 5. Bounds, terrain and candidate queries

**S:** after each sample, the frame obtains world slot `+0x180` and checks:

- Nonnegative XY and grid bounds `tileWidth * 23`, `tileHeight * 23`.
- Layered nibble-grid data at grid `+0xd0/+0xd8` and sample XY, under movement/Positioned flags.
- Nearby entities via `0x141e47a40 → 0x141e47ca0 → 0x141e32dc0` when not already blocked.
- Explicit sample height versus `0x141d84a10` terrain height, unless the applicable flags/sentinel bypass it.

The order and bypasses matter; not every entity, cell or visual wall enters the same query.
The nibble value's complete semantic name and all movement flags are not yet mapped.

The frame search extent is:

```text
extent = max(int8(current.+0x1e3), int8(current.+0x1e4)) + 1
rough acceptance:
    max(abs(dx), abs(dy)) - int8(candidate.+0x1e3) < extent
```

Birth uses **fixed extent 1**, written at `0x141bb83ed` and passed by pointer through the query descriptor;
it does not read the source's two size bytes for that extent.
Both rough tests use strict less-than and reject candidate size `-1`; candidate `+0x1e4` is not used here.
The constructor initializes source sizes from metadata bytes `+0x10/+0x11`, without proving later immutability.
This is a coarse grid/body filter with sentinel cases and subsequent checks.
It does not prove a universal circular or square final collision body.
Bucket traversal, insertion and synchronous active-cursor mutation are traced below;
complete cross-bucket/world update ordering remains open.

The action functors stored in Positioned distinguish ordinary candidates and special objects:

| Storage | Functor vtable / invoke | Traced destination |
|---|---|---|
| `+0x160`, erased pointer `+0x198` | `0x14352cfc8 / 0x141a43720` | `0x141ba7b30` |
| `+0x1a0`, erased pointer `+0x1d8` | `0x14352d058 / 0x141bbbf00` | `0x141bad450` |

The setters move these function objects from the motion specification.
A slot offset alone is not enough to attribute a callback in an unrelated class.
The table above corrects the earlier reversed storage attribution: the shared straight caller,
wrapper, setter and actual captured-functor transfers are joined in section 8.
The [motion admission supplement](motion-admission-inputs.md) follows the concrete special-contact normal writer,
metadata/base and movement-specific byte inputs, restoration and sign-bit suppression.
Those transitions affect later Chain candidate admission; they are distinct from numerical damage acceptance.

### 5.1. Bucket order and first accepted candidate

**S:** `0x141e47a40` converts a sample-centered rectangle into 23-cell buckets using `0x1400f0a70`,
clips it to the grid, then visits increasing X within each increasing Y row (`0x141e47c45..0x141e47c64`).
Buckets have stride `0x38`; their `+0x10/+0x18/+0x20` vector contains 16-byte records with a Positioned pointer
and a tag byte at record `+0x08`.
Within a bucket, `0x141e47ca0` scans forward, stopping when `(tag & 0x7f) > 1`.
Tag high-bit, layer, sentinel-size and action filters still apply.
A true callback immediately stops both inner and outer traversal; a false callback permits later candidates.
This query does not first sort all candidates by target ID, Euclidean distance or continuous collision time.

Registration `0x141d83fb0 → 0x141d7c4c0` inserts by **upper bound of `tag & 0x7f`**.
Thus this inserter places a new same-rank record after existing records of that rank.
The tag is constructed in this order:

```text
if metadata.byte21 != 0: rank = 4
elif metadata.byte19 != 0 and !(Positioned.byte1e7 & 2): rank = 3
elif Positioned.byte1e6 & 0x40: rank = 1
elif !(Positioned.byte1e7 & 1) and (Positioned.byte1e5 & 8): rank = 0
else: rank = 2
tag = rank | (0x80 if Positioned.byte1e5 & 1 else 0)
```

These are observed classifications, not a recovered universal enum.
Ordinary registration uses the center's bucket; the metadata `+0x21` branch can register across multiple buckets.
After a successful sample commit, `0x141e2f020` requests grid `+0x30` relocation if the 23-cell bucket changes.
Movement/re-registration can therefore alter same-rank order.
The synchronous insertion/deletion cursor rules are traced below;
multi-bucket duplicates and complete world entity-update order remain unresolved.

Birth overlap `0x141bb83c0 → 0x141bbaf70` has similar row-major traversal but uses rank threshold **0**,
rather than the frame query's 1, and reaches the action through different filters.
It must not be replaced with an assumed identical frame candidate list.

### 5.1.1 Callbacks can mutate the active bucket; traversal is not an entry snapshot

Both frame query `0x141e47ca0` and birth query `0x141bbaf70` link a stack node into bucket `+0x28`.
It holds the next record index, the head-slot address and the previous active node.
They increment/save this index before invoking the candidate callback.
After a false callback they reload the index and vector begin/end; normal and early-success exits restore the old head.
Nested synchronous queries on the same bucket therefore have separately registered cursors.

Insertion `0x141d7c4c0`, after its rank upper-bound search, walks every active cursor:
if `insertIndex < nextIndex`, it increments that cursor before shifting/inserting records.
An insertion exactly at the next index does not increment it.
The existing vector-growth branch does not change this index-based rule;
queries reload vector storage after the callback rather than retain its old begin pointer.

Removal `0x141d7c650..0x141d7c6cf` finds the first equal object pointer,
decrements each cursor when `eraseIndex < nextIndex`, and shifts following records left in order.
Its checked implementation assumes the object is present; no safe absent-object behavior is established.
Removing the current callback object consequently preserves the next surviving candidate instead of skipping it.
Removing a future object leaves it absent from later traversal.
Inserting into the visited prefix is skipped by that cursor;
inserting at or after its next index can be visited in the same query, subject to rank/layer/action filters.

The checked relocation wrapper `0x141d84250` unregisters through `0x141d84290`, then registers again through
`0x141d83fb0`; those normal bucket branches use the same removal/insertion helpers.
Same-bucket wrapper `0x141d844e0` likewise removes then inserts.
KB area query `0x141acc1f0` uses the same active-cursor node and post-callback reload pattern,
with its own synchronization and target filters.

This closes a synchronous bucket-mutation rule, not global once-only visitation or concurrent safety.
Cross-bucket movement/re-entry, multi-bucket duplicates, lifetime ownership, other mutators and world update order remain open.
Round13 `bucket-mutation-aligned.asm` and `bucket-mutation-findings.zh-CN.md` retain the instruction boundaries.
`check_bucket_mutation.py` checks 185,184 finite index/suffix cases for the translated cursor conditions;
it does not execute client instructions or model threads/rank filters.

### 5.2. Query sample precedes the ordinary position commit

**S:** `0x141e329ca` queries using a next-sample coordinate on the stack.
Only afterward does `0x141e32a2f..0x141e32a37` commit it to Positioned `+0x294/+0x298` and update bucket membership.
The usual blocked branch at `0x141e32bc4` bypasses that normal commit and notifies the observer from the retained position.
An action callback reading its flying object's current integer coordinate is therefore not automatically reading
the sample coordinate used by the surrounding query.

`0x141e32dc0` also contains a metadata-priority branch described below.
The specialized `0x141ba7fee..0x141ba80b3` action branch can apply another Chebyshev gate using the source's
retained coordinate when `int8(source.+0x1e3) < int8(source.+0x1e4)` and the checked target-component/tag exclusions hold.
Neither is evidence for a universal circular final body.

The frame's `Positioned +0x1e5 & 0x04` permits some otherwise blocked non-nibble samples to commit;
an unblocked sample clears that mask at `0x141e32bae`.
It cannot bypass the preceding bounds check or a nibble blockage.
The constructor clears this mask; a real writer at `0x141b25356` sets it on the action owner's Positioned,
conditional on a distinct path-search out-bool through `0x141e4ef90 → 0x141e4d890` and action `+0x770 != null`.
That out-bool is not the helper's AL success result.
This establishes one setter, not default reachability on the checked flying projectile objects.
Do not name it universal phasing or terrain-ignore.
The loop in section 3 is consequently a summary of normal stop/continue behavior, not a replacement for native flags.

### 5.3. Metadata-priority displacement bypasses the action callback

For different source/candidate objects, let `ps/pc` be their metadata `+0x58` priorities.
If neither priority equals 3 and `ps > pc`, the normal callback route is replaced:

1. Reject if candidate float `+0x2e8 > 0`.
2. Decode source path endpoint minus its **retained** XY as vector `v`;
   let `r = candidate.retainedXY - source.retainedXY`. Reject if `dot(v,r) < 0`.
3. Choose the perpendicular `n` toward the candidate's side; collinear input chooses `(-vy,vx)`.
4. Set `d = dot(n,r)/length(n)` and integer displacement `Normalized(n, 12.0f-d)`.
5. Temporarily clear candidate byte `+0x1e2`, call `0x141e33f80` with the displaced position and
   third argument `40.0f`, restore the byte, set candidate `+0x2e8 = 1.0f`, and return false.

For nonzero direction, nonoverflowing int32 dot products/squared lengths and finite representable conversions,
normalization helper `0x140671da0` rounds each axis with `floor(component+0.5f)`.
Metadata enum meaning, the `40.0f` argument's units and resource-specific reachability remain unproved.
This path does not call the action contact functor; body-query overlap alone is not a damage-contact event.

## 6. Birth overlap and termination

**S:** the multiple-emission path calls `0x141ba99d0`, which registers the observer and can query source overlap through
`0x141bb83c0 → 0x141bbaf70 → 0x141ba7b30`.
This complements the frame-source contact path rather than replacing it.

For the traced action observer:

| Cause | Observer notification | Action destination |
|---|---|---|
| Last sample | `0x1401d8280`, observer `+0x08 → 0x141ba67b0` | action `+0x4c8`, terminal `(null,true)` in the traced binding |
| Target/terrain stop | `0x1401d81f4`, observer `+0x10 → 0x141ba67e0` | action `+0x4d0`, terminal `(savedTargetOrNull,false)` |
| Out of bounds | same blocked observer route | no enemy target required |

Enemy contact can retain the target at action `+0x3a8`; the blocked adapter uses it and subsequently clears it.
Positioned `+0x1e6` bit 7 can suppress a notification; its full role must not be guessed from the sign test.
The terminal can select continuation, so a motion-stop notification is not automatically final projectile destruction.
See [continuation and memory](continuation-and-memory.md).

## 7. Validation and remaining work

The raster reference passes four fixed paths plus 1616 small 2D/3D cases against an independent closed-form recurrence.
This checks the translated arithmetic and tie behavior, not native execution, float time or gameplay correctness.
The raw VA ranges and local evidence are listed in [the ledger](evidence-and-open-questions.md).

Remaining: speed-profile producers, all state/layer bits, arc/render precision, special movers, callback re-entry,
moving-target update order, final body geometry, and network-mode-specific admission.
The [World scheduling supplement](world-scheduling.md) maps the shared outer update chain and its mode differences;
it does not close creation/admission or collision authority.
No new 30 Hz requirement or official-server time model follows from this frame code.

## 8. Joining emission to installed straight motion and frame advancement

The [real layout-to-placement composition](emission-admission.md#6-joining-real-layouts-to-placement-and-the-movement-callback)
previously stopped at final virtual `+0x558`. Round51 now continues the same allocated objects through:

```text
fan / Volley / subgroup -> per-arrow emitter -> final placement and speed
 -> actual final-table +558 wrapper -> shared 141bacf60
 -> straight wrapper 141e34220 -> specification setter 141e34400
 -> original captured-functor transfers and integer initialization 141e4d180
 -> subsequent supplied frame deltas at 141e32710 -> progress 141e33c40
 -> integer samples 141e4d390 -> actual bounds/nibble checks
 -> supplied entity-query / terrain-height responses -> commit or stop
 -> completion/blocked notification-call boundary
```

This is a finite original-instruction interpretation, not a running-client trace.
It executes default bow/wand, Burning Arrow, ordinary Fireball, KB and TS movement bindings;
the table binding shared by bow and wand does not construct or distinguish their actual equipment here.
The entry still supplies the source/profile, clone state, resources and environment described below.

The later [TS generator-to-straight composition](tornado-shot.md#23-joining-the-dedicated-origin-callback-to-actual-straight-movement)
also executes the previously supplied skill-specific `560/568` placement callbacks, native in-bounds Positioned
commit and the actual TS/shared mover on generated children. Generic placement is corrected to saved mother
grid/height; the initial-secondary target-height substitution has explicit continuation/return exclusions.
Selected nearby-target inputs do not change the resulting straight endpoints in those controls. Actual
model/resource/terrain/component/spatial APIs, presentation, later frames and descendant lifetime remain separate.

The subsequent [generated-child frame/terminal join](tornado-shot.md#24-generated-children-through-native-frames-and-terminal-history-release)
feeds those TS children into the original frame, integer samples and spatial query over supplied empty buckets,
then installed notification/terminal/stage/final callbacks. It covers natural completion and bounds stops,
two-update progression and actual history reference release. Data/model/component/storage and retirement
delivery remain inputs; nonempty target contact, descendant continuation and Entity destruction are not established.

### 8.1 Captured callbacks are installed in a specific order

For the checked shared straight route, the original function-object copy/swap helper `0x140133ff0`,
two capture-copy leaves `0x141bbbf10 / 0x141bbbf40`, the wrapper's default capture-copy leaf
`0x141c10d90` and the non-allocating destructor branch actually execute.
After setter completion, each distinct flying Positioned contains:

| Storage | Stored vtable | Captured `+8` |
|---|---|---|
| `+0x160`, erased pointer `+0x198` points to this inline buffer | `0x14352cfc8` | that arrow's derived action |
| `+0x1a0`, erased pointer `+0x1d8` points to this inline buffer | `0x14352d058` | the same derived action |

This corrects the previously reversed table in section 5. These stores are checked after real setter
execution, including multiple siblings; they are not supplied final callback objects.
It does not execute either captured target-contact callback or establish every other movement specification.

The straight specification also clears the optional progress profile and writes current layer 1 and movement
flags 0 for the supplied base-layer input 1. Actual original instructions initialize raw time to zero,
the duration and per-dominant-step time, preserving float32 square-root/division order.
Those values are compared with a separate duration equation and the bounded ordinary raster reference.
The source object metadata and initial state remain explicit fixtures, not a reconstructed constructor.

### 8.2 One emitted object advances through several samples per update

One supplied Fireball input uses source/aim world XY `(100,100) -> (250,150)`, source/target terrain heights
11/29, vertical offset `-39.0625`, descriptor speed 1000, projectile-speed increase 50 and preserved aim.
The joined original chain installs integer endpoints `(9,9,3) -> (23,13,1)` and speed `138.0f`.
Its duration is `0.1064995527267456f` and step time `0.0076071107760071754f`.
These are constructed input values, not measured default skill or bow-muzzle calibration.

With a supplied delta `f32(1/30)`, the original frame visits:

```text
(10,9,3), (11,10,3), (12,10,3), (13,10,2)
```

It commits the fourth sample and leaves residual `0.0029048938304185867f`.
A later sufficient delta processes the remaining ten samples, includes `(23,13,1)`, deactivates
the path, clears residual and reaches completion notification `0x1401d8280`.
Separate 1/120-second, uneven and single-large-delta fixtures retain the same ordinary straight raster.
Two half-step updates demonstrate that no sample runs before enough residual accumulates.
These checks supply delta values; they do not recover the actual World cadence or prescribe 30 Hz.
They also do not generalize partition equivalence to arc heights, mutable targets or re-entrant callbacks.

### 8.3 Blocking is checked at intermediate samples before position commit

With the same source and clear earlier cells, make candidate `(12,10)` fail one of three checks:

- Set only that cell's layer-1 clearance nibble to zero; the actual native nibble indexing/comparison executes.
- Supply a terrain-height response above the sample; the actual frame height comparison executes.
- Supply a true entity-query response at that sample, without extra callback mutation.

Each control visits three samples, keeps committed position `(11,10,3)`, clears motion/residual and reaches
blocked notification `0x1401d81f4`. The nibble failure skips the third entity query; the other two controls
make it. The entity queries receive extent 1 and layer 1 for the supplied zero source-size bytes.
This is a concrete caller/ordering witness, not an execution of target enumeration, local hit acceptance,
Pierce/Chain/Fork/Return, damage or notification subscribers. In particular, a supplied query boolean
must not be generalized into a universal target-hit/stop policy with arbitrary state flags or callback effects.
Spatial bucket relocation is a declared boundary in these isolated fixtures.

The actual TS wrapper is also checked with primary, initial secondary, later-secondary and already-continued
inputs. The initial secondary's supplied stored height 7 replaces the prepared target height only under
its native phase/continuation gates, before actual integer motion initialization. A nonzero
`projectiles_travel_at_fixed_height` instead makes the shared path use the source's effective height.
Actual upstream publication of these flags/heights remains separate from their recovered consumption.

### 8.4 Evidence and remaining boundary

Ignored `round51/audit_motion_join.py` and `motion-join-audit.asm/json` retain complete unwind spans or bounded
reachable leaf CFGs, final-class/capture bindings and call anchors. `check_motion_join.py` and
`motion-join-controls.json` execute 27 finite composition controls / 374817 original-instruction steps /
2664 visited sites, including five-table multi-arrow installation,
actual capture transfers, delta partitions, blocking, height gates and dominant-axis/reverse-direction examples.
The byte audit covers 105 ranges / 4684 decoded sites / nine anchors / 32 final-class/capture table bindings.
The previously recovered finite atan/sincos interpreters are separately byte-checked; bounded random draws
continue to reuse the sealed seeded reference.

Supplied boundaries remain source/profile/raw inputs, constructor and copied clone/history state, resources,
retained allocation, component-map lookup, metadata/scale/layer and positive grid dimensions/data,
terrain heights/floor/side admission, equipment, optional source extension and ordinary Fireball special-height gate.
Orientation, interpolation/pitch, attachment presentation, initial-phase event `0xb4db`, spatial relocation,
event listeners and target-query responses are explicit fixtures. The original observer registration executes,
but notification fan-out/subscribers are not executed. The checked route is straight, not the ballistic solvers
or specialized arc movers. No native target, capture, server, CI, peer workspace or remote operation occurs.

The next composition boundary is the actual entity query and captured action-contact handlers, followed by
history/continuation and correctly keyed authoritative results. Complete source/clone/input lifetimes,
mode/owner/monster paths and actual scheduling/RNG/TLS consumption remain obligations.

## 9. Joining frame queries to the installed ordinary contact and history

This finite composition removes section 8's supplied entity-query boolean. It keeps the same emitted object,
original movement setup and frame sampler, then executes:

```text
frame sample
  -> 141e47a40 rectangle / clipping / bucket traversal
  -> 141e47ca0 rank / sign / coarse-size / selected-layer filters
  -> 141e32dc0 source / metadata-priority / special / suppression gates
  -> installed ordinary capture at Positioned+160, erased pointer +198
  -> 141a43720 -> 141ba7b30
  -> current-phase history / local-prediction wrapper
  -> ignore, remember-and-ignore, Pierce, or retain target and stop
```

This is original-instruction execution in an offline interpreter, not native process execution or authoritative
damage. Target construction, spatial registration and component lookups remain supplied. Independently recovered
numerical prediction rules are reused as a labelled callee boundary; the actual attack/spell wrappers,
argument transfer and boolean inversion execute.

### 9.1 Query order reaches the captured action

`0x141e47a40` uses `extent + runtimeDword - 2` for the sample rectangle, converts it with `0x1400f0a70`
using divisor 23, clips it and scans increasing X within increasing Y. Controls supply runtimeDword = 2,
an eight-by-eight bucket grid and bucket vectors; these are not recovered actual world registrations.
Lock/unlock import calls are observed fixtures, not OS locking or thread-order evidence. The native bucket walker
installs a stack cursor and restores its previous head on both false and true exits.

The frame forwards its emitted Positioned pointer; the installed capture forwards that arrow's derived action.
No final callback or accepted-contact boolean is injected between sample, bucket walker and contact body.
An empty query at `(23,23)` visits `(0,0), (1,0), (0,1), (1,1)`. Within a supplied vector, target ID 300
before ID 100 is accepted first; if history rejects 300, traversal reaches 100. No ID/distance sort intervenes.

Controls also cover high-bit tags, rank cutoff, candidate size -1, the strict coarse boundary, self rejection
and layer-zero selection of candidate base/current layer. Higher source metadata priority with positive target
cooldown rejects before ordinary capture; the displacement calculation for an eligible target is not executed.
Extent -1 returns before locking. Direct negative-coordinate query examples retain native signed-division
behavior instead of replacing the rectangle helper with host ceil/floor; the ordinary frame separately checks bounds.

### 9.2 Retained contact, remembered avoidance and Pierce differ

For supplied straight path `(9,9,3) -> (23,13,1)`, a zero-size target at `(13,10)` reaches ordinary contact
on the fourth due sample. With local prediction supplied as permitting contact and no Pierce, the handler
stores a weak target at `action+0x3a8` and returns true. The mover stops with committed `(12,10,3)` and
reaches the blocked-notification call. Candidate and committed positions differ. The target is not yet in history:
terminal subscribers were not executed. A second explicit query rejects it at the retained-contact gate before
prediction, despite that empty history.

This stop composition executes five final tables: ranged Default Attack, Fireball, KB, TS and BA. Their actual
attack/spell prediction wrappers differ as documented in [local hit prediction](local-hit-prediction.md).
Supplied equipment, state mask and target components mean these are not six complete skill releases or a claim
that every class normally stops there.

When deeper prediction is supplied as rejecting contact, the original wrapper inverts it, the common handler
inserts the ID through `+0x4c0 -> 0x141bae0c0`, and returns false. A size-one target is enumerated on three
successive samples, but native history rejects the later two before prediction. The projectile finishes;
remembering a target does not establish damage. Another control inserts ID 101 ahead of existing 150/300 through
original sorted insertion, with spare capacity supplied and overlapping byte movement a standard-library boundary.

With finite Pierce budget one, actual `+0x678 -> 0x141ba96a0` spends it, executes `0x141baf5f0` with reason 2,
records the first target, increments executed-Pierce count and returns false to the query. Later overlaps are
suppressed by history. A second target at `(16,11)` produces a retained stop at committed `(15,11)`; remaining
budget and executed count are zero and one. The reason helper's impact-object construction is bypassed by supplied
row mode 5. Its reason/stage subscriber is an observer boundary, not executed result consumption.

A TS primary-mask fixture executes forced Pierce without a finite budget; cannot-Pierce rejects before that mask.
This joins the recovered priority to sample/contact state without repeating the isolated qualification matrix
or proving all upstream mask writers. Outbound/return/additional-Return history selection also executes real
`+0x6a0` with explicit phase bytes. An outbound ID does not block an empty returning partition. These controls
do not execute the Return phase producer or establish per-skill clone/shared-history allocation policy.

### 9.3 A query stop is not a universal hit notification

With supplied source Positioned byte `+0x1e6` high bit set, the candidate adapter returns true before ordinary
capture. The frame stops, suppresses the ordinary blocked notification and clears that high bit. Thus true
can stop motion without prediction, memory insertion or a hit event. This composes the
[motion-admission suppression rules](motion-admission-inputs.md) with the query/frame route; the actual flag
producer and larger lifecycle remain separate.

Conversely, completed/pending/state-mask gates in `0x141ba7b30` reject candidates and permit further samples.
The first-sample stop uses supplied constructor-like `+0x1e5 = 0`, rather than section 8's flag-4 fixture.
Candidate `(10,9)` is not committed; `(9,9)` is retained. Do not generalize these cases to every flag combination,
birth query, moving target or special callback.

### 9.4 Evidence and remaining boundary

Ignored `round52/audit_query_join.py` and `query-join-audit.asm/json` bind 126 complete-unwind/bounded-leaf
ranges, 7363 decoded sites, eight anchors and 55 final-table slots. `check_query_join.py` and
`query-join-controls.json` execute 38 controls / 322220 original-instruction steps / 3362 visited sites.
They extend the sealed section 8 interpreter with signed division and NOT instruction semantics; those additions
do not replace the original query/contact algorithms. Trig and bounded sampling retain the earlier boundaries.

Besides section 8's source/profile/constructor/clone/resource/terrain/equipment/presentation fixtures, this round
supplies target identities/metadata/components/targetability, spatial bucket registration, runtime query/visual
globals, source extension absence, weak-reference operations and numerical prediction acceptance. Reason-2 impact
construction is gated off; reason/stage listeners, observer fan-out, final terminal, authoritative results/damage
and native runtime/threads are not executed. Optional ordinary-contact branches and the special-object callback
are not exhaustively composed. Full release, actual input publication, history allocation/lifetime, special
movers and authoritative scheduling remain obligations.

The next composition follows the registered observer into terminal and Return below. Result subscribers,
other continuation executors and upstream lifetimes remain separate obligations.

## 10. Joining registered notifications to terminal and Return

**S:** this extends section 9's same emitted object, query, installed ordinary capture and retained target through
the original notification dispatcher and shared terminal. The dispatch result is no longer supplied by a fixture.
Constructor/clone inputs are still supplied: the embedded action observer uses table `0x143235690`, bound from
the actual constructor LEA/write at `0x141ba2198/0x141ba219f`. Its `+0x08/+0x10` entries are
`0x141ba67b0/0x141ba67e0`. The token and token-vector entry are created by the already executed finalizer.

```text
same emitted object -> frame/query/contact -> retained weak target
-> 1415aa660(Positioned+18, blocked/completed trampoline, flying Entity)
-> registered token -> action+310 observer -> +4d0 or +4c8
-> 141ba68b0(target,false) or 141ba68a0(null,true)
-> 141ba68c0 -> continuation or final
```

The dispatcher first traverses its direct-observer vector, then its token vector. It captures each vector's
entry count at that pass's start, rereads indexed entries, and skips nulls. Cleanup `0x1401aa8b0` decrements
dispatch depth and compacts both vectors only at depth zero. This is not an immutable snapshot of entries or
a claim about every mutation/reentrant listener. A supplied direct entry plus the actual registered token
reaches the action twice: the first call completes it; the second sees the completed guard and performs no
second reason/final callback. A null direct entry is removed by the executed cleanup.

### 10.1 Retained target, natural completion and final ordering

For the five supplied final tables covering the six witnesses, with no admitted continuation, target `(13,10)`
stops the same path at committed `(12,10,3)`. The blocked adapter passes the retained target Entity to terminal.
The original common reason-0 helper records ID 101 before its skill `+0x530` call. The following order executes:

1. Select reason 0 and run its common history logic.
2. Choose the target/blocked model helper `0x141baa850`.
3. Set completed byte `+0x332`, then call final skill slot `+0x590` with the contact and `natural=false`.
4. Clear this action's history pointers `+0x438/+0x440` and release its retained reference.
5. Publish the existing end/event notification and set the action retirement flag through the common path.
6. After terminal returns, the blocked adapter resets pending weak target `+0x3a8` and Split flag `+0x3b0`.

At the final skill call the history pointer and pending target still exist, although completed is already true.
This ordering matters to callbacks; do not clear all state before invoking them. A supplied control block with
strong count two becomes one through the actual atomic decrement; last-owner destruction remains unexecuted.
History records are not damage receipts and can remain alive for other owners.

With no target, the last sample instead reaches the completed trampoline and terminal `(null,true)`.
Reason 0 carries `natural=true`, selects `0x141baaa50`, and still completes/releases the action.
The model helpers, nontrivial skill presentation and authored-event listeners remain explicit output boundaries.
Source-suppressed stopping from section 9 still skips notification and terminal entirely.

### 10.2 Actual Return switches the history phase and resumes motion

An ordinary Fireball-table control supplies `projectiles_return=1`, owner grid `(9,9)`, an outbound history
already containing 150, and a 40 ms frame input. Contact 101 reaches the real terminal, ordered qualification,
reason-7 helper, Return executor `0x141bb0710` and redirect `0x141bb2ae0`. This is the same action and object:

- Reason 7 first records 101 in outbound history `[101,150]`.
- The executor sets first-return byte `+0x461`, runs actual eight-word initialization and bounded sampling,
  and caches extra-Return outcome `+0x480`.
- Redirect records the contact in the new phase, giving first-return history `[101]`, and installs the actual
  replacement straight trajectory. The supplied target provides its new segment origin `(13,10)`.
- Terminal does not set completed, release history, call the final skill hook or retire the action. The blocked
  adapter subsequently clears its pending target. The frame returns with active movement and retained residual.
- A subsequent supplied 25 ms update contacts target 150 at `(11,10)`, despite its outbound history membership.
  With no extra Return it selects reason 0 and stops at committed `(12,10)`; both histories are `[101,150]`.
  With extra chance 100 it instead executes another reason 7, sets `+0x462`, records 150 in phase two and resumes.
  This second Return does not reinitialize/redraw the cached extra chance.

Both first-return controls, extra chance 0 and 100, actually draw 91 from the same supplied seed words and finish
at native counter 9, matching the sealed eight-word reference. Zero/100 do not skip this executor's draw.
`skill_projectiles_cannot_return=1` instead selects final before reaching the executor or its draw.
These frame deltas are test inputs, not an inferred client tick contract. Segment extent beyond the owner's
position depends on the retained row/configuration; reaching the owner is not assumed to mean destruction.

### 10.3 Evidence and remaining boundary

Ignored `round53/audit_notifications.py` and `notification-audit.asm/json` preserve 164 complete-unwind/bounded-leaf
ranges, 9897 decoded sites, nine transition anchors, 97 final/observer slots and the constructor table writer.
`check_notifications.py` and `notification-controls.json` execute 16 finite controls, including actual Return
qualification, eight-word initialization/bounded draw, replacement trajectory, new-phase contact and final release.
They extend the sealed interpreter with CDQE, register BTR, PSRLDQ and atomic-XADD semantics; the latter is a
single-thread instruction model, not a concurrency proof.

Construction/clone/equipment/profile/raw-stat/resource inputs, target registration/components/metadata,
numerical prediction, weak-handle operations, model/presentation/event listeners and last-reference destruction
remain supplied or unexecuted. Nontrivial skill reason/final callbacks and authoritative result handling are not
closed by this composition. Neither are actual grant/cache lifetimes, other continuation executors, special movers,
moving-target/world order, complete six-skill releases or mode/owner/monster parity. No native process runs.
The subsequent [same-Actor stage/result join](results-and-protocol.md#61-joining-emitted-contacts-installed-skill-callbacks-and-the-same-actor-queue)
removes the reason-output boundary for selected generic/TS/ordinary-BA and nonfinal Fireball paths; it preserves
the construction, target, prediction, final-presentation and lifetime boundaries stated here.

The [generated TS child follow-up](tornado-shot.md#25-generated-children-through-nonempty-contacts-shared-history-and-return)
instead starts from actual native final-to-child creation and continues through nonempty native buckets and
contact/history/Pierce/Return, without manufacturing another flight/action. It also executes original pending
weak-handle assignment/removal and inline target-observer cleanup, which remain fixtures in this older join.
Numerical prediction, target/component/terrain inputs, storage and complete Entity retirement remain separate.
