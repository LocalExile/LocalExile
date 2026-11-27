-- The short form of a rule for a plain number: while the actor has the stat, the stat's value changes a quantity.
-- how: flat (added), increased (summed %, then multiplied), more (each multiplied), override (replaces), minimum,
-- maximum, flag (on while the value is not 0). scale: the stat's value times this is the amount (0.001 for a stat in
-- milliseconds feeding seconds). conditions: a JSON list of {"if": an fx_conditions id, ...its inputs}; all must hold.
-- target: an fx_targets id, the actor itself unless said. Several rows may share a stat (a keystone's lines); ord
-- orders them. Only rows with enabled = 1 load; checked = 1 once compared with the real game.
CREATE TABLE fx_modifiers (
    stat TEXT NOT NULL,
    ord INTEGER NOT NULL DEFAULT 0,
    quantity TEXT NOT NULL,
    how TEXT NOT NULL CHECK (how IN ('flat', 'increased', 'more', 'override', 'minimum', 'maximum', 'flag')),
    scale REAL NOT NULL DEFAULT 1,
    conditions TEXT NOT NULL DEFAULT '[]',
    target TEXT NOT NULL DEFAULT 'self',
    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    checked INTEGER NOT NULL DEFAULT 0 CHECK (checked IN (0, 1)),
    note TEXT,
    PRIMARY KEY (stat, ord)
) STRICT;
