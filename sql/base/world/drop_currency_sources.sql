-- Where a currency drops besides the core table.
CREATE TABLE drop_currency_sources (
    currency_id TEXT NOT NULL,
    source_kind TEXT NOT NULL CHECK (source_kind IN ('core', 'boss', 'league', 'atlas', 'vendor')),
    source_id TEXT NOT NULL,
    weight REAL NOT NULL CHECK (weight >= 0),
    drop_level INTEGER NOT NULL CHECK (drop_level >= 0),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    confidence TEXT NOT NULL CHECK (confidence IN ('verified', 'estimated', 'unknown')),
    source TEXT NOT NULL,
    notes TEXT,
    PRIMARY KEY (currency_id, source_kind, source_id)
) STRICT;
