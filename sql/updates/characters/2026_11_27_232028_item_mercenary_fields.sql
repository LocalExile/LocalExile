ALTER TABLE items ADD COLUMN mercenary_contract TEXT;
ALTER TABLE items ADD COLUMN mercenary_locked INTEGER NOT NULL DEFAULT 0;
