-- The released 2026_11_11_063112 update creates the public table on fresh databases.
-- The follow-up update installs this schema under that public name after preserving operator settings.
CREATE TABLE encounter_jewel_drop_overrides_schema (
    boss_id TEXT NOT NULL,
    item_id TEXT NOT NULL,
    chance REAL NOT NULL CHECK (chance >= 0 AND chance <= 1),
    PRIMARY KEY (boss_id, item_id)
) STRICT;
