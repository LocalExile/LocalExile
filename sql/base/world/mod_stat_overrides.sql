-- A mod stat's roll range in place of the client's (Mods Stat<n>Min/Max), by mod Id and stat index.
CREATE TABLE mod_stat_overrides (
    mod_id TEXT NOT NULL,
    stat_index INTEGER NOT NULL CHECK (stat_index >= 0),
    minimum INTEGER NOT NULL,
    maximum INTEGER NOT NULL,
    PRIMARY KEY (mod_id, stat_index),
    CHECK (minimum <= maximum)
) STRICT;
