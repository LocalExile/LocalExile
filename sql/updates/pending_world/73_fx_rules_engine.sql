-- Two engines read the fx rule tables: main's world rules (Modules/Core/Game/Combat/ItemEffectRules.cs, which
-- refuses a row it cannot compile) and the effect-rules engine (Modules/Core/Game/Effects/FxRules.cs). engine says
-- whose a rule is: NULL for main's (its updates insert without the column), 'effects' for ours. Every rule before this
-- file is ours but the ones main's updates add; a new rule of ours names engine = 'effects'.
ALTER TABLE fx_rules ADD COLUMN engine TEXT CHECK (engine IS NULL OR engine = 'effects');

UPDATE fx_rules SET engine = 'effects'
WHERE id NOT IN ('onslaught_1_chance', 'onslaught_2_duration', 'onslaught_3_four_seconds', 'onslaught_4_ten_seconds',
    'granted_mark', 'socketed_spell_on_block', 'charge_on_kill_endurance', 'charge_on_kill_frenzy', 'charge_on_kill_power',
    'charge_on_kill_shield', 'charge_on_kill_dual_wield', 'charge_on_kill_vs_frozen_1_power',
    'charge_on_kill_vs_frozen_2_frenzy', 'maligaro_poison_frenzy', 'maligaro_poison_power', 'frenzy_on_skill_hit',
    'power_on_hit', 'power_on_skill_hit');
