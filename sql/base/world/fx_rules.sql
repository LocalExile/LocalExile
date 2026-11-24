-- One effect rule: something switches it on (fx_rule_sources), a trigger fires it (fx_rule_when), its conditions
-- hold (fx_rule_if) and it does its actions in order (fx_rule_do). book: a label to sort by (items, flasks, maps,
-- currency, monsters, passives); parts are shared across books. replaces: the stat or name the older code handles
-- this by, when there is such code: with rules on that code is skipped, with rules off it runs and the rule does not.
-- status: built (every part it names has code), partial, or not built. Only rows with enabled = 1 load; checked = 1
-- once the rule was compared with the real game. origin: the mod, item or place it was taken from.
CREATE TABLE fx_rules (
    id TEXT PRIMARY KEY,
    book TEXT NOT NULL,
    title TEXT NOT NULL,
    replaces TEXT,
    status TEXT NOT NULL DEFAULT 'not built' CHECK (status IN ('built', 'partial', 'not built')),
    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    checked INTEGER NOT NULL DEFAULT 0 CHECK (checked IN (0, 1)),
    origin TEXT,
    note TEXT
) STRICT;
