-- Death Aura: the game's own buffs. The enemies inside wear unique_deaths_oath_debuff, and the wearer shows
-- unique_deaths_oath_aura, whose visual is the aura around the character (it had none: seen 2026-11-23).
UPDATE effect_skills SET art = 'unique_deaths_oath_debuff,unique_deaths_oath_aura',
    note = 'Death Aura (Death''s Oath): its chaos damage per minute to enemies within 30 cells, a guess (the stat set has no radius). art: the enemies'' buff, then the wearer''s (its visual is the aura). The character''s damage-over-time modifiers are not applied'
WHERE effect = 'DeathAura';
-- Abberath's Fury: the art was drawn at its own size, much wider than the 18 cells it hits within (seen 2026-11-23).
-- scale 0 fits the art to the radius.
UPDATE effect_skills SET scale = 0,
    note = 'Abberath''s Fury (Abberath''s Hooves): a burst around the character every half second while moving. Art, radius 18 and the period are guesses; scale 0 fits the art to the radius'
WHERE effect = 'RepeatingShockwave';
