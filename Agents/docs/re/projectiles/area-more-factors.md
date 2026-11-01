# Ordered more-area factors and qualification (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md) and the [area-radius contract](area-radius.md).
The target is the pinned client executable, SHA256
`78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5`.
These are original-byte and offline instruction-interpreter findings, not native execution or game recordings.

## 1. Boundary and arithmetic

Registrar `0x1400baae0` binds `1670 skill_area_of_effect_+%_final` to callback `0x142747740`.
It supplies the more-area input to aggregate `1671`; the aggregate and subsequent integer-root radius
conversion remain separate rounding boundaries. This callback does not itself compute a radius.

Let `S[k]` be a stable signed 32-bit stat input and `w(x)` wrap to signed 32 bits.
Let `f(x)` round each operation to float32, using the default nearest/ties-even environment.
For an admitted contribution `c`, the factor is:

```text
F(c) = max(0f, f(f(w(c+100))/100f))
```

An ineligible conditional term contributes factor `1f`, rather than consulting its modifier value.
Each `c` below is already an integer contribution; products wrap **before** adding 100 and converting.
No factor is exponentiated per charge, per totem or per duration unit.
Negative counts are not separately clamped by these selected multiplication sites.
This machine behavior does not establish whether a particular signed input is reachable in the official game.

The final `MULSS` sequence is `0x1427492df..0x14274935a`:

```text
p = f(F1*100f)
for i = 2..24: p = f(p*Fi)
v = f(p-100f)
result = CVTT32(f(v+(v<0 ? -0.5f : +0.5f)))
```

The sign-dependent half implements the checked finite rounding; it is not banker's rounding.
There is no separate final positive saturation or final nonnegative clamp in this callback.
Invalid/out-of-range conversion follows the selected x64 `CVTTSS2SI` boundary, not a saturating cast.
Different MXCSR settings and exceptional floating-point environments are outside the finite contract.

## 2. Exact factor order

Rows are in multiplication order, which differs from construction and registration order.
Unqualified rows consume their supplied stat without an additional skill-tag or support-compatibility test here.
Names identify the data inputs; they do not authorize adding conditions absent from the body.

| Order | Stat key and Id | Integer contribution / qualification |
|---:|---|---|
| 1 | `ace support_concentrated_effect_skill_area_of_effect_+%_final` | `S[ace]` |
| 2 | `f6c active_skill_area_of_effect_+%_final` | `S[f6c]` |
| 3 | `1b49 support_spell_cascade_area_of_effect_+%_final` | `S[1b49]` |
| 4 | `23cc support_spell_boost_area_of_effect_+%_final` | `S[23cc]` |
| 5 | `24ef bladestorm_area_of_effect_+%_final_while_in_sandstorm` | `S[24ef]` if `S[24f2]!=0` |
| 6 | `2538 blood_sand_stance_melee_skills_area_of_effect_+%_final` | `S[2538]` if `S[1a6b]!=0` |
| 7 | `2640 support_pulverise_area_of_effect_+%_final` | `S[2640]` |
| 8 | `2ec7 support_brand_area_of_effect_+%_final` | `S[2ec7]` |
| 9 | `2ee2 active_skill_area_of_effect_+%_final_per_endurance_charge` | `w(S[2ee2]*S[5])` |
| 10 | `2fd3 skill_area_of_effect_+%_final_in_sand_stance` | `S[2fd3]` if `S[24f5]!=0` |
| 11 | `317a skill_area_of_effect_+%_final_from_skill_specific_stat` | `S[317a]` |
| 12 | `2ab9 left_ring_slot_nova_spells_area_of_effect_+%_final` | `S[2ab9]` if `S[1b6]!=0 && S[2a6c]!=0` |
| 13 | `2aba right_ring_slot_nova_spells_area_of_effect_+%_final` | `S[2aba]` under the same nova-spell condition |
| 14 | `2abb ring_3_slot_nova_spells_area_of_effect_+%_final` | `S[2abb]` under the same nova-spell condition |
| 15 | `4c47 active_skill_area_of_effect_+%_final_per_100ms_duration` | `w(S[4c47]*truncTowardZero(S[168]/100))` |
| 16 | `344b precision_granted_area_of_effect_+%_final` | `S[344b]` |
| 17 | `37de ultimatum_skill_area_of_effect_+%_final` | `S[37de]` |
| 18 | `4421 cleave_area_of_effect_+%_final` | `S[4421]` |
| 19 | `49d5 support_corrupting_cry_area_of_effect_+%_final` | `S[49d5]` |
| 20 | `5756 consecrated_cry_warcry_area_of_effect_+%_final` | `S[5756]` |
| 21 | `4cc8 area_of_effect_+%_final_from_azmeri_dust` | `S[4cc8]` |
| 22 | `5183 gladiator_area_of_effect_+%_final_while_wielding_mace` | `S[5183]` if `S[2f67]!=0` |
| 23 | `5399 area_of_effect_+%_final_for_bow_attacks_firing_single_projectile` | `S[5399]` if `S[3]==9 && S[4b4b]==1` |
| 24 | `5a3e stygian_spire_totem_area_of_effect_+%_final_per_maximum_totem` | `w(S[5a3e]*S[3be])` if `S[94a]!=0` |

Qualifier/quantity inputs are `24f2 affected_by_sandstorm`, `1a6b skill_is_melee_skill`,
`24f5 in_sand_stance`, `1b6 casting_spell`, `2a6c skill_is_nova_skill`,
`2f67 is_wielding_mace`, `3 main_hand_weapon_type`, `4b4b virtual_total_number_of_projectiles`,
`94a skill_is_totemified`, `5 current_endurance_charges`, `168 skill_effect_duration`,
and `3be number_of_totems_allowed`.
Boolean gates admit any nonzero signed value; the bow tests require their exact equalities.
The arrows alias `4b4c` cannot substitute for `4b4b`.

The blood/sand melee contribution checks the melee input; it does not add a second sand-stance test.
The three nova-ring factors multiply separately and require both casting and nova inputs.
Pulverise, Cleave and the other unqualified rows do not acquire extra gates from their names.
Whether an item/support assigns those inputs to a skill is a distinct upstream grant/selection contract.

The duration site `0x142748cee..0x142748d0c` uses signed division by 100, implemented by
`0x51eb851f`, high-half multiplication, arithmetic shift and sign correction, **before** multiplying the rate.
With rate 50 and duration 199 the contribution is 50, not 99; duration -99 contributes zero.

## 3. Order is numerically observable

The offline `order-witness.json` uses explicit synthetic inputs with all qualification gates enabled.
Its 24 rate inputs, in the table order, are:

```text
[28,-5,28,-6,19,49,49,40,50,26,2,39,10,21,-5,32,38,33,30,44,7,33,-2,48]
```

Endurance/max-totem quantities are 1 and duration is 100; the remaining gates admit their factors.
Original order returns 17065. Reversing the same factors with float32 rounding returns 17064.
This is a constructed arithmetic witness, not an official build, observed game discrepancy or occurrence rate.
It shows why a dictionary/product reduction with unspecified order is insufficient for exact reconstruction.

## 4. Verification and remaining inputs

Round25 `more_area_model.py` expresses the independent formula; `check_more_area.py` compares it with
the existing original-instruction interpreter rebound to the pinned PE.
The 1,598 cases include 216 individual-factor values, 65 gate cases, 17 duration cases,
147 signed/wrapped product cases and 1,152 mixed/extreme synthetic cases.
All 38,352 intermediate multiplication prefixes match bit-for-bit, as do final signed results.
Comparing intermediate values also avoids hiding an ordering error behind final conversion overflow.

`review_more_area.py/json` independently rechecks the selected callback and max leaf:
1,730 decoded instructions, all 24 multiplication operands, seventeen semantic byte anchors,
22 literal expected controls and the order witness.
This review shares the underlying instruction interpreter; it is not a second native implementation.
The checker's frozen files remain unchanged during the review.
The exercised input union is 36 keys; the registration's 31 dependencies remain a distinct inventory.

Inputs are stable/cache-ready synthetic values. Slow provider traversal, invalidation/activation,
actual modifier grant eligibility and final network publication are not established by these comparisons.
The separate increased-area callback `d7` is covered by the [complete 34-term expression](area-increased-terms.md).
Action-specific intensity/repeat/donor contributions and supplied final overrides retain the lifetimes
documented in the parent area chapter; this formula must not blindly replace them or double-apply a final value.
