-- Client table IncursionRoomBossFightEvents: all 9 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "IncursionRoomBossFightEvents" ("_row", "Room", "__col1", "__col2", "__col3", "__col4", "__col5", "TurretMonster") VALUES
(0, 32, '', 'StartMonsterSpawnersOfType( "Metadata/Monsters/LeagueIncursion/VaalSaucerRoomSpiderSpawner1", 200, 1 );', '', '', '', NULL),
(1, 54, '', 'incursion_golems.ModifyState( activated, 1 ); StartMonsterSpawnersOfType( "Metadata/Monsters/LeagueIncursion/VaalSaucerRoomSpiderSpawner1", 200, 1 );', '', '', '', NULL),
(2, 76, '', 'incursion_golems.ModifyState( activated, 1 ); StartMonsterSpawnersOfType( "Metadata/Monsters/LeagueIncursion/VaalSaucerRoomSpiderSpawner2", 200, 1 );', '', '', '', NULL),
(3, 33, '', 'room_turret1.ModifyState( fight, 1 );', '', '', '', NULL),
(4, 55, 'turrets.AddBuff( omnitect_turret_damage_buff, infinity, 0, 10 );', 'room_turret1.ModifyState( fight, 1 );', '', '', '', 6715),
(5, 77, 'turrets.AddBuff( omnitect_turret_damage_buff, infinity, 0, 10 );', 'room_turret1.ModifyState( fight, 1 ); room_turret_upgrade1.ModifyState( fight, 1 );', '', '', '', 6715),
(6, 34, 'initiator.Transition();', '', '', '', '', NULL),
(7, 56, 'initiator.Transition(); turrets.AddBuff( omnitect_turret_damage_buff, infinity, 10, 0 );', '', '', '', '', 6719),
(8, 78, 'initiator.Transition(); initiator2.Transition(); turrets.AddBuff( omnitect_turret_damage_buff, infinity, 10, 0 );', '', '', '', '', 6719);
