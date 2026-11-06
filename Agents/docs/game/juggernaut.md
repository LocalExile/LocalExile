# Juggernaut

Every ordinary node is applied through its stats' sum, so another source of the same stat works the same way.
Numbers come from the wiki text; the client is checked where the two could differ.
All 14 ordinary nodes passed native acceptance in two builds; the checklist is in the pull request.

## Nodes

| Node | Stats (value) | Consumer |
| --- | --- | --- |
| Six small nodes | `physical_damage_reduction_rating_+%` 15 | Armour pools |
| Juggernaut1, 2 | `endurance_charge_duration_+%` 15 | `Charges.LimitsOf` |
| Juggernaut3 | `attack_speed_+%` 5 | `AttackSpeedPct` |
| Juggernaut4 | `base_movement_velocity_+%` 4 | `MovementVelocityPct` |
| Juggernaut5 | `base_stun_duration_+%` 20 | `HitReaction` |
| Juggernaut6 | `base_chaos_damage_resistance_%` 11 | Chaos resistance |
| Juggernaut14 | `life_regeneration_rate_per_minute_%` 30 | `PanelStats.LifePerMinute` |
| Unstoppable | action and movement floors, `base_movement_velocity_+%` 10, `base_cannot_be_stunned` | Chill and freeze floors, `PlayerHitRecovery` |
| Unbreakable | `damage_reduction_rating_from_body_armour_doubled`, `base_armour_%_applies_to_chaos_damage` 15 | `DoubleBodyArmour`, `PlayerHitMitigation` |
| Unyielding | `damage_+%_per_endurance_charge` 8, `area_of_effect_+%_per_endurance_charge` 6, `stun_duration_+%_per_endurance_charge` 10 | `ChargeEffects`, `HitReaction` |
| Unyielding | `chance_to_gain_endurance_charge_when_you_stun_enemy_%` 25 | `ChargeEvent.Stun` |
| Undeniable | `accuracy_rating` 500, `gain_accuracy_rating_equal_to_2_times_strength`, `attack_speed_+%_per_200_accuracy_rating` 1 | `CharacterStats` |
| Unflinching | when hit 30%, `max_endurance_charges` 1, to maximum 25%, one per second if hit recently | `ChargeGains` |
| Unrelenting | `physical_` and `elemental_damage_reduction_%_per_endurance_charge` 1 | `ChargeEffects` |
| Untiring | `gain_%_total_phys_damage_prevented_in_the_past_10_sec_as_life_regen_per_sec` 15, `life_regeneration_rate_+%` 40 | `PreventedDamageRecovery` |

A blocked hit counts as hit for when-hit gains and "hit recently" (wiki Blocking, fixed in 3.11.1c).
"Hit recently" ends at death.

## Stun

Chance: 200 x stunning damage / (maximum life x (1 - reduction)); 20% or less is ignored.
A reduction R above 75 becomes `75 + 25 x (R - 75) / (R - 50)`.
Duration: 350 ms x (100 + attacker's increases) / (100 + defender's recovery).
A boss cannot be stunned while stunned or for 4 s after.
A critical strike that deals cold damage cannot stun.

The wiki leaves the non-melee factor unknown; `Metadata/Characters/Character.ot` gives all of it:

| Stat | Value |
| --- | --- |
| `melee_hit_damage_stun_multiplier_+%` | 25 |
| `non_physical_hit_damage_stun_multiplier_+%` | -25 |
| `non_melee_hit_damage_stun_multiplier_+%_final` | -25 |
| `object_inherent_melee_hit_stun_duration_+%_final` | 50 (`Monster.ot`: 20) |

So a hit's stunning damage is its damage x 1.25 for melee physical, 1 for melee non-physical, 0.75 for non-melee
physical and 0.5625 for non-melee non-physical.
The client computes none of this: the keys appear only in two unrelated functions, so the formula is the server's.

## Undeniable

The stat's id says 200, but its text says "per 150 Accuracy Rating" and so does the client.
Main hand `0x142701d20` and off hand `0x142703500`: `floor(accuracy / 150) x value`, added to increased attack speed.
Accuracy (`0x1426f9f80`): `strength x (flag 0x3496 ? 200 : 0 + flag 0x1de7 ? 100 : 0) / 100` joins the flat sum.

## Untiring

The value is per mille: 15 is 1.5% (the description's `deciseconds_to_seconds`).
`BuffTemplates[27]` is `JuggernautPhysDamagePreventedAsLifeRegen`, whose `BuffDefinitions[1086]` is invisible and has no
stats, so the client gets only the life updates.
Prevented is the raw hit minus what it finally dealt (wiki Prevented damage).
Armour, reduction, Fortification, spell suppression, avoidance, immunity and what a block keeps out count.
Evasion, ward, energy shield and damage taken as another type do not.
PoB scales its pseudo recoup by Life regeneration and recovery increases; the server does too.

## Not applied

- A stun during a running recovery grants on-stun gains but neither restarts nor extends the recovery.
- Minions and corpse explosions use plain damage and the 0.35 s base, and do not count as "you stun".
- A critical strike's cold rule has no exception for attackers whose critical strikes cannot inherently freeze.
- Only the main hand's local threshold reduction counts, and the off hand's attack speed uses the main accuracy.
- Prevented damage stays behind on an area change, as Life Recoup does.
- The wiki names Kitava stun-immune; no gate exists unless the monster's mods say so.
- Other on-stun gains stay unrolled: `chance_to_gain_power_charge_on_stun_%`, `chance_to_gain_frenzy_charge_on_stun_%`
  and the melee-stun stats.
