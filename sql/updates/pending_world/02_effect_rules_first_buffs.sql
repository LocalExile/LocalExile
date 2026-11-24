-- The first rows that use the shared timed buff and a check: two unique effects the server did not have.
-- Death Rush: "You gain Adrenaline for X seconds on Kill" (the client's SentinelAdrenaline template gives its numbers).
-- Kongor's Undying Rage: "You gain Onslaught for X seconds on Critical Strike".
INSERT INTO effect_rules (stat, ord, rule, reviewed, note) VALUES
('gain_adrenaline_for_X_seconds_on_kill', 0,
 '{"when":"kill","do":[{"kind":"buff","template":"SentinelAdrenaline","seconds":{"from":"stat"}}]}',
 1, 'You gain Adrenaline for X seconds on Kill (Death Rush)'),
('onslaught_on_crit_duration_ms', 0,
 '{"when":"hit","checks":["critical"],"do":[{"kind":"onslaught","seconds":{"from":"stat","scale":0.001}}]}',
 1, 'You gain Onslaught for X seconds on Critical Strike (Kongor''s Undying Rage)');
