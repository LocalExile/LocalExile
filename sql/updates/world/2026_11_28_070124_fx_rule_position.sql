-- One position per rule: where it rolls among the rules of its event, for every actor it serves.
-- The compiler refuses two enabled rules at the same position for the same event and actor.
DROP INDEX fx_kill_charge_order;
ALTER TABLE fx_rules RENAME COLUMN kill_charge_order TO position;
UPDATE fx_rules SET position = (SELECT min(w.roll_order) FROM fx_rule_when w WHERE w.rule = fx_rules.id)
WHERE position IS NULL AND EXISTS (SELECT 1 FROM fx_rule_when w WHERE w.rule = fx_rules.id AND w.roll_order IS NOT NULL);
ALTER TABLE fx_rule_when DROP COLUMN roll_order;
