-- The stats the first effect rules (effect_rules) already rebuild: handled by a rule, not only by the older code.
-- Some are not carried by a Mods row or a passive (a skill-granting mod's own key), so they get their row here.
INSERT INTO fx_stats (stat, kind, handled, ref)
SELECT DISTINCT stat, 'trigger', 'rule', 'effect_rules' FROM effect_rules WHERE enabled = 1 AND reviewed = 1
ON CONFLICT(stat) DO UPDATE SET handled = 'rule', ref = 'effect_rules';
