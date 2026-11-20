-- Weapon passive tree state (Mods component flag bit 7), stored as JSON per item row.
ALTER TABLE items ADD COLUMN weapon_passive_tree TEXT;
