-- The Normal Labyrinth rooms the level snapshot leaves out but the client plays.
--
-- Each has a logged load the port does not match ("oracle <area> <seed>" mismatch), and one mismatch left the room out:
-- those loads were rooms of a day's Labyrinth, which the client builds with the day's node (the Gateway's AreaInstance
-- names it), and the oracle runs without one. 1_Labyrinth_OH_straight at seed 820631882 (day 20770, node 1): the client's
-- tile hash 2944466374, the port's with the node 2944466374, without it 1506586991. Every capture of the five matches.
-- ServerLevelAreas no longer counts a Labyrinth room's oracle against it.
INSERT INTO server_level_areas (area_id, generated, seed_in_place_of_one) VALUES
('1_Labyrinth_C_straight', 1, NULL),
('1_Labyrinth_EH_block_branch', 1, NULL),
('1_Labyrinth_OH_branch', 1, NULL),
('1_Labyrinth_OH_straight', 1, NULL),
('1_Labyrinth_P_branch_bottleneck_2', 1, NULL);
