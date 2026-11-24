-- Item-triggered spells whose trigger is a stat of the granted skill ("carrier":"skill"), released by the "skill"
-- effect kind. Which granted skills these rows fire depends on the executors (Game.RegisterTriggeredSkillExecutor):
-- today Lightning Bolt (Choir of the Storm, Voice of the Storm), Fire Burst on Hit (Essence of Hysteria) and Fire
-- Burst on Kill. The cooldown and its stored uses are the skill's own.
INSERT INTO effect_rules (stat, ord, rule, reviewed, note) VALUES
('cast_on_crit_%', 0,
 '{"carrier":"skill","when":"hit","checks":["critical"],"chance":{"from":"stat"},"cooldown":{"data":"cooldown"},"uses":{"data":"uses"},"do":[{"kind":"skill"}]}',
 1, 'Trigger this Spell when you deal a Critical Strike'),
('cast_on_hit_%', 0,
 '{"carrier":"skill","when":"hit","chance":{"from":"stat"},"cooldown":{"data":"cooldown"},"uses":{"data":"uses"},"do":[{"kind":"skill"}]}',
 1, 'Trigger this Spell when you Hit'),
('chance_to_cast_on_kill_%_target_self', 0,
 '{"carrier":"skill","when":"kill","chance":{"from":"stat"},"cooldown":{"data":"cooldown"},"uses":{"data":"uses"},"do":[{"kind":"skill","at":"self"}]}',
 1, 'Trigger this Spell on Kill (it targets its user: a nova around the character)');
