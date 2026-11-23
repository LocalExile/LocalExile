-- Client table VillageJobTypes: all 10 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "VillageJobTypes" ("_row", "Id", "Icon", "StateText", "MaxWorkersStat", "__col4", "JobSpeedStat") VALUES
(0, 'Idling', '', 'Idling', 20728, 0, NULL),
(1, 'Mining', 'Art/2DArt/UIImages/InGame/Village/JobIcons/MiningJobIcon', 'Mining', 20713, 1, 20695),
(2, 'Smelting', 'Art/2DArt/UIImages/InGame/Village/JobIcons/SmithingJobIcon', 'Smelting', 20715, 1, 20696),
(3, 'Farming', 'Art/2DArt/UIImages/InGame/Village/JobIcons/FarmingJobIcon', 'Farming', 20720, 0, 20698),
(4, 'Disenchanting', 'Art/2DArt/UIImages/InGame/Village/JobIcons/DisenchantingJobIcon', 'Disenchanting', 20717, 1, 20697),
(5, 'Mapping', 'Art/2DArt/UIImages/InGame/Village/JobIcons/MapRunningJobIcon', 'Mapping', 20726, 0, 20699),
(6, 'Shipping', 'Art/2DArt/UIImages/InGame/Village/JobIcons/ShippingJobIcon', 'Shipping', 20723, 0, NULL),
(7, 'Fishing', 'Art/2DArt/UIImages/InGame/Village/JobIcons/FishingJobIcon', 'Fishing', 20727, 1, 20701),
(8, 'LostAtSea', '', 'Lost at Sea', NULL, 0, NULL),
(9, 'LostMapping', '', 'Trapped in the Atlas', NULL, 0, NULL);
