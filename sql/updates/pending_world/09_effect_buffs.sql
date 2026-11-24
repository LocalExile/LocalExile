-- Item-triggered skills that give their user a buff (the "buff" executor), and the trigger of Shadowstitch's Shade Form.
INSERT INTO effect_buffs (effect, buff, routine, seconds, stats, checked, note) VALUES
('JunTriggeredBloodRage', NULL, 'BloodRage', NULL, '{}', 0,
 'Blood Rage on kill (a veiled weapon mod): the gem''s own routine with the item skill''s numbers'),
('ShadeForm', 'shade_form', NULL, NULL, '{"physical_immunity":1,"stealth_+%":"stealth_+%"}', 0,
 'Shade Form (Shadowstitch). Its cooldown should not recover while the buff lasts: not modelled'),
('ShadeFormWhenHit', 'shade_form', NULL, NULL, '{"physical_immunity":1,"stealth_+%":"stealth_+%"}', 0,
 'Shade Form when hit. Its cooldown should not recover while the buff lasts: not modelled');

INSERT INTO effect_rules (stat, ord, rule, reviewed, note) VALUES
('trigger_on_skill_use_from_chest_%', 0,
 '{"carrier":"skill","when":"skill_use","chance":{"from":"stat"},"cooldown":{"data":"cooldown"},"uses":{"data":"uses"},"do":[{"kind":"skill","at":"self"}]}',
 1, 'Trigger this skill when you use a Socketed Skill. Fires for any skill use: there is no fact yet for a skill socketed in the item');
