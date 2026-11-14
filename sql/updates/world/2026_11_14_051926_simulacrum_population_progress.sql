-- Calibrated to observed runs (~6900-7200 kills across 15 waves); NOT an official GGG progress formula.
-- Population varies with the rarity mix and bosses, independently of the simultaneous living-monster cap.
INSERT INTO system_settings (key, value) VALUES
('simulacrum_population_scale_pct', 100),
('simulacrum_progress_normal', 1),
('simulacrum_progress_magic', 2),
('simulacrum_progress_rare', 4),
('simulacrum_progress_unique', 25)
ON CONFLICT(key) DO NOTHING;
