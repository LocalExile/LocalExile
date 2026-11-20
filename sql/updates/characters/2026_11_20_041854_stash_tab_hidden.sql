-- A stash tab left out of the panel: a deleted folder, kept so later tabs keep their places (and inventory ids).
ALTER TABLE stash_tab_headers ADD COLUMN hidden INTEGER NOT NULL DEFAULT 0 CHECK (hidden IN (0, 1));
