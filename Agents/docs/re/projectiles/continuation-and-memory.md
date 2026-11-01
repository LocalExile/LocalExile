# Shared contact memory and continuation (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [012 — projectile execution](../012-projectile-execution.md).
This is a bounded static reconstruction of shared methods with selected TS/ordinary-bow vtable bindings.
Skill overrides and authoritative damage rules still require separate evidence.

## 1. Contact admission and terminal are different decisions

**S:** `0x141ba7b30` receives local candidate entities from birth/frame queries.
Its branches include action/owner/target validity, team/state filtering, processed-target memory, local hit/avoidance
prediction, Split, Pierce and retaining a target for terminal.
Some locally missed/avoided targets are inserted into memory too.
Therefore a recorded contact does not imply successful damage.

The following omits guards and special objects; it is not complete server-ready pseudocode:

```text
OnCandidate(target):
    if invalid / completed / pending contact: ignore
    if currentPhaseMemory contains target: ignore
    if applicable local miss/avoid branch:
        remember target; ignore
    if Split eligible:
        save target and pendingSplit=true; stop mover
    elif Pierce eligible:
        DispatchReason(Pierce, target); increment piercesDone; continue mover
    else:
        save target and pendingSplit=false; stop mover
```

The traced attack gate reaches `0x141b6f950 → 0x14193a8c0 → 0x141ebcff0 → 0x141ebc990`,
then the applicable entropy branch `0x141ee4920 → 0x141ebd1a0`.
It reads accuracy/evasion/always-never-hit inputs without waiting for a queued S2C result.
The concrete ordinary Fireball spell gate instead reaches local spell-dodge helper `0x141ebd2f0`;
its action-identity inputs are not a queue lookup.
The [local prediction contract](local-hit-prediction.md) identifies target-owned entropy, its creation trailer
and reset timer, plus the separate spell-avoidance identity recipes and explicit caller modes.
The [ordered probability producer](attack-hit-chance.md) includes native finite power and the entropy handoff.
Actual input publication, update order and further skill checks remain separate;
see [the concrete witnesses](skill-witnesses.md).

The [frame-to-contact composition](motion-and-contact.md#9-joining-frame-queries-to-the-installed-ordinary-contact-and-history)
now joins the same emitted object and integer samples through real bucket filters and the installed ordinary capture
to this handler, native history lookup/insertion and shared Pierce. It checks remembered avoidance, retained stops,
reason-2 history and supplied return partitions with explicit target/component/prediction/weak-reference fixtures.
At that earlier boundary, a retained stop has not yet run the final callback or necessarily inserted that target
into history. The later [registered-terminal composition](motion-and-contact.md#10-joining-registered-notifications-to-terminal-and-return)
executes the blocked/completed dispatch and common terminal: reason history precedes completed/final/release,
and the blocked adapter clears pending contact afterward. It also executes actual Return, replacement motion,
new-phase memory and a second contact. Nontrivial reason/stage subscribers and authoritative results remain separate.

Pierce getter `+0x678 → 0x141ba96a0` reads cannot/always-pierce, return-always-pierce and other conditions,
and the finite-budget path consumes remaining count `+0x44c`.
`+0x454` separately counts executed pierces; those are not interchangeable fields.

The later [TS final-to-child composition](tornado-shot.md#21-joining-final-completion-to-actual-child-construction-and-release)
executes the installed generator, original copies, registration and release through fresh child-history
allocation. By default, siblings from one mother share a new history; another mother starts another history,
and the mother's remembered targets are not inherited. Nonzero `can_shotgun` gives each child its own history.
The shared identity allocator is retained independently of that history reset. These are local contact-memory
rules, not proof of authoritative damage quotas, and the composition stops at the client mover-call inputs.

The later [TS placement/mover join](tornado-shot.md#23-joining-the-dedicated-origin-callback-to-actual-straight-movement)
shows why copied continuation state also matters for geometry: the ordinary child origin callback restores
saved mother XY/height, while the initial-secondary target-height override requires zero Chain/Fork counters,
zero byte `460` and zero returning byte. A returning mother's children retain their returning flag; their
native movement cannot be replaced with the initial flat-height shortcut. Effective shared-mover overrides
and full descendant lifetime remain separate.

The [generated TS child frame/terminal join](tornado-shot.md#24-generated-children-through-native-frames-and-terminal-history-release)
also executes release of these actually generated histories. Ordinary siblings decrement one shared control
block; only the last invokes its payload destructor. Shotgun children release distinct blocks. The native
strong/weak transitions execute with explicit vector/control-storage APIs. Completed actions still retain
their flight pointer under the retirement-delivery boundary, so history release is not Entity destruction.

The [generated-child continuation composition](tornado-shot.md#26-generated-children-through-fork-split-and-chain)
extends the same actual TS children through Fork/Split clones and Chain redirects. Shotgun descendants deep-copy
ancestor contacts rather than forgetting them, while shared Chain histories coordinate equal-position choices
through reservations. Fork/Split source retirement retains its history in the bounded trace; it is not the
reason-zero final-release path. Effective grants, input geometry and deferred retirement remain boundaries.

### 1.1 Pierce qualification order, probability and budget side effects

The complete shared method `0x141ba96a0..0x141ba99cf` applies the following branches in order.
All five final client tables covering the six witnesses bind `+0x678` to this method.
An earlier return prevents all later checks:

| Order | Condition | Result |
|---|---|---|
| 1 | `action.mode3c0 == 1` | false |
| 2 | `virtual_projectiles_cannot_pierce` (`0x2247`) nonzero | false |
| 3 | positive `projectile_chance_to_not_pierce_%` (`0x3438`) succeeds through `0x140e3b450` | false |
| 4 | `action.byte330 & 4` | true |
| 5 | nonzero Fork generation `+0x45c`, `+0x598()` and bool stat `0x2194` | true |
| 6 | nonzero executed Chain count `+0x458`, `+0x598()` and the **same** bool stat `0x2194` | true |
| 7 | `+0x6a0()` and `returning_projectiles_always_pierce` (`0x28f3`) | true |
| 8 | target stat `projectiles_always_pierce_you` (`0x18e0`) | true |
| 9 | `arrows_always_pierce` (`0x18db`) when `+0x598()` is true, otherwise `virtual_always_pierce` (`0x18dd`) | true when nonzero |
| 10 | absolute Volley slot `+0x450 == 1`, `+0x598()` and `arrows_from_first_firing_point_always_pierce` (`0x3062`) | true |
| 11 | `projectiles_pierce_all_nearby_targets` (`0x18e6`) and owner-to-target grid XY squared distance at most 1225 | true |
| 12 | nonzero `projectiles_pierce_all_targets_in_x_range` (`0x343a`) and its squared-range comparison passes | true |
| 13 | target `is_burning` (`0xe42`) and caster `always_pierce_burning_enemies` (`0x48a7`) | true |
| 14 | remaining finite budget `+0x44c` is nonzero | decrement it and return true; otherwise false |

The data name of `0x2194` is `arrows_always_pierce_after_forking`; both native branches really query that key.
Do not replace the Chain branch with another stat based only on the name.
For ordinary, nonoverflowing coordinates/ranges, branch 12 is inclusive `R² >= distanceSquared`.
Its exact arithmetic uses a wrapped 32-bit XY squared sum zero-extended before float conversion, versus a
wrapped 32-bit range square converted as signed float32; exceptional integer inputs need those distinctions.
Branch 11 compares the wrapped squared sum unsigned. Branch 14 tests **nonzero**, not signed positive;
negative-budget reachability is not established.

The five final tables bind `+0x6a0` to the returning-byte `+0x461` getter.
Their `+0x598` differs: Fireball is constant false; ranged/BA/KB/TS use the equipment/type and
`skill_can_fire_arrows` (`0x354`) test, not unconditional true.
TS's forced-primary bit therefore remains subject to the preceding mode/cannot/probability rejection.
Neither that bit nor return-always-pierce overrides those earlier guards.

Constructor `0x141ba2160` caches `projectile_number_of_targets_to_pierce` (`0x18d7`) into `+0x44c` at
`0x141ba2336`, while zeroing executed count `+0x454`.
Copy `0x141ba2410` preserves the **current remaining** `+0x44c` and executed `+0x454`.
Qualification does not reread `0x18d7`; later stat changes do not automatically refill this cache here.
Other stat branches select the current effective view when reached.
The method is not a pure getter: calling it a second time can spend another budget unit or random sample.

After successful qualification, contact caller `0x141ba8a15..0x141ba8a39` first dispatches reason 2,
then increments executed `+0x454` independently of which success branch was used.
Early true branches do not spend `+0x44c`; probability rejection spends neither counter.
The reason callback can have additional effects; neither counter is a damage-hit receipt.
The [random-input supplement](random-inputs.md#69-pierce-prevention-rolls-at-each-qualification) explains
the uncached TLS gate. Round17 `pierce-findings.zh-CN.md`, `pierce-witnesses.asm` and `pierce-audit.json`
retain 10 aligned spans, 624 instructions and 15 final-table bindings.

## 2. Corrected terminal order: Bounce is not Return

**S:** shared terminal `0x141ba68c0` contains the following eligible branches:

| Order / local reason | Path | Effect |
|---|---|---|
| Split / 1 | `+0x5f8 → 0x141ba6210`, emitter `0x141bae540` | emit descendants and retire source |
| Skill-specific / 3 | `+0x680`, then `+0x520` | skill-controlled continuation |
| Fork / 4 | `+0x640 → 0x141bb6600`, `0x141bae3d0 → 0x141ba6380` | increment generation, emit descendants |
| Chain / 5 | `+0x638 → 0x141bb6530`, `0x141bb0250 → 0x141baea60 → 0x141baf2a0` | choose/reserve target and normally redirect existing action; alternate skill chain hook also exists |
| Random/terrain bounce / 6 | `0x141baf130 → +0x768 → 0x141bb09c0` | reads `projectiles_bounce_*`, not Return |
| Other optional hook | shared terminal branch | not fully reconstructed |
| Return / 7 | `+0x698 → 0x141bb04f0`, then `+0x518 → 0x141bb0710` | change return phase and redirect, or a separate clone path |
| Final / 0 | set completed, skill `+0x590`, release own history reference | final skill-specific stage and retirement |

Pierce uses local reason 2 in the contact path.
These **reason numbers** passed to skill hooks are not the network result-stage byte `action+0x120`.
That byte has its own increment/consumption lifecycle in [results and protocol](results-and-protocol.md).

The prior research's `baf130 → bb09c0 = Return` label was incorrect.
Reading the bounce stat family and following the later `+0x698/+0x518` calls resolves the distinction.
Do not infer a mechanic from the fact that it sets a new trajectory.

The terminal order is conditional, not a universal fixed chain:

- Explode-only and skill-specific branches can bypass continuations.
- Targetless natural-end Split first requires `projectiles_can_split_at_end_of_range`, then still checks
  `projectiles_can_split_from_terrain` in the traced branch; do not simplify these checks into an OR.
- `arrows_that_pierce_also_return` (`0x1c64`) in `0x141bafd47..0x141bafd8e` invokes Return from the Pierce reason,
  using `redirect=false` to emit a returning clone while preserving the continuing source path.
  The source keeps moving, but is not state-unchanged: `0x141bb0710` writes its Return phase before choosing
  redirect versus clone (`+0x461` at `0x141bb0833`, or `+0x462` at `0x141bb08e2`).

## 3. State retained by related projectile actions

**S:** these fields are read/written by the traced shared code:

| Action field | Observed role |
|---|---|
| `+0x328` | associated flying entity |
| `+0x332` | final-completed guard |
| `+0x3a8`, `+0x3b0` | pending contact handle and whether it requests Split |
| `+0x438/+0x440` | history payload and shared-pointer control block |
| `+0x454/+0x458/+0x45c` | executed pierces / chains / fork generation |
| `+0x460` | Split already executed |
| `+0x461/+0x462` | first / additional Return phase state |
| `+0x468/+0x470` | bounce-related count/budget state; not Return count |
| `+0x478/+0x47c` | prepared additional-Chain outcome / close-range Chain budget compensation |
| `+0x480` | additional-Return chance outcome |

The action copy constructor `0x141ba2410` copies budgets and phase bytes but explicitly zeros the new `+0x438/+0x440`.
History sharing is chosen by the emitter afterward.
It is not an accidental consequence of cloning a shared pointer.
Fork increments source generation before cloning; Split sets its executed flag before emission.
The traced descendants inherit those updated values and the other copied continuation state.
No unconditional reset of all budgets was found in these paths.

## 4. The history payload has three target sets and a reservation table

**S:** payload layout, deep-copy helper `0x141bb8840`, and contains/insert `0x141bae2b0 / 0x141bae0c0` establish:

```text
history +0x00: vector<uint32> processed targets, phase 0
history +0x18: vector<uint32> processed targets, phase 1
history +0x30: vector<uint32> processed targets, phase 2
history +0x48: vector<8-byte {derivedActionByte, padding, nextTargetId}> reservations
```

Each vector has a 24-byte header.
Target sets are ordered and selected by:

```text
if !skill.slot6a0(): phase0
elif !action.byte462: phase1
else: phase2
```

TS and ordinary-bow tables bind `+0x6a0` to `0x141241550`, which returns action `+0x461`.
For these bindings, the three sets correspond to outbound, first Return and additional Return.
Other skills can override the phase getter.
First Return can revisit outbound targets without clearing the outbound set.
Processed-target memory includes applicable miss/avoid cases; it is not a damage receipt ledger.

`0x141bae1d0` erases only the current derived action's reservation from `history+0x48`.
It **does not clear the target sets**.
Reason dispatcher `0x141baf5f0` performs that reservation cleanup, remembers a nonnull current contact in the selected
phase, and invokes skill contact hook `+0x530`, with additional special branches around those steps.

## 5. Shotgun and history inheritance

**S:** getter `+0x550 → 0x141bb5ad0` reads `projectiles_can_shotgun` (`0xb2b`).
The shared emitter uses it together with an explicit preserve-history parameter:

| canShotgun | preserveHistory and source history present | Result |
|---|---|---|
| false | true | related emitted actions share source history |
| false | false | the batch shares a newly allocated empty history |
| true | true | each emitted action gets its own deep copy of source history |
| true | false | each emitted action gets its own empty history |

The explicit contact ID, when supplied, is inserted into each selected child phase.
Split and Fork pass preserve-history=true in the traced shared paths; the Return clone path also preserves history.
Shotgun children can therefore retain ancestor contacts while independently recording future contacts.

**I:** the community's shared-hit-ID intuition has a concrete counterpart, but the client model is a shared/copied
history payload, phase selection and inheritance policy.
It is not a single globally consumed projectile ID.
This does not by itself settle all skill-specific shotgun rules, secondary-area overlap or authoritative damage admission.

## 6. Chain target selection and sibling coordination

**S:** ordinary Chain usually reuses the action/entity through `0x141baf2a0 → +0x620 → 0x141bb2ae0`.
It increments executed chains and writes a reservation keyed by this action's derived byte.
The five final classes covering six witnesses share budget `+0x638 → 0x141bb6530` and priority `+0x648 → true`.
For those bindings, the signed budget is:

```text
virtual_number_of_chains_for_projectiles[223f]
  + bool(byte478) + int(mode3c0==3) + int47c
  + (int450==4 && slot598() && !stat2241 ? stat3065 : 0)
```

`stat2241` is `virtual_projectiles_cannot_chain`; `stat3065` adds chains from the fourth firing point.
This getter does not clamp and does not apply cannot-chain as an independent universal early rejection.
Effective virtual Stats still require their upstream stat graph; this is not a formula for summing raw equipment mods.
Preparation caches `additional_chain_chance_%` (`0x227d`) into `byte478` through a TLS probability helper,
not an action-seeded stream; see [the exact sampler](random-inputs.md#63-additional-chain-budget-is-a-non-cosmetic-tls-consumer).
Normal construction zeros `478/47c`; cloning copies them.
Preparation's `+0x1e8` gate can skip the write; every actual arrival at its probability write point overwrites `478`.
Initial siblings copy the same preparation outcome but have independent executed-Chain `458` fields.
Later descendants inherit the source's current consumed count as well as its cached outcome;
the checked TS copy chain and secondary template do not reset these fields.
Providing final stat `223f` does not bypass the separate `478` addition or probability roll.
Successful close-range Chain increments `47c` as well as executed count `458`, preserving remaining budget.
Its distance check is owner to source contact/projectile, squared distance unsigned `<400`.

Eligibility `0x141bb0250` checks mode 1 first and returns `!stat2241`, bypassing the later ordinary tests.
Otherwise exhausted budget can be rescued by close-range stat `0x3066` and the same `<400` distance check.
With a contact it then checks `skill_cannot_chain_from_enemies` (`0x3837`), target cannot-chain-from (`0x1268`),
and target prevent-chain chance (`0x1893`).
Without a contact, natural completion rejects; only a blocked stop can test terrain-chain chance (`0x371d`).
The mode enum and special-object transition from mode 1 to 3 are not fully named here.

Round16 `additional-chain-budget.zh-CN.md` and `additional-chain-witnesses.asm` trace preparation,
the common/TS copy chain, the five concrete final-class bindings and the separate executed-counter increment.
This closes the cache/copy/budget data flow without claiming an official TLS synchronization policy.

The target search in `0x141baea60` centers on the current contact, or projectile grid if absent.
Its float32 radius is `max(1,trunc(((float(stat2ee8)+100)/100)*60))`, preserving that operation order.
The broad bucket rectangle includes an additional global padding value whose runtime source is not closed.
Query `0x141bbb860` visits X buckets then Y rows, forward records, only eligible rank-zero entries.
It admits the inclusive unsigned squared-distance boundary with signed candidate body radius:
`distanceSquared <= (radius+int8(Positioned+1e3))^2`.
Component/state/relation/path filters follow; a mutation during callbacks may affect traversal.

Candidate helper `0x141baef40` records three flags plus squared distance from stored action coordinates `+0x130/+0x134`:

1. Already processed in the selected phase.
2. Reserved by any action sharing this history.
3. Preferred by skill hook `+0x648`.

The selector progressively narrows the list:

```text
if any candidate is unseen: remove seen candidates
if any remaining candidate is unreserved: remove reserved candidates
if any remaining candidate has skill priority: remove ordinary candidates
choose minimum stored-coordinate squared distance among survivors
```

Previously seen/reserved candidates are not unconditionally hard-filtered at this selection layer.
Later contact memory and skill rules still decide what happens when the projectile reaches a target.
Do not rename the stored-coordinate distance as distance from the current collision point without tracing its writers.
All three narrowing passes preserve survivor order.
The final strict unsigned minimum keeps the **earliest surviving traversal entry** on equal distance;
it does not use object ID or draw another random tie breaker.
The five checked final classes return true for priority, so that hook does not distinguish their ordinary candidates.

Input reader `0x141938bf0` writes stored aim `+0x130/+0x134`, replacing it with current target grid when a target
handle resolves; base clone `0x141938020` copies it.
Preparation/release can replace a coincident-source aim with a deterministic fallback.
The checked ordinary Chain executor and redirect update segment origins but do not overwrite this stored aim.
Thus search center and ranking reference differ; neither must be called the original mouse position.
TS secondary templates explicitly write their own aim; see [TS continuation](tornado-shot.md).

### 6.1 Source and candidate prevention use different random contexts

The source contact's nonzero `prevent_projectile_chaining_%_chance` draws integer `[0,99]` from a freshly initialized
eight-word context `[sequence,derived,owner,outerRemaining2d0,innerRemaining2b8,0,0,0]`.
Pass is `draw>=chance`, without a percentage clamp; even negative or at-least-100 values consume the draw.
Terrain qualification instead skips sampling for chance `<=0` or `>=100`.
Both are separate from candidate selection.

The search initializes one five-word stream
`[sequence,derived,chainsDone458,outerRemaining2d0,owner] → 0x141a0c9d0`.
Each candidate reaching its nonzero prevention stat consumes `[0,99]` from that shared search stream,
before ranking, even if another candidate is eventually chosen.
The candidate helper also excludes the current contact, applies a conditional directional dot-product test
and compares selected source/target Positioned bytes.
The full field producers, physical-layer names and effective-stat graph remain open.
The [ordered candidate filter](chain-candidate-admission.md) closes the conditional displacement half-plane,
asymmetric selected-Positioned-byte test, exact skip/draw conditions and post-admission record construction.
Its source/candidate byte rule is `source==0 && target==1`, not general equality.
Earlier rejection consumes no candidate sample; negative and at-least-100 nonzero prevention still consume one.
The concrete Chain wrapper's relation-matrix/path inputs remain separate from this closed callback computation.

## 7. Return changes phase and can occur more than once

**S:** complete qualification `0x141bb04f0..0x141bb0703` applies these checks in order:

1. `skill_projectiles_cannot_return` (`0x5480`) rejects when nonzero.
2. If `+0x6a0()` says already returning, require `+0x480 != 0` and `+0x462 == 0`.
3. `+0x498()`, nonzero `0x36d` or `0x36c`, and `first_and_final_barrage_projectiles_return` (`0x50cb`) permit Return.
4. Absolute firing slot `+0x450 == 3`, `+0x598()` and `arrows_from_third_firing_point_return` (`0x3064`) permit it.
5. Nonzero `projectiles_return` (`0x46d`) permits it.
6. Nonzero `attack_projectiles_return` (`0x9c0`) and `+0x578()` permit it.
7. Otherwise use signed `projectile_return_%_chance` (`0x3216`) as described below.

The already-returning check is only a prerequisite: it does **not** bypass the later ordinary qualification rules.
The five final classes bind `+0x698/+0x518` to the same qualifier/executor and `+0x6a0` to the `+0x461` getter.
Fireball's attack-test `+0x578` is false; the other four classes return true there.
The final arrow test `+0x598` remains equipment/stat-dependent outside Fireball's constant-false binding.
These are actual table bindings, not an assumption from the skill's name.

Executor `0x141bb0710..0x141bb09bf` first returns if its flying entity `+0x328` is null.
With an entity, it calculates positions/heights, then:

```text
if first Return:
    byte461 = true
    byte480 = fresh Init8/bounded100 result < signed(stat49f5)
else:
    byte462 = true
if redirect:
    remember supplied contact in new phase when applicable
    redirect current action through slot620
else:
    emit count=1 with preserved history through slot618
```

Both phase writes precede redirect or count-one emission. First Return does not clear `+0x462` here;
normal construction already zeroes it. Later execution preserves `+0x480` without rereading its stat or drawing.
Common construction zeroes `+0x461/+0x462/+0x480`; common copy preserves all three.
The source's phase/cache changes therefore precede ordinary clone inheritance.
The extra-Return stat is `projectile_additional_return_chance_%` (`0x49f5`).
`returning_projectiles_always_pierce` is separately consumed by the Pierce decision.

The [same-object composition](motion-and-contact.md#102-actual-return-switches-the-history-phase-and-resumes-motion)
now executes the qualifier, reason-7 history, actual executor/RNG/redirect, replacement straight motion and a
second frame contact. Contact is remembered before the phase change and again in the new phase before redirect.
Final history release is conditional on the eventual final branch; one stop can therefore be followed by more
samples on the same object. Supplied owner/row/terrain inputs and weak references remain explicit boundaries.

The executor itself does not recheck the qualifier's cannot-return, additional-phase or probability gates.
The mapped Pierce-reason `arrows_that_pierce_also_return` branch calls it directly with `redirect=false`,
without calling `+0x698`. Thus the ordinary qualifier's phase limit is not a demonstrated global limit on all
direct executor calls. Preserve the calling branch's actual guards when porting this rule.

### 7.1 Return origin, destination and planned overshoot

**S:** executor `0x141bb0710` takes the current contact entity's integer grid XY when supplied,
otherwise the projectile's integer grid XY (`0x141bb0756..0x141bb0770`).
It takes the owner's grid XY at Return execution time (`0x141bb07cc..0x141bb0817`).
This establishes an initialization-time owner position, not per-frame homing.
Both positions are converted to cell centers and terrain-sampled before redirect or clone emission.

For finite values at normal map scale, the traced helpers use these operations:

```text
CellCenter(c) = f32(f32(f32(c) + 0.5) * 10.8695650100708)
WorldCell(w) = floor(f32(w * 0.09200000017881393))
Nearest(v) = floor(f32(v + 0.5))
```

`f32` means rounding to single precision at that step; integer truncation below is toward zero.
`Nearest` is not banker's rounding: a negative half rounds toward positive infinity.
The helpers are `0x140df6820`, `0x1402c2390` and `0x140664f10`, respectively.
Host trigonometric functions have not been shown bit-equivalent to the native functions.

The TS and ordinary-bow vtables bind base-range slot `+0x600` to `0x141ba62d0`:

```text
if stat(projectile_maximum_range_override, 0x2ffd) != 0: R = float(stat)
elif stat(reclaimed_malevolence_notable_abyss_searching, 0x5ad7) != 0: R = 50
else: R = 150
```

The first check is nonzero, not positive; no clamp was found in this getter.
For these tables, Returning slot `+0x6a0` reads `byte461`, and absolute-distance override slot `+0x6a8`
is `0x1401d8850`, returning float zero.
Other skill tables may override them.

Redirect `0x141bb2ae0` converts the supplied world positions back to grid points `S,D`.
Its range-extension branch (`0x141bb3072..0x141bb316b`) is:

```text
if !metadata.byte3c or isReturning():
    delta = D-S
    L = sqrt(float(delta.x*delta.x + delta.y*delta.y))
    if L >= 0.01:
        effectiveLength = slot600()
        if isReturning():
            U = slot6a8()
            effectiveLength = U if U > 0 else effectiveLength + L
        scale = effectiveLength/L
        delta = trunc_componentwise(f32(delta*scale))
        heightDelta = trunc(f32(heightDelta*scale))
    endpoint = S+delta
```

For the traced TS/bow Return bindings, this gives
`endpoint = S + trunc_componentwise((D-S)*((L+R)/L))`.
The default planned endpoint extends about 150 grid cells beyond the owner's sampled position.
For example, `S=(100,100), D=(0,100)` gives `endpoint=(-150,100)`.
Component truncation means diagonal extension is not exactly 150 cells.
This resolves the default getter and geometric plan; it does not validate the server's complete Return behavior.
Contact, terrain, skill overrides and earlier termination can shorten actual travel.

Two degenerate-direction paths must remain distinct:

- If executed Pierce/Chain/Fork counts are all zero, the action is Returning and `S` equals the prior stored
  origin `+0x338/+0x33c`, it first substitutes a 60-cell aim along mover heading plus pi,
  using `Nearest(Rotate((0,-60), heading+pi))` (`0x141bb2bdf..0x141bb2c8d`).
- If `S==D` still holds, `0x141bb2faf..0x141bb306e` creates a five-word random context
  `[sequence16, derivedByte8, chainsDone, action+0x2d0, ownerId]` through `0x141a0c9d0`.
  With a quantized angle `phi` in `[0,2*pi]`, it sets
  `D.x=S.x-trunc(sin(phi)*-60)` and `D.y=S.y-trunc(cos(phi)*-60)`.
  The second expression adds cosine; it is not the usual negative-cosine aim convention.
  This provisional point still goes through range extension.

### 7.2 Return heading and height handoff

**S:** when `metadata.byte41` is false and the direction is nonzero, redirect uses
`wrap(atan2(D.y-S.y,D.x-S.x)+pi/2)` as heading (`0x141bb2c91..0x141bb2d13`).
It updates stored origins at `+0x338/+0x340` and repositions the flying entity to the new origin's cell center.

Executor samples `startGroundZ=-TerrainSample(startWorld)` and the equivalent owner-end value.
Its projectile height getter `0x141e35800` returns explicit `Positioned+0x2b4` when `+0x290 != INT_MAX`;
otherwise it takes a metadata/terrain path.
Do not call every result of that getter a ground height.
With `heightOffset=projectileZ-startGroundZ`, the local conversion is:

```text
H0 = trunc(heightOffset / -7.8125)
G0 = trunc(startGroundZ /  7.8125)
G1 = trunc(endGroundZ   /  7.8125)
action+0x334 = H0
startHeight = H0-G0
endHeight = H0-G1
// if range extension above runs:
endHeight = startHeight + trunc((endHeight-startHeight)*scale)
```

TS/bow slot `+0x548` returns true, so redirect passes the end height rather than `INT_MAX`.
It uses speed slot `+0x628` and mover hooks `+0x560/+0x558` (`0x141bb31df..0x141bb324c`).
The TS `+0x558` wrapper `0x141a82c90` applies its private initial-height override only under eligible
non-Chain/Fork/Split/Return conditions; Return skips it and reaches shared `0x141bacf60`.
Final render-coordinate interpretation and all special overrides still need their corresponding mover evidence.

### 7.3 Main and additional Return probabilities restart equal recipes

Both probability sites initialize a fresh eight-word context through `0x1419c29b0`:

```text
[sequence11a:u16, derived11c:u8, owner88:u32,
 outerRemaining2d0:u32, innerRemaining2b8:u8, 0,0,0]
sample = IntegerUniformInclusive(context, 0, 99)
```

Main qualification `0x3216` skips initialization/draw when signed `p==0` (false) or `p>=100` (true).
A negative nonzero `p` still initializes and draws, then fails signed `sample<p`.
First execution's `0x49f5` instead always initializes and draws before signed `sample<q`, even for zero,
negative or at-least-100 values. It first writes returning byte `+0x461`, then reads the seed/stat and overwrites
`+0x480`. These are seeded action contexts, not the TLS chance helper used by additional Chain/Pierce prevention.

If both sites actually read equal seed inputs and main qualification took the probability branch, the two
fresh contexts produce the **same first bounded sample**. They are not independent trials, and the additional
check does not consume the second sample from the main context. For example, recipe
`[37,3,4096,1,2,0,0,0]` gives 40: main `p=50` passes; additional `q=40` fails, while `q=50` passes.
This is a sample-level implication, not a measured distribution over real casts.

The ordinary terminal calls qualifier, reason-7 dispatch, retirement check, then executor.
The intervening callback can prevent execution; complete indirect seed mutations are not exhausted.
Equality of the actual input words is a premise, not an unconditional claim about every call chain.
New clone identities or repeat changes can change later recipes; unconditional Return may skip the main draw entirely.

Round18 `return-findings.zh-CN.md`, `return-witnesses.asm` and `return-audit.json` retain eight aligned spans,
972 instructions and 40 final-table slots. `return-rng-vectors.json` has 16 offline groups using the already
audited eight-word reference; no client execution or new live route evidence is implied.

## 8. Ordinary Fork uses a deterministic fan

**S:** `0x141bae3d0` constructs a descriptor with count 2, whole-fan angle `pi/2`, aim distance 100,
zero fixed heading/height offsets, `reverse=false`, `useFixedHeading=false`, and `preserveHistory=true`.
The aim distance is 100, from constant `0x1436b30a0`; it is not the Return base range of 150.

`forking_angle_+%` (`0x2fd8`) gives `W=max(0,(1+p/100)*pi/2)` in float32 arithmetic.
`chance_to_fork_extra_projectile_%` (`0x2fd7`) changes count to 3 on success:
zero skips the draw, at least 100 succeeds directly, otherwise an integer `[0,99]` is compared with the stat.
Its eight-word context is
`[sequence16, derivedByte8, ownerId, action+0x2d0, action+0x2b8, 0,0,0] → 0x1419c29b0`.

Fan builder `0x141ba6380` obtains the central direction from the action's stored segment origin `+0x340/+0x344`
to the contact's current grid position, or the projectile's current grid position if contact is absent.
For coincident points it uses fallback Positioned fields `+0x214/+0x218/+0x21c` and flags `+0x1f1/+0x1f2`.
Those fallback fields have not been identified sufficiently to call them a mouse aim or target search.

```text
center = wrap(atan2(dy,dx)+pi/2)
if useFixedHeading: center = fixedHeading
if reverse: center = wrap(center+pi)
angle = center-W/2
step = W/(count-1)
for each child:
    Q.x = projectileGrid.x + Nearest(100*sin(angle))
    Q.y = projectileGrid.y + Nearest(-100*cos(angle))
    angle = f32(angle+step)
```

Thus the default two directions are center minus/plus 45 degrees.
With an extra child they are minus 45, zero and plus 45 degrees.
The fan itself has no independent per-child random angle; later shared-emission variance can still modify targets.
Native repeated float addition must not be replaced casually by `angle0+i*step` for bit-matching work.

The builder increments Fork generation before cloning (`0x141ba65d8`).
Each child goes through slot `+0x618` with count 1, preserved history and the contact ID.
The source is the projectile's precise world XY; each target `Q` becomes a cell center and is terrain-sampled.
Descriptor start/end height offsets are subtracted from the corresponding height arguments.

### 8.1 Fork qualification, chance lifetime and child inheritance

The shared terminal's Fork branch first requires `mode3c0 != 1`, a nonnull contact and signed
`budget > forkGeneration45c`. It dispatches reason 4 and checks retirement bit `action.word122 & 0x10`
before entering `0x141bae3d0`. An early exit takes no extra-child probability draw or generation increment.
The bound `+0x640 → 0x141bb6600` is a budget getter, not that entire admission gate:

```text
B = stat(virtual_number_of_forks_for_projectiles_final, 0x2a0f)
if absoluteSlot450 == 2 and slot598() and stat(virtual_projectiles_cannot_fork, 0x223d) == 0:
    B += stat(arrows_from_second_firing_point_fork, 0x3063)
```

It queries the current view and compares against consumed generation; no remaining-Fork constructor cache is used here.
The cannot-fork key suppresses this bonus within the getter, rather than causing an independent unconditional rejection.
Do not invent a missing guard or replace the final virtual stat with a sum of raw modifiers.
Native addition wraps to int32 before the caller's signed comparison; no clamp is present here.

After reason dispatch, each actual extra-child executor rereads `0x2fd7` and constructs its local descriptor.
Signed `p==0` skips initialization/draw and keeps two; signed `p>=100` skips them and selects three.
Other values, including negative values, freshly initialize the listed eight-word context and draw.
Negative `p` cannot pass `sample<p`, but still consumes the initialization and sample.
The result lives only in the descriptor count. It is neither a prepared root cache nor TLS state;
generation, stage, firing slot and target ID are absent from this seed recipe.

Builder `0x141ba6380` spans three adjacent unwind records through `0x141ba67a1`; stopping at the first
record's end `0x141ba65fb` would miss the actual child loop.
It increments the source generation at `0x141ba65d8` **before** any per-child clone/admission attempt.
Same-grid child aims are skipped; later admission can also reject a slot. Neither refunds that generation here.
The extra child changes the fan count, not the number of generations spent.
Concrete clone chains copy the updated generation and current Pierce/Chain/Return counters/caches.
Primitive counters are copied independently; history pointers are first cleared by common copy and assigned by the emitter.

With source history present, each count-one emission preserves shared history or deep-copies it under canShotgun,
then inserts the supplied contact ID into the child's current-phase memory.
Shotgun therefore does not make a Fork child forget its triggering target.
If source history is absent, these separate count-one calls create separate empty histories;
the whole fan is not automatically one newly shared batch in that exceptional case.
Two/three descriptor slots do not guarantee two/three visible admitted objects.

Round18 `fork-findings.zh-CN.md`, `fork-aligned.asm`, `fork-audit.json` and `fork-vectors.json`
retain the full guard/descriptor/three-part builder and all five concrete clone chains:
24 spans, 2576 instructions, 62 instruction assertions, 30 slots and 21 offline vector groups.

## 9. Random/terrain Bounce has its own geometry and lifecycle

**S:** qualifier `0x141baf130` reaches executor `0x141bb09c0` through slot `+0x768`.
Its stat family is:

| Native key | Stat |
|---|---|
| `0x2db4` | `projectiles_bounce_randomly_on_collision_x_times` |
| `0x2db5` | `projectiles_bounce_randomly_terrain_only` |
| `0x2db6` | `projectiles_bounce_max_travel_distance_per_bounce` |
| `0x2db7` | `projectiles_bounce_max_travel_distance_all_bounces` |
| `0x2db8` | `projectiles_bounce_max_variance_deg` |
| `0x2db9` | `projectiles_bounce_max_object_variance_deg` |
| `0x2dba` | `projectiles_bounce_invert_direction` |

### 9.1 Counter, budget and baseline state

Normal construction zeroes `+0x468/+0x46c/+0x470/+0x474`.
When cached max `+0x470` is zero, qualification caches max-bounce stat `0x2db4` there and all-distance stat
`0x2db7` at `+0x46c` (`0x141baf162..0x141baf1b7`).
This caching precedes terrain-only, distance and height rejection, so a nonzero cache does not prove a prior Bounce.
Qualification still reads current `2db4` first: zero disables it, while a later nonzero value does not replace cached max.
The per-Bounce distance/angle inputs are instead read again by the executor.
Height below the sampled terrain marker sets executed count to cached max before rejecting.
These are signed height-marker comparisons, with the Positioned sentinel getter, not a general float world-Z test.
Executor stores `TotalTravel()` into baseline `+0x474` if the executed count `+0x468` is zero, then increments count.
`TotalTravel()` is `action+0x348 + length(projectileGrid-action+0x340)` (`0x141bae370`).
The copy constructor inherits bounce count/budgets but zeroes baseline `+0x474`;
it gives child `+0x348` the source's `TotalTravel()`.

### 9.2 Angle and endpoint

If contact is nonnull and object-variance stat `0x2db9` is nonzero, use that stat; otherwise use `0x2db8`.
Let `V` be the selected variance, `h` the mover heading, `T` the natural-end boolean and `I` invert-stat nonzero:

```text
if V == 0:
    referenceAngle = 0
    low = -pi; high = pi
else:
    referenceAngle = h if (T XOR I) else -h
    low = -V*pi/180; high = V*pi/180
phi = referenceAngle + QuantizedFloat(low, high)
```

Zero variance means full-circle random here, not no deflection.
The nonzero branch selects signed heading; replacing that with an assumed heading-plus-pi reflection is incorrect.
The bounded executor reads no wall normal for specular reflection.
Its four-word context is
`[sequence16, derivedByte8, incrementedBounceCount, ownerId] → 0x14146d8f0`.
That initializer is reconstructed in [the random-input reference](random-inputs.md);
it must not be replaced with a padded TS seed.

The shared float distribution helpers `0x1404cd340 → 0x1404ce3f0` use:

```text
N = 1000 * trunc(f32(f32(high-low)+1))
J = integerUniformInclusive(0,N)
value = f32(low + f32(f32(J/N)*f32(high-low)))
```

For `[-pi,pi]`, `N=7000`: the draw has 7001 fractions before float rounding, not continuous sampling.
The endpoint distance at `0x141bb0bcb..0x141bb0c2e` has this literal boundary:

```text
consumed = 0
if baseline <= 0: consumed = TotalTravel()-baseline
distance = min(float(perBounceDistance), float(allDistance)-consumed)
Q.x = projectileGrid.x + trunc(sin(phi)*distance)
Q.y = projectileGrid.y - trunc(cos(phi)*distance)
```

The positive-baseline branch leaves `consumed=0` in qualifier, executor and the cached-budget update check.
The called length helper preserves the registers on which this branch depends; this is not inferred caller-clobber state.
An ordinary first same-action Bounce at positive travel retains that positive baseline on later redirects.
A clone clears the baseline but inherits executed count/cache/cumulative travel;
if count is already positive, a later Bounce does not establish a new baseline.
Do not rewrite these branches as an intuitive distance-since-first-bounce calculation.
Unlike Fork, these offsets truncate instead of using `Nearest`.
The source is precise current world XY and terrain-sampled there; the target is `CellCenter(Q)` and terrain-sampled.

Contact nonnull with nonzero ID redirects the same action through slot `+0x620`, preserving its baseline.
No contact or ID zero emits a clone through `+0x618` with `preserveHistory=false` and contact ID zero.
That clone inherits count/budgets/travel but resets baseline as described above.
Entity and terrain Bounce therefore differ in identity and history lifecycle, not only in direction.

### 9.3 Conditional executor-count boundary is distinct from visible Bounce count

Qualifier `0x141baf1b7..0x141baf1c3` rejects `executedCount > cachedMax`; equality can enter.
Executor increments count first.
Frame update `0x141ba91b4..0x141ba91ff`, when cached max is nonzero, calls `Terminal(null,naturalEnd=true)`
if consumed distance reaches the total budget or `executedCount >= cachedMax`.
Qualification also checks terrain-only, height and distance restrictions.
Terminal may run Split, a skill branch or Chain before reaching Bounce.
That proactive terminal is not followed by an unconditional return: the checked shared update can continue to catch-up.
Bounce terminal dispatches reason 6 and checks retirement before calling the executor.
A successful no-contact Bounce emits a clone and retires its source; a normal contact with nonzero ID redirects instead.
The edge case of a nonnull contact with ID zero chooses clone emission while source-retirement checks only pointer nullness;
its reachability/outer handling is not closed.

For positive max `N`, finite/no-overflow arithmetic, a single inheritance path and all other eligibility/retirement
gates permitting, these comparisons allow executor counts `1..N+1` from initial zero.
At count `N`, a later update can proactively enter targetless terminal and the qualifier still admits equality,
so the executor can increment once more; subsequent qualification rejects `count>N`.
This is a **conditional executor-call bound**, not evidence for `N+1` distinct visible flight segments.
The extra clone/direction change may occur at the same location before appreciable flight.
Admission, frame ordering, callback retirement or earlier continuations can shorten/change the outcome.
Do not call it an official off-by-one defect or a measured visible count.

The five final classes for all six witnesses share Bounce `768`, emitter `618` and redirect `620` bindings.
BA/KB update wrappers first invoke the shared update and do not reset the four Bounce fields in their checked bodies.
Chain's same-instance redirect preserves them; Split/Fork clones inherit count/cache/cumulative travel but clear baseline.
These descendant counters advance independently, rather than sharing a family-wide Bounce budget.
Local evidence: `round6/bounce-findings.zh-CN.md`, `bounce-core.asm`, `bounce-helper-detail.asm`,
`bounce-bindings.asm` and `bounce-update-wrappers.asm` in ignored projectile research logs.

## 10. Shared Split targets first, then fills missing directions

The six checked witnesses share count getter `+0x5f8 → 0x141ba6210`.
It selects `virtual_projectile_number_to_split` (`0x29c9`), or arrow alias `0x29ca` via `+0x598`.
When `modifiers_to_number_of_projectiles_instead_apply_to_splitting` (`0x29c7`) applies,
it adds action `+0x2c0`, plus `+0x23c` when `byte231` is set, then clamps the signed sum to at least zero.
Those extra-count snapshots are correspondingly suppressed in the ordinary initial-emission count getter.
Contact and terminal also add the **target's** `projectiles_hitting_self_split_into_x` (`0x3351`).
This target contribution is not an unconditional property of the firing skill.

After earlier contact admission, `0x141ba877d..0x141ba8840` skips Split for action mode `+0x3c0 == 1`,
cannot-Split (`0x47c9`), explode-only (`0x47cf`), object-contact-explode-only (`0x47d0`) or prior Split `byte460`.
Its count-plus-target contribution test is nonzero, not a positive clamp of the combined value.
Negative target-contribution reachability and integer overflow are not established here.
The emitter's loop uses an unsigned requested-count comparison after a zero-only entry check;
a reachable negative combined value would not safely behave as zero.
An eligible contact is retained with pending-Split state before the Pierce decision.
The terminal path dispatches reason 1, emits only if the action was not retired by that callback, then retires the source.
It does not proceed to final reason-zero skill `+0x590` in that Split branch.
Targetless terrain/natural-end guards are separate, as described in section 2.

Emitter `0x141bae540` uses the contact's grid/world XY when supplied, otherwise the current projectile's.
Its spatial search uses base radius 60; `0x141bbb5c0` includes candidate body radius from signed Positioned `+0x1e3`
and accepts the inclusive squared-distance boundary.
Broad bucket bounds include an additional global padding term whose source is not named here.
The helper traverses forward bucket/rank records and applies outer component/state admission.
Its `0x141bb9c70` filter checks target validity, trap/mine stats, team/state and ignored-target-selection inputs,
then a terrain/path helper and exclusion of the exact triggering contact object.
The checked target with nonzero `is_water_sphere_owner_id` is also rejected as a candidate.
The full path helper's layer/obstacle semantics and cross-query mutation ordering remain open.

Each admitted 16-byte candidate stores:
`{seenInCurrentHistoryPhase:bool, padding, centerDistanceSquared:u32, target:pointer}`.
Seen targets are retained for ranking rather than unconditionally rejected.
Sorter `0x141bbc1f0` orders by unsigned `(seen, squaredDistance)`, so unseen candidates precede seen ones,
then nearer candidates precede farther ones within each group.
The distance is relative to the Split query center, not Chain's stored-action reference.
Target ID is not a tie-breaker.
For at most 32 records, the strict-less insertion path preserves encounter order for equal keys.
Larger lists use the [exact partition/heap contract](split-target-order.md), now reconstructed with
record-permutation and intermediate-partition comparisons. Initial sort budget is candidate count;
it decreases as two separate integer shifts. Equal heap children select the right child.
A 33-record normal-budget fixture changes equal-key order, while an all-equal array can retain it;
neither a universally stable sort nor a target-ID tie-breaker matches the shared implementation.

Before emission, the source's already-Split byte is set and a fresh eight-word context is initialized:

```text
[sequence16, derivedByte8, ownerId, outerRemaining2d0, innerRemaining2b8, 0, 0, 0]
```

For each requested slot it uses the corresponding sorted candidate's grid, if available.
Missing slots start at the query center; any selected point equal to that center also takes this fallback:

```text
phi = QuantizedFloat(0, 6.2831854820251465f)  # 7001 fractions before float rounding
Q.x = center.x - trunc(sin(phi) * -60.0f)
Q.y = center.y + trunc(cos(phi) *  60.0f)
```

Only these fallback/coincident slots consume that angle draw.
They are independent random directions, not an evenly spaced fan or a unique-direction resampling scheme.
Each slot calls shared emitter `+0x618` with count 1 and preserved history;
the explicit contact ID is the original triggering contact, not the newly selected aim target.
Normal skill/history, height, launch-variance and birth-admission rules still apply afterward.
Thus a shortage of nearby targets does not itself reduce the shared emitter's requested number of children.
Actual downstream admission can still reject a child.
This local prediction policy is separate from authoritative damage quotas.

Evidence: ignored `round5/split-findings.zh-CN.md` and its aligned `split-*` listings.

## 11. Remaining reconstruction obligations

The later [generated TS child contact composition](tornado-shot.md#25-generated-children-through-nonempty-contacts-shared-history-and-return)
executes default/independent sibling histories against a common target, including local rejection that still
records the target. A finite-Pierce/Return combination then admits an outbound victim again in the return phase
while remembering the turnaround victim there; a supplied extra-Return grant uses the third partition without
redrawing its cached chance. Native pending-handle assignment/unsubscription and history destruction execute.
The original membership/phase rules are unchanged; effective grants, local hit prediction and storage remain
explicit inputs, not official TS secondary policy or authoritative damage.

The [retired-source cleanup join](tornado-shot.md#27-retired-forksplit-sources-through-actor-cleanup-and-history-ownership)
then executes actual Actor removal and the TS destructor for Fork/Split sources. Native reservation removal
and strong-count release are separate operations. Shared histories survive for live descendants; independent
source history can be destroyed first. Ordinary-final descendants have cleared handles before later Actor
cleanup, avoiding a second release. Flight removal is requested through the real deferred queue; that join
does not drain generated flights or establish populated component teardown or official frame order.

The [native stat-input supplement](continuation-stat-inputs.md) recovers twelve upstream outputs and
the five final-table bindings, including projectile-count conversion to Split/Chain and cannot/always-Pierce.
These formula callbacks do not imply automatic raw-network recalculation or replace the consumers above.

Remaining upstream input producers/activation and budgets, every contact guard, special objects, Split query encounter/mutation order, Fork fallback fields and
later emission variance, Bounce visible segment count/cross-action timing, full path tests, Return in-flight target updates
or owner-contact termination, action retirement timing, mode-specific calls and all skill overrides remain incomplete.
Normal finite coordinate arithmetic above does not establish exceptional/overflow behavior.
Contact history, result queues and server damage quotas must stay separate until explicit evidence connects them.
Raw local evidence and the corrected prior labels are listed in [the ledger](evidence-and-open-questions.md).
