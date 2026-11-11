-- Elder encounters in the native level 83/84/85 areas grant item levels 85/86/87.
UPDATE catalog_entries SET payload = json_set(payload, '$.EncounterItemLevelBonus', 2)
WHERE kind = 'unique_item' AND key = 'Watcher''s Eye|Metadata/Items/Jewels/JewelPrismatic';
