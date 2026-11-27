-- Three more single unique lines in code.
UPDATE fx_stats SET handled = 'code', ref = 'ChargeOnslaught.SourceOnslaughtPredicates', why = NULL, fit = NULL
WHERE stat = 'base_should_have_onslaught_from_stat';
UPDATE fx_stats SET handled = 'code', ref = 'MonsterPoisons.Settings.From', why = NULL, fit = 'approximate'
WHERE stat = 'chance_to_poison_on_melee_hit_%';
UPDATE fx_stats SET handled = 'code', ref = 'Progression (spell block total)', why = NULL, fit = NULL
WHERE stat = 'cannot_block_spells';
UPDATE fx_stats SET handled = 'code', ref = 'IncomingDamage.DamagePlayerHitTyped', why = NULL, fit = NULL
WHERE stat = '%_physical_damage_bypasses_energy_shield';
