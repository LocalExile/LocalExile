# Shared projectile random inputs (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [012 — projectile execution](../012-projectile-execution.md).
**S:** the pinned client uses several independently initialized contexts. A common transition implementation
does not mean the callers share one stream. Field names below describe reads, not recovered source symbols.
See [the ledger](evidence-and-open-questions.md) for binary identity, retained reads and validation limits.

## 1. Context recipes

Words are listed in native input order; arithmetic seed words wrap to uint32.
Sequence and action byte are zero-extended from their actual u16/u8 widths.

| Purpose | Initializer / words | Lifetime and qualification |
|---|---|---|
| Multi-emission angle/distance variance | `0x141a0c9d0`, 5: `[sequence11a, emittingByte11c, stage120, repeats2d0, releaseOrdinal3f8]` | once for the whole multi batch; **no owner ID** in this recipe |
| Multi random-direction layout | `0x141bba070`, 7: `[sequence11a, emittingByte11c, repeats2d0, field458, owner88, slot540Value, signedSlot]` | reinitialized for each layout group |
| Single random-direction branch | `0x141a0c9d0`, 5: `[rootSequence11a, cloneByte11c, cloneField458, rootRepeats2d0, owner88]` | caller flag plus stat `0x3741`; not the multi recipe |
| Object seed | `0x14193a720`, pure hash below | after admission and derived-byte allocation; no RNG draw |
| Motion speed factor | `0x1419c29b0`, 8: `[sequence11a, thisArrowByte11c, owner88, repeats2d0, innerRepeats2b8, 0, 0, 0]` | finalizer, when action `+0x3a4` is float zero |
| Ordinary Fork extra-child roll | same eight-word implementation and recipe as the motion row | fresh per reached executor, unless signed chance is zero or at least 100; no root cache |
| Main Return probability | same eight-word implementation and recipe as the motion row | fresh per reached probability check; zero/at-least-100 shortcuts; not TLS |
| Additional Return outcome | same eight-word implementation and recipe as the motion row | first executor with a flying entity always initializes/draws and overwrites `byte480`; later executions preserve it |
| Bounce | `0x14146d8f0`, 4: `[sequence11a, derivedByte11c, incrementedBounceCount, owner88]` | fresh context per Bounce; strict four-word initializer reproduced offline |
| Split missing/coincident aim | `0x1419c29b0`, 8: `[sequence11a,derived11c,owner88,repeats2d0,innerRepeats2b8,0,0,0]` | fresh context per Split; only missing/coincident slots draw angles |
| Chain source/terrain prevention | same eight-word recipe | fresh qualification context; source and terrain percentage boundaries consume differently |
| Chain candidate prevention | `0x141a0c9d0`, 5: `[sequence11a,derived11c,chainsDone458,repeats2d0,owner88]` | one context per target search; eligible nonzero-chance candidates consume in traversal order |
| Additional-chain budget roll | TLS helper `0x140e3b450 → 0x140e3b310(7)` | preparation caches the outcome in `byte478`; not seeded by the action context |
| Pierce prevention | same TLS chance helper | uncached at each reached qualification; runs before forced/return-always success rules |
| Attack contact entropy | `0x141ebd1a0`: one signed Park-Miller step from `u32(selectedTargetId*436 + sequence)` when reset is set | selected target Stats owns the accumulator, three-second timer and creation-data initialization; not per-arrow independent draws |
| Spell contact avoidance | `0x141ebd2f0`: identity-derived one/two signed Park-Miller samples | sequence/derived/stage recipes; no target entropy update; ordinary Fireball passes false for TLS mode |
| Main animation selection | scalar `0x140e0bec0`: `u32(sequence11a + outerRemaining2d0)`; zero falls back to Actor `+0x21c:u16` | selector-local stack state, independent of projectile layout/terminal/TLS streams |

The [TS terminal case study](tornado-geometry.md)
records another eight-word context. Do not replace these recipes with that context, append missing words,
or substitute a per-projectile `System.Random`.
`field458` is the executed-Chain counter in the traced continuation state.
The final TS, ordinary Fireball, KB, ranged Default Attack and BA tables bind `+0x540` to
`0x1400ecd70` (constant zero). For these witnesses the layout's sixth word is therefore zero.
Other action classes must be checked separately.

### 1.1 Remaining-repeat values, release ordinal and copied state

Shared repeat ctor `0x1419e2a50` initializes `+0x2d0/+0x2d4` as remaining/total outer repeats,
and `+0x2b8/+0x2b9` as remaining/total inner repeat bytes, initially zero.
Spell base `0x1419ee9a0` supplies outer count from `spell_repeat_count` (`0x912`);
bow base `0x141b6eb70` uses `attack_repeat_count` (`0x4eb`).
Skill-type query `0x30` can force the outer counts to zero; the number is not assigned an inferred type name here.

At outer completion `0x1419e3960`, decrement the remaining outer value.
If its old value was nonzero and the cancellation gate permits, advance stage and restart the concrete action;
otherwise clamp the remaining value to zero and finish.
On the ordinary repeated path its emission values are `R,R-1,...,0`, not `0,1,...,R`.

Animation start `0x141ba2e60 → 0x1419e3b10 → 0x1419e3bd0` establishes inner counts.
After an outer repeat has begun (`outerRemaining < outerTotal`), the checked path clears inner total.
On the initial outer action, rapid-fire count `0x2342` also needs matching buff/skill/sequence gates;
Unleash uses the actual owner-buff callback when max-seals stat `0x2905` is nonzero.
The maximum seal stat alone is not the actual inner count.
The selected inner total is copied into the remaining byte.
The [bound Buff callbacks](repeat-source-state.md) supply the missing local transition:
Anticipation consumes a positive signed16 count and caches it for same-sequence reentry;
RapidFireCharge uses a separate sequence-qualified three-charge state machine.
Neither method draws RNG. Their mutable count/last-sequence/cache fields can be overwritten by `0x1c1`;
actual server values and update order remain inputs, not consequences of a maximum-count stat.

For release event hash `0x9b8d`, `0x141ba4620` calls virtual `+0x4b8` **before** decrementing
nonzero inner remaining and scheduling another event through `+0x470`.
Here `+0x470` relocates the existing animation timeline; it does not create a new action queue.
The complete Fireball event handler has the same release-before-decrement order.
Seed inputs therefore observe `K,K-1,...,0` on an ordinary inner-repeat path.
Fireball's start override briefly clears the byte, but then enters the same animation/repeat setup;
that intermediate clear does not prove Fireball disables inner repeats.
Concrete eligibility and all runtime buff callback values still need their own evidence.

Shared release `0x141ba4930` increments `releaseOrdinal3f8` before dispatch.
The shared constructor zeroes it, so the first normal root release reads **1**.
It increments once per release callback, not once per sibling arrow.
The clone copies it; a skill-created stage that calls the emitter directly does not implicitly increment it.

Source ordinal `+0x448` is different: construction writes sentinel `0xffffffff`, then common start
conditionally resets it to zero when byte `1e8` is zero. The first admitted projectile hashes/copies zero;
binding then increments the source for subsequent siblings. Rejected side origins do not spend it.
Inner repeats rewind the existing animation without common start, retaining the accumulating source ordinal;
outer completion restarts the action and conditionally resets it, without resetting release ordinal there.
The copy preserves `448` but resets `3fc/400` to `1/0`; these distribution fields are not inner-repeat counters.
See [the complete selected lifecycle and final bindings](release-and-repeat.md) for byte-level anchors,
buff callback premises, the temporary synthetic-contact startup context and remaining writers.
That context supplies an existing action and skips the normal source reset;
[typed 0x1ce/0x1cf callers](synthetic-start-and-server-commands.md) are mapped, while other callers and
the exact synthetic pre-emission source ordinal remain open.
The common-start MTX profile call is not a gameplay-emission bypass of the release preincrement.

The copy chain `0x141ba2410 → 0x1419e2d40` retains both repeat totals/remaining values,
Chain count, release ordinal and speed cache.
Ordinary selected-target Chain increments `+0x458` at `0x141baf585` before redirect;
a separate special terminal Chain branch increments it at `0x141ba6ca9` before virtual `+0x528`.
A failed candidate search is not a reason to increment it.
Volley mutates a **local seed word** by 529; that operation does not increment the action's Chain counter.

## 2. Object seed is a hash, not a stream draw

`0x14193a720` implements the following uint32 calculation:

```text
M = 0x5bd1e995
mix(x):
    x = u32(x*M)
    return u32((x ^ (x>>24))*M)
h = mix(sequence_u16) ^ 0x22c1780b
for x in [owner_u32, emittingByte_u8, newlyAllocatedDerivedByte, sourceOrdinal_u32]:
    h = u32(h*M) ^ mix(x)
h = u32(h*0x6dc79cad)
h ^= h>>13
h = u32(h*M)
return h ^ (h>>15)
```

The emitting action byte is not the new clone byte. The ordinal is read before its increment.
Resource flag `+0x2a4` can force object seed zero **after** derived-byte allocation; it only skips the hash.
The single dispatcher inlines the same calculation. Subsequent component uses of this seed are not fully traced;
it is not established as a universal combat/damage seed.

## 3. Four-, five- and seven-word initialization and sampling

These initializers use four core words `[0, MAT1, MAT2, TMAT]`, with constants
`MAT1=0x8f7011ee`, `MAT2=0xfc78ff1f`, `TMAT=0x3793fdff`.
All operations below wrap to uint32, including compound assignments.

```text
f1(x) = u32((x^(x>>27))*1664525)
f2(x) = u32((x^(x>>27))*1566083941)
r=f1(s0 ^ s1 ^ s3)
s1+=r; r+=wordCount; s2+=r; s0=r
i=1
for j in 0 .. max(7,wordCount)-1:
    r=f1(si ^ s[(i+1)%4] ^ s[(i+3)%4])
    s[(i+1)%4]+=r
    r+=i+(j<wordCount ? words[j] : 0)
    s[(i+2)%4]+=r; si=r; i=(i+1)%4
repeat 4 times:
    r=f2(si+s[(i+1)%4]+s[(i+3)%4])
    s[(i+1)%4]^=r
    r-=i; s[(i+2)%4]^=r; si=r; i=(i+1)%4
perform 8 warmup transitions
```

Four/five words have three/two no-input rounds; seven words have seven input rounds.
Zero-padding any of these to eight words changes initialization. The native state has a transition counter at `+0`,
with core words at `+4/+8/+c/+10`.
The shipped transition is reproduced in
[the offline reference](../../../../scripts/re/projectiles/native_projectile_rng.py), reusing the checked
transition/bounded sampler from [the terminal reference](../../../../scripts/re/projectiles/native_tornado_rng.py).
It resembles TinyMT32 but differs from its standard recurrence; a standard library is not a substitute.

Float distribution constructor `0x1404cd340` and sampler `0x1404ce3f0` calculate, for normal supported ranges:

```text
span = f32(b-a)
integerMax = u32(trunc(f32(span+1))*1000)  // truncate BEFORE multiplying
q = integerUniformInclusive(0, integerMax)
value = f32(a + f32(f32(f32(q)/f32(integerMax))*span))
```

The bounded integer helper rejects incomplete upper buckets; rejected raw draws advance state.
`[-1,+1]` has 3001 possible integer samples; ordinary `[0,6.2831854820251465f]` has 7001.
This ordinary tau differs from TS terminal's `6.283180236816406f`.
Even `a==b` consumes a sample here: the integer range still has 1001 elements.
Zero-variance skips described next occur in the caller, not in this distribution.
The offline tool also reconstructs this distribution for normal finite supported ranges.
It rejects reversed/nonfinite/overflowing constructor inputs instead of claiming exceptional native parity.

Chain candidate prevention uses one five-word context for the whole search, not one fresh context per candidate.
The [ordered admission callback](chain-candidate-admission.md) skips draws for earlier geometric/byte rejections
and for zero chance; every other signed chance draws before the processed/reserved/priority record is built.
Negative and at-least-100 values still advance this search stream.
This is separate from source-contact qualification's fresh eight-word context and additional-chain TLS chance.

### 3.1 Related animation and target-movement contexts

The [external ranged supplement](external-ranged-evidence.md) supplies two additional independently checked callers.
Scalar initializer `0x140e0bec0` seeds animation choice; `0x141e991e0` uses a nonzero request seed at `+8`,
otherwise the Actor's u16 at `+0x21c`, before its conditioned/weighted animation selection.
This does not prove that every monster clip uses the root input sequence directly.

Actor setup `0x141e9cc65..0x141e9ccd9` uses specialized two-word initializer `0x1413cbe50` with
`[entity88, loadedTypeData8]` for a movement-speed percentage, then inclusive range helper `0x1404d03d0`.
The loaded type's base constructor `0x142474910` writes `+8` from path-hash helper `0x1424748a0`,
also used by type registration.
The complete resource/alias/levelled-path identity and actual runtime roll still need comparison.

Both use the same right-shift recurrence and bounded-sampling family as projectile contexts;
the decisive instruction is `shr r11d,1` at `0x140485008`, not the public TinyMT left-shift recurrence.
They have independent local states and different initialization inputs; TLS `+0x170` remains another family.
The scalar and two-word initialization plus three sequential bounded outputs/state were checked in 207
retained-instruction interpreter cases, not in a running client.
Only the two-word specialization was checked here, not every key length accepted by the PR's generic constructor.

Equal speed bounds skip sampling but retain that fixed percentage, rather than forcing zero.
This matters for exact target-motion inputs even when no random word is consumed.
The port's contrary equal-bound shortcut and the three affected data rows are recorded in the supplement.

## 4. Batch variance and admission consume different streams

Profile `+0x10` is angle half-width:
`f32(f32(f32(slot6c0())*0.01745329238474369f)*0.5f)`;
the shared getter reads `projectile_angle_variance` (`0x1b26`).
Profile `+0x14` is float-converted `slot6e0()` / `projectile_distance_variance` (`0x222f`).
For each admitted, constructed and bound arrow, `0x141bb2480`:

1. If angle half-width is nonzero, takes one batch `U[-1,+1]` sample and rotates the ray by `U*halfWidth`.
2. If distance variance is nonzero, takes the **next** sample from that same stream.
   For ordinary finite, nonnegative variance `v` and ray length `L`:

```text
base=max(1, L-v/2)
Lnew=base+(U+1)*v/2
scale=Lnew/L
aim=origin+(aim-origin)*scale
targetHeight=originHeight+(targetHeight-originHeight)*scale
```

These are float32 operations, not a claim of exact host trig parity.
Positive/negative float zero skip the corresponding draw; NaN follows a different branch and is not a supported parity case.
The later finalizer may alter range again, so `Lnew` is not necessarily the final lifetime/range.

Random-direction layout samples its angle **before** per-arrow admission.
A rejected side origin or an already-set distance stop consumes that layout sample but no derived byte,
object seed or batch-variance samples. The first candidate that sets the distance stop still launches;
only later candidates are suppressed. Fan/radial layout itself has no random-layout draw.
All Volley groups retain the same batch variance state through pointers; they do not restart it per arrow/group.

The [layout-to-placement join](emission-admission.md#6-joining-real-layouts-to-placement-and-the-movement-callback)
extends that admission/variance rule to actual distinct fan/Volley rays, downstream range/height changes and
the movement-call inputs. Rejected side candidates compact admitted draws; a shared stop can suppress later
subgroups. Its explicit source speed cache bypasses fresh speed-variation generation, and supplied presentation
callbacks do not establish a concrete skill's full draw schedule.

The [admission/identity/variance composition](emission-admission.md#4-a-constructed-rejection-witness)
supplies ordered candidate rays and runs the original emitter through its pre-finalizer output.
Rejecting candidate zero moves its otherwise-first variance pair and admitted identity to the next accepted
candidate while retaining that candidate's layout slot. Object seeds are recomputed pure hashes, not RNG draws.
These controls reuse the sealed bounded sampler and finite native sin/cos bridge; upstream layout traversal,
actual terrain and final placement are separate boundaries.

## 5. Correction: profile vector contains seeds, not explicit directions

Profile `+0x18` contains six uint32 words when the caller flag and `random_projectile_direction` (`0x3741`) apply.
It is **not an array of precomputed directions**. `0x141bb9280` appends signed source slot as word seven,
initializes the layout state, and draws a separate angle for each slot. Each angle adds the caller rotation base
and rotates the input aim ray. This is not a common random rotation of equally spaced rays.
The separate radial helper `0x141bb9500` must not be conflated with this branch.

The [actual profile producer](layout-stat-inputs.md#3-profile-construction-and-local-lifetime) supplies
`[sequence11a,derived11c,outer2d0,chain458,owner88,virtual540()]` in that order,
clearing its vector before conditional construction. Its selected-stat read order and the dispatch priority
Volley, nonempty seed vector, radial, then fan are mapped separately from RNG arithmetic and TLS history.

For Volley, center uses signed slot 0 and the original six words.
Every side-group call to `0x141bb8f00` adds `0x211` (529, uint32 wrap) to word index 3 before dispatch.
The two side groups for each increasing `k` pass signed `+k/-k`; their word-3 changes are cumulative:
529, 1058, 1587, etc. A zero-count side still mutates that word, and layout initialization precedes its zero-count check.
The direction sample count then equals that group's slot count, including later rejected slots.

## 6. Motion finalization has independent random consumers

At `0x141ba99d0`, speed cache action `+0x3a4` is reused when nonzero (also on the checked NaN branch).
The shared constructor zeroes this field and the clone copies its bits.
Children cloned from a zero-cache root can initialize their own identity-based speed context;
children cloned from an already nonzero-cache flying action inherit that factor.
Do not redraw merely because a redirect or new derived action has different counters.
If a computed factor is exactly zero, the next finalizer still takes the zero-cache branch;
an unconditional separate initialized boolean would change that behavior.
When zero, the eight-word motion context in section 1 is initialized and samples are taken in this order:

```text
offV = stat493e / 200
mainV = slot6c8(stat1c33) / 200
offSample  = distribution[-offV,+offV](rng)   // first
mainSample = distribution[-mainV,+mainV](rng) // second
cache = (1+offSample)*(1+mainSample)
```

Both distributions sample even at zero variance. This differs from the batch variance caller's skip rule.
`0x493e` is off-hand speed variation; `0x1c33` is the shared main speed-variation stat.
The divisor is the actual float32 `200.0` at `0x1436a3cf4`.
An earlier revision inferred 100 without reading that constant; this corrects that error.

### 6.1 Coincident-cell fallback and distance-dependent random angle

After earlier finalizer modifications, source and aim each become
`floor(f32(worldXY*0.09200000017881393f))`.
If both grid coordinates coincide, `0x141ba9b8d..0x141ba9c53` initializes five words
`[seq,thisByte,field458,repeats2d0,owner]` and samples angle in `[0,6.283180236816406f]`.
The temporary aim is `origin+652.1738891601562f*(sin(angle),cos(angle))`.
This test does not require an exactly zero world-space vector.
Later projection/range/height processing still applies; 652.17 is not a proved final projectile range.

Distance-angle stat `0x21ae` has a separate fresh initialization of the same five words.
It applies if the stat `s != 0` and current world XY ray length `L >= 0.10000000149011612f`:

```text
fullWidthDegrees = max(5, ((100-L*0.09200000017881393f)*float(s))/100)
n = bounded(3)+1
total = 0
repeat n times: total += distribution[-fullWidthDegrees/2,+fullWidthDegrees/2](rng)
angleRadians = (total/float(n))*0.01745329238474369f
temporaryAim = origin + Rotate((aim-origin)/L,angleRadians)*L
```

Every operation is float32 at its instruction boundary.
The first draw selects the sample count; later draws choose angles, with rejection draws retained.
The minimum is five degrees of full width, not five degrees of half-width.
This is a mixture of one-/two-/three-sample means, not a single uniform angle or a proved Gaussian.
If both fallback and distance-angle apply, their identical recipes restart independently.
Their first raw outputs therefore match; the streams must not be concatenated.
The tool reconstructs scalar samples, not native trig or the full finalizer.

### 6.2 Local cosmetic orientation uses thread state

If metadata `+0x41` is nonzero, `0x141baa1f4..0x141baa257` sets Positioned orientation from
`0x140e3b310(10)`. The helper reads `gs:[0x58] → [0] → +0x170`, a thread-local uint32 state:

```text
tls = u32(tls*0x41c64e6d+0x3039)
index = (tls>>16)&0x3ff
orientation = f32(f32(float(index)*3.141590118408203f)*0.001953125f)
```

The general helper consumes up to 15 bits per LCG step and concatenates chunks for wider requests.
The checked caller requests ten bits and advances once.
This is not an action/object-seed stream; complete seeding/thread mapping and other consumers remain open.
It establishes local orientation, not that authoritative trajectory or damage depends on TLS.
The concrete [TS wind](tornado-shot.md) also uses that LCG, with a different 15-bit angle conversion.
Downstream uses of every object seed have not all been traced.

#### 6.2.1 TLS template value and one runtime seed writer

The PE TLS raw template spans `0x14373e670..0x14373f0a8` (2616 bytes), with zero-fill size 0.
Its uint32 at offset `+0x170` is 1; this is a disk-template value, not a sampled running-thread state.
Setup helper `0x140e2f210` contains a separate runtime overwrite:
`0x140e2f2fe` calls IAT `0x142f43468`, identified by the PE import table as `KERNEL32.GetTickCount`,
then `0x140e2f315` stores the unchanged DWORD result into `gs:[0x58] → [0] → +0x170`.
The preceding IAT `0x142f43388` is `SetThreadStackGuarantee`.
Caller `0x140e2f350` invokes this setup before dispatching a context object's virtual callback;
another direct call occurs at `0x140e2f440`.

These mapped paths establish a runtime system-time seed source for that TLS state.
They do not establish every executing thread/callback, every reseeder, or that all skill paths traverse this setup.
Subsequent consumers also advance the state, so neither a fixed initial 1 nor the action-seed recipes determine
the thread state at an arbitrary projectile action.
This does not imply the official server reads or reproduces the client's system-time seed.
Evidence: `round7/tls-template.json`, `tls-thread-setup.asm`, `tls-thread-entry.asm`,
`tls-setup-second-caller.asm` and `tls-independent-review.asm` in ignored research logs.

#### 6.2.2 Thread setup, other seed writers and temporary scopes

Thread-creation helper `0x140e2f3c0` can call setup `0x140e2f210` on the current thread when its two checked
context fields are zero, or create a new thread through `KERNEL32.CreateThread` at `0x140e2f4aa`.
The new-thread wrapper stores `0x140e2f350` at `+0x10` and its context at `+0x18`:

```text
CreateThread → 0x140e338e0 → 0x1402167b0 → 0x1402167e0
             → wrapper.function(wrapper.context)
             → 0x140e2f350 → setup/GetTickCount seed
             → CoInitializeEx → context(+0x88).slot10() → CoUninitialize
```

This closes the normal valid-callback/COM-success path of one setup mechanism,
not every error branch or the identity of the OS thread running projectile actions.
World queue consumer `0x141655a10` has an update caller through `0x1401027d0 → 0x14164e250`,
as well as cleanup/destructor callers.
Leaf `0x141677490` jumps to the same consumer and appears in a callback table.
These entry points do not by themselves establish a fixed worker thread or its complete random history.

Additional genuine writes to TLS `+0x170` exist:

| Checked path | Seed source and bounded scope |
|---|---|
| `0x140f193b3..0x140f193ca` | another `GetTickCount` write under a global guard; a later 1% roll controls setting that guard, so a failed attempt can reseed on a later call |
| `0x1417c4770..0x1417c47d0` | Corpsebloom effect skeleton/footprint sampling: save, seed from associated entity ID plus entry count, bounded sample, restore |
| `0x142046a70` | terrain/topology selection: obtain a value from separate TLS `+0x190` context via `0x141ff9720`, temporarily replace `+0x170` for bounded sampling, restore |
| `0x14204e132..0x14204e1c0` | terrain generation: hash the record collection, replace zero with one, store at associated `+0x114`, write to TLS before terrain callbacks |

The last mapped function has no old-state restoration in its inspected body; deeper callbacks are not exhausted.
Their identified domains do not establish a universal seed scope around action creation or wind/Chain calls.
A candidate at `0x14160f43b/0x14160f43f` reads and writes back the same TLS value; it is not another reseeder.
The candidate index is a bounded GS/nearby-offset scan, not exhaustive writer coverage.

Thus `GetTickCount` initialization does not prove that every later TLS decision is unsynchronizable:
temporary deterministic scopes exist elsewhere.
Conversely, their existence does not show that they enclose additional-chain chance or TS wind.
Reproduction needs the actual decision-point state or its enclosing seed scope plus preceding draws;
no complete bridge from server/action inputs to those TLS consumers is established yet.
See round9 `rng-tls-synchronization.zh-CN.md`, `rng-extra-reseeders.asm`, `rng-world-consumer-callers.asm`,
`rng-thread-start-thunk.asm`, `rng-thread-start-dispatch.asm` and `rng-thread-start-invoke.asm`.
The concrete effect/terrain identities and scope endpoints are in `rng-scope-envelope-followup.zh-CN.md`.

The [World callback follow-up](world-scheduling.md#8-dispatcher-record-invocation-and-remaining-framethread-boundary)
connects registration through `S=N+0x18` to concrete record invokers and direct/batch/per-item task wrappers.
Submission can execute immediately, enqueue, or be serviced by a waiting thread; it does not establish a fixed thread.
The [frame construction follow-up](world-scheduling.md#9-concrete-frame-graph-and-listener-construction)
closes the root dependencies and listener binding: `START/MsgProc` lead to the `Simulation` node,
and the initialized frame listener comes from startup table `0x142f9cce0`.
Both concrete Simulation nodes permit current-thread/batch execution without the per-record parallel branch.
No enclosing seed/save/restore for additional-chain chance or TS wind is established by the inspected invokers;
their TLS `+0x168` task context is separate from RNG state `+0x170`.

#### 6.2.3 Concrete action-sequence scopes exist, but are not universal

Two additional paths provide positive action-input-to-TLS evidence:

| Sampling entry | Concrete caller and identity | Save / seed / restore |
|---|---|---|
| `0x141c512d0` | `0x141c50c10 → 0x141c50d00`; Uul-Netol Bone Spires placement, stat `0x54fd` | `0x141c51375 / 0x141c51379 / 0x141c513b4` |
| `0x141c53130` | `0x141c52500`, installed table `0x143550638` slot `+0x4b0`; Xoph Geyser placement, stats `0x54f9/0x54fa` | `0x141c531cb / 0x141c531cf / 0x141c53204` |

Both save the old TLS `+0x170`, replace it with zero-extended `action.sequence11a`,
draw nine bits through `0x140e3b310` until the value is below 360, and immediately restore the saved value.
The integer angle becomes `f32(float(degrees)*0.01745329238474369f)`;
both instructions reference the same float32 constant at `0x1436a2c84`.
Sequence 1 rejects 454 then accepts 126; sequence 2 rejects 396 then accepts 289.
The old thread value is restored rather than replaced by the temporary final state.

These methods also initialize a separate eight-word context:
`[sequence, derived, owner, repeats2d0, innerRepeats2b8, sequence, 0, 0]`.
The TLS scope ends before subsequent radius getters and placement helpers use that other context.
Do not treat the eight-word initializer as the source of the TLS angle or extend the saved scope over the later calls.

The concrete caller stats identify two graft-skill workflows, not ordinary TS or generic action setup.
They prove that deterministic local TLS scopes are possible; they do not establish such a scope around
TS wind `0x1414fc340` or additional-chain preparation `0x141ba2e60`.
The complete direct bodies of those two target consumers have no mapped save/seed/restore around the draw;
all outer/indirect callers are not exhausted.
Their synchronization still requires the decision-point state or a proved enclosing scope and prior draw sequence.

Evidence: round9 `tls-extra-scope-full.asm`, `rng-action-bonespire.asm`, `rng-action-vtable-constructors.asm`
and `rng-scope-envelope-followup.zh-CN.md`; round10 `sequence-scopes.asm` and `sequence-scope-vectors.json`.
Offline enumeration covers all 65,536 sequence values for this scalar translation (at most ten draws).
A separate retained-instruction interpreter agrees on 136 draws across 106 sequence cases;
neither check executes the native function or validates a live action trace.

The [inline wind observer](capture-contract.md#26-inline-tornado-shot-wind-randomness) now records the actual
pre-state, CPU post-state and TLS store independently at this separate TS consumer. Helper-only tracing
missed that inline step. Its wind invocation also scopes the existing resource-RNG hooks, while retaining
the distinction between effect selection, TLS rotation and child-group geometry. The original-byte audit
and five-instruction finite controls establish the hook premises; no runtime thread/outer envelope is supplied.
Each call is checked independently, with no assumed continuity across wind calls or other consumers.

#### 6.2.4 Remaining writer candidates: advancement and a narrower placement scope

Two owners left untyped by the round37 provenance index now have explicit instruction-level classifications.
This reuses that sealed index; it is not another whole-image writer scan or proof of complete writer coverage.

At `0x1413c0c00`, the store `0x1413c0d1e` is the second LCG advance from the first result retained in R11d,
not a new seed. Three directly called vector/scalar leaves between the two stores preserve R11/RBX/R14;
their known call arguments put their output writes on the caller's stack. A call-clobber-conservative index
therefore lost a positive lineage that this bounded inspection recovers. The first store is `0x1413c0c69`.
This classification does not identify every caller or trace the later effect attachment.

The other owner, `0x141c50ff0`, has a genuine temporary scope. The known caller `0x141c50d00` invokes it at
`0x141c50f70`, after obtaining candidate targets; false return selects the already mapped `0x141c512d0`
fallback. Its upstream `0x141c50c10` reads Bone Spires count stat `0x54fd` and calls that same placement
workflow. This supplies a concrete domain, rather than treating an unidentified writer as generic action setup.

For a nonnull candidate in the checked loop, let `i = EDI` be the requested placement slot and `r = EBP` the
failed-placement count. The scalar region `0x141c51080..0x141c5111b` does the following:

1. Save the old TLS uint32 and set `seed = u32(zero_extend(sequence11a) + i + r)`.
2. Seed TLS, request nine bits until the sample is below 360, and convert degrees using binary32
   `0.01745329238474369f`.
3. Reset TLS to the **same seed**, call `0x140e3b3f0(2,15)` and restore the originally saved TLS state.
   That helper rejects four-bit samples at least 14 and returns the accepted value plus two.

The second integer return is overwritten by the subsequent GS-vector load in the checked caller; it must not
be renamed a random placement radius. Resetting before that call makes its draw stream independent of the
number of rejected angle samples, while the final restoration preserves the incoming thread state.
Restoration precedes vector rotation, terrain/spacing checks and appending a position. Failed placement keeps
the requested slot and increments `r`; a null candidate skips these draws. The surrounding loop caps failures
at 1000 and can clear accumulated positions before returning false. Those surrounding paths are static
observations, not part of the scalar-region controls.

Round75 interprets that exact scalar region and both actual helper bodies in 144 supplied-state cases,
covering four sequence values, three slots, three failure counts and four independent prior TLS states.
All return the original TLS state; repeated seeding and both rejection streams are checked separately.
No target-gathering, complete placement, actual thread or official-server route is supplied by those controls.
This additional Bone Spires scope still provides no enclosing scope for ordinary TS wind, startup extra Chain
or per-qualification Pierce; do not extend it past its concrete restore point.

### 6.3 Additional-chain budget is a non-cosmetic TLS consumer

Preparation `0x141ba304d..0x141ba3073` reads `additional_chain_chance_%` (`0x227d`).
Signed `p<=0` stores false without a draw; otherwise it calls `0x140e3b450` and stores the bool in `action+0x478`.
Shared Chain budget adds that cached bool; ordinary cloning preserves it.
This is not another sample from either prevention context in the table above.

The complete common preparation `0x141ba2e60..0x141ba40ab` first calls repeat/animation preparation.
Its `action.byte1e8 != 0` gate skips the later write point entirely.
Each invocation that actually reaches the write point overwrites `+0x478`; there is no old-value/initialized guard there.
It is therefore a per-preparation outcome, not an unconditional once-per-cast roll or a new roll for every Chain target.
Later preparation failure/cancellation does not undo draws already consumed.
The five final classes covering all six witnesses reach this preparation:
Fireball slot `+0x500` binds directly; default ranged/BA/KB/TS use `0x141b6f460 → 0x141ba2e60`.
TS has a further checked concrete start call at `0x1414fbffa`.

The complete helper `0x140e3b450..0x140e3b4a8` returns true immediately for unsigned `p>=100`,
false for zero, and for `1..99` requests seven bits from the same TLS LCG helper:

```text
do:
    state = u32(state*0x41c64e6d+0x3039)
    r = (state>>16)&0x7f
while r>=100
return r<p
```

Each rejected `100..127` advances the thread state again; it is not a modulo-100 or guaranteed single-draw roll.
The helper alone would accept a negative signed bit pattern through unsigned `>=100`,
but this preparation call's signed positive guard prevents that case.
Zero and at-least-100 values consume no TLS state in this path.
Seven bits follow from the helper's highest-set-bit scan over 99, not an assumed random-library contract.

Unlike the mapped cosmetic orientation, this bool enters a local continuation budget.
Identical deterministic action seeds alone therefore do not establish identical complete continuation decisions.

Copy `0x141ba2410` preserves this bool at `0x141ba2735..0x141ba273c`, executed Chain count `+0x458`
at `0x141ba26ab..0x141ba26b1`, and close-range compensation `+0x47c` at `0x141ba2742..0x141ba2748`.
The checked multi-emitter clones without re-preparing that outcome.
Siblings inherit one preparation result but consume separate action-local Chain counters;
a descendant created later inherits the already consumed count unless a specific override changes it.
The full checked TS clone chain and secondary template do not reset these three fields.
See [continuation state](continuation-and-memory.md#6-chain-target-selection-and-sibling-coordination).

The selected final-count stat `0x223f` is additive with the cached bool; it does not override it.
Supplying a count already including a chosen chance result while retaining `0<stat227d<100` still triggers the draw
and can add another one. With `stat227d<=0` the cached contribution is deterministically zero;
with `stat227d>=100` it is deterministically one, both without a TLS draw at this point.
These are static input consequences, not a recommendation to discard probability or an observed official publishing policy.
No network field directly assigning `action+0x478` has been established.

Evidence: round16 `additional-chain-budget.zh-CN.md`, `additional-chain-witnesses.asm` and
`additional-chain-audit.json`; the latter covers 3124 retained instructions, 16 aligned spans and 25 final-table slots.
Complete seed-writer coverage, actual execution thread, other consumers, mode coverage and reconciliation remain open.
This client-local finding does not imply an official server uses or trusts the client's thread state.

### 6.4 Local spell dodge is another random family

Ordinary Fireball's `+0x570 → 0x1419eee20` calls `0x141ebd2f0` with effective caster stats,
target, sequence u16, derived u8, stage u8 and `nondeterministic=false`.
This function performs local spell-dodge admission, not an Actor result-queue lookup.
It does not use the projectile layout/speed stream.

After object classification and cannot-be-dodged/always-dodge guards, it computes integer
`chance=truncTowardZero(targetSpellDodge*(100-casterReduceEnemyDodge)/100)`.
No 100-percent cap was located in this helper; nonpositive chance passes immediately.
Target stat reads may redirect through the resolved `shaman_voodoo_pole_target_id` target.

The deterministic first seed is `u32((sequence<<16)|(derived<<8)|stage)`.
Each seed goes through one signed Schrage/Park–Miller transformation with divisor 127773,
multiplier 16807, subtraction factor 2836 and negative-result adjustment 2147483647, then modulo 100.
Keep signed truncating division and native 32-bit arithmetic; a library's seed normalization is not established here.
For nonzero `extra_dodge_rolls`, clamped to `[-1,1]`, the second seed is
`u32((derived<<16)|(sequence<<8)|stage)`.
Sequence remains 16 bits in this second expression, so its bits overlap with derived;
this is not a permutation of three separate bytes.
Negative extra-roll value selects the maximum roll; positive selects the minimum.
The helper passes if the selected roll is at least chance.
The calling `+0x570` wrapper inverts this bool: true there means this contact is excluded.

A true nondeterministic flag instead draws/rejects through the TLS bit helper until below 100.
The ordinary Fireball wrapper explicitly passes false.
These static branches are documented but are not part of the offline layout RNG tool's tested interface.
Round31 separately validates the selected spell arithmetic and the attack entropy/creation/timer contract in
[local hit prediction](local-hit-prediction.md); its disclosed offline interpreter is separate from that layout tool.

### 6.5 Native trig has distinct implementations and rounding boundaries

Reconstructing the angle sample does not by itself reconstruct the direction vector.
The checked TS terminal and shared radial callers reach the same two single-output float interfaces:

| Caller | sin | cos |
|---|---|---|
| TS terminal `0x141a82a48/0x141a82a27` | `0x14015a720 → 0x142d7b0f0` | `0x1401f1670 → 0x142d76440` |
| radial `0x141bb9647/0x141bb9652` | `0x142d7b0f0` | `0x142d76440` |

Both entries branch on global DWORD `0x14458157c`.
Zero selects separate double-precision multiply/add operations; nonzero selects an AVX/FMA implementation.
The latter contains actual fused multiply/add instructions with one rounding, not interchangeable separate operations.
Both promote the float input to double and convert the normal result back to float.
Small-input and quadrant-reduction branches also matter.
The producer and runtime value of this dispatch flag, and the active floating-point environment, remain unverified.

TS terminal then negates the returned cosine, multiplies by float32 120 and truncates toward zero;
its sine branch multiplies by float32 -120 before truncation.
Those signed integer offsets enter the existing integer aim formula.
Radial angle construction and rotation instead use separate float32 multiply/add/subtract instructions outside trig;
the caller does not fuse them simply because its trig callee may use FMA.

Generic batch variance `0x141bb2960` uses another double-output helper `0x140e2acd0..0x140e2b69e`.
It writes float sine/cosine outputs, uses different double coefficients and has no FMA instructions in its checked body.
Matching terminal trig therefore does not automatically match variance rotation.
Host `sin/cos` plus an outer float conversion remains a candidate approximation, not a verified replacement.

The ordinary terminal sampler has 7001 finite angle outputs.
Retained-instruction interpreters now cover all 7001 inputs for each ordinary/FMA branch without executing client code.
Under nearest/ties-even and gradual-underflow assumptions, both produce identical vectors in this domain.
Compared with this host's `f32(math.sin/cos(phi))`, five sine and nine cosine results differ by one float ULP;
after the actual signed multiplication by 120 and truncation, all 7001 integer aim offsets agree.
The interpreters cover 287 ordinary and 174 FMA instruction sites;
an independent pinned-byte re-decode checked their union of 455 sites and 67 retained constant records.
FMA uses an exact rational product/sum followed by one double rounding; unsupported exact-zero FMA signs fail explicitly.
This closes a bounded candidate comparison, not arbitrary trig equivalence or a live-client observation.
The separate bounded [radial count/phase/origin comparison](#612-radial-rotation-and-an-explicit-grid-boundary-counterexample)
now exposes a concrete grid-boundary difference; it does not establish full finalizer or actual-input parity.
Round11 `native-trig-provenance.zh-CN.md`, `trig-focused-complete.asm` and `trig-read-manifest.json`
retain checked return boundaries, eight byte-range digests and 67 referenced constants.
`check_terminal_trig.py`, `terminal-trig-results.json`, `terminal-trig-fma-results.json`
and `terminal-trig-finite-domain.zh-CN.md` retain the finite comparison and its explicit environment/coverage limits.
These are static implementation/precision observations; they do not establish a live-client parity result.

### 6.6 Variance sincos: finite stat witnesses and geometry limits

The extracted GrantedEffectStatSets table contains eight explicit `projectile_angle_variance` witnesses,
with distinct full widths 8, 15, 25 and 45 degrees.
These are concrete inputs, not all obtainable skills or every possible dynamically modified width.
For each width, interpreting `0x140e2acd0` over all 3001 quantized samples gives 12,004 completed angles,
without unsupported-instruction or missing-memory failures.
The 65 covered instructions, all 67 retained constant records and eight source-range digests were
independently checked against the pinned executable.

Compared with this host's float-converted math functions, sine bits agree throughout;
cosine differs by one float ULP at two width-15 samples and one width-45 sample.
Preserving the caller's independent float32 rotation and origin addition yields 40 rotated-vector and
21 absolute-aim bit differences across 180,060 explicitly synthetic geometry cases, but no final grid differences.
These 15 origin/ray fixtures are not runtime coordinates or an exhaustive geometric domain.
They do not justify replacing native trig for arbitrary positions, other widths, distance variance or finalizers.

All four widths stay within the helper's small-angle branches.
Large-angle reduction, exceptional inputs and the running floating-point environment remain unverified.
Round12 `variance-sincos-finite-domain.zh-CN.md`, `check_variance_sincos.py`,
`angle-variance-data-witnesses.json`, `variance-sincos-results.json` and `variance-independent-check.json`
retain data provenance, operand order, finite results and the independent byte checks.

### 6.7 Ordinary fan uses a separate atan before the shared variance sincos

Fan step calculation calls `0x142d75ccc` directly at `0x141bb985a`.
The float-input atan implementation promotes to double, performs piecewise rational arithmetic,
then converts back to float at its checked return `0x142d75eb7`.
It has neither the sin/cos dispatch flag nor FMA instructions in that function.
The neighboring function at `0x142d75ec0` is separate and must not be included as an atan continuation.

Fan rotation at `0x141bb9999` then calls the same independent sincos helper as batch variance.
Randomized layout at `0x141bb941a/0x141bb9425` instead calls the separate CRT sin/cos entries,
as does radial layout.
The four layout branches therefore do not share one interchangeable trig implementation.

Retained-instruction interpretation covers 800 explicitly synthetic cursor fixtures:
two origins, axis/diagonal offsets of 1 through 200 cells, default spread 60 and modifier zero.
All five normal finite atan magnitude branches are exercised, covering 86 instruction sites and 16 constants.
Compared with this host's float-converted atan, all 800 atan results, 3200 count-dependent steps
and 15,200 fan angles have equal bits under the stated nearest/ties-even assumptions.
The following section separately checks the subsequent sincos rotation and quantization fragments;
admission and the complete finalizer remain outside this comparison.
Zero distance, other modifiers, fixed-spread overrides and exceptional atan paths remain outside these fixtures.
Round12 `fan-atan-and-layout-trig.zh-CN.md`, `check_fan_atan.py`, `fan-atan-results.json`
and `fan-atan-covered-inputs.json` retain the exact function boundaries, constants and finite inputs.

### 6.8 Fan rotation and final grid: bounded finite comparison

Round13 interprets the actual independent sincos and outer float32 instructions for those same 800 cursor fixtures,
with counts 2/3/5/9, giving 15,200 rays and 1038 distinct finite angles in approximately `[-pi/3,pi/3]`.
Unlike the four variance-width witnesses, this includes 440 rays taking the helper's signed pi/2 reduction branches.
It does not cover the long-reduction path or exceptional values.

The input normalization performs float32 `inverseLength = 1/L`, then multiplies both axes by it.
It is not two independently rounded axis divisions by `L`.
The interpreted outer path then centers each angle, rotates, scales by 150 world units and adds origin,
preserving each separate float32 operation.
Actual quantization/floor fragments are interpreted with these world inputs.
This is a joined arithmetic comparison, not a complete admission-through-finalizer or flight/contact execution.

Fresh pinned-byte re-decoding checks 277 covered instruction sites, all 277 sequential-next addresses
and 14 used constant addresses, without discrepancies.
There are no unsupported-instruction, missing-memory or external-call failures in the 15,200 outputs.
Compared with this host's float-converted math functions, sine agrees throughout and cosine differs twice by one ULP.
Those two rays also differ after rotation and scaling, sometimes by multiple ULPs;
origin addition absorbs the differences in these fixtures, giving zero absolute-world or final-grid mismatches.
This is not a bound on errors at other origins, modifiers, widths, lengths or rounding environments.
The native-versus-host comparison is not a .NET `MathF` equivalence test or a runtime-client observation.

Round13 `fan-rotation-and-grid.zh-CN.md`, `check_fan_rotation.py`, `fan-rotation-results.json`,
`fan-rotation-covered-inputs.json` and `fan-rotation-byte-audit.json` retain inputs, instruction coverage and limits.
The [animation chapter](animation-and-release.md#2-main-request-inputs-and-their-independent-rng)
separately connects the scalar animation seed to three weighted selectors;
matching fan arithmetic does not reproduce clip selection or release timing.

### 6.9 Pierce prevention rolls at each qualification

Shared Pierce qualification `0x141ba96a0` reads `projectile_chance_to_not_pierce_%` (`0x3438`) at
`0x141ba96f1..0x141ba970f` after rejecting mode 1 and cannot-pierce (`0x2247`), but **before** forced,
return-always, target/arrow/range and finite-budget success rules.
Signed `p<=0` skips sampling; positive values call the same TLS chance helper `0x140e3b450` as additional Chain.
For `p>=100` it returns true without a draw, preventing Pierce.
For `1..99`, each reached call takes seven-bit TLS samples, rejecting 100..127, and prevents Pierce when
the accepted sample is below `p`. This result is not stored in the action for later qualification calls.

Thus a zero remaining Pierce budget or a later forced-pierce condition does not skip this earlier probability draw.
The TS primary forced bit and return-always-pierce likewise do not override probability/cannot/mode rejection.
Conversely, earlier contact/history/avoidance rejection can prevent the qualification method from being called;
this is per reached qualification, not every geometrically observed contact.
The [ordered continuation rules](continuation-and-memory.md#11-pierce-qualification-order-probability-and-budget-side-effects)
separate current stat reads, cached finite budget `+0x44c` and executed counter `+0x454`.

For an explicit TLS state of 1 and `p=50`, consecutive reached calls have different behavior:

| Call | Updated states and seven-bit samples | Probability-prefix outcome |
|---|---|---|
| First | `1103527590 → 70` | continue to later Pierce rules |
| Second | `2524885223 → 126` rejected, then `662824084 → 1` | prevent Pierce |

These are offline static-rule vectors, not running-client observations or proof that later success rules pass.
Round17 `pierce-gate-vectors.json` retains nine groups including nonpositive, at-least-100, mode/cannot no-draw
and consecutive-call cases; each LCG update is checked against the existing explicit-state orientation helper.
`pierce-witnesses.asm` and `pierce-audit.json` retain the full qualification, caller, construction/copy and helper
spans, plus all five final classes' relevant slots. Unlike Chain's prepared `+0x478`, this is an uncached
noncosmetic TLS decision. Actual enclosing seed scopes/thread history and official server reconciliation remain open.

### 6.10 Probability mechanisms have different lifetimes and shortcuts

Do not route all probability modifiers through one presumed random policy:

| Mechanism / key | Random source and result lifetime | No-draw cases at the mapped call site |
|---|---|---|
| Extra Chain `0x227d` | TLS; preparation overwrites cached `+0x478`, then clones inherit it | earlier preparation gate, signed `p<=0`, or `p>=100` |
| Prevent Pierce `0x3438` | TLS; each reached qualification, no cached result | earlier mode/cannot gates, signed `p<=0`, or `p>=100` |
| Extra Fork child `0x2fd7` | fresh Init8, local descriptor count | earlier Fork admission/retirement, signed `p==0`, or `p>=100` |
| Main Return `0x3216` | fresh Init8 at the probability branch | earlier qualifier result, signed `p==0`, or `p>=100` |
| Extra Return `0x49f5` | fresh Init8 on first execution with an entity; caches `+0x480` | no entity or already returning; **no** probability-value shortcut |

The last three use the same eight input words listed in section 1 and bounded integer `[0,99]`, with signed
`sample<chance`. Negative nonzero values still draw for Fork/main Return; extra Return draws even for zero
and at-least-100 values. A bounded call may consume rejection transitions; it is not always one raw output.
They use separate newly initialized contexts rather than consecutive samples from a shared global stream.

For equal actual inputs, main/extra Return have the same first bounded sample.
The [Return lifecycle](continuation-and-memory.md#73-main-and-additional-return-probabilities-restart-equal-recipes)
states the intervening callback, phase and clone premises; do not claim equal inputs unconditionally.
Fork generation is absent from its seed, while changed derived identity or repeats can alter subsequent results.
For recipe `[1,2,4096,0,0,0,0,0]`, the first sample is 80: extra-Fork chance 80 keeps two children, 81 selects three.

Round18 Return/Fork aligned listings and audits close these call sites, shortcut boundaries and copy/state timing.
Their offline vector groups reuse the existing audited eight-word arithmetic reference, not native execution.
The capture profile now also observes the scoped TLS helper's actual chunk inputs and final state;
this supports later comparison without claiming to know its complete earlier seed/consumer history.

The later [terminal/Return composition](motion-and-contact.md#102-actual-return-switches-the-history-phase-and-resumes-motion)
executes the original eight-word initializer, inclusive 0..99 distribution and underlying bounded helper within
the same emitted object's first Return. Supplied words `[4660,2,826366246,2,0,0,0,0]` produce 91/counter 9,
matching the sealed reference. Extra chance 0 and 100 both consume that draw; second Return reuses the cache
without another initialization/draw. Cannot-return rejects before this executor. These concrete native inputs
are fixtures, not an observed official seed or proof of TLS synchronization.

### 6.11 Shared speed-modifier variation restarts another context

Helper `0x141bb35f0` is an additional consumer of fresh Init8 with
`[sequence11a,derived11c,owner88,outer2d0,inner2b8,0,0,0]`.
Native weapon-stat classification and `skill_is_attack` gates control this branch, independently of the amplitude.
It builds a `[0,1]` distribution with integer maximum 2000, samples bounded 2001 and divides by 2000,
then interpolates from negative to positive signed `projectile_speed_variation_+%_final_with_melee_weapon_attacks`.
Zero amplitude still draws once the gates pass; a bounded call may use rejection transitions.
It does not draw TLS, reuse the `+0x3a4` cache's context or continue Return/Fork's local stream.

Equal actual eight inputs restart the same sample on repeated calls; Chain count and phase are not seed words.
This does not imply equal numeric results: executed Chain count, Return state and current stat queries still
change the [speed-modifier formula](motion-speed-and-time.md#11-shared-integer-modifier-selected-views-and-rounding).
Round19 `speed-modifier-audit.json` retains the seed construction, distribution constants and offline vectors.

### 6.12 Radial rotation and an explicit grid-boundary counterexample

The [radial helper](actions-and-emission.md#41-radial-uses-a-separate-full-circle-step-and-caller-phase)
uses approximate tau `6.283180236816406f`, then native sin `0x142d7b0f0` and cos `0x142d76440`.
Round23 interprets its actual count/step/theta and independent float32 rotation, scaling, height and origin-add
segments. It supplies the resulting world coordinates to the already recovered grid/floor oracle.
The two radius-branch choices are fixture inputs; admission, identity allocation and intervening finalizer
transformations are not executed by this comparison.

Counts 1/2/3/5/9/16/32 and five explicit phases yield 340 angles / 195 distinct bit patterns.
Sixteen fixtures and default-150/preserved-length branches give 10880 geometries, each checked under both
ordinary and FMA CRT paths against independent outer float32 formulas. All 21760 comparisons agree;
ordinary/FMA trig agrees on this domain. Fresh PE checks cover 564 distinct instructions, 71 constant records
and 15 original range digests. Independent review adds twenty explicit phase-zero expected grid controls.

Host trig still cannot be substituted based on earlier terminal/fan grid agreement:
at synthetic phase bits `0x3f384998`, axis ray `(1,0)`, radius 150 and deliberately chosen
origin Y bits `0xc2c5c969`, native sine bits `0x3f28c72d` versus Python-host `0x3f28c72c`
produce world Y exactly zero versus `-0.00000762939453125`.
The injected grid is therefore `(10,0)` versus `(10,-1)`.
Seven test occurrences repeat this one designed boundary fixture across counts; they are not an error rate.
Neither actual phase/position reachability, a .NET MathF result nor a final client collision mismatch is established.

Evidence: Round23 `layout-findings.zh-CN.md`, `layout-radial-manifest.json`, `layout-check.py`,
`layout-results.json`, `layout-vectors.json`, `layout-audit.py`, `layout-byte-audit.json`,
and `layout-independent-review.py/json/md`.
The random-direction and Volley finite cuts below extend those domains separately.
The later [nondefault fan cut](actions-and-emission.md#42-nondefault-fan-branches-and-sequential-distance-offsets)
checks fixed-spread/modified-atan branches, sequential radius/height and machine-width offset conversions with
2,550 finite cases. It interprets the complete fan using explicit per-arrow emitter and cached trig ABI bridges;
candidate attempts are not final motion endpoints. Wider/exceptional domains, actual input publication,
downstream admission/variance and complete finalization remain open.
These gaps do not invalidate the recovered finite equations or the explicit numerical counterexample.

### 6.13 Ordinary random-direction: all 7001 angle bins and selected callers

`0x141bb9280` uses fuller tau bits `0x40c90fdb` (`6.2831854820251465f`), unlike radial/TS terminal tau.
For its zero-to-tau distribution the constructor supplies integer maximum 7000; bounded sampling returns
`q` in the inclusive range `0..7000`. Preserve float32 operations:

```text
sample = f32(f32(f32(f32(q)/7000f)*tau)+0f)
theta = f32(sample+rotationBase)
```

The selected direct dispatcher, Volley center and Volley side callers all pass `rotationBase=+0`.
The callee's `[rsp+0x170]` corresponds to caller `[rsp+0x60]` after its frame and return address;
Volley side's `xmm10` is target height, not a phase producer. Nonzero comparison phases are explicitly synthetic.
The helper normalizes via reciprocal multiplication, selects default 150 or preserved length,
and applies separate float32 height/rotation/scale/origin-add operations as in the finite radial equations.
It samples before per-arrow admission; even zero-count invocation initializes the context/distribution first.
The ordinary count-one dispatcher bypass is a different path.

Round24 `random-read.py`, `random-check.py`, `random-audit.py` and their manifests/results check
all 7001 zero-phase bins, supplementary injected phases and a designed grid boundary:
14,165 geometries / 28,330 two-dispatch comparisons / 7027 distinct angle bit patterns.
Ordinary/FMA agree in this domain. Host sine differs in seven bins and cosine in twelve;
at `q=850`, a deliberately chosen origin gives native injected grid `(9,0)` versus host `(9,-1)`.
This is a numerical counterexample, not an observed game divergence or a .NET MathF result.
Fresh PE audit covers 592 interpreted addresses, 1675 retained instructions, ten caller-phase anchors,
78 constants and 22 range digests. Bounded integer RNG output, subgroup calls and world-to-grid injection
are explicit test boundaries; full finalization, actual world inputs and draw scheduling remain separate.
The [Volley cut](actions-and-emission.md#51-float32-translation-and-subgroup-boundaries) also closes its finite
translation/order and typed side-query contract without claiming complete terrain implementation.

### 6.14 Startup, orientation and Pierce TLS continuity

**S:** the later [Actor runner](world-scheduling.md#21-native-runner-current-root-admission-and-startup-ordering)
composition now executes nonzero additional-Chain preparation through the actual chance helper, followed by
explicit Animated releases and explicit Pierce qualifications in the same supplied TLS memory. This joins
previously isolated consumer rules; it does not establish actual OS-thread membership or official scheduling.

| Consumer | Original call / write | Lifetime in this composition |
|---|---|---|
| Additional Chain | `0x141ba3064 -> 0x140e3b450`, cache write `0x141ba3073` | ordinary preparation overwrites `+0x478`; later stat edits alone do not re-roll it |
| Optional birth orientation | `0x141baa229 -> 0x140e3b310` | selected row byte `+0x41` enables a ten-bit draw for each admitted placement |
| Prevent Pierce | `0x141ba9708 -> 0x140e3b450` | each reached qualification reads current selected `0x3438`; result is not cached |

The native bit helper reads and writes the same TLS `+0x170` using
`state = uint32(state * 0x41c64e6d + 0x3039)`. Seven-bit chance requests take
`(state >> 16) & 127`, rejecting samples at least 100; ten-bit orientation takes
`(state >> 16) & 1023`. Rejected chance samples advance that same state. The joined controls execute the
original helper, including its TLS lookup and store, rather than an external random-value provider.

For supplied initial state `1`, additional-Chain chance `50` first advances to `1103527590`, samples `70`
and caches false. Two explicit releases each admit three placements. With optional orientation disabled,
those placements make no TLS draws; with it enabled they make six ten-bit draws. The following two reached
Pierce qualifications use prevention chance `50` and a supplied forced-Pierce bit, so only the earlier
probability prefix can reject them in this fixture:

| Optional orientation | State before first Pierce | First / second Pierce | Final TLS state |
|---|---|---|---|
| Disabled | `1103527590` | false / true | `4182499122` |
| Enabled for six placements | `3655513600` | true / true | `180171308` |

All six shared-resource witnesses reproduce that difference: bow, wand, Fireball, KB, TS and Burning Arrow.
Paired controls retain equal animation seeds, action-local initializer inputs, emission identities/layouts
and motion arguments. Thus equal action seeds alone do not imply equal later TLS-based decisions when the
consumer history differs. This is a finite native-rule counterexample, not evidence that an MTX changes
authoritative damage or that every installed projectile row enables this orientation option.

No-draw controls preserve the distinct gates. Signed nonpositive chance skips sampling; chance at least 100
resolves without sampling. Earlier mode/cannot-Pierce rejection precedes its roll even with forced bit `4`.
Preparation byte `+0x1e8` preserves the old cache and ordinal without reaching the chance helper. Re-entering
the already-started outer startup also skips preparation; two separate controls explicitly enter the common
preparation prefix and stop after its cache write, demonstrating overwrite/re-roll without claiming a normal
second startup. The six clone-cache observations use the disclosed fixture payload copy, not a newly recovered
full constructor/copy chain. Optional orientation's setter remains an observed presentation boundary.

Ignored round71 `audit_rng_lifetime.py`, `rng-lifetime-audit.asm/json`, `check_rng_lifetime.py` and
`rng-lifetime-controls.json` retain 34 controls and the original byte/slot audit. Shared resources, selected
stats, initial TLS state, later Animated invocations and qualifications are supplied. Enclosing seed scopes,
outside consumers, actual threads, natural contact order and official correction remain separate obligations.
The existing [scoped probe](capture-contract.md) already covers root startup and TLS chance/bit/LCG steps;
this static join does not add or execute live hooks.

## 7. Validation boundary

The [KB lifecycle composition](fireball-and-kinetic-blast.md#31-joining-an-emitted-source-to-its-constructed-explosion-group)
now allocates the group's derived identity before building its actual eight-word position context. Original
initialization, bounded draws, finite distributions and sector traversal agree with the sealed arithmetic reference
for each single-group control. Rejected sectors still consume a discarded fourth angle candidate; removing that
draw changes subsequent sectors. A later group from the same source has a fresh derived word and its own stream.
The finite trigonometric interpreter additionally executes original `0x140e2abd0` scalar range reduction when a
KB angle exceeds two pi. Eleven signed finite controls through four pi check the required unsigned branch and
double-to-integer/scalar conversion support. This does not close all trig domains or TLS seed/consumer history.

The offline reference includes strict four-/five-/seven-word initialization, object hash, raw/bounded samples,
finite float distribution, scalar Bounce/fallback/distance-angle and explicit-state cosmetic orientation.
Retained-listing integer interpretation independently checked
206 initializer/bounded cases and 104 object-hash cases; see the evidence ledger for local material.
Round3 separately checked 103 four-word initializer/raw cases, 721 TLS bit-helper cases and
42 scalar-SSE distance-angle budget cases against retained-listing interpreters.
These checks compare static arithmetic translations, not running-client values or server parity.
Remaining trig domains, unclosed field writers/special release contexts, TLS seed/consumers,
all skipped draws and end-to-end ordering remain reconstruction obligations.
