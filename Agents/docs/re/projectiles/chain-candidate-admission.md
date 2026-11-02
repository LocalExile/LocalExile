# Chain candidate admission and random-stream consumption

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

This chapter closes the ordered local candidate filter between spatial enumeration and the already recovered
[Chain selector](continuation-and-memory.md#6-chain-target-selection-and-sibling-coordination).
It covers the pinned 3.29.3b executable; it does not describe authoritative damage admission.
The candidate callback can reject a target or consume randomness before ranking sees any record.
Consequently matching only the search radius and nearest-target rule is insufficient for prediction parity.

## 1. Concrete callbacks and input boundaries

The common search `0x141baea60` initializes one five-word context:

```text
[action.sequence11a, action.derived11c, action.chainsDone458,
 action.outerRemaining2d0, owner.objectId88]
```

It supplies an inclusive integer distribution `0..99`, that context and a candidate-output vector to callback
`0x141baef40`, through spatial enumerator `0x141bbb860` and wrapper `0x141bb9aa0`.
The wrapper tail-jumps to the callback at `0x141bb9c22` after its own admission checks.
The callback receives a candidate Entity and the enumerator's signed integer displacement from search center.
Its context retains the current contact, action, distribution, random state and output-vector addresses.
The search center is the contact grid when present, otherwise the flying projectile's grid.
This displacement and the later ranking distance from action `+0x130/+0x134` are distinct inputs.

The [shared initializer and bounded sampler](random-inputs.md#3-four--five--and-seven-word-initialization-and-sampling)
remain the arithmetic source; candidate rejection does not restart the search stream.
The selected wrapper is not Split's `0x141bb9c70`.
Split has a different append path and does not call this Chain-specific prevention/priority callback there.
Do not transfer Chain's probability draw schedule to Split just because both enumerate nearby targets.

## 2. Checks before the prevention draw

Callback `0x141baef40` performs these steps in order:

1. Reject the exact current-contact Entity pointer at `0x141baef53..0x141baef56`.
2. If action `byte3cc` is nonzero, evaluate the conditional direction test below.
3. Select one Positioned byte from both flying projectile and candidate, then apply the asymmetric byte test below.
4. Read the candidate's `prevent_projectile_chaining_%_chance` (`0x1893`).

The first three rejections consume no candidate-prevention sample and append no record.
The direction/Positioned checks therefore affect which random samples later candidates receive.
The checks are not merely post-selection validation of the nearest candidate.

### 2.1 Direction uses the query displacement

The first length helper `0x14015d1e0` computes a wrapping uint32 sum of the integer displacement squares,
converts that unsigned sum through a zero-extended register to float32, and takes float32 square root.
Only a value greater than zero enters normalization and the dot test.
For ordinary finite nonoverflowing inputs, let `D` be that displacement and `B` the float pair at action `+0x3c4`:

```text
x = f32(D.x); y = f32(D.y)
length = f32(sqrt(f32(f32(x*x) + f32(y*y))))
inverse = f32(1.0f / length)
unitX = f32(x * inverse); unitY = f32(y * inverse)
dot = f32(f32(B.y * unitY) + f32(B.x * unitX))
reject if dot < 0
```

The arithmetic is separate scalar SSE operations, including reciprocal multiplication rather than direct
component division and Y-product before X-product in the final sum.
The zero-displacement case skips the test; the perpendicular boundary `dot==0` passes.
The action vector is not normalized by this callback.
It therefore implements a conditional half-plane filter for ordinary nonzero inputs, not a nearest-angle sort.
The integer precheck must not be silently replaced with an unbounded mathematical norm:
in a synthetic input `D=(65536,0)`, its squared sum wraps to zero and skips the direction gate.
This is an arithmetic witness, not evidence that this displacement occurs in a real Chain query.
The [special-contact producer](motion-admission-inputs.md) now binds one actual writer: object-rotation normal,
oriented to the flying projectile's side and stored before ordinary contact admission.
Nonfinite inputs, complete activation/writer coverage and the special object's full identity remain open.

### 2.2 Positioned byte selection is asymmetric

For each Positioned object `P`, the callback selects:

```text
selected(P) = P.byte2c0 if int8(P.byte1e6) < 0 else P.byte2c1
```

It reads the source from the flying projectile Entity at action `+0x328`, and the target from the candidate Entity.
It rejects only when `selected(source)==0 && selected(target)==1`.
This is not a general equality test: source 1/target 0, source 0/target 2 and source 2/target 1 pass this gate.
The [typed motion inputs](motion-admission-inputs.md#3-positioned-selected-bytes-have-base-and-movement-specific-sources)
now bind metadata initialization, straight/arc overrides, restoration and a concrete sign-bit transition.
Those producers still do not establish ground/flying/layer enum names or all actual caller values.

## 3. Nonzero prevention consumes the shared search stream

At `0x141baf030..0x141baf04c`, zero prevention skips sampling.
Every nonzero signed value calls inclusive helper `0x1404d03d0` with the same search-state pointer and bounds `0..99`.
The returned signed integer is compared against the candidate stat; reject when `sample < chance`.
There is no probability clamp or nonzero shortcut in this callback:

| Candidate prevention | Draw? | Result at this gate |
|---|---|---|
| zero | no | pass |
| negative | yes | pass for a normal `0..99` result |
| 1 through 99 | yes | reject when sample is smaller |
| at least 100 | yes | reject |

The helper may consume rejection transitions internally; one bounded sample is not necessarily one raw RNG output.
Neither the processed-history flag, sibling reservation nor eventual selection suppresses an already reached draw.
The selected-source prevention test in Chain qualification instead restarts its own eight-word context;
the [source/candidate distinction](continuation-and-memory.md#61-source-and-candidate-prevention-use-different-random-contexts)
must be preserved.

A supplied-context offline example starts the search with `[1,2,0,0,4096]`:

| Encountered fixture | Candidate sample | Appended? |
|---|---|---|
| exact current contact, chance 50 | none | no |
| chance 0 | none | yes |
| chance -1 | 41 | yes |
| chance 100 | 14 | no |
| rejected direction, chance 50 | none | no |
| chance 50 | 83 | yes |
| chance 1 | 35 | yes |

These are explicit candidate inputs and the previously audited reference sampler, not a recorded encounter order.
They illustrate why removing a rejected candidate or clamping a guaranteed outcome can change later samples.

## 4. Record construction happens after those gates

The admitted 16-byte record is:

```text
+0x00 candidate Entity pointer
+0x08 processed-in-selected-history-phase byte
+0x09 target ID reserved anywhere in shared history byte
+0x0a skill priority byte from virtual +0x648
+0x0b padding
+0x0c uint32 squared grid distance from action +0x130/+0x134
```

History lookup `0x141bae2b0` executes before the reservation loop and priority callback.
The reservation test scans history `+0x48..+0x50`, eight bytes per entry, comparing target ID at entry `+4`.
It does not restrict the match to the current action's derived byte.
Null history yields reserved=false.
The squared-distance subtractions, products and sum wrap at 32 bits; selection later compares the result unsigned.
The append path copies the complete record, or delegates capacity growth to `0x1401edaa0`.
There is no random tie-break or candidate-specific stream reinitialization after append.
The existing selector then applies unseen, unreserved and priority narrowing before the strict minimum-distance scan.

## 5. Wrapper admission remains a separate boundary

The full aligned `0x141bb9aa0` body establishes these additional ordered inputs before the callback:

- Entity `+0x8c` bit 3 must be present.
- Where a Stats component exists, reject trap `0x9bf` and remote mine `0x36c`.
- The Actor branch rejects nonzero `is_water_sphere_owner_id` (`0x3516`) when a Stats component exists.
- Consult the byte matrix at `0x144489800` using `(context.word38 << 8) + (Actor-owner Positioned.word1e0 & 0x7fff)`.
  A nonzero byte rejects; matrix values and their complete runtime producers are not recovered here.
- Reject a present component returned by `0x14013da60` when its byte `+0x20` is nonzero.
- Where a Stats component exists, reject `ignored_by_enemy_target_selection` (`0x1994`).
- If context byte `+0x30` is zero, update its `+0x28` displacement from candidate grid minus query center.
  Otherwise preserve the supplied displacement; this is a conditional scratch write, not a universal recomputation.
- Invoke `0x141d87160` with candidate grid, that displacement, fixed argument 1 and context byte `+0x31`.
  A true result rejects before the Chain callback.

Without an Actor, the wrapper does not tail-call the Chain callback on its checked path;
it takes a component-dependent skip or diagnostic branch.
The deeper path/terrain helper, matrix lifecycle, other component identity and traversal mutation still need recovery.
An accepted callback record does not prove that all arbitrary Entities qualify as future contacts or receive damage.

## 6. Evidence and validation

Ignored round37 material includes `audit_chain_candidates.py`, `chain-candidates-original.asm`,
`chain-candidates-audit.json`, `check_chain_candidates.py`, `chain-candidates-controls.json`
and the fresh same-agent review in `review_chain_candidates.py` / `chain-candidates-review.json`.
The audit binds ten known functions/leaves, their aligned original ranges and six Stats names.
This is semantic completion of known bodies, not ten newly discovered functions.

The candidate-body interpreter checks 1348 independent finite/gate/record fixtures and one seven-candidate flow.
It executes the original four vector-helper instruction bodies; Stats, history predicate, priority, bounded samples
and capacity growth remain explicit fixtures.
The shared-flow sampler is the previously audited reference, not reimplemented native RNG in this interpreter.
The wrapper is checked statically, rather than included in the candidate interpreter's execution count.
No native client execution, capture, authoritative hit resolution or complete input-producer claim follows.
