-- Weapon passive tree state (Mods component flag bit 7, JSON) and StoredExperience component (u32).
ALTER TABLE items ADD COLUMN weapon_passive_tree TEXT;
ALTER TABLE items ADD COLUMN stored_experience INTEGER NOT NULL DEFAULT 0;
