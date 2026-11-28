-- What the Voidborn Reliquary's chest can drop: a foil unique (RelicItemEffectVariations row), how many supporter
-- entries name it, and the outcome's weight.
CREATE TABLE voidborn_reliquary_outcomes (
    reward TEXT NOT NULL,
    foil INTEGER NOT NULL CHECK (foil BETWEEN 0 AND 255),
    entries INTEGER NOT NULL CHECK (entries > 0),
    weight REAL NOT NULL CHECK (weight > 0),
    PRIMARY KEY (reward, foil)
) STRICT;
