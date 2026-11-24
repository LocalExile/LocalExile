-- What a rule does, in order. action: an fx_actions id with its inputs (args, JSON). target: an fx_targets id with
-- its inputs (target_args: a radius, a count). value: where the amount comes from, JSON over fx_values
-- ({"from": "roll"}, {"from": "stat", "stat": ..., "scale": ...}, {"fixed": 4}); empty when the action has none.
-- timers: a JSON list over fx_timers ({"timer": "duration", "seconds": {...}}).
CREATE TABLE fx_rule_do (
    rule TEXT NOT NULL,
    ord INTEGER NOT NULL DEFAULT 0,
    action TEXT NOT NULL,
    args TEXT NOT NULL DEFAULT '{}',
    target TEXT NOT NULL DEFAULT 'self',
    target_args TEXT NOT NULL DEFAULT '{}',
    value TEXT NOT NULL DEFAULT '{}',
    timers TEXT NOT NULL DEFAULT '[]',
    PRIMARY KEY (rule, ord)
) STRICT;
