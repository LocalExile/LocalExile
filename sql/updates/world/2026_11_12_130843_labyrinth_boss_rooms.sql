-- The three Labyrinth boss rooms the level snapshot leaves out but the client plays.
--
-- A room the snapshot drops is never built, so its boss is never placed: Izaro's three arenas came out empty.
-- Each of the three was left out for a reason the port does not reproduce:
--   1_Labyrinth_boss_1 / 1_Labyrinth_boss_2  one logged load the port could not build ("oracle <area> <seed>"
--                                            all 0/-1), while every captured seed of the room matches.
--   2_Labyrinth_boss_2_end                   two transition objects more than its capture ("Objects: 2").
-- Generated at seed 1 and at three random seeds, each room builds its layout and places Izaro three times, which is
-- what WorldAreas.json lists for the room (the three varieties all named Izaro).
INSERT INTO server_level_areas (area_id, generated, seed_in_place_of_one) VALUES
('1_Labyrinth_boss_1', 1, NULL),
('1_Labyrinth_boss_2', 1, NULL),
('2_Labyrinth_boss_2_end', 1, NULL);
