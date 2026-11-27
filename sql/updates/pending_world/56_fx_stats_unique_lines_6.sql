-- Two more single unique lines.
UPDATE fx_stats SET handled = 'code', ref = 'MonsterAilments.AilmentsOnHitLocked', why = NULL, fit = NULL
WHERE stat = 'chance_to_shock_chilled_enemies_%';
UPDATE fx_stats SET handled = 'code', ref = 'MonsterDecay, PlayerDebuffs, ArenaHazards', why = NULL, fit = NULL
WHERE stat = 'chaos_damage_taken_over_time_+%';
