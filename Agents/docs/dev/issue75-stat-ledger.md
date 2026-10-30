# Issue #75: server-stat completion ledger

This is the exact 357-virtual-stat target, separate from the 387 panel rows and their 134 other dependencies.
It tracks review disposition, not just presence in source code. The [review report](issue75-review.md) records
commits, validation receipts and decisions. Historical native checks in [the port record](issue75-port.md)
remain scoped to their original revisions.

## Reading and maintaining this ledger

- `pending`: this continuation has not completed the stat's individual correctness audit. Existing implementation
  or historical evidence may exist; this status does not mean the stat is absent.
- `partial`: the linked review establishes a correction or a concrete remaining gap. It does not certify every
  source, condition, hand/skill context, gameplay consumer or lifecycle.
- `complete`: use only after every applicable requirement is implemented and supported by reviewed evidence,
  including current-branch native presentation/lifecycle checks. No entry is currently certified to this full definition.

Run `python -X utf8 scripts/dev/audit-character-panel.py` from the repository root. It checks exact ledger membership
against the extracted panel dependencies and rejects missing, duplicate or unexpected names. Literal code matches
remain navigation aids. The script reports authored statuses; it never upgrades them based on those matches.
Update the relevant record and rows with each implementation group. Keep source formulas, units/rounding,
qualified source composition, overrides/clearing, equipment/support changes, transitions and actual consumers in
scope. Do not replace this with a percentage based on passing tests or visible rows.

## Penetration

Partial: eleven aliases now compose qualified actor/support/weapon inputs and elemental prohibition. Actual hits
consume the corrected sources. Off-hand execution, conditional sources and native validation remain open.
See [review and tests](issue75-review.md#confirmed-defects-addressed) and [damage](../protocol/20-damage.md).

## Ailment coefficients

Partial: twelve duration/effect aliases now include real Unbound Ailments inputs and clearing. A coefficient alone
does not implement an ailment. Eligibility, application, critical behavior, stacking, conditional modifiers and
native validation remain open; [review](issue75-review.md#confirmed-defects-addressed).

## Impale

Partial: all three selected chance aliases and ordinary/gem contact application now have automated evidence.
Conditional sources, extra application/consumption, strongest/Impaler and native lifecycle remain open;
[review](issue75-review.md#impale-review-and-corrections), [module](../protocol/20-damage.md#3a-impale-contacts-and-reflected-damage).

## Critical

Partial: main-hand/spell chance, unconditional more/less factors, multiplier scope and use-time/shared rolls have
reviewed corrections. Off-hand chance/multiplier publication is a confirmed remaining gap. Other conditions,
ordinary-bow snapshots, repeat exceptions and native parity remain open;
[formula review](issue75-review.md#moreless-critical-chance-correction),
[roll lifetime](issue75-review.md#shared-critical-rolls-within-a-gem-use).

## Accuracy

Partial: raw BASE/increased and actual Additional Accuracy support compose before flooring. Ordinary/gem contacts
use evasion entropy and independent critical confirmation; main-hand normal/evasive aliases include raw cannot-evade.
Off-hand execution/sources, other conditional/more operators and native parity remain open;
[source review](issue75-review.md#accuracy-sources-and-selected-support-inputs),
[projectiles](issue75-review.md#gem-projectile-accuracy-continuation-and-volley-exclusions).

## Damage expectation

Partial: average/DPS now includes critical luck and separate normal-reference landing/confirmation probabilities.
Dual-hand, multi-hit/secondary, integer-roll expectation, cooldown/downtime, DoT, other conditions and native
formatting remain open; [review](issue75-review.md#accuracy-weighted-attack-averages-and-dps).

## Leech coefficients

Partial: 24 scoped coefficient aliases and three speed aliases now compose qualified actor/support/hand sources,
including general/elemental inputs, physical/chaos ES and combined Life/Mana. Real supports and fractional unit
composition have automated controls. Ordinary direct hits now create timed/instant resource recovery with separate
instance duration, total recovery and caps. Ghost Reaver preserves distinct converted/native ES instances and applies
its cap/recharge changes, with actual node/refund controls. Conditional sources, other conversions, weapon-family conditions, skill-specific
recovery speeds and native getter/recovery parity remain open. See [review](issue75-review.md#ordinary-active-leech-recovery)
and [damage module](../protocol/20-damage.md#5b-character-panel-leech-limits). Do not add overlapping display scopes
as independent leech amounts in combat.
Actual leech/full-ES condition flags now drive evidenced hit-damage scopes and Energy Leech's full-ES/quality
bonuses, with qualified support, contact and periodic refresh controls; other condition consumers remain open.
Attack-only ES overleech now retains qualified attack instances under the same total cap, with real item-source,
mixed-instance, expiry and Ghost Reaver controls. Ordinary Life-overleech projection and zero replacement are fixed;
the other conversion/conditional/native requirements remain open.
Claw mastery/Weapon Master instant sources now respect usable active claws, and Life-instant sources follow
Ghost Reaver conversion. Real-source and override controls establish this bounded correction; mixed converted/native
ES composition still requires native evidence ([review](issue75-review.md#instant-leech-sources-and-ghost-reaver-correction)).
Highwayman's actual total-recovery doubling source now reaches the Life consumer once, with real modifier/removal
and explicit-time controls. It remains distinct from maximum-rate doubling and does not certify native stacking.

## Leech limits and prohibitions

Partial: the 15 limit/prohibition aliases share the resolver consumed by ordinary active recovery. Fractional
combat caps remain nonzero when whole display endpoints truncate to zero. Raw/virtual replacements, Low Life
Life/Mana scope, separate amount/rate changes, explicit Life maximum-rate doubling and lifecycle have automated
controls. Other cap conditions, conversions and current native recovery checks remain open;
[review](issue75-review.md#ordinary-active-leech-recovery),
[module](../protocol/20-damage.md#5b-character-panel-leech-limits). Historical native display checks remain scoped to their recorded revision.

## Pending audit

Trace each remaining entry through raw collection, formula, publication and authoritative consumer. Start with
known partial systems in the [remaining work](issue75-review.md#remaining-review-and-implementation) and
[combat roadmap](../../roadmap/skills-and-combat.md). Imported work must be checked in this branch's composition;
historical certificates are not silently carried forward. Missing live resources do not block independent code
and automated checks. Wait for shared resources and stop only this task's own processes when validating natively.

## Exact target

Current disposition: 188 partial, 169 pending, 0 complete. These are audit states, not implemented-stat counts.

| Stat | Status | Review record / next audit |
|---|---|---|
| `armour_differs_against_hits` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `armour_differs_against_projectiles` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `arrow_number_of_targets_to_pierce` | pending | [pending audit](#pending-audit) |
| `ascendance` | pending | [pending audit](#pending-audit) |
| `attack_block_%` | pending | [pending audit](#pending-audit) |
| `attack_speed_+%_per_frenzy_charge` | pending | [pending audit](#pending-audit) |
| `avoid_bleed_%` | pending | [pending audit](#pending-audit) |
| `avoid_chill_%` | pending | [pending audit](#pending-audit) |
| `avoid_freeze_%` | pending | [pending audit](#pending-audit) |
| `avoid_ignite_%` | pending | [pending audit](#pending-audit) |
| `avoid_poison_%` | pending | [pending audit](#pending-audit) |
| `avoid_shock_%` | pending | [pending audit](#pending-audit) |
| `avoid_stun_%` | pending | [pending audit](#pending-audit) |
| `brands_per_target_limit` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `cannot_be_chilled` | pending | [pending audit](#pending-audit) |
| `cannot_be_frozen` | pending | [pending audit](#pending-audit) |
| `cannot_be_ignited` | pending | [pending audit](#pending-audit) |
| `cannot_be_shocked` | pending | [pending audit](#pending-audit) |
| `cannot_be_stunned` | pending | [pending audit](#pending-audit) |
| `cannot_be_stunned_by_attacks` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `cannot_be_stunned_by_spells` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `cannot_gain_bleeding` | pending | [pending audit](#pending-audit) |
| `cannot_leech_energy_shield` | partial | [leech limits and prohibitions](#leech-limits-and-prohibitions) |
| `cannot_leech_life` | partial | [leech limits and prohibitions](#leech-limits-and-prohibitions) |
| `cannot_leech_mana` | partial | [leech limits and prohibitions](#leech-limits-and-prohibitions) |
| `cast_speed_+%` | pending | [pending audit](#pending-audit) |
| `cast_speed_+%_per_frenzy_charge` | pending | [pending audit](#pending-audit) |
| `chance_to_dodge_spells_%` | pending | [pending audit](#pending-audit) |
| `chance_to_evade_%` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `chance_to_freeze_%` | pending | [pending audit](#pending-audit) |
| `chance_to_shock_%` | pending | [pending audit](#pending-audit) |
| `chaos_damage_immunity` | pending | [pending audit](#pending-audit) |
| `chaos_damage_over_time_resistance_%` | pending | [pending audit](#pending-audit) |
| `chaos_damage_resistance_%` | pending | [pending audit](#pending-audit) |
| `character_sheet_display_presence_radius` | pending | [pending audit](#pending-audit) |
| `cold_damage_immunity` | pending | [pending audit](#pending-audit) |
| `cold_damage_resistance_%` | pending | [pending audit](#pending-audit) |
| `combined_bleeding_duration_on_self_+%` | pending | [pending audit](#pending-audit) |
| `combined_poison_duration_on_self_+%` | pending | [pending audit](#pending-audit) |
| `current_endurance_charges` | pending | [pending audit](#pending-audit) |
| `current_frenzy_charges` | pending | [pending audit](#pending-audit) |
| `current_power_charges` | pending | [pending audit](#pending-audit) |
| `deal_no_damage` | pending | [pending audit](#pending-audit) |
| `dexterity` | pending | [pending audit](#pending-audit) |
| `display_attack_block_chance` | pending | [pending audit](#pending-audit) |
| `display_estimated_physical_damage_reduciton_%` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `display_estimated_physical_damage_reduciton_against_projectiles_%` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `display_estimated_physical_damage_reduction_against_hits_%` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `display_hundred_times_damage_per_skill_use` | partial | [damage expectation](#damage-expectation) |
| `display_skill_increased_item_quantity_+%` | pending | [pending audit](#pending-audit) |
| `display_skill_increased_item_rarity_+%` | pending | [pending audit](#pending-audit) |
| `display_spell_block_chance` | pending | [pending audit](#pending-audit) |
| `energy_shield_gained_on_enemy_death` | pending | [pending audit](#pending-audit) |
| `energy_shield_gained_on_spell_hit` | pending | [pending audit](#pending-audit) |
| `energy_shield_recharge_rate_per_minute` | pending | [pending audit](#pending-audit) |
| `evasion_rating` | pending | [pending audit](#pending-audit) |
| `extra_damage_taken_from_crit_+%` | pending | [pending audit](#pending-audit) |
| `fire_damage_immunity` | pending | [pending audit](#pending-audit) |
| `fire_damage_resistance_%` | pending | [pending audit](#pending-audit) |
| `global_chance_to_knockback_%` | pending | [pending audit](#pending-audit) |
| `hundred_times_attacks_per_second` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `hundred_times_casts_per_second` | pending | [pending audit](#pending-audit) |
| `hundred_times_damage_per_second` | partial | [damage expectation](#damage-expectation) |
| `immune_to_chill` | pending | [pending audit](#pending-audit) |
| `immune_to_freeze` | pending | [pending audit](#pending-audit) |
| `immune_to_ignite` | pending | [pending audit](#pending-audit) |
| `immune_to_shock` | pending | [pending audit](#pending-audit) |
| `intelligence` | pending | [pending audit](#pending-audit) |
| `intermediary_combined_action_speed_+%` | pending | [pending audit](#pending-audit) |
| `life_gained_on_enemy_death` | pending | [pending audit](#pending-audit) |
| `life_gained_on_spell_hit` | pending | [pending audit](#pending-audit) |
| `life_leech_from_chaos_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `life_leech_from_cold_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `life_leech_from_fire_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `life_leech_from_lightning_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `life_leech_from_physical_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `life_leech_from_spell_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `life_recharge_rate_per_minute` | pending | [pending audit](#pending-audit) |
| `lightning_damage_immunity` | pending | [pending audit](#pending-audit) |
| `lightning_damage_resistance_%` | pending | [pending audit](#pending-audit) |
| `main_hand_accuracy_rating` | partial | [accuracy](#accuracy) |
| `main_hand_attack_damage_penetrates_chaos_resist_%` | partial | [penetration](#penetration) |
| `main_hand_attack_damage_penetrates_cold_resist_%` | partial | [penetration](#penetration) |
| `main_hand_attack_damage_penetrates_fire_resist_%` | partial | [penetration](#penetration) |
| `main_hand_attack_damage_penetrates_lightning_resist_%` | partial | [penetration](#penetration) |
| `main_hand_attack_speed_+%` | pending | [pending audit](#pending-audit) |
| `main_hand_chance_to_blind_on_hit_%` | pending | [pending audit](#pending-audit) |
| `main_hand_chance_to_hit_%` | partial | [accuracy](#accuracy) |
| `main_hand_chance_to_hit_evasive_monsters_%` | partial | [accuracy](#accuracy) |
| `main_hand_chance_to_knockback_%` | pending | [pending audit](#pending-audit) |
| `main_hand_critical_strike_chance` | partial | [critical](#critical) |
| `main_hand_critical_strike_multiplier` | partial | [critical](#critical) |
| `main_hand_hit_causes_monster_flee_%` | pending | [pending audit](#pending-audit) |
| `main_hand_knockback_on_crit` | pending | [pending audit](#pending-audit) |
| `main_hand_life_leech_from_physical_attack_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `main_hand_local_life_gain_per_target` | pending | [pending audit](#pending-audit) |
| `main_hand_local_mana_gain_per_target` | pending | [pending audit](#pending-audit) |
| `main_hand_mana_leech_from_physical_attack_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `main_hand_maximum_chaos_damage` | pending | [pending audit](#pending-audit) |
| `main_hand_maximum_cold_damage` | pending | [pending audit](#pending-audit) |
| `main_hand_maximum_fire_damage` | pending | [pending audit](#pending-audit) |
| `main_hand_maximum_lightning_damage` | pending | [pending audit](#pending-audit) |
| `main_hand_maximum_physical_damage` | pending | [pending audit](#pending-audit) |
| `main_hand_maximum_total_damage` | pending | [pending audit](#pending-audit) |
| `main_hand_minimum_chaos_damage` | pending | [pending audit](#pending-audit) |
| `main_hand_minimum_cold_damage` | pending | [pending audit](#pending-audit) |
| `main_hand_minimum_fire_damage` | pending | [pending audit](#pending-audit) |
| `main_hand_minimum_lightning_damage` | pending | [pending audit](#pending-audit) |
| `main_hand_minimum_physical_damage` | pending | [pending audit](#pending-audit) |
| `main_hand_minimum_total_damage` | pending | [pending audit](#pending-audit) |
| `main_hand_reduce_enemy_block_%` | pending | [pending audit](#pending-audit) |
| `main_hand_stun_threshold_reduction_+%` | pending | [pending audit](#pending-audit) |
| `mana_gained_on_enemy_death` | pending | [pending audit](#pending-audit) |
| `mana_leech_from_chaos_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `mana_leech_from_cold_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `mana_leech_from_fire_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `mana_leech_from_lightning_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `mana_leech_from_physical_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `mana_leech_from_spell_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `mana_recharge_rate_per_minute` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_bleeding_damage_to_deal_per_minute_from_critical_main_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_bleeding_damage_to_deal_per_minute_from_critical_off_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_bleeding_damage_to_deal_per_minute_from_non_critical_main_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_bleeding_damage_to_deal_per_minute_from_non_critical_off_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_energy_shield` | pending | [pending audit](#pending-audit) |
| `maximum_energy_shield_leech_amount_per_leech` | partial | [leech limits and prohibitions](#leech-limits-and-prohibitions) |
| `maximum_ignite_damage_to_deal_per_minute_from_critical_main_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_ignite_damage_to_deal_per_minute_from_critical_off_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_ignite_damage_to_deal_per_minute_from_critical_secondary_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_ignite_damage_to_deal_per_minute_from_critical_spell_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_ignite_damage_to_deal_per_minute_from_non_critical_main_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_ignite_damage_to_deal_per_minute_from_non_critical_off_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_ignite_damage_to_deal_per_minute_from_non_critical_secondary_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_ignite_damage_to_deal_per_minute_from_non_critical_spell_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_life` | pending | [pending audit](#pending-audit) |
| `maximum_life_leech_amount_per_leech` | partial | [leech limits and prohibitions](#leech-limits-and-prohibitions) |
| `maximum_mana` | pending | [pending audit](#pending-audit) |
| `maximum_mana_leech_amount_per_leech` | partial | [leech limits and prohibitions](#leech-limits-and-prohibitions) |
| `maximum_poison_damage_to_always_deal_per_minute_from_critical_main_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_poison_damage_to_always_deal_per_minute_from_critical_off_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_poison_damage_to_always_deal_per_minute_from_critical_secondary_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_poison_damage_to_always_deal_per_minute_from_critical_spell_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_poison_damage_to_always_deal_per_minute_from_non_critical_main_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_poison_damage_to_always_deal_per_minute_from_non_critical_off_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_poison_damage_to_always_deal_per_minute_from_non_critical_secondary_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_poison_damage_to_always_deal_per_minute_from_non_critical_spell_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_poison_damage_to_deal_per_minute_from_critical_main_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_poison_damage_to_deal_per_minute_from_critical_off_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_poison_damage_to_deal_per_minute_from_critical_secondary_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_poison_damage_to_deal_per_minute_from_critical_spell_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_poison_damage_to_deal_per_minute_from_non_critical_main_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_poison_damage_to_deal_per_minute_from_non_critical_off_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_poison_damage_to_deal_per_minute_from_non_critical_secondary_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_poison_damage_to_deal_per_minute_from_non_critical_spell_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `maximum_total_recovery_per_minute_from_energy_shield_leech` | partial | [leech limits and prohibitions](#leech-limits-and-prohibitions) |
| `maximum_total_recovery_per_minute_from_life_leech` | partial | [leech limits and prohibitions](#leech-limits-and-prohibitions) |
| `maximum_total_recovery_per_minute_from_mana_leech` | partial | [leech limits and prohibitions](#leech-limits-and-prohibitions) |
| `minimum_bleeding_damage_to_deal_per_minute_from_critical_main_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_bleeding_damage_to_deal_per_minute_from_critical_off_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_bleeding_damage_to_deal_per_minute_from_non_critical_main_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_bleeding_damage_to_deal_per_minute_from_non_critical_off_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_ignite_damage_to_deal_per_minute_from_critical_main_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_ignite_damage_to_deal_per_minute_from_critical_off_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_ignite_damage_to_deal_per_minute_from_critical_secondary_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_ignite_damage_to_deal_per_minute_from_critical_spell_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_ignite_damage_to_deal_per_minute_from_non_critical_main_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_ignite_damage_to_deal_per_minute_from_non_critical_off_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_ignite_damage_to_deal_per_minute_from_non_critical_secondary_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_ignite_damage_to_deal_per_minute_from_non_critical_spell_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_poison_damage_to_always_deal_per_minute_from_critical_main_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_poison_damage_to_always_deal_per_minute_from_critical_off_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_poison_damage_to_always_deal_per_minute_from_critical_secondary_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_poison_damage_to_always_deal_per_minute_from_critical_spell_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_poison_damage_to_always_deal_per_minute_from_non_critical_main_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_poison_damage_to_always_deal_per_minute_from_non_critical_off_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_poison_damage_to_always_deal_per_minute_from_non_critical_secondary_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_poison_damage_to_always_deal_per_minute_from_non_critical_spell_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_poison_damage_to_deal_per_minute_from_critical_main_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_poison_damage_to_deal_per_minute_from_critical_off_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_poison_damage_to_deal_per_minute_from_critical_secondary_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_poison_damage_to_deal_per_minute_from_critical_spell_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_poison_damage_to_deal_per_minute_from_non_critical_main_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_poison_damage_to_deal_per_minute_from_non_critical_off_hand_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_poison_damage_to_deal_per_minute_from_non_critical_secondary_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `minimum_poison_damage_to_deal_per_minute_from_non_critical_spell_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `movement_velocity_+%` | pending | [pending audit](#pending-audit) |
| `off_hand_accuracy_rating` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `off_hand_attack_damage_penetrates_chaos_resist_%` | partial | [penetration](#penetration) |
| `off_hand_attack_damage_penetrates_cold_resist_%` | partial | [penetration](#penetration) |
| `off_hand_attack_damage_penetrates_fire_resist_%` | partial | [penetration](#penetration) |
| `off_hand_attack_damage_penetrates_lightning_resist_%` | partial | [penetration](#penetration) |
| `off_hand_attack_speed_+%` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `off_hand_chance_to_blind_on_hit_%` | pending | [pending audit](#pending-audit) |
| `off_hand_chance_to_hit_%` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `off_hand_chance_to_hit_evasive_monsters_%` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `off_hand_chance_to_knockback_%` | pending | [pending audit](#pending-audit) |
| `off_hand_critical_strike_chance` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `off_hand_critical_strike_multiplier` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `off_hand_hit_causes_monster_flee_%` | pending | [pending audit](#pending-audit) |
| `off_hand_knockback_on_crit` | pending | [pending audit](#pending-audit) |
| `off_hand_life_leech_from_physical_attack_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `off_hand_local_life_gain_per_target` | pending | [pending audit](#pending-audit) |
| `off_hand_local_mana_gain_per_target` | pending | [pending audit](#pending-audit) |
| `off_hand_mana_leech_from_physical_attack_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `off_hand_maximum_chaos_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `off_hand_maximum_cold_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `off_hand_maximum_fire_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `off_hand_maximum_lightning_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `off_hand_maximum_physical_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `off_hand_maximum_total_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `off_hand_minimum_chaos_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `off_hand_minimum_cold_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `off_hand_minimum_fire_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `off_hand_minimum_lightning_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `off_hand_minimum_physical_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `off_hand_minimum_total_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `off_hand_reduce_enemy_block_%` | pending | [pending audit](#pending-audit) |
| `off_hand_stun_threshold_reduction_+%` | pending | [pending audit](#pending-audit) |
| `omniscience_for_attribute_requirements` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `physical_damage_reduction_rating` | pending | [pending audit](#pending-audit) |
| `physical_damage_reduction_rating_against_projectiles` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `projectile_attack_block_%` | pending | [pending audit](#pending-audit) |
| `projectile_number_of_targets_to_pierce` | pending | [pending audit](#pending-audit) |
| `projectile_speed_+%` | pending | [pending audit](#pending-audit) |
| `quiet_might_keystone_spell_damage_+%_final_per_power_charge` | pending | [pending audit](#pending-audit) |
| `reduce_enemy_cold_resistance_%` | partial | [penetration](#penetration) |
| `reduce_enemy_fire_resistance_%` | partial | [penetration](#penetration) |
| `reduce_enemy_lightning_resistance_%` | partial | [penetration](#penetration) |
| `secondary_critical_strike_chance` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `secondary_critical_strike_multiplier` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `secondary_maximum_chaos_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `secondary_maximum_cold_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `secondary_maximum_fire_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `secondary_maximum_lightning_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `secondary_maximum_physical_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `secondary_maximum_total_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `secondary_minimum_chaos_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `secondary_minimum_cold_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `secondary_minimum_fire_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `secondary_minimum_lightning_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `secondary_minimum_physical_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `secondary_minimum_total_damage` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `self_chill_duration_-%` | pending | [pending audit](#pending-audit) |
| `self_freeze_duration_-%` | pending | [pending audit](#pending-audit) |
| `self_ignite_duration_-%` | pending | [pending audit](#pending-audit) |
| `self_shock_duration_-%` | pending | [pending audit](#pending-audit) |
| `show_blood_charge_info` | partial | [blood charges](issue75-review.md#blood-charges) |
| `skill_area_of_effect_+%` | pending | [pending audit](#pending-audit) |
| `skill_is_instant` | pending | [pending audit](#pending-audit) |
| `skill_show_average_damage_instead_of_dps` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `spell_block_%` | pending | [pending audit](#pending-audit) |
| `spell_block_differs_against_projectiles` | pending | [pending audit](#pending-audit) |
| `spell_critical_strike_chance` | partial | [critical](#critical) |
| `spell_critical_strike_multiplier` | partial | [critical](#critical) |
| `spell_damage_%_suppressed` | pending | [pending audit](#pending-audit) |
| `spell_effective_total_chance_permyriad_for_hit_to_be_critical` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `spell_effective_total_chance_permyriad_for_hit_to_be_double_critical` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `spell_maximum_chaos_damage` | pending | [pending audit](#pending-audit) |
| `spell_maximum_cold_damage` | pending | [pending audit](#pending-audit) |
| `spell_maximum_fire_damage` | pending | [pending audit](#pending-audit) |
| `spell_maximum_lightning_damage` | pending | [pending audit](#pending-audit) |
| `spell_maximum_physical_damage` | pending | [pending audit](#pending-audit) |
| `spell_maximum_total_damage` | pending | [pending audit](#pending-audit) |
| `spell_minimum_chaos_damage` | pending | [pending audit](#pending-audit) |
| `spell_minimum_cold_damage` | pending | [pending audit](#pending-audit) |
| `spell_minimum_fire_damage` | pending | [pending audit](#pending-audit) |
| `spell_minimum_lightning_damage` | pending | [pending audit](#pending-audit) |
| `spell_minimum_physical_damage` | pending | [pending audit](#pending-audit) |
| `spell_minimum_total_damage` | pending | [pending audit](#pending-audit) |
| `spell_projectile_block_%` | pending | [pending audit](#pending-audit) |
| `spell_suppression_chance_%` | pending | [pending audit](#pending-audit) |
| `strength` | pending | [pending audit](#pending-audit) |
| `stun_duration_+%` | pending | [pending audit](#pending-audit) |
| `stun_recovery_+%` | pending | [pending audit](#pending-audit) |
| `total_energy_shield_recovery_per_minute_from_regeneration` | pending | [pending audit](#pending-audit) |
| `total_life_recovery_per_minute_from_regeneration` | pending | [pending audit](#pending-audit) |
| `total_mana_recovery_per_minute_from_regeneration` | pending | [pending audit](#pending-audit) |
| `total_number_of_arrows_to_fire` | pending | [pending audit](#pending-audit) |
| `total_number_of_projectiles_to_fire` | pending | [pending audit](#pending-audit) |
| `uncapped_attack_block_%` | pending | [pending audit](#pending-audit) |
| `uncapped_chaos_damage_over_time_resistance_%` | pending | [pending audit](#pending-audit) |
| `uncapped_chaos_damage_resistance_%` | pending | [pending audit](#pending-audit) |
| `uncapped_cold_damage_resistance_%` | pending | [pending audit](#pending-audit) |
| `uncapped_fire_damage_resistance_%` | pending | [pending audit](#pending-audit) |
| `uncapped_lightning_damage_resistance_%` | pending | [pending audit](#pending-audit) |
| `virtual_block_recovery_+%` | pending | [pending audit](#pending-audit) |
| `virtual_cannot_be_poisoned` | pending | [pending audit](#pending-audit) |
| `virtual_chance_to_blind_on_hit_%` | pending | [pending audit](#pending-audit) |
| `virtual_chill_duration_+%` | partial | [ailment coefficients](#ailment-coefficients) |
| `virtual_chill_effect_+%` | partial | [ailment coefficients](#ailment-coefficients) |
| `virtual_cooldown_speed_+%` | pending | [pending audit](#pending-audit) |
| `virtual_curse_effect_+%` | pending | [pending audit](#pending-audit) |
| `virtual_endurance_charge_duration_+%` | pending | [pending audit](#pending-audit) |
| `virtual_energy_shield_delay_-%` | pending | [pending audit](#pending-audit) |
| `virtual_energy_shield_gain_per_target` | pending | [pending audit](#pending-audit) |
| `virtual_energy_shield_leech_from_chaos_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `virtual_energy_shield_leech_from_cold_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `virtual_energy_shield_leech_from_fire_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `virtual_energy_shield_leech_from_lightning_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `virtual_energy_shield_leech_from_physical_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `virtual_energy_shield_leech_from_spell_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `virtual_energy_shield_leech_speed_+%` | partial | [leech coefficients](#leech-coefficients) |
| `virtual_flask_duration_+%` | pending | [pending audit](#pending-audit) |
| `virtual_freeze_duration_+%` | partial | [ailment coefficients](#ailment-coefficients) |
| `virtual_frenzy_charge_duration_+%` | pending | [pending audit](#pending-audit) |
| `virtual_frostburn_duration_+%` | partial | [ailment coefficients](#ailment-coefficients) |
| `virtual_frostburn_effect_+%` | partial | [ailment coefficients](#ailment-coefficients) |
| `virtual_ignite_duration_+%` | partial | [ailment coefficients](#ailment-coefficients) |
| `virtual_immune_to_bleeding` | pending | [pending audit](#pending-audit) |
| `virtual_immune_to_poison` | pending | [pending audit](#pending-audit) |
| `virtual_knockback_distance_+%` | pending | [pending audit](#pending-audit) |
| `virtual_life_leech_from_attack_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `virtual_life_leech_speed_+%` | partial | [leech coefficients](#leech-coefficients) |
| `virtual_mana_leech_from_attack_damage_permyriad` | partial | [leech coefficients](#leech-coefficients) |
| `virtual_mana_leech_speed_+%` | partial | [leech coefficients](#leech-coefficients) |
| `virtual_maximum_absorption_charges` | partial | [converted charges](issue75-review.md#converted-special-charges) |
| `virtual_maximum_affliction_charges` | partial | [converted charges](issue75-review.md#converted-special-charges) |
| `virtual_maximum_brutal_charges` | partial | [converted charges](issue75-review.md#converted-special-charges) |
| `virtual_maximum_endurance_charges` | pending | [pending audit](#pending-audit) |
| `virtual_maximum_energy_shield_leech_amount_per_leech_permyriad_max_energy_shield` | partial | [leech limits and prohibitions](#leech-limits-and-prohibitions) |
| `virtual_maximum_energy_shield_leech_rate_permyriad_per_minute` | partial | [leech limits and prohibitions](#leech-limits-and-prohibitions) |
| `virtual_maximum_frenzy_charges` | pending | [pending audit](#pending-audit) |
| `virtual_maximum_life_leech_amount_per_leech_permyriad_max_life` | partial | [leech limits and prohibitions](#leech-limits-and-prohibitions) |
| `virtual_maximum_life_leech_rate_permyriad_per_minute` | partial | [leech limits and prohibitions](#leech-limits-and-prohibitions) |
| `virtual_maximum_mana_leech_amount_per_leech_permyriad_max_mana` | partial | [leech limits and prohibitions](#leech-limits-and-prohibitions) |
| `virtual_maximum_mana_leech_rate_permyriad_per_minute` | partial | [leech limits and prohibitions](#leech-limits-and-prohibitions) |
| `virtual_maximum_power_charges` | pending | [pending audit](#pending-audit) |
| `virtual_maximum_siphoning_charges` | pending | [pending audit](#pending-audit) |
| `virtual_maximum_spirit_charges` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `virtual_mine_throwing_speed_+%` | pending | [pending audit](#pending-audit) |
| `virtual_minion_damage_+%` | pending | [pending audit](#pending-audit) |
| `virtual_never_freeze` | pending | [pending audit](#pending-audit) |
| `virtual_never_ignite` | pending | [pending audit](#pending-audit) |
| `virtual_physical_damage_taken_per_minute_per_siphoning_charge` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
| `virtual_power_charge_duration_+%` | pending | [pending audit](#pending-audit) |
| `virtual_sap_duration_+%` | partial | [ailment coefficients](#ailment-coefficients) |
| `virtual_sap_effect_+%` | partial | [ailment coefficients](#ailment-coefficients) |
| `virtual_scorch_duration_+%` | partial | [ailment coefficients](#ailment-coefficients) |
| `virtual_scorch_effect_+%` | partial | [ailment coefficients](#ailment-coefficients) |
| `virtual_shock_duration_+%` | partial | [ailment coefficients](#ailment-coefficients) |
| `virtual_shock_effect_+%` | partial | [ailment coefficients](#ailment-coefficients) |
| `virtual_total_main_hand_chance_to_bleed_on_hit_%` | pending | [pending audit](#pending-audit) |
| `virtual_total_main_hand_chance_to_ignite_on_hit_%` | pending | [pending audit](#pending-audit) |
| `virtual_total_main_hand_chance_to_poison_on_hit_%` | pending | [pending audit](#pending-audit) |
| `virtual_total_main_hand_impale_on_hit_%_chance` | partial | [impale](#impale) |
| `virtual_total_off_hand_chance_to_bleed_on_hit_%` | pending | [pending audit](#pending-audit) |
| `virtual_total_off_hand_chance_to_ignite_on_hit_%` | pending | [pending audit](#pending-audit) |
| `virtual_total_off_hand_chance_to_poison_on_hit_%` | pending | [pending audit](#pending-audit) |
| `virtual_total_off_hand_impale_on_hit_%_chance` | partial | [impale](#impale) |
| `virtual_total_secondary_chance_to_ignite_on_hit_%` | pending | [pending audit](#pending-audit) |
| `virtual_total_secondary_chance_to_poison_on_hit_%` | pending | [pending audit](#pending-audit) |
| `virtual_total_spell_chance_to_ignite_on_hit_%` | pending | [pending audit](#pending-audit) |
| `virtual_total_spell_chance_to_poison_on_hit_%` | pending | [pending audit](#pending-audit) |
| `virtual_total_spell_impale_on_hit_%_chance` | partial | [impale](#impale) |
| `virtual_trap_throwing_speed_+%` | pending | [pending audit](#pending-audit) |
| `ward` | pending | [pending audit](#pending-audit) |
| `ward_delay` | partial | [row batch](issue75-review.md#offence-defence-misc-and-charges-row-batch) |
