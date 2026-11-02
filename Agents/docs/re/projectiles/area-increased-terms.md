# Complete increased-area expression

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md) and the [area-radius contract](area-radius.md).
Companion: [ordered more-area factors](area-more-factors.md).
Target executable SHA256: `78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5`.
Findings use original bytes and offline instruction interpretation, not native execution or game recordings.

## 1. Boundary and arithmetic

Registrar `0x1400ba7b0` binds `d7 skill_area_of_effect_+%` to callback `0x142745c70`.
Its 34 contributions are summed with signed 32-bit wrapping. This body has no float conversion,
square root, final nonnegative clamp or saturating addition. It supplies increased area to aggregate `1671`;
the more-area product, aggregate rounding and radius conversion remain separate boundaries.

Let `S[k]` denote a stable signed 32-bit stat value, `w(x)` wrap to signed 32 bits,
`trunc` divide toward zero and `minSigned` compare signed values.
The complete expression is `w(sum(the 34 contributions below))`.
Boolean gates accept any nonzero input, including negative synthetic values, unless exact zero or
a signed threshold is specified. Synthetic extremes describe machine arithmetic, not official reachability.

## 2. Twenty conditional additive contributions

An admitted row contributes its supplied modifier value `S[key]`; an ineligible row contributes zero.
These are local callback gates. Item/support grant compatibility is a separate upstream contract.

| Modifier key and Id | Local qualification |
|---|---|
| `9e0 area_of_effect_+%_while_dead` | `S[9df]!=0` |
| `12a1 skill_area_of_effect_+%_if_enemy_killed_recently` | `S[11b3]!=0` |
| `1869 skill_area_of_effect_+%_while_no_frenzy_charges` | `S[6]==0` |
| `1bcd area_of_effect_+%_if_have_stunned_an_enemy_recently` | `S[1bcf]!=0` |
| `1bd8 area_of_effect_+%_while_fortified` | `S[3cd3]!=0` |
| `1c1b area_of_effect_+%_if_you_have_blocked_recently` | `S[1340]!=0 OR S[15fe]!=0` |
| `1e4c area_of_effect_+%_while_you_have_arcane_surge` | `S[1e58]!=0` |
| `3574 area_of_effect_+%_if_culled_recently` | `S[3576]!=0` |
| `34df fanaticism_hand_cast_area_of_effect_+%` | `S[1b6]!=0 AND S[320e]==0` |
| `2bde area_of_effect_+%_if_enemy_stunned_with_two_handed_melee_weapon_recently` | `S[2bbc]!=0` |
| `2654 area_of_effect_+%_while_totem_active` | `S[d85]!=0` |
| `27d3 mine_area_of_effect_+%_if_detonated_mine_recently` | `S[949]!=0 AND S[1df3]!=0` |
| `2bd4 area_of_effect_+%_if_killed_at_least_5_enemies_recently` | signed `S[1f22]>=5` |
| `2f54 skill_area_of_effect_+%_in_sand_stance` | `S[24f5]!=0` |
| `34ba area_of_effect_+%_while_you_do_not_have_convergence` | `S[34bb]==0` |
| `4d47 tinctured_weapon_skill_area_of_effect_+%_if_enemy_killed_recently` | `S[50df]!=0 AND S[11b3]!=0` |
| `511b banner_area_of_effect_+%` | `S[2b5c]!=0` |
| `553a vaal_skills_area_of_effect_+%` | `S[ba6]!=0` |
| `3b28 area_of_effect_+%_if_have_crit_recently` | `S[11b9]!=0` |
| `5146 retaliation_skill_area_of_effect_+%` | `S[fc2]!=0` |

The block test short-circuits: spell-block input `15fe` is queried only if attack-block input `1340` is zero.
Hand-cast, recent-mine and tincture conjunctions also short-circuit their second query.
In particular, `4d47` checks tincture and recent kill; it does not read declared dependency
`4d77 skill_is_weapon_attack`. That absence does not establish whether a granting system can actually
assign this modifier to a nonweapon skill.

## 3. Three unconditional supplied values

| Key and Id | Contribution |
|---|---|
| `1b5 base_skill_area_of_effect_+%` | `S[1b5]` |
| `34f1 stable_skill_area_of_effect_+%` | `S[34f1]` |
| `2942 virtual_cyclone_skill_area_of_effect_+%_from_melee_range` | `S[2942]` |

`2942` is already a computed flat input here; this callback does not repeat a Cyclone/melee-range qualification.
The two bases are added separately; neither replaces the other.

## 4. Eleven quantity contributions and upper caps

| Modifier key and Id | Contribution |
|---|---|
| `5393 area_of_effect_+%_per_10_rage` | `w(S[5393]*trunc(S[517c]/10))` |
| `1d76 area_of_effect_+%_per_25_rampage_stacks` | `w(S[1d76]*trunc(S[94b]/25))` |
| `1bdd area_of_effect_+%_per_endurance_charge` | `w(S[1bdd]*S[5])` |
| `1f14 area_of_effect_+%_per_enemy_killed_recently` | `w(S[1f14]*minSigned(50,S[1f22]))` |
| `20f8 area_of_effect_+%_per_active_herald_of_light_minion` | `w(S[20f8]*minSigned(50,S[2100]))` |
| `22ca area_of_effect_+%_per_frost_fury_stage` | `w(S[22ca]*S[2174])` |
| `1ab skill_area_of_effect_+%_per_power_charge` | `w(S[1ab]*S[7])` |
| `ec5 skill_area_of_effect_+%_per_active_mine` | `w(S[ec5]*S[ed0])` |
| `380a skill_area_of_effect_+%_per_power_charge_up_to_50%` | `minSigned(50,w(S[380a]*S[7]))` |
| `1f15 area_of_effect_+%_per_enemy_killed_recently_up_to_25%` | `minSigned(25,w(S[1f15]*S[1f22]))` |
| `26a2 slayer_area_of_effect_+%_per_enemy_killed_recently_up_to_50%` | `minSigned(50,w(S[26a2]*S[1f22]))` |

The first two divisions happen **before** rate multiplication. For rate 7, rage 19 contributes 7,
rage -19 contributes -7 and rage -9 contributes zero. Rampage uses complete signed groups of 25.
The selected `/10` sequence at `0x142747635..0x142747646` uses `0x66666667`, high-half multiply,
arithmetic shift 2 and sign correction. The `/25` sequence uses `0x51eb851f`, shift 3 and sign correction
at `0x142747648..0x142747664`.

`1f14` and `20f8` cap the **count** at 50 before multiplying. With rate 7 and count 99 they contribute 350.
`380a`, `1f15` and `26a2` cap the **wrapped product** at 50, 25 and 50 respectively.
With rate 7 and count 99 their contributions are 50, 25 and 50.
There is no lower-zero clamp at these selected caps: herald count -99 and rate 7 contribute -693.
For capped power, rate `2147483647` times two wraps to -2, which remains -2 after the upper cap.
The active-mine product has no additional `skill_is_mined` gate in this callback.

The power/kill/Slayer product sites are `0x142747109`, `0x142747144` and `0x142747183`;
their signed cap selection is `0x14274759a..0x1427475e1`.
Herald/recent-kill count selection is `0x1427472a9..0x142747324`, before their multiplication
at `0x142747695` / `0x142747688`.

## 5. Qualifier and quantity identities

| Key and Id |
|---|
| `5 current_endurance_charges` |
| `6 current_frenzy_charges` |
| `7 current_power_charges` |
| `1b6 casting_spell` |
| `949 skill_is_mined` |
| `94b current_rampage_stacks` |
| `9df is_dead` |
| `ba6 skill_is_vaal_skill` |
| `d85 number_of_active_totems` |
| `ed0 number_of_active_mines` |
| `fc2 skill_is_retaliation_skill` |
| `11b3 have_killed_recently` |
| `11b9 have_crit_recently` |
| `1340 have_blocked_attack_recently` |
| `15fe have_blocked_spell_recently` |
| `1bcf have_stunned_an_enemy_recently` |
| `1df3 number_of_mines_detonated_recently` |
| `1e58 has_arcane_surge` |
| `1f22 number_of_enemies_killed_recently` |
| `2100 number_of_active_herald_of_light_minions` |
| `2174 frost_fury_current_number_of_stages` |
| `24f5 in_sand_stance` |
| `2b5c skill_is_banner_skill` |
| `2bbc have_stunned_an_enemy_with_a_two_handed_melee_weapon_recently` |
| `320e is_not_hand_cast` |
| `34bb have_convergence` |
| `3576 have_culled_recently` |
| `3cd3 has_fortification` |
| `50df tincture_applied` |
| `517c effective_rage_for_per_rage_effects` |

The exercised input union is 64 keys. The 54 declared dependencies are not that union:
eleven observed inputs are undeclared (`5,6,7,9df,ed0,fc2,11b3,1f22,3cd3,50df,517c`),
whereas declared `4d77` is not read by the selected body. Dependency metadata alone is insufficient
to reconstruct these conditions or an invalidation contract.

## 6. Verification and scope

Round26 `increased_area_model.py` expresses the 34-term formula independently.
`check_increased_area.py` compares it against freshly decoded original instructions using stable,
cache-ready synthetic inputs: 3,875 cases, all 127,875 intermediate integer addition prefixes and final results match.
The cases include 306 individual-term values, 384 gate combinations, 150 signed-division boundaries,
1,485 product/cap cases, 1,536 mixed/extreme cases and fourteen explicit controls.

The 33 additions at `0x142747678..0x142747724` retain the two original accumulators in the checker.
For already-computed integer terms, reassociation of wrapping additions preserves the final bits;
this does not authorize reordering stateful stat queries or moving a multiplication across a cap/division.

`review_increased_area.py/json` separately rereads the registrar/callback and checks all 33 final addition operands,
gate tests and branch polarity, product/cap/division anchors, 35 hand-computed expected controls
and eight short-circuit controls. It freezes and checks the writer's artifacts.
This review shares the underlying interpreter; it is not an independent native execution engine.

The comparison does not enter slow provider traversal, establish actual modifier grants,
cache activation/invalidation, action-specific extra area or final wire publication.
Use the parent chapter's supplied-final and action-cache lifetimes; do not unconditionally recompute
and double-apply a value already supplied by a server.
