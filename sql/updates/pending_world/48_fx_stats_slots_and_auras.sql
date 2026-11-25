-- fx_stats only. Read by code from this change on: defences "from Equipped Helmet and Gloves / Helmet and Boots /
-- Gloves and Boots" (CharacterStats: the slots' own value times the percent) and the weapon tree's "more Cast
-- Speed". Read already, by its name as text: "increased Rarity of Items found from Chests" (StrongboxLoot).
--
-- Not a row here but part of the same change: a mod that is an aura now stays with or off its wearer by what its
-- own aura template says it reaches (ModCatalog.AuraForSelf), so "Nearby Allies ..." lines no longer count for the
-- wearer and "You and nearby Allies ..." lines no longer go missing.
UPDATE fx_stats SET handled = 'code', code = 'CharacterStats.cs, Progression.cs', why = NULL, fit = NULL
WHERE stat IN ('armour_from_helmet_and_gloves_+%', 'evasion_rating_from_helmet_and_boots_+%', 'energy_shield_from_gloves_and_boots_+%');
UPDATE fx_stats SET handled = 'code', code = 'Player.cs', why = NULL, fit = NULL WHERE stat = 'weapon_tree_cast_speed_+%_final';
UPDATE fx_stats SET handled = 'code', code = 'StrongboxLoot.cs', why = NULL, fit = NULL WHERE stat = 'chest_item_rarity_+%';
