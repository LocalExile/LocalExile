-- Preserve the source corrections after the catalog moved from C# seeds to world.db.
UPDATE catalog_entries
SET payload = json_insert(payload, '$.Mods[#]', 'MinionLifeConvertedToEnergyShieldUnique__1')
WHERE kind = 'unique_item'
  AND key = 'Fleshcrafter|Metadata/Items/Armours/BodyArmours/BodyInt14'
  AND NOT EXISTS (SELECT 1 FROM json_each(payload, '$.Mods')
                  WHERE value = 'MinionLifeConvertedToEnergyShieldUnique__1');

UPDATE catalog_entries
SET payload = json_set(payload,
    '$.Mods[' || (SELECT key FROM json_each(payload, '$.Mods')
                 WHERE value = 'CatsStealthDurationUnique__1_' LIMIT 1) || ']',
    'CatsAgilityDurationUnique__1')
WHERE kind = 'unique_item'
  AND key = 'Replica Farrul''s Fur|Metadata/Items/Armours/BodyArmours/BodyStrDex17'
  AND EXISTS (SELECT 1 FROM json_each(payload, '$.Mods')
              WHERE value = 'CatsStealthDurationUnique__1_');
