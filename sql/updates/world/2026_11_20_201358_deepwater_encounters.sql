-- The Allflame encounters in ordinary areas: Coral Nest, Cursed Treasure, Brinerot Plunder
-- (Modules/Core/Content/Deepwater/DeepwaterEncounters.cs). Server policy, all guesses: the client data gives no trigger
-- distance, guard counts or Ducat chance. The trigger is the distance in cells from a locked chest (Cursed Treasure,
-- Brinerot Plunder) at which its guards come; the crab counts are per crab spawner (four in the room); the Brinerot extras
-- come from the bone pile besides one per barrel; the Ducat chance is per Brinerot guard, in percent. The two buff chances
-- are percent: Craiceann's Call's crab pack on a rare or unique kill, Sailor's Lantern's duplicated gold.
INSERT INTO system_settings (key, value) VALUES
('deepwater_encounter_trigger_radius', 30),
('deepwater_encounter_coral_crabs_min', 3),
('deepwater_encounter_coral_crabs_max', 5),
('deepwater_encounter_cursed_min', 6),
('deepwater_encounter_cursed_max', 10),
('deepwater_encounter_brinerot_extra_min', 4),
('deepwater_encounter_brinerot_extra_max', 6),
('deepwater_encounter_ducat_chance', 3),
('deepwater_craiceann_chance', 100),
('deepwater_sailors_lantern_chance', 25)
ON CONFLICT(key) DO NOTHING;
