-- Custom items from existing bases, mods, words and art (no reader yet).
CREATE TABLE item_templates (
    id TEXT PRIMARY KEY,
    label TEXT NOT NULL,
    base_type TEXT NOT NULL,
    rarity INTEGER NOT NULL,
    item_level INTEGER NOT NULL,
    quality INTEGER NOT NULL,
    custom_name TEXT,
    name_words TEXT,
    visual INTEGER,
    corrupted INTEGER NOT NULL,
    mirrored INTEGER NOT NULL,
    mods TEXT NOT NULL
) STRICT;
