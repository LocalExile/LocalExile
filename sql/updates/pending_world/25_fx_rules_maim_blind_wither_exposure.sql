-- Maim, Blind, Withered and Exposure on hit (the rest of HitDebuffs.cs, and Blind.cs's hit) as fx rules. As before:
-- every hit of the character's side counts (who: any), stats the code adds up and rolls once are one rule's summed
-- chance, and while a rule is live for a character the code leaves its stats alone (a skill's share alone stays the
-- code's). A rule rolls the chance and names the debuff, its base duration and, for Exposure, its base magnitude.
-- What the debuff asks of the enemy (immunity, a chance to avoid it) and what the hitting side's other stats do to
-- it (duration, effect, magnitude) is still worked out by the code the hit used before; those stats are modifiers
-- of the quantities below.
-- Not here: Elemental Equilibrium (the passive tree, later) and the projectile-only Maim chance; see fx_stats.
UPDATE fx_actions SET takes = 'debuff: intimidated, unnerved, crushed, hindered, maimed, blinded, withered, fire_exposure, cold_exposure, lightning_exposure; value: the base magnitude of a debuff that has one (an Exposure''s resistance)',
    note = 'Puts a debuff on the trigger''s enemy for the duration timer (its base duration), from the moment of the hit; a later one restarts it, and Withered adds a stack. The debuff''s own rules are the code''s: the enemy''s immunity and chance to avoid it, and the quantities that change its duration, effect or magnitude.'
WHERE id = 'debuff';
INSERT INTO fx_conditions (id, takes, built, origin, note) VALUES
('damage_type', 'is: fire, cold, lightning', 1, 'chance to inflict Fire Exposure on Hit with Fire Damage', 'The trigger''s hit deals damage of that type, after the enemy''s mitigation. As a term''s "when" in a summed chance it is written deals_fire, deals_cold or deals_lightning.'),
('enemy_immune', 'to: a debuff or ailment', 0, 'immune_to_maim on a monster', 'The trigger''s enemy cannot have that debuff. Not a rule condition yet: the debuff action asks it itself (immune_to_maim, immune_to_exposure, immune_to_wither, cannot_be_blinded).'),
('enemy_avoids', 'what: a debuff or ailment', 0, 'avoid_maim_%_chance on a monster', 'The trigger''s enemy rolls its own chance to avoid that debuff. Not a rule condition yet: the debuff action rolls it itself (avoid_maim_%_chance).')
ON CONFLICT(id) DO NOTHING;
INSERT INTO fx_quantities (id, unit, formula, built, origin, note) VALUES
('maimed_physical_damage_taken', '%', 'code', 1, 'support_maimed_enemies_physical_damage_taken_+%', 'The increased physical damage an enemy takes while maimed by this hit''s Maim (the Maim support). The strongest running one counts.'),
('withered_duration', 's', 'code', 1, 'support_withered_base_duration_ms', 'How long a Withered stack from a hit lasts: the rule''s 2 seconds, or the hitting skill''s own duration, then wither_expire_speed.'),
('exposure_magnitude', '%', 'code', 1, 'exposure_you_inflict_applies_extra_%_to_affected_resistance', 'What is added to the resistance of any Exposure the character inflicts (its own magnitude comes from the rule).'),
('fire_exposure_magnitude', '%', 'code', 1, 'fire_exposure_you_inflict_applies_extra_fire_resistance_+%', 'What is added to the resistance of Fire Exposure the character inflicts.'),
('cold_exposure_magnitude', '%', 'code', 1, 'cold_exposure_you_inflict_applies_extra_cold_resistance_+%', 'What is added to the resistance of Cold Exposure the character inflicts.'),
('cannot_inflict_exposure', '', 'code', 1, 'cannot_inflict_exposure', 'The character inflicts no Exposure at all.')
ON CONFLICT(id) DO NOTHING;
-- These chances were written down as numbers of their own; they are a rule's now.
DELETE FROM fx_modifiers WHERE stat IN ('%_chance_to_blind_on_critical_strike', 'withered_on_hit_for_2_seconds_%_chance',
    'base_inflict_fire_exposure_on_hit_%_chance', 'base_inflict_cold_exposure_on_hit_%_chance', 'base_inflict_lightning_exposure_on_hit_%_chance');
DELETE FROM fx_quantities WHERE id IN ('%_chance_to_blind_on_critical_strike', 'withered_on_hit_for_2_seconds_%_chance',
    'inflict_fire_exposure_on_hit_%_chance', 'inflict_cold_exposure_on_hit_%_chance', 'inflict_lightning_exposure_on_hit_%_chance',
    'exposure_you_inflict_applies_extra_%_to_affected_resistance');
UPDATE fx_modifiers SET quantity = 'exposure_magnitude', how = 'flat' WHERE stat = 'exposure_you_inflict_applies_extra_%_to_affected_resistance';
INSERT INTO fx_modifiers (stat, ord, quantity, how, scale, note) VALUES
('support_maimed_enemies_physical_damage_taken_+%', 0, 'maimed_physical_damage_taken', 'flat', 1, 'Enemies Maimed by Supported Skills take {0}% increased Physical Damage'),
('support_withered_base_duration_ms', 0, 'withered_duration', 'override', 0.001, 'The hitting skill''s own Withered duration, in place of the rule''s 2 seconds'),
('fire_exposure_you_inflict_applies_extra_fire_resistance_+%', 0, 'fire_exposure_magnitude', 'flat', 1, 'Fire Exposure you inflict applies an extra {0}% to Fire Resistance'),
('cold_exposure_you_inflict_applies_extra_cold_resistance_+%', 0, 'cold_exposure_magnitude', 'flat', 1, 'Cold Exposure you inflict applies an extra {0}% to Cold Resistance'),
('exposure_you_inflict_has_minimum_resistance_%', 0, 'exposure_magnitude', 'maximum', 1, 'Exposure you inflict applies at least {0}% to the affected Resistance: the resistance applied is no higher than this'),
('cannot_inflict_exposure', 0, 'cannot_inflict_exposure', 'flag', 1, 'Cannot inflict Exposure')
ON CONFLICT(stat, ord) DO NOTHING;
INSERT INTO fx_rules (id, book, title, replaces, status, origin) VALUES
('maim_on_hit', 'debuffs', 'Attacks have a chance to Maim on Hit, for 4 seconds', 'maim_on_hit_%', 'built', 'HitDebuffs.cs'),
('blind_on_hit', 'debuffs', 'Chance to Blind Enemies on Hit, for 4 seconds; the attacks-only chance counts for an attack and the critical strike chance for a critical strike', 'global_chance_to_blind_on_hit_%', 'built', 'Blind.cs, HitDebuffs.cs'),
('wither_on_hit', 'debuffs', 'Chance to inflict Withered for 2 seconds on Hit: one more stack', 'withered_on_hit_for_2_seconds_%_chance', 'built', 'HitDebuffs.cs'),
('fire_exposure_on_hit', 'debuffs', 'Chance to inflict Fire Exposure on Hit (-10% for 4 seconds); the "with Fire Damage" chance counts for a hit that deals fire damage', 'base_inflict_fire_exposure_on_hit_%_chance', 'built', 'HitDebuffs.cs'),
('cold_exposure_on_hit', 'debuffs', 'Chance to inflict Cold Exposure on Hit (-10% for 4 seconds); the "with Cold Damage" chance counts for a hit that deals cold damage', 'base_inflict_cold_exposure_on_hit_%_chance', 'built', 'HitDebuffs.cs'),
('lightning_exposure_on_hit', 'debuffs', 'Chance to inflict Lightning Exposure on Hit (-10% for 4 seconds); the "with Lightning Damage" chance counts for a hit that deals lightning damage', 'base_inflict_lightning_exposure_on_hit_%_chance', 'built', 'HitDebuffs.cs'),
('fire_exposure_on_hit_own', 'debuffs', 'Inflict Fire Exposure on Hit, applying the stat''s own value to the resistance', 'fire_exposure_on_hit_magnitude', 'built', 'HitDebuffs.cs'),
('cold_exposure_on_hit_own', 'debuffs', 'Inflict Cold Exposure on Hit, applying the stat''s own value to the resistance', 'cold_exposure_on_hit_magnitude', 'built', 'HitDebuffs.cs'),
('lightning_exposure_on_hit_own', 'debuffs', 'Inflict Lightning Exposure on Hit, applying the stat''s own value to the resistance', 'lightning_exposure_on_hit_magnitude', 'built', 'HitDebuffs.cs'),
('all_exposure_on_hit', 'debuffs', 'Inflict Fire, Cold and Lightning Exposure on Hit', 'inflict_all_exposure_on_hit', 'built', 'HitDebuffs.cs');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES
('maim_on_hit', 0, 'stat', 'maim_on_hit_%'),
('blind_on_hit', 0, 'stat', 'global_chance_to_blind_on_hit_%'),
('blind_on_hit', 1, 'stat', 'attacks_chance_to_blind_on_hit_%'),
('blind_on_hit', 2, 'stat', '%_chance_to_blind_on_critical_strike'),
('wither_on_hit', 0, 'stat', 'withered_on_hit_for_2_seconds_%_chance'),
('wither_on_hit', 1, 'stat', 'withered_on_hit_chance_%'),
('fire_exposure_on_hit', 0, 'stat', 'base_inflict_fire_exposure_on_hit_%_chance'),
('fire_exposure_on_hit', 1, 'stat', 'chance_to_inflict_fire_exposure_on_hit_with_fire_damage_%'),
('cold_exposure_on_hit', 0, 'stat', 'base_inflict_cold_exposure_on_hit_%_chance'),
('cold_exposure_on_hit', 1, 'stat', 'chance_to_inflict_cold_exposure_on_hit_with_cold_damage_%'),
('lightning_exposure_on_hit', 0, 'stat', 'base_inflict_lightning_exposure_on_hit_%_chance'),
('lightning_exposure_on_hit', 1, 'stat', 'chance_to_inflict_lightning_exposure_on_hit_with_lightning_damage_%'),
('fire_exposure_on_hit_own', 0, 'stat', 'fire_exposure_on_hit_magnitude'),
('cold_exposure_on_hit_own', 0, 'stat', 'cold_exposure_on_hit_magnitude'),
('lightning_exposure_on_hit_own', 0, 'stat', 'lightning_exposure_on_hit_magnitude'),
('all_exposure_on_hit', 0, 'stat', 'inflict_all_exposure_on_hit');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('maim_on_hit', 0, 'hit', '{"who":"any"}'),
('blind_on_hit', 0, 'hit', '{"who":"any"}'),
('wither_on_hit', 0, 'hit', '{"who":"any"}'),
('fire_exposure_on_hit', 0, 'hit', '{"who":"any"}'),
('cold_exposure_on_hit', 0, 'hit', '{"who":"any"}'),
('lightning_exposure_on_hit', 0, 'hit', '{"who":"any"}'),
('fire_exposure_on_hit_own', 0, 'hit', '{"who":"any"}'),
('cold_exposure_on_hit_own', 0, 'hit', '{"who":"any"}'),
('lightning_exposure_on_hit_own', 0, 'hit', '{"who":"any"}'),
('all_exposure_on_hit', 0, 'hit', '{"who":"any"}');
INSERT INTO fx_rule_if (rule, grp, ord, condition, negate, args) VALUES
('maim_on_hit', 0, 0, 'chance', 0, '{"percent":{"from":"roll","skill":true}}'),
('blind_on_hit', 0, 0, 'chance', 0, '{"percent":{"from":"sum","of":[{"stat":"global_chance_to_blind_on_hit_%"},{"stat":"attacks_chance_to_blind_on_hit_%","when":"attack"},{"stat":"%_chance_to_blind_on_critical_strike","when":"critical"}],"skill":true}}'),
('wither_on_hit', 0, 0, 'chance', 0, '{"percent":{"from":"sum","of":[{"stat":"withered_on_hit_for_2_seconds_%_chance"},{"stat":"withered_on_hit_chance_%"}],"skill":true}}'),
('fire_exposure_on_hit', 0, 0, 'chance', 0, '{"percent":{"from":"sum","of":[{"stat":"base_inflict_fire_exposure_on_hit_%_chance"},{"stat":"chance_to_inflict_fire_exposure_on_hit_with_fire_damage_%","when":"deals_fire"}],"skill":true}}'),
('cold_exposure_on_hit', 0, 0, 'chance', 0, '{"percent":{"from":"sum","of":[{"stat":"base_inflict_cold_exposure_on_hit_%_chance"},{"stat":"chance_to_inflict_cold_exposure_on_hit_with_cold_damage_%","when":"deals_cold"}],"skill":true}}'),
('lightning_exposure_on_hit', 0, 0, 'chance', 0, '{"percent":{"from":"sum","of":[{"stat":"base_inflict_lightning_exposure_on_hit_%_chance"},{"stat":"chance_to_inflict_lightning_exposure_on_hit_with_lightning_damage_%","when":"deals_lightning"}],"skill":true}}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('maim_on_hit', 0, 'debuff', '{"debuff":"maimed"}', 'enemy', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('blind_on_hit', 0, 'debuff', '{"debuff":"blinded"}', 'enemy', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('wither_on_hit', 0, 'debuff', '{"debuff":"withered"}', 'enemy', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":2}}]'),
('fire_exposure_on_hit', 0, 'debuff', '{"debuff":"fire_exposure"}', 'enemy', '{}', '{"fixed":-10}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('cold_exposure_on_hit', 0, 'debuff', '{"debuff":"cold_exposure"}', 'enemy', '{}', '{"fixed":-10}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('lightning_exposure_on_hit', 0, 'debuff', '{"debuff":"lightning_exposure"}', 'enemy', '{}', '{"fixed":-10}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('fire_exposure_on_hit_own', 0, 'debuff', '{"debuff":"fire_exposure"}', 'enemy', '{}', '{"from":"roll"}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('cold_exposure_on_hit_own', 0, 'debuff', '{"debuff":"cold_exposure"}', 'enemy', '{}', '{"from":"roll"}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('lightning_exposure_on_hit_own', 0, 'debuff', '{"debuff":"lightning_exposure"}', 'enemy', '{}', '{"from":"roll"}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('all_exposure_on_hit', 0, 'debuff', '{"debuff":"fire_exposure"}', 'enemy', '{}', '{"fixed":-10}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('all_exposure_on_hit', 1, 'debuff', '{"debuff":"cold_exposure"}', 'enemy', '{}', '{"fixed":-10}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('all_exposure_on_hit', 2, 'debuff', '{"debuff":"lightning_exposure"}', 'enemy', '{}', '{"fixed":-10}', '[{"timer":"duration","seconds":{"fixed":4}}]');
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES
('maim_on_hit_%', 'trigger', 'rule', 'maim_on_hit', 'HitDebuffs.cs'),
('global_chance_to_blind_on_hit_%', 'trigger', 'rule', 'blind_on_hit', 'Blind.cs'),
('attacks_chance_to_blind_on_hit_%', 'trigger', 'rule', 'blind_on_hit', 'Blind.cs'),
('%_chance_to_blind_on_critical_strike', 'trigger', 'rule', 'blind_on_hit', 'Blind.cs'),
('withered_on_hit_for_2_seconds_%_chance', 'trigger', 'rule', 'wither_on_hit', 'HitDebuffs.cs'),
('withered_on_hit_chance_%', 'trigger', 'rule', 'wither_on_hit', 'HitDebuffs.cs'),
('base_inflict_fire_exposure_on_hit_%_chance', 'trigger', 'rule', 'fire_exposure_on_hit', 'HitDebuffs.cs'),
('chance_to_inflict_fire_exposure_on_hit_with_fire_damage_%', 'trigger', 'rule', 'fire_exposure_on_hit', 'HitDebuffs.cs'),
('base_inflict_cold_exposure_on_hit_%_chance', 'trigger', 'rule', 'cold_exposure_on_hit', 'HitDebuffs.cs'),
('chance_to_inflict_cold_exposure_on_hit_with_cold_damage_%', 'trigger', 'rule', 'cold_exposure_on_hit', 'HitDebuffs.cs'),
('base_inflict_lightning_exposure_on_hit_%_chance', 'trigger', 'rule', 'lightning_exposure_on_hit', 'HitDebuffs.cs'),
('chance_to_inflict_lightning_exposure_on_hit_with_lightning_damage_%', 'trigger', 'rule', 'lightning_exposure_on_hit', 'HitDebuffs.cs'),
('fire_exposure_on_hit_magnitude', 'trigger', 'rule', 'fire_exposure_on_hit_own', 'HitDebuffs.cs'),
('cold_exposure_on_hit_magnitude', 'trigger', 'rule', 'cold_exposure_on_hit_own', 'HitDebuffs.cs'),
('lightning_exposure_on_hit_magnitude', 'trigger', 'rule', 'lightning_exposure_on_hit_own', 'HitDebuffs.cs'),
('inflict_all_exposure_on_hit', 'trigger', 'rule', 'all_exposure_on_hit', 'HitDebuffs.cs')
ON CONFLICT(stat) DO UPDATE SET kind = 'trigger', handled = 'rule', ref = excluded.ref;
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES
('support_maimed_enemies_physical_damage_taken_+%', 'number', 'modifier', 'maimed_physical_damage_taken flat', 'HitDebuffs.cs'),
('support_withered_base_duration_ms', 'number', 'modifier', 'withered_duration override', 'HitDebuffs.cs'),
('fire_exposure_you_inflict_applies_extra_fire_resistance_+%', 'number', 'modifier', 'fire_exposure_magnitude flat', 'HitDebuffs.cs'),
('cold_exposure_you_inflict_applies_extra_cold_resistance_+%', 'number', 'modifier', 'cold_exposure_magnitude flat', 'HitDebuffs.cs'),
('exposure_you_inflict_has_minimum_resistance_%', 'number', 'modifier', 'exposure_magnitude maximum', 'HitDebuffs.cs'),
('cannot_inflict_exposure', 'flag', 'modifier', 'cannot_inflict_exposure flag', 'HitDebuffs.cs'),
('exposure_you_inflict_applies_extra_%_to_affected_resistance', 'number', 'modifier', 'exposure_magnitude flat', 'HitDebuffs.cs')
ON CONFLICT(stat) DO UPDATE SET kind = excluded.kind, handled = 'modifier', ref = excluded.ref;
INSERT INTO fx_stats (stat, kind, handled, code, why, fit) VALUES
('projectile_attacks_chance_to_maim_on_hit_%_if_you_have_beast_minion', 'conditional number', 'code', 'ProjectileRelease.cs, Projectiles.cs', 'The chance is read with its condition (a beast minion) when the projectile is released, and rolled where the projectile hits.', 'A rule on hit for an attack, with a "has a beast minion" condition and a fact that the hit was a projectile''s, doing the maimed debuff.'),
('keystone_elemental_equilibrium', 'flag', 'code', 'HitDebuffs.cs', 'After the hit''s own Exposure: ends the Exposure of each element the hit dealt and inflicts -25% Exposure of the others. A keystone: the passive tree comes after items, maps and currencies.', 'A rule on hit with damage_type conditions for each element and two actions: the exposure debuffs, and a new action that ends a debuff.'),
('elemental_equilibrium_effect_+%', 'number', 'code', 'HitDebuffs.cs', 'Scales the Exposure Elemental Equilibrium inflicts; it goes where the keystone goes.', 'A modifier of the keystone rule''s Exposure magnitude.')
ON CONFLICT(stat) DO UPDATE SET handled = 'code', why = excluded.why, fit = excluded.fit;
