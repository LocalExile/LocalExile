# Ordered attack hit probability (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md).
**S:** this reconstructs the stable-input composition in `0x141ebc990`, base chance `0x1424ff510`,
and the finite nonnegative native power path they reach.
It feeds the [target entropy callback](local-hit-prediction.md); it is not a numerical damage roll,
a critical-strike roll, or an authoritative hit-result lookup.
The selected ordinary attack callers and outer shortcuts remain in that chapter.

## 1. Inputs, hand selection and early success

The builder receives attacker Entity, selected attack stats, target Entity, selected target stats,
a hand-selector byte, a caller-projectile byte and a probability callable.
The checked legal hand selectors are **0: off hand, 1: main hand**.
Their native two-entry tables are in this order; do not reverse them by guessing a bool parameter name.
The selected caller carries action `+0x518` into this hand input.
Stats and component getters are input boundaries, not another locally reconstructed stat-grant graph.

The following conditions return true **without invoking the probability callable**, in this order:

| Order | Condition |
|---|---|
| 1 | selected-hand `off_hand_always_hit` / `main_hand_always_hit` (`0x2b1/0x2b0`) nonzero |
| 2 | target `cannot_evade` (`0x93e`) nonzero |
| 3 | selected-hand always-hit-burning stat (`0x59fc/0x59fb`) and target `is_burning` (`0xe42`) nonzero |
| 4 | attack `hits_cannot_be_evaded_vs_blinded_enemies` (`0x1e7c`) and target `blinded` (`0x3cd`) nonzero |
| 5 | attack `attack_is_melee` (`0x5ce`), `is_wielding_sword` (`0x2f65`) and `melee_hits_cannot_be_evaded_while_wielding_sword` (`0x5109`) all nonzero |

Callable cleanup still occurs on these returns.
Outer Chest, target never/always-hit, provider-redirection and post-success dodge-all rules are separate;
the builder's early true is not an unconditional final damaging hit.

## 2. Accuracy modifiers and their rounding boundary

`A` starts from selected-hand accuracy `0x125/0x124`, interpreted as signed i32 at this stage.
`M` starts at `1.0f`.
For each qualified nonzero stat below, in order, compute
`factor=max(0f, f32(f32(i32(u32(100+stat)))/100f))`, then `M=f32(M*factor)`:

| Order | Attack stat | Qualification |
|---|---|---|
| 1 | `accuracy_rating_+%_final_vs_unique_enemies_from_mastery` (`0x3cbc`) | target `monster_rarity` (`0x243b`) equals 3 |
| 2 | marked-enemy mastery (`0x46aa`) | qualifying target Buff exists, as below |
| 3 | marked-enemy Crucible modifier (`0x46ab`) | same qualifying-Buff predicate |
| 4 | close-range mastery (`0x47c8`) | native squared positioned distance at most 400 |
| 5 | monster-versus-monster accuracy (`0x4b07`) | target Monster component exists; target `is_player` (`0x170`) and `is_player_minion` (`0x48a`) both zero |

The marked test queries the target Buff component only if either marked modifier is nonzero.
It searches entries for all of: Buff `+0xd0` mask `0x10` clear, definition `+0x67:i32 == 0x0e`,
and Buff owner `+0x28:u32` equal to the attacker's Entity ID `+0x88`.
It stops at the first match; multiple matching Buffs do not multiply a modifier repeatedly.
A mark by a different owner does not satisfy this predicate.

Close range reads attacker/target Positioned `+0x294/+0x298` integer coordinates.
Differences, squares and sum use wrapping 32-bit arithmetic; comparison with 400 is unsigned.
The normal boundary is distance 20 in those coordinate units, including equality.
Do not replace its wrapped behavior with an unbounded squared-distance computation without qualification.

If `M` is not exactly `1.0f`, convert signed A to f32, multiply by M and call `0x140664f10`.
The finite in-range meaning of that leaf is `floor(f32(value+0.5f))`, not ties-to-even or truncation.
Its original CVTTSS2SI/negative correction is retained; with masked invalid conversion, out-of-range input returns indefinite
integer bits `0x80000000` rather than applying an invented saturation.
If M equals one, this optional round-trip is skipped.

## 3. Attack classification and defender base

The two booleans are:

```text
projectile = attack[is_projectile 0x5cd] != 0 OR callerProjectile
melee      = attack[attack_is_melee 0x5ce] != 0 AND NOT callerProjectile
```

They can both be true for a false caller flag and two nonzero stats.
The checked ranged wrappers pass a true caller flag, which prevents that melee branch even if a stat says melee.

If target `keystone_voranas_adaption` (`0x5834`) is nonzero, select `u32(2*ward[0x38d0])`
as defender base, with multiplier one.
It replaces ordinary evasion and skips the projectile-evasion-keystone multiplier here.
Otherwise select target `evasion_rating` (`0x11c`).
With target `keystone_projectile_evasion` (`0x349`) nonzero, multiply this evasion by
`(projectile ? 2f : 1f) * (melee ? 0.75f : 1f)`, using f32 order.
Otherwise that multiplier is one.

Now apply effective-level modifiers, preserving the **integer multiply before conversion**:

```text
A = cvtt_i32(f32(f32(i32(u32((100 + attack[0x1fee]) * A))) / 100f))
E = cvtt_i32(f32(f32(f32(i32(u32((100 + target[0x1fef]) * defenderBase))) / 100f)
                  * evasionMultiplier))
```

`0x1fee/0x1fef` are virtual accuracy/evasion final modifiers from effective level.
No new stat evaluation or generic dependency publication is implied by identifying these reads.
The next helper zero-extends the resulting **u32 bit patterns**, so negative/wrapped intermediate integers
must not silently be clamped to zero before that call.

## 4. Base hit chance and native power

`0x1424ff510` returns exactly `0.75f` if both u32 inputs are zero.
Otherwise, every operation below is rounded to f32 at its indicated step:

```text
a = f32(u32(A))
e = f32(u32(E))
x = f32(e / 5.0f)
p = NativePowF(x, f32(0.9))
h = f32(f32(a / f32(a + p)) * 1.25f)
baseHit = clamp(h, f32(0.05), 1.0f)
```

The literals are original data, not a wiki-version substitution:
divisor 5 at `0x1436a3614`, exponent bits `0x3f666666` at `0x1436a3130`,
and multiplier 1.25 at `0x1436a3258`.
The exponent is approximately `0.8999999761581421`, not a double-precision decimal 0.9.
For example A=1/E=5 gives 0.625, A=3/E=5 gives 0.9375, and A=4/E=5 reaches the upper clamp.

`0x1403b4950` tail-jumps to native power body `0x142d79cb0`.
Dispatch field `0x14458157c` selects ordinary and AVX/FMA branches, as in the already researched trig family.
Those branches use distinct table/polynomial/log-exp sequences, packed double intermediates and a final f32 result;
the near-one AVX path also rejoins the ordinary common exponentiation suffix.
For this caller x is zero or a finite positive f32 from a u32 divided by five, and exponent is fixed positive f32 0.9.
Negative-base, arbitrary-exponent, NaN, infinity and error-callback behavior are outside this bounded call contract.
CPU dispatch and nearest-even/gradual-underflow arithmetic are explicit offline inputs, not observed runtime settings.

Retained code and indexed data reads support original-instruction interpretation of these relevant paths.
In the tested domain both dispatches and the host-double-power-then-f32 candidate agree bitwise:
1416 base controls and 1026 direct power controls, including zero, low integer E, signed-edge u32 values and random E.
Sixteen additional powers match 80-digit Decimal expectations through both branches.
This is not a proof that every library `Pow`/`MathF.Pow` or every floating-point environment is interchangeable.
Keep the original f32 inputs/order and finite-domain qualification when using this as port evidence.

## 5. Final evasion expression and entropy handoff

Let `P(k)=f32(f32(i32(u32(100+target[k])))/100f)`.
Using f32 after every arithmetic operation, in this exact order:

```text
evade = 1f - min(baseHit, 1f)
evade += f32(target[virtual_chance_to_evade_attacks_% 0x1d3e]) / 100f
evade *= f32(i32(u32(100 + target[chance_to_evade_attacks_+%_final 0x29fb])))
evade /= 100f
evade *= projectile ? P(chance_to_evade_projectile_attacks_+%_final 0x29fc) : 1f
evade *= melee      ? P(chance_to_evade_melee_attacks_+%_final 0x29fd) : 1f
hit = clamp(1f - min(evade, 1f), f32(0.05), 1f)
```

The generic final percentage is multiplied **before** dividing by 100; do not regroup it into a predivided factor.
Base and final clamps are separate, so complements and intermediate rounding remain observable.
For base 0.9375, virtual evade 25 gives final 0.6875; generic final evade +100 gives 0.875.
With both classifications active, projectile and melee +100 together give 0.75;
a true caller-projectile flag disables that melee contribution and gives 0.875.

At `0x141ebcf54`, callable `+0x10` receives pointers to hit f32 and the final A/E integers.
The selected entropy thunk uses the probability; its accumulator threshold truncates `f32(hit*100f)`.
The builder propagates the callback bool, invokes callable disposal and clears the callable owner pointer.
Five offline cases connect the complete builder through the actual entropy thunk/body and verify resulting
target accumulator/timer, rather than testing only detached formulas.

## 6. Evidence and remaining boundaries

Round32 `chance-audit.json` retains ten functions/leaves / ten ranges / 1386 instructions, two callable bindings,
constants and 32 exact stat names from a hash-pinned extracted table.
`chance-paths.json` records 2958 controls / 484145 interpreted steps / 832 visited instruction sites;
the full retained power body includes error branches outside this positive-input scope.
Stat/component getters and callable disposal are disclosed fixtures; Buff-list predicates, wrapped distance,
rounding, classification, arithmetic, native power and selected entropy instructions execute in the offline interpreter.
The shared R28 register/memory substrate is pinned; this is not native execution or real traffic.

`chance-review.json` freshly rereads all ranges, bindings and 594 observed binary-data reads, checks 37 additional
anchors/four raw constants and verifies thirteen literal rational paths plus sixteen 80-digit Decimal powers.
Those paths reuse the disclosed interpreter, not a second native execution engine.
Peer fixed R16 `becc6dc40ce292bce7b6d0425540a7892567c6ee` has 59 exact Git sources reverified;
its accepted-result listeners/Buff capacity remain separate and are not reimplemented by this investigation.

This closes the selected stable-input attack probability expression and its finite power interface.
Remaining obligations concern actual selected input publication/cache timing, target/provider/world order,
entropy correction routes, other classes/callers and official C/S numerical/result policy.
Full rendering and general-purpose math-library reconstruction are outside this contract.
No production server, client files, running process, shared REA session, CI deployment or remote ref was changed.
