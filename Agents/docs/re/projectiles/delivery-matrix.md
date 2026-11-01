# Frozen projectile research delivery matrix

Part of [RE 012](../012-projectile-execution.md); source freeze and evidence types are defined there.
This is a finite contract inventory, not a research-round score or an implementation completion percentage.

Status **conditional** means the rule is recovered for the stated bindings/inputs; actual-input parity is not proven.
**Partial** means a named decision remains unresolved inside that contract.
T checks are distributed and runnable; E checks are retained historical audits, not public native replays.
Every row inherits the executable/version restriction in [evidence](evidence-and-open-questions.md).

## 1. Common contracts

| ID / status | Recovered rule; inputs → outputs | Conditions | Evidence / verification | Unresolved obligation |
|---|---|---|---|---|
| C1 Identity — conditional | Sequence + allocator + source/release ordinals → registered action/derived identity, object seed and result key | Checked ordinary and generated actions; preserve widths/order | [Emission](actions-and-emission.md), [results](results-and-protocol.md); T RNG/hash vectors, E admission/TS controls | Actual ordering and wrap coexistence policy; K3 |
| C2 Counts — conditional | Effective generic/arrow bases, additions and conversion flags → total/release counts | Four actual computed callbacks and consumer-specific clamps/bypasses | [Count formulas](count-stat-inputs.md), [release](release-and-repeat.md); S/E callback and release audits | Which effective values are published/refreshed at each witness read; K3 |
| C3 Layout — conditional | Selected fan/fixed/Volley/radial/random inputs + aim/origin → rays and side-origin admission | Dispatcher precedence; TS secondary overrides; finite numeric domains | [Layout](layout-stat-inputs.md), [geometry/emission](actions-and-emission.md), [admission](emission-admission.md); S/E geometry joins | Actual profile/terrain/model values, complete end-to-end float agreement; K3 |
| C4 Repeats — conditional | Remaining/total counts + Buff/sequence + animation events → releases, ordinal retention/reset | Inner rewind versus outer restart; synthetic start separate | [Repeat lifecycle](release-and-repeat.md), [Buff state](repeat-source-state.md), [animation](animation-and-release.md); S/E | Actual Buff/model/event state and read-update order; K3 |
| C5 RNG — partial | Explicit 4/5/7/8-word recipes → finite draws; hash inputs → object seed | Preserve initializer, wrapping, rejection and conditional draws | [Random inputs](random-inputs.md); T stream/object/TLS scalar vectors; E branch audits | TLS gameplay initial/enclosing state and cross-consumer order; K1 |
| C6 Motion/height — conditional | Integer endpoints, explicit/sentinel height, speed and frame deltas → due samples, interpolation state and callbacks | Finite sampler, separate arc polynomial/curve paths and redirect lifetimes | [Motion](motion-and-contact.md), [speed/time](motion-speed-and-time.md), [configuration](motion-configuration-inputs.md); T raster; S/E joins | Actual origin/model/terrain/frame inputs and all exceptional numeric branches; K3 |
| C7 Contact — conditional | Terrain layers + buckets/body/state/relationships + local prediction → admitted contact/Pierce/retained stop | Real candidate/filter order; local admission is not damage | [Motion](motion-and-contact.md), [prediction](local-hit-prediction.md), [probability](attack-hit-chance.md); S/E | Actual entropy/input corrections, wider special contacts; K3 |
| C8 History/shotgun — conditional | History policy + phase + contact → shared/new/deep-copied histories and reservations | Explicit preserve-history; avoidance can be remembered | [History](continuation-and-memory.md); S/E generated TS contact/continuation/retirement controls | Authoritative damage/area overlap remains skill/server-specific; K2/K3 |
| C9 Continuations — conditional | Budgets/prohibitions/counters + candidate order → Pierce/Fork/Split/Chain/Bounce/Return | Recovered ordered gates; grants do not imply legal skill/support capability | [Continuation](continuation-and-memory.md), [stat formulas](continuation-stat-inputs.md), [Split](split-target-order.md), [Chain](chain-candidate-admission.md); S/E | TLS-dependent outcomes K1; legal effective grants and actual timing K3 |
| C10 Results — partial | S2C record/control + retained actions/current views → queued/immediate/exact/stage processing | Duplicate/key and mode/kind filters; prefix admission != damage | [Results](results-and-protocol.md), [C/S contract](client-server-contract.md); S/E decoder/queue controls, T synthetic analysis | Official Fireball direct/area route K2; full numerical damage downstream |
| C11 Scheduling — partial | Mode + frame budget + ordering fields → World/Actor/component ingress checkpoints | Predictive differs from nonzero queued-ms mode; frame/sample clocks differ | [World](world-scheduling.md), [message inputs](world-message-inputs.md), [Instance](instance-frame-inputs.md); S/E | Actual callback membership/thread and official ordering; K1/K3 |
| C12 Lifecycle — conditional | Completion latch + retained owner/history/listeners → source retirement and deferred flight removal | R81 TS Fork/Split sources with live descendants; selected controls | [TS retirement](tornado-shot.md#27-retired-forksplit-sources-through-actor-cleanup-and-history-ownership); E R81, public sanitized audit receipt | Entity queue not drained; populated teardown/whole engine outside delivery |

## 2. Six ordinary witnesses

These rows are client contracts, not six newly implemented or live-accepted server skills.
All inherit K1 where TLS paths are reached and K3 for actual input/publication timing.

| ID / status | Recovered rule; inputs → outputs | Conditions | Evidence / verification | Remaining gap |
|---|---|---|---|---|
| W1 Bow — conditional | Ranged Default Attack + actual Bow/arrow flag → arrow count/resource, attack gate and shared continuations | Final ranged `Melee` registration, ordinary equipment path | [Bow/wand/BA](bow-wand-and-burning-arrow.md); S bindings, E shared composition, separately cited O captures | Not a full Bow equipment/model release replay; main capture policy is not universal native incapability |
| W2 Wand — conditional | Same final ranged class + non-Bow equipment → projectile alias/WandProjectile resource and local attack gate | Ordinary wand branch; missing resource can return null | [Bow/wand/BA](bow-wand-and-burning-arrow.md); S bindings/resources, E shared composition | Not a separate complete Wand release replay; all MTX/off-hand cases excluded |
| W3 Fireball — partial | Concrete factory + mover/reason/radius/resource inputs → flight, terminal effect and stage transitions | Ordinary variation; direct/area presentation and queue admission separated | [Fireball/KB](fireball-and-kinetic-blast.md), [radius](area-radius.md), [results](results-and-protocol.md); S/E, cited O prediction | K2: which official route represents direct and extra area targets; no inferred double hit or shotgun damage |
| W4 KB — conditional | Source reason/contact + count/radius/seed/terrain → registered explosion group, centers, timed effects and exact target results | Trigger target excluded; only retained centers advance timing/stages | [Fireball/KB](fireball-and-kinetic-blast.md); S/E source-group/queue/time joins, T RNG building blocks | Actual first update, stats/terrain and official send order; not all KBoC variants |
| W5 TS — conditional | Mother final + independent child count/group seed + allocator → wind, quantized radial children and histories | Ordinary primary/secondary; final bindings and selected continuations through retirement | [TS](tornado-shot.md), [geometry](tornado-geometry.md); T terminal RNG, E generated-child chain through R81 | Not universal homing, full lifetime or live geometry parity; actual continuation grants K3 |
| W6 Burning Arrow — conditional | Final BA class + equipment/variation/Vaal gates → ordinary arrow, reason stages and deferred presentation | Ordinary BA; special explosion branches stay gated | [Bow/wand/BA](bow-wand-and-burning-arrow.md); S bindings, E ordinary same-Actor reason/stage controls | Ordinary controls do not prove Vaal/variant damage or full attachment/render behavior |

## 3. Using the matrix

Use the linked formulas and transitions for later implementation under explicit input assumptions.
Do not implement a missing input producer by silently choosing a plausible constant or treating a stat name as a rule.
K1/K2/K3 have finite followups in [verification and limitations](verification-and-limitations.md).
Public tools reproduce scalar/reference and synthetic capture checks; the historical E joins are auditable claims
with fixed local provenance, not fully distributable execution suites.
