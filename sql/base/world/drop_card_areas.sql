-- Divination cards that drop in an area from a map tier on.
CREATE TABLE drop_card_areas (
    area_id TEXT NOT NULL,
    area_name TEXT NOT NULL,
    card_id TEXT NOT NULL,
    min_map_tier INTEGER NOT NULL CHECK (min_map_tier BETWEEN 1 AND 16),
    source TEXT NOT NULL,
    PRIMARY KEY (area_id, card_id)
) STRICT;
