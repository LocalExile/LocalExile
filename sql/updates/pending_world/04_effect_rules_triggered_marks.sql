-- The first rows whose stat is on a skill an item grants ("carrier":"skill"), released by the "skill" effect kind.
-- "Trigger Level X Assassin's / Poacher's / Warlord's Mark when you Hit a Rare or Unique Enemy and have no Mark":
-- the trigger stat is the granted mark's own; its cooldown is the skill's (0.5 s).
INSERT INTO effect_rules (stat, ord, rule, reviewed, note) VALUES
('trigger_on_hit_against_rare_or_unique_if_no_marked_enemy', 0,
 '{"carrier":"skill","when":"hit","checks":["rare_or_unique","!has_mark"],"cooldown":{"data":"cooldown"},"do":[{"kind":"skill"}]}',
 1, 'Trigger Level X <Mark> when you Hit a Rare or Unique Enemy and have no Mark');
