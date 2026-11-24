-- Onslaught on kill as effect rules: the four stats KillOnslaught.From read in code (OnslaughtOnKill.cs), with the
-- same chances and durations. Each rolls on its own and a gain never shortens the Onslaught running.
INSERT INTO effect_rules (stat, ord, rule, reviewed, note) VALUES
('chance_to_gain_onslaught_on_kill_%', 0,
 '{"when":"kill","chance":{"from":"stat"},"do":[{"kind":"onslaught","seconds":{"stat":"onslaught_time_granted_on_kill_ms","scale":0.001,"fallback":4}}]}',
 1, '% chance to gain Onslaught for 4 seconds on Kill; onslaught_time_granted_on_kill_ms replaces the 4 seconds'),
('onslaught_buff_duration_on_kill_ms', 0,
 '{"when":"kill","do":[{"kind":"onslaught","seconds":{"from":"stat","scale":0.001}}]}',
 1, 'You gain Onslaught for X seconds on Kill'),
('chance_to_gain_onslaught_on_kill_for_4_seconds_%', 0,
 '{"when":"kill","chance":{"from":"stat"},"do":[{"kind":"onslaught","seconds":{"fixed":4}}]}',
 1, '% chance to gain Onslaught for 4 seconds on Kill'),
('chance_to_gain_onslaught_on_kill_for_10_seconds_%', 0,
 '{"when":"kill","chance":{"from":"stat"},"do":[{"kind":"onslaught","seconds":{"fixed":10}}]}',
 1, '% chance to gain Onslaught for 10 seconds on Kill');
