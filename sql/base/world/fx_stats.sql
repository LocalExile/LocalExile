-- Every stat a mod, passive or monster can carry, and where it stands in the effect rules.
-- kind: the first sort of what the stat is (number, conditional number, trigger, grants a skill, item property, area,
-- monster, flag, other), from its name and tooltip text; a guess until checked = 1.
-- handled: modifier (fx_modifiers), rule (fx_rules; ref names it), item (a property of the item itself, worked out
-- when the item is made), code (the game module reads it and no rule replaces it yet), display (only changes text),
-- deferred (belongs to a later phase; why says which), none (nothing reads it).
-- code: the files that read the stat today. mods: how many Mods rows carry it. text: its tooltip line.
-- why and fit: for a stat not rebuilt, what is in the way and how it could fit.
CREATE TABLE fx_stats (
    stat TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    handled TEXT NOT NULL CHECK (handled IN ('modifier', 'rule', 'item', 'code', 'display', 'deferred', 'none')),
    ref TEXT,
    code TEXT,
    mods INTEGER NOT NULL DEFAULT 0,
    text TEXT,
    why TEXT,
    fit TEXT,
    checked INTEGER NOT NULL DEFAULT 0 CHECK (checked IN (0, 1))
) STRICT;
CREATE INDEX fx_stats_handled ON fx_stats (handled, kind);
