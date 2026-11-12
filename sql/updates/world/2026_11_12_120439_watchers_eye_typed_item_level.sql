-- Elder encounters grant two item levels above the area level.
ALTER TABLE unique_items ADD COLUMN encounter_item_level_bonus INTEGER CHECK (encounter_item_level_bonus >= 0);
UPDATE unique_items SET encounter_item_level_bonus = 2
WHERE name = 'Watcher''s Eye' AND base_type = 'Metadata/Items/Jewels/JewelPrismatic';
