-- The item and jewel catalogues: one typed row per entry, as JSON (kind: unique_item, radius_jewel, threshold_jewel, unique_jewel_global, unique_jewel_special, timeless_jewel, tattoo_effect, unique_jewel_visual).
CREATE TABLE catalog_entries (
    kind TEXT NOT NULL,
    key TEXT NOT NULL,
    payload TEXT NOT NULL CHECK (json_valid(payload)),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    PRIMARY KEY (kind, key)
) STRICT;
