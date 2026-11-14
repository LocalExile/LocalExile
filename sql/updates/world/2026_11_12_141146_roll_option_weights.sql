-- A unique's random pool can weight its rows: each draw takes a row by its weight, without replacement.
ALTER TABLE unique_item_roll_options ADD COLUMN weight INTEGER NOT NULL DEFAULT 1 CHECK (weight > 0);

-- Voices rolls its number of added small passives instead of always 7: one of the four ExpansionJewelEmptyPassive
-- rows, weighted as poedb's Voices page lists them (1: 5, 3: 25, 5: 300, 7: 670, read 2026-11-12). Its three jewel
-- sockets stay fixed.
DELETE FROM unique_item_mods
WHERE name = 'Voices' AND base_type = 'Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge'
  AND mod = 'ExpansionJewelEmptyPassiveUnique__4';
INSERT INTO unique_item_roll_groups (name, base_type, grp, count) VALUES
('Voices', 'Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge', 0, 1);
INSERT INTO unique_item_roll_options (name, base_type, grp, ord, mod, weight) VALUES
('Voices', 'Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge', 0, 0, 'ExpansionJewelEmptyPassiveUnique__1', 5),
('Voices', 'Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge', 0, 1, 'ExpansionJewelEmptyPassiveUnique__2', 25),
('Voices', 'Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge', 0, 2, 'ExpansionJewelEmptyPassiveUnique_3_', 300),
('Voices', 'Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge', 0, 3, 'ExpansionJewelEmptyPassiveUnique__4', 670);
