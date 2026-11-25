-- Item skills that make a ground effect (how "ground"; art is the ground's kind). The radius is a guess: the game's
-- data states none for these two skills.
INSERT INTO effect_skills (effect, how, art, seconds, radius, note) VALUES
  ('TriggeredConsecrate', 'ground', 'ConsecratedGround', 5, 14, 'Consecrate on critical strike (Gifts from Above): consecrated ground where the strike landed, for the skill''s 5 seconds. Radius a guess'),
  ('TriggeredShockedGround', 'ground', 'ShockedGround', 5, 14, 'Shock Ground when hit (Ylfeban''s Trickery): shocked ground under the character; enemies on it are Shocked at 15%. Radius a guess');
UPDATE fx_stats SET handled = 'rule', ref = 'the granted skill''s own trigger (GrantedSkillRules, how ground)', why = NULL, fit = 'approximate'
WHERE stat IN ('local_display_cast_level_X_consecrate_on_crit', 'local_display_cast_level_x_shock_ground_when_hit');
