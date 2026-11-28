-- The Valdo Map modifier pool (Mods ids) with each one's selection weight.
CREATE TABLE valdo_map_mods (
    mod_id TEXT PRIMARY KEY,
    weight INTEGER NOT NULL CHECK (weight >= 0)
) STRICT;
