-- Negative positions run before the legacy additional grants (including the random charge);
-- positive positions run after them. Each enabled charge rule needs its own nonzero position.
ALTER TABLE fx_rules ADD COLUMN kill_charge_order INTEGER CHECK (kill_charge_order <> 0);

-- Capture the old order once at upgrade, including existing custom and disabled rules.
WITH charge_groups AS (
    SELECT r.id, EXISTS (
        SELECT 1 FROM fx_rule_if i WHERE i.rule = r.id AND i.condition IN ('equipment', 'enemy_frozen_at_death')
    ) AS conditional
    FROM fx_rules r
    WHERE EXISTS (SELECT 1 FROM fx_rule_when w WHERE w.rule = r.id AND w.trigger = 'kill')
      AND EXISTS (SELECT 1 FROM fx_rule_do d WHERE d.rule = r.id AND d.action = 'charge' AND d.target = 'self')
), positions AS (
    SELECT id, CASE WHEN conditional THEN 10 * row_number() OVER (PARTITION BY conditional ORDER BY id)
                    ELSE -10 * row_number() OVER (PARTITION BY conditional ORDER BY id DESC) END AS position
    FROM charge_groups
)
UPDATE fx_rules SET kill_charge_order = (SELECT position FROM positions WHERE positions.id = fx_rules.id)
WHERE id IN (SELECT id FROM positions);

CREATE UNIQUE INDEX fx_kill_charge_order ON fx_rules (kill_charge_order)
WHERE enabled = 1 AND kill_charge_order IS NOT NULL;
