-- Effect rules: a stat that turns a rule on for an actor, the event that fires it, and what it does
-- (Modules/Core/Game/Effects/EffectRules.cs reads the rule's JSON). Several rows may share a stat; ord orders them.
-- Only rows with reviewed = 1 and enabled = 1 load.
CREATE TABLE effect_rules (
    stat TEXT NOT NULL,
    ord INTEGER NOT NULL DEFAULT 0,
    rule TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    reviewed INTEGER NOT NULL DEFAULT 0 CHECK (reviewed IN (0, 1)),
    note TEXT,
    PRIMARY KEY (stat, ord)
) STRICT;
