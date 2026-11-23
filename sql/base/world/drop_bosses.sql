-- Bosses that have their own drops.
CREATE TABLE drop_bosses (
    boss_id TEXT PRIMARY KEY,
    boss_name TEXT NOT NULL,
    area_id TEXT,
    category TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1))
) STRICT;
