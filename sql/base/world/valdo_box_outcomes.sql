-- What a Valdo's Puzzle Box can open into: a foil Valdo Map's name, layout (WorldAreas Id; NULL: a random Atlas
-- layout), the foil unique its completion rewards (RelicItemEffectVariations row) and the outcome's weight.
CREATE TABLE valdo_box_outcomes (
    id INTEGER PRIMARY KEY,
    map_name TEXT NOT NULL,
    area_id TEXT,
    reward TEXT NOT NULL,
    foil INTEGER NOT NULL CHECK (foil BETWEEN 0 AND 255),
    weight REAL NOT NULL CHECK (weight > 0)
) STRICT;
