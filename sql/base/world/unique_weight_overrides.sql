-- A unique's drop weights in place of unique_weights.
CREATE TABLE unique_weight_overrides (
    name TEXT PRIMARY KEY,
    weight REAL NOT NULL CHECK (weight BETWEEN 0 AND 1),
    ruthless_weight REAL NOT NULL CHECK (ruthless_weight BETWEEN 0 AND 1)
) STRICT;
