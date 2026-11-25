-- Signature lines of well-known uniques, built in code.
UPDATE fx_stats SET handled = 'code', ref = 'PlayerHitDamage.Resolve', why = NULL, fit = 'approximate'
WHERE stat = 'unique_quill_rain_damage_+%_final';
UPDATE fx_stats SET handled = 'code', ref = 'WeaponTypeDamage.MeleePhysicalMore', why = NULL, fit = NULL
WHERE stat = 'unique_facebreaker_unarmed_melee_physical_damage_+%_final';
UPDATE fx_stats SET handled = 'code', ref = 'Progression (pool movement speed)', why = NULL, fit = 'approximate'
WHERE stat = 'movement_speed_is_%_of_base';
UPDATE fx_stats SET handled = 'code', ref = 'AttackHands.FixedWeaponCritical', why = NULL, fit = 'approximate'
WHERE stat IN ('local_weapon_crit_chance_is_100', 'local_weapon_crit_chance_is_%');
