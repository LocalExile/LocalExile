-- Keep installed pilot databases and their edited rows when widening the allowed event/action pairs.
ALTER TABLE item_effect_rules RENAME TO item_effect_rules_before_block;
CREATE TABLE item_effect_rules (
    id INTEGER PRIMARY KEY,
    stat TEXT NOT NULL,
    event TEXT NOT NULL,
    action TEXT NOT NULL,
    chance_from_stat INTEGER NOT NULL DEFAULT 0 CHECK (chance_from_stat IN (0, 1)),
    duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (duration_ms BETWEEN 0 AND 2147483647),
    duration_stat TEXT,
    rare_or_unique INTEGER NOT NULL DEFAULT 0 CHECK (rare_or_unique IN (0, 1)),
    requires_no_mark INTEGER NOT NULL DEFAULT 0 CHECK (requires_no_mark IN (0, 1)),
    UNIQUE (stat, event, action),
    CHECK ((event = 'kill' AND action = 'onslaught' AND rare_or_unique = 0 AND requires_no_mark = 0
            AND (duration_ms > 0 OR duration_stat IS NOT NULL))
        OR (event = 'hit' AND action = 'cast_granted_mark' AND chance_from_stat = 0
            AND duration_ms = 0 AND duration_stat IS NULL)
        OR (event = 'block' AND action = 'cast_supported_socketed_spell' AND chance_from_stat = 0
            AND duration_ms = 0 AND duration_stat IS NULL AND rare_or_unique = 0 AND requires_no_mark = 0))
) STRICT;
INSERT INTO item_effect_rules SELECT * FROM item_effect_rules_before_block;
DROP TABLE item_effect_rules_before_block;
