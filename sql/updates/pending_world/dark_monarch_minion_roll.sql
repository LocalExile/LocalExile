UPDATE catalog_entries
SET payload = json_insert(payload, '$.Mods[#]', 'DoubleMinionLimitsUnique_1')
WHERE kind = 'unique_item'
  AND key = 'The Dark Monarch|Metadata/Items/Armours/Helmets/HelmetInt14'
  AND NOT EXISTS (
      SELECT 1
      FROM json_each(catalog_entries.payload, '$.Mods')
      WHERE value = 'DoubleMinionLimitsUnique_1'
  );
