-- Breach (Modules/Core/Content/Breach): 1 (the measured official behaviour) makes a touched Breach enrage when its
-- radius ramp runs out, about 34 s after the touch, and close a second later; 0 closes it 60 s after the touch.
INSERT INTO system_settings (key, value) VALUES
('breach_enrage', 1)
ON CONFLICT(key) DO NOTHING;
