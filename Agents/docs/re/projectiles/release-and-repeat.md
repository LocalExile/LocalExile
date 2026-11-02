# Release ordinals and repeat lifecycles (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [012 — projectile execution](../012-projectile-execution.md).
This chapter closes selected state transitions feeding [emission](actions-and-emission.md)
and [random inputs](random-inputs.md), using the pinned original PE and five final action tables.
It does not establish runtime clip admission or a network hit-report contract.

## 1. Separate counters

| Offset / width | Meaning in checked paths | Construction and changes |
|---|---|---|
| `+0x2d0:u32` | Remaining outer repeats | Constructor input; decremented on completion, clamped to zero on termination |
| `+0x2d4:u32` | Initial outer repeat count | Constructor input; not changed by the checked completion function |
| `+0x2b8:u8` | Remaining inner repeats | Initially zero; copied from `2b9` during setup; decremented after release |
| `+0x2b9:u8` | Prepared inner repeat count | Initially zero; selected buff callbacks/stat inputs can overwrite it |
| `+0x3f8:u32` | Ordinary release callback ordinal | Initially zero; preincremented at shared release entry |
| `+0x3fc:u32` | Contact-point distribution divisor | Initially one; conditional animation-event counting can replace it |
| `+0x400:u32` | Extra distribution offset | Constructor/copy resets to zero; no nonzero writer established in this cut |
| `+0x448:u32` | Source's admitted-projectile ordinal | Constructor sentinel `0xffffffff`; normal start resets to zero; increments after each binding |
| `+0x450:u32` | Absolute Volley slot | Independently written during emission |

Neither release ordinal nor source ordinal is the shared derived-byte allocator or result stage.
The first ordinary root release reads **release ordinal 1**, while its first admitted projectile uses
**source ordinal 0**. Substituting either for the other changes object hashes or batch random inputs.

## 2. Construction, copy and start

Shared constructor `0x141ba2160` clears `3f8`, sets the qword at `3fc` to one (`3fc=1,400=0`),
and writes `448=0xffffffff`. Repeat base `0x1419e2a50` initializes the inner bytes and outer counts.
The copy chain `0x141ba2410 → 0x1419e2d40` retains outer/inner counters, `3f8`, `448` and `450`,
but resets the qword at `3fc` to one. A clone does not simply preserve every distribution field.

Common start `0x141ba2e60` first calls repeat setup `0x1419e3b10`.
Only after that, when action byte `+0x1e8` is zero, it writes `448=0` at `0x141ba2ed5`
and enters the normal animation-start region. This is a conditional start transition,
not a universal property of construction, copying or every call to an emitter.
Fireball's earlier clear of `2b8` still precedes this shared setup and does not disable repeats.
The four attack-class starts also save [hand selection](hand-selection.md) before this reset;
Barrage's selector can therefore read the prior `448`, independently of its later emission value.

### 2.1 Temporary synthetic-contact context

All five final `+0x268` slots bind wrapper `0x14193b8a0`.
It takes an existing action and a nullable event pointer, sets `1e8=1`, calls concrete `+0xf8`,
constructs a local event, calls concrete `+0x140` with it, then clears `1e8=0` on ordinary return.
The local event copies the supplied fields when present, but forces DWORD `+8=0`, name `contact`
and WORD `+0x38=0x9b8d`. This is a positive producer/consumer chain for a temporary synthetic-contact
startup context; it is not a Predictive/Lockstep mode bit.
No new action is allocated or source ordinal initialized in this wrapper.
The [typed server-command ingress](synthetic-start-and-server-commands.md) connects `0x1ce/0x1cf`
through executor `0x142195880`: it creates/configures an action, registers it with derived byte one,
then calls this wrapper before removing the temporary source action.
Other caller origins, concrete callback eligibility and exceptional-exit cleanup remain unclosed.

Base construction clears `1e8`; base copy preserves it. Clearing the source after an event does not clear
a clone made while the flag was one. No later clear for every such clone is established here.
With `1e8` nonzero, common start skips its normal source-ordinal reset and owner-position snapshot,
but still enters repeat setup first. That setup refreshes selected preparation inputs/descriptors before
its own flag check skips inner-repeat eligibility rebuilding.
Thus this context does not freeze every prepared field. Fireball's concrete start additionally clears `2b8`.
The actual prior source ordinal must be retained as an input, rather than assumed to be zero or sentinel.
Where the forced event reaches the ordinary release callback, `3f8` still preincrements normally.

## 3. Release and admitted-projectile ordering

Shared release `0x141ba4930` preincrements `3f8` at `0x141ba493a`, before descriptor refresh,
count gating and emission. A suppressed or empty release can therefore advance it.
It is not the number of successfully emitted projectiles and does not reset `448`.

Multi-emission `0x141bb2480` rejects stopped budgets/invalid side origins before construction.
On its admitted path it reads source `448` into the object hash (`0x141bb260c`),
copies that value to the new action (`0x141bb26f1..0x141bb26f7`), binds the entity via `+0x710`,
and then increments the source (`0x141bb271f..0x141bb2722`). Rejected origins do not spend this ordinal.
Single emission `0x141bb13a0` likewise hashes/copies before binding and increments the source afterward.
The new action retains its assigned ordinal while the source advances.

### 3.1 Concrete flying-clone handoff

The five checked final `+450` clone bindings use these positive copy chains:

| Final class | Copy wrapper chain before common `0x141ba2410` |
|---|---|
| Bow / wand | `1412e2970 → 1412439d0` |
| Burning Arrow | `141378df0 → 1412439d0` |
| Tornado Shot | `1414fbc30 → 141a82340 → 1412439d0` |
| Fireball | `1413dfc70 → 1413dfbf0 → 141241a60` |
| Kinetic Blast | `1412e13a0 → 1412e12a0 → 1412439d0` |

Common copy reaches `0x1419e2d40 → 0x141938020`.
The base copies `1e8` at `0x1419384bc/4c4`; projectile copy copies `448` at `0x141ba267b/2681`
and resets `3fc/400` to `1/0`. Concrete wrappers install their final tables and skill fields.
This binds the earlier base-copy facts to actual flying clones, rather than inferring them from a shared offset.

In multi-emission, clone creation `0x141bb26d9` precedes registration `0x141bb26e9`.
The emitter then explicitly recopies the **current source** `448` into the child at `0x141bb26f1/26f7`,
writes absolute Volley slot `450`, calls child `+710` at `0x141bb2718`, and increments source `448`.
The checked single-emission branch instead registers at `0x141bb1aa4`, binds at `0x141bb1ab9`,
then increments at `0x141bb1abc`; it has no corresponding post-registration ordinal recopy.
It depends on the actual copy chain. Do not assume the two paths have identical callback-mutation behavior.

The [typed registration/initialization contract](action-initialization.md) now closes another handoff edge:
copy clears `124` while retaining `1e8/448`; registration appends the child before guarded initialization.
All five final initialization callbacks latch before the startup-global gate. With that gate nonzero,
the checked ordinary registration/initialization path neither resets the ordinal nor clears the synthetic flag.
Conditional native contribution callbacks under global zero remain separate stateful boundaries.

All five final `+710` bindings are `0x141bab560`.
It replaces child flight entity `+328` at `0x141bab659`, configures observers/target state,
then tail-calls `0x141bab8f0`. Registration can call initialization and writes retained-action identity/state.
These are concrete handoff edges, not a transitive guarantee that every nested callee preserves `448/1e8`
or that every synthetic child keeps its flag throughout its lifetime.
The source's actual synthetic pre-emission ordinal and every later child-flag clear remain open.

### 3.2 Ordinary release example

For an ordinary path with two admitted projectiles per release and no other field mutation:

| Point | Source `3f8` | Source `448` | Assigned child ordinals |
|---|---:|---:|---|
| Constructed | 0 | `0xffffffff` | — |
| Normal common start | 0 | 0 | — |
| First release entry | 1 | 0 | — |
| First release complete | 1 | 2 | 0, 1 |
| Inner repeat release entry | 2 | 2 | — |
| Second release complete | 2 | 4 | 2, 3 |
| Outer normal restart | Retained here | 0 | — |

The `0x141bb0f20` call at `0x141ba3b58` builds a profile for Cannonade Volley firing-point effects;
it is **not evidence of direct gameplay emission bypassing `3f8`**. The previous interpretation is corrected.
Its gates include normal `1e8==0`, the startup global, stat `4323 mtx_cannonade_volley_effect_at_firing_points`
and unsigned effective count greater than one. Its layout/callback chain reaches entity creation `0x141f682a0`
in this MTX presentation path; a globally effect-specific factory identity is not established.
Profile's optional integer-line query clips profile distance at the first blocking cell; it does not report a hit.
Other skill-created direct-emitter calls still require their own lifecycle evidence.
Checked redirect `0x141bb2ae0` has no direct `448` write; that bounded body check does not exclude all nested aliases.

## 4. Inner repeats rewind an existing animation

Repeat setup skips its initial-source preparation when `1e8` is nonzero or selected mine/trap/totem
stats apply (`36c/14c/35a`). Those paths do not unconditionally clear previously copied inner state.
In `0x1419e3bd0`, unsigned `outerRemaining < outerTotal` clears `2b9`.
Otherwise the selected sources are:

- Nonzero rapid-fire stat `2342`: a type-`4fc` buff must match skill and action `118`;
  its object's virtual `+0x58` receives action `11a`. A true AL permits the stat's low byte to replace `2b9`.
- Nonzero maximum-seals stat `2905`: a type-`502` matching buff's callback receives the action pointer;
  the returned **AL itself** replaces `2b9`. Maximum seals is not the actual repeat count.

The latter can overwrite the former if both branches execute; legal support combinations are not inferred.
The [concrete repeat-source callbacks](repeat-source-state.md) now bind these two definitions to their actual
backend methods: RapidFireCharge accumulates/consumes sequence-qualified state; Anticipation consumes a positive
count once and replays its cached count on same-sequence reentry. Both mutate Buff state without RNG draws.
Their existing-instance network overwrite route is mapped separately from official server values/timing.
Skill matching compares the current skill ID exactly; a null action `f0` supplies current ID zero.
Buff skill ID zero is not a wildcard.
Missing/zero sources do not universally clear an existing `2b9`. Finally setup copies `2b9` into `2b8`.

Event `9b8d` in shared handler `0x141ba4620` first calls `+0x4b8`, then decrements nonzero `2b8`,
refreshes and calls `+0x470`. Fireball's complete chained handler `0x1419eec90..0x1419eee1b`
has the same release-before-decrement ordering. Ordinary RNG inputs therefore see `K,K-1,...,0`.

All five final `+0x470` slots bind `0x1419e41a0`. The checked callers pass factor bits `0x3dcccccd`
(approximately 0.1). For ordinary finite inputs it computes:

```text
d = selectedTrackIndex != -1 ? controller[1bc] - controller[1b8] : 0
desiredTime = max(0, min(controller[1a4] - d * factor,
                         event[0c] - epsilon))
epsilon bits = 0x3c23d70a
```

Through `0x140cf0c00 → 0x141dbda20`, it relocates the **existing selected animation timeline**,
with state-dependent immediate/deferral/listener branches. It does not construct another action queue.
The inner path does not reenter common start, so `448` continues accumulating across admitted inner batches.
Whether a particular rewind yields another runtime event still depends on playback/resource/listener state.

## 5. Outer completion restarts the action

Completion `0x1419e3960` decrements `2d0`. If its old value was zero or `+0x210` cancels,
it clamps the field to zero and terminates. Otherwise it advances stage and calls `+0x458`, then `+0xf8`.
All five checked `+0x458` slots are no-ops; `+0xf8` reaches common start.
The normal outer path thus emits with remaining values `R,R-1,...,0` and conditionally resets `448` at restart.
The transient unsigned underflow from old zero is clamped before termination, not a normal next-release input.
`+0x210=0x14193eb80` checks skill-type query `0x30` and action `11e`; no inferred type name is assigned here.

## 6. Contact-point distribution is separate

When stat `2120 distribute_projectiles_over_contact_points` is nonzero, common start counts selected-animation
events whose DWORD `+8` is zero or in `[16,26]`, and whose WORD `+38` is `9b8d`; it stores that count in `3fc`.
It does not write `400`. When the stat is zero, this section does not refresh `3fc`.

Shared getter `0x141ba61c0` implements:

```text
total = sharedCount141ba6070()
divisor = signed32(action3fc)
if divisor <= 1: return total
(q,r) = signed_div_rem(total, divisor)
index = wrap32(action448 + action400)
candidate = q + (unsigned32(index) < unsigned32(r) ? 1 : 0)
return max_signed(candidate,0)
```

Signed division and the unsigned remainder comparison are intentional. The getter does not increment an ordinal.
The divisor counts eligible animation contacts, not remaining inner repeats. Ranged, KB and BA bind `+0x5f0`
directly to this getter; Fireball and TS override it. Do not apply the equation universally to their special stages.
The upstream [count formulas](count-stat-inputs.md) and later preparation override remain separate layers.

## 7. Final bindings and evidence boundary

| Witness / final table | Start `+f8` | Event `+140` | Clone `+450` | Count `+5f0` |
|---|---|---|---|---|
| Bow/wand `14326d930` | `1412e2a40` | `1412df000` | `1412e2970` | `141ba61c0` |
| Fireball `14333ac40` | `1413df7f0` | `1419eec90` | `1413dfc70` | `141a8b520` |
| KB `14326b9c0` | `1412e1490` | `141ba4620` | `1412e13a0` | `141ba61c0` |
| TS `143423898` | `1414fbfb0` | `1414fbd00` | `1414fbc30` | `141a82510` |
| BA `1432ec7e8` | `1412def90` | `1412df000` | `141378df0` | `141ba61c0` |

The checked clone wrappers reach shared copy `0x141ba2410`; starts reach common start.
Round22 `repeat-lifecycle-audit.py/json` rereads 45 complete functions / 64 aligned ranges / 6713 instructions,
105 final-table slots (100 expected-target assertions), 58 instruction anchors and two float constants.
The complete chained Fireball event body supersedes its short first unwind fragment.

Round23 `start-audit.py/json` adds 42 functions / 68 chained ranges / 6318 instructions,
40 final slots and 77 anchors for synthetic startup, concrete wrappers and the separate MTX effect chain.
Round28 binds the two server-command callers; it does not exhaust other callers.
Cloned-flag later cleanup, other writers, actual Buff state/publication inputs,
nonzero `400` writers, complete animation timing and runtime scheduling remain open.
No client execution, C2S collision report, damage-authority transfer or server implementation is established here.
