-- Four states the game module publishes under a name without the leading underscore the others have: their
-- conditions carry the exact name as "key" (StatConditions.TableCondition reads "key", else "_" + "is").
UPDATE fx_modifiers SET conditions = replace(conditions, '"is":"has_cats_stealth"', '"is":"has_cats_stealth","key":"has_cats_stealth"') WHERE conditions LIKE '%"is":"has_cats_stealth"%' AND conditions NOT LIKE '%"key":"has_cats_stealth"%';
UPDATE fx_modifiers SET conditions = replace(conditions, '"is":"has_cat_aspect"', '"is":"has_cat_aspect","key":"has_cat_aspect"') WHERE conditions LIKE '%"is":"has_cat_aspect"%' AND conditions NOT LIKE '%"key":"has_cat_aspect"%';
UPDATE fx_modifiers SET conditions = replace(conditions, '"is":"using_pride"', '"is":"using_pride","key":"using_pride"') WHERE conditions LIKE '%"is":"using_pride"%' AND conditions NOT LIKE '%"key":"using_pride"%';
UPDATE fx_modifiers SET conditions = replace(conditions, '"is":"has_cats_agility"', '"is":"has_cats_agility","key":"has_cats_agility"') WHERE conditions LIKE '%"is":"has_cats_agility"%' AND conditions NOT LIKE '%"key":"has_cats_agility"%';
