-- What a Grand Heist curio display offers, with how often each was seen (reference data for the Heist rewards).
CREATE TABLE heist_curio_rewards (
    name TEXT PRIMARY KEY,
    category TEXT NOT NULL,
    rarity_tier REAL NOT NULL CHECK (rarity_tier > 0),
    seen INTEGER NOT NULL CHECK (seen >= 0),
    source TEXT NOT NULL,
    confidence TEXT NOT NULL CHECK (confidence IN ('verified', 'estimated', 'unknown')),
    notes TEXT
) STRICT;
