-- The areas the Gateway generates itself (generated = 1), and the seed played in place of seed 1 for an area whose
-- seed-1 layout the client cannot build either; read by Auth and the Gateway (ServerLevelAreas).
CREATE TABLE server_level_areas (
    area_id TEXT PRIMARY KEY,
    generated INTEGER NOT NULL CHECK (generated IN (0, 1)),
    seed_in_place_of_one INTEGER CHECK (seed_in_place_of_one > 1)
) STRICT;
