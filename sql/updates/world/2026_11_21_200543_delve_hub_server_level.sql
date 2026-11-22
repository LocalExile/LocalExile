-- Enable server-side level generation for the Azurite Mine Encampment (Delve_Hub), which already matches
-- scripts/re/levelgen/gate-baseline.json (0 walk, projectile, height and object diffs), so players arrive at its
-- room default / waypoint instead of the unwalkable fallback cell (197, 261).
INSERT INTO server_level_areas (area_id, generated, seed_in_place_of_one)
VALUES ('Delve_Hub', 1, NULL)
ON CONFLICT(area_id) DO UPDATE SET generated = 1, seed_in_place_of_one = NULL;
