-- Allflame Phase 7: ship upgrade piece drop chances and extra scattered-object count for ship upgrade row 3
-- (Modules/Core/Content/Deepwater/DeepwaterUpgrades.cs, DeepwaterLoot.cs).
-- Server policy, all guesses: client data gives no drop rates for Deadcaller Bellows, Spectral Infuser or Alchemical
-- Exchange, and no exact extra scattered-object count for map_deepwater_upgrade_enable_scatter_objects.
INSERT INTO system_settings (key, value) VALUES
('deepwater_upgrade_piece1_drop_chance', 35),
('deepwater_upgrade_piece3_drop_chance', 25),
('deepwater_upgrade_piece4_drop_chance', 25),
('deepwater_scatter_upgrade_extra_min', 1),
('deepwater_scatter_upgrade_extra_max', 2)
ON CONFLICT(key) DO NOTHING;
