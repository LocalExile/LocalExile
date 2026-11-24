-- A larger batch. Rules that replace code behind the switch: the six "Curse Enemies with X on Hit" stats and
-- Temporal Chains on hit, Arcane Surge on critical strike and on spell hit, Phasing on kill, Spirit Charge on kill.
-- Amounts whose arithmetic stays in code, written down as modifiers of quantities with one 'not built' rule each:
-- recovery on block, and the chances a hit has to poison, bleed, freeze, impale, stun and knock back.
-- Stats looked at and left in code get the reason and a way to fit them in fx_stats.
INSERT INTO fx_actions (id, takes, built, origin, note) VALUES
('curse', 'curse: the hex''s GrantedEffects Id; refresh_after: seconds a running curse is left alone for (1 unless given); value: the gem level it is applied at', 1, 'Curse Enemies with Despair on Hit', 'Puts a hex on the trigger''s enemy as a cast of it at that level would: the curse limit and hexproof apply.')
ON CONFLICT(id) DO NOTHING;
UPDATE fx_actions SET takes = takes || '; or buff: onslaught, phasing, arcane_surge (the game''s own buffs of those names)' WHERE id = 'buff';
UPDATE fx_actions SET takes = takes || '; or type: spirit (one Spirit Charge, up to the limit)' WHERE id = 'charge';
INSERT INTO fx_rules (id, book, title, replaces, status, origin) VALUES
('curse_despair_on_hit', 'curses', 'Curse Enemies with Despair on Hit; the stat is the level of the curse', 'curse_on_hit_level_despair', 'built', 'ItemCursesOnHit.cs'),
('curse_elemental_weakness_on_hit', 'curses', 'Curse Enemies with Elemental Weakness on Hit; the stat is the level of the curse', 'curse_on_hit_level_elemental_weakness', 'built', 'ItemCursesOnHit.cs'),
('curse_flammability_on_hit', 'curses', 'Curse Enemies with Flammability on Hit; the stat is the level of the curse', 'curse_on_hit_level_flammability', 'built', 'ItemCursesOnHit.cs'),
('curse_frostbite_on_hit', 'curses', 'Curse Enemies with Frostbite on Hit; the stat is the level of the curse', 'curse_on_hit_level_frostbite', 'built', 'ItemCursesOnHit.cs'),
('curse_conductivity_on_hit', 'curses', 'Curse Enemies with Conductivity on Hit; the stat is the level of the curse', 'curse_on_hit_level_conductivity', 'built', 'ItemCursesOnHit.cs'),
('curse_vulnerability_on_hit', 'curses', 'Curse Enemies with Vulnerability on Hit; the stat is the level of the curse', 'curse_on_hit_level_vulnerability', 'built', 'ItemCursesOnHit.cs'),
('temporal_chains_on_hit', 'curses', 'Chance to Curse Enemies with Temporal Chains on Hit', 'curse_on_hit_%_temporal_chains', 'built', 'Curses.cs'),
('arcane_surge_on_crit', 'buffs', 'Chance to gain Arcane Surge when you deal a Critical Strike', 'gain_arcane_surge_on_crit_%_chance', 'built', 'SupportGemBuffs.cs'),
('arcane_surge_on_spell_hit', 'buffs', 'Chance to gain Arcane Surge on Hit with Spells', 'gain_arcane_surge_on_hit_%_chance', 'built', 'SupportGemBuffs.cs'),
('phasing_on_kill', 'buffs', 'Chance to gain Phasing for 4 seconds on Kill', 'phasing_for_4_seconds_on_kill_%', 'built', 'Phasing.cs'),
('spirit_charge_on_kill', 'charges', 'Chance to gain a Spirit Charge on Kill', 'gain_spirit_charge_on_kill_%_chance', 'built', 'SpiritCharges.cs');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES
('curse_despair_on_hit', 0, 'stat', 'curse_on_hit_level_despair'),
('curse_elemental_weakness_on_hit', 0, 'stat', 'curse_on_hit_level_elemental_weakness'),
('curse_flammability_on_hit', 0, 'stat', 'curse_on_hit_level_flammability'),
('curse_frostbite_on_hit', 0, 'stat', 'curse_on_hit_level_frostbite'),
('curse_conductivity_on_hit', 0, 'stat', 'curse_on_hit_level_conductivity'),
('curse_vulnerability_on_hit', 0, 'stat', 'curse_on_hit_level_vulnerability'),
('temporal_chains_on_hit', 0, 'stat', 'curse_on_hit_%_temporal_chains'),
('arcane_surge_on_crit', 0, 'stat', 'gain_arcane_surge_on_crit_%_chance'),
('arcane_surge_on_spell_hit', 0, 'stat', 'gain_arcane_surge_on_hit_%_chance'),
('phasing_on_kill', 0, 'stat', 'phasing_for_4_seconds_on_kill_%'),
('spirit_charge_on_kill', 0, 'stat', 'gain_spirit_charge_on_kill_%_chance');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('curse_despair_on_hit', 0, 'hit', '{}'),
('curse_elemental_weakness_on_hit', 0, 'hit', '{}'),
('curse_flammability_on_hit', 0, 'hit', '{}'),
('curse_frostbite_on_hit', 0, 'hit', '{}'),
('curse_conductivity_on_hit', 0, 'hit', '{}'),
('curse_vulnerability_on_hit', 0, 'hit', '{}'),
('temporal_chains_on_hit', 0, 'hit', '{}'),
('arcane_surge_on_crit', 0, 'hit', '{}'),
('arcane_surge_on_spell_hit', 0, 'hit', '{}'),
('phasing_on_kill', 0, 'kill', '{}'),
('spirit_charge_on_kill', 0, 'kill', '{}');
INSERT INTO fx_rule_if (rule, grp, ord, condition, negate, args) VALUES
('temporal_chains_on_hit', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('arcane_surge_on_crit', 0, 0, 'critical', 0, '{}'),
('arcane_surge_on_crit', 0, 1, 'chance', 0, '{"percent":{"from":"roll"}}'),
('arcane_surge_on_spell_hit', 0, 0, 'skill_type', 0, '{"is":"spell"}'),
('arcane_surge_on_spell_hit', 0, 1, 'chance', 0, '{"percent":{"from":"roll"}}'),
('phasing_on_kill', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}'),
('spirit_charge_on_kill', 0, 0, 'chance', 0, '{"percent":{"from":"roll"}}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('curse_despair_on_hit', 0, 'curse', '{"curse":"Despair"}', 'enemy', '{}', '{"from":"roll"}', '[]'),
('curse_elemental_weakness_on_hit', 0, 'curse', '{"curse":"ElementalWeakness"}', 'enemy', '{}', '{"from":"roll"}', '[]'),
('curse_flammability_on_hit', 0, 'curse', '{"curse":"Flammability"}', 'enemy', '{}', '{"from":"roll"}', '[]'),
('curse_frostbite_on_hit', 0, 'curse', '{"curse":"Frostbite"}', 'enemy', '{}', '{"from":"roll"}', '[]'),
('curse_conductivity_on_hit', 0, 'curse', '{"curse":"Conductivity"}', 'enemy', '{}', '{"from":"roll"}', '[]'),
('curse_vulnerability_on_hit', 0, 'curse', '{"curse":"Vulnerability"}', 'enemy', '{}', '{"from":"roll"}', '[]'),
('temporal_chains_on_hit', 0, 'curse', '{"curse":"TemporalChains","refresh_after":0}', 'enemy', '{}', '{"fixed":1}', '[]'),
('arcane_surge_on_crit', 0, 'buff', '{"buff":"arcane_surge"}', 'self', '{}', '{}', '[]'),
('arcane_surge_on_spell_hit', 0, 'buff', '{"buff":"arcane_surge"}', 'self', '{}', '{}', '[]'),
('phasing_on_kill', 0, 'buff', '{"buff":"phasing"}', 'self', '{}', '{}', '[{"timer":"duration","seconds":{"fixed":4}}]'),
('spirit_charge_on_kill', 0, 'charge', '{"type":"spirit"}', 'self', '{}', '{}', '[]');
INSERT INTO fx_stats (stat, kind, handled, ref, code) VALUES
('curse_on_hit_level_despair', 'trigger', 'rule', 'curse_despair_on_hit', 'ItemCursesOnHit.cs'),
('curse_on_hit_level_elemental_weakness', 'trigger', 'rule', 'curse_elemental_weakness_on_hit', 'ItemCursesOnHit.cs'),
('curse_on_hit_level_flammability', 'trigger', 'rule', 'curse_flammability_on_hit', 'ItemCursesOnHit.cs'),
('curse_on_hit_level_frostbite', 'trigger', 'rule', 'curse_frostbite_on_hit', 'ItemCursesOnHit.cs'),
('curse_on_hit_level_conductivity', 'trigger', 'rule', 'curse_conductivity_on_hit', 'ItemCursesOnHit.cs'),
('curse_on_hit_level_vulnerability', 'trigger', 'rule', 'curse_vulnerability_on_hit', 'ItemCursesOnHit.cs'),
('curse_on_hit_%_temporal_chains', 'trigger', 'rule', 'temporal_chains_on_hit', 'Curses.cs'),
('gain_arcane_surge_on_crit_%_chance', 'trigger', 'rule', 'arcane_surge_on_crit', 'SupportGemBuffs.cs'),
('gain_arcane_surge_on_hit_%_chance', 'trigger', 'rule', 'arcane_surge_on_spell_hit', 'SupportGemBuffs.cs'),
('phasing_for_4_seconds_on_kill_%', 'trigger', 'rule', 'phasing_on_kill', 'Phasing.cs'),
('gain_spirit_charge_on_kill_%_chance', 'trigger', 'rule', 'spirit_charge_on_kill', 'SpiritCharges.cs')
ON CONFLICT(stat) DO UPDATE SET kind = 'trigger', handled = 'rule', ref = excluded.ref;
INSERT INTO fx_quantities (id, unit, formula, built, origin, note) VALUES
('life_gained_on_block', 'life', 'code', 1, 'Life gained when you Block', 'Flat life recovered by each block.'),
('life_%_recovered_on_block', '%', 'code', 1, 'Recover #% of Life when you Block', 'Percent of maximum life recovered by each block.'),
('mana_gained_on_block', 'mana', 'code', 1, '# Mana gained when you Block', 'Flat mana recovered by each block.'),
('mana_%_recovered_on_block', '%', 'code', 1, 'Recover #% of your maximum Mana when you Block', 'Percent of maximum mana recovered by each block.'),
('energy_shield_gained_on_block', 'energy shield', 'code', 1, 'Gain # Energy Shield when you Block', 'Flat energy shield recovered by each block.'),
('energy_shield_%_recovered_on_block', '%', 'code', 1, 'Recover #% of Energy Shield when you Block', 'Percent of maximum energy shield recovered by each block.'),
('energy_shield_from_armour_on_block', '%', 'code', 1, 'Recover Energy Shield equal to #% of Armour when you Block', 'Energy shield recovered by each block, as a percent of armour.'),
('poison_chance', '%', 'code', 1, '#% chance to Poison on Hit', 'The chance a hit poisons. The poison itself (damage, duration, stacks) is the ailment code''s.'),
('bleed_chance', '%', 'code', 1, 'Attacks have #% chance to cause Bleeding', 'The chance an attack hit causes bleeding.'),
('freeze_chance', '%', 'code', 1, '#% chance to Freeze', 'The chance a hit freezes, before the hit''s cold damage decides how long.'),
('impale_chance', '%', 'code', 1, '#% chance to Impale Enemies on Hit', 'The chance a hit impales.'),
('always_stun', '', 'code', 1, 'Always Stuns Enemies on Hit', 'Every hit stuns, whatever its damage.'),
('knockback_chance', '%', 'code', 1, '#% chance to Knock Enemies Back on hit', 'The chance a hit knocks its enemy back.')
ON CONFLICT(id) DO NOTHING;
-- A condition that is part of the hit (attack, spell, critical strike) is in the note: the hit code asks it.
INSERT INTO fx_modifiers (stat, ord, quantity, how, note) VALUES
('life_gained_on_block', 0, 'life_gained_on_block', 'flat', NULL),
('recover_X_life_on_block', 0, 'life_gained_on_block', 'flat', NULL),
('recover_%_of_maximum_life_on_block', 0, 'life_%_recovered_on_block', 'flat', NULL),
('mana_gained_on_block', 0, 'mana_gained_on_block', 'flat', NULL),
('mana_%_gained_on_block', 0, 'mana_%_recovered_on_block', 'flat', NULL),
('energy_shield_gained_on_block', 0, 'energy_shield_gained_on_block', 'flat', NULL),
('energy_shield_%_gained_on_block', 0, 'energy_shield_%_recovered_on_block', 'flat', NULL),
('energy_shield_%_of_armour_rating_gained_on_block', 0, 'energy_shield_from_armour_on_block', 'flat', NULL),
('base_chance_to_poison_on_hit_%', 0, 'poison_chance', 'flat', NULL),
('chance_to_poison_on_hit_with_attacks_%', 0, 'poison_chance', 'flat', 'Counts for an attack hit only.'),
('spells_chance_to_poison_on_hit_%', 0, 'poison_chance', 'flat', 'Counts for a spell hit only.'),
('global_poison_on_hit', 0, 'poison_chance', 'override', 'Poison on Hit: the chance is 100%.'),
('bleed_on_hit_with_attacks_%', 0, 'bleed_chance', 'flat', 'Counts for an attack hit only.'),
('bleed_on_crit_%_with_attacks', 0, 'bleed_chance', 'flat', 'Counts for an attack hit that is a critical strike.'),
('base_chance_to_freeze_%', 0, 'freeze_chance', 'flat', NULL),
('impale_on_hit_%_chance', 0, 'impale_chance', 'flat', NULL),
('spells_impale_on_hit_%_chance', 0, 'impale_chance', 'flat', 'Counts for a spell hit only.'),
('spell_impale_on_crit_%_chance', 0, 'impale_chance', 'flat', 'Counts for a spell hit that is a critical strike.'),
('always_stun', 0, 'always_stun', 'flag', NULL),
('base_global_chance_to_knockback_%', 0, 'knockback_chance', 'flat', NULL),
('knockback_on_crit_with_bow', 0, 'knockback_chance', 'override', 'A critical strike with a bow always knocks back.')
ON CONFLICT(stat, ord) DO NOTHING;
INSERT INTO fx_stats (stat, kind, handled, ref) VALUES
('life_gained_on_block', 'trigger', 'modifier', 'life_gained_on_block flat'),
('recover_X_life_on_block', 'trigger', 'modifier', 'life_gained_on_block flat'),
('recover_%_of_maximum_life_on_block', 'trigger', 'modifier', 'life_%_recovered_on_block flat'),
('mana_gained_on_block', 'trigger', 'modifier', 'mana_gained_on_block flat'),
('mana_%_gained_on_block', 'trigger', 'modifier', 'mana_%_recovered_on_block flat'),
('energy_shield_gained_on_block', 'trigger', 'modifier', 'energy_shield_gained_on_block flat'),
('energy_shield_%_gained_on_block', 'trigger', 'modifier', 'energy_shield_%_recovered_on_block flat'),
('energy_shield_%_of_armour_rating_gained_on_block', 'trigger', 'modifier', 'energy_shield_from_armour_on_block flat'),
('base_chance_to_poison_on_hit_%', 'trigger', 'modifier', 'poison_chance flat'),
('chance_to_poison_on_hit_with_attacks_%', 'trigger', 'modifier', 'poison_chance flat'),
('spells_chance_to_poison_on_hit_%', 'trigger', 'modifier', 'poison_chance flat'),
('global_poison_on_hit', 'trigger', 'modifier', 'poison_chance override'),
('bleed_on_hit_with_attacks_%', 'trigger', 'modifier', 'bleed_chance flat'),
('bleed_on_crit_%_with_attacks', 'trigger', 'modifier', 'bleed_chance flat'),
('base_chance_to_freeze_%', 'trigger', 'modifier', 'freeze_chance flat'),
('impale_on_hit_%_chance', 'trigger', 'modifier', 'impale_chance flat'),
('spells_impale_on_hit_%_chance', 'trigger', 'modifier', 'impale_chance flat'),
('spell_impale_on_crit_%_chance', 'trigger', 'modifier', 'impale_chance flat'),
('always_stun', 'trigger', 'modifier', 'always_stun flag'),
('base_global_chance_to_knockback_%', 'trigger', 'modifier', 'knockback_chance flat'),
('knockback_on_crit_with_bow', 'trigger', 'modifier', 'knockback_chance override')
ON CONFLICT(stat) DO UPDATE SET handled = 'modifier', ref = excluded.ref;
INSERT INTO fx_rules (id, book, title, status, origin, note) VALUES
('recover_on_block', 'recovery', 'Life, mana and energy shield recovered when you Block', 'not built', 'code', 'Each block spends the amounts: the arithmetic is BlockRecovery.cs and the block code. Needs the recover action.'),
('poison_on_hit', 'ailments', 'A hit''s chance to Poison', 'not built', 'code', 'The poison code rolls the chance and builds the poison from the hit''s damage. Needs an ailment action that takes the hit''s damage.'),
('bleed_on_hit', 'ailments', 'An attack hit''s chance to cause Bleeding', 'not built', 'code', 'The bleed code rolls the chance and builds the bleed from the hit''s physical damage.'),
('freeze_on_hit', 'ailments', 'A hit''s chance to Freeze', 'not built', 'code', 'The elemental ailment code rolls the chance; the freeze''s length comes from the hit''s cold damage.'),
('impale_on_hit', 'ailments', 'A hit''s chance to Impale', 'not built', 'code', 'The impale code rolls the chance and stores a share of the hit''s physical damage.'),
('stun_on_hit', 'ailments', 'Always Stuns Enemies on Hit', 'not built', 'code', 'The hit reaction code decides the stun; the flag skips its threshold.'),
('knockback_on_hit', 'ailments', 'A hit''s chance to Knock Enemies Back', 'not built', 'code', 'The knockback code rolls the chance and moves the monster away from the hit''s origin.');
INSERT INTO fx_rule_sources (rule, ord, kind, key) VALUES
('recover_on_block', 0, 'always', ''),
('poison_on_hit', 0, 'always', ''),
('bleed_on_hit', 0, 'always', ''),
('freeze_on_hit', 0, 'always', ''),
('impale_on_hit', 0, 'always', ''),
('stun_on_hit', 0, 'always', ''),
('knockback_on_hit', 0, 'always', '');
INSERT INTO fx_rule_when (rule, ord, trigger, args) VALUES
('recover_on_block', 0, 'block', '{}'),
('poison_on_hit', 0, 'hit', '{}'),
('bleed_on_hit', 0, 'hit', '{}'),
('freeze_on_hit', 0, 'hit', '{}'),
('impale_on_hit', 0, 'hit', '{}'),
('stun_on_hit', 0, 'hit', '{}'),
('knockback_on_hit', 0, 'hit', '{}');
INSERT INTO fx_rule_do (rule, ord, action, args, target, target_args, value, timers) VALUES
('recover_on_block', 0, 'recover', '{"pool":"life"}', 'self', '{}', '{"from":"quantity","flat":"life_gained_on_block","percent_of_maximum":"life_%_recovered_on_block"}', '[]'),
('recover_on_block', 1, 'recover', '{"pool":"mana"}', 'self', '{}', '{"from":"quantity","flat":"mana_gained_on_block","percent_of_maximum":"mana_%_recovered_on_block"}', '[]'),
('recover_on_block', 2, 'recover', '{"pool":"energy shield"}', 'self', '{}', '{"from":"quantity","flat":"energy_shield_gained_on_block","percent_of_maximum":"energy_shield_%_recovered_on_block","percent_of_armour":"energy_shield_from_armour_on_block"}', '[]'),
('poison_on_hit', 0, 'debuff', '{"debuff":"poison"}', 'enemy', '{}', '{"from":"quantity","chance":"poison_chance"}', '[]'),
('bleed_on_hit', 0, 'debuff', '{"debuff":"bleeding"}', 'enemy', '{}', '{"from":"quantity","chance":"bleed_chance"}', '[]'),
('freeze_on_hit', 0, 'debuff', '{"debuff":"freeze"}', 'enemy', '{}', '{"from":"quantity","chance":"freeze_chance"}', '[]'),
('impale_on_hit', 0, 'debuff', '{"debuff":"impale"}', 'enemy', '{}', '{"from":"quantity","chance":"impale_chance"}', '[]'),
('stun_on_hit', 0, 'debuff', '{"debuff":"stun"}', 'enemy', '{}', '{"from":"quantity","flag":"always_stun"}', '[]'),
('knockback_on_hit', 0, 'debuff', '{"debuff":"knockback"}', 'enemy', '{}', '{"from":"quantity","chance":"knockback_chance"}', '[]');
INSERT INTO fx_stats (stat, kind, handled, why, fit) VALUES
('gain_elusive_on_crit_%_chance', 'trigger', 'code', 'Elusive keeps its chances and effect as read when the stats are built, cannot be refreshed while it runs, and shares its roll with Nightblade''s own chance.', 'Rules on kill and on a critical skill_hit with a new Elusive buff action that takes the effect and the Power Charge loss.'),
('gain_elusive_on_kill_chance_%', 'trigger', 'code', 'Elusive keeps its chances and effect as read when the stats are built, cannot be refreshed while it runs, and shares its roll with Nightblade''s own chance.', 'Rules on kill and on a critical skill_hit with a new Elusive buff action that takes the effect and the Power Charge loss.'),
('lose_a_power_charge_when_you_gain_elusive_%_chance', 'trigger', 'code', 'Elusive keeps its chances and effect as read when the stats are built, cannot be refreshed while it runs, and shares its roll with Nightblade''s own chance.', 'Rules on kill and on a critical skill_hit with a new Elusive buff action that takes the effect and the Power Charge loss.'),
('chill_attackers_for_4_seconds_on_block_%_chance', 'trigger', 'code', 'Rolled where a monster''s hit lands on the character, which knows the attacker; the block and when_hit events do not carry the attacker yet.', 'Rules on block and when_hit with an ailment action on the trigger''s enemy (an ailment without damage, for a time), once those events carry the attacker.'),
('shock_attackers_for_4_seconds_on_block_%_chance', 'trigger', 'code', 'Rolled where a monster''s hit lands on the character, which knows the attacker; the block and when_hit events do not carry the attacker yet.', 'Rules on block and when_hit with an ailment action on the trigger''s enemy (an ailment without damage, for a time), once those events carry the attacker.'),
('chill_enemy_when_hit_duration_ms', 'trigger', 'code', 'Rolled where a monster''s hit lands on the character, which knows the attacker; the block and when_hit events do not carry the attacker yet.', 'Rules on block and when_hit with an ailment action on the trigger''s enemy (an ailment without damage, for a time), once those events carry the attacker.'),
('chance_to_inflict_brittle_on_enemy_on_block_%', 'trigger', 'code', 'Rolled where a monster''s hit lands on the character, which knows the attacker; the block and when_hit events do not carry the attacker yet.', 'Rules on block and when_hit with an ailment action on the trigger''s enemy (an ailment without damage, for a time), once those events carry the attacker.'),
('chance_to_inflict_sap_on_enemy_on_block_%', 'trigger', 'code', 'Rolled where a monster''s hit lands on the character, which knows the attacker; the block and when_hit events do not carry the attacker yet.', 'Rules on block and when_hit with an ailment action on the trigger''s enemy (an ailment without damage, for a time), once those events carry the attacker.'),
('chance_to_inflict_scorch_on_enemy_on_block_%', 'trigger', 'code', 'Rolled where a monster''s hit lands on the character, which knows the attacker; the block and when_hit events do not carry the attacker yet.', 'Rules on block and when_hit with an ailment action on the trigger''s enemy (an ailment without damage, for a time), once those events carry the attacker.'),
('chance_to_freeze_enemies_for_1_second_when_hit_%', 'trigger', 'code', 'Rolled where a monster''s hit lands on the character, which knows the attacker; the block and when_hit events do not carry the attacker yet.', 'Rules on block and when_hit with an ailment action on the trigger''s enemy (an ailment without damage, for a time), once those events carry the attacker.'),
('fortify_on_hit', 'trigger', 'code', 'Fortification is built up from the hit''s damage against the monster''s life, in its own session.', 'A rule on a melee hit with a Fortification action that takes the hit''s damage.'),
('strike_skills_fortify_on_hit', 'trigger', 'code', 'Fortification is built up from the hit''s damage against the monster''s life, in its own session.', 'A rule on a melee hit with a Fortification action that takes the hit''s damage.'),
('melee_movement_skill_chance_to_fortify_on_hit_%', 'trigger', 'code', 'Fortification is built up from the hit''s damage against the monster''s life, in its own session.', 'A rule on a melee hit with a Fortification action that takes the hit''s damage.'),
('chance_to_taunt_on_hit_%', 'trigger', 'code', 'Rolled in the minion and character taunt code, which adds the minion''s own stats.', 'A rule on hit with the taunted debuff; the attacks-only stat is a summed term.'),
('attacks_chance_to_taunt_on_hit_%', 'trigger', 'code', 'Rolled in the minion and character taunt code, which adds the minion''s own stats.', 'A rule on hit with the taunted debuff; the attacks-only stat is a summed term.'),
('explode_enemies_for_10%_life_as_physical_on_kill_chance_%', 'trigger', 'code', 'The explosion reads what the character wore and the corpse''s state at the kill, captured before the kill''s other effects.', 'A rule on kill with a damage action around the corpse, taking a share of its life.'),
('explode_enemies_for_25%_life_as_chaos_on_kill_chance_%', 'trigger', 'code', 'The explosion reads what the character wore and the corpse''s state at the kill, captured before the kill''s other effects.', 'A rule on kill with a damage action around the corpse, taking a share of its life.'),
('explode_cursed_enemies_for_25%_life_as_chaos_on_kill_chance_%', 'trigger', 'code', 'The explosion reads what the character wore and the corpse''s state at the kill, captured before the kill''s other effects.', 'A rule on kill with a damage action around the corpse, taking a share of its life.'),
('gain_flask_chance_on_crit_%', 'trigger', 'code', 'Flask charges are counted in the flask session with its own fractions.', 'A rule on a critical hit with a flask charge action.'),
('gain_movement_speed_+%_for_20_seconds_on_kill', 'trigger', 'code', 'Slayer''s buffs keep their own timers and present through the ascendancy''s code.', 'Rules on kill with a timed buff whose value is the stat.'),
('gain_attack_speed_+%_for_20_seconds_on_killing_rare_or_unique_enemy', 'trigger', 'code', 'Slayer''s buffs keep their own timers and present through the ascendancy''s code.', 'Rules on kill with a timed buff whose value is the stat.')
ON CONFLICT(stat) DO UPDATE SET why = excluded.why, fit = excluded.fit;
