-- These are native Mods component properties, separate from the modifier lists.
ALTER TABLE items ADD COLUMN memory_strands INTEGER NOT NULL DEFAULT 0 CHECK (memory_strands BETWEEN 0 AND 255);
ALTER TABLE items ADD COLUMN intangibility INTEGER NOT NULL DEFAULT 0 CHECK (intangibility BETWEEN 0 AND 100);
ALTER TABLE items ADD COLUMN required_level INTEGER CHECK (required_level BETWEEN 0 AND 255);
