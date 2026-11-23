-- Bounded, paced encounter population. Counts and pacing are server policy, not published GGG constants.
INSERT INTO system_settings (key, value) VALUES
('simulacrum_pack_size', 15),
('simulacrum_pack_interval_ms', 750),
('simulacrum_active_limit', 100)
ON CONFLICT(key) DO NOTHING;
