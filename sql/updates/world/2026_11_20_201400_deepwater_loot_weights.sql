-- The Allflame run's scattered loot (Modules/Core/Content/Deepwater/DeepwaterLoot.cs). Server policy, all guesses: the
-- client data gives no Sulphur node size mix, and its Chests rows give a spawn weight (__col7) only for Sunken Loot (50),
-- which wins over the row here.
INSERT INTO system_settings (key, value) VALUES
('deepwater_sulphur_node_share_small', 40),
('deepwater_sulphur_node_share_base', 35),
('deepwater_sulphur_node_share_large', 20),
('deepwater_sulphur_node_share_huge', 5),
('deepwater_weight_sunken_loot', 50),
('deepwater_weight_gold_pile', 40),
('deepwater_weight_treasure_clam', 15),
('deepwater_weight_message_in_a_bottle', 20),
('deepwater_weight_golden_lantern', 5),
('deepwater_weight_soothing_coral', 15),
('deepwater_weight_allflame_kindling', 10)
ON CONFLICT(key) DO NOTHING;
