# 012 — Client projectile execution and server contracts (3.29.3b)

The client executes projectile emission, movement samples, local contacts, continuations and skill stages.
It needs consistent action identities, effective inputs and authoritative results from the server.
Recovering that computation does not reveal the official server implementation or make prediction authoritative damage.

This research delivery is frozen at `dd7a33465a448de12ccc18fa0a6437b7a60481b5` (R81).
It contains no server implementation and does not claim full native or official-server parity.
The [delivery matrix](projectiles/delivery-matrix.md) identifies recovered contracts and remaining gaps.
The pinned executable and source provenance are in [evidence](projectiles/evidence-and-open-questions.md).

## 1. Developer reading order

| Read | Purpose |
|---|---|
| [Execution model](projectiles/execution-model.md) | Rules, formulas, ordering and state transitions from action start to retirement |
| [Client/server contract](projectiles/client-server-contract.md) | Inputs to publish, timing boundaries, result identities and authority |
| [Six witnesses](projectiles/skill-witnesses.md) | Ordinary bow, wand, Fireball, KB, Tornado Shot and Burning Arrow differences |
| [Delivery matrix](projectiles/delivery-matrix.md) | Finite coverage, evidence, reproducibility and open obligations |
| [Verification and limitations](projectiles/verification-and-limitations.md) | Runnable checks, evidence classes, critical gaps and bounded followups |

Read these before the detailed references below.
The references preserve field-level evidence and local audit entry points; they are not an execution backlog.
Round labels identify historical provenance, not a current work queue or a completion measure.
The former round-by-round entry and ledger remain in the frozen source commit instead of being duplicated here.

## 2. Evidence and interpretation

- **S — static:** bounded instruction, binding and data-flow evidence in the pinned executable.
- **T — translated reference:** runnable arithmetic or synthetic observer/analyzer checks.
- **E — interpreted instructions:** historical execution of selected original instructions with explicit fixtures.
  The original code, private harness and complete ancestor graph are not distributed here.
- **O — observed:** a cited real-client observation or official capture, with its source/version and scope.
  There is no new live recording in this delivery.
- **L — LocalExile:** project implementation at a stated revision; not evidence of official behavior.
- **I / U:** inference or unresolved question, respectively; neither is promoted to a measured result.

A closed static chain can guide implementation without a recording.
A passing reference or interpreted fixture does not establish actual inputs, official message choice, thread
membership, full execution order or end-to-end agreement.
Local processed-target memory is not a damage receipt; effect creation is not an extra damaging hit.

## 3. Detailed references

| Contract | Reference chapters |
|---|---|
| Admission/initialization | [Root policy](projectiles/root-eligibility-and-policy.md), [initialization](projectiles/action-initialization.md), [hands](projectiles/hand-selection.md) |
| Emission/effective inputs | [Actions/emission](projectiles/actions-and-emission.md), [counts](projectiles/count-stat-inputs.md), [layout](projectiles/layout-stat-inputs.md), [admission](projectiles/emission-admission.md) |
| Release/repeats | [Lifecycle](projectiles/release-and-repeat.md), [Buff state](projectiles/repeat-source-state.md), [animation](projectiles/animation-and-release.md) |
| Randomness | [Contexts and consumers](projectiles/random-inputs.md), [TS geometry](projectiles/tornado-geometry.md) |
| Motion/contact | [Sampler/queries](projectiles/motion-and-contact.md), [speed/time](projectiles/motion-speed-and-time.md), [configuration](projectiles/motion-configuration-inputs.md), [admission flags](projectiles/motion-admission-inputs.md) |
| Objects/prediction | [Type binding](projectiles/object-type-binding.md), [prediction](projectiles/local-hit-prediction.md), [attack probability](projectiles/attack-hit-chance.md), [KineticBarrier](projectiles/kinetic-wall-contact.md) |
| Continuations/memory | [State/history](projectiles/continuation-and-memory.md), [stat formulas](projectiles/continuation-stat-inputs.md), [Split ordering](projectiles/split-target-order.md), [Chain admission](projectiles/chain-candidate-admission.md) |
| Scheduling/synchronization | [World](projectiles/world-scheduling.md), [incoming records](projectiles/world-message-inputs.md), [Instance clocks](projectiles/instance-frame-inputs.md), [synthetic commands](projectiles/synthetic-start-and-server-commands.md), [results](projectiles/results-and-protocol.md) |
| Skill specialization | [Bow/wand/BA](projectiles/bow-wand-and-burning-arrow.md), [Fireball/KB](projectiles/fireball-and-kinetic-blast.md), [TS](projectiles/tornado-shot.md) |
| Area/presentation inputs | [Radius](projectiles/area-radius.md), [increased terms](projectiles/area-increased-terms.md), [more factors](projectiles/area-more-factors.md), [resources](projectiles/resource-selection.md) |
| Provenance | [Capture contract](projectiles/capture-contract.md), [external evidence](projectiles/external-ranged-evidence.md), [evidence index](projectiles/evidence-and-open-questions.md) |

Wire layouts remain in [11 — skills](../protocol/11-skills.md) and its linked protocol chapters.
Public/community descriptions, executable rules and LocalExile policy are different source classes.
Do not silently substitute one for another.

## 4. Delivery boundary

Recovered arithmetic, selected final bindings and bounded state transitions are reviewable now.
Critical gaps are gameplay-affecting TLS scope/order, Fireball official direct/area result routing, and actual
effective inputs/read-update timing for the six witnesses.
They prevent deterministic native or official-server parity claims, without invalidating unrelated recovered rules.
See the [finite followups](projectiles/verification-and-limitations.md).
