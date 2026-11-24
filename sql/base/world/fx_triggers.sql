-- When a rule fires: an event (a hit, a kill, a flask used, an area entered), or "active" for as long as its source lasts.
-- One row is one reusable part of the effect rules (Agents/docs/dev/effect-rules.md, "The fx tables"). id is the name
-- rules use it by. takes: the inputs it needs, in plain words. built = 1 once the game module has code for it; a row
-- with built = 0 is a part a rule may already name, listed as work to do. origin: the mod or place it was first
-- taken from. note: what it does exactly.
CREATE TABLE fx_triggers (
    id TEXT PRIMARY KEY,
    takes TEXT NOT NULL DEFAULT '',
    built INTEGER NOT NULL DEFAULT 0 CHECK (built IN (0, 1)),
    origin TEXT,
    note TEXT NOT NULL
) STRICT;
