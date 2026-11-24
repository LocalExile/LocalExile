INSERT INTO unique_item_mods (name, base_type, implicit, ord, mod)
SELECT item.name, item.base_type, 0, COALESCE((
    SELECT MAX(existing.ord) + 1
    FROM unique_item_mods AS existing
    WHERE existing.name = item.name AND existing.base_type = item.base_type AND existing.implicit = 0
), 0), 'DoubleMinionLimitsUnique_1'
FROM unique_items AS item
WHERE item.name = 'The Dark Monarch'
  AND item.base_type = 'Metadata/Items/Armours/Helmets/HelmetInt14'
  AND NOT EXISTS (
      SELECT 1
      FROM unique_item_mods AS existing
      WHERE existing.name = item.name AND existing.base_type = item.base_type AND existing.implicit = 0
        AND existing.mod = 'DoubleMinionLimitsUnique_1'
  );
