-- An aura or buff skill's own "increased effect" stat. The code has this list for fifteen auras (AuraCatalog); these
-- rows add to it: the five heralds ("Herald of Ash has #% increased Buff Effect"), Arctic Armour and Tempest
-- Shield. The stat counts for the skill wherever its aura's values are worked out; a skill that does not run as an
-- aura on this server is not reached by its row. 7 stats on 166 lines that no code read.
CREATE TABLE fx_aura_effects (
    family TEXT NOT NULL,
    ord INTEGER NOT NULL DEFAULT 0,
    stat TEXT NOT NULL,
    note TEXT,
    PRIMARY KEY (family, ord)
) STRICT;
INSERT INTO fx_aura_effects (family, ord, stat) VALUES
('HeraldOfAsh', 0, 'herald_of_ash_buff_effect_+%'),
('HeraldOfIce', 0, 'herald_of_ice_buff_effect_+%'),
('HeraldOfThunder', 0, 'herald_of_thunder_buff_effect_+%'),
('HeraldOfAgony', 0, 'herald_of_agony_buff_effect_+%'),
('HeraldOfPurity', 0, 'herald_of_light_buff_effect_+%'),
('ArcticArmour', 0, 'arctic_armour_buff_effect_+%'),
('TempestShield', 0, 'tempest_shield_buff_effect_+%');
UPDATE fx_stats SET handled = 'modifier', ref = 'fx_aura_effects: its skill''s own increased effect', why = NULL, fit = NULL
WHERE stat IN ('herald_of_ash_buff_effect_+%', 'herald_of_ice_buff_effect_+%', 'herald_of_thunder_buff_effect_+%', 'herald_of_agony_buff_effect_+%', 'herald_of_light_buff_effect_+%', 'arctic_armour_buff_effect_+%', 'tempest_shield_buff_effect_+%');
