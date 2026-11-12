-- Voices rolls its number of added small passives (1, 3, 5 or 7: one of four ExpansionJewelEmptyPassive rows, PoE
-- wiki item data 2026-11-12); the catalogue had the 7-passive row fixed. Its three jewel sockets stay fixed.
DELETE FROM unique_item_mods
WHERE name = 'Voices' AND base_type = 'Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge'
  AND mod = 'ExpansionJewelEmptyPassiveUnique__4';
INSERT INTO unique_item_roll_groups (name, base_type, grp, count) VALUES
('Voices', 'Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge', 0, 1);
INSERT INTO unique_item_roll_options (name, base_type, grp, ord, mod) VALUES
('Voices', 'Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge', 0, 0, 'ExpansionJewelEmptyPassiveUnique__1'),
('Voices', 'Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge', 0, 1, 'ExpansionJewelEmptyPassiveUnique__2'),
('Voices', 'Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge', 0, 2, 'ExpansionJewelEmptyPassiveUnique_3_'),
('Voices', 'Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge', 0, 3, 'ExpansionJewelEmptyPassiveUnique__4');
