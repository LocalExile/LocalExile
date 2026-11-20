-- Allflame Phase 6: Ducat chests/rooms, Cursed Ducat drops & minibosses, and Message in a Bottle outcome weights
-- (Modules/Core/Content/Deepwater/DeepwaterLoot.cs, DeepwaterMonsters.cs, DeepwaterCursedDucats.cs, DeepwaterBottles.cs).
-- Server policy, all guesses: client data gives no spawn weights for Pirate's Locker or Offering of the Drowned, no
-- miniboss/rare drop rates, and no weights for DeepwaterLootBottles rows.
INSERT INTO system_settings (key, value) VALUES
('deepwater_weight_pirates_locker', 8),
('deepwater_weight_offering_of_the_drowned', 5),
('deepwater_room_ducat_chance', 15),
('deepwater_sunken_ducat_chance', 4),
('deepwater_miniboss_chance', 15),
('deepwater_cursed_ducat_miniboss_chance', 100),
('deepwater_cursed_ducat_rare_chance', 25),
('deepwater_bottle_weight_BottledUniqueBelt', 1500),
('deepwater_bottle_weight_BottledUniqueJewel', 1500),
('deepwater_bottle_weight_BottledUniqueRing', 2000),
('deepwater_bottle_weight_BottledUniqueAmulet', 1500),
('deepwater_bottle_weight_BottledUniqueFlask', 1200),
('deepwater_bottle_weight_BottledHinekorasLock', 5),
('deepwater_bottle_weight_BottledFracturingOrb', 100),
('deepwater_bottle_weight_BottledDivineOrb', 500),
('deepwater_bottle_weight_BottledDivineOrbStack', 50),
('deepwater_bottle_weight_BottledReflectingMist', 100),
('deepwater_bottle_weight_BottledStackedDeckStack', 1400),
('deepwater_bottle_weight_BottledBrokenMirror', 4),
('deepwater_bottle_weight_BottledAlbinoCrabClaw', 4),
('deepwater_bottle_weight_BottledChainHookOfAngling', 5)
ON CONFLICT(key) DO NOTHING;
