-- Where a divination card drops: an area, a boss or a league.
CREATE TABLE drop_card_sources (
    card_id TEXT NOT NULL,
    source_kind TEXT NOT NULL CHECK (source_kind IN ('area', 'boss', 'league')),
    source_id TEXT NOT NULL,
    min_map_tier INTEGER CHECK (min_map_tier IS NULL OR min_map_tier BETWEEN 1 AND 16),
    drop_level INTEGER NOT NULL CHECK (drop_level >= 0),
    confidence TEXT NOT NULL CHECK (confidence IN ('verified', 'estimated', 'unknown')),
    source TEXT NOT NULL,
    notes TEXT,
    PRIMARY KEY (card_id, source_kind, source_id)
) STRICT;
