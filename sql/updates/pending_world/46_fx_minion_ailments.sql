-- Read by code from this change on: a minion's hit rolls Chill, Freeze and Shock from its cold and lightning damage
-- on the minion's own chances ("Minions have #% chance to Freeze / Shock", and the freeze and shock share of "...
-- chance to Freeze, Shock and Ignite"), and "Minion Critical Strikes do not deal extra Damage" counts at its hit.
-- Read already: "Minions never deal Critical Strikes" (the minion's critical roll asks for it by name).
UPDATE fx_stats SET handled = 'code', code = 'GolemCombat.cs, MonsterAilments.cs', why = NULL, fit = NULL
WHERE stat IN ('minion_chance_to_freeze_%', 'minion_chance_to_shock_%', 'minion_chance_to_freeze_shock_ignite_%');
UPDATE fx_stats SET handled = 'code', code = 'GolemCombat.cs', why = NULL, fit = NULL
WHERE stat IN ('minion_no_critical_strike_multiplier', 'minion_cannot_crit');
-- Read already, by a built name (MinionUtility: the offering's kind + "_offering_effect_+%").
UPDATE fx_stats SET handled = 'code', code = 'MinionUtility.cs', why = NULL, fit = NULL
WHERE stat IN ('bone_offering_effect_+%', 'flesh_offering_effect_+%', 'spirit_offering_effect_+%');
