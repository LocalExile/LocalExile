-- Apply the conditional-item source corrections to the typed catalogue, including databases whose JSON catalogue
-- was already corrected before the typed-table migration. Do not replace an administrator's other item edits.
INSERT INTO unique_item_mods (name, base_type, implicit, ord, mod)
SELECT name, base_type, 0,
       COALESCE((SELECT MAX(ord) + 1 FROM unique_item_mods m
                 WHERE m.name = u.name AND m.base_type = u.base_type AND m.implicit = 0), 0),
       'MinionLifeConvertedToEnergyShieldUnique__1'
FROM unique_items u
WHERE name = 'Fleshcrafter' AND base_type = 'Metadata/Items/Armours/BodyArmours/BodyInt14'
  AND NOT EXISTS (SELECT 1 FROM unique_item_mods m
                  WHERE m.name = u.name AND m.base_type = u.base_type AND m.implicit = 0
                    AND m.mod = 'MinionLifeConvertedToEnergyShieldUnique__1');

UPDATE unique_item_mods
SET mod = 'CatsAgilityDurationUnique__1'
WHERE name = 'Replica Farrul''s Fur' AND base_type = 'Metadata/Items/Armours/BodyArmours/BodyStrDex17'
  AND implicit = 0 AND mod = 'CatsStealthDurationUnique__1_';
