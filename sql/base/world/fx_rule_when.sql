-- A rule's triggers (fx_triggers ids). Any one of them fires the rule. args: the trigger's inputs as JSON.
CREATE TABLE fx_rule_when (
    rule TEXT NOT NULL,
    ord INTEGER NOT NULL DEFAULT 0,
    trigger TEXT NOT NULL,
    args TEXT NOT NULL DEFAULT '{}',
    PRIMARY KEY (rule, ord)
) STRICT;
