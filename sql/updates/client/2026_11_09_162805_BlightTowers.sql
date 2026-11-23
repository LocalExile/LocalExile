-- Client table BlightTowers: all 31 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "BlightTowers" ("_row", "Id", "Name", "Description", "Icon", "NextUpgradeOptions", "__col5", "Tier", "Radius", "__col8", "SpendResourceAchievement", "StatsKey", "StatsKeys", "StatsKeys2", "__col13") VALUES
(0, 'EmptyNode', 'Tower Foundation', 'A powered base for one of Cassia''s towers.', 'Art/2DArt/UIImages/InGame/Placeholder/temp_league_prototype_placeholder', '[1,6,11,16,21,26]', 1487001455, '', 0, -1, NULL, NULL, '[]', '[]', 0),
(1, 'ChillingTower1', 'Chilling Tower Mk I', 'Damage: Low' || char(13) || '
Chills and Damages nearby enemies' || char(13) || '
' || char(13) || '
Ineffective against Blighted Monsters of the same type', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconCold', '[2]', -1090896293, 'I', 35, 3, NULL, 10578, '[10577,10579,10580,10581,12350,17747]', '[24,3622,201,10581,12355,17746]', 0),
(2, 'ChillingTower2', 'Chilling Tower Mk II', 'Increased Damage' || char(13) || '
Increased effect of Chill' || char(13) || '
Affects more Targets', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconCold', '[3]', 1371628040, 'II', 35, 3, NULL, 10578, '[10577,10579,10580,10581,12350,17747]', '[24,3622,201,10581,12355,17746]', 0),
(3, 'ChillingTower3', 'Chilling Tower Mk III', 'Increased Damage' || char(13) || '
Increased effect of Chill' || char(13) || '
Affects more targets', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconCold', '[4,5]', -48389433, 'III', 35, 3, NULL, 10578, '[10577,10579,10580,10581,12350,17747]', '[24,3622,201,10581,12355,17746]', 0),
(4, 'FreezingTower', 'Freezebolt Tower', 'Damage: Very High' || char(13) || '
Shoots a Piercing projectile which Freezes enemies' || char(13) || '
' || char(13) || '
Ineffective against Blighted Monsters of the same type', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconFreezing', '[]', -632665268, 'IV', 75, 4, NULL, 10583, '[10582,10584,10585,12339,12350,17747]', '[24,10584,175,8816,12355,17746]', 0),
(5, 'IcePrisonTower', 'Glacial Cage Tower', 'Deals no Damage' || char(13) || '
Imprisons Enemies' || char(13) || '
' || char(13) || '
Ineffective against Blighted Monsters of the same type', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconIcePrison', '[]', 1141601187, 'IV', 60, 5, NULL, 10587, '[10586,10588,10589,12340,12350,17747]', '[217,10588,3396,436,12355,17746]', 0),
(6, 'ShockingTower1', 'Shock Nova Tower Mk I', 'Damage: Low' || char(13) || '
Creates Shocked Ground' || char(13) || '
Casts Shock Nova' || char(13) || '
' || char(13) || '
Ineffective against Blighted Monsters of the same type', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconLightning', '[7]', 1459477666, 'I', 20, 6, NULL, 10568, '[10569,10590,10591,10592,10593,12350,17747]', '[24,10590,3623,10661,2320,12355,17746]', 1),
(7, 'ShockingTower2', 'Shock Nova Tower Mk II', 'Increased Damage' || char(13) || '
Increased chance to Shock' || char(13) || '
Increased Area of Effect', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconLightning', '[8]', 1368253867, 'II', 25, 6, NULL, 10568, '[10569,10590,10591,10592,10593,12350,17747]', '[24,10590,3623,10661,2320,12355,17746]', 1),
(8, 'ShockingTower3', 'Shock Nova Tower Mk III', 'Increased Damage' || char(13) || '
Increased chance to Shock' || char(13) || '
Increased Area of Effect', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconLightning', '[9,10]', 94056515, 'III', 30, 6, NULL, 10568, '[10569,10590,10591,10592,10593,12350,17747]', '[24,10590,3623,10661,2320,12355,17746]', 1),
(9, 'LightningStormTower', 'Lightning Storm Tower', 'Damage: High' || char(13) || '
Lightning rapidly strikes around the Tower' || char(13) || '
' || char(13) || '
Ineffective against Blighted Monsters of the same type', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconLightningStorm', '[]', 1277124932, 'IV', 45, 7, NULL, 10595, '[10594,10654,10597,12341,12350,17747]', '[24,10596,214,12341,12355,17746]', 1),
(10, 'ArcingTower', 'Arc Tower', 'Damage: Medium' || char(13) || '
Casts Arc' || char(13) || '
' || char(13) || '
Ineffective against Blighted Monsters of the same type', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconArcing', '[]', -1027797953, 'IV', 45, 8, NULL, 10599, '[10598,10600,10601,12342,12350,17747]', '[24,2320,174,8817,12355,17746]', 0),
(11, 'BuffTower1', 'Empowering Tower Mk I', 'Improves the effectiveness of other Towers' || char(13) || '
Does not Damage Enemies', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconBuff', '[12]', -1750551302, 'I', 35, 15, NULL, 10603, '[10602,10604,10605,10606,12350,17747]', '[4602,218,10605,10606,12355,17746]', 1),
(12, 'BuffTower2', 'Empowering Tower Mk II', 'Improves the effectiveness of other Towers' || char(13) || '
Does not Damage Enemies', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconBuff', '[13]', -2021019078, 'II', 45, 15, NULL, 10603, '[10602,10604,10605,10606,12350,17747]', '[4602,218,10605,10606,12355,17746]', 1),
(13, 'BuffTower3', 'Empowering Tower Mk III', 'Improves the effectiveness of other Towers' || char(13) || '
Does not Damage Enemies', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconBuff', '[14,15]', 1692746775, 'III', 55, 15, NULL, 10603, '[10602,10604,10605,10606,12350,17747]', '[4602,218,10605,10606,12355,17746]', 1),
(14, 'BuffPlayersTower', 'Imbuing Tower', 'Improves the effectiveness of Players in range.' || char(13) || '
Does not Damage Enemies', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconBuffPlayer', '[]', -720723365, 'IV', 55, 16, NULL, 10608, '[10607,10609,10610,12343,12350,17745]', '[4602,10609,10610,12343,12355,17746]', 1),
(15, 'WeakenEnemiesTower', 'Smothering Tower', 'Weakens Enemies in range' || char(13) || '
Does not Damage Enemies', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconWeakining', '[]', 1166693452, 'IV', 55, 17, NULL, 10612, '[10611,10613,10614,12344,12350,17747]', '[4602,10613,10614,12344,12355,17746]', 1),
(16, 'StunningTower1', 'Seismic Tower Mk I', 'Damage: Low' || char(13) || '
Repeats 3 times' || char(13) || '
Always Stuns' || char(13) || '
Damages and Stuns nearby Enemies' || char(13) || '
' || char(13) || '
Ineffective against Blighted Monsters of the same type', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconPhysical', '[17]', -239063143, 'I', 45, 9, NULL, 10616, '[10615,10617,10618,10619,12350,17747]', '[24,342,7880,10660,12355,17746]', 0),
(17, 'StunningTower2', 'Seismic Tower Mk II', 'Repeats 5 times', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconPhysical', '[18]', 242248516, 'II', 45, 9, NULL, 10616, '[10615,10617,10618,10619,12350,17747]', '[24,342,7880,10660,12355,17746]', 0),
(18, 'StunningTower3', 'Seismic Tower Mk III', 'Repeats 7 times', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconPhysical', '[19,20]', -854560930, 'III', 45, 9, NULL, 10616, '[10615,10617,10618,10619,12350,17747]', '[24,342,7880,10660,12355,17746]', 0),
(19, 'TemporalTower', 'Temporal Tower', 'Persistently Slows Monsters ' || char(13) || '
Does not Damage Enemies' || char(13) || '
' || char(13) || '
Ineffective against Blighted Monsters of the same type', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconTemporal', '[]', 1524497081, 'IV', 45, 10, NULL, 10621, '[10620,10622,10623,12345,12350,17747]', '[4602,10657,10658,12345,17746]', 1),
(20, 'PetrificationTower', 'Stone Gaze Tower', 'Periodically Petrifies Monsters' || char(13) || '
Does not Damage Enemies' || char(13) || '
' || char(13) || '
Ineffective against Blighted Monsters of the same type', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconPetrification', '[]', -241524287, 'IV', 45, 11, NULL, 10625, '[10624,10626,10627,12354,12350,17747]', '[217,3396,10656,12356,12355,17746]', 0),
(21, 'MinionTower1', 'Summoning Tower Mk I', 'Summons 3 Minions' || char(13) || '
Minion Damage: Low' || char(13) || '
Minion Life: Low' || char(13) || '
Can only summon Minions when previous Minions are dead' || char(13) || '
' || char(13) || '
Ineffective against Blighted Monsters of the same type', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconMinion', '[22]', 93106736, 'I', 30, 12, NULL, 10629, '[10628,10630,10631,10632,10629,12350,17747]', '[490,619,161,10655,11833,12355,17746]', 0),
(22, 'MinionTower2', 'Summoning Tower Mk II', 'Summons 5 Minions', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconMinion', '[23]', 499014349, 'II', 30, 12, NULL, 10629, '[10628,10630,10631,10632,10629,12350,17747]', '[490,619,161,10655,11833,12355,17746]', 0),
(23, 'MinionTower3', 'Summoning Tower Mk III', 'Summons 7 Minions', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconMinion', '[24,25]', 1389469754, 'III', 30, 12, NULL, 10629, '[10628,10630,10631,10632,10629,12350,17747]', '[490,619,161,10655,11833,12355,17746]', 0),
(24, 'FlyingMinionTower', 'Scout Tower', 'Summons 10 Flying Minions' || char(13) || '
Minion Damage: High' || char(13) || '
Minion Life: Low' || char(13) || '
Can only summon Minions when previous Minions are dead' || char(13) || '
' || char(13) || '
Ineffective against Blighted Monsters of the same type', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconFlyingMinion', '[]', 1443410162, 'IV', 75, 13, NULL, 10634, '[10633,10635,10636,10634,12346,12350,13409,17747]', '[490,619,161,11833,12352,12355,10655,17746]', 0),
(25, 'TankyMinionTower', 'Sentinel Tower', 'Summons 1 Durable Minion' || char(13) || '
Minion Damage: Low' || char(13) || '
Minion Life: High' || char(13) || '
Can only summon Minions when previous Minions are dead' || char(13) || '
' || char(13) || '
Ineffective against Blighted Monsters of the same type', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconTankyMinion', '[]', 467940328, 'IV', 45, 14, NULL, 10638, '[10637,10639,10640,10638,12347,12350,17747]', '[490,619,161,11833,3172,12355,17746]', 0),
(26, 'FlameTower1', 'Fireball Tower Mk I', 'Damage: Medium' || char(13) || '
Casts a Fireball' || char(13) || '
' || char(13) || '
Ineffective against Blighted Monsters of the same type', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconFire', '[27]', 172162436, 'I', 45, 0, NULL, 10642, '[10641,10643,10644,10645,12350,17747]', '[24,218,175,1816,12355,17746]', 0),
(27, 'FlameTower2', 'Fireball Tower Mk II', 'Casts 3 Fireballs', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconFire', '[28]', 1399853640, 'II', 60, 0, NULL, 10642, '[10641,10643,10644,10645,12350,17747]', '[24,218,175,1816,12355,17746]', 0),
(28, 'FlameTower3', 'Fireball Tower Mk III', 'Casts 5 Fireballs', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconFire', '[29,30]', 155134479, 'III', 75, 0, NULL, 10642, '[10641,10643,10644,10645,12350,17747]', '[24,218,175,1816,12355,17746]', 0),
(29, 'FlamethrowerTower', 'Flamethrower Tower', 'Damage: High' || char(13) || '
Casts a stream of Fire' || char(13) || '
' || char(13) || '
Ineffective against Blighted Monsters of the same type', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconFlamethrower', '[]', -1733718789, 'IV', 45, 1, NULL, 10647, '[10646,10648,10649,12348,12350,17747]', '[24,10648,218,8815,12355,17746]', 1),
(30, 'MeteorTower', 'Meteor Tower', 'Damage: Very High' || char(13) || '
Rains Meteors from the sky' || char(13) || '
' || char(13) || '
Ineffective against Blighted Monsters of the same type', 'Art/2DArt/UIImages/InGame/Blight/Tower Icons/IconMeteor', '[]', -741826316, 'IV', 100, 2, NULL, 10651, '[10650,10652,10653,12349,12350,17747]', '[24,199,10659,1864,12355,17746]', 0);
