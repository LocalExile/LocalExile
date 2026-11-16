-- Bino's Kitchen Knife's poison spread line is two lines in the Path of Building export, so unique_items.py could not tie
-- it to its Mods row and the definition went without it (poedb: PoisonSpreadAndHealOnPoisonedKillUniqueDagger8).
INSERT OR IGNORE INTO unique_item_mods (name, base_type, implicit, ord, mod) VALUES
('Bino''s Kitchen Knife', 'Metadata/Items/Weapons/OneHandWeapons/Daggers/Dagger17', 0, 4, 'PoisonSpreadAndHealOnPoisonedKillUniqueDagger8');
