-- Drop weights within a category (scarabs, cards, essences, ...).
CREATE TABLE drop_item_weights (
    category TEXT NOT NULL CHECK (category IN ('scarab', 'card', 'essence', 'fossil', 'catalyst', 'delirium-orb', 'breachstone', 'legion-splinter', 'runegraft')),
    item_id TEXT NOT NULL,
    weight REAL NOT NULL CHECK (weight >= 0),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    source TEXT NOT NULL,
    confidence TEXT NOT NULL CHECK (confidence IN ('verified', 'estimated', 'unknown')),
    patch TEXT NOT NULL,
    sampled_at TEXT NOT NULL,
    notes TEXT,
    PRIMARY KEY (category, item_id)
) STRICT;
