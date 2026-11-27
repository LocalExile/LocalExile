-- Always-on item skills (cast_on_gain_skill): a "worn" rule stands while the item is worn, and its skill is on as its
-- effect_skills row says. how "aura": damage over time to the enemies within radius, art = the buff they wear
-- (BuffDefinitions Id). how "pulse": the skill goes off around the character every "every" seconds while it moves.
-- how "worn": the skill only carries another "worn" rule's effects. Every radius and period is a best guess.
INSERT INTO effect_skills (effect, how, art, scale, seconds, radius, hits, every, spread, checked, note) VALUES
('DeathAura', 'aura', 'monster_aura_chaos_degen', 1, 2, 30, 1, 0, 0, 0,
 'Death Aura (Death''s Oath): its chaos damage per minute to enemies within 30 cells, a guess (the stat set has no radius). The character''s damage-over-time modifiers are not applied'),
('RepeatingShockwave', 'pulse', 'FireNovaEnchantment', 1, 2, 18, 1, 0.5, 0, 0,
 'Abberath''s Fury (Abberath''s Hooves): a burst around the character every half second while moving. Art, radius 18 and the period are guesses'),
('BlindingAura', 'worn', NULL, 1, 2, NULL, 1, 0, 0, 0,
 'Blinding Aura: its rule gives virtual_nearby_enemies_are_blinded, which blinds within 35 cells; light radius does not grow it yet');

INSERT INTO effect_rules (stat, ord, rule, reviewed, note) VALUES
('cast_on_gain_skill', 0,
 '{"carrier":"skill","when":"worn","do":[{"kind":"skill","at":"self"}]}',
 1, 'A skill that is on while its item is worn: as its effect_skills row has it (aura, pulse)'),
('cast_on_gain_skill', 1,
 '{"carrier":"skill","when":"worn","only":"BlindingAura","do":[{"kind":"stats","stats":{"local_display_nearby_enemies_are_blinded":1,"virtual_nearby_enemies_are_blinded":1}}]}',
 1, 'Blinding Aura: nearby enemies are blinded while it is worn');
