-- Share the existing Ice Bite rule. A world that removed it or repurposed it for player equipment keeps its setup.
INSERT INTO fx_rule_when (rule, ord, trigger, args)
SELECT w.rule, max(w.ord) + 1, 'kill', '{"who":"minion"}'
FROM fx_rule_when w
WHERE w.rule = 'charge_on_kill_vs_frozen_2_frenzy'
  AND EXISTS (SELECT 1 FROM fx_rule_do d WHERE d.rule = w.rule AND d.action = 'charge' AND d.target = 'self')
  AND NOT EXISTS (SELECT 1 FROM fx_rule_if i WHERE i.rule = w.rule AND i.condition = 'equipment')
GROUP BY w.rule
HAVING count(*) = 1 AND max(w.trigger) = 'kill';
