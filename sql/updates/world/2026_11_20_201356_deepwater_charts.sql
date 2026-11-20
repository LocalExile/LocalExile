-- Allflame Charts as items (Modules/Core/Content/Deepwater/DeepwaterCharts.cs). Server policy, all guesses: the client
-- data gives no chart rarity. The percent chances that a dropped chart is magic or rare (the rest are normal), and whether
-- magic and rare charts drop unidentified (1) as maps do.
INSERT INTO system_settings (key, value) VALUES
('deepwater_chart_magic_percent', 30),
('deepwater_chart_rare_percent', 10),
('deepwater_chart_unidentified', 1)
ON CONFLICT(key) DO NOTHING;
