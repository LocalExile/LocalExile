# Slayer

The ordinary Slayer (Duelist, `Ascendancy` row 9, start node 33795) has 14 nodes: `PassiveSkills` rows 1519 to 1532.
Every stat is applied through its sum, so another source of the same stat works the same way.
The Phrecia Aristocrat shares the start node; its nodes are in [aristocrat.md](aristocrat.md).

## Nodes

| Node | Stats (value) | Consumer |
| --- | --- | --- |
| Slayer1 | `critical_strike_chance_+%` 15, `attack_damage_+%` 10 | `AttackBaseCriticalStrike.IncreasedSum`, `PlayerHitDamage` |
| Slayer2, 3 | `attack_speed_+%` 5, `attack_damage_+%` 10 | `PassiveBonus`, `PlayerHitDamage` |
| Slayer4, 5 | `base_life_leech_from_attack_damage_permyriad` 40, `attack_damage_+%` 10 | `LeechSources` |
| Slayer6 | `base_skill_area_of_effect_+%` 10, `attack_damage_+%` 10 | `SkillArea` |
| Slayer13 | `base_frenzy_charge_duration_+%` 15, `endurance_charge_duration_+%` 15, `attack_damage_+%` 10 | `Charges.LimitsOf` |
| Headsman (3184) | `gain_movement_speed_+%_for_20_seconds_on_kill` 10 | `Slayer.Kill`, `Slayer.Tick` |
| Headsman | `gain_attack_speed_+%_for_20_seconds_on_killing_rare_or_unique_enemy` 10 | `Slayer.Kill`, `Slayer.Tick` |
| Headsman | `kill_enemy_on_hit_if_under_20%_life` | `CullingStrike` |
| Overwhelm (17315) | `override_weapon_base_critical_strike_chance` 800 | `AttackBaseCriticalStrike` |
| Overwhelm | `slayer_critical_strike_multiplier_+_per_nearby_enemy_up_to_100` 10 | `Slayer.Tick`, `PassiveBonus` |
| Overwhelm | aura `SlayerCritMultiplier`: nearby enemies `base_critical_strike_multiplier_+` -30 | `EnemyAuraStatLocked` |
| Bane of Legends (62817) | `slayer_damage_+%_final_against_unique_enemies` 20, `damage_+%_if_enemy_killed_recently_final` 10 | `Slayer.OutgoingDamage` |
| Bane of Legends | `cannot_take_reflected_physical_damage` | none needed: no monster reflects damage |
| Endless Hunger (34484) | `life_leech_on_overkill_damage_%` 20 | `LeechSources.OverkillLife` |
| Endless Hunger | `attack_speed_+%_while_leeching` 20 | `LeechingAttackSpeed` |
| Endless Hunger | `cannot_be_stunned_while_leeching`, `unaffected_by_bleeding_while_leeching` | `WithLeechingDefences` |
| Brutal Fervour (10143) | `life_leech_does_not_stop_at_full_life`, `maximum_life_leech_amount_per_leech_+%` 100 | `LeechSession`, `LeechLimits` |
| Brutal Fervour | `damage_taken_+%_while_leeching` -10 | `PlayerDamageTaken.IncreasedPct` |
| Impact (38180) | `slayer_area_of_effect_+%_per_enemy_killed_recently_up_to_50%` 5 | `Slayer.Tick`, `SkillArea` |
| Impact | `accuracy_rating_+%` 50, `slayer_damage_+%_final_from_distance` 15 | `AccuracyRating`, `MeleeProximity` |
| Impact | `melee_range_+` 4 | not applied (below) |
| Masterful Form (16306) | `maximum_endurance_charges_is_equal_to_maximum_frenzy_charges`, `max_frenzy_charges` 1 | `ChargePanelStats` |

## Client

The network player stats are taken as sent ([04](../protocol/04-player-entity.md) §4.1a), so the server sends the final values.
The client's own calculators show what they read, by key (Stats row + 1):

- Crit multiplier `0x14276e1f0` adds `min(100, S[26a6] x S[18a1])`, `number_of_nearby_enemies`; no lower bound.
- Area `0x14274c820` adds `min(50, S[26a2] x S[1f22])`, `number_of_enemies_killed_recently`
  ([area terms](../re/projectiles/area-increased-terms.md) §4).
- `track_number_of_enemies_killed_recently` (`0x142834e90`) is set by any of the per-kill area stats; the client counts
  neither number, so the server does.
- Maximum endurance `0x142811290`: with Masterful Form's flag it is `virtual_maximum_frenzy_charges`.
- Increased attack speed `0x142704be0`, damage taken `0x1427d25c0`, `cannot_be_stunned` `0x1427e17d0` and
  `virtual_unaffected_by_bleeding` `0x142896810` add the "while Leeching" stats only with `is_leeching`.

## Model

- "Nearby" is 60 cells, the radius of Overwhelm's own aura; the client does not name it.
- The tick (250 ms) counts living hostile monsters within it and kills of the last 4 s, only when a stat reads them.
  They join the stat sources with `have_killed_recently`; a change rebuilds the pools and sends the stats.
- Headsman's buffs are BuffDefinitions 1341 and 1342 (`bane_of_legends_*`, invisible, no values): 20 s from the last
  kill, refreshed by each, ended by death.
- Impact's area joins `base_skill_area_of_effect_+%`, as Rampage's does.
- Bane of Legends scales the rolled hit of the three executors that run `GameEvents.OutgoingDamage` (default attack,
  area skills, projectiles), so their ailments read it too.
- Overkill is the hit's direct damage beyond the target's Life before it; Impale's reflected part is not counted.
- Overwhelm's aura acts on monster critical strikes on the player, by the radius at contact.

## Not applied

- `melee_range_+`: the server models no melee range stat.
  The client reads it in a range sum (`0x1427e3950`), but whether its walk-up distance grows is not known.
- Bane of Legends on other hit executors (triggered spells, traps, totems) and on damage over time not made from a hit.
- Overwhelm's aura on monster hits against minions and on Rampage's incoming hits.
- Unaffected by Bleeding: the only bleed on the player is the Labyrinth's spike trap, which then deals nothing.

## Not seen in the client

Live 2026-11-09 confirmed the crit multiplier, Headsman, Bane of Legends (1.1x, 1.2x on a map boss, 1.32x after a
kill), Impact's area (+10% to +60%), Masterful Form and the attack speed while leeching. Not observed, so only tested:

- overkill leech: no log line, and its effect hides among the other leech;
- cannot be stunned, 10% less damage taken and unaffected by Bleeding while leeching: the logged 0x01b3 stats are
  cut at 96 bytes, before these keys;
- whether the client's walk-up distance would follow `melee_range_+` (not applied, above).
