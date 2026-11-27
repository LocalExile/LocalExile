-- A trigger the skill's own stats do not name (skill_has_trigger_from_unique_item marks many skills, each with its
-- item's own trigger): a row for one skill only ("only").
INSERT INTO effect_skills (effect, how, art, scale, seconds, radius, hits, every, spread, checked, note) VALUES
('FieryImpactHeistMaceImplicit', 'area', 'InfernalCryOnHitExplodeEffect', 1, 2, 15, 1, 0, 0, 0,
 'Fiery Impact (the heist maces'' implicit): an attack with the weapon around the target. Art: Infernal Cry''s on-hit explosion (the skill''s action is that one); radius a guess');

INSERT INTO effect_rules (stat, ord, rule, reviewed, note) VALUES
('skill_has_trigger_from_unique_item', 0,
 '{"carrier":"skill","only":"FieryImpactHeistMaceImplicit","when":"hit","checks":["melee"],"cooldown":{"data":"cooldown"},"uses":{"data":"uses"},"do":[{"kind":"skill"}]}',
 1, 'Trigger Level X Fiery Impact on Melee Hit with this Weapon (any melee hit for now: a hit does not say which weapon made it)');
