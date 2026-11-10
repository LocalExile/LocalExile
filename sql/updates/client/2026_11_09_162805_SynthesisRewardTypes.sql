-- Client table SynthesisRewardTypes: all 107 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "SynthesisRewardTypes" ("_row", "Id", "Description", "ArtFile", "AchievementItemsKey") VALUES
(0, 'Currency', 'Contains 2 to 3 Synthesised Chests with Currency Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/CurrencyItemRewardIcon', NULL),
(1, 'Currency2', 'Contains 3 to 4 Synthesised Chests with Currency Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/CurrencyItemRewardIcon', NULL),
(2, 'Currency3', 'Contains 4 to 5 Synthesised Chests with Currency Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/CurrencyItemRewardIcon', NULL),
(3, 'CurrencyHigh3', 'Contains 2 to 3 Synthesised Chests with valuable Currency Items' || char(13) || '
Contains 2 Synthesised Chests with Currency Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/CurrencyItemRewardIconTier2', NULL),
(4, 'Currency4', 'Contains 5 to 6 Synthesised Chests with Currency Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/CurrencyItemRewardIcon', NULL),
(5, 'CurrencyHigh4', 'Contains 3 to 4 Synthesised Chests with valuable Currency Items' || char(13) || '
Contains 2 Synthesised Chests with Currency Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/CurrencyItemRewardIconTier2', NULL),
(6, 'CurrencyEpic4', 'Contains a Synthesised Chest with very valuable Currency Items' || char(13) || '
Contains 4 to 5 Synthesised Chests with Currency Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/CurrencyItemRewardIconTier3', NULL),
(7, 'Currency5', 'Contains 6 to 7 Synthesised Chests with Currency Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/CurrencyItemRewardIcon', NULL),
(8, 'CurrencyHigh5', 'Contains 4 to 5 Synthesised Chests with valuable Currency Items' || char(13) || '
Contains 2 Synthesised Chests with Currency Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/CurrencyItemRewardIconTier2', NULL),
(9, 'CurrencyEpic5', 'Contains a Synthesised Chest with very valuable Currency Items' || char(13) || '
Contains 5 to 6 Synthesised Chests with Currency Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/CurrencyItemRewardIconTier3', NULL),
(10, 'Uniques', 'Contains a Synthesised Chest with a Unique Item' || char(13) || '
Contains 1 to 2 Synthesised Chests with Fractured Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/UniqueItemRewardIcon', NULL),
(11, 'Uniques2', 'Contains a Synthesised Chest with a Unique Item' || char(13) || '
Contains 2 to 3 Synthesised Chests with Fractured Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/UniqueItemRewardIcon', NULL),
(12, 'Uniques3', 'Contains 2 Synthesised Chests with a Unique Item' || char(13) || '
Contains 2 to 3 Synthesised Chests with Fractured Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/UniqueItemRewardIcon', NULL),
(13, 'Uniques4', 'Contains 2 Synthesised Chests with a Unique Item' || char(13) || '
Contains 3 to 4 Synthesised Chests with Fractured Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/UniqueItemRewardIcon', NULL),
(14, 'Uniques5', 'Contains 3 Synthesised Chests with a Unique Item' || char(13) || '
Contains 3 to 4 Synthesised Chests with Fractured Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/UniqueItemRewardIcon', NULL),
(15, 'UniquesHigh5', 'Contains 1 Synthesised Chest with a valuable Unique Item' || char(13) || '
Contains 3 to 4 Synthesised Chests with a Unique Item', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/UniqueItemRewardIconTier2', NULL),
(16, 'FracturedItems3', 'Contains 2 to 3 Synthesised Chests with valuable Fractured Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/FracturedItemRewardIconTier3', NULL),
(17, 'Gems', 'Contains 1 to 2 Synthesised Chests with Gem Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/SkillGemItemRewardIcon', NULL),
(18, 'Gems2', 'Contains 2 to 3 Synthesised Chests with Gem Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/SkillGemItemRewardIcon', NULL),
(19, 'Gems3', 'Contains 3 to 4 Synthesised Chests with Gem Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/SkillGemItemRewardIcon', NULL),
(20, 'GemsHigh3', 'Contains 2 to 3 Synthesised Chests with valuable Gem Items' || char(13) || '
Contains 1 to 2 Synthesised Chests with Gem Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/SkillGemItemRewardIconTier2', NULL),
(21, 'Maps', 'Contains 1 to 2 Synthesised Chests with Map Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/MapItemRewardIcon', NULL),
(22, 'Maps2', 'Contains 2 to 3 Synthesised Chests with Map Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/MapItemRewardIcon', NULL),
(23, 'Maps3', 'Contains 3 to 4 Synthesised Chests with Map Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/MapItemRewardIcon', NULL),
(24, 'Trinkets', 'Contains 1 to 2 Synthesised Chests with Jewellery Items' || char(13) || '
Contains 1 Synthesised Chest with Fractured Jewellery Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/TrinketsItemRewardIcon', NULL),
(25, 'Trinkets2', 'Contains 2 to 3 Synthesised Chests with Jewellery Items' || char(13) || '
Contains 1 Synthesised Chest with Fractured Jewellery Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/TrinketsItemRewardIcon', NULL),
(26, 'Trinkets3', 'Contains 3 to 4 Synthesised Chests with Jewellery Items' || char(13) || '
Contains 1 Synthesised Chest with Fractured Jewellery Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/TrinketsItemRewardIcon', NULL),
(27, 'TrinketsHigh3', 'Contains 2 to 3 Synthesised Chests with valuable Jewellery Items' || char(13) || '
Contains 2 Synthesised Chests with Fractured Jewellery Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/TrinketsItemRewardIcon', NULL),
(28, 'Trinkets4', 'Contains 4 to 5 Synthesised Chests with Jewellery Items' || char(13) || '
Contains 1 Synthesised Chest with Fractured Jewellery Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/TrinketsItemRewardIcon', NULL),
(29, 'TrinketsHigh4', 'Contains 3 Synthesised Chests with valuable Jewellery Items' || char(13) || '
Contains 3 Synthesised Chests with Fractured Jewellery Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/TrinketsItemRewardIcon', NULL),
(30, 'TrinketsEpic4', 'Contains 1 Synthesised Chest with very valuable Jewellery Items' || char(13) || '
Contains 4 to 5 Synthesised Chests with Fractured Jewellery Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/TrinketsItemRewardIcon', NULL),
(31, 'Trinkets5', 'Contains 4 to 5 Synthesised Chests with Jewellery Items' || char(13) || '
Contains 2 Synthesised Chests with Fractured Jewellery Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/TrinketsItemRewardIcon', NULL),
(32, 'TrinketsHigh5', 'Contains 3 to 4 Synthesised Chests with valuable Jewellery Items' || char(13) || '
Contains 4 Synthesised Chests with Fractured Jewellery Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/TrinketsItemRewardIcon', NULL),
(33, 'TrinketsEpic5', 'Contains 1 Synthesised Chest with very valuable Jewellery Items' || char(13) || '
Contains 5 to 6 Synthesised Chests with Fractured Jewellery Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/TrinketsItemRewardIcon', NULL),
(34, 'Abyss', 'Contains 2 to 3 Synthesised Chests with Abyssal Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/AbyssItemRewardIcon', NULL),
(35, 'Abyss2', 'Contains 3 to 4 Synthesised Chests with Abyssal Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/AbyssItemRewardIcon', NULL),
(36, 'AbyssHigh2', 'Contains 3 to 4 Synthesised Chests with valuable Abyssal Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/AbyssItemRewardIconTier2', NULL),
(37, 'Armour', 'Contains 1 to 2 Synthesised Chests with Armour Items' || char(13) || '
Contains 1 Synthesised Chest with Fractured Armour Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/ArmourItemRewardIcon', NULL),
(38, 'Armour2', 'Contains 2 to 3 Synthesised Chests with Armour Items' || char(13) || '
Contains 1 Synthesised Chest with Fractured Armour Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/ArmourItemRewardIcon', NULL),
(39, 'Armour3', 'Contains 3 to 4 Synthesised Chests with Armour Items' || char(13) || '
Contains 1 Synthesised Chest with Fractured Armour Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/ArmourItemRewardIcon', NULL),
(40, 'ArmourHigh3', 'Contains 2 to 3 Synthesised Chests with valuable Armour Items' || char(13) || '
Contains 2 Synthesised Chests with Fractured Armour Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/ArmourItemRewardIcon', NULL),
(41, 'Armour4', 'Contains 4 to 5 Synthesised Chests with Armour Items' || char(13) || '
Contains 1 Synthesised Chest with Fractured Armour Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/ArmourItemRewardIcon', NULL),
(42, 'ArmourHigh4', 'Contains 3 Synthesised Chests with valuable Armour Items' || char(13) || '
Contains 3 Synthesised Chests with Fractured Armour Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/ArmourItemRewardIcon', NULL),
(43, 'ArmourEpic4', 'Contains 1 Synthesised Chest with very valuable Armour Items' || char(13) || '
Contains 2 Synthesised Chests with valuable Armour Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/ArmourItemRewardIcon', NULL),
(44, 'Armour5', 'Contains 4 to 5 Synthesised Chests with Armour Items' || char(13) || '
Contains 2 Synthesised Chests with Fractured Armour Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/ArmourItemRewardIcon', NULL),
(45, 'ArmourHigh5', 'Contains 3 to 4 Synthesised Chests with valuable Armour Items' || char(13) || '
Contains 4 Synthesised Chests with Fractured Armour Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/ArmourItemRewardIcon', NULL),
(46, 'ArmourEpic5', 'Contains 1 Synthesised Chest with very valuable Armour Items' || char(13) || '
Contains 3 Synthesised Chests with valuable Armour Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/ArmourItemRewardIcon', NULL),
(47, 'Weapons', 'Contains 1 to 2 Synthesised Chests with Weapon Items' || char(13) || '
Contains 1 Synthesised Chest with Fractured Weapon Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/WeaponsItemRewardIcon', NULL),
(48, 'Weapons2', 'Contains 2 to 3 Synthesised Chests with Weapon Items' || char(13) || '
Contains 1 Synthesised Chest with Fractured Weapon Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/WeaponsItemRewardIcon', NULL),
(49, 'Weapons3', 'Contains 3 to 4 Synthesised Chests with Weapon Items' || char(13) || '
Contains 1 Synthesised Chest with Fractured Weapon Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/WeaponsItemRewardIcon', NULL),
(50, 'WeaponsHigh3', 'Contains 2 to 3 Synthesised Chests with valuable Weapon Items' || char(13) || '
Contains 2 Synthesised Chests with Fractured Weapon Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/WeaponsItemRewardIcon', NULL),
(51, 'Weapons4', 'Contains 4 to 5 Synthesised Chests with Weapon Items' || char(13) || '
Contains 1 Synthesised Chest with Fractured Weapon Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/WeaponsItemRewardIcon', NULL),
(52, 'WeaponsHigh4', 'Contains 3 Synthesised Chests with valuable Weapon Items' || char(13) || '
Contains 3 Synthesised Chests with Fractured Weapon Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/WeaponsItemRewardIcon', NULL),
(53, 'WeaponsEpic4', 'Contains 1 Synthesised Chest with very valuable Weapon Items' || char(13) || '
Contains 2 Synthesised Chests with valuable Weapon Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/WeaponsItemRewardIcon', NULL),
(54, 'Weapons5', 'Contains 4 to 5 Synthesised Chests with Weapon Items' || char(13) || '
Contains 2 Synthesised Chests with Fractured Weapon Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/WeaponsItemRewardIcon', NULL),
(55, 'WeaponsHigh5', 'Contains 3 to 4 Synthesised Chests with valuable Weapon Items' || char(13) || '
Contains 4 Synthesised Chests with Fractured Weapon Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/WeaponsItemRewardIcon', NULL),
(56, 'WeaponsEpic5', 'Contains 1 Synthesised Chest with very valuable Weapon Items' || char(13) || '
Contains 3 Synthesised Chests with valuable Weapon Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/WeaponsItemRewardIcon', NULL),
(57, 'Perandus', 'Contains 2 to 3 Synthesised Chests with Perandus Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/PerandusItemRewardIcon', NULL),
(58, 'Perandus2', 'Contains 3 to 4 Synthesised Chests with Perandus Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/PerandusItemRewardIcon', NULL),
(59, 'Perandus3', 'Contains 4 to 5 Synthesised Chests with Perandus Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/PerandusItemRewardIcon', NULL),
(60, 'Perandus4', 'Contains 5 to 6 Synthesised Chests with Perandus Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/PerandusItemRewardIcon', NULL),
(61, 'Harbinger', 'Contains 1 Synthesised Chest with valuable Harbinger Items' || char(13) || '
Contains 1 to 2 Synthesised Chests with Harbinger Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/HarbingerItemRewardIcon', NULL),
(62, 'Harbinger2', 'Contains 1 Synthesised Chest with valuable Harbinger Items' || char(13) || '
Contains 2 to 3 Synthesised Chests with Harbinger Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/HarbingerItemRewardIcon', NULL),
(63, 'Harbinger3', 'Contains 2 Synthesised Chests with valuable Harbinger Items' || char(13) || '
Contains 2 to 3 Synthesised Chests with Harbinger Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/HarbingerItemRewardIcon', NULL),
(64, 'Breach', 'Contains 1 Synthesised Chest with valuable Breach Items' || char(13) || '
Contains 1 to 2 Synthesised Chests with Breach Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/BreachItemRewardIcon', NULL),
(65, 'Breach2', 'Contains 1 Synthesised Chest with valuable Breach Items' || char(13) || '
Contains 2 to 3 Synthesised Chests with Breach Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/BreachItemRewardIcon', NULL),
(66, 'Breach3', 'Contains 2 Synthesised Chests with valuable Breach Items' || char(13) || '
Contains 2 to 3 Synthesised Chests with Breach Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/BreachItemRewardIcon', NULL),
(67, 'Essences', 'Contains 1 to 2 Synthesised Chests with Essence Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/EssencesItemRewardIcon', NULL),
(68, 'Essences2', 'Contains 2 to 3 Synthesised Chests with Essence Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/EssencesItemRewardIcon', NULL),
(69, 'Essences3', 'Contains 3 to 4 Synthesised Chests with Essence Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/EssencesItemRewardIcon', NULL),
(70, 'EssencesHigh3', 'Contains a Synthesised Chest with valuable Essence Items' || char(13) || '
Contains 3 to 4 Synthesised Chests with Essence Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/EssencesItemRewardIconTier2', NULL),
(71, 'Essences4', 'Contains 4 to 5 Synthesised Chests with Essence Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/EssencesItemRewardIcon', NULL),
(72, 'EssencesHigh4', 'Contains 2 Synthesised Chests with valuable Essence Items' || char(13) || '
Contains 3 to 4 Synthesised Chests with Essence Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/EssencesItemRewardIconTier2', NULL),
(73, 'Fragments', 'Contains 1 to 2 Synthesised Chests with Fragment Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/FragmentsItemRewardIcon', NULL),
(74, 'Fragments2', 'Contains 2 to 3 Synthesised Chests with Fragment Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/FragmentsItemRewardIcon', NULL),
(75, 'Fragments3', 'Contains 3 to 4 Synthesised Chests with Fragment Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/FragmentsItemRewardIcon', NULL),
(76, 'FragmentsHigh3', 'Contains a Synthesised Chest with valuable Fragment Items' || char(13) || '
Contains 2 to 3 Synthesised Chests with Fragment Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/FragmentsItemRewardIconTier2', NULL),
(77, 'Scarabs', 'Contains 2 to 3 Synthesised Chests with Scarab Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/ScarabItemRewardIcon', NULL),
(78, 'Scarabs2', 'Contains 1 to 2 Synthesised Chests with valuable Scarab Items' || char(13) || '
Contains 1 to 2 Synthesised Chests with Scarab Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/ScarabItemRewardIconTier2', NULL),
(79, 'Scarabs3', 'Contains 1 to 2 Synthesised Chests with very valuable Scarab Items' || char(13) || '
Contains 1 to 2 Synthesised Chests with valuable Scarab Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/ScarabItemRewardIconTier3', NULL),
(80, 'Fossils', 'Contains 2 to 3 Synthesised Chests with Fossil Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/FossilItemRewardIcon', NULL),
(81, 'Fossils2', 'Contains 1 to 2 Synthesised Chests with valuable Fossil Items' || char(13) || '
Contains 1 to 2 Synthesised Chests with Fossil Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/FossilItemRewardIconTier2', NULL),
(82, 'Fossils3', 'Contains 1 to 2 Synthesised Chests with very valuable Fossil Items' || char(13) || '
Contains 1 to 2 Synthesised Chests with valuable Fossil Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/FossilItemRewardIconTier3', NULL),
(83, 'Prophecies', 'Contains 2 to 3 Synthesised Chests with Prophecy Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/PropheciesItemRewardIcon', NULL),
(84, 'Prophecies2', 'Contains 3 to 4 Synthesised Chests with Prophecy Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/PropheciesItemRewardIcon', NULL),
(85, 'Prophecies3', 'Contains 4 to 5 Synthesised Chests with Prophecy Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/PropheciesItemRewardIcon', NULL),
(86, 'PropheciesHigh3', 'Contains a Synthesised Chest with valuable Prophecy Items' || char(13) || '
Contains 3 to 4 Synthesised Chests with Prophecy Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/PropheciesItemRewardIconTier2', NULL),
(87, 'Prophecies4', 'Contains 5 to 6 Synthesised Chests with Prophecy Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/PropheciesItemRewardIcon', NULL),
(88, 'PropheciesHigh4', 'Contains 2 Synthesised Chests with valuable Prophecy Items' || char(13) || '
Contains 3 to 4 Synthesised Chests with Prophecy Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/PropheciesItemRewardIconTier2', NULL),
(89, 'DivinationCards', 'Contains 1 to 2 Synthesised Chests with Divination Card Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/DivinationCardsItemRewardIcon', NULL),
(90, 'DivinationCards2', 'Contains 2 to 3 Synthesised Chests with Divination Card Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/DivinationCardsItemRewardIcon', NULL),
(91, 'DivinationCards3', 'Contains 3 to 4 Synthesised Chests with Divination Card Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/DivinationCardsItemRewardIcon', NULL),
(92, 'DivinationCards4', 'Contains 4 to 5 Synthesised Chests with Divination Card Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/DivinationCardsItemRewardIcon', NULL),
(93, 'Generic', 'Contains 3 to 4 Synthesised Chests' || char(13) || '
Contains 1 Synthesised Chest with Fractured Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/GenericItemRewardIcon', NULL),
(94, 'Generic2', 'Contains 4 to 5 Synthesised Chests' || char(13) || '
Contains 1 Synthesised Chest with Fractured Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/GenericItemRewardIcon', NULL),
(95, 'Generic3', 'Contains 5 to 6 Synthesised Chests' || char(13) || '
Contains 2 Synthesised Chests with Fractured Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/GenericItemRewardIcon', NULL),
(96, 'Generic4', 'Contains 6 to 8 Synthesised Chests' || char(13) || '
Contains 2 Synthesised Chests with Fractured Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/GenericItemRewardIcon', NULL),
(97, 'Generic5', 'Contains 6 to 8 Synthesised Chests' || char(13) || '
Contains 3 Synthesised Chests with Fractured Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/GenericItemRewardIcon', NULL),
(98, 'Talismans', 'Contains 1 to 2 Synthesised Chests with Talisman Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/TalismansItemRewardIcon', NULL),
(99, 'Talismans2', 'Contains 2 to 3 Synthesised Chests with Talisman Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/TalismansItemRewardIcon', NULL),
(100, 'Talismans3', 'Contains 3 to 4 Synthesised Chests with Talisman Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/TalismansItemRewardIcon', NULL),
(101, 'Labyrinth', 'Contains 1 to 2 Synthesised Chests with Labyrinth Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/LabyrinthItemRewardIcon', NULL),
(102, 'Labyrinth2', 'Contains 2 to 3 Synthesised Chests with Labyrinth Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/LabyrinthItemRewardIcon', NULL),
(103, 'Labyrinth3', 'Contains 3 to 4 Synthesised Chests with Labyrinth Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/LabyrinthItemRewardIcon', NULL),
(104, 'Labyrinth4', 'Contains 4 to 5 Synthesised Chests with Labyrinth Items', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/LabyrinthItemRewardIcon', NULL),
(105, 'GlobalMod1', 'Contains Memory Modifier', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/GlobalModTier1', NULL),
(106, 'GlobalMod2', 'Contains multiple Memory Modifiers', 'Art/2DArt/UIImages/InGame/Synthesis/Tiles/MapIslands/GlobalModTier2', NULL);
