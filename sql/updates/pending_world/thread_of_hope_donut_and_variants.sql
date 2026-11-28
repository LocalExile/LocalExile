-- Thread of Hope: use the ring-only donut allocation mod (no 800 base disc radius),
-- default normal drops/The Gulf to Small..Very Large (JewelRingRadiusValuesUnique__1),
-- and expose Massive (JewelRingRadiusValuesUnique__2) as an explicit unique variant.
UPDATE unique_item_mods
SET mod = 'JewelRingRadiusValuesUnique__1'
WHERE name = 'Thread of Hope' AND base_type = 'Metadata/Items/Jewels/JewelStr'
  AND implicit = 0 AND mod = 'JewelRingRadiusValuesUnique__2';

UPDATE unique_item_mods
SET mod = 'AllocateDisconnectedPassivesDonutUnique__1'
WHERE name = 'Thread of Hope' AND base_type = 'Metadata/Items/Jewels/JewelStr'
  AND implicit = 0 AND mod = 'JewelUniqueAllocateDisconnectedPassives';

INSERT OR IGNORE INTO unique_item_variants (name, base_type, variant, variant_base_type, weight) VALUES
('Thread of Hope', 'Metadata/Items/Jewels/JewelStr', 'Variable', NULL, 1),
('Thread of Hope', 'Metadata/Items/Jewels/JewelStr', 'Massive', NULL, 0);

INSERT OR IGNORE INTO unique_item_variant_mods (name, base_type, variant, ord, mod) VALUES
('Thread of Hope', 'Metadata/Items/Jewels/JewelStr', 'Variable', 0, 'JewelRingRadiusValuesUnique__1'),
('Thread of Hope', 'Metadata/Items/Jewels/JewelStr', 'Massive', 0, 'JewelRingRadiusValuesUnique__2');

UPDATE radius_jewels
SET conversion_mod = 'AllocateDisconnectedPassivesDonutUnique__1',
    radius = 0
WHERE kind = 'radius' AND name = 'Thread of Hope' AND base = 'Crimson Jewel';
