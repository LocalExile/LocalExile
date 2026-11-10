-- Unique drop weights (0..1, absolute: the rest of the range is no unique), normal and Ruthless.
CREATE TABLE unique_weights (
    name TEXT PRIMARY KEY,
    weight REAL NOT NULL CHECK (weight BETWEEN 0 AND 1),
    ruthless_weight REAL NOT NULL CHECK (ruthless_weight BETWEEN 0 AND 1)
) STRICT;
