-- Population and reward weights are server policy; GGG does not publish the encounter's exact drop/spawn weights.
INSERT INTO system_settings (key, value) VALUES
('simulacrum_wave_monsters', 80),
('simulacrum_monsters_per_wave', 12),
('simulacrum_boss_chance_pct', 25),
('simulacrum_unique_chance_pct', 25)
ON CONFLICT(key) DO NOTHING;
