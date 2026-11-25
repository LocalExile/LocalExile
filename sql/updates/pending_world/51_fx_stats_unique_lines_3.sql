-- Unique lines built in code (batch 29), and the Aegis skills.
UPDATE fx_stats SET handled = 'code', ref = 'MonsterPoisons.TryPoisonOnHitLocked', why = NULL, fit = NULL
WHERE stat IN ('chance_to_poison_%_vs_cursed_enemies', 'attacks_chance_to_poison_%_on_max_frenzy_charges');
UPDATE fx_stats SET handled = 'code', ref = 'PlayerStatSteps (BlockDamage step)', why = NULL, fit = NULL
WHERE stat = 'critical_strike_multiplier_+_per_1%_block_chance';
UPDATE fx_stats SET handled = 'code', ref = 'AttributeOffenceSources.Thresholds', why = NULL, fit = NULL
WHERE stat = 'critical_strike_chance_+%_with_at_least_200_int';
-- Physical Aegis (The Ghastly Theatre) was built already (PhysicalAegis.cs); the other four follow it (ElementalAegis.cs).
UPDATE fx_stats SET handled = 'code', ref = 'PhysicalAegis.cs', why = NULL, fit = NULL
WHERE stat = 'local_display_cast_physical_aegis_on_gain_skill';
UPDATE fx_stats SET handled = 'code', ref = 'ElementalAegis.cs', why = NULL, fit = NULL
WHERE stat IN ('local_display_cast_fire_aegis_on_gain_skill', 'local_display_cast_cold_aegis_on_gain_skill',
  'local_display_cast_lightning_aegis_on_gain_skill', 'local_display_cast_elemental_aegis_on_gain_skill');
