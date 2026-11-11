-- A boss's own drops.
CREATE TABLE drop_boss_items (
    boss_id TEXT NOT NULL,
    item_id TEXT NOT NULL,
    item_kind TEXT NOT NULL CHECK (item_kind IN ('unique', 'currency', 'gem', 'card', 'other')),
    chance REAL CHECK (chance IS NULL OR (chance >= 0 AND chance <= 1)),
    confidence TEXT NOT NULL CHECK (confidence IN ('verified', 'estimated', 'unknown')),
    source TEXT NOT NULL,
    notes TEXT,
    PRIMARY KEY (boss_id, item_id)
) STRICT;
