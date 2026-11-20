-- The Allflame encounter areas (DeepwaterPlugin, re/011 §3c4): the client's tile hash equals the port's for a sent
-- chart and Voyage grid (3.29.3d, 2026-11-18), and a live capture of each (2026-11-19) matches the port's walk and
-- projectile grids, heights and doodads.
INSERT INTO server_level_areas (area_id, generated, seed_in_place_of_one) VALUES
  ('DeepwaterEncounter', 1, NULL), ('DeepwaterEncounterVoyage', 1, NULL);
