-- A rule's conditions (fx_conditions ids), in groups: every condition of a group must hold, and any one group is
-- enough. negate = 1 turns one around ("have no mark"). args: the condition's inputs as JSON.
CREATE TABLE fx_rule_if (
    rule TEXT NOT NULL,
    grp INTEGER NOT NULL DEFAULT 0,
    ord INTEGER NOT NULL DEFAULT 0,
    condition TEXT NOT NULL,
    negate INTEGER NOT NULL DEFAULT 0 CHECK (negate IN (0, 1)),
    args TEXT NOT NULL DEFAULT '{}',
    PRIMARY KEY (rule, grp, ord)
) STRICT;
