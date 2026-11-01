# Client/server projectile contract

Part of [RE 012](../012-projectile-execution.md).
This separates recovered client readers from the server policy that supplies their inputs.
It does not specify undocumented official packet choices.

## 1. Inputs and their observation points

| Input | Consumer / required ordering | Recovered boundary and open part |
|---|---|---|
| Owner, skill, sequence, target/aim, action flags | Root admission and selected final action factory | Wire readers and retained action identity are mapped; remote-owner and all activation policies are not |
| Actual equipment and usable hand | Eligibility, hand selection, arrow-vs-projectile aliases before release | A projectile tag alone does not select bow handling; actual effective values/timing remain K3 |
| Effective count/layout/continuation stats | Selected views at getter-specific times | Raw pairs, computed transactions, overrides and per-action caches differ; publish final aliases where the ordinary getter requires them |
| Buff repeat state and animation/model inputs | Repeat setup, clip selection, event release | Maximum repeat/seal stat is not current count; inner release sees predecrement state |
| Source/release/repeat ordinals, derived allocation | Layout, object hash, motion and skill-stage RNG | Preserve widths, copies and allocation order; do not replace with Entity ID or a new server seed |
| Selected projectile row, resources and variation | High-level release refresh, count preparation, movement and redirects | Initial selection, retained row and later refresh differ; MTX overrides are separate from gameplay count |
| Origin/model scale/height, target height | Placement and mover initialization | Model attachment, terrain/default sentinel and explicit height are not interchangeable |
| Speed, range, duration and mover configuration | Initial movement or the reached redirect | Cached speed variation and residual conversion have their own lifetimes |
| Terrain layers, entities, body/relationship/state | Birth and due-sample queries | Query order/body-aware tests matter; point targets or a generic wall test are approximations |
| Target entropy / reset timer / initialization | Local attack prediction and Stats creation/update | Creation trailer initializes separate component fields; raw stat update is not explicit entropy replacement |
| TLS enclosing state and update order | Reached Pierce/Chain chance consumers and shared draw paths | Conditional arithmetic is recovered; actual gameplay state and surrounding order remain K1 |
| Authoritative hit/control/resource messages | Actor ingress, queue consumers and independent resource updates | Supported routes are recovered; official Fireball direct/area choice remains K2 |

For exact fields/formulas use [execution](execution-model.md), [count inputs](count-stat-inputs.md),
[layout inputs](layout-stat-inputs.md), [continuation inputs](continuation-stat-inputs.md) and
[local prediction](local-hit-prediction.md).
K3 is not solved by filling every fixture with plausible constant values.

## 2. Result identity and ingress

The logical key is `(sequence:u16, derived:u8, stage:u8, targetId:u32)`.
Wire handle order is **sequence, stage, derived, target**, whereas record order places derived before stage.
The Actor vector sorts by unsigned `(sequence,derived,stage)`, then scans the equal range for target.
Upper-bound insertion allows duplicates; exact consumption removes one matching record, not all duplicates.
The allocator epoch is not in the handle, so rollover is not globally unique identity.

For S2C `0x1af`:

```text
if kind bit 0 is clear:
    insert queued record (no action-presence or passed-stage rejection here)
else:
    find current/retained action by sequence and derived
    process immediately with current selected stats, or empty stats/null action
    do not fall back to queue insertion
```

Derived zero selects the current action; nonzero searches retained actions.
The message stage does not select an old stat snapshot.
Kind bit 1 determines damage-package payload presence; optional-field presence is separately flag-controlled.
Fresh factories zero omitted fields, while a direct reread of an existing record can preserve them.
See [decoder/queue details](results-and-protocol.md).

For S2C `0x1b0`, find the first full-key record, remove it and optionally process it.
Absent record is a no-op, not a future cancellation token.
Local exact consumers similarly remove a found record even if later ProcessResult admission rejects it.
Stage/end batching is mode/kind-filtered; ordinary queued records survive in the checked client bindings.
Advancing a stage is not proof that all results for that stage were applied.

## 3. Late messages, ordering and clocks

Local contact can precede or follow result arrival.
The recovered late queued path does not automatically replay the old contact.
Current selected stats, retained action lifetime and exact key are still needed when processing a later control.
Reset clears queue records; no generic per-hit timeout is established by the bounded queue search.
Do not invent a TTL or global no-late-results rule from an observed stage transition.

World headers carry ordering object/checkpoint fields separately from target object ID.
Nonzero-mode updates can interleave eligible incoming records with object/component checkpoints.
After activation, `0x2b5` delimits a frame using the current integer-millisecond `0x2b6` length.
Zero-mode/Predictive update behavior differs; it is not a universal fixed 30 Hz scheduler.
Instance clocks, model animation, Actor updates and Positioned float progress have separate boundaries.
The server framework's 120000 integer units/second and 30 Hz boundaries are LocalExile choices, not recovered
client-wide time units.
See [World inputs](world-message-inputs.md), [World ordering](world-scheduling.md) and
[Instance frames](instance-frame-inputs.md).
Full callback membership, actual threading and official checkpoint/message schedule are unresolved.

## 4. Prediction and authority

The client can create flight objects, sample trajectories, query terrain/entities, predict accuracy/avoidance,
continue projectiles and generate skill stages locally.
That does not establish a C2S per-projectile hit, wall or continuation reporting protocol.
Bounded sender searches in [results](results-and-protocol.md) found no usable such route; this is not a global
proof that no sender exists anywhere.

Real-client observations cited in [external evidence](external-ranged-evidence.md) show different local outcomes
for particular action/input conditions.
They do not establish a universal attack-versus-spell prediction rule or invalidate final-class executable bindings.
Suppressing a server visual requires matching that phase's identity, inputs, geometry and lifecycle, not merely
reaching a shared emitter or seeing a projectile tag.

**Implementation implication (I):** retain server authority over resources, collision/damage admission and numerical
results while reproducing the client inputs and local state needed for coherent prediction.
Do not make an unlocated client report a prerequisite for collision, and do not treat local history as confirmed damage.
`0x1b1` resource assignments and hit presentation/results are separate protocol effects.
An observed effect, consumed queue record or successful prefix admission does not prove numerical damage calculation.

## 5. Six-witness handoff

The [skill guide](skill-witnesses.md) identifies the final classes, key differences and linked formulas.
The finite [matrix](delivery-matrix.md) lists what is usable now.
K1 blocks deterministic TLS-dependent predictions, K2 blocks official Fireball result-route equivalence, and K3
blocks full real-input agreement across the six witnesses.
No complete engine reconstruction is required to use the other recovered contracts.
