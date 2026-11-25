-- Two more single unique lines built in code.
UPDATE fx_stats SET handled = 'code', ref = 'Combat.cs (the hit''s critical chance against its target)', why = NULL, fit = 'approximate'
WHERE stat = 'global_critical_strike_chance_+%_vs_chilled_enemies';
UPDATE fx_stats SET handled = 'code', ref = 'TargetDamage.ContactDamage', why = NULL, fit = 'approximate'
WHERE stat = 'frozen_monsters_take_increased_damage';
UPDATE fx_stats SET handled = 'code', ref = 'Leech.WithCriticalLeech', why = NULL, fit = 'approximate'
WHERE stat IN ('life_leech_permyriad_on_crit', 'cannot_leech_life_from_critical_strikes');
