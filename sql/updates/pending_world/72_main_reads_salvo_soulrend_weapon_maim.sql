-- LocalExile 0.6.1 reads these itself: Blazing Salvo's own extra projectiles (GatewayProjectileSkillProfiles), Soulrend's
-- own damage (Soulrend.cs) and a weapon's "Maim on hit" on its attacks only (HitDebuffs, WithWeaponMaim). The rows that
-- carried them to generic stats would count them a second time.
UPDATE fx_skill_stats SET enabled = 0, note = 'Off: main reads blazing_salvo_number_of_additional_projectiles itself (0.6.1).' WHERE stat = 'blazing_salvo_number_of_additional_projectiles' AND skill = 'blazing_salvo';
UPDATE fx_skill_stats SET enabled = 0, note = 'Off: main reads soulrend_damage_+% itself (0.6.1).' WHERE stat = 'soulrend_damage_+%' AND skill = 'soulrend';
UPDATE fx_item_local SET enabled = 0, note = 'Off: main applies a weapon''s local_maim_on_hit to its own attacks (0.6.1).' WHERE stat = 'local_maim_on_hit';
UPDATE fx_stats SET handled = 'code', code = 'GatewayProjectileSkillProfiles.cs' WHERE stat = 'blazing_salvo_number_of_additional_projectiles';
UPDATE fx_stats SET handled = 'code', code = 'Soulrend.cs' WHERE stat = 'soulrend_damage_+%';
UPDATE fx_stats SET handled = 'code', code = 'HitDebuffs.cs' WHERE stat IN ('local_maim_on_hit', 'local_maim_on_hit_%');
