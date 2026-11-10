-- Item limits by key (timeless_jewel, impossible_escape, radius:<name>, tattoo:<group>); 0 = unlimited.
CREATE TABLE limit_overrides (
    key TEXT PRIMARY KEY,
    value INTEGER NOT NULL CHECK (value >= 0)
) STRICT;
