-- Death Aura: one row for both. unique_deaths_oath_aura on the enemies inside (its shadow effect is theirs) and on the
-- wearer as the aura's source (its aura effect). As an ordinary buff the wearer got the enemies' shadows (seen 2026-11-23).
UPDATE effect_skills SET art = 'unique_deaths_oath_aura,unique_deaths_oath_aura',
    note = 'Death Aura (Death''s Oath): its chaos damage per minute to enemies within 30 cells, a guess (the stat set has no radius). art: the buff on the enemies inside, then the buff the wearer states as the aura''s source. The character''s damage-over-time modifiers are not applied'
WHERE effect = 'DeathAura';
-- Abberath's Fury: fitted by the art's assumed size the ring still reached about twice as far as the 18 cells it hits
-- within (measured against the character in a recording, 2026-11-23). An own scale instead, about half the fitted one.
UPDATE effect_skills SET scale = 0.42,
    note = 'Abberath''s Fury (Abberath''s Hooves): a burst around the character every half second while moving. Art, radius 18 and the period are guesses; scale 0.42 measured from a recording so the ring ends about where the hit does'
WHERE effect = 'RepeatingShockwave';
