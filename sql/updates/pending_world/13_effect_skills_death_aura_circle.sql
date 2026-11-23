-- Death Aura: a circle on the ground at the aura's radius (art's third part), so its edge can be seen. The buff's own
-- aura effect is only a small swirl on the character (seen 2026-11-23). The circle's art is a guess: the Ancestral
-- deathcaster totem's chaos aura, whose size the client's table gives (275 units), so it is drawn at the radius.
UPDATE effect_skills SET art = 'unique_deaths_oath_aura,unique_deaths_oath_aura,AncestralTotemChaosDegen',
    note = 'Death Aura (Death''s Oath): its chaos damage per minute to enemies within 30 cells, a guess (the stat set has no radius). art: the buff on the enemies inside, the buff the wearer states as the aura''s source, the circle drawn at the radius (a guess). The character''s damage-over-time modifiers are not applied'
WHERE effect = 'DeathAura';
