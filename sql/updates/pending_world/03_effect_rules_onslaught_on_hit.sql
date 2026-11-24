-- "You gain Onslaught for X seconds on Hit" (the Honoured Berserker tattoo and other sources) as a row: every hit of
-- the character's own (its skills, brands, heralds and mirages), from the one hit event. A totem's, trap's or mine's
-- hit is not the character's and gives nothing.
INSERT INTO effect_rules (stat, ord, rule, reviewed, note) VALUES
('gain_onslaught_on_hit_duration_ms', 0,
 '{"when":"hit","do":[{"kind":"onslaught","seconds":{"from":"stat","scale":0.001}}]}',
 1, 'You gain Onslaught for X seconds on Hit');
