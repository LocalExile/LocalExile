-- Currency drop weights (Agents/docs/dev/drop-data-schema.md).
CREATE TABLE drop_currency_weights (
    currency_id TEXT PRIMARY KEY,
    weight REAL NOT NULL CHECK (weight >= 0),
    drop_level INTEGER NOT NULL CHECK (drop_level >= 0),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    source TEXT NOT NULL,
    confidence TEXT NOT NULL CHECK (confidence IN ('verified', 'estimated', 'unknown')),
    notes TEXT
) STRICT;
