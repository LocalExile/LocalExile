-- Client table CurrencyItems: all 1962 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "CurrencyItems" ("_row", "BaseItemTypesKey", "StackSize", "CurrencyUseType", "Action", "Directions", "FullStack_BaseItemTypesKey", "Description", "Usage_AchievementItemsKeys", "Scroll", "Possession_AchievementItemsKey", "__col10", "__col11", "CurrencyTab_StackSize", "XBoxDirections", "__col14", "ModifyMapsAchievements", "ModifyContractsAchievements", "CombineAchievements", "ChangedForHardmode", "DescriptionHardmode", "IsGold", "__col21", "__col22") VALUES
(0, 5, 40, 2, 'add_armour_quality', 'Right click this item then left click an armour to apply it. Has greater effect on lower item level, non-unique armours. The maximum quality is 20%.', NULL, 'Improves the quality of an armour', '[]', 0, 1140, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an armour to apply it. Has greater effect on lower item level, non-unique armours. The maximum quality is 20%.', 100, '[]', '[]', '[]', 0, '', 0, 1, 1),
(1, 6, 10, 2, 'duplicate', 'Right click this item then left click an equipable non-unique item to apply it. Mirrored copies cannot be modified.', NULL, 'Creates a mirrored copy of an item', '[11656]', 0, 1141, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an equipable non-unique item to apply it. Mirrored copies cannot be modified.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(2, 1, 40, 2, 'identify', 'Right click this item then left click an unidentified item to apply it.', NULL, 'Identifies an item', '[10890]', 1, 1142, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an unidentified item to apply it.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(3, 9, 40, 1, 'skill_point_respec', 'Right click on this item to use it.', NULL, 'Grants a passive skill refund point', '[11238]', 0, 1143, '[13]', '[64]', 10000, '<<xbox_button_a>> to use.', 100, '[]', '[]', '[]', 0, '', 0, 1, 0),
(4, 10, 40, 1, 'atlas_skill_point_respec', 'Right click on this item to use it.', NULL, 'Grants an atlas passive skill refund point', '[11239]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to use.', 100, '[]', '[]', '[]', 0, '', 0, 1, 0),
(5, 2, 20, 2, 'reroll', 'Right click this item then left click a rare item to apply it.', NULL, 'Reforges a rare item with new random modifiers', '[11232]', 0, 1144, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a rare item to apply it.', 100, '[]', '[7257]', '[]', 0, '', 0, 0, 1),
(6, 3, 20, 2, 'replace_rare_mod_veiled', 'Right click this item then left click a rare item to apply it.', NULL, 'Removes a random modifier and adds a random Veiled modifier to a rare item', '[11649]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a rare item to apply it.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(7, 4, 20, 2, 'reroll_rare_veiled', 'Right click this item then left click a rare item to apply it.', NULL, 'Reforges a rare item with new random modifiers including a random Veiled modifier', '[11649]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a rare item to apply it.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(8, 8, 20, 2, 'transmute_to_random', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to a random rarity', '[11224]', 0, 1145, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 100, '[]', '[7255]', '[]', 0, '', 0, 0, 1),
(9, 11, 40, 2, 'transmute_to_magic', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to a magic item', '[11161,11221]', 0, 1146, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 100, '[]', '[7253]', '[]', 0, '', 0, 0, 1),
(10, 7, 20, 2, 'transmute_to_rare', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to a rare item', '[11164,11225]', 0, 1147, '[9]', '[8]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 100, '[7003]', '[7256]', '[]', 0, '', 0, 0, 1),
(11, 0, 20, 2, 'add_weapon_quality', 'Right click this item then left click a weapon to apply it. Has greater effect on lower item level, non-unique weapons. The maximum quality is 20%.', NULL, 'Improves the quality of a weapon', '[]', 0, 1148, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on weapon to apply it. Has greater effect on lower item level, non-unique weapons. The maximum quality is 20%.', 100, '[]', '[]', '[]', 0, '', 0, 1, 1),
(12, 16, 30, 2, 'add_mod_to_magic', 'Right click this item then left click a magic item to apply it. Magic items can have up to two random modifiers.', NULL, 'Augments a magic item with a new random modifier', '[11163,11222]', 0, 1149, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a magic item to apply it. Magic items can have up to two random modifiers.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(13, 12, 20, 2, 'reroll_magic', 'Right click this item then left click a magic item to apply it.', NULL, 'Reforges a magic item with new random modifiers', '[11162,11223]', 0, 1150, '[16]', '[64]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a magic item to apply it.', 100, '[]', '[7254]', '[]', 0, '', 0, 0, 1),
(14, 13, 30, 2, 'convert_to_normal', 'Right click this item then left click on a magic or rare item to apply it.', NULL, 'Removes all modifiers from an item', '[11226]', 0, 1151, '[8]', '[96]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a magic or rare item to apply it.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(15, 14, 20, 2, 'add_mod_to_rare', 'Right click this item then left click a rare item to apply it. Rare items can have up to six random modifiers.', NULL, 'Augments a rare item with a new random modifier', '[11233]', 0, 1152, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a rare item to apply it. Rare items can have up to six random modifiers.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(16, 15, 20, 2, 'upgrade_magic_to_rare', 'Right click this item then left click a magic item to apply it. Current modifiers are retained and a new one is added.', NULL, 'Upgrades a magic item to a rare item', '[11231]', 0, 1153, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a magic item to apply it. Current modifiers are retained and a new one is added.', 100, '[]', '[7258]', '[]', 0, '', 0, 0, 1),
(17, 17, 40, 1, 'town_portal', 'Right click on this item to use it.', NULL, 'Creates a portal to town', '[]', 1, 1154, '[]', '[]', 10000, '<<xbox_button_a>> to use.', 100, '[]', '[]', '[]', 0, '', 0, 0, 0),
(18, 18, 20, 2, 'add_flask_quality', 'Right click this item then left click a flask or tincture to apply it. Has greater effect on lower item level, non-unique flasks. The maximum quality is 20%.', NULL, 'Improves the quality of a flask or tincture', '[8258]', 0, 1155, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a flask or tincture to apply it. Has greater effect on lower item level, non-unique flasks. The maximum quality is 20%.', 100, '[]', '[]', '[]', 0, '', 0, 1, 1),
(19, 19, 20, 2, 'add_gem_quality', 'Right click this item then left click a gem to apply it. The maximum quality is 20%.', NULL, 'Improves the quality of a gem', '[11338]', 0, 1156, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a gem to apply it. The maximum quality is 20%.', 100, '[]', '[]', '[]', 0, '', 0, 1, 1),
(20, 26, 20, 2, 'add_map_alt_quality', 'Right click this item then left click a map to apply it. The maximum quality is 20%.', NULL, 'Improves the quality of a map enhancing Item Quantity' || char(13) || '
Replaces other quality types', '[7965]', 0, 1157, '[2]', '[12]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a map to apply it. The maximum quality is 20%.', 100, '[7004]', '[]', '[]', 0, '', 0, 1, 1),
(21, 27, 20, 2, 'add_map_alt_quality', 'Right click this item then left click a map to apply it. The maximum quality is 20%.', NULL, 'Improves the quality of a map enhancing Item Rarity' || char(13) || '
Replaces other quality types', '[10544]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a map to apply it. The maximum quality is 20%.', 100, '[]', '[]', '[]', 0, '', 0, 1, 1),
(22, 28, 20, 2, 'add_map_alt_quality', 'Right click this item then left click a map to apply it. The maximum quality is 20%.', NULL, 'Improves the quality of a map enhancing Pack Size' || char(13) || '
Replaces other quality types', '[10544]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a map to apply it. The maximum quality is 20%.', 100, '[]', '[]', '[]', 0, '', 0, 1, 1),
(23, 29, 20, 2, 'add_map_alt_quality', 'Right click this item then left click a map to apply it. The maximum quality is 20%.', NULL, 'Improves the quality of a map enhancing Divination Cards found' || char(13) || '
Replaces other quality types', '[10544]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a map to apply it. The maximum quality is 20%.', 100, '[]', '[]', '[]', 0, '', 0, 1, 1),
(24, 30, 20, 2, 'add_map_alt_quality', 'Right click this item then left click a map to apply it. The maximum quality is 20%.', NULL, 'Improves the quality of a map enhancing Scarabs found' || char(13) || '
Replaces other quality types', '[10544]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a map to apply it. The maximum quality is 20%.', 100, '[]', '[]', '[]', 0, '', 0, 1, 1),
(25, 31, 20, 2, 'add_map_alt_quality', 'Right click this item then left click a map to apply it. The maximum quality is 20%.', NULL, 'Improves the quality of a map enhancing Currency found' || char(13) || '
Replaces other quality types', '[10544]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a map to apply it. The maximum quality is 20%.', 100, '[]', '[]', '[]', 0, '', 0, 1, 1),
(26, 20, 20, 2, 'reroll_socket_colours', 'Right click this item then left click a socketed item to apply it. The item''s quality increases the chances of non-white sockets.', NULL, 'Reforges the colour of sockets on an item', '[11550]', 0, 1158, '[22]', '[48]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a socketed item to apply it. The item''s quality increases the chances of non-white sockets.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(27, 21, 20, 2, 'reroll_socket_links', 'Right click this item then left click a socketed item to apply it. The item''s quality increases the chances of obtaining more links.', NULL, 'Reforges the links between sockets on an item', '[11227]', 0, 1159, '[22]', '[64]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a socketed item to apply it. The item''s quality increases the chances of obtaining more links.', 100, '[]', '[]', '[]', 0, '', 0, 1, 1),
(28, 22, 20, 2, 'reroll_socket_numbers', 'Right click this item then left click a socketed item to apply it. The item''s quality increases the chances of obtaining more sockets.', NULL, 'Reforges the number of sockets on an item', '[11228]', 0, 1160, '[12]', '[32]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a socketed item to apply it. The item''s quality increases the chances of obtaining more sockets.', 100, '[]', '[]', '[]', 0, '', 0, 1, 1),
(29, 23, 20, 2, 'reroll_mod_values', 'Right click this item then left click a magic, rare or unique item to apply it.', NULL, 'Randomises the values of the random modifiers on an item', '[11235]', 0, 1161, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a magic, rare or unique item to apply it.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(30, 24, 20, 2, 'reroll_implicit_mod', 'Right click this item then left click another item to apply it.', NULL, 'Randomises the values of the implicit modifiers of an item', '[11234]', 0, 1162, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on another item to apply it.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(31, 25, 10, 2, 'reroll_variable_defences', 'Right click this item then left click an armour to apply it.', NULL, 'Randomises the numeric values of base defences on an armour', '[11657]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an armour to apply it.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(32, 32, 5, 3, '', 'A stack of 5 fragments becomes a Scroll of Wisdom.', 1, '', '[]', 0, 1163, '[]', '[]', 0, 'A stack of 5 fragments becomes a Scroll of Wisdom.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(33, 33, 20, 3, '', 'A stack of 20 shards becomes an Orb of Transmutation.', 11, '', '[]', 0, 1164, '[]', '[]', 0, 'A stack of 20 shards becomes an Orb of Transmutation.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(34, 34, 20, 3, '', 'A stack of 20 shards becomes an Orb of Alteration.', 12, '', '[]', 0, 1165, '[]', '[]', 0, 'A stack of 20 shards becomes an Orb of Alteration.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(35, 35, 20, 3, '', 'A stack of 20 shards becomes an Orb of Alchemy.', 7, '', '[]', 0, 1166, '[]', '[]', 0, 'A stack of 20 shards becomes an Orb of Alchemy.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(36, 36, 10, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 100, '[]', '[]', '[]', 0, '', 0, 0, 0),
(37, 37, 10, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 100, '[]', '[]', '[]', 0, '', 0, 0, 0),
(38, 38, 10, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 100, '[]', '[]', '[]', 0, '', 0, 0, 0),
(39, 39, 10, 2, 'inital_imprint', 'Right click this item then left click on an item to create an imprint. This imprint can later be used to restore this specific item to that imprinted state.', NULL, 'Creates an imprint of an item for later restoration' || char(13) || '
The imprint can''t be applied to Fractured Items', '[]', 0, 1167, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to create an imprint. This imprint can later be used to restore this specific item to that imprinted state.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(40, 40, 1, 2, 'restore_imprint', 'Right click this item then left click on the imprinted original item to restore its modifiers. An imprint cannot be dropped or traded to other players.', NULL, 'Restores an imprinted item onto the original' || char(13) || '
Can''t be applied to Fractured Items', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on the imprinted original item to restore its modifiers. An imprint cannot be dropped or traded to other players.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(41, 41, 20, 2, 'corrupt_item', 'Right click this item then left click an item to corrupt it. Corrupted items cannot be modified again.', NULL, 'Corrupts an item, modifying it unpredictably', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to corrupt it. Corrupted items cannot be modified again.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(42, 42, 9, 2, 'labyrinth_enchant', 'Left click on the skull and then left click on the item you wish to modify.', NULL, 'Adds an enchantment to an item, or replaces an existing enchantment', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on the item you wish to modify.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(43, 43, 10, 3, '', 'Can be placed in the Divine Font to allow enchantment of corrupted gloves, boots, helmets or belts.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 100, '[]', '[]', '[]', 0, '', 0, 0, 0),
(44, 44, 1, 2, 'corrupt_item', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(45, 45, 1, 2, 'incursion_corrupt', '', NULL, '', '[9951]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(46, 46, 1, 2, 'incursion_corrupt_gem', '', NULL, '', '[10199]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(47, 47, 5000, 3, '', 'Trade coins to Cadiro Perandus.', NULL, '', '[]', 0, NULL, '[]', '[]', 50000, 'Trade coins to Cadiro Perandus.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(48, 48, 30, 3, '', 'Cross Navali''s palm with silver to receive a prophecy.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Cross Navali''s palm with silver to receive a prophecy.', 100, '[]', '[]', '[]', 0, '', 0, 0, 0),
(49, 49, 1, 6, 'add_scrying', 'Right click on this item then left click a Map on your Atlas.', NULL, '[Scrying|Scries] a Map on your Atlas', '[11574]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select this item then <<xbox_button_a>> a Map on your Atlas.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(50, 50, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(51, 51, 10, 3, '', 'Right click on this item then left click on a Beast in your Menagerie to itemise the Beast.', NULL, 'Stores a Beast in an item', '[]', 0, NULL, '[]', '[]', 5000, 'You can use these to itemise beasts by interacting with the signs outside the beasts'' cages in the menagerie.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(52, 52, 1, 1, 'add_captured_monster', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(53, 53, 10, 2, 'itemise_necropolis_corpse', 'Right click on this item then left click on a Corpse in the Necropolis Morgue.', NULL, 'Stores a Corpse in an item', '[]', 0, NULL, '[]', '[]', 0, 'Use this item on a Corpse in your Necropolis Morgue to store it inside.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(54, 54, 1, 1, 'add_necropolis_corpse', 'Right click on this item to use it while in the Necropolis to add it to the Necropolis Morgue.', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to use it while in the Necropolis to add it to the Necropolis Morgue.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(55, 55, 20, 2, 'add_influence_mod_to_rare', 'Right click this item then left click a high-level rare item with no influence to apply it. Rare items can have up to six random modifiers.', NULL, 'Adds Shaper influence and a new Shaper modifier to a rare item', '[11648]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a high-level rare item with no influence to apply it. Rare items can have up to six random modifiers.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(56, 56, 20, 2, 'add_influence_mod_to_rare', 'Right click this item then left click a high-level rare item with no influence to apply it. Rare items can have up to six random modifiers.', NULL, 'Adds Elder influence and a new Elder modifier to a rare item', '[11648]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a high-level rare item with no influence to apply it. Rare items can have up to six random modifiers.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(57, 57, 10, 2, 'apply_zana_influence', 'Right click this item then left click a normal equipment item to apply it.', NULL, 'Randomises the number of Memory Strands on a normal equipment item', '[11650]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal equipment item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(58, 58, 10, 2, 'consume_zana_influence_upgrade_mods', 'Right click this item then left click an equipment item with Memory Strands to apply it.', NULL, 'Consumes all Memory Strands on an item to attempt to upgrade the tier' || char(13) || '
of explicit modifiers based on the number of Memory Strands consumed', '[11650]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an equipment item with Memory Strands to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(59, 59, 10, 2, 'enchant_map_zana_influence_drops', 'Right click this item then left click a Memory Influenced Map item to apply it. Can apply up to 3 to a single Map item.', NULL, 'Modifies a Memory Influenced Map, causing items found to' || char(13) || '
have more Memory Strands, but fewer items drop', '[11650]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Memory Influenced Map item to apply it. Can apply up to 3 to a single Map item.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(60, 60, 10, 2, 'itemise_sextant_mods', 'Right click on this item then left click on a Voidstone to itemise an applied Sextant Modifier.', NULL, 'Stores a Sextant Modifier in an item', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Voidstone to itemise an applied Sextant Modifier.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(61, 61, 1, 2, 'restore_sextant_mods', 'Right click on this item then left click on a Voidstone to apply the itemised Sextant Modifier to the Voidstone.', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Voidstone to apply the itemised Sextant Modifier to the Voidstone.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(62, 62, 20, 2, 'reroll_skill_quality_type', 'Right click this item then left click a skill gem to apply it.', NULL, 'Change the type of quality of a skill gem with quality to another random quality', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a gem to apply it. The maximum quality is 20%.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(63, 63, 20, 2, 'reroll_support_quality_type', 'Right click this item then left click a support gem to apply it.', NULL, 'Change the type of quality of a support gem with quality to another random quality', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a gem to apply it. The maximum quality is 20%.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(64, 64, 20, 2, 'heist_weapon_enchant', 'Right click this item then left click on the item you wish to modify.', NULL, 'Adds or replaces an enchantment on a weapon' || char(13) || '
This may reforge the weapon''s sockets', '[11660]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on the item you wish to modify.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(65, 65, 20, 2, 'heist_armour_enchant', 'Right click this item then left click on the item you wish to modify.', NULL, 'Adds or replaces an enchantment on a body armour' || char(13) || '
This may reforge the body armour''s sockets', '[11660]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on the item you wish to modify.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(66, 66, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Currency Items reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(67, 67, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Unique Items reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(68, 68, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Gems reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(69, 69, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Weapons reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(70, 70, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Armour reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(71, 71, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Map Items reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(72, 72, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Trinkets reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(73, 73, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Abyss Items reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(74, 74, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Expedition Items reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(75, 75, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Currency Items reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(76, 76, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Breach Items reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(77, 77, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Essences reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(78, 78, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Fragments reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(79, 79, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Scarabs reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(80, 80, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Fossils reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(81, 81, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Currency Items reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(82, 82, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Divination Cards reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(83, 83, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Generic Items reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(84, 84, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Talismans reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(85, 85, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Labyrinth Items reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(86, 86, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Legion reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(87, 87, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Blight reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(88, 88, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium with the Ultimatum reward type', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(89, 89, 10, 2, 'apply_affliction_reward', 'Right click this item then left click a Map item to apply it. Can apply up to 5 to a single Map item.', NULL, 'Modifies a Map item adding layers of Delirium', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map item to apply it. Can apply up to 5 to a single Map item.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(90, 90, 20, 2, 'reroll_cluster_type_enchant_mod_action', 'Right click this item then left click a Cluster Jewel item to apply it.', NULL, 'Replaces the Small Passive Skill Enchantment of a Cluster Jewel', '[11430]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Cluster Jewel item to apply it.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(91, 212, 10, 3, '', 'Right click this item then left click a shaped map on the atlas to downgrade it. You can then earn the Shaper''s Orb again to reshape another map afterwards.', NULL, 'Downgrades a map on the Atlas', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a shaped map on the atlas to downgrade it. You can then earn the Shaper''s Orb again to reshape another map afterwards.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(92, 91, 10, 2, 'graft_add_mod_to_magic', 'Right click this item then left click a magic Graft to apply it. Magic Grafts can have up to two random modifiers.', NULL, 'Augments a magic Graft with a new random modifier', '[10999]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a magic Graft to apply it. Magic Grafts can have up to two random modifiers.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(93, 92, 10, 2, 'graft_upgrade_magic_to_rare', 'Right click this item then left click a magic Graft to apply it. Current modifiers are retained and a new one is added.', NULL, 'Upgrades a magic Graft to a rare Graft', '[11000]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a magic Graft to apply it. Current modifiers are retained and a new one is added.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(94, 93, 10, 2, 'graft_add_mod_to_rare', 'Right click this item then left click a rare Graft to apply it. Rare Grafts can have up to four random modifiers.', NULL, 'Augments a rare Graft with a new random modifier', '[11001]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a rare Graft to apply it. Rare Grafts can have up to four random modifiers.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(95, 94, 10, 2, 'graft_corrupt_item', 'Right click this item then left click a Graft to corrupt it. Corrupted Grafts cannot be modified again.', NULL, 'Corrupts a Graft, either destroying it entirely or replacing the implicit with a powerful modifier. This does not replace the Skill the Graft will use.', '[11002]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Graft to corrupt it. Corrupted Grafts cannot be modified again.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(96, 95, 10, 2, 'mutated_add_mod_to_magic', 'Right click this item then left click a magic item to apply it. Magic items can have up to two random modifiers.', NULL, 'Augments a magic item with a new random modifier with
higher level modifiers being more likely', '[11003]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a magic item to apply it. Magic items can have up to two random modifiers.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(97, 96, 10, 2, 'mutated_upgrade_magic_to_rare', 'Right click this item then left click a magic item to apply it. Current modifiers are retained and a new one is added.', NULL, 'Upgrades a magic item to a rare item with higher level
modifiers being more likely', '[11004]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a magic item to apply it. Current modifiers are retained and a new one is added.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(98, 97, 10, 2, 'mutated_add_mod_to_rare', 'Right click this item then left click a rare item to apply it. Rare items can have up to six random modifiers.', NULL, 'Augments a rare item with a new random modifier with
higher level modifiers being more likely', '[11005]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a rare item to apply it. Rare items can have up to six random modifiers.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(99, 98, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(100, 99, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(101, 100, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(102, 101, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(103, 102, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(104, 103, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(105, 104, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(106, 105, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(107, 106, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(108, 107, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(109, 108, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(110, 109, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(111, 110, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(112, 111, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(113, 112, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(114, 113, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(115, 114, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(116, 115, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(117, 116, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(118, 117, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(119, 118, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(120, 119, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(121, 120, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(122, 121, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(123, 122, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(124, 123, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(125, 124, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(126, 125, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(127, 126, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(128, 127, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(129, 128, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(130, 129, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(131, 130, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(132, 131, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(133, 132, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(134, 133, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(135, 134, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(136, 135, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(137, 136, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(138, 137, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(139, 138, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(140, 139, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(141, 140, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(142, 141, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(143, 142, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(144, 143, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(145, 144, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(146, 145, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(147, 146, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(148, 147, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(149, 148, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(150, 149, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(151, 150, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(152, 151, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(153, 152, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(154, 153, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(155, 154, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(156, 155, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(157, 156, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(158, 157, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(159, 158, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(160, 159, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(161, 160, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(162, 161, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(163, 162, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(164, 163, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(165, 164, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(166, 165, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(167, 166, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(168, 167, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(169, 168, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(170, 169, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(171, 170, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(172, 171, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(173, 172, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(174, 173, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(175, 174, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(176, 175, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(177, 176, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(178, 177, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(179, 178, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(180, 179, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(181, 180, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(182, 181, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(183, 182, 10, 2, 'use_essence', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to rare with one guaranteed property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(184, 183, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(185, 184, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(186, 185, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(187, 186, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(188, 187, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(189, 188, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(190, 189, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(191, 190, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(192, 191, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(193, 192, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(194, 193, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(195, 194, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(196, 195, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(197, 196, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(198, 197, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[7719]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(199, 198, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[11665]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(200, 199, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[11665]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(201, 200, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[11665]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(202, 201, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[11665]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(203, 202, 10, 2, 'use_high_essence', 'Right click this item then left click a normal or rare item to apply it.', NULL, 'Upgrades a normal item to rare or reforges a rare item, guaranteeing one property', '[11292]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal or rare item to apply it.', 0, '[]', '[]', '[]', 1, 'Upgrades a normal item to rare with one guaranteed property', 0, 0, 1),
(204, 203, 9, 2, 'corrupt_monolith', 'Right click this item then left click a monster trapped by Essences to corrupt them. Corrupted Essences cannot be modified again.', NULL, 'Corrupts the Essences trapping a monster, modifying them unpredictably', '[1027,7712,8917,10138]', 0, NULL, '[]', '[]', 10000, 'Use this item on a monster trapped by Essences to corrupt them. Corrupted Essences cannot be modified again.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(205, 204, 10, 2, 'add_atlas_mod_low', 'Right click this item then left click a Voidstone to apply it.', NULL, 'Adds or rerolls a modifier on a Voidstone', '[]', 0, NULL, '[2,7]', '[10,10]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Voidstone to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(206, 205, 10, 2, 'add_atlas_mod_mid', 'Right click this item then left click a Voidstone to apply it.', NULL, 'Adds or rerolls a modifier on a Voidstone', '[]', 0, NULL, '[2,7]', '[20,10]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Voidstone to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(207, 206, 10, 2, 'add_atlas_mod_high', 'Right click this item then left click a Voidstone to apply it.', NULL, 'Adds or rerolls a modifier on a Voidstone', '[4223]', 0, NULL, '[2,7]', '[30,10]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Voidstone to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(208, 207, 10, 2, 'add_atlas_mod_maven', 'Right click this item then left click a Voidstone to apply it.', NULL, 'Adds or rerolls a modifier on a Voidstone', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Voidstone to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(209, 209, 10, 3, '', 'Right click this item then left click a white map on the Atlas to apply it.', NULL, 'Uncompletes a white map on the Atlas', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a white map on the Atlas to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(210, 210, 10, 3, '', 'Right click this item then left click a yellow or white map on the Atlas to apply it.', NULL, 'Uncompletes a yellow or white map on the Atlas', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a yellow or white map on the Atlas to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(211, 211, 10, 3, '', 'Right click this item then left click any map on the Atlas to apply it.', NULL, 'Uncompletes any map on the Atlas', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on any map on the Atlas to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(212, 213, 100, 3, '', 'Combine 100 Splinters to create Xoph''s Breachstone.', 3030, '', '[]', 0, NULL, '[]', '[]', 5000, 'Combine 100 Splinters to create Xoph''s Breachstone.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(213, 214, 100, 3, '', 'Combine 100 Splinters to create Tul''s Breachstone.', 3031, '', '[]', 0, NULL, '[]', '[]', 5000, 'Combine 100 Splinters to create Tul''s Breachstone.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(214, 215, 100, 3, '', 'Combine 100 Splinters to create Esh''s Breachstone.', 3032, '', '[]', 0, NULL, '[]', '[]', 5000, 'Combine 100 Splinters to create Esh''s Breachstone.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(215, 216, 100, 3, '', 'Combine 100 Splinters to create Uul-Netol''s Breachstone.', 3033, '', '[]', 0, NULL, '[]', '[]', 5000, 'Combine 100 Splinters to create Uul-Netol''s Breachstone.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(216, 217, 100, 3, '', 'Combine 100 splinters to create Chayula''s Breachstone.', 3034, '', '[]', 0, NULL, '[]', '[]', 5000, 'Combine 100 splinters to create Chayula''s Breachstone.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(217, 218, 10, 2, 'upgrade_breach_item', 'Right click this item then left click an applicable breachstone item to upgrade it.', NULL, 'Upgrades Xoph''s Breachstone to a more powerful version', '[9159]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an applicable breachstone item to upgrade it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(218, 219, 10, 2, 'upgrade_breach_item', 'Right click this item then left click an applicable breachstone item to upgrade it.', NULL, 'Upgrades Tul''s Breachstone to a more powerful version', '[9159]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an applicable breachstone item to upgrade it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(219, 220, 10, 2, 'upgrade_breach_item', 'Right click this item then left click an applicable breachstone item to upgrade it.', NULL, 'Upgrades Esh''s Breachstone to a more powerful version', '[9159]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an applicable breachstone item to upgrade it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(220, 221, 10, 2, 'upgrade_breach_item', 'Right click this item then left click an applicable breachstone item to upgrade it.', NULL, 'Upgrades Uul-Netol''s Breachstone to a more powerful version', '[9159]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an applicable breachstone item to upgrade it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(221, 222, 10, 2, 'upgrade_breach_item', 'Right click this item then left click an applicable breachstone item to upgrade it.', NULL, 'Upgrades Chayula''s Breachstone to a more powerful version', '[9159]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an applicable breachstone item to upgrade it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(222, 223, 10, 2, 'upgrade_unique', 'Right click this item then left click a valid Breach Unique item to transform it.', NULL, 'Transforms a Breach Unique item to its alternate form', '[9159]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a valid Breach Unique item to transform it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(223, 224, 100, 3, '', 'Combine 100 Splinters to create a Timeless Karui Emblem.', 3055, '', '[]', 0, NULL, '[]', '[]', 10000, 'Combine 100 Splinters to create a Timeless Karui Emblem.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(224, 225, 100, 3, '', 'Combine 100 Splinters to create a Timeless Maraketh Emblem.', 3056, '', '[]', 0, NULL, '[]', '[]', 10000, 'Combine 100 Splinters to create a Timeless Maraketh Emblem.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(225, 226, 100, 3, '', 'Combine 100 Splinters to create a Timeless Eternal Emblem.', 3057, '', '[]', 0, NULL, '[]', '[]', 10000, 'Combine 100 Splinters to create a Timeless Eternal Emblem.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(226, 227, 100, 3, '', 'Combine 100 Splinters to create a Timeless Templar Emblem.', 3058, '', '[]', 0, NULL, '[]', '[]', 10000, 'Combine 100 Splinters to create a Timeless Templar Emblem.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(227, 228, 100, 3, '', 'Combine 100 Splinters to create a Timeless Vaal Emblem.', 3059, '', '[]', 0, NULL, '[]', '[]', 10000, 'Combine 100 Splinters to create a Timeless Vaal Emblem.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(228, 229, 300, 3, '', 'Combine 300 Splinters to create a Simulacrum.', 3065, '', '[]', 0, NULL, '[]', '[]', 10000, 'Combine 300 Splinters to create a Simulacrum.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(229, 230, 20, 2, 'remove_random_mod', 'Right click this item then left click on a magic or rare item to apply it.', NULL, 'Removes a random modifier from an item', '[11230]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a magic or rare item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(230, 231, 20, 2, 'transmute_to_rare_with_sockets', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to a rare item with up to four linked sockets', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(231, 232, 20, 3, '', 'This item is no longer usable.', NULL, 'Formerly reforged a map item as another of the same tier', '[]', 0, NULL, '[]', '[]', 10000, 'This item is no longer usable.', 0, '[7008]', '[]', '[]', 0, '', 0, 0, 0),
(232, 233, 20, 3, '', 'This item is no longer usable.', NULL, 'Formerly reforged a map item as another of a higher tier', '[10155]', 0, NULL, '[]', '[]', 10000, 'This item is no longer usable.', 0, '[7009]', '[]', '[]', 0, '', 0, 0, 0),
(233, 234, 20, 3, '', 'This item is no longer usable.', NULL, 'Formerly improved the quality of a Strongbox', '[]', 0, NULL, '[]', '[]', 10000, 'This item is no longer usable.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(234, 235, 20, 3, '', 'This item is no longer usable.', NULL, 'Formerly greatly improved the quality and rewards of a Strongbox and strengthened its defenders', '[]', 0, NULL, '[]', '[]', 10000, 'This item is no longer usable.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(235, 236, 20, 2, 'reroll_unique', 'Right click this item then left click a unique item to apply it.', NULL, 'Reforges a unique equipment as another of the same item class', '[9163]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a unique item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(236, 237, 20, 2, 'fracture_random_mod', 'Right click this item then left click a rare item to apply it. Cannot be used on Influenced or Fractured items.', NULL, 'Fracture a random modifier on a rare item with at least 4 modifiers, locking it in place', '[11658]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a rare item to apply it. Cannot be used on Influenced or Fractured items.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(237, 238, 20, 3, '', 'A stack of 20 shards becomes an Orb of Annulment.', 230, '', '[]', 0, NULL, '[]', '[]', 0, 'A stack of 20 shards becomes an Orb of Annulment', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(238, 239, 20, 3, '', 'A stack of 20 shards becomes an Orb of Binding.', 231, '', '[]', 0, NULL, '[]', '[]', 0, 'A stack of 20 shards becomes an Orb of Binding', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(239, 240, 20, 3, '', 'A stack of 20 shards becomes an Orb of Horizons.', 232, '', '[]', 0, NULL, '[]', '[]', 0, 'A stack of 20 shards becomes an Orb of Horizons', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(240, 241, 20, 3, '', 'A stack of 20 shards becomes a Harbinger''s Orb.', 233, '', '[]', 0, NULL, '[]', '[]', 0, 'A stack of 20 shards becomes a Harbinger''s Orb', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(241, 242, 20, 3, '', 'A stack of 20 shards becomes an Engineer''s Orb.', 234, '', '[]', 0, NULL, '[]', '[]', 0, 'A stack of 20 shards becomes an Engineer''s Orb', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(242, 243, 20, 3, '', 'A stack of 20 shards becomes an Ancient Orb.', 236, '', '[]', 0, NULL, '[]', '[]', 0, 'A stack of 20 shards becomes an Ancient Orb', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(243, 244, 20, 3, '', 'A stack of 20 shards becomes a Chaos Orb.', 2, '', '[]', 0, NULL, '[]', '[]', 0, 'A stack of 20 shards becomes a Chaos Orb', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(244, 245, 20, 3, '', 'A stack of 20 shards becomes a Mirror of Kalandra.', 6, '', '[]', 0, NULL, '[]', '[]', 0, 'A stack of 20 shards becomes a Mirror of Kalandra', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(245, 246, 20, 3, '', 'A stack of 20 shards becomes an Exalted Orb.', 14, '', '[]', 0, NULL, '[]', '[]', 0, 'A stack of 20 shards becomes an Exalted Orb', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(246, 247, 20, 3, '', 'A stack of 20 shards becomes a Regal Orb.', 15, '', '[]', 0, NULL, '[]', '[]', 0, 'A stack of 20 shards becomes a Regal Orb', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(247, 248, 20, 3, '', 'A stack of 20 shards becomes a Fracturing Orb.', 237, '', '[]', 0, NULL, '[]', '[]', 0, 'A stack of 20 shards becomes a Fracturing Orb', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(248, 2994, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains a Vaal Side Area' || char(13) || '
5% increased Quantity of Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '2% increased Quantity of Items found in Area', 0, 0, 0),
(249, 2995, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains a Vaal Side Area' || char(13) || '
5% increased Quantity of Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '2% increased Quantity of Items found in Area', 0, 0, 0),
(250, 2996, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains a Vaal Side Area' || char(13) || '
5% increased Quantity of Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '2% increased Quantity of Items found in Area', 0, 0, 0),
(251, 2997, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains a Vaal Side Area' || char(13) || '
5% increased Quantity of Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '2% increased Quantity of Items found in Area', 0, 0, 0),
(252, 2999, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains a Vaal Side Area' || char(13) || '
10% increased Quantity of Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '4% increased Quantity of Items found in Area', 0, 0, 0),
(253, 3000, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains a Vaal Side Area' || char(13) || '
10% increased Quantity of Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '4% increased Quantity of Items found in Area', 0, 0, 0),
(254, 3001, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains a Vaal Side Area' || char(13) || '
10% increased Quantity of Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '4% increased Quantity of Items found in Area', 0, 0, 0),
(255, 3002, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains a Vaal Side Area' || char(13) || '
10% increased Quantity of Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '4% increased Quantity of Items found in Area', 0, 0, 0),
(256, 3004, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(257, 3005, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(258, 3006, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(259, 3007, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(260, 3009, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(261, 3010, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(262, 3011, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(263, 3012, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(264, 3014, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(265, 3015, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(266, 3016, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(267, 3017, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(268, 3019, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(269, 3020, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(270, 3021, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(271, 3022, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(272, 3024, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(273, 3025, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(274, 3026, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(275, 3027, 10, 3, '', 'Can be used in a personal Map Device.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(276, 3029, 1, 3, '', 'Can be used in a personal Map Device, allowing you to capture the Soul of the Map''s Boss. The Vessel containing the captured Soul can be retrieved from the Map Device. You must be in the Map when the boss is defeated.', NULL, 'Unique Boss deals 10% increased Damage' || char(13) || '
Unique Boss has 10% increased Attack and Cast Speed' || char(13) || '
Unique Boss has 10% increased Life' || char(13) || '
Unique Boss has 20% increased Area of Effect', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device, allowing you to capture the Soul of the Map''s Boss. The Vessel containing the captured Soul can be retrieved from the Map Device. You must be in the Map when the boss is defeated.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(277, 3030, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(278, 3031, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(279, 3032, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(280, 3033, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(281, 3034, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(282, 3035, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(283, 3036, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(284, 3037, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(285, 3038, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(286, 3039, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(287, 3040, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(288, 3041, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(289, 3042, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(290, 3043, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(291, 3044, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(292, 3045, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(293, 3046, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(294, 3047, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(295, 3048, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(296, 3049, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(297, 3050, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(298, 3051, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(299, 3052, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(300, 3053, 1, 3, '', 'Can be traded to a vendor for a Hivebrain Gland.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(301, 3055, 1, 3, '', 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, '10% increased Experience Gain' || char(13) || '
20% increased Reward progress from Monster Kills' || char(13) || '
Monsters deal 10% increased Damage' || char(13) || '
Monsters have 10% increased Toughness for each time they have been Revived' || char(13) || '
Monsters gain 50% increased Critical Strike Chance each time they are Revived' || char(13) || '
Monsters gain +5% to Critical Strike Multiplier each time they are Revived', '[]', 0, NULL, '[]', '[]', 10000, 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(302, 3056, 1, 3, '', 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, '10% increased Experience Gain' || char(13) || '
20% increased Reward progress from Monster Kills' || char(13) || '
Monsters deal 10% increased Damage' || char(13) || '
Monsters have 10% increased Toughness for each time they have been Revived' || char(13) || '
Monsters have 5% increased Attack and Cast Speed for each time they have been Revived', '[]', 0, NULL, '[]', '[]', 10000, 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(303, 3057, 1, 3, '', 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, '10% increased Experience Gain' || char(13) || '
20% increased Reward progress from Monster Kills' || char(13) || '
Monsters deal 10% increased Damage' || char(13) || '
Monsters have 10% increased Toughness for each time they have been Revived' || char(13) || '
Monsters have 10% increased Accuracy Rating for each time they have been Revived' || char(13) || '
Monsters have 5% increased Area of Effect for each time they have been Revived', '[]', 0, NULL, '[]', '[]', 10000, 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(304, 3058, 1, 3, '', 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, '10% increased Experience Gain' || char(13) || '
20% increased Reward progress from Monster Kills' || char(13) || '
Monsters deal 10% increased Damage' || char(13) || '
Monsters have 10% increased Toughness for each time they have been Revived' || char(13) || '
Monsters Penetrate 3% of Enemy Elemental Resistances for each time
they have been Revived', '[]', 0, NULL, '[]', '[]', 10000, 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(305, 3059, 1, 3, '', 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, '10% increased Experience Gain' || char(13) || '
20% increased Reward progress from Monster Kills' || char(13) || '
Monsters deal 10% increased Damage' || char(13) || '
Monsters have 10% increased Toughness for each time they have been Revived' || char(13) || '
Monsters gain 5% of Physical Damage as Extra Chaos Damage for each time
they have been Revived', '[]', 0, NULL, '[]', '[]', 10000, 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(306, 3060, 1, 3, '', 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, 'Monsters gain 10% increased Maximum Life each time they are Revived' || char(13) || '
Monsters Regenerate 1% of Life per second for each time they have been Revived' || char(13) || '
Monsters gain +20% chance to Avoid being Stunned for each time they are Revived' || char(13) || '
10% increased Experience Gain' || char(13) || '
10% improved Rewards', '[]', 0, NULL, '[]', '[]', 10000, 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(307, 3061, 1, 3, '', 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, 'Monsters gain 10% increased Maximum Life each time they are Revived' || char(13) || '
Monsters gain 10% increased Attack and Cast Speed each time they are Revived' || char(13) || '
Monsters gain 10% increased Movement Speed each time they are Revived' || char(13) || '
10% increased Experience Gain' || char(13) || '
10% improved Rewards', '[]', 0, NULL, '[]', '[]', 10000, 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(308, 3062, 1, 3, '', 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, 'Monsters gain 10% increased Maximum Life each time they are Revived' || char(13) || '
Monsters gain 50% increased Critical Strike Chance each time they are Revived' || char(13) || '
Monsters gain 3% additional Critical Strike Multiplier each time they are Revived' || char(13) || '
10% increased Experience Gain' || char(13) || '
10% improved Rewards', '[]', 0, NULL, '[]', '[]', 10000, 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(309, 3063, 1, 3, '', 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, 'Monsters gain 10% increased Maximum Life each time they are Revived' || char(13) || '
Monsters gain 4% additional Physical Damage Reduction each time they are Revived' || char(13) || '
Monsters gain 4% additional Resistances each time they are Revived' || char(13) || '
10% increased Experience Gain' || char(13) || '
10% improved Rewards', '[]', 0, NULL, '[]', '[]', 10000, 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(310, 3064, 1, 3, '', 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, 'Monsters gain 10% increased Maximum Life each time they are Revived' || char(13) || '
Monsters deal 10% increased Damage for each time they have been Revived' || char(13) || '
10% increased Experience Gain' || char(13) || '
10% improved Rewards', '[]', 0, NULL, '[]', '[]', 10000, 'Place two or more different Emblems in a Map Device to access the Domain of Timeless Conflict. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(311, 3066, 1, 3, '', 'Travel to the Aspirants'' Plaza and spend this item to open the Eternal Labyrinth.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Travel to the Aspirants'' Plaza and spend this item to open the Eternal Labyrinth.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(312, 3067, 1, 3, '', 'Travel to the Aspirants'' Plaza and spend this item to open the Eternal Labyrinth of Fortune.', NULL, 'The Labyrinth''s rewards have been enriched.', '[]', 0, NULL, '[]', '[]', 10000, 'Travel to the Aspirants'' Plaza and spend this item to open the Eternal Labyrinth of Fortune.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(313, 3068, 1, 3, '', 'Travel to the Aspirants'' Plaza and spend this item to open the Eternal Labyrinth of Opportunity.', NULL, 'The Divine Font can be used additional times.', '[]', 0, NULL, '[]', '[]', 10000, 'Travel to the Aspirants'' Plaza and spend this item to open the Eternal Labyrinth of Opportunity.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(314, 3069, 1, 3, '', 'Travel to the Aspirants'' Plaza and spend this item to open the Eternal Labyrinth of Potential.', NULL, 'The Divine Font will offer additional Corrupted Gem crafting options.', '[]', 0, NULL, '[]', '[]', 10000, 'Travel to the Aspirants'' Plaza and spend this item to open the Eternal Labyrinth of Potential.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(315, 3065, 1, 3, '', 'Open a Delirium portal by using this item in a personal Map Device. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Open a Delirium portal by using this item in a personal Map Device. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(316, 4177, 1, 3, '', 'Open a portal to Absence of Mercy and Empathy by using this item in a personal Map Device. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Open a portal to Absence of Mercy and Empathy by using this item in a personal Map Device. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(317, 4178, 10, 3, '', 'Combine 10 Crescent Splinters to create The Maven''s Writ.', 4177, '', '[]', 0, NULL, '[]', '[]', 0, 'Combine 10 Crescent Splinters to create The Maven''s Writ.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(318, 4179, 1, 3, '', 'Use four of these in a personal Map Device to open Portals to The Shaper''s Realm. Can only be used once.', NULL, 'The Shaper''s Realm is level 85' || char(13) || '
The Shaper''s full power is unleashed in The Shaper''s Realm', '[]', 0, NULL, '[]', '[]', 10000, 'Use four of these in a personal Map Device to open Portals to The Shaper''s Realm. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(319, 4180, 1, 3, '', 'Use four of these in a personal Map Device to open Portals to The Shaper''s Realm. Can only be used once.', NULL, 'The Shaper''s Realm is level 85' || char(13) || '
The Shaper''s and The Elder''s full power are unleashed in The Shaper''s Realm', '[]', 0, NULL, '[]', '[]', 10000, 'Use four of these in a personal Map Device to open Portals to The Shaper''s Realm. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(320, 4181, 1, 3, '', 'Use four of these in a personal Map Device to open Portals to The Eye of the Storm. Can only be used once.', NULL, 'The Eye of the Storm is level 85' || char(13) || '
Sirus'' full power is unleashed in The Eye of the Storm', '[]', 0, NULL, '[]', '[]', 10000, 'Use four of these in a personal Map Device to open Portals to The Eye of the Storm. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(321, 4182, 1, 3, '', 'Use four of these in a personal Map Device to open Portals to The Cortex. Can only be used once.', NULL, 'The Cortex is level 85' || char(13) || '
Venarius'' full power is unleashed in the Cortex', '[]', 0, NULL, '[]', '[]', 10000, 'Use four of these in a personal Map Device to open Portals to The Cortex. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(322, 4183, 1, 3, '', 'Use four of these in a personal Map Device to open Portals to The Absence of Mercy and Empathy. Can only be used once.', NULL, 'The Absence of Mercy and Empathy is level 85' || char(13) || '
The Maven''s full power is unleashed in The Absence of Mercy and Empathy', '[]', 0, NULL, '[]', '[]', 10000, 'Use four of these in a personal Map Device to open Portals to The Absence of Mercy and Empathy. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(323, 4184, 1, 3, '', 'Use four of these in a personal Map Device to open Portals to The Absence of Symmetry and Harmony. Can only be used once.', NULL, 'The Absence of Symmetry and Harmony is level 85' || char(13) || '
The Eater of Worlds'' full power is unleashed in The Absence of Symmetry and Harmony', '[]', 0, NULL, '[]', '[]', 10000, 'Use four of these in a personal Map Device to open Portals to The Absence of Symmetry and Harmony. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(324, 4185, 1, 3, '', 'Use four of these in a personal Map Device to open Portals to The Absence of Patience and Wisdom. Can only be used once.', NULL, 'The Absence of Patience and Wisdom is level 85' || char(13) || '
The Searing Exarch''s full power is unleashed in The Absence of Patience and Wisdom', '[]', 0, NULL, '[]', '[]', 10000, 'Use four of these in a personal Map Device to open Portals to The Absence of Patience and Wisdom. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(325, 5354, 1, 3, '', 'Use four of these in a personal Map Device to open Portals to The Moment of Trauma. Can only be used once.', NULL, 'The Moment of Trauma is level 85' || char(13) || '
The Incarnation of Fear''s full power is unleashed in The Moment of Trauma', '[]', 0, NULL, '[]', '[]', 10000, 'Use four of these in a personal Map Device to open Portals to The Moment of Trauma. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(326, 5355, 1, 3, '', 'Use four of these in a personal Map Device to open Portals to The Moment of Reverence. Can only be used once.', NULL, 'The Moment of Reverence is level 85' || char(13) || '
The Incarnation of Dread''s full power is unleashed in The Moment of Reverence', '[]', 0, NULL, '[]', '[]', 10000, 'Use four of these in a personal Map Device to open Portals to The Moment of Reverence. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(327, 5356, 1, 3, '', 'Use four of these in a personal Map Device to open Portals to The Moment of Loneliness. Can only be used once.', NULL, 'The Moment of Loneliness is level 85' || char(13) || '
The Incarnation of Neglect''s full power is unleashed in The Moment of Loneliness', '[]', 0, NULL, '[]', '[]', 10000, 'Use four of these in a personal Map Device to open Portals to The Moment of Loneliness. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(328, 4212, 1, 3, '', 'Open a portal to the Sacred Grove by using this item in a personal Map Device. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Open a portal to the Sacred Grove by using this item in a personal Map Device. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(329, 4213, 1, 3, '', 'Open a portal to the Hive Colony by using this item in a personal Map Device. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Open a portal to the Hive Colony by using this item in a personal Map Device. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(330, 4214, 1, 3, '', 'Open a portal into the Black Barya by using this item in a personal Map Device. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Open a portal into the Black Barya by using this item in a personal Map Device. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(331, 260, 10, 3, '', 'Sacrifice this item on the Altar of Sacrifice along with Architect''s Hand to transform it. ', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(332, 261, 10, 3, '', 'Sacrifice this item on the Altar of Sacrifice along with Mask of the Spirit Drinker to transform it. ', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(333, 262, 10, 3, '', 'Sacrifice this item on the Altar of Sacrifice along with Apep''s Slumber to transform it. ', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(334, 263, 10, 3, '', 'Sacrifice this item on the Altar of Sacrifice along with Dance of the Offered to transform it. ', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(335, 264, 10, 3, '', 'Sacrifice this item on the Altar of Sacrifice along with Story of the Vaal to transform it. ', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(336, 265, 10, 3, '', 'Sacrifice this item on the Altar of Sacrifice along with Coward''s Chains to transform it. ', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(337, 266, 10, 3, '', 'Sacrifice this item on the Altar of Sacrifice along with Soul Catcher to transform it. ', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(338, 267, 10, 3, '', 'Sacrifice this item on the Altar of Sacrifice along with any Tempered Flesh, Tempered Spirit or Tempered Mind to transform it. ', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(339, 268, 10, 3, '', 'Sacrifice this item on the Altar of Sacrifice along with Sacrificial Heart to transform it. ', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(340, 269, 20, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'More Fire modifiers' || char(13) || '
No Cold modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(341, 270, 20, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'More Cold modifiers' || char(13) || '
No Fire modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(342, 271, 20, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'More Lightning modifiers' || char(13) || '
No Physical modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(343, 272, 20, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'More Physical modifiers' || char(13) || '
No Chaos modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(344, 273, 20, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'More Chaos modifiers' || char(13) || '
No Lightning modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(345, 274, 20, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'More Life modifiers' || char(13) || '
No Defence modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(346, 275, 20, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'More Defence modifiers' || char(13) || '
No Life Modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(347, 276, 20, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'More Physical Ailment or Chaos Ailment modifiers' || char(13) || '
No Elemental modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(348, 277, 20, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'More Elemental modifiers' || char(13) || '
No Physical Ailment or Chaos Ailment modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(349, 278, 20, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'More Caster modifiers' || char(13) || '
Fewer Attack modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(350, 279, 20, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'More Attack modifiers' || char(13) || '
Fewer Caster modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(351, 280, 20, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'More Mana modifiers' || char(13) || '
No Speed modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(352, 281, 20, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'More Speed modifiers' || char(13) || '
No Mana modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(353, 282, 20, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'More Minion, Aura or Curse modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(354, 283, 20, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'More Drop modifiers' || char(13) || '
No Tagless modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(355, 284, 20, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'More Critical modifiers' || char(13) || '
No Attribute modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(356, 285, 20, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'More Attribute modifiers' || char(13) || '
No Critical modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(357, 286, 10, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'More Gem modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(358, 287, 10, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'Corrupted' || char(13) || '
Has a Corrupted implicit modifier', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(359, 288, 10, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'Has an Abyssal socket', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(360, 289, 10, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'Fracture a random modifier' || char(13) || '
Cannot be used on Influenced or Fractured items', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(361, 290, 10, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'Has a Corrupt Essence modifier', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(362, 291, 10, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'Makes a random modifier type much more likely and prevents ' || char(13) || '
another random modifier type' || char(13) || '
Effects revealed once resonator is fully socketed', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(363, 292, 10, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'Numeric modifier values are lucky' || char(13) || '
High Level modifiers are more common', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(364, 293, 10, 3, '', 'Place in a Resonator to influence item crafting.', NULL, 'Item is overvalued by vendors', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(365, 294, 1, 2, 'delve_currency_upgrade', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to a rare item', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(366, 295, 1, 2, 'delve_currency_upgrade', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to a rare item', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(367, 296, 1, 2, 'delve_currency_upgrade', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to a rare item', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(368, 297, 1, 2, 'delve_currency_upgrade', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to a rare item', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(369, 298, 1, 2, 'delve_currency_reroll', 'Right click this item then left click a rare item to apply it.', NULL, 'Reforges a rare item with new random modifiers', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a rare item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(370, 299, 1, 2, 'delve_currency_reroll', 'Right click this item then left click a rare item to apply it.', NULL, 'Reforges a rare item with new random modifiers', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a rare item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(371, 300, 1, 2, 'delve_currency_reroll', 'Right click this item then left click a rare item to apply it.', NULL, 'Reforges a rare item with new random modifiers', '[9953]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a rare item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(372, 301, 1, 2, 'delve_currency_reroll', 'Right click this item then left click a rare item to apply it.', NULL, 'Reforges a rare item with new random modifiers', '[9953]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a rare item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(373, 302, 10, 2, 'delve_currency_upgrade', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to a rare item', '[7720,8322]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(374, 303, 10, 2, 'delve_currency_upgrade', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to a rare item', '[7720,8322]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(375, 304, 10, 2, 'delve_currency_upgrade', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to a rare item', '[7720,8322]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(376, 305, 10, 2, 'delve_currency_upgrade', 'Right click this item then left click a normal item to apply it.', NULL, 'Upgrades a normal item to a rare item', '[7720,8322]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(377, 306, 10, 2, 'delve_currency_reroll', 'Right click this item then left click a rare item to apply it.', NULL, 'Reforges a rare item with new random modifiers', '[7720,8322]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a rare item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(378, 307, 10, 2, 'delve_currency_reroll', 'Right click this item then left click a rare item to apply it.', NULL, 'Reforges a rare item with new random modifiers', '[7720,8322]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a rare item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(379, 308, 10, 2, 'delve_currency_reroll', 'Right click this item then left click a rare item to apply it.', NULL, 'Reforges a rare item with new random modifiers', '[7720,8322,9953]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a rare item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(380, 309, 10, 2, 'delve_currency_reroll', 'Right click this item then left click a rare item to apply it.', NULL, 'Reforges a rare item with new random modifiers', '[7720,8322,9953]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a rare item to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(381, 3550, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Essence item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(382, 3551, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Currency item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(383, 3552, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Unique item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(384, 3553, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Map item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(385, 3554, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Unique Map item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(386, 3555, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Rare Abyss item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(387, 3556, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Fragment item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(388, 3557, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Scarab item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(389, 3558, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Essence item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(390, 3559, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Fossil item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(391, 3560, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Expedition item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(392, 3561, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds incubated Stacked Decks to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(393, 3562, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Talisman item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(394, 3563, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Labyrinth item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(395, 3564, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated 6-Linked Armour item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(396, 3565, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Currency item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(397, 3566, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated League-specific Unique item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(398, 3567, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Shaper or Elder Armour item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(399, 3568, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Shaper or Elder Weapon item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(400, 3569, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Shaper or Elder Trinket item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(401, 3570, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Elder Map item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(402, 3571, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Breach item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(403, 3572, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds incubated Currency items to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(404, 3573, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated high level Corrupted Gem or valuable Gem item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(405, 3574, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(406, 3575, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Quality Gem item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(407, 3576, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Bestiary item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(408, 3577, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Blight item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(409, 3578, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Ultimatum item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(410, 3579, 1, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Delirium item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(411, 3580, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Essence item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(412, 3581, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Currency item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(413, 3582, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Unique item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(414, 3583, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Map item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(415, 3584, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Unique Map item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(416, 3585, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Rare Abyss item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(417, 3586, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Fragment item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(418, 3587, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Scarab item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(419, 3588, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Essence item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(420, 3589, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Fossil item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(421, 3590, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Expedition item to an equippable item ' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(422, 3591, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds incubated Stacked Decks to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(423, 3592, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Talisman item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(424, 3593, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Labyrinth item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(425, 3594, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated 6-Linked Armour item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(426, 3595, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Currency item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(427, 3596, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated League-specific Unique item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(428, 3597, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Shaper or Elder Armour item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(429, 3598, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Shaper or Elder Weapon item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(430, 3599, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Shaper or Elder Trinket item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(431, 3600, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Elder Map item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(432, 3601, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Breach item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(433, 3602, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds incubated Currency items to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(434, 3603, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated high level Corrupted Gem or valuable Gem item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(435, 3604, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(436, 3605, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Quality Gem item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(437, 3606, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Bestiary item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(438, 3607, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Blight item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(439, 3608, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Ultimatum item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(440, 3609, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Delirium item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(441, 3610, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Coin item or Gold to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[11293]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(442, 3611, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Valuable Djinn Coin item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[11293]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(443, 3612, 10, 2, 'apply_incubator', 'Right click this item then left click an item to apply it. The Incubated item drops after killing a specific number of monsters.', NULL, 'Adds an incubated Empowered Mirage Map item to an equippable item' || char(13) || '
Item drops after killing {0} monsters', '[11293]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it. The Incubated item drops after killing a specific number of monsters.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(444, 329, 1, 2, 'add_gem_experience', 'Right click this item then left click a gem to apply it.', NULL, 'Adds stored experience to a gem, up to its maximum level', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a gem to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(445, 330, 1, 2, 'add_weapon_passive_tree_experience', 'Right click this item then left click an item with a Crucible Passive Skill Tree to apply it.', NULL, 'Adds stored Crucible experience to an item', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item with a Crucible Passive Skill Tree to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(446, 344, 100, 1, 'grant_micro', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(447, 345, 100, 1, 'grant_micro', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(448, 346, 100, 1, 'grant_micro', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(449, 347, 100, 1, 'grant_micro', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(450, 348, 100, 1, 'grant_micro', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(451, 349, 1, 1, 'open_box', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(452, 361, 1, 1, 'open_box', 'Right click on this item to open it.', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to open.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(453, 362, 1, 1, 'open_box', 'Right click on this item to open it.', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to open.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(454, 363, 1, 1, 'open_box', 'Right click on this item to open it.', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to open.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(455, 364, 1, 1, 'open_box', 'Right click on this item to open it.', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to open.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(456, 365, 1, 1, 'open_box', 'Right click on this item to open it.', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to open.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(457, 366, 1, 1, 'open_box', 'Right click on this item to open it.', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to open.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(458, 367, 1, 1, 'open_box', 'Right click on this item to open it.', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to open.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(459, 368, 1, 1, 'open_box', 'Right click on this item to open it.', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to open.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(460, 369, 1, 1, 'open_box', 'Right click on this item to open it.', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to open.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(461, 370, 1, 1, 'open_box', 'Right click on this item to open it.', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to open.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(462, 371, 1, 1, 'open_box', 'Right click on this item to open it.', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to open.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(463, 3549, 1, 1, 'open_box', '', NULL, 'Contains an unknown Leaguestone.', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(464, 3086, 10, 1, 'add_snake_oil_buff', 'Right click on this item to use it. Cannot be removed from hideout.', NULL, 'Exiles that consume Serpent Extract daily are sane exiles. ', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to use.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(465, 3087, 7, 3, '', '', NULL, '<currencyitem>{3x Chaos Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(466, 3088, 8, 3, '', '', NULL, '<uniqueitem>{Headhunter}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(467, 3089, 9, 3, '', '', NULL, '<uniqueitem>{Lifesprig}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(468, 3090, 7, 3, '', '', NULL, '<rareitem>{Two-Stone Ring}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(469, 3091, 6, 3, '', '', NULL, '<whiteitem>{Prismatic Ring}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(470, 3092, 5, 3, '', '', NULL, '<uniqueitem>{Axe}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(471, 3093, 5, 3, '', '', NULL, '<uniqueitem>{Nightmare Bascinet}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(472, 3094, 3, 3, '', '', NULL, '<currencyitem>{40x Scroll of Wisdom}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(473, 3095, 8, 3, '', '', NULL, '<uniqueitem>{Voll''s Devotion}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(474, 3096, 9, 3, '', '', NULL, '<uniqueitem>{Blood of Corruption}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(475, 3097, 4, 3, '', '', NULL, '<magicitem>{Life Armour}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(476, 3098, 5, 3, '', '', NULL, '<uniqueitem>{Prismatic Ring}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(477, 3099, 11, 3, '', '', NULL, '<uniqueitem>{Headhunter}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(478, 3100, 3, 3, '', '', NULL, '<uniqueitem>{The Goddess Bound}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(479, 3101, 5, 3, '', '', NULL, '<currencyitem>{10x Orb of Regret}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(480, 3102, 6, 3, '', '', NULL, '<gemitem>{Minion Gem}' || char(13) || '
<default>{Quality:} <augmented>{+20%}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(481, 3103, 13, 3, '', '', NULL, '<gemitem>{Level 21 Spell Gem}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(482, 3104, 9, 3, '', '', NULL, '<uniqueitem>{Taste of Hate}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(483, 3105, 12, 3, '', '', NULL, '<currencyitem>{Exalted Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(484, 3106, 8, 3, '', '', NULL, '<uniqueitem>{Divination Distillate}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(485, 3107, 7, 3, '', '', NULL, '<uniqueitem>{Rise of the Phoenix}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(486, 3108, 4, 3, '', '', NULL, '<rareitem>{Amulet}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(487, 3109, 9, 3, '', '', NULL, '<uniqueitem>{Wake of Destruction}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(488, 3110, 6, 3, '', '', NULL, '<whiteitem>{Six-Link Staff}' || char(13) || '
<default>{Item Level:} <normal>{55}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(489, 3111, 3, 3, '', '', NULL, '<currencyitem>{Gemcutter''s Prism}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(490, 3112, 5, 3, '', '', NULL, '<divination>{Divination Card}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(491, 3113, 2, 3, '', '', NULL, '<rareitem>{Jewellery}' || char(13) || '
<default>{Item Level:} <normal>{79}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(492, 3114, 1, 3, '', '', NULL, '<currencyitem>{5x Chaos Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(493, 3115, 7, 3, '', '', NULL, '<magicitem>{Runic One-Hand Weapon}' || char(13) || '
<default>{Item Level:} <normal>{100}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(494, 3116, 12, 3, '', '', NULL, '<uniqueitem>{Mjölner}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(495, 3117, 5, 3, '', '', NULL, '<currencyitem>{5x Currency}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(496, 3118, 10, 3, '', '', NULL, '<uniqueitem>{League-Specific Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(497, 3119, 7, 3, '', '', NULL, '<currencyitem>{20x Orb of Fusing}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(498, 3120, 5, 3, '', '', NULL, '<uniqueitem>{Romira''s Banquet}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(499, 3121, 8, 3, '', '', NULL, '<currencyitem>{Chaos Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0);
INSERT INTO "CurrencyItems" ("_row", "BaseItemTypesKey", "StackSize", "CurrencyUseType", "Action", "Directions", "FullStack_BaseItemTypesKey", "Description", "Usage_AchievementItemsKeys", "Scroll", "Possession_AchievementItemsKey", "__col10", "__col11", "CurrencyTab_StackSize", "XBoxDirections", "__col14", "ModifyMapsAchievements", "ModifyContractsAchievements", "CombineAchievements", "ChangedForHardmode", "DescriptionHardmode", "IsGold", "__col21", "__col22") VALUES
(500, 3122, 12, 3, '', '', NULL, '<uniqueitem>{Crown of Eyes}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(501, 3123, 4, 3, '', '', NULL, '<uniqueitem>{The Whispering Ice}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(502, 3124, 9, 3, '', '', NULL, '<currencyitem>{5x Regal Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(503, 3125, 8, 3, '', '', NULL, '<uniqueitem>{Kaom''s Heart}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(504, 3126, 5, 3, '', '', NULL, '<currencyitem>{3x Exalted Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(505, 3127, 7, 3, '', '', NULL, '<uniqueitem>{Windripper}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(506, 3128, 9, 3, '', '', NULL, '<uniqueitem>{Pledge of Hands}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(507, 3129, 3, 3, '', '', NULL, '<gemitem>{Gem}' || char(13) || '
<default>{Quality:} <augmented>{+20%}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(508, 3130, 6, 3, '', '', NULL, '<whiteitem>{Six-Link Astral Plate}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(509, 3131, 11, 3, '', '', NULL, '<whiteitem>{Six-Link Body Armour}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(510, 3132, 11, 3, '', '', NULL, '<gemitem>{Level 4 Enhance}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(511, 3133, 6, 3, '', '', NULL, '<currencyitem>{10x Vaal Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(512, 3134, 6, 3, '', '', NULL, '<uniqueitem>{Alpha''s Howl}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(513, 3135, 4, 3, '', '', NULL, '<uniqueitem>{Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(514, 3136, 7, 3, '', '', NULL, '<currencyitem>{10x Gemcutter''s Prism}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(515, 3137, 16, 3, '', '', NULL, '<uniqueitem>{Atziri''s Acuity}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(516, 3138, 9, 3, '', '', NULL, '<uniqueitem>{Tabula Rasa}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(517, 3139, 6, 3, '', '', NULL, '<rareitem>{Map}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(518, 3140, 4, 3, '', '', NULL, '<magicitem>{Dictator''s Prophecy Wand}' || char(13) || '
<default>{Item Level:} <normal>{100}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(519, 3141, 9, 3, '', '', NULL, '<uniqueitem>{Maligaro''s Virtuosity}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(520, 3142, 5, 3, '', '', NULL, '<whiteitem>{Five-Link Staff}' || char(13) || '
<default>{Item Level:} <normal>{66}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(521, 3143, 7, 3, '', '', NULL, '<uniqueitem>{The Whispering Ice}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(522, 3144, 6, 3, '', '', NULL, '<uniqueitem>{Le Heup of All}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(523, 3145, 10, 3, '', '', NULL, '<uniqueitem>{Atziri''s Disfavour}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(524, 3146, 9, 3, '', '', NULL, '<uniqueitem>{Doedre Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(525, 3147, 8, 3, '', '', NULL, '<uniqueitem>{Map}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(526, 3148, 8, 3, '', '', NULL, '<currencyitem>{10x Chaos Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(527, 3149, 5, 3, '', '', NULL, '<size:31>{<magicitem>{Bloodthirsty Eternal Sword}' || char(13) || '
<default>{Item Level:} <normal>{66}}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(528, 3150, 7, 3, '', '', NULL, '<rareitem>{Map}' || char(13) || '
<default>{Map Tier:} <normal>{15}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(529, 3151, 4, 3, '', '', NULL, '<uniqueitem>{Sword}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(530, 3152, 9, 3, '', '', NULL, '<currencyitem>{Mirror of Kalandra}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(531, 3153, 11, 3, '', '', NULL, '<gemitem>{Level 4 Enlighten}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(532, 3154, 3, 3, '', '', NULL, '<currencyitem>{Vaal Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(533, 3155, 7, 3, '', '', NULL, '<uniqueitem>{Song of the Sirens}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(534, 3156, 9, 3, '', '', NULL, '<gemitem>{Level 20 Support Gem}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(535, 3157, 11, 3, '', '', NULL, '<gemitem>{Level 4 Empower}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(536, 3158, 6, 3, '', '', NULL, '<gemitem>{Level 20 Gem}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(537, 3159, 2, 3, '', '', NULL, '<gemitem>{Mirror Arrow}' || char(13) || '
<default>{Quality:} <augmented>{+20%}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(538, 3160, 3, 3, '', '', NULL, '<currencyitem>{7x Orb of Alchemy}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(539, 3161, 4, 3, '', '', NULL, '<uniqueitem>{Mon''tregul''s Grasp}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(540, 3162, 6, 3, '', '', NULL, '<gemitem>{Summon Raging Spirit}' || char(13) || '
<default>{Quality:} <augmented>{+20%}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(541, 3163, 9, 3, '', '', NULL, '<size:31>{<magicitem>{Merciless One-Hand Weapon}' || char(13) || '
<default>{Item Level:} <normal>{100}}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(542, 3164, 5, 3, '', '', NULL, '<uniqueitem>{Shield}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(543, 3165, 4, 3, '', '', NULL, '<magicitem>{Seraphim''s Armour}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(544, 3166, 5, 3, '', '', NULL, '<currencyitem>{3x Orb of Fusing}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(545, 3167, 8, 3, '', '', NULL, '<uniqueitem>{Kaom''s Heart}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(546, 3168, 5, 3, '', '', NULL, '<uniqueitem>{Doryani''s Fist}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(547, 3169, 9, 3, '', '', NULL, '<uniqueitem>{Dagger}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(548, 3170, 8, 3, '', '', NULL, '<uniqueitem>{Bow}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(549, 3171, 7, 3, '', '', NULL, '<uniqueitem>{Mao Kun}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(550, 3172, 9, 3, '', '', NULL, '<uniqueitem>{Doryani''s Fist}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(551, 3173, 6, 3, '', '', NULL, '<uniqueitem>{Staff}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(552, 3174, 8, 3, '', '', NULL, '<magicitem>{Gemini Claw of Celebration}' || char(13) || '
<default>{Item Level:} <normal>{83}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(553, 3175, 13, 3, '', '', NULL, '<uniqueitem>{Voltaxic Rift}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(554, 3176, 12, 3, '', '', NULL, '<uniqueitem>{Midnight Bargain}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(555, 3177, 8, 3, '', '', NULL, '<uniqueitem>{Shavronne''s Revelation}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(556, 3178, 8, 3, '', '', NULL, '<uniqueitem>{Shavronne Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(557, 3179, 6, 3, '', '', NULL, '<gemitem>{Level 3 Enlighten}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(558, 3180, 3, 3, '', '', NULL, '<uniqueitem>{The Taming}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(559, 3181, 5, 3, '', '', NULL, '<uniqueitem>{Drillneck}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(560, 3182, 4, 3, '', '', NULL, '<whiteitem>{Map}' || char(13) || '
<default>{Map Tier:} <normal>{14}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(561, 3183, 6, 3, '', '', NULL, '<whiteitem>{Six-Link Harbinger Bow}' || char(13) || '
<default>{Item Level:} <normal>{91}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(562, 3184, 4, 3, '', '', NULL, '<magicitem>{Surgeon''s Flask}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(563, 3185, 9, 3, '', '', NULL, '<gemitem>{Vaal Gem}' || char(13) || '
<default>{Quality:} <augmented>{+20%}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(564, 3186, 3, 3, '', '', NULL, '<whiteitem>{Mortal Hope}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(565, 3187, 5, 3, '', '', NULL, '<uniqueitem>{Ring}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(566, 3188, 7, 3, '', '', NULL, '<uniqueitem>{Ring}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(567, 3189, 4, 3, '', '', NULL, '<size:31>{<magicitem>{Merciless Two-Hand Weapon}' || char(13) || '
<default>{Item Level:} <normal>{100}}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(568, 3190, 1, 3, '', '', NULL, 'Disabled', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(569, 3191, 6, 3, '', '', NULL, '<whiteitem>{Six-Link Coronal Maul}' || char(13) || '
<default>{Item Level:} <normal>{83}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(570, 3192, 5, 3, '', '', NULL, '<uniqueitem>{Death''s Hand}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(571, 3193, 4, 3, '', '', NULL, '<uniqueitem>{Wand}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(572, 3194, 8, 3, '', '', NULL, '<uniqueitem>{Shavronne''s Wrappings}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(573, 3195, 8, 3, '', '', NULL, '<whiteitem>{Map}' || char(13) || '
<default>{Map Tier:} <normal>{15}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(574, 3196, 6, 3, '', '', NULL, '<magicitem>{Ring of Bameth}' || char(13) || '
<default>{Item Level:} <normal>{83}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(575, 3197, 6, 3, '', '', NULL, '<uniqueitem>{Fertile Mind}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(576, 3198, 4, 3, '', '', NULL, '<uniqueitem>{Body Armour}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(577, 3199, 3, 3, '', '', NULL, '<uniqueitem>{Pacifism}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(578, 3200, 8, 3, '', '', NULL, '<uniqueitem>{Gold Ring}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(579, 3201, 7, 3, '', '', NULL, '<whiteitem>{Six-Link Vaal Regalia}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(580, 3202, 10, 3, '', '', NULL, '<magicitem>{Perandus'' Gold Ring}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(581, 3203, 3, 3, '', '', NULL, '<magicitem>{Unassailable Amulet}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(582, 3204, 6, 3, '', '', NULL, '<whiteitem>{Six-Link Body Armour}' || char(13) || '
<default>{Item Level:} <normal>{100}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(583, 3205, 4, 3, '', '', NULL, '<magicitem>{Malicious Gemini Claw}' || char(13) || '
<default>{Item Level:} <normal>{83}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(584, 3206, 9, 3, '', '', NULL, '<uniqueitem>{Soul Taker}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(585, 3207, 5, 3, '', '', NULL, '<uniqueitem>{Lioneye Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(586, 3208, 4, 3, '', '', NULL, '<uniqueitem>{Coruscating Elixir}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(587, 3209, 8, 3, '', '', NULL, '<uniqueitem>{Rat''s Nest}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(588, 3210, 3, 3, '', '', NULL, '<uniqueitem>{Ventor''s Gamble}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(589, 3211, 7, 3, '', '', NULL, '<uniqueitem>{Voltaxic Rift}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(590, 3212, 4, 3, '', '', NULL, '<whiteitem>{Sacrifice Fragment}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(591, 3213, 7, 3, '', '', NULL, '<uniqueitem>{Vaults of Atziri}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(592, 3214, 5, 3, '', '', NULL, '<uniqueitem>{Granite Flask}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(593, 3215, 3, 3, '', '', NULL, '<uniqueitem>{Hyaon''s Fury}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(594, 3216, 10, 3, '', '', NULL, '<whiteitem>{Map}' || char(13) || '
<default>{Map Tier:} <normal>{12}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(595, 3217, 11, 3, '', '', NULL, '<currencyitem>{10x Divine Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(596, 3218, 6, 3, '', '', NULL, '<uniqueitem>{Lioneye''s Vision}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(597, 3219, 5, 3, '', '', NULL, '<uniqueitem>{Storm Cloud}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(598, 3220, 8, 3, '', '', NULL, '<gemitem>{Level 19 Transfigured Gem}' || char(13) || '
<default>{Quality:} <augmented>{+19%}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(599, 3221, 10, 3, '', '', NULL, '<divination>{House of Mirrors}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(600, 3223, 4, 3, '', '', NULL, '<whiteitem>{Map}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(601, 3222, 2, 3, '', '', NULL, '<uniqueitem>{Kaom''s Roots}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(602, 3224, 8, 3, '', '', NULL, '<uniqueitem>{Atziri''s Disfavour}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(603, 3225, 5, 3, '', '', NULL, '<rareitem>{Body Armour}' || char(13) || '
<default>{Item Level:} <normal>{100}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(604, 3226, 4, 3, '', '', NULL, '<magicitem>{Vivid Jewel}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(605, 3227, 2, 3, '', '', NULL, '<uniqueitem>{Two-Stone Ring}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(606, 3228, 9, 3, '', '', NULL, '<whiteitem>{Map}' || char(13) || '
<default>{Map Tier:} <normal>{6}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(607, 3229, 1, 3, '', '', NULL, ' ', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(608, 3230, 9, 3, '', '', NULL, '<magicitem>{Merciless Weapon}' || char(13) || '
<default>{Item Level:} <normal>{100}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(609, 3231, 8, 3, '', '', NULL, '<magicitem>{Weapon of Crafting}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(610, 3232, 11, 3, '', '', NULL, '<uniqueitem>{The Harvest}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(611, 3233, 5, 3, '', '', NULL, '<uniqueitem>{Sire of Shards}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(612, 3234, 7, 3, '', '', NULL, '<whiteitem>{Six-Link Holy Chainmail}' || char(13) || '
<default>{Item Level:} <normal>{60}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(613, 3235, 8, 3, '', '', NULL, '<uniqueitem>{Rigwald''s Curse}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(614, 3236, 4, 3, '', '', NULL, '<uniqueitem>{Agnerod Staff}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(615, 3237, 6, 3, '', '', NULL, '<uniqueitem>{Agate Amulet}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(616, 3238, 3, 3, '', '', NULL, '<normal>{Map}' || char(13) || '
<default>{Map Tier:} <normal>{5}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(617, 3239, 20, 1, 'create_random_divination_card', 'Right click to take a divination card out of the deck.  
 Hold Ctrl + Right Click to repeatedly move a card from the stack into your inventory.', NULL, 'A stack of unknown divination cards', '[11571]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to take a divination card out of the deck. 
 Hold <<xbox_button_a>> to repeatedly move a card from the stack into your inventory.', 0, '[]', '[]', '[]', 0, '', 0, 1, 0),
(618, 3240, 9, 3, '', '', NULL, '<currencyitem>{10x Stacked Deck}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(619, 3241, 2, 3, '', '', NULL, '<uniqueitem>{Crystal Sceptre}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(620, 3242, 1, 3, '', '', NULL, '<uniqueitem>{Vaal Gauntlets}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(621, 3243, 4, 3, '', '', NULL, '<magicitem>{Vivid Crimson Jewel}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(622, 3244, 5, 3, '', '', NULL, '<uniqueitem>{Unset Ring}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(623, 3245, 5, 3, '', '', NULL, '<uniqueitem>{Rigwald Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(624, 3246, 7, 3, '', '', NULL, '<size:31>{<gemitem>{Support Gem}' || char(13) || '
<default>{Quality:} <augmented>{+23%}' || char(13) || '
<corrupted>{Corrupted}}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(625, 3247, 6, 3, '', '', NULL, '<uniqueitem>{Beyond Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(626, 3248, 8, 3, '', '', NULL, '<uniqueitem>{Nemesis Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(627, 3249, 7, 3, '', '', NULL, '<uniqueitem>{Varunastra}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(628, 3250, 6, 3, '', '', NULL, '<uniqueitem>{Death''s Oath}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(629, 3251, 8, 3, '', '', NULL, '<uniqueitem>{Carcass Jack}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(630, 3252, 16, 3, '', '', NULL, '<uniqueitem>{Eyes of the Greatwolf}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(631, 3253, 6, 3, '', '', NULL, '<whiteitem>{Six-Link Short Bow}' || char(13) || '
<default>{Item Level:} <normal>{50}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(632, 3254, 8, 3, '', '', NULL, '<uniqueitem>{Lightning Coil}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(633, 3255, 3, 3, '', '', NULL, '<uniqueitem>{Astramentis}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(634, 3256, 4, 3, '', '', NULL, '<uniqueitem>{Claw}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(635, 3257, 5, 3, '', '', NULL, '<uniqueitem>{Gloves}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(636, 3258, 6, 3, '', '', NULL, '<uniqueitem>{Belt}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(637, 3259, 8, 3, '', '', NULL, '<uniqueitem>{Rigwald''s Quills}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(638, 3260, 3, 3, '', '', NULL, '<uniqueitem>{Rustic Sash}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(639, 3261, 7, 3, '', '', NULL, '<uniqueitem>{Umbilicus Immortalis}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(640, 3262, 4, 3, '', '', NULL, '<uniqueitem>{Jewel}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(641, 3263, 16, 3, '', '', NULL, '<rareitem>{Vaal Temple Map}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(642, 3264, 2, 3, '', '', NULL, '<uniqueitem>{Berek''s Respite}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(643, 3265, 10, 3, '', '', NULL, '<currencyitem>{2x Exalted Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(644, 3266, 5, 3, '', '', NULL, '<rareitem>{Talisman}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(645, 3267, 9, 3, '', '', NULL, 'Disabled', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(646, 3268, 9, 3, '', '', NULL, '<uniqueitem>{Trypanon}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(647, 3269, 5, 3, '', '', NULL, '<rareitem>{Ring}' || char(13) || '
<default>{Item Level:} <normal>{100}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(648, 3270, 3, 3, '', '', NULL, '<magicitem>{Electrocuting Jewellery}' || char(13) || '
<default>{Item Level:} <normal>{76}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(649, 3271, 4, 3, '', '', NULL, '<uniqueitem>{Weapon}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(650, 3272, 7, 3, '', '', NULL, '<uniqueitem>{Meginord''s Girdle}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(651, 3273, 9, 3, '', '', NULL, '<currencyitem>{30x Orb of Scouring}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(652, 3274, 5, 3, '', '', NULL, '<gemitem>{Portal}' || char(13) || '
<default>{Quality:} <augmented>{+1-20%}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(653, 3275, 10, 3, '', '', NULL, '<uniqueitem>{Jewel}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(654, 3276, 6, 3, '', '', NULL, '<uniqueitem>{Chin Sol}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(655, 3277, 4, 3, '', '', NULL, '<rareitem>{Map}' || char(13) || '
<default>{Map Tier:} <normal>{16}' || char(13) || '
<corrupted>{Unidentified Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(656, 3278, 27, 3, '', '', NULL, '<uniqueitem>{Charan''s Sword}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(657, 3279, 3, 3, '', '', NULL, '<uniqueitem>{The Harvest}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(658, 3280, 7, 3, '', '', NULL, 'Disabled', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(659, 3281, 9, 3, '', '', NULL, '<currencyitem>{4x Tainted Mythic Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(660, 3282, 4, 3, '', '', NULL, '<uniqueitem>{Balefire}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(661, 3283, 4, 3, '', '', NULL, '<uniqueitem>{Breach Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(662, 3284, 6, 3, '', '', NULL, '<uniqueitem>{Chayula Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(663, 3285, 8, 3, '', '', NULL, '<uniqueitem>{Starforge}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(664, 3286, 4, 3, '', '', NULL, '<uniqueitem>{Belt of the Deceiver}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(665, 3287, 3, 3, '', '', NULL, '<magicitem>{Elreon''s Jewellery}' || char(13) || '
<default>{Item Level:} <normal>{85}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(666, 3288, 5, 3, '', '', NULL, 'Disabled', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(667, 3289, 8, 3, '', '', NULL, '<uniqueitem>{Kiara''s Determination}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(668, 3290, 6, 3, '', '', NULL, '<whiteitem>{Six-Linked Body Armour}' || char(13) || '
<default>{Influenced Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(669, 3291, 5, 3, '', '', NULL, '<whiteitem>{Five-Linked Body Armour}' || char(13) || '
<default>{Influenced Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(670, 3292, 6, 3, '', '', NULL, '<uniqueitem>{Severed in Sleep}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(671, 3293, 4, 3, '', '', NULL, 'Disabled', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(672, 3294, 8, 3, '', '', NULL, '<uniqueitem>{Lightpoacher}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(673, 3295, 5, 3, '', '', NULL, '<uniqueitem>{Nemesis Item}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(674, 3296, 9, 3, '', '', NULL, '<uniqueitem>{Atziri Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(675, 3297, 7, 3, '', '', NULL, '<uniqueitem>{Daresso''s Salute}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(676, 3298, 6, 3, '', '', NULL, '<uniqueitem>{Bloodbond}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(677, 3299, 10, 3, '', '', NULL, '<whiteitem>{Opal Ring}' || char(13) || '
<default>{Item Level:} <normal>{100}' || char(13) || '
<default>{Shaper Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(678, 3300, 5, 3, '', '', NULL, '<rareitem>{Jewellery}' || char(13) || '
<default>{Item Level:} <normal>{100}' || char(13) || '
<default>{Shaper Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(679, 3301, 8, 3, '', '', NULL, '<uniqueitem>{Poorjoy''s Asylum}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(680, 3302, 9, 3, '', '', NULL, '<currencyitem>{9x Shrieking Essence}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(681, 3303, 3, 3, '', '', NULL, '<uniqueitem>{Watcher''s Eye}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(682, 3304, 4, 3, '', '', NULL, '<rareitem>{Item}' || char(13) || '
<default>{Item Level:} <normal>{100}' || char(13) || '
<default>{Elder Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(683, 3305, 5, 3, '', '', NULL, '<uniqueitem>{The Light of Meaning}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(684, 3306, 8, 3, '', '', NULL, '<magicitem>{Merciless Vaal Axe}' || char(13) || '
<default>{Item Level:} <normal>{100}' || char(13) || '
<default>{Elder Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(685, 3307, 5, 3, '', '', NULL, '<gemitem>{Level 21 Golem Gem}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(686, 3308, 3, 3, '', '', NULL, '<currencyitem>{3x Essence}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(687, 3309, 4, 3, '', '', NULL, '<uniqueitem>{The Putrid Cloister}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(688, 3310, 6, 3, '', '', NULL, '<uniqueitem>{Belly of the Beast}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(689, 3311, 10, 3, '', '', NULL, '<currencyitem>{40x Orb of Regret}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(690, 3312, 7, 3, '', '', NULL, '<gemitem>{Level 21 Aura Gem}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(691, 3313, 9, 3, '', '', NULL, '<uniqueitem>{Voidforge}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(692, 3314, 5, 3, '', '', NULL, '<uniqueitem>{Atziri''s Reflection}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(693, 3315, 5, 3, '', '', NULL, '<size:28>{<rareitem>{Elder Guardian Occupied Map}' || char(13) || '
<default>{Modifiers:} <normal>{8}' || char(13) || '
<corrupted>{Corrupted}}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(694, 3316, 6, 3, '', '', NULL, '<uniqueitem>{Bestiary Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(695, 3317, 6, 3, '', '', NULL, '<uniqueitem>{The Twilight Temple}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(696, 3318, 8, 3, '', '', NULL, '<currencyitem>{3x Deafening Essence}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(697, 3319, 4, 3, '', '', NULL, '<whiteitem>{Six-Link Sacrificial Garb}' || char(13) || '
<default>{Item Level:} <normal>{100}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(698, 3320, 9, 3, '', '', NULL, '<uniqueitem>{The Poet''s Pen}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(699, 3321, 8, 3, '', '', NULL, '<divination>{The Doctor}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(700, 3322, 5, 3, '', '', NULL, '<currencyitem>{20x Quality Currency}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(701, 3323, 6, 3, '', '', NULL, '<uniqueitem>{Zerphi''s Heart}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(702, 3324, 9, 3, '', '', NULL, '<currencyitem>{3x Orb of Annulment}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(703, 3325, 4, 3, '', '', NULL, '<uniqueitem>{Harbinger Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(704, 3326, 7, 3, '', '', NULL, '<uniqueitem>{Vaal Aspect}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(705, 3327, 6, 3, '', '', NULL, '<whiteitem>{Offering to the Goddess}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(706, 3328, 4, 3, '', '', NULL, '<currencyitem>{Journey Tattoo}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(707, 3329, 8, 3, '', '', NULL, '<uniqueitem>{Item}' || char(13) || '
 <corrupted>{Two-Implicit}' || char(13) || '
 <corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(708, 3330, 5, 3, '', '', NULL, 'Disabled', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(709, 3331, 6, 3, '', '', NULL, '<magicitem>{Jewel of Potency}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(710, 3332, 13, 3, '', '', NULL, '<currencyitem>{Mirror Shard}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(711, 3333, 7, 3, '', '', NULL, '<magicitem>{Flaring Eclipse Staff}' || char(13) || '
<default>{Item Level:} <normal>{100}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(712, 3334, 3, 3, '', '', NULL, '<currencyitem>{Valdo''s Puzzle Box}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(713, 3335, 5, 3, '', '', NULL, '<uniqueitem>{Obliteration}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(714, 3336, 5, 3, '', '', NULL, '<uniqueitem>{Delve Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(715, 3337, 3, 3, '', '', NULL, '<whiteitem>{Mortal Fragment}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(716, 3338, 4, 3, '', '', NULL, '<magicitem>{Sceptre of Celebration}' || char(13) || '
<default>{Shaper Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(717, 3339, 9, 3, '', '', NULL, '<uniqueitem>{Tabula Rasa}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(718, 3340, 22, 3, '', '', NULL, '<whiteitem>{Six-Link Imperial Bow}' || char(13) || '
<default>{Item Level:} <normal>{100}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(719, 3341, 6, 3, '', '', NULL, '<uniqueitem>{Xoph''s Blood}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(720, 3342, 5, 3, '', '', NULL, '<uniqueitem>{Jewel}' || char(13) || '
<magicitem>{Primordial}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(721, 3343, 3, 3, '', '', NULL, '<uniqueitem>{Fidelitas'' Spike}' || char(13) || '
 <corrupted>{Two-Implicit}' || char(13) || '
 <corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(722, 3344, 5, 3, '', '', NULL, '<uniqueitem>{Gluttony}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(723, 3345, 7, 3, '', '', NULL, '<currencyitem>{10x Exalted Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(724, 3346, 4, 3, '', '', NULL, '<currencyitem>{20x Orb of Chance}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(725, 3347, 7, 3, '', '', NULL, '<uniqueitem>{Farrul''s Fur}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(726, 3348, 5, 3, '', '', NULL, '<uniqueitem>{Tidebreaker}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(727, 3349, 7, 3, '', '', NULL, '<magicitem>{Glyphic Prophecy Wand}' || char(13) || '
<default>{Item Level:} <normal>{100}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(728, 3350, 4, 3, '', '', NULL, '<rareitem>{Bone Helmet}' || char(13) || '
<default>{Elder Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(729, 3351, 3, 3, '', '', NULL, '<whiteitem>{Sulphite Scarab}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(730, 3352, 7, 3, '', '', NULL, '<currencyitem>{Orb of Annulment}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(731, 3353, 12, 3, '', '', NULL, '<rareitem>{Fishing Rod}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(732, 3354, 4, 3, '', '', NULL, '<rareitem>{Bone Helmet}' || char(13) || '
<default>{Item Level:} <normal>{100}' || char(13) || '
<default>{Elder Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(733, 3355, 5, 3, '', '', NULL, '<whiteitem>{Incarnation Echo}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(734, 3356, 4, 3, '', '', NULL, '<uniqueitem>{Skin of the Loyal}' || char(13) || '
<size:26>{<default>{Item Level:} <normal>{25}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(735, 3357, 1, 3, '', '', NULL, '<currencyitem>{17x Chromatic Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(736, 3358, 8, 3, '', '', NULL, '<uniqueitem>{Precursor''s Emblem}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(737, 3359, 7, 3, '', '', NULL, '<whiteitem>{4x Scarab}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(738, 3360, 5, 3, '', '', NULL, '<uniqueitem>{Doomfletch}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(739, 3361, 5, 3, '', '', NULL, 'Disabled', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(740, 3362, 9, 3, '', '', NULL, '<uniqueitem>{Prismatic Jewel}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(741, 3363, 6, 3, '', '', NULL, '<gemitem>{Level 1 Summon Skeletons}' || char(13) || '
<default>{Quality:} <augmented>{+23%}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(742, 3364, 8, 3, '', '', NULL, '<uniqueitem>{Mageblood}' || char(13) || '
<brequelmutated> {Foulborn}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(743, 3365, 10, 3, '', '', NULL, '<uniqueitem>{Headhunter}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(744, 3366, 3, 3, '', '', NULL, '<uniqueitem>{Elegant Round Shield}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(745, 3367, 6, 3, '', '', NULL, '<uniqueitem>{Soul Ripper}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(746, 3368, 3, 3, '', '', NULL, '<magicitem>{Overpowering Opal Ring}' || char(13) || '
<default>{Item Level:} <normal>{100}' || char(13) || '
<default>{Elder Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(747, 3369, 5, 3, '', '', NULL, '<whiteitem>{Cartography Scarab}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(748, 3370, 4, 3, '', '', NULL, '<uniqueitem>{Vaults of Atziri}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(749, 3371, 8, 3, '', '', NULL, '<uniqueitem>{Fishing Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(750, 3372, 6, 3, '', '', NULL, '<uniqueitem>{Bottled Faith}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(751, 3373, 4, 3, '', '', NULL, '<currencyitem>{10x Grand Eldritch Ichor}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(752, 3374, 5, 3, '', '', NULL, '<uniqueitem>{Seven-League Step}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(753, 3375, 7, 3, '', '', NULL, '<gemitem>{Level 21 Trap Gem}' || char(13) || '
<default>{Quality:} <augmented>{+23%}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(754, 3376, 5, 3, '', '', NULL, '<uniqueitem>{Skin of the Lords}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(755, 3377, 2, 3, '', '', NULL, '<whiteitem>{Scarab}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(756, 3378, 1, 3, '', '', NULL, '<uniqueitem>{Grand Spectrum}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(757, 3379, 4, 3, '', '', NULL, '<whiteitem>{Albino Rhoa Feather}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(758, 3380, 6, 3, '', '', NULL, '<gemitem>{Level 21 Vaal Summon Skeletons}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(759, 3381, 3, 3, '', '', NULL, '<uniqueitem>{Aul''s Uprising}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(760, 3382, 4, 3, '', '', NULL, '<uniqueitem>{Unending Hunger}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(761, 3383, 9, 3, '', '', NULL, '<uniqueitem>{Rigwald''s Quills}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(762, 3384, 5, 3, '', '', NULL, '<uniqueitem>{The Light of Meaning}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(763, 3385, 6, 3, '', '', NULL, '<uniqueitem>{Inspired Learning}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(764, 3386, 3, 3, '', '', NULL, 'Disabled', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(765, 3387, 5, 3, '', '', NULL, '<currencyitem>{5x Fossil}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(766, 3388, 2, 3, '', '', NULL, '<rareitem>{Map}' || char(13) || '
<default>{Map Tier:} <normal>{15}' || char(13) || '
<default>{Modifiers:} <normal>{8}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(767, 3389, 2, 3, '', '', NULL, '<uniqueitem>{Dying Sun}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(768, 3390, 4, 3, '', '', NULL, '<whiteitem>{Uber Elder Fragment}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(769, 3391, 4, 3, '', '', NULL, '<uniqueitem>{Farrul Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(770, 3392, 2, 3, '', '', NULL, '<size:30>{<magicitem>{Diamond Ring of Redemption}' || char(13) || '
<default>{Item Level:} <normal>{100}' || char(13) || '
<default>{Redeemer Item}}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(771, 3393, 6, 3, '', '', NULL, '<uniqueitem>{Machina Mitts}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(772, 3394, 5, 3, '', '', NULL, '<uniqueitem>{Asenath''s Gentle Touch}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(773, 3395, 4, 3, '', '', NULL, '<uniqueitem>{Asenath''s Mark}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(774, 3396, 10, 3, '', '', NULL, '<uniqueitem>{The Saviour}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(775, 3397, 8, 3, '', '', NULL, '<whiteitem>{Six-Link Astral Plate}' || char(13) || '
<default>{Item Level:} <normal>{100}' || char(13) || '
<default>{Crusader Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(776, 3398, 6, 3, '', '', NULL, '<rareitem>{One-Hand Weapon}' || char(13) || '
<default>{Item Level:} <normal>{100}' || char(13) || '
<default>{Double-Influenced Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(777, 3399, 3, 3, '', '', NULL, 'Disabled', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(778, 3400, 5, 3, '', '', NULL, '<uniqueitem>{Timeless Jewel}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(779, 3401, 6, 3, '', '', NULL, '<rareitem>{Jewellery}' || char(13) || '
<default>{Item Level:} <normal>{86}' || char(13) || '
<default>{Double-Influenced Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(780, 3402, 3, 3, '', '', NULL, '<magicitem>{Jewellery of Farrul}' || char(13) || '
<default>{Item Level:} <normal>{100}' || char(13) || '
<default>{Shaper + Hunter Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(781, 3403, 4, 3, '', '', NULL, '<currencyitem>{Elderslayer''s Exalted Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(782, 3404, 5, 3, '', '', NULL, '<uniqueitem>{Thread of Hope}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(783, 3405, 3, 3, '', '', NULL, '<gemitem>{Level 21 Chaos Gem}' || char(13) || '
<default>{Quality:} <augmented>{+23%}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(784, 3406, 13, 3, '', '', NULL, '<size:26>{<rareitem>{Map}}' || char(13) || '
<size:26>{<default>{Map Tier:} <normal>{13}' || char(13) || '
<default>{Delirium:} <normal>{100%}' || char(13) || '
<default>{Modifiers:} <normal>{8}' || char(13) || '
<corrupted>{Corrupted}}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(785, 3407, 16, 3, '', '', NULL, '<currencyitem>{19x Mirror Shard}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(786, 3408, 6, 3, '', '', NULL, '<uniqueitem>{Jewellery}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(787, 3409, 8, 3, '', '', NULL, '<uniqueitem>{Inspired Learning}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(788, 3410, 9, 3, '', '', NULL, '<gemitem>{Level 4 Exceptional Support Gem}' || char(13) || '
<default>{Quality:} <augmented>{+23%}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(789, 3411, 1, 3, '', '', NULL, '<currencyitem>{5x Exalted Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(790, 3412, 4, 3, '', '', NULL, '<uniqueitem>{Facebreaker}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(791, 3413, 2, 3, '', '', NULL, '<size:29>{<uniqueitem>{Attribute Transforming Jewel}}' || char(13) || '
<size:29>{<corrupted>{Corrupted}}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(792, 3414, 13, 3, '', '', NULL, '<uniqueitem>{Maw of Mischief}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(793, 3415, 10, 3, '', '', NULL, '<rareitem>{Helmet}' || char(13) || '
<default>{Double-Veiled Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(794, 3416, 4, 3, '', '', NULL, '<uniqueitem>{Metamorph Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(795, 3417, 2, 3, '', '', NULL, '<currencyitem>{20x Orb of Alteration}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(796, 3418, 8, 3, '', '', NULL, '<uniqueitem>{Cospri''s Malice}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(797, 3419, 1, 3, '', '', NULL, '<currencyitem>{10x Orb of Alteration}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(798, 3420, 10, 3, '', '', NULL, '<rareitem>{Diamond Ring}' || char(13) || '
<default>{Item Level:} <normal>{100}' || char(13) || '
<default>{Shaper + Elder Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(799, 3421, 9, 3, '', '', NULL, '<uniqueitem>{League-Specific Item}' || char(13) || '
<default>{Double-Influenced Item}' || char(13) || '
<default>{Item Level:} <normal>{97}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(800, 3422, 7, 3, '', '', NULL, '<size:27>{<uniqueitem>{The Eternity Shroud}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(801, 3423, 7, 3, '', '', NULL, '<magicitem>{Eldritch Bone Helmet (Concentrated Effect)}' || char(13) || '
<default>{Item Level:} <normal>{89}' || char(13) || '
<default>{Elder Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(802, 3424, 6, 3, '', '', NULL, '<uniqueitem>{Omeyocan}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(803, 3425, 4, 3, '', '', NULL, '<uniqueitem>{Unnatural Instinct}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(804, 3426, 5, 3, '', '', NULL, '<whiteitem>{Six-Link Astral Plate}' || char(13) || '
<default>{Item Level:} <normal>{100}' || char(13) || '
<default>{Influenced Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(805, 3427, 8, 3, '', '', NULL, '<divination>{The Nurse}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(806, 3428, 3, 3, '', '', NULL, '<uniqueitem>{The Fourth Vow}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(807, 3429, 6, 3, '', '', NULL, '<magicitem>{Fecund Ursine Pelt}' || char(13) || '
<default>{Warlord Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(808, 3430, 5, 3, '', '', NULL, '<uniqueitem>{One With Nothing}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(809, 3431, 2, 3, '', '', NULL, '<uniqueitem>{Circle of Fear}' || char(13) || '
<enchanted>{Three-Implicit}' || char(13) || '
<enchanted>{Synthesised}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(810, 3432, 3, 3, '', '', NULL, '<uniqueitem>{Tabula Rasa}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(811, 3433, 3, 3, '', '', NULL, '<uniqueitem>{Victario''s Influence}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(812, 3434, 2, 3, '', '', NULL, 'Disabled', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(813, 3435, 5, 3, '', '', NULL, '<divination>{The Artist}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(814, 3436, 6, 3, '', '', NULL, '<uniqueitem>{Torrent''s Reclamation}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(815, 3437, 6, 3, '', '', NULL, '<uniqueitem>{Victario''s Influence}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(816, 3438, 4, 3, '', '', NULL, '<uniqueitem>{Timeless Jewel}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(817, 3439, 7, 3, '', '', NULL, '<uniqueitem>{Item}' || char(13) || '
<default>{Influenced Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(818, 3440, 1, 3, '', '', NULL, '<magicitem>{Quicksilver Flask of the Cheetah}' || char(13) || '
<default>{Item Level:} <normal>{100}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(819, 3441, 4, 3, '', '', NULL, '<whiteitem>{Divination Scarab}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(820, 3442, 3, 3, '', '', NULL, '<uniqueitem>{Megalomaniac}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(821, 3443, 7, 3, '', '', NULL, '<uniqueitem>{Voices}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(822, 3444, 8, 3, '', '', NULL, '<gemitem>{Level 21 Transfigured Gem}' || char(13) || '
<default>{Quality:} <augmented>{+23%}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(823, 3445, 6, 3, '', '', NULL, '<uniqueitem>{Bino''s Kitchen Knife}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(824, 3446, 6, 3, '', '', NULL, 'Disabled', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(825, 3447, 3, 3, '', '', NULL, '<currencyitem>{13x Orb of Alteration}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(826, 3448, 9, 3, '', '', NULL, '<uniqueitem>{Ryslatha''s Coil}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(827, 3449, 5, 3, '', '', NULL, '<rareitem>{Vaal Temple Map}' || char(13) || '
<default>{Delirium:} <normal>{100%}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(828, 3450, 7, 3, '', '', NULL, '<uniqueitem>{Astral Projector}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(829, 3451, 8, 3, '', '', NULL, '<gemitem>{Level 21 Transfigured Golem Gem}' || char(13) || '
<default>{Quality:} <augmented>{+23%}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(830, 3452, 4, 3, '', '', NULL, '<gemitem>{Level 21 Transfigured Gem}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(831, 3453, 6, 3, '', '', NULL, '<uniqueitem>{Item}' || char(13) || '
<default>{Double-Influenced Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(832, 3454, 3, 3, '', '', NULL, '<rareitem>{Jewel}' || char(13) || '
<enchanted>{Implicit Modifier:} ' || char(13) || '
<magicitem>{Corrupted Blood cannot be inflicted on you}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(833, 3455, 11, 3, '', '', NULL, '<currencyitem>{10x Delirium Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(834, 3456, 4, 3, '', '', NULL, '<currencyitem>{Skittering Delirium Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(835, 3457, 3, 3, '', '', NULL, '<uniqueitem>{Prism Guardian}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(836, 3458, 5, 3, '', '', NULL, '<uniqueitem>{Jewel}' || char(13) || '
<magicitem>{Primordial}' || char(13) || '
<corrupted>{1-2 Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(837, 3459, 12, 3, '', '', NULL, '<uniqueitem>{Vaal Jewel}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(838, 3460, 8, 3, '', '', NULL, '<currencyitem>{10x Incursion Vial}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(839, 3461, 4, 3, '', '', NULL, '<uniqueitem>{Synthesis Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(840, 3462, 6, 3, '', '', NULL, '<uniqueitem>{Elegant Hubris}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(841, 3463, 18, 3, '', '', NULL, '<whiteitem>{Fishing Rod}' || char(13) || '
<default>{Item Level:} <normal>{99}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(842, 3464, 4, 3, '', '', NULL, '<uniqueitem>{Replica Cortex}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(843, 3465, 5, 3, '', '', NULL, '<currencyitem>{Delirium Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(844, 3466, 8, 3, '', '', NULL, '<currencyitem>{7x Exalted Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(845, 3467, 4, 3, '', '', NULL, '<whiteitem>{Shaper Guardian Map}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(846, 3468, 6, 3, '', '', NULL, '<uniqueitem>{Leather Belt}' || char(13) || '
<default>{Double-Influenced Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(847, 3469, 7, 3, '', '', NULL, '<uniqueitem>{Bramblejack}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(848, 3470, 5, 3, '', '', NULL, '<magicitem>{Item}' || char(13) || '
<default>{Elevated Item}' || char(13) || '
<default>{Influenced Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(849, 3471, 9, 3, '', '', NULL, '<uniqueitem>{Forbidden Shako}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(850, 3472, 3, 3, '', '', NULL, '<rareitem>{Cluster Jewel}' || char(13) || '
<default>{Item Level:} <normal>{84}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(851, 3473, 7, 3, '', '', NULL, '<uniqueitem>{The Pandemonius}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(852, 3474, 6, 3, '', '', NULL, '<uniqueitem>{Shroud of the Lightless}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(853, 3475, 8, 3, '', '', NULL, '<currencyitem>{10x Oil}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(854, 3476, 5, 3, '', '', NULL, '<uniqueitem>{Synthesis Map}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(855, 3477, 8, 3, '', '', NULL, 'Disabled', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(856, 3478, 2, 3, '', '', NULL, '<whiteitem>{Body Armour}' || char(13) || '
<default>{Item Level:} <normal>{100}' || char(13) || '
<default>{Double-Influenced Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(857, 3479, 7, 3, '', '', NULL, '<uniqueitem>{Replica Bated Breath}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(858, 3480, 5, 3, '', '', NULL, '<currencyitem>{Tainted Mythic Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(859, 3481, 5, 3, '', '', NULL, 'Disabled', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(860, 3482, 5, 3, '', '', NULL, '<currencyitem>{Astrolabe}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(861, 3483, 6, 3, '', '', NULL, '<gemitem>{Level 21 Vaal Molten Shell}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(862, 3484, 3, 3, '', '', NULL, '<currencyitem>{Simulacrum}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(863, 3485, 4, 3, '', '', NULL, '<magicitem>{Helmet}' || char(13) || '
<default>{Double-Influenced Item}' || char(13) || '
<default>{Item Level:} <normal>{100}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(864, 3486, 5, 3, '', '', NULL, '<magicitem>{Merciless Tornado Wand}' || char(13) || '
<default>{Item Level:} <normal>{100}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(865, 3487, 4, 3, '', '', NULL, '<uniqueitem>{Cortex}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(866, 3488, 9, 3, '', '', NULL, '<uniqueitem>{Watcher''s Eye}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(867, 3489, 4, 3, '', '', NULL, '<whiteitem>{Horned Scarab}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(868, 3490, 6, 3, '', '', NULL, '<uniqueitem>{Replica Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(869, 3491, 5, 3, '', '', NULL, '<uniqueitem>{Mageblood}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(870, 3492, 3, 3, '', '', NULL, '<gemitem>{Exceptional Support Gem}' || char(13) || '
<default>{Quality:} <augmented>{+1-20%}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(871, 3493, 7, 3, '', '', NULL, '<uniqueitem>{Belt}' || char(13) || '
<default>{Double-Influenced Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(872, 3494, 8, 3, '', '', NULL, '<uniqueitem>{Ngamahu''s Flame}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(873, 3495, 3, 3, '', '', NULL, '<currencyitem>{5x Eldritch Chaos Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(874, 3496, 5, 3, '', '', NULL, '<uniqueitem>{Cold Iron Point}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(875, 3497, 3, 3, '', '', NULL, '<gemitem>{Level 21 Transfigured Gem}' || char(13) || '
<default>{Quality:} <augmented>{+20%}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(876, 3498, 7, 3, '', '', NULL, '<uniqueitem>{Maloney''s Mechanism}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(877, 3499, 8, 3, '', '', NULL, '<uniqueitem>{The Squire}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(878, 3500, 6, 3, '', '', NULL, '<uniqueitem>{Stranglegasp}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(879, 3501, 4, 3, '', '', NULL, '<magicitem>{Spiked Gloves of the Conquest (Culling Strike)}' || char(13) || '
<default>{Item Level:} <normal>{86}' || char(13) || '
<default>{Warlord Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(880, 3502, 3, 3, '', '', NULL, '<uniqueitem>{Boots}' || char(13) || '
<default>{Item Level:} <normal>{100}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(881, 3503, 4, 3, '', '', NULL, '<magicitem>{Punishing Map}' || char(13) || '
<default>{Map Tier:} <normal>{16}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(882, 3504, 7, 3, '', '', NULL, '<uniqueitem>{Maven Item}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(883, 3505, 7, 3, '', '', NULL, '<uniqueitem>{Mageblood}' || char(13) || '
<default>{Quality:} <augmented>{+20%}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(884, 3506, 5, 3, '', '', NULL, '<whiteitem>{Shield}' || char(13) || '
<default>{Shaper Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(885, 3507, 3, 3, '', '', NULL, '<gemitem>{Empower, Enhance or Enlighten}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(886, 3508, 8, 3, '', '', NULL, '<currencyitem>{76x Simulacrum Splinter}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(887, 3509, 6, 3, '', '', NULL, '<uniqueitem>{Nebulis}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(888, 3510, 5, 3, '', '', NULL, '<whiteitem>{Blueprint}' || char(13) || '
<default>{Area Level:} <normal>{83}' || char(13) || '
<default>{Fully Revealed}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(889, 3511, 3, 3, '', '', NULL, '<whiteitem>{Bestiary Scarab}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(890, 3512, 6, 3, '', '', NULL, '<uniqueitem>{Queen of the Forest}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(891, 3513, 4, 3, '', '', NULL, '<whiteitem>{Experimented Item}' || char(13) || '
<default>{Item Level:} <normal>{100}' || char(13) || '
<default>{Double-Influenced Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(892, 3514, 12, 3, '', '', NULL, '<currencyitem>{7x Divine Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(893, 3515, 2, 3, '', '', NULL, '<uniqueitem>{Arakaali''s Fang}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(894, 3516, 9, 3, '', '', NULL, '<uniqueitem>{Mageblood}' || char(13) || '
<default>{Item Level:} <normal>{84}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(895, 3517, 3, 3, '', '', NULL, '<currencyitem>{10x Orb of Unmaking}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(896, 3518, 5, 3, '', '', NULL, '<rareitem>{Item}' || char(13) || '
<default>{Item Level:} <normal>{100}' || char(13) || '
<fractured>{Fractured}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(897, 3519, 3, 3, '', '', NULL, '<uniqueitem>{Tavukai}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(898, 3520, 2, 3, '', '', NULL, '<currencyitem>{1x Fertile Catalyst}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(899, 3521, 1, 3, '', 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', NULL, 'The next Lieutenant you hit will be killed instantly', '[]', 0, NULL, '[]', '[]', 0, 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(900, 3522, 1, 3, '', 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', NULL, 'The next Gargoyle you hit will be killed instantly', '[]', 0, NULL, '[]', '[]', 0, 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(901, 3523, 1, 3, '', 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', NULL, 'The next Conduit you interact with will be disabled permanently', '[]', 0, NULL, '[]', '[]', 0, 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(902, 3524, 1, 3, '', 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', NULL, 'The next Essence you hit will be killed instantly', '[]', 0, NULL, '[]', '[]', 0, 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(903, 3525, 1, 3, '', 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', NULL, 'The next Font you hit will be killed instantly', '[]', 0, NULL, '[]', '[]', 0, 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(904, 3526, 1, 3, '', 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', NULL, 'NO LONGER USED', '[]', 0, NULL, '[]', '[]', 0, 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(905, 3527, 1, 3, '', 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', NULL, 'The next Idol you hit will no longer regenerate life', '[]', 0, NULL, '[]', '[]', 0, 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(906, 3528, 1, 3, '', 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', NULL, 'The next Charge Disruptor you use will reactivate after a short duration', '[]', 0, NULL, '[]', '[]', 0, 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(907, 3529, 1, 3, '', 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', NULL, 'NO LONGER USED', '[]', 0, NULL, '[]', '[]', 0, 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(908, 3530, 1, 3, '', 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', NULL, 'The next Portal you hit will be killed instantly', '[]', 0, NULL, '[]', '[]', 0, 'This item will be used automatically when entering the next Izaro encounter. This item will drop on the ground when leaving the Labyrinth.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(909, 249, 100, 1, 'bestiary_change_net', '', NULL, 'Effective against Beasts of levels 1 to 13.' || char(13) || '
Activate to use this type of Net when capturing Beasts.', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(910, 250, 100, 1, 'bestiary_change_net', '', NULL, 'Effective against Beasts of levels 7 to 23.' || char(13) || '
Activate to use this type of Net when capturing Beasts.', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(911, 251, 100, 1, 'bestiary_change_net', '', NULL, 'Effective against Beasts of levels 15 to 35.' || char(13) || '
Activate to use this type of Net when capturing Beasts.', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(912, 252, 100, 1, 'bestiary_change_net', '', NULL, 'Effective against Beasts of levels 25 to 46.' || char(13) || '
Activate to use this type of Net when capturing Beasts.', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(913, 253, 100, 1, 'bestiary_change_net', '', NULL, 'Effective against Beasts of levels 37 to 56.' || char(13) || '
Activate to use this type of Net when capturing Beasts.', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(914, 254, 100, 1, 'bestiary_change_net', '', NULL, 'Effective against Beasts of levels 48 to 66.' || char(13) || '
Activate to use this type of Net when capturing Beasts.', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(915, 255, 100, 1, 'bestiary_change_net', '', NULL, 'Effective against Beasts of levels 58 to 70.' || char(13) || '
Activate to use this type of Net when capturing Beasts.', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(916, 256, 100, 1, 'bestiary_change_net', '', NULL, 'Effective against Beasts of levels 60 to 75.' || char(13) || '
Activate to use this type of Net when capturing Beasts.', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(917, 257, 100, 1, 'bestiary_change_net', '', NULL, 'Effective against Beasts of levels 64 and above.' || char(13) || '
Activate to use this type of Net when capturing Beasts.', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(918, 258, 100, 1, 'bestiary_change_net', '', NULL, 'Effective against Beasts of levels 68 and above.' || char(13) || '
Activate to use this type of Net when capturing Beasts.', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(919, 259, 100, 1, 'bestiary_change_net', '', NULL, 'Can be used on Beast corpses of all levels.' || char(13) || '
Activate to use this type of Net when capturing Beasts.', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(920, 3659, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an additional Breach', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Breach', 0, 0, 0),
(921, 3660, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an additional Breach' || char(13) || '
Breaches in Area contain an additional Breach Boss'' Clasped Hand', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Breach', 0, 0, 0),
(922, 3661, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an additional Breach' || char(13) || '
Breaches in Area contain an additional Breach Boss'' Clasped Hand' || char(13) || '
Breach Hands in Area drop double Breach Splinters', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Breach', 0, 0, 0),
(923, 3662, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 2 additional Breaches' || char(13) || '
Breaches in Area contain an additional Breach Boss'' Clasped Hand' || char(13) || '
Breach Hands in Area drop double Breach Splinters', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Breach', 0, 0, 0),
(924, 3663, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains a Breach Hive', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains a Breach Hive', 0, 0, 0),
(925, 3664, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Breaches in Area will always belong to Chayula', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Breaches in Area will always belong to Chayula', 0, 0, 0),
(926, 3665, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 2 additional Unstable Breaches', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Unstable Breach', 0, 0, 0),
(927, 3666, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Unstable Breaches in Area contain a Boss' || char(13) || '
Breach Hives in Area lead to a Hive Fortress', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Unstable Breaches in Area contain a Boss' || char(13) || '
Breach Hives in Area lead to a Hive Fortress', 0, 0, 0),
(928, 3667, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Ailith''s skills in Area instead enrage the Hive, increasing the Difficulty
and Reward of subsequent waves', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Ailith''s skills in Area instead enrage the Hive, increasing the Difficulty
and Reward of subsequent waves', 0, 0, 0),
(929, 3668, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Unstable Breaches in Areas are 10% faster for each Unstable Breach already opened
Monsters from Unstable Breaches in Areas have increased Difficulty and Reward for
each Unstable Breach already opened', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Unstable Breaches in Areas are 10% faster for each Unstable Breach already opened
Monsters from Unstable Breaches in Areas have increased Difficulty and Reward for
each Unstable Breach already opened', 0, 0, 0),
(930, 3669, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '20% increased Maps found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '20% increased Maps found in Area', 0, 0, 0),
(931, 3670, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '35% increased Maps found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '20% increased Maps found in Area', 0, 0, 0),
(932, 3671, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '50% increased Maps found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '20% increased Maps found in Area', 0, 0, 0),
(933, 3672, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '85% increased Maps found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '20% increased Maps found in Area', 0, 0, 0),
(934, 3673, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '10% increased Maps found in Area for each Map Modifier affecting Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '10% increased Maps found in Area for each Map Modifier affecting Area', 0, 0, 0),
(935, 3674, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area has 2 additional random Modifiers', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area has 2 additional random Modifiers', 0, 0, 0),
(936, 3677, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 8 to 12 additional packs of
Difficult and Rewarding Monsters which drop 300% increased Maps', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains 4 to 6 additional packs of
Difficult and Rewarding Monsters which drop 300% increased Maps', 0, 0, 0),
(937, 3676, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Non-Unique Maps found in Area are Corrupted with 8 Modifiers', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Non-Unique Maps found in Area are Corrupted with 8 Modifiers', 0, 0, 0),
(938, 3675, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'A Unique Map will drop from the Final Map Boss', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'DISABLED', 0, 0, 0),
(939, 3678, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '50% increased Unique Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '50% more Unique Items found in Area', 0, 0, 0),
(940, 3679, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '100% increased Unique Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '50% more Unique Items found in Area', 0, 0, 0),
(941, 3680, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '150% increased Unique Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '50% more Unique Items found in Area', 0, 0, 0),
(942, 3681, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '250% increased Unique Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '50% more Unique Items found in Area', 0, 0, 0),
(943, 3682, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Unique Monsters have 1% increased Toughness, Damage, Rarity and Quantity of items dropped per 1% increased Pack Size of Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'DISABLED', 0, 0, 0),
(944, 3683, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Unique Monsters in Area have an additional Reward
Unique Monsters in Area have 30% increased Toughness', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'DISABLED', 0, 0, 0),
(945, 3684, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Unique Monsters in Area have 4 additional Monster Modifiers', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Unique Monsters in Area have 2 additional Monster Modifiers', 0, 0, 0),
(946, 3685, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains Einhar' || char(13) || '
Area contains an additional Red Beast', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(947, 3686, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains Einhar' || char(13) || '
Area contains 2 additional Red Beasts', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(948, 3687, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains Einhar' || char(13) || '
Area contains 3 additional Red Beasts', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(949, 3688, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains Einhar' || char(13) || '
Area contains 5 additional Red Beasts', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(950, 3689, 1, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains Einhar' || char(13) || '
Area contains 4 additional Red Beasts from The Wilds' || char(13) || '
Cannot be used alongside a Bestiary Scarab', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(951, 3690, 1, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains Einhar' || char(13) || '
Area contains 4 additional Red Beasts from The Sands' || char(13) || '
Cannot be used alongside a Bestiary Scarab', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(952, 3691, 1, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains Einhar' || char(13) || '
Area contains 4 additional Red Beasts from The Caverns' || char(13) || '
Cannot be used alongside a Bestiary Scarab', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(953, 3692, 1, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains Einhar' || char(13) || '
Area contains 4 additional Red Beasts from The Deep' || char(13) || '
Cannot be used alongside a Bestiary Scarab', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(954, 3693, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains Einhar', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains Einhar', 0, 0, 0),
(955, 3695, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Create a copy of Beasts captured in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Create a copy of Beasts captured in Area', 0, 0, 0),
(956, 3694, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 5 additional Red Beasts if it contains Einhar', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains 3 additional Red Beasts if it contains Einhar', 0, 0, 0),
(957, 3696, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains The Black Mórrigan if it contains Einhar', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains The Black Mórrigan if it contains Einhar', 0, 0, 0),
(958, 3697, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Adds Shaper Influence outcome to Map Area' || char(13) || '
30% more Rare Shaper Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Adds Shaper Influence outcome to Map Area' || char(13) || '
30% more Rare Shaper Items found in Area', 0, 0, 0),
(959, 3698, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Adds Shaper Influence outcome to Map Area' || char(13) || '
60% more Rare Shaper Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Adds Shaper Influence outcome to Map Area' || char(13) || '
30% more Rare Shaper Items found in Area', 0, 0, 0),
(960, 3699, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Adds Shaper Influence outcome to Map Area' || char(13) || '
90% more Rare Shaper Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Adds Shaper Influence outcome to Map Area' || char(13) || '
30% more Rare Shaper Items found in Area', 0, 0, 0),
(961, 3700, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Adds Shaper Influence outcome to Map Area' || char(13) || '
150% more Rare Shaper Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Adds Shaper Influence outcome to Map Area' || char(13) || '
30% more Rare Shaper Items found in Area', 0, 0, 0),
(962, 3701, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Adds Elder Influence outcome to Map Area' || char(13) || '
30% more Rare Elder Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Adds Elder Influence outcome to Map Area' || char(13) || '
30% more Rare Elder Items found in Area', 0, 0, 0),
(963, 3702, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Adds Elder Influence outcome to Map Area' || char(13) || '
60% more Rare Elder Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Adds Elder Influence outcome to Map Area' || char(13) || '
30% more Rare Elder Items found in Area', 0, 0, 0),
(964, 3703, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Adds Elder Influence outcome to Map Area' || char(13) || '
90% more Rare Elder Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Adds Elder Influence outcome to Map Area' || char(13) || '
30% more Rare Elder Items found in Area', 0, 0, 0),
(965, 3704, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Adds Elder Influence outcome to Map Area' || char(13) || '
150% more Rare Elder Items found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Adds Elder Influence outcome to Map Area' || char(13) || '
30% more Rare Elder Items found in Area', 0, 0, 0),
(966, 3705, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Adds Shaper Influence outcome to Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Adds Shaper Influence outcome to Area', 0, 0, 0),
(967, 3706, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Adds Elder Influence outcome to Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Adds Elder Influence outcome to Area', 0, 0, 0),
(968, 3708, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'A Map Boss is accompanied by a random Shaper Guardian, Elder Guardian, Conqueror or Synthesis Boss' || char(13) || '
Can only be used with Tier 14+ Maps', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'A Map Boss is accompanied by a random Shaper Guardian, Elder Guardian, Conqueror or Synthesis Boss' || char(13) || '
Can only be used with Tier 14+ Maps', 0, 0, 0),
(969, 3707, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Influenced Monster Packs in Area have 40% increased Pack Size', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Influenced Monster Packs in Area have 25% increased Pack Size', 0, 0, 0),
(970, 3709, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains Niko' || char(13) || '
Map owner gains 20% more Sulphite', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(971, 3710, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains Niko' || char(13) || '
Map owner gains 40% more Sulphite', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(972, 3711, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains Niko' || char(13) || '
Map owner gains 60% more Sulphite', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(973, 3712, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains Niko' || char(13) || '
Map owner gains 100% more Sulphite', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(974, 3713, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains Niko', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains Niko', 0, 0, 0),
(975, 3714, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Map owner gains 150% more Sulphite', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Map owner gains 150% more Sulphite', 0, 0, 0),
(976, 3715, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Sulphite found in Map Area releases Enraging Fumes
Monsters affected by Enraging Fumes have 50% increased Item Quantity
Sulphite in your maps is guarded by Monsters from the Azurite Mine', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Sulphite found in Map Area releases Enraging Fumes
Monsters affected by Enraging Fumes have 50% increased Item Quantity
Sulphite in your maps is guarded by Monsters from the Azurite Mine', 0, 0, 0),
(977, 3716, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '50% more Divination Cards found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(978, 3717, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '100% more Divination Cards found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(979, 3718, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '150% more Divination Cards found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(980, 3719, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '250% more Divination Cards found in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(981, 3720, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 8 to 12 additional packs of Doedre''s Devoted
Doedre''s Devoted have 1% additional chance to drop a Stacked Deck', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'DISABLED', 0, 0, 0),
(982, 3722, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Divination Cards which drop in Area are stolen by a Final Map Boss
That Final Map Boss becomes more Difficult and deals
increased Damage the more Divination Cards they have stolen
That Final Map Boss duplicates all stolen Divination Cards when defeated', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'DISABLED', 0, 0, 0),
(983, 3721, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 6 to 10 additional Packs of Divination Touched Magic Monsters that
have 1000% increased chance to drop Divination Cards', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'DISABLED', 0, 0, 0),
(984, 3723, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area is haunted by 3 additional Tormented Spirits', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area is haunted by 3 additional Tormented Spirits', 0, 0, 0),
(985, 3724, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area is haunted by 5 additional Tormented Spirits', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area is haunted by 3 additional Tormented Spirits', 0, 0, 0),
(986, 3725, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area is haunted by 7 additional Tormented Spirits', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area is haunted by 3 additional Tormented Spirits', 0, 0, 0),
(987, 3726, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area is haunted by 12 additional Tormented Spirits', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area is haunted by 3 additional Tormented Spirits', 0, 0, 0),
(988, 3727, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area is haunted by 4 additional Tormented Spirits
Tormented Spirits in Area have a 10% chance to
be set free when Possessed Monsters are slain
 and dig up a Possessed Monster', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area is haunted by 3 additional Tormented Spirits
Tormented Spirits in Area have a 10% chance to
be set free when Possessed Monsters are slain
 and dig up a Possessed Monster', 0, 0, 0),
(989, 3728, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Tormented Spirits in Area are replaced with unusual variants', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Tormented Spirits in Area are replaced with unusual variants', 0, 0, 0),
(990, 3729, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Tormented Spirits in Area have a 25% chance to
be set free when Possessed Monsters are slain', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Tormented Spirits in Area have a 25% chance to
be set free when Possessed Monsters are slain', 0, 0, 0),
(991, 3730, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Rare Monsters in Area have a quarter chance to be Possessed by up to 3 Tormented Spirits', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Rare Monsters in Area have a quarter chance to be Possessed by up to 1 Tormented Spirit', 0, 0, 0),
(992, 3731, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 3 additional Strongboxes', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains 3 additional Strongboxes', 0, 0, 0),
(993, 3732, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 4 additional Strongboxes', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains 3 additional Strongboxes', 0, 0, 0),
(994, 3733, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 5 additional Strongboxes', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains 3 additional Strongboxes', 0, 0, 0),
(995, 3734, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 9 additional Strongboxes', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains 3 additional Strongboxes', 0, 0, 0),
(996, 3735, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 4 additional Strongboxes', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains 3 additional Strongboxes', 0, 0, 0),
(997, 3736, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '15% Chance for Strongboxes in Area to be openable again', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '15% Chance for Strongboxes in Area to be openable again', 0, 0, 0),
(998, 3737, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '75% Increased effect of Explicit Modifiers on Strongboxes in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '75% Increased effect of Explicit Modifiers on Strongboxes in Area', 0, 0, 0),
(999, 3738, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains many additional Strongboxes
Area''s inhabitants are lying in ambush', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains many additional Strongboxes
Area''s inhabitants are lying in ambush', 0, 0, 0);
INSERT INTO "CurrencyItems" ("_row", "BaseItemTypesKey", "StackSize", "CurrencyUseType", "Action", "Directions", "FullStack_BaseItemTypesKey", "Description", "Usage_AchievementItemsKeys", "Scroll", "Possession_AchievementItemsKey", "__col10", "__col11", "CurrencyTab_StackSize", "XBoxDirections", "__col14", "ModifyMapsAchievements", "ModifyContractsAchievements", "CombineAchievements", "ChangedForHardmode", "DescriptionHardmode", "IsGold", "__col21", "__col22") VALUES
(1000, 3739, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Strongboxes in Area are more likely to be rarer varieties', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Strongboxes in Area are more likely to be rarer varieties', 0, 0, 0),
(1001, 3740, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 2 additional Harbingers', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains 2 additional Harbingers', 0, 0, 0),
(1002, 3741, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 3 additional Harbingers', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains 2 additional Harbingers', 0, 0, 0),
(1003, 3742, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 4 additional Harbingers', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains 2 additional Harbingers', 0, 0, 0),
(1004, 3743, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 7 additional Harbingers', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains 2 additional Harbingers', 0, 0, 0),
(1005, 3744, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 3 additional Harbingers', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains 1 additional Harbinger', 0, 0, 0),
(1006, 3745, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Harbingers in Area spawn Empowered Obelisks
Empowered Obelisks drop Currency Shards when defeated', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Harbingers in Area drop a single type of currency shard', 0, 0, 0),
(1007, 3747, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Currency shards dropped by Harbingers in Area are duplicated', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Currency shards dropped by Harbingers in Area have a 25% chance to be duplicated', 0, 0, 0),
(1008, 3746, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Harbingers in Area have a 50% chance to be replaced by a powerful Harbinger Boss', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Harbingers in Area have a 50% chance to be replaced by a powerful Harbinger Boss', 0, 0, 0),
(1009, 3748, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an additional Expedition Encounter', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Expedition Encounter', 0, 0, 0),
(1010, 3749, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an additional Expedition Encounter' || char(13) || '
Area contains 25% increased number of Runic Monster Markers' || char(13) || '
20% increased number of Explosives', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Expedition Encounter', 0, 0, 0),
(1011, 3750, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an additional Expedition Encounter' || char(13) || '
Area contains 50% increased number of Runic Monster Markers' || char(13) || '
20% increased number of Explosives', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Expedition Encounter', 0, 0, 0),
(1012, 3751, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an additional Expedition Encounter' || char(13) || '
Area contains 50% increased number of Runic Monster Markers' || char(13) || '
20% increased number of Explosives' || char(13) || '
Remnants in area have an additional Suffix', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Expedition Encounter', 0, 0, 0),
(1013, 3752, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an Expedition Encounter', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, 'Area contains an Expedition Encounter', 0, 0, 0),
(1014, 3753, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Expedition Encounters in Area have 100% increased
number of Runic Monster Markers', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, 'Expedition Encounters in Area have 100% increased
number of Runic Monster Markers', 0, 0, 0),
(1015, 3754, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Expedition Encounters in Area have 50% increased number of Explosives
80% increased Explosive Radius', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, 'Expedition Encounters in Area have 50% increased number of Explosives
80% increased Explosive Radius', 0, 0, 0),
(1016, 3756, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Remnants in Expedition Encounters in Area have 2 additional Suffixes and Prefixes', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, 'Remnants in Expedition Encounters in Area have 1 additional Suffix and Prefix', 0, 0, 0),
(1017, 3755, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Logbooks found in Area always have 4 Implicits
Expedition Monsters gain increased Difficulty and Reward for each Remnant detonated', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, 'Logbooks found in Area always have 4 Implicits
Expedition Monsters gain increased Difficulty and Reward for each Remnant detonated', 0, 0, 0),
(1018, 3757, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an additional Legion Encounter', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Legion Encounter', 0, 0, 0),
(1019, 3758, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an additional Legion Encounter' || char(13) || '
Each Legion contains a War Hoard', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Legion Encounter', 0, 0, 0),
(1020, 3759, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an additional Legion Encounter' || char(13) || '
Each Legion contains a War Hoard' || char(13) || '
Each Legion is accompanied by a General', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Legion Encounter', 0, 0, 0),
(1021, 3760, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 2 additional Legion Encounters' || char(13) || '
Each Legion contains a War Hoard' || char(13) || '
Each Legion is accompanied by a General', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Legion Encounter', 0, 0, 0),
(1022, 3761, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an additional Legion Encounter', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Legion Encounter', 0, 0, 0),
(1023, 3762, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Legion Factions in Area have 5 additional Sergeants', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Legion Factions in Area have 2 additional Sergeants', 0, 0, 0),
(1024, 3763, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Legion Chests in Area have a 20% chance to spread their Rewards
 to Legion Monsters they release from stasis when they are broken out
Legion Monsters that have gained Rewards have increased Difficulty', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Legion Chests in Area have a 20% chance to spread their Rewards
 to Legion Monsters they release from stasis when they are broken out
Legion Monsters that have gained rewards have increased Difficulty', 0, 0, 0),
(1025, 3764, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Legion Encounters in Area always contain the Maraketh Faction', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Legion Encounters in Area always contain the Maraketh Faction', 0, 0, 0),
(1026, 3765, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Legion Monsters in Area can be broken out of Stasis multiple times
Legion Monsters in Area gain increased Difficulty and Reward for each time they are broken out', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Legion Monsters in Area can be broken out of Stasis multiple times
Legion Monsters in Area gain increased Difficulty and Reward for each time they are broken out', 0, 0, 0),
(1027, 3766, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an Ultimatum Encounter', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an Ultimatum Encounter', 0, 0, 0),
(1028, 3767, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an Ultimatum Encounter' || char(13) || '
Ultimatum Monsters grant 100% increased Experience', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an Ultimatum Encounter', 0, 0, 0),
(1029, 3768, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an Ultimatum Encounter' || char(13) || '
Ultimatum Monsters grant 100% increased Experience' || char(13) || '
Ultimatum Encounters grant rewards as though you completed an additional Round', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an Ultimatum Encounter', 0, 0, 0),
(1030, 3769, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an Ultimatum Encounter' || char(13) || '
Ultimatum Monsters grant 200% increased Experience' || char(13) || '
Ultimatum Encounters grant rewards as though you completed 2 additional Rounds', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an Ultimatum Encounter', 0, 0, 0),
(1031, 3770, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains a Blight Encounter', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains a Blight Encounter', 0, 0, 0),
(1032, 3771, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains a Blight Encounter' || char(13) || '
Blight Encounters contain an additional Blight Boss', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains a Blight Encounter', 0, 0, 0),
(1033, 3772, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains a Blight Encounter' || char(13) || '
Blight Encounters contain an additional Blight Boss' || char(13) || '
50% chance for Blight Chests to contain an additional Reward', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains a Blight Encounter', 0, 0, 0),
(1034, 3773, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains a Blight Encounter' || char(13) || '
Blight Encounters contain an additional Blight Boss' || char(13) || '
50% chance for Blight Chests to contain an additional Reward' || char(13) || '
Lanes of Blight Encounters in Areas have an additional Reward Chest', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains a Blight Encounter', 0, 0, 0),
(1035, 3774, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an additional Abyss', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Abyss', 0, 0, 0),
(1036, 3775, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an additional Abyss' || char(13) || '
Abysses in Area spawn 50% increased Monsters', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Abyss', 0, 0, 0),
(1037, 3776, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an additional Abyss' || char(13) || '
Abysses in Area spawn 50% increased Monsters' || char(13) || '
Abysses in Area that do not lead to an Abyssal Depths lead to a Stygian Spire', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Abyss', 0, 0, 0),
(1038, 3777, 10, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 2 additional Abysses' || char(13) || '
Abysses in Area spawn 50% increased Monsters' || char(13) || '
Abysses in Area that do not lead to an Abyssal Depths lead to a Stygian Spire', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Abyss', 0, 0, 0),
(1039, 3778, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an additional [ContainsAbyss|Abyss]', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1040, 3779, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '[ContainsAbyss|Abyss] Chasms in Area spawn 100% increased Monsters per fed [AbyssFedSoul|Soul]', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1041, 3780, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Non-final [ContainsAbyss|Abyss] Pits in Area create an [AbyssalCrystal|Abyssal Crystal] on closing', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1042, 3781, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an [AbyssalDepths|Abyssal Depths]', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1043, 3782, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'An [ContainsAbyss|Abyss] Pit in Area will spawn an [AbyssalConsort|Abyssal Consort]', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1044, 3783, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 5 additional Rogue Exiles', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains 2 additional Rogue Exiles', 0, 0, 0),
(1045, 3784, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Wild Rogue Exiles in Area have a 30% chance to be replaced with a Rogue Giant', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Wild Rogue Exiles in Area have a 30% chance to be replaced with a Rogue Giant', 0, 0, 0),
(1046, 3785, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Wild Rogue Exiles in Area have a 50% chance to appear in pairs', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Wild Rogue Exiles in Area have a 50% chance to appear in pairs', 0, 0, 0),
(1047, 3786, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an additional Exceptional Rogue Exile', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Exceptional Rogue Exile', 0, 0, 0),
(1048, 3787, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 3 additional Imprisoned Monsters', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Imprisoned Monster', 0, 0, 0),
(1049, 3788, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Essences found in Area are a tier higher', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Essences found in Area are a tier higher', 0, 0, 0),
(1050, 3790, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Rare monsters that are natural inhabitants of the Area are imprisoned by Essences', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Rare monsters that are natural inhabitants of the Area are imprisoned by Essences', 0, 0, 0),
(1051, 3789, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Corrupting an Essence in Area can only result in
upgrading or transforming Essences', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Corrupting an Essence in Area can only result in
upgrading or transforming Essences', 0, 0, 0),
(1052, 3791, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Imprisoned Monsters released in Areas grant a random Essence Modifier to another Imprisoned Monster in the Area
Imprisoned Monsters in Areas have increased Difficulty and Reward for
each Essence Modifier', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Imprisoned Monsters released in Areas grant a random Essence Modifier to another Imprisoned Monster in the Area
Imprisoned Monsters in Areas have increased Difficulty and Reward for
each Essence Modifier', 0, 0, 0),
(1053, 3793, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 2 additional Apparition Shrines', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Apparition Shrine', 0, 0, 0),
(1054, 3794, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an additional Evolving Shrine', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Evolving Shrine', 0, 0, 0),
(1055, 3792, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 3 additional Shrines', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains 2 additional Shrines', 0, 0, 0),
(1056, 3795, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Shrines in Area are guarded by an Atlas Boss
Modifiers to the Final Map Boss also apply to these Atlas Bosses', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Shrines in Area are guarded by an Atlas Boss
Modifiers to the Final Map Boss also apply to this Atlas Boss', 0, 0, 0),
(1057, 3796, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Rerolling Favours at Ritual Altars in Area has no cost the first 2 times
Ritual Altars in Area allow rerolling Favours 2 additional times', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Rerolling Favours at Ritual Altars in Area has no cost the first 2 times
Ritual Altars in Area allow rerolling Favours 2 additional times', 0, 0, 0),
(1058, 3797, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Ritual Altars in Area spawn a Wildwood Wisp
Wildwood Wisps grant 100% increased Tribute Gained to nearby Players', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Ritual Altars in Area spawn a Wildwood Wisp
Wildwood Wisps grant 100% increased Tribute Gained to nearby Players', 0, 0, 0),
(1059, 3798, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Rituals in Area offer 100% increased Favours', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Rituals in Area offer 100% increased Favours', 0, 0, 0),
(1060, 3799, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Rituals in Area contain an additional Rare Itemised Corpse Monster', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Rituals in Area contain an additional Rare Itemised Corpse Monster', 0, 0, 0),
(1061, 3800, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains the Sacred Grove', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains the Sacred Grove', 0, 0, 0),
(1062, 3802, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'If Area contains the Sacred Grove, it will contain up to 1 additional Tier 4 seed of each type, if possible', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'If Area contains the Sacred Grove, it will contain up to 1 additional Tier 4 seed of each type, if possible', 0, 0, 0),
(1063, 3801, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Lifeforce dropped by Harvest Monsters in Area is duplicated
Harvest Monsters in Area have 100% more Life', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Lifeforce dropped by Harvest Monsters in Area is duplicated
Harvest Monsters in Area have 100% more Life', 0, 0, 0),
(1064, 3803, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains Alva', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains Alva', 0, 0, 0),
(1065, 3804, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 12 to 16 additional packs of Incursion Monsters', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains 3 to 5 additional packs of Incursion Monsters', 0, 0, 0),
(1066, 3806, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Final Architect slain in Area will drop an Itemised Temple
 Itemised Temples dropped in Area are generated based on current Temple layout, but with randomised room tiers', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Final Architect slain in Area will drop an Itemised Temple
 Itemised Temples dropped in Area are generated based on current Temple layout, but with randomised room tiers', 0, 0, 0),
(1067, 3805, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Incursions in Area have a 35% chance for all Monsters to be at least Magic
Incursions in Area have 15% increased Pack Size', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Incursions in Area have a 35% chance for all Monsters to be at least Magic', 0, 0, 0),
(1068, 3807, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains Jun', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains Jun', 0, 0, 0),
(1069, 3808, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '75% Increased number of Monster Packs Substituted by Allflame Embers in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '75% Increased number of Monster Packs Substituted by Allflame Embers in Area', 0, 0, 0),
(1070, 3810, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Interrogated Immortal Syndicate targets in Area have a 50% chance to
not lose Rank upon completing Interrogation', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Interrogated Immortal Syndicate targets in Area have a 50% chance to
not lose Rank upon completing Interrogation', 0, 0, 0),
(1071, 3809, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Immortal Syndicate Members in Area have 50% increased chance to be accompanied by reinforcements', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Immortal Syndicate Members in Area have 50% increased chance to be accompanied by reinforcements', 0, 0, 0),
(1072, 3811, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Slaying enemies close together in Area can attract monsters from Beyond this realm', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Slaying enemies close together in Area can attract monsters from Beyond this realm', 0, 0, 0),
(1073, 3812, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Items dropped by Beyond Demons in Area are Corrupted', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Items dropped by Beyond Demons in Area are Corrupted', 0, 0, 0),
(1074, 3813, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Beyond Portals in Area have 50% increased Merging Radius
Characters that Kill Beyond Rare Monsters in Area have a 30% chance to gain their Modifiers for 20 seconds', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Beyond Portals in Area have 50% increased Merging Radius
Characters that Kill Beyond Rare Monsters in Area have a 30% chance to gain their Modifiers for 20 seconds', 0, 0, 0),
(1075, 3815, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Rare and Unique Monsters slain in Area create 8 to 12 additional Beyond Portals', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Rare and Unique Monsters slain in Area create 8 to 12 additional Beyond Portals', 0, 0, 0),
(1076, 3814, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Beyond Bosses Spawned in Area are accompanied by Beyond Bosses of other Factions
Beyond Bosses in Area drop 20% increased Tainted Currency
Beyond Portals in Area have 30% increased chance to spawn a Unique Boss', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Beyond Bosses Spawned in Area are accompanied by Beyond Bosses of other Factions
Beyond Bosses in Area drop 20% increased Tainted Currency
Beyond Portals in Area have 30% increased chance to spawn a Unique Boss', 0, 0, 0),
(1077, 3816, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains an Ultimatum Encounter', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an Ultimatum Encounter', 0, 0, 0),
(1078, 3817, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Ultimatum Monsters grant 150% increased Experience
Ultimatum Encounters grant rewards as though you completed 2 additional Rounds', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Ultimatum Monsters grant 150% increased Experience
Ultimatum Encounters grant rewards as though you completed 2 additional Rounds', 0, 0, 0),
(1079, 3818, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Ultimatum Encounters in Area will always lead to a Unique Boss if possible', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Ultimatum Encounters in Area will always lead to a Unique Boss if possible', 0, 0, 0),
(1080, 3819, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Ultimatum Encounters in Area will only offer Catalysts
as Rewards to the Map Owner', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Ultimatum Encounters in Area will only offer Catalysts
as Rewards to the Map Owner', 0, 0, 0),
(1081, 3820, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Ultimatum Encounter rewards in Area offering Catalysts
will offer Inscribed Ultimatums to the Map Owner instead', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'DISABLED', 0, 0, 0),
(1082, 3821, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains a Mirror of Delirium', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains a Mirror of Delirium', 0, 0, 0),
(1083, 3822, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Delirium Reward Meters fill 100% faster in Area
Delirium in Area increases 50% faster with distance from the mirror', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Delirium Reward Meters fill 100% faster in Area
Delirium in Area increases 50% faster with distance from the mirror', 0, 0, 0),
(1084, 3823, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Delirium Encounters in Area generate 2 additional Reward types', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'DISABLED', 0, 0, 0),
(1085, 3824, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Delirium Encounters in Area contain all Unique Delirium Bosses
Delirium Reward Types in Area gain +1 to count on defeating a Unique Delirium Boss
Can only be used with Tier 11+ Maps', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Delirium Encounters in Area contain all Unique Delirium Bosses
Delirium Reward Types in Area gain +1 to count on defeating a Unique Delirium Boss
Can only be used with Tier 11+ Maps', 0, 0, 0),
(1086, 3825, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Maps found in Area have layers of Delirium', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'DISABLED', 0, 0, 0),
(1087, 3826, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains a Blight Encounter', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains a Blight Encounter', 0, 0, 0),
(1088, 3827, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Blight Chests in Area have a 20% chance to be openable again', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Blight Chests in Area have a 20% chance to be openable again', 0, 0, 0),
(1089, 3828, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Blight Encounters in Area have one Blighted Chest
Blight Encounters in Area spawn additional waves of Enemies
Blighted Chests in Area grow larger and more
rewarding the more Blighted Enemies are slain', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Blight Encounters in Area have one Blighted Chest
Blight Encounters in Area spawn additional waves of Enemies
Blighted Chests in Area grow larger and more
rewarding the more Blighted Enemies are slain', 0, 0, 0),
(1090, 3829, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Blight Encounters in Area have up to 3 additional Unique Bosses
Unique enemies in Blight Encounters have 100% increased life
Tier 14+ Blighted Maps found in Area drop as Blight-Ravaged Maps instead', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'DISABLED', 0, 0, 0),
(1091, 3830, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Each Empowering Tower in Areas grants Blighted Monsters in
range increased Difficulty and Reward', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Each Empowering Tower in Areas grants Blighted Monsters in
range increased Difficulty and Reward', 0, 0, 0),
(1092, 3831, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 2 additional Ore Deposits', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains an additional Ore Deposit', 0, 0, 0),
(1093, 3832, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Monsters guarding Ore Deposits in Area are at least Magic', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Monsters guarding Ore Deposits in Area are at least Magic', 0, 0, 0),
(1094, 3833, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Ore Deposits in Area grant Smelted Bars instead of Marking Ore', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Ore Deposits in Area grant Smelted Bars instead of Marking Ore', 0, 0, 0),
(1095, 3834, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Monsters guarding Ore Deposits have increased Difficulty and
Reward and Ore Deposits contain 15% increased Ore
for each Ore Deposit completed in Area ', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Monsters guarding Ore Deposits have increased Difficulty and
Reward and Ore Deposits contain 15% increased Ore
for each Ore Deposit completed in Area ', 0, 0, 0),
(1096, 3835, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area is inhabited by a Mercenary', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1097, 3836, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Mercenaries found in Area are Infamous' || char(13) || '
Mercenaries found in Area are accompanied by two Wild Mercenaries', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1098, 3837, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'All Equipment Items on Mercenaries found in Area are Unique', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1099, 3838, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Wild Rogue Exiles in Area have 50% chance to be accompanied by a Wild Mercenary' || char(13) || '
Wild Mercenaries have increased Difficulty for each Wild Mercenary in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1100, 3839, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '40% increased Magic Pack Size', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '25% increased Magic Pack Size', 0, 0, 0),
(1101, 3840, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains 4 additional Packs with Mirrored Rare Monsters', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains 2 additional Packs with Mirrored Rare Monsters', 0, 0, 0),
(1102, 3841, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Up to 3 additional Rare Monsters in Area will be Pantheon-Touched
Apparitions spawned by Pantheon-Touched Monsters deal 100% increased damage', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Up to 1 additional Rare Monster in Area will be Pantheon-Touched
Apparitions spawned by Pantheon-Touched Monsters deal 100% increased damage', 0, 0, 0),
(1103, 3842, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains Hunted Traitors', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains Hunted Traitors', 0, 0, 0),
(1104, 3843, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Portals to Area have a 50% chance to not be consumed on use', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Portals to Area have a 50% chance to not be consumed on use', 0, 0, 0),
(1105, 3844, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Grants an additional Kirac Mission on Map Completion', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Grants an additional Kirac Mission on Map Completion', 0, 0, 0),
(1106, 3845, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '10 Monster Packs in Area are upgraded to Magic', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '10 Monster Packs in Area are upgraded to Magic', 0, 0, 0),
(1107, 3846, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Monsters in Area have a chance to be Empowered by 2000 Wildwood Wisps', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Monsters in Area have a chance to be Empowered by 2000 Wildwood Wisps', 0, 0, 0),
(1108, 3847, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '100% increased effect of Area Prefix Modifiers
Area Suffix Modifiers have no effect', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '100% increased effect of Area Prefix Modifiers
Area Suffix Modifiers have no effect', 0, 0, 0),
(1109, 3848, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, '100% increased effect of Area Suffix Modifiers
Area Prefix Modifiers have no effect', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, '100% increased effect of Area Suffix Modifiers
Area Prefix Modifiers have no effect', 0, 0, 0),
(1110, 3849, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area contains a Resplendent Tempest', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area contains a Resplendent Tempest', 0, 0, 0),
(1111, 3850, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Area has 150% increased Magic Monsters
Magic Monsters in area have an additional Modifier', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Area has 150% increased Magic Monsters', 0, 0, 0),
(1112, 3851, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Rare Monsters in Area have 2 additional Modifiers', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Rare Monsters in Area have 2 additional Modifiers', 0, 0, 0),
(1113, 3852, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Other Scarabs are not consumed on use', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Other Scarabs are not consumed on use', 0, 0, 0),
(1114, 3853, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Tier 16+ Areas contain the Bosses from a random Maven Invitation', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'DISABLED', 0, 0, 0),
(1115, 3854, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'All Rare and Unique Monsters in Area have dropped items
transformed by a Reward Modifier', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'DISABLED', 0, 0, 0),
(1116, 3855, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Players in Area gain increased Item Rarity for each Monster
slain, up to 400%, decaying over time', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Players in Area gain increased Item Rarity for each Monster
slain, up to 400%, decaying over time', 0, 0, 0),
(1117, 3856, 20, 3, '', 'Can be used in a personal Map Device to add modifiers to a Map.', NULL, 'Monster packs in Area have a 15% chance to be replaced by a random Atlas Boss
Modifiers to the Final Map Boss also apply to these Atlas Bosses', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to add modifiers to a Map.', 0, '[]', '[]', '[]', 1, 'Monster packs in Area have a 15% chance to be replaced by a random Atlas Boss
Modifiers to the Final Map Boss also apply to these Atlas Bosses', 0, 0, 0),
(1118, 3857, 10, 3, '', 'Can be combined with other Oils at Cassia to enchant corrupted Rings, Amulets or Blighted Maps.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1119, 3858, 10, 3, '', 'Can be combined with other Oils at Cassia to enchant mirrored Rings, Amulets or Blighted Maps.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1120, 3859, 10, 3, '', 'Can be combined with other Oils at Cassia to enchant Amulets.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1121, 3860, 10, 3, '', 'Can be combined with other Oils at Cassia to Enchant Rings or Amulets, or to modify Blighted Maps.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1122, 3861, 10, 3, '', 'Can be combined with other Oils at Cassia to Enchant Rings or Amulets, or to modify Blighted Maps.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1123, 3862, 10, 3, '', 'Can be combined with other Oils at Cassia to Enchant Rings or Amulets, or to modify Blighted Maps.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1124, 3863, 10, 3, '', 'Can be combined with other Oils at Cassia to Enchant Rings or Amulets, or to modify Blighted Maps.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1125, 3864, 10, 3, '', 'Can be combined with other Oils at Cassia to Enchant Rings or Amulets, or to modify Blighted Maps.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1126, 3865, 10, 3, '', 'Can be combined with other Oils at Cassia to Enchant Rings or Amulets, or to modify Blighted Maps.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1127, 3866, 10, 3, '', 'Can be combined with other Oils at Cassia to Enchant Rings or Amulets, or to modify Blighted Maps.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1128, 3867, 10, 3, '', 'Can be combined with other Oils at Cassia to Enchant Rings or Amulets, or to modify Blighted Maps.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1129, 3868, 10, 3, '', 'Can be combined with other Oils at Cassia to Enchant Rings or Amulets, or to modify Blighted Maps.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1130, 3869, 10, 3, '', 'Can be combined with other Oils at Cassia to Enchant Rings or Amulets, or to modify Blighted Maps.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1131, 3870, 10, 3, '', 'Can be combined with other Oils at Cassia to Enchant Rings or Amulets, or to modify Blighted Maps.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1132, 3871, 10, 3, '', 'Can be combined with other Oils at Cassia to Enchant Rings or Amulets, or to modify Blighted Maps.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1133, 3872, 10, 3, '', 'Can be combined with other Oils at Cassia to Enchant Rings or Amulets, or to modify Blighted Maps.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1134, 310, 20, 2, 'add_jewellery_quality', 'Right click this item then left click a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', NULL, 'Adds quality that enhances Elemental Damage modifiers on a ring, amulet or belt' || char(13) || '
Replaces other quality types', '[11664]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', 0, '[]', '[]', '[]', 0, '', 0, 1, 1),
(1135, 311, 20, 2, 'add_jewellery_quality', 'Right click this item then left click a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', NULL, 'Adds quality that enhances Caster modifiers on a ring, amulet or belt' || char(13) || '
Replaces other quality types', '[11664]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', 0, '[]', '[]', '[]', 0, '', 0, 1, 1),
(1136, 312, 20, 2, 'add_jewellery_quality', 'Right click this item then left click a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', NULL, 'Adds quality that enhances Attack modifiers on a ring, amulet or belt' || char(13) || '
Replaces other quality types', '[11664]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', 0, '[]', '[]', '[]', 0, '', 0, 1, 1),
(1137, 313, 20, 2, 'add_jewellery_quality', 'Right click this item then left click a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', NULL, 'Adds quality that enhances Defence modifiers on a ring, amulet or belt' || char(13) || '
Replaces other quality types', '[11664]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', 0, '[]', '[]', '[]', 0, '', 0, 1, 1),
(1138, 314, 20, 2, 'add_jewellery_quality', 'Right click this item then left click a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', NULL, 'Adds quality that enhances Life and Mana modifiers on a ring, amulet or belt' || char(13) || '
Replaces other quality types', '[11664]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', 0, '[]', '[]', '[]', 0, '', 0, 1, 1),
(1139, 315, 20, 2, 'add_jewellery_quality', 'Right click this item then left click a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', NULL, 'Adds quality that enhances Resistance modifiers on a ring, amulet or belt' || char(13) || '
Replaces other quality types', '[11664]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', 0, '[]', '[]', '[]', 0, '', 0, 1, 1),
(1140, 316, 20, 2, 'add_jewellery_quality', 'Right click this item then left click a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', NULL, 'Adds quality that enhances Attribute modifiers on a ring, amulet or belt' || char(13) || '
Replaces other quality types', '[11664]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', 0, '[]', '[]', '[]', 0, '', 0, 1, 1),
(1141, 317, 20, 2, 'add_jewellery_quality', 'Right click this item then left click a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', NULL, 'Adds quality that enhances Physical and Chaos Damage modifiers on a ring, amulet or belt' || char(13) || '
Replaces other quality types', '[11664]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', 0, '[]', '[]', '[]', 0, '', 0, 1, 1),
(1142, 318, 20, 2, 'add_jewellery_quality', 'Right click this item then left click a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', NULL, 'Adds quality that enhances Speed modifiers on a ring, amulet or belt' || char(13) || '
Replaces other quality types', '[11664]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', 0, '[]', '[]', '[]', 0, '', 0, 1, 1),
(1143, 319, 20, 2, 'add_jewellery_quality', 'Right click this item then left click a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', NULL, 'Adds quality that enhances Critical modifiers on a ring, amulet or belt' || char(13) || '
Replaces other quality types', '[11664]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', 0, '[]', '[]', '[]', 0, '', 0, 1, 1),
(1144, 320, 20, 2, 'add_random_jewellery_quality', 'Right click this item then left click a Corrupted ring, amulet or belt to apply it. The maximum quality is 20%.', NULL, 'Applies a random quality type and amount of quality to a Corrupted ring, amulet or belt' || char(13) || '
Replaces other quality types', '[11653,11664]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Corrupted ring, amulet or belt to apply it. The maximum quality is 20%.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1145, 321, 20, 2, 'add_jewellery_quality', 'Right click this item then left click a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', NULL, 'Adds quality that enhances Prefix modifiers on a ring, amulet or belt' || char(13) || '
Replaces other quality types', '[11290]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', 0, '[]', '[]', '[]', 0, '', 0, 1, 1),
(1146, 322, 20, 2, 'add_jewellery_quality', 'Right click this item then left click a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', NULL, 'Adds quality that enhances Suffix modifiers on a ring, amulet or belt' || char(13) || '
Replaces other quality types', '[11290]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a ring, amulet or belt to apply it. Has greater effect on lower item level, non-unique jewellery. The maximum quality is 20%.', 0, '[]', '[]', '[]', 0, '', 0, 1, 1),
(1147, 323, 10, 2, 'upgrade_unique', 'Right click this item then left click an applicable Harbinger unique item to upgrade it.', NULL, 'Upgrades The Flow Untethered to a more powerful version', '[7118]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an applicable Harbinger unique item to upgrade it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1148, 324, 10, 2, 'upgrade_unique', 'Right click this item then left click an applicable Harbinger unique item to upgrade it.', NULL, 'Upgrades The Fracturing Spinner to a more powerful version', '[7118]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an applicable Harbinger unique item to upgrade it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1149, 325, 10, 2, 'upgrade_unique', 'Right click this item then left click an applicable Harbinger unique item to upgrade it.', NULL, 'Upgrades The Tempest''s Binding to a more powerful version', '[7118]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an applicable Harbinger unique item to upgrade it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1150, 326, 10, 2, 'upgrade_unique', 'Right click this item then left click an applicable Harbinger unique item to upgrade it.', NULL, 'Upgrades The Rippling Thoughts to a more powerful version', '[7118]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an applicable Harbinger unique item to upgrade it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1151, 327, 10, 2, 'upgrade_unique', 'Right click this item then left click an applicable Harbinger unique item to upgrade it.', NULL, 'Upgrades The Enmity Divine to a more powerful version', '[7118]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an applicable Harbinger unique item to upgrade it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1152, 328, 10, 2, 'upgrade_unique', 'Right click this item then left click an applicable Harbinger unique item to upgrade it.', NULL, 'Upgrades The Unshattered Will to a more powerful version', '[7118]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an applicable Harbinger unique item to upgrade it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1153, 3915, 20, 2, 'add_influence_mod_to_rare', 'Right click this item then left click a high-level rare item with no influence to apply it. Rare items can have up to six random modifiers.', NULL, 'Adds Crusader influence and a new Crusader modifier to a rare item', '[11648]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a high-level rare item with no influence to apply it. Rare items can have up to six random modifiers.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1154, 3916, 20, 2, 'add_influence_mod_to_rare', 'Right click this item then left click a high-level rare item with no influence to apply it. Rare items can have up to six random modifiers.', NULL, 'Adds Redeemer influence and a new Redeemer modifier to a rare item', '[11648]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a high-level rare item with no influence to apply it. Rare items can have up to six random modifiers.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1155, 3917, 20, 2, 'add_influence_mod_to_rare', 'Right click this item then left click a high-level rare item with no influence to apply it. Rare items can have up to six random modifiers.', NULL, 'Adds Hunter influence and a new Hunter modifier to a rare item', '[11648]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a high-level rare item with no influence to apply it. Rare items can have up to six random modifiers.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1156, 3918, 20, 2, 'add_influence_mod_to_rare', 'Right click this item then left click a high-level rare item with no influence to apply it. Rare items can have up to six random modifiers.', NULL, 'Adds Warlord influence and a new Warlord modifier to a rare item', '[11648]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a high-level rare item with no influence to apply it. Rare items can have up to six random modifiers.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1157, 3919, 10, 4, 'transfer_item_influence', 'Right click this item, then left click the item you wish to take the influence from, then left click an item of the same item class you wish to apply it to.', NULL, 'Destroys an item, applying its influence to another of the same item class' || char(13) || '
The second item is reforged as a rare item with both influence types and new modifiers', '[11654]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on the item you wish to take the influence from, then <<xbox_button_a>> on an item of the same item class you wish to apply it to.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1158, 3921, 50000, 1, 'heist_coin', 'Right click on this item while in a Town or Hideout to use it.', NULL, 'Creates a portal to the Rogue Harbour from a Town or Hideout
Used as Currency for services in the Rogue Harbour', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_a>> to use.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1159, 331, 100, 3, '', 'Combine 100 Splinters to create a Ritual Vessel.', 332, '', '[]', 0, NULL, '[]', '[]', 0, 'Combine 100 Splinters to create a Ritual Vessel.', 0, '[]', '[]', '[7534]', 0, '', 0, 0, 0),
(1160, 332, 10, 2, 'ritual_stone', 'Right-click this item then left-click a Ritual Altar to store the monsters from the completed Ritual in this item. Cannot be used on a Ritual in a map opened with a Blood-Filled Vessel.', NULL, 'Stores the monsters slain for the first time from a completed Ritual Altar for future use', '[]', 0, NULL, '[]', '[]', 10000, 'Use this item on a Ritual Altar to store the monsters from the completed Ritual in the item. Cannot be used on a Ritual in a map opened with a Blood-Filled Vessel.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1161, 4115, 10, 2, 'upgrade_influence_mod', 'Right click this item then left click an item with at least two Influenced Modifiers to apply it.', NULL, 'Removes one Influenced Modifier from an item with at least two Influenced' || char(13) || '
Modifiers and upgrades another Influenced Modifier' || char(13) || '
Upgrading a modifier of the highest tier transforms the' || char(13) || '
modifier into an Elevated Modifier' || char(13) || '
Attempting to upgrade an Elevated Modifier rerolls its values' || char(13) || '
Can be used on Body Armours, Boots, Gloves and Helmets', '[11655]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item with at least two Influenced Modifiers to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1162, 3085, 1, 3, '', 'Can be used in a personal Map Device alongside a Map to add the monsters stored from a previous Ritual Altar to a Ritual Altar in that Map.', NULL, '', '[]', 0, NULL, '[]', '[]', 0, 'Can be used in a personal Map Device alongside a Map to add the monsters stored from a previous Ritual Altar to a Ritual Altar in that Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1163, 5417, 1, 3, '', 'Right click this item to view Mercenary details.' || char(13) || '
Can be used in a personal Map Device alongside a Map to have this previously fought Mercenary reappear in the area for a rematch.', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '<<xbox_button_lstick>> + <<xbox_button_rt>> to view Mercenary details.' || char(13) || '
Can be used in a personal Map Device alongside a Map to have this previously fought Mercenary reappear in the area for a rematch.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1164, 4216, 20, 2, 'add_flask_seal', 'Right click this item then left click a flask to apply it.', NULL, 'Adds an enchantment to a utility flask that will improve it' || char(13) || '
but prevent it from gaining charges during its effect' || char(13) || '
Replaces any existing enchantment', '[11237]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a flask to apply it.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1165, 4217, 20, 2, 'add_flask_injector', 'Right click this item then left click a flask to apply it.', NULL, 'Adds an enchantment to a utility flask that will cause' || char(13) || '
it to be used when certain conditions are met' || char(13) || '
Replaces any existing enchantment', '[11237]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a flask to apply it.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1166, 4218, 1000, 3, '', 'These can be brought to Tujen to refresh his vendor inventory.', NULL, '', '[]', 0, NULL, '[]', '[]', 1000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1167, 4219, 1000, 3, '', 'These can be brought to Rog to refresh his vendor inventory.', NULL, '', '[]', 0, NULL, '[]', '[]', 1000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1168, 4220, 1000, 3, '', 'These can be brought to Gwennen to refresh her vendor inventory.', NULL, '', '[]', 0, NULL, '[]', '[]', 1000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1169, 4221, 1000, 3, '', 'These can be brought to Dannig to refresh his vendor inventory.', NULL, '', '[]', 0, NULL, '[]', '[]', 1000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1170, 4222, 50000, 3, '', 'These can be used to trade with Gwennen.', NULL, '', '[]', 0, NULL, '[]', '[]', 50000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1171, 4223, 50000, 3, '', 'These can be used to trade with Gwennen.', NULL, '', '[]', 0, NULL, '[]', '[]', 50000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1172, 4224, 25000, 3, '', 'These can be used to trade with Gwennen.', NULL, '', '[]', 0, NULL, '[]', '[]', 25000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1173, 4225, 5000, 3, '', 'These can be used to trade with Gwennen.', NULL, '', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1174, 4226, 50000, 3, '', 'These can be used to trade with Tujen.', NULL, '', '[]', 0, NULL, '[]', '[]', 50000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1175, 4227, 50000, 3, '', 'These can be used to trade with Tujen.', NULL, '', '[]', 0, NULL, '[]', '[]', 50000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1176, 4228, 25000, 3, '', 'These can be used to trade with Tujen.', NULL, '', '[]', 0, NULL, '[]', '[]', 25000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1177, 4229, 5000, 3, '', 'These can be used to trade with Tujen.', NULL, '', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1178, 4230, 50000, 3, '', 'These can be used to trade with Rog.', NULL, '', '[]', 0, NULL, '[]', '[]', 50000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1179, 4231, 50000, 3, '', 'These can be used to trade with Rog.', NULL, '', '[]', 0, NULL, '[]', '[]', 50000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1180, 4232, 25000, 3, '', 'These can be used to trade with Rog.', NULL, '', '[]', 0, NULL, '[]', '[]', 25000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1181, 4233, 5000, 3, '', 'These can be used to trade with Rog.', NULL, '', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1182, 4234, 50000, 3, '', 'These can be used to trade with Dannig.', NULL, '', '[]', 0, NULL, '[]', '[]', 50000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1183, 4235, 50000, 3, '', 'These can be used to trade with Dannig.', NULL, '', '[]', 0, NULL, '[]', '[]', 50000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1184, 4236, 25000, 3, '', 'These can be used to trade with Dannig.', NULL, '', '[]', 0, NULL, '[]', '[]', 25000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1185, 4237, 5000, 3, '', 'These can be used to trade with Dannig.', NULL, '', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1186, 4238, 20, 2, 'reroll_socket_colours_hellscape', 'Right click this item then left click a corrupted socketed item to apply it. The item''s quality increases the chances of non-white sockets.', NULL, 'Unpredictably reforges the colour of sockets on a corrupted item', '[11653]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a corrupted socketed item to apply it. The item''s quality increases the chances of non-white sockets.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1187, 4239, 20, 2, 'reroll_socket_links_hellscape', 'Right click this item then left click a corrupted socketed item to apply it.', NULL, 'Unpredictably adds or removes a link to the largest group of linked sockets on a corrupted item', '[11653]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a corrupted socketed item to apply it.', 100, '[]', '[]', '[]', 0, '', 0, 1, 1),
(1188, 4240, 20, 2, 'reroll_socket_numbers_hellscape', 'Right click this item then left click a corrupted socketed item to apply it.', NULL, 'Unpredictably adds or removes a socket on a corrupted item', '[11653]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a corrupted socketed item to apply it.', 100, '[]', '[]', '[]', 0, '', 0, 1, 1),
(1189, 4241, 20, 2, 'reroll_rare_hellscape', 'Right click this item then left click a corrupted rare item to apply it.', NULL, 'Unpredictably either reforges a corrupted rare item with new random modifiers or removes all of its modifiers', '[11653]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a corrupted rare item to apply it.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1190, 4242, 20, 2, 'add_mod_to_rare_hellscape', 'Right click this item then left click a corrupted rare item to apply it. Rare items can have up to six random modifiers.', NULL, 'Unpredictably adds or removes a modifier on a corrupted rare item', '[11653]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a corrupted rare item to apply it. Rare items can have up to six random modifiers.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1191, 4243, 20, 2, 'upgrade_to_unique_hellscape', 'Right click this item then left click a corrupted item to apply it. Cannot apply to Maps or Vestigial items.', NULL, 'Unpredictably either upgrades a corrupted item to unique rarity or destroys it', '[11653]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an corrupted item to apply it. Cannot apply to Maps.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1192, 4244, 40, 2, 'add_armour_quality_hellscape', 'Right click this item then left click a corrupted armour to apply it. The maximum random quality is 20%.', NULL, 'Randomises the quality of a corrupted armour', '[11653]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a corrupted armour to apply it. The maximum random quality is 20%.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1193, 4245, 20, 2, 'add_weapon_quality_hellscape', 'Right click this item then left click a corrupted weapon to apply it. The maximum random quality is 20%.', NULL, 'Randomises the quality of a corrupted weapon', '[11653]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a corrupted weapon to apply it. The maximum random quality is 20%.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1194, 4246, 10, 2, 'upgrade_mod_tier_hellscape', 'Right click this item then left click a corrupted rare item to apply it.', NULL, 'Unpredictably raises or lowers the tier of each modifier on a corrupted rare item', '[11653]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a corrupted rare item to apply it.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1195, 4247, 20, 2, 'reroll_mod_values_outside_ranges_hellscape', 'Right click this item then left click a unique item to apply it. Corrupted items cannot be modified again.', NULL, 'Unpredictably either randomises the values of random modifiers on a
unique equipment and corrupts it, or destroys it', '[11289]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a unique item to apply it.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1196, 333, 10, 2, 'add_cleansing_fire_implicit_1', 'Right click this item then left click a normal, magic or rare item to apply it. Cannot be used on Shaper, Elder or Elderslayer influenced items.', NULL, 'Adds a Lesser Searing Exarch implicit modifier to' || char(13) || '
a Body Armour, Boots, Gloves or Helmet' || char(13) || '
This replaces any existing implicit modifiers other than Eater of Worlds implicit modifiers', '[8537]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal, magic or rare item to apply it. Cannot be used on Shaper, Elder or Elderslayer influenced items.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1197, 334, 10, 2, 'add_cleansing_fire_implicit_2', 'Right click this item then left click a normal, magic or rare item to apply it. Cannot be used on Shaper, Elder or Elderslayer influenced items.', NULL, 'Adds a Greater Searing Exarch implicit modifier to' || char(13) || '
a Body Armour, Boots, Gloves or Helmet' || char(13) || '
This replaces any existing implicit modifiers other than Eater of Worlds implicit modifiers', '[8538]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal, magic or rare item to apply it. Cannot be used on Shaper, Elder or Elderslayer influenced items.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1198, 335, 10, 2, 'add_cleansing_fire_implicit_3', 'Right click this item then left click a normal, magic or rare item to apply it. Cannot be used on Shaper, Elder or Elderslayer influenced items.', NULL, 'Adds a Grand Searing Exarch implicit modifier to' || char(13) || '
a Body Armour, Boots, Gloves or Helmet' || char(13) || '
This replaces any existing implicit modifiers other than Eater of Worlds implicit modifiers', '[8539]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal, magic or rare item to apply it. Cannot be used on Shaper, Elder or Elderslayer influenced items.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1199, 336, 10, 2, 'add_cleansing_fire_implicit_4', 'Right click this item then left click a normal, magic or rare item to apply it. Cannot be used on Shaper, Elder or Elderslayer influenced items.', NULL, 'Adds an Exceptional Searing Exarch implicit modifier to' || char(13) || '
a Body Armour, Boots, Gloves or Helmet' || char(13) || '
This replaces any existing implicit modifiers other than Eater of Worlds implicit modifiers', '[11652]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal, magic or rare item to apply it. Cannot be used on Shaper, Elder or Elderslayer influenced items.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1200, 337, 10, 2, 'add_great_tangle_implicit_1', 'Right click this item then left click a normal, magic or rare item to apply it. Cannot be used on Shaper, Elder or Elderslayer influenced items.', NULL, 'Adds a Lesser Eater of Worlds implicit modifier to' || char(13) || '
a Body Armour, Boots, Gloves or Helmet' || char(13) || '
This replaces any existing implicit modifiers other than Searing Exarch implicit modifiers', '[8534]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal, magic or rare item to apply it. Cannot be used on Shaper, Elder or Elderslayer influenced items.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1201, 338, 10, 2, 'add_great_tangle_implicit_2', 'Right click this item then left click a normal, magic or rare item to apply it. Cannot be used on Shaper, Elder or Elderslayer influenced items.', NULL, 'Adds a Greater Eater of Worlds implicit modifier to' || char(13) || '
a Body Armour, Boots, Gloves or Helmet' || char(13) || '
This replaces any existing implicit modifiers other than Searing Exarch implicit modifiers', '[8535]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal, magic or rare item to apply it. Cannot be used on Shaper, Elder or Elderslayer influenced items.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1202, 339, 10, 2, 'add_great_tangle_implicit_3', 'Right click this item then left click a normal, magic or rare item to apply it. Cannot be used on Shaper, Elder or Elderslayer influenced items.', NULL, 'Adds a Grand Eater of Worlds implicit modifier to' || char(13) || '
a Body Armour, Boots, Gloves or Helmet' || char(13) || '
This replaces any existing implicit modifiers other than Searing Exarch implicit modifiers', '[8536]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal, magic or rare item to apply it. Cannot be used on Shaper, Elder or Elderslayer influenced items.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1203, 340, 10, 2, 'add_great_tangle_implicit_4', 'Right click this item then left click a normal, magic or rare item to apply it. Cannot be used on Shaper, Elder or Elderslayer influenced items.', NULL, 'Adds an Exceptional Eater of Worlds implicit modifier to' || char(13) || '
a Body Armour, Boots, Gloves or Helmet' || char(13) || '
This replaces any existing implicit modifiers other than Searing Exarch implicit modifiers', '[11652]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a normal, magic or rare item to apply it. Cannot be used on Shaper, Elder or Elderslayer influenced items.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1204, 341, 10, 2, 'conflict_orb', 'Right click this item then left click a non-unique item to apply it. Can only be used on items that have both a Searing Exarch and an Eater of Worlds implicit modifier. The chance of raising or lowering a modifier depends on its relative strength.', NULL, 'Unpredictably raise the strength of one Searing Exarch or Eater of Worlds modifier on an item and lower the strength of another' || char(13) || '
Lesser modifiers that have their strength lowered will be removed', '[10506]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a non-unique item to apply it. Can only be used on items that have both a Searing Exarch and Eater of Worlds implicit modifier. The chance of raising or lowering a modifier depends on its relative strength.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1205, 342, 1, 3, '', 'Trade this in to Siosa inside the Library for a Skill Gem', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1206, 343, 10, 1, 'create_random_reliquary_map', 'Right click to open.', NULL, 'Contains a Mysterious Map with a Foil Unique Reward', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to open.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1207, 4250, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1208, 4251, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1209, 4252, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1210, 4253, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1211, 4254, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1212, 4255, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1213, 4256, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1214, 4257, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1215, 4258, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1216, 4259, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1217, 4260, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1218, 4261, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1219, 4262, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1220, 4263, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1221, 4264, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1222, 4265, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1223, 4266, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1224, 4267, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1225, 4268, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1226, 4269, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1227, 4270, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1228, 4271, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1229, 4272, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1230, 4273, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1231, 4274, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1232, 4275, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1233, 4276, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1234, 4277, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1235, 4278, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1236, 4279, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1237, 4280, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1238, 4281, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1239, 4282, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1240, 4283, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1241, 4284, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1242, 4285, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1243, 4286, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1244, 4287, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1245, 4288, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1246, 4289, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1247, 4290, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1248, 4291, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1249, 4292, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1250, 4293, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1251, 4294, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1252, 4295, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1253, 4296, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1254, 4297, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1255, 4298, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1256, 4299, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1257, 4300, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1258, 4301, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1259, 4302, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1260, 4303, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1261, 4304, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1262, 4305, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1263, 4306, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1264, 4307, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1265, 4308, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1266, 4309, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1267, 4310, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1268, 4311, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1269, 4312, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1270, 4313, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1271, 4314, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1272, 4315, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1273, 4316, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1274, 4317, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1275, 4318, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1276, 4319, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1277, 4320, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1278, 4321, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1279, 4322, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1280, 4323, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1281, 4324, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1282, 4325, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1283, 4326, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1284, 4327, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1285, 4328, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1286, 4329, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1287, 4330, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1288, 4331, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1289, 4332, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1290, 4333, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1291, 4334, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1292, 4335, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1293, 4336, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1294, 4337, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1295, 4338, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1296, 4339, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1297, 4340, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1298, 4341, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1299, 4342, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1300, 4343, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1301, 4344, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1302, 4345, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1303, 4346, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1304, 4347, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1305, 4348, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1306, 4349, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1307, 4350, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1308, 4351, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1309, 4352, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1310, 4353, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1311, 4354, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1312, 4355, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1313, 4356, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1314, 4357, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1315, 4358, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1316, 4359, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1317, 4360, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1318, 4361, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1319, 4362, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1320, 4363, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1321, 4364, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1322, 4365, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1323, 4366, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1324, 4367, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1325, 4368, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1326, 4369, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1327, 4370, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1328, 4371, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1329, 4372, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1330, 4373, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1331, 4374, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1332, 4375, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1333, 4376, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1334, 4377, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1335, 4378, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1336, 4379, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1337, 4380, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1338, 4381, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1339, 4382, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1340, 4383, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1341, 4384, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1342, 4385, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1343, 4386, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1344, 4387, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1345, 4388, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1346, 4389, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1347, 4390, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1348, 4391, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1349, 4392, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1350, 4393, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1351, 4394, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1352, 4395, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1353, 4396, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1354, 4397, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1355, 4398, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1356, 4399, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1357, 4400, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1358, 4401, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1359, 4402, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1360, 4403, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1361, 4404, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1362, 4405, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1363, 4406, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1364, 4407, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1365, 4408, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1366, 4409, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1367, 4410, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1368, 4411, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1369, 4412, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1370, 4413, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1371, 4414, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1372, 4415, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1373, 4416, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1374, 4417, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1375, 4418, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1376, 4419, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1377, 4420, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1378, 4421, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1379, 4422, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1380, 4423, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1381, 4424, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1382, 4425, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1383, 4426, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1384, 4427, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1385, 4428, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1386, 4429, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1387, 4430, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1388, 4431, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1389, 4432, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1390, 4433, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1391, 4434, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1392, 4435, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1393, 4436, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1394, 4437, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1395, 4438, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1396, 4439, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1397, 4440, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1398, 4441, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1399, 4442, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1400, 4443, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1401, 4444, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1402, 4445, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1403, 4446, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1404, 4447, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1405, 4448, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1406, 4449, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1407, 4450, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1408, 4451, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1409, 4452, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1410, 4453, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1411, 4454, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1412, 4455, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1413, 4456, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1414, 4457, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1415, 4458, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1416, 4459, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1417, 4460, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1418, 4461, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1419, 4462, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1420, 4463, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1421, 4464, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1422, 4465, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1423, 4466, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1424, 4467, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1425, 4468, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1426, 4469, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1427, 4470, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1428, 4471, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1429, 4472, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1430, 4473, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1431, 4474, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1432, 4475, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1433, 4476, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1434, 4477, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1435, 4478, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1436, 4479, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1437, 4480, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1438, 4481, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1439, 4482, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1440, 4483, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1441, 4484, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1442, 4485, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1443, 4486, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1444, 4487, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1445, 4488, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1446, 4489, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1447, 4490, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1448, 4491, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1449, 4492, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1450, 4493, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1451, 4494, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1452, 4495, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1453, 4496, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1454, 4497, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1455, 4498, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1456, 4499, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1457, 4500, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1458, 4501, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1459, 4502, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1460, 4503, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1461, 4504, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1462, 4505, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1463, 4506, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1464, 4507, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1465, 4508, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1466, 4509, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1467, 4510, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1468, 4511, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1469, 4512, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1470, 4513, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1471, 4514, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1472, 4515, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1473, 4516, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1474, 4517, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1475, 4518, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1476, 4519, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1477, 4520, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1478, 4521, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1479, 4522, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1480, 4523, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1481, 4524, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1482, 4525, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1483, 4526, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1484, 4527, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1485, 4528, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1486, 4529, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1487, 4530, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1488, 4531, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1489, 4532, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1490, 4533, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1491, 4534, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1492, 4535, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1493, 4536, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1494, 4537, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1495, 4538, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1496, 4539, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1497, 4540, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1498, 4541, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1499, 4542, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0);
INSERT INTO "CurrencyItems" ("_row", "BaseItemTypesKey", "StackSize", "CurrencyUseType", "Action", "Directions", "FullStack_BaseItemTypesKey", "Description", "Usage_AchievementItemsKeys", "Scroll", "Possession_AchievementItemsKey", "__col10", "__col11", "CurrencyTab_StackSize", "XBoxDirections", "__col14", "ModifyMapsAchievements", "ModifyContractsAchievements", "CombineAchievements", "ChangedForHardmode", "DescriptionHardmode", "IsGold", "__col21", "__col22") VALUES
(1500, 4543, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1501, 4544, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1502, 4545, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1503, 4546, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1504, 4547, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1505, 4548, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1506, 4549, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1507, 4550, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1508, 4551, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1509, 4552, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1510, 4553, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1511, 4554, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1512, 4555, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1513, 4556, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1514, 4557, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1515, 4558, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1516, 4559, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1517, 4560, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1518, 4561, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1519, 4562, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1520, 4563, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1521, 4564, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1522, 4565, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1523, 4566, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1524, 4567, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1525, 4568, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1526, 4569, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1527, 4570, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1528, 4571, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1529, 4572, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1530, 4573, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1531, 4574, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1532, 4575, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1533, 4576, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1534, 4577, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1535, 4578, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1536, 4579, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1537, 4580, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1538, 4581, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1539, 4582, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1540, 4583, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1541, 4584, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1542, 4585, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1543, 4586, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1544, 4587, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1545, 4588, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1546, 4589, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1547, 4590, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1548, 4591, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1549, 4592, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1550, 4593, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1551, 4594, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1552, 4595, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1553, 4596, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1554, 4597, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1555, 4598, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1556, 4599, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1557, 4600, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1558, 4601, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1559, 4602, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1560, 4603, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1561, 4604, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1562, 4605, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1563, 4606, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1564, 4607, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1565, 4608, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1566, 4609, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1567, 4610, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1568, 4611, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1569, 4612, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1570, 4613, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1571, 4614, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1572, 4615, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1573, 4616, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1574, 4617, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1575, 4618, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1576, 4619, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1577, 4620, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1578, 4621, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1579, 4622, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1580, 4623, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1581, 4624, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1582, 4625, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1583, 4626, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1584, 4627, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1585, 4628, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1586, 4629, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1587, 4630, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1588, 4631, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1589, 4632, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1590, 4633, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1591, 4634, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1592, 4635, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1593, 4636, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1594, 4637, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1595, 4638, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1596, 4639, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1597, 4640, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1598, 4641, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1599, 4642, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1600, 4643, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1601, 4644, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1602, 4645, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1603, 4646, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1604, 4647, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1605, 4648, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1606, 4649, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1607, 4650, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1608, 4651, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1609, 4652, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1610, 4653, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1611, 4654, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1612, 4655, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1613, 4656, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1614, 4657, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1615, 4658, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1616, 4659, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1617, 4660, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1618, 4661, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1619, 4662, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1620, 4663, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1621, 4664, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1622, 4665, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1623, 4666, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1624, 4667, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1625, 4668, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1626, 4669, 1, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1627, 4670, 20, 2, 'reroll_rare_eldritch', 'Right click this item then left click a rare item with The Searing Exarch or The Eater of Worlds dominance to apply it.', NULL, 'If The Searing Exarch is dominant, reroll prefix modifiers' || char(13) || '
If The Eater of Worlds is dominant, reroll suffix modifiers', '[11651]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a rare item with The Searing Exarch or The Eater of Worlds dominance to apply it.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1628, 4671, 20, 2, 'add_mod_to_rare_eldritch', 'Right click this item then left click a rare item with The Searing Exarch or The Eater of Worlds dominance to apply it. Rare items can have up to six random modifiers.', NULL, 'If The Searing Exarch is dominant, add a prefix modifier' || char(13) || '
If The Eater of Worlds is dominant, add a suffix modifier', '[11651]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a rare item with The Searing Exarch or The Eater of Worlds dominance to apply it. Rare items can have up to six random modifiers.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1629, 4672, 20, 2, 'remove_random_mod_eldritch', 'Right click this item then left click on a magic or rare item with The Searing Exarch or The Eater of Worlds dominance to apply it.', NULL, 'If The Searing Exarch is dominant, remove a prefix modifier' || char(13) || '
If The Eater of Worlds is dominant, remove a suffix modifier', '[11651]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a magic or rare item item with The Searing Exarch or The Eater of Worlds dominance to apply it.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1630, 4691, 20, 3, '', 'This item is no longer usable.', NULL, 'Formally used to reroll all of Kirac''s Atlas Missions, including at least one Unique map', '[8638]', 0, NULL, '[]', '[]', 10000, 'This item is no longer usable.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1631, 4692, 20, 3, '', 'This item is no longer usable.', NULL, 'Formally used to reroll all of Kirac''s Atlas Missions, including at least one Breachstone', '[8638]', 0, NULL, '[]', '[]', 10000, 'This item is no longer usable.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1632, 4693, 20, 3, '', 'This item is no longer usable.', NULL, 'Formally used to reroll all of Kirac''s Atlas Missions, adding additional mission options', '[8638]', 0, NULL, '[]', '[]', 10000, 'This item is no longer usable.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1633, 4694, 20, 3, '', 'This item is no longer usable.', NULL, 'Formally used to reroll all of Kirac''s Atlas Missions, corrupting all non-Unique Maps', '[8638]', 0, NULL, '[]', '[]', 10000, 'This item is no longer usable.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1634, 4695, 20, 3, '', 'This item is no longer usable.', NULL, 'Formally used to reroll all of Kirac''s Atlas Missions, adding layers of Delirium to all non-Unique Maps', '[8638]', 0, NULL, '[]', '[]', 10000, 'This item is no longer usable.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1635, 4696, 20, 3, '', 'This item is no longer usable.', NULL, 'Formally used to reroll all of Kirac''s Atlas Missions, granting missions with rewarding implicit modifiers', '[8638]', 0, NULL, '[]', '[]', 10000, 'This item is no longer usable.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1636, 4697, 20, 3, '', 'This item is no longer usable.', NULL, 'Formally used to reroll all of Kirac''s Atlas Missions, including at least one Blighted Map', '[8638]', 0, NULL, '[]', '[]', 10000, 'This item is no longer usable.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1637, 4698, 20, 3, '', 'This item is no longer usable.', NULL, 'Formally used to reroll all of Kirac''s Atlas Missions, including at least one Shaper Guardian, Elder Guardian, or Elderslayer Map', '[8638]', 0, NULL, '[]', '[]', 10000, 'This item is no longer usable.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1638, 4699, 20, 3, '', 'This item is no longer usable.', NULL, 'Formally used to reroll all of Kirac''s Atlas Missions, using uncompleted non-Unique Maps where possible', '[8638]', 0, NULL, '[]', '[]', 10000, 'This item is no longer usable.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1639, 5382, 10, 6, 'add_atlas_mission', 'Right click this item and then left click a Map on your Atlas to apply a Shaped Region.', NULL, 'Adds a Shaped Region to a quadrant of the Atlas', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map on your Atlas to apply a Shaped Region.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1640, 5383, 10, 6, 'add_atlas_mission', 'Right click this item and then left click a Map on your Atlas to apply a Shaped Region.', NULL, 'Adds a Shaped Region containing the Sacred Grove to a quadrant of the Atlas', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map on your Atlas to apply a Shaped Region.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1641, 5384, 10, 6, 'add_atlas_mission', 'Right click this item and then left click a Map on your Atlas to apply a Shaped Region.', NULL, 'Adds a Shaped Region containing [ContainsAbyss|Abysses] to a quadrant of the Atlas', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map on your Atlas to apply a Shaped Region.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1642, 5385, 10, 6, 'add_atlas_mission', 'Right click this item and then left click a Map on your Atlas to apply a Shaped Region.', NULL, 'Adds a Shaped Region containing Breaches to a quadrant of the Atlas', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map on your Atlas to apply a Shaped Region.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1643, 5386, 10, 6, 'add_atlas_mission', 'Right click this item and then left click a Map on your Atlas to apply a Shaped Region.', NULL, 'Adds a Shaped Region containing Ritual Altars to a quadrant of the Atlas', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map on your Atlas to apply a Shaped Region.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1644, 5387, 10, 6, 'add_atlas_mission', 'Right click this item and then left click a Map on your Atlas to apply a Shaped Region.', NULL, 'Adds a Shaped Region containing Blight encounters to a quadrant of the Atlas', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map on your Atlas to apply a Shaped Region.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1645, 5388, 10, 6, 'add_atlas_mission', 'Right click this item and then left click a Map on your Atlas to apply a Shaped Region.', NULL, 'Adds a Shaped Region containing Ultimatum encounters to a quadrant of the Atlas', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map on your Atlas to apply a Shaped Region.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1646, 5389, 10, 6, 'add_atlas_mission', 'Right click this item and then left click a Map on your Atlas to apply a Shaped Region.', NULL, 'Adds a Shaped Region containing Mirrors of Delirium to a quadrant of the Atlas', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map on your Atlas to apply a Shaped Region.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1647, 5390, 10, 6, 'add_atlas_mission', 'Right click this item and then left click a Map on your Atlas to apply a Shaped Region.', NULL, 'Adds a Shaped Region containing Legion encounters to a quadrant of the Atlas', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map on your Atlas to apply a Shaped Region.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1648, 5391, 10, 6, 'add_atlas_mission', 'Right click this item and then left click a Map on your Atlas to apply a Shaped Region.', NULL, 'Adds a Shaped Region containing Expedition encounters to a quadrant of the Atlas', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map on your Atlas to apply a Shaped Region.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1649, 5392, 10, 6, 'add_atlas_mission', 'Right click this item and then left click a Map on your Atlas to apply a Shaped Region.', NULL, 'Adds a Shaped Region containing Ore Deposits to a quadrant of the Atlas', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map on your Atlas to apply a Shaped Region.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1650, 5393, 10, 2, 'legion_cocoon_unique', 'Right click this item and then left click a Unique item.', NULL, '[Enshroud|Enshrouds] Unique Gloves', '[11663]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Unique item.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1651, 5394, 10, 2, 'legion_cocoon_unique', 'Right click this item and then left click a Unique item.', NULL, '[Enshroud|Enshrouds] Unique Boots', '[11663]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Unique item.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1652, 5395, 10, 2, 'legion_cocoon_unique', 'Right click this item and then left click a Unique item.', NULL, '[Enshroud|Enshrouds] Unique Helmets', '[11663]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Unique item.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1653, 5396, 10, 2, 'legion_cocoon_unique', 'Right click this item and then left click a Unique item.', NULL, '[Enshroud|Enshrouds] Unique Shields', '[11663]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Unique item.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1654, 5397, 10, 2, 'legion_cocoon_unique', 'Right click this item and then left click a Unique item.', NULL, '[Enshroud|Enshrouds] Unique Body Armours', '[11663]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Unique item.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1655, 4700, 10, 2, 'extract_oil', 'Right click this item then left click an Anointed item to destroy it.', NULL, 'Destroys an Anointed Item to recover one of the Oils that was used to apply that Anointment', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an Anointed item to destroy it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1656, 4727, 20, 1, 'sentinel_craft', 'Right click this item then select two Sentinels to combine.', NULL, 'Destroys two Sentinels to create a fully-charged Sentinel with a combination of their properties. Sometimes, the resultant item is modified unpredictably', '[]', 0, NULL, '[]', '[]', 400, 'Press <<xbox_button_a>> then select two Sentinels to combine.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1657, 4728, 20, 1, 'sentinel_craft', 'Right click this item then select two Sentinels to combine.', NULL, 'Destroys two Sentinels to create a fully-charged Sentinel with a combination of their properties. Sometimes, the resultant item is modified unpredictably, with a higher chance of a special base type', '[]', 0, NULL, '[]', '[]', 400, 'Press <<xbox_button_a>> then select two Sentinels to combine.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1658, 4729, 20, 1, 'sentinel_craft', 'Right click this item then select two Sentinels to combine.', NULL, 'Destroys two Sentinels to create a fully-charged Sentinel with a combination of their properties. Sometimes, the resultant item is modified unpredictably, with a higher chance of a upgrading the tier of modifiers', '[]', 0, NULL, '[]', '[]', 400, 'Press <<xbox_button_a>> then select two Sentinels to combine.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1659, 4730, 20, 1, 'sentinel_craft', 'Right click this item then select two Sentinels to combine.', NULL, 'Destroys two Sentinels to create a fully-charged Sentinel with a combination of their properties. Sometimes, the resultant item is modified unpredictably, with a higher chance of new random modifiers', '[]', 0, NULL, '[]', '[]', 400, 'Press <<xbox_button_a>> then select two Sentinels to combine.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1660, 4731, 20, 1, 'sentinel_craft', 'Right click this item then select two Armours to combine. These items must be of the same item class.', NULL, 'Destroys two Armours to create a new Armour with a combination of their properties. Sometimes, the resultant item is modified unpredictably', '[]', 0, NULL, '[]', '[]', 10000, 'Press <<xbox_button_a>> then select two Armours to combine. These items must be of the same item class.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1661, 4732, 20, 1, 'sentinel_craft', 'Right click this item then select two Weapons or Quivers to combine. These items must be of the same item class.', NULL, 'Destroys two Weapons or Quivers to create a new Weapon or Quiver with a combination of their properties. Sometimes, the resultant item is modified unpredictably', '[]', 0, NULL, '[]', '[]', 10000, 'Press <<xbox_button_a>> then select two Weapons or Quivers to combine. These items must be of the same item class.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1662, 4733, 20, 1, 'sentinel_craft', 'Right click this item then select two Jewellery items to combine. These items must be of the same item class.', NULL, 'Destroys two Jewellery items to create a new Jewellery item with a combination of their properties. Sometimes, the resultant item is modified unpredictably', '[]', 0, NULL, '[]', '[]', 10000, 'Press <<xbox_button_a>> then select two Jewellery items to combine. These items must be of the same item class.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1663, 4762, 50000, 3, '', 'Can be used at the Horticrafting bench in your hideout.', NULL, '', '[]', 0, NULL, '[]', '[]', 50000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1664, 4763, 50000, 3, '', 'Can be used at the Horticrafting bench in your hideout.', NULL, '', '[]', 0, NULL, '[]', '[]', 50000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1665, 4764, 50000, 3, '', 'Can be used at the Horticrafting bench in your hideout.', NULL, '', '[]', 0, NULL, '[]', '[]', 50000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1666, 4765, 10, 3, '', 'Can be used at the Horticrafting bench in your hideout.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1667, 4766, 10, 3, '', 'Can be used at the Horticrafting bench in your hideout.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1668, 4767, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Minion Instability to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1669, 4768, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Resolute Technique to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1670, 4769, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Blood Magic to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1671, 4770, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Pain Attunement to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1672, 4771, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Elemental Equilibrium to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1673, 4772, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Iron Grip to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1674, 4773, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Point Blank to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1675, 4774, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Acrobatics to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1676, 4775, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Ghost Reaver to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1677, 4776, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Vaal Pact to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1678, 4777, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Elemental Overload to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1679, 4778, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Avatar of Fire to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1680, 4779, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Eldritch Battery to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1681, 4780, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Ancestral Bond to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1682, 4781, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Crimson Dance to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1683, 4782, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Perfect Agony to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1684, 4783, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Runebinder to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1685, 4784, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Glancing Blows to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1686, 4785, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Call To Arms to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1687, 4786, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds the Agnostic to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1688, 4787, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Supreme Ego to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1689, 4788, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds The Impaler to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1690, 4789, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Hex Master to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1691, 4790, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Lethe Shade to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1692, 4791, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Ghost Dance to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1693, 4792, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Versatile Combatant to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1694, 4793, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Magebane to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1695, 4794, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Solipsism to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1696, 4795, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Divine Shield to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1697, 4796, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Iron Will to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1698, 4797, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Wicked Ward to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1699, 4798, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Wind Dancer to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1700, 4799, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Conduit to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1701, 4800, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Arrow Dancing to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1702, 4801, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Precise Technique to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1703, 4802, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Iron Reflexes to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1704, 4803, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Unwavering Stance to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1705, 4804, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Imbalanced Guard to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1706, 4805, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Eternal Youth to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1707, 4806, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Mind over Matter to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1708, 4807, 10, 2, 'corrupt_relic', 'Right click this item then left click a non-Corrupted Sanctified Relic to apply it.', NULL, 'Adds Zealot''s Oath to a Sanctified Relic', '[]', 0, NULL, '[]', '[]', 5000, '<<xbox_button_a>> to select, then  <<xbox_button_a>> on a non-Corrupted Sanctified Relic to apply it.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1709, 4808, 50000, 3, '', '', NULL, 'Can be spent at Vendors', '[]', 0, NULL, '[]', '[]', 50000, '', 0, '[]', '[]', '[]', 0, '', 1, 0, 0),
(1710, 4809, 1, 1, 'open_crucible_geode', 'Right click on this item to open it.', NULL, '', '[]', 0, NULL, '[]', '[]', 0, 'Press <<xbox_button_a>> to open.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1711, 4810, 1, 1, 'open_crucible_geode', 'Right click on this item to open it.', NULL, '', '[]', 0, NULL, '[]', '[]', 0, 'Press <<xbox_button_a>> to open.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1712, 4823, 30, 1, 'silver_coin', 'Right click on this item while in a Town or Hideout to use it. This item is not consumed on use.', NULL, 'Creates a portal to the Halls of the Dead from a Town or Hideout
Used as Currency in the Halls of the Dead to participate in the Trial of the Ancestors', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to use. This item is not consumed on use.', 100, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1713, 4811, 1, 3, '', '', NULL, '<currencyitem>{5x Divine Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1714, 4812, 5, 3, '', '', NULL, 'Disabled', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1715, 4813, 8, 3, '', '', NULL, '<uniqueitem>{Arakaali''s Fang}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1716, 4814, 4, 3, '', '', NULL, '<gemitem>{Level 21 Vaal Cold Snap}' || char(13) || '
<default>{Quality:} <augmented>{+20%}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1717, 4815, 8, 3, '', '', NULL, '<uniqueitem>{Item}' || char(13) || '
<legiondivergent>{Vestigial}' || char(13) || '
<default>{Item Level:} <normal>{86}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1718, 4824, 10, 2, 'hinekoras_lock', 'Right click this item then left click an item to apply it.', NULL, 'Allows an item to foresee the result of the next Currency item used on it
Modifying the item in any way removes the ability to foresee', '[11659]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an item to apply it.', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1719, 4825, 1, 3, '', 'Each player can only consume one Omen in each combat area.', NULL, 'Will be Consumed when you Die
to prevent 75% of Experience loss', '[9780,9853]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1720, 4826, 1, 3, '', 'Each player can only consume one Omen in each combat area.', NULL, 'Will be Consumed when you Die
to create a Portal to Town', '[9781,9854]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1721, 4827, 1, 3, '', 'Each player can only consume one Omen in each combat area.', NULL, 'Will be Consumed when you reach 25% of Life
to grant 75% chance to Avoid Damage from Hits for 4 seconds', '[9782,9855]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1722, 4828, 1, 3, '', 'Each player can only consume one Omen in each combat area.', NULL, 'Will be Consumed when you reach 25% of Life
to Refill all Flasks', '[9783,9856]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1723, 4829, 1, 3, '', 'Each player can only consume one Omen in each combat area.', NULL, 'Will be Consumed when you level up
to grant Soul Eater for 5 minutes', '[9784,9857]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1724, 4830, 1, 3, '', 'Each player can only consume one Omen in each combat area.', NULL, 'Will be Consumed when you level up
to create an Acceleration Shrine', '[9785,9858]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1725, 4831, 1, 3, '', 'Each player can only consume one Omen in each combat area.', NULL, 'Will be Consumed when you use an Orb of Chance
on an item which can become Unique to ensure it does so', '[9786,9859]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1726, 4832, 1, 3, '', 'Each player can only consume one Omen in each combat area.', NULL, 'Will be Consumed when you use a Jeweller''s Orb
on an item to ensure maximum Sockets', '[9787,9860]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1727, 4833, 1, 3, '', 'Each player can only consume one Omen in each combat area.', NULL, 'Will be Consumed when you use an Orb of Fusing
on an item to ensure maximum Links', '[9788,9861]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1728, 4834, 1, 3, '', 'Each player can only consume one Omen in each combat area.', NULL, 'Will be Consumed when you use a Chromatic Orb
on an item to guarantee at least one Red, Green and Blue Socket', '[9789,9862]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1729, 4835, 1, 3, '', 'Each player can only consume one Omen in each combat area.', NULL, 'Will be Consumed when you Die
to make each nearby enemy permanently take 25% more Damage', '[9873,9867]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1730, 4836, 1, 3, '', 'Each player can only consume one Omen in each combat area.', NULL, 'Will be Consumed when you Die
to add 300% of lost Experience to Gems', '[9874,9868]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1731, 4837, 1, 3, '', 'Each player can only consume one Omen in each combat area.', NULL, 'Will be Consumed when you reach 25% of Life
to cause 100% of Damage taken to be Recouped as Life for 4 seconds', '[9875,9869]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1732, 4838, 1, 3, '', 'Each player can only consume one Omen in each combat area.', NULL, 'Will be Consumed when you reach 25% of Life
to grant Adrenaline for 10 seconds', '[9876,9870]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1733, 4839, 1, 3, '', 'Each player can only consume one Omen in each combat area.', NULL, 'Will be Consumed when you reach 25% of Life
to grant Shade Form for 3 seconds', '[9877,9871]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1734, 4840, 1, 3, '', 'Each player can only consume one Omen in each combat area.', NULL, 'Will be Consumed when you level up
to grant 100% increased Experience gained for 60 seconds', '[9878,9872]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1735, 4841, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1736, 4842, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1737, 4843, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1738, 4844, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1739, 4845, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1740, 4846, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1741, 4847, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1742, 4848, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1743, 4849, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1744, 4850, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1745, 4851, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1746, 4852, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1747, 4853, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1748, 4854, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1749, 4855, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1750, 4856, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1751, 4857, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1752, 4858, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1753, 4859, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1754, 4860, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1755, 4861, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1756, 4862, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1757, 4863, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1758, 4864, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1759, 4865, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1760, 4866, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1761, 4867, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1762, 4868, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1763, 4869, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1764, 4870, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1765, 4871, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1766, 4872, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1767, 4873, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1768, 4874, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1769, 4875, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1770, 4876, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1771, 4877, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1772, 4878, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1773, 4879, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1774, 4880, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1775, 4881, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1776, 4882, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1777, 4883, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1778, 4884, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1779, 4885, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1780, 4886, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1781, 4887, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1782, 4888, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1783, 4889, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1784, 4890, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1785, 4891, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1786, 4892, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1787, 4893, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1788, 4894, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1789, 4895, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1790, 4896, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1791, 4897, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1792, 4898, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1793, 4899, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1794, 4900, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1795, 4901, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1796, 4902, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1797, 4903, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1798, 4904, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1799, 4905, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1800, 4906, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1801, 4907, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1802, 4908, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1803, 4909, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1804, 4910, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1805, 4911, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1806, 4912, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1807, 4913, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1808, 4914, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1809, 4915, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1810, 4916, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1811, 4917, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1812, 4918, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1813, 4919, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1814, 4920, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1815, 4921, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1816, 4922, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1817, 4923, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1818, 4924, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1819, 4925, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1820, 4926, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1821, 4927, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1822, 4928, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1823, 4929, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1824, 4930, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1825, 4931, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1826, 4932, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1827, 4933, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click an allocated Passive Skill. Maximum 50 Tattoos.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Skill to apply it. Maximum 50 Tattoos.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1828, 4934, 10, 3, '', '', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1829, 4935, 10, 3, '', '', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1830, 4936, 10, 3, '', '', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1831, 4937, 10, 3, '', '', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1832, 4938, 10, 3, '', '', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1833, 4939, 10, 3, '', '', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1834, 4940, 10, 3, '', '', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1835, 4941, 12, 3, '', '', NULL, '<currencyitem>{2x Divine Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1836, 4942, 7, 3, '', '', NULL, '<uniqueitem>{Nimis}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1837, 4943, 2, 3, '', '', NULL, '<uniqueitem>{Sublime Vision}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1838, 5082, 9, 3, '', '', NULL, '<magicitem>{Dictator''s Weapon}' || char(13) || '
<default>{Item Level:} <normal>{83}' || char(13) || '
<fractured>{Fractured}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1839, 5083, 4, 3, '', '', NULL, '<whiteitem>{Item}' || char(13) || '
<default>{Item Level:} <normal>{100}' || char(13) || '
<enchanted>{Perfect Eldritch Implicit Modifier}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1840, 5084, 9, 3, '', '', NULL, '<uniqueitem>{Divinarius}' || char(13) || '
<corrupted>{Two-Implicit}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1841, 5085, 2, 3, '', '', NULL, '<currencyitem>{2x Fracturing Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1842, 4965, 50000, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 50000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1843, 4966, 50000, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 50000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1844, 4967, 50000, 3, '', '', NULL, '', '[]', 0, NULL, '[]', '[]', 50000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1845, 208, 20, 1, 'create_random_scarab', 'Right Click to reveal a random Scarab', NULL, 'A collection of unknown Scarabs', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to reveal a random Scarab', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1846, 5177, 250, 3, '', 'Can be used for Runesmithing Weapons at the Kingsmarch Blacksmith.', NULL, '', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1847, 5178, 250, 3, '', 'Can be used for Runesmithing Weapons at the Kingsmarch Blacksmith.', NULL, '', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1848, 5179, 250, 3, '', 'Can be used for Runesmithing Weapons at the Kingsmarch Blacksmith.', NULL, '', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1849, 5180, 250, 3, '', 'Can be used for Runesmithing Weapons at the Kingsmarch Blacksmith.', NULL, '', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1850, 5181, 250, 3, '', 'Can be used for Runesmithing Weapons at the Kingsmarch Blacksmith.', NULL, '', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1851, 5182, 250, 3, '', 'Can be used for Runesmithing Weapons at the Kingsmarch Blacksmith.', NULL, '', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1852, 5183, 250, 3, '', 'Can be used for Runesmithing Weapons at the Kingsmarch Blacksmith.', NULL, '', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1853, 5184, 250, 3, '', 'Can be used for Runesmithing Weapons at the Kingsmarch Blacksmith.', NULL, '', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1854, 5185, 250, 3, '', 'Can be used for Runesmithing Weapons at the Kingsmarch Blacksmith.', NULL, '', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1855, 5186, 250, 3, '', 'Can be used for Runesmithing Weapons at the Kingsmarch Blacksmith.', NULL, '', '[]', 0, NULL, '[]', '[]', 5000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1856, 5242, 1, 3, '', 'Open a portal to the Crux of Nothingness by using this item in a personal Map Device. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Open a portal to the Crux of Nothingness by using this item in a personal Map Device. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1857, 4682, 1, 3, '', 'Open portals to the Moment of Trauma to face the Incarnation of Fear by using this item in a personal Map Device. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Open portals to the Moment of Trauma to face the Incarnation of Fear by using this item in a personal Map Device. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1858, 4684, 1, 3, '', 'Open portals to the Moment of Reverence to face the Incarnation of Dread by using this item in a personal Map Device. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Open portals to the Moment of Reverence to face the Incarnation of Dread by using this item in a personal Map Device. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1859, 4686, 1, 3, '', 'Open portals to the Moment of Loneliness to face the Incarnation of Neglect by using this item in a personal Map Device. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Open portals to the Moment of Loneliness to face the Incarnation of Neglect by using this item in a personal Map Device. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1860, 4687, 10, 6, 'add_memory_line', 'Right click this item and then left click a Map on your Atlas to apply a Memory Thread to that quadrant.', NULL, 'Adds a Memory Thread to the Atlas leading to the Incarnation of Fear', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map on your Atlas to apply a Memory Thread to that quadrant.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1861, 4688, 10, 6, 'add_memory_line', 'Right click this item and then left click a Map on your Atlas to apply a Memory Thread to that quadrant.', NULL, 'Adds a Memory Thread to the Atlas leading to the Incarnation of Dread', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map on your Atlas to apply a Memory Thread to that quadrant.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1862, 4689, 10, 6, 'add_memory_line', 'Right click this item and then left click a Map on your Atlas to apply a Memory Thread to that quadrant.', NULL, 'Adds a Memory Thread to the Atlas leading to the Incarnation of Neglect', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a Map on your Atlas to apply a Memory Thread to that quadrant.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1863, 5243, 3, 3, '', '', NULL, '<currencyitem>{1,500x Vivid Crystallised Lifeforce}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1864, 5244, 4, 3, '', '', NULL, '<uniqueitem>{Relic}' || char(13) || '
<default>{Item Level:} <normal>{83}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1865, 5245, 10, 3, '', '', NULL, '<uniqueitem>{Defiance of Destiny}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1866, 5246, 5, 3, '', '', NULL, '<uniqueitem>{Dendrobate}' || char(13) || '
 <corrupted>{Two-Implicit}' || char(13) || '
 <corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1867, 5286, 20, 3, '', 'Can be used in a personal Map Device to substitute the varities of Monster Packs present in a Map.', NULL, 'Substitutes Monster Packs in Area
Pack Leaders are random Syndicate Members which
can have random Syndicate Gear equipped
Pack Monsters revive after Final Map Boss has been slain', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to substitute the varities of Monster Packs present in a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1868, 5247, 10, 2, 'reflect_jewelry', 'Right click this item then left click a rare amulet or ring to reflect it. Cannot be used on Veiled or Influenced items.', NULL, 'Unpredictably reflects a rare amulet or ring, breaking it
into two mirrored halves with reflected modifiers', '[11661]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a rare amulet or ring to reflect it. Cannot be used on Veiled or Influenced items.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1869, 5248, 1, 3, '', 'Open a portal to the Mastermind''s Lair by using this item in a personal Map Device. Can only be used once. While viewing your Atlas, Right-click this item to pinpoint its location.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, 'Open a portal to the Mastermind''s Lair by using this item in a personal Map Device. Can only be used once.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1870, 5249, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1871, 5250, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1872, 5251, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1873, 5252, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1874, 5253, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1875, 5254, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1876, 5255, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1877, 5256, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1878, 5257, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1879, 5258, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1880, 5259, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1881, 5260, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1882, 5261, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1883, 5262, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1884, 5263, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1885, 5264, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1886, 5265, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1887, 5266, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1888, 5267, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1889, 5268, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1890, 5269, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1891, 5270, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1892, 5271, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1893, 5272, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1894, 5273, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1895, 5274, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1896, 5275, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1897, 5276, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1898, 5277, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1899, 5278, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1900, 5279, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1901, 5280, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1902, 5281, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1903, 5282, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1904, 5283, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1905, 5284, 10, 7, 'apply_passive_tattoo', 'Right click this item then left click
an allocated Passive Mastery Skill.
Only one Runegraft of any given
type can be in use at a time.', NULL, '', '[11662]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on an allocated Passive Mastery Skill to apply. Only one copy of any given Runegraft can be in use at a time.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1906, 5287, 20, 3, '', 'Can be used in a personal Map Device to substitute the varities of Monster Packs present in a Map.', NULL, 'Substitutes Monster Packs in Area
Pack Monsters do not drop items
Pack Monsters grant increased Item Rarity when slain, up to
400%, decaying over time', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to substitute the varities of Monster Packs present in a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1907, 5288, 20, 3, '', 'Can be used in a personal Map Device to substitute the varities of Monster Packs present in a Map.', NULL, 'Substitutes Monster Packs in Area
Pack Monsters that die when Rare spread
Modifiers to other Rare Monsters in Area', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to substitute the varities of Monster Packs present in a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1908, 5289, 20, 3, '', 'Can be used in a personal Map Device to substitute the varities of Monster Packs present in a Map.', NULL, 'Substitutes Monster Packs in Area
Pack has single Monster
Monster has greatly increased Life', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to substitute the varities of Monster Packs present in a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1909, 5290, 20, 3, '', 'Can be used in a personal Map Device to substitute the varities of Monster Packs present in a Map.', NULL, 'Substitutes Monster Packs in Area
Pack Leader Empowers Map Bosses
with Wildwood Wisps when slain', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to substitute the varities of Monster Packs present in a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1910, 5291, 20, 3, '', 'Can be used in a personal Map Device to substitute the varities of Monster Packs present in a Map.', NULL, 'Substitutes Monster Packs in Area
Pack Monsters cannot drop
Equipment, Flasks or Tinctures', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to substitute the varities of Monster Packs present in a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1911, 5292, 20, 3, '', 'Can be used in a personal Map Device to substitute the varities of Monster Packs present in a Map.', NULL, 'Substitutes Monster Packs in Area
Pack Monster Equipment Drops are converted to Gold', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to substitute the varities of Monster Packs present in a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1912, 5293, 20, 3, '', 'Can be used in a personal Map Device to substitute the varities of Monster Packs present in a Map.', NULL, 'Substitutes Monster Packs in Area
Pack Monsters are Harmless Toads', '[]', 0, NULL, '[]', '[]', 10000, 'Can be used in a personal Map Device to substitute the varities of Monster Packs present in a Map.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1913, 5294, 10, 2, 'upgrade_unique', 'Right click this item then left click a valid Afarud Unique item to restore it to its Maraketh form.', NULL, 'Restores an Afarud Unique item to its past Maraketh form', '[11287]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a valid Afarud Unique item to restore it to its Maraketh form.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1914, 5295, 10, 2, 'upgrade_and_corrupt_unique', 'Right click this item then left click a valid Maraketh Unique item to twist it into its Afarud form.', NULL, 'Twists a Maraketh Unique item into its Afarud form,
Corrupting it and adding an Implicit Modifier', '[11288]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a valid Maraketh Unique item to twist it into its Afarud form.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1915, 5296, 10, 2, 'add_built_in_support', 'Right click this item then left click a level 20 Skill Gem to corrupt it. The added Support effect will always be valid for the Skill Gem. Corrupted items cannot be modified again.', NULL, 'Corrupts a level 20 Skill Gem, Imbuing it with a random Intelligence Support effect', '[11284]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a level 20 Skill Gem to corrupt it. The added Support effect will always be valid for the Skill Gem. Corrupted items cannot be modified again.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1916, 5297, 10, 2, 'add_built_in_support', 'Right click this item then left click a level 20 Skill Gem to corrupt it. The added Support effect will always be valid for the Skill Gem. Corrupted items cannot be modified again.', NULL, 'Corrupts a level 20 Skill Gem, Imbuing it with a random Strength Support effect', '[11285]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a level 20 Skill Gem to corrupt it. The added Support effect will always be valid for the Skill Gem. Corrupted items cannot be modified again.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1917, 5298, 10, 2, 'add_built_in_support', 'Right click this item then left click a level 20 Skill Gem to corrupt it. The added Support effect will always be valid for the Skill Gem. Corrupted items cannot be modified again.', NULL, 'Corrupts a level 20 Skill Gem, Imbuing it with a random Dexterity Support effect', '[11286]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a level 20 Skill Gem to corrupt it. The added Support effect will always be valid for the Skill Gem. Corrupted items cannot be modified again.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1918, 5299, 20, 2, 'special_corrupt_item', '', NULL, 'Corrupts a Unique item, modifying it unpredictably
Can Modify Corrupted Items
Items Corrupted this way can have up to 2 Implicit Modifiers', '[]', 0, NULL, '[]', '[]', 10000, '', 100, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1919, 5300, 10, 2, 'reflect_jewelry', 'Right click this item then left click a rare amulet or ring to reflect it. Cannot be used on Veiled or Influenced items.', NULL, 'Unpredictably reflects a rare amulet or ring, breaking it
into two mirrored halves with reflected modifiers', '[]', 0, NULL, '[]', '[]', 10000, '<<xbox_button_a>> to select, then <<xbox_button_a>> on a rare amulet or ring to reflect it. Cannot be used on Veiled or Influenced items.', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1920, 5310, 4, 3, '', '', NULL, '<currencyitem>{10x Vendor Refresh Currency}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1921, 5311, 3, 3, '', '', NULL, '<uniqueitem>{The Surrender}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1922, 5312, 16, 3, '', '', NULL, '<uniqueitem>{The Original Scripture}' || char(13) || '
<default>{Item Level:} <normal>{83}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1923, 5313, 2, 3, '', '', NULL, '<uniqueitem>{Stormfire}' || char(13) || '
<default>{Quality:} <augmented>{+20%}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1924, 5314, 5, 3, '', '', NULL, '<currencyitem>{2x Hinekora''s Lock}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1925, 5301, 20, 2, 'reroll_basic_gem', '', NULL, 'Changes a Support or Skill Gem into another of the same type
Retains Gem Level, Quality and Experience', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1926, 5349, 1, 3, '', 'Place this item into an allocated equipment item womb on the Genesis Tree. Right click to retrieve from the Genesis Tree.', NULL, 'Can grow into an Armour or Jewellery item on the Genesis Tree', '[]', 0, NULL, '[]', '[]', 0, 'Place this item into an allocated equipment item womb on the Genesis Tree. <<xbox_button_a>> to retrieve from the Genesis Tree.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1927, 5350, 1, 3, '', 'Place this item into an allocated equipment item womb on the Genesis Tree. Right click to retrieve from the Genesis Tree.', NULL, 'Can grow into a Currency item on the Genesis Tree', '[]', 0, NULL, '[]', '[]', 0, 'Place this item into an allocated equipment item womb on the Genesis Tree. <<xbox_button_a>> to retrieve from the Genesis Tree.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1928, 5351, 1, 3, '', 'Place this item into an allocated equipment item womb on the Genesis Tree. Right click to retrieve from the Genesis Tree.', NULL, 'Can grow into a Unique item on the Genesis Tree', '[]', 0, NULL, '[]', '[]', 0, 'Place this item into an allocated equipment item womb on the Genesis Tree. <<xbox_button_a>> to retrieve from the Genesis Tree.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1929, 5352, 1, 3, '', 'Place this item into an allocated equipment item womb on the Genesis Tree. Right click to retrieve from the Genesis Tree.', NULL, 'Can grow into a Graft item on the Genesis Tree', '[]', 0, NULL, '[]', '[]', 0, 'Place this item into an allocated equipment item womb on the Genesis Tree. <<xbox_button_a>> to retrieve from the Genesis Tree.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1930, 5353, 1, 3, '', 'Place this item into an allocated equipment item womb on the Genesis Tree. Right click to retrieve from the Genesis Tree.', NULL, 'Can grow into an item on the Genesis Tree', '[]', 0, NULL, '[]', '[]', 0, 'Place this item into an allocated equipment item womb on the Genesis Tree. <<xbox_button_a>> to retrieve from the Genesis Tree.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1931, 5357, 5, 3, '', '', NULL, '<uniqueitem>{Replica Dragonfang''s Flight}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1932, 5358, 8, 3, '', '', NULL, '<currencyitem>{Reflecting Mist}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1933, 5359, 5, 3, '', '', NULL, '<whiteitem>{Experimented Jewellery}' || char(13) || '
<default>{Item Level:} <normal>{86}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1934, 5360, 2, 3, '', '', NULL, '<gemitem>{Level 21 Flame Link}' || char(13) || '
<default>{Quality:} <augmented>{+20%}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1935, 5361, 2, 3, '', '', NULL, '<currencyitem>{Veiled Exalted Orb}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1936, 5403, 99, 1, 'grant_event_point', '', NULL, 'Grants an Event Point.', '[]', 0, NULL, '[]', '[]', 0, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1937, 5404, 3, 3, '', '', NULL, '<uniqueitem>{Originator Incarnation Item}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1938, 5405, 6, 3, '', '', NULL, '<uniqueitem>{Svalinn}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1939, 5406, 3, 3, '', '', NULL, '<gemitem>{Awakened Exceptional Gem}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1940, 5407, 2, 3, '', '', NULL, '<uniqueitem>{Eyes of the Greatwolf}' || char(13) || '
<default>{Item Level:} <normal>{82}' || char(13) || '
<corrupted>{Corrupted}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1941, 5408, 5, 3, '', '', NULL, '<currencyitem>{Hinekora''s Lock}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1942, 5409, 1, 3, '', '', NULL, '<whiteitem>{Uber Pinnacle Fragment}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1943, 5410, 9, 3, '', '', NULL, '<whiteitem>{6x Horned Scarab}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1944, 5411, 2, 3, '', '', NULL, '<uniqueitem>{Kalandra''s Touch}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1945, 5418, 10, 2, 'add_mod_and_corrupt_rare_abyss_jewel', 'Can be used as part of Allflame Crafting aboard The Sovereign.', NULL, 'Augment a Rare Abyssal Jewel with a random additional Modifier and corrupt it, even if it is already at maximum Modifier count.', '[11586]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1946, 5419, 10, 2, 'reroll_rare_infamous', 'Can be used as part of Allflame Crafting aboard The Sovereign.', NULL, 'Reforges a Rare item with random modifiers, including an Infamous modifier', '[11740]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1947, 5420, 10, 3, '', 'Can be used as part of Allflame Crafting aboard The Sovereign.', NULL, '', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1948, 5421, 50000, 3, '', 'Can be used as part of Allflame Crafting aboard The Sovereign.', NULL, '', '[]', 0, NULL, '[]', '[]', 50000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1949, 5437, 1, 1, 'open_loot_bottle', 'Right click to open.', NULL, '', '[]', 0, NULL, '[]', '[]', 0, 'Press <<xbox_button_a>> to open.', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1950, 5438, 10, 3, '', 'Can be used as part of Allflame Crafting aboard The Sovereign.', NULL, 'Transforms into a random Ducat', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1951, 5439, 10, 2, 'reroll_single_attribute_modifier', 'Can be used as part of Allflame Crafting aboard The Sovereign.', NULL, 'Changes a modifier that grants a single Attribute to grant a different Attribute', '[11741]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1952, 5440, 10, 2, 'split_to_single_explicit', 'Can be used as part of Allflame Crafting aboard The Sovereign.', NULL, 'Splits an item into many ghostly forms, each retaining a single random modifier', '[11742]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1953, 5441, 10, 2, 'reroll_deepwater_chart', 'Can be used as part of Allflame Crafting aboard The Sovereign.', NULL, 'Rerolls the room type of a Chart, potentially changing the connections it will have when Charted.', '[11743]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1954, 5442, 10, 2, 'add_deepwater_hazard_belt_mod', 'Can be used as part of Allflame Crafting aboard The Sovereign.', NULL, 'Augments a rare belt with a Trap or Mine modifier', '[11744]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1955, 5443, 10, 2, 'add_pantheon_aspect', 'Can be used as part of Allflame Crafting aboard The Sovereign.', NULL, 'Crafts a random Pantheon Aspect Skill onto an item', '[11745]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1956, 5444, 10, 2, 'add_deepwater_tattoo_enchant', 'Can be used as part of Allflame Crafting aboard The Sovereign.', NULL, 'Etches over between 1 to 3 sockets on a pair of Gloves, granting random enchantments but rendering the sockets unusable', '[11746]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1957, 5445, 10, 2, 'reset_ghostliness_or_delete', 'Can be used as part of Allflame Crafting aboard The Sovereign.', NULL, 'Randomly either removes all [Intangibility] from an item or destroys it.', '[11747]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1958, 5446, 10, 2, 'add_eldritch_implicit_amulet', 'Can be used as part of Allflame Crafting aboard The Sovereign.', NULL, 'Replaces the implicit Modifier of a non-Unique Amulet with an Eldritch implicit, provided it does not already have one.', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 1),
(1959, 5447, 3, 3, '', '', NULL, '<uniqueitem>{Progenesis}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1960, 5448, 3, 3, '', '', NULL, '<uniqueitem>{Ephemeral Edge}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0),
(1961, 5449, 2, 3, '', '', NULL, '<whiteitem>{Divine Vessel}', '[]', 0, NULL, '[]', '[]', 10000, '', 0, '[]', '[]', '[]', 0, '', 0, 0, 0);
