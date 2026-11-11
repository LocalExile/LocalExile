-- Client table AlternateSkillTargetingBehaviours: all 8 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "AlternateSkillTargetingBehaviours" ("_row", "Id", "MaxDistance", "ClientStrings", "MaxMineTargetingDistance", "MaxTrapTargetingDistance", "MaxTotemTargetingDistance", "TargetingFlags") VALUES
(0, 'ConsoleMovementMaxDistance', 80, 4621, 0, 0, 0, '[0]'),
(1, 'ConsoleFiftyUnitsDistance', 50, 4621, 0, 0, 0, '[0]'),
(2, 'ConsoleAtFeet', 5, 4621, 5, 5, 5, '[0,3]'),
(3, 'ConsoleAtFeetNormalSelfcast', 0, 4621, 5, 5, 5, '[0,3]'),
(4, 'ConsoleQuickDodge', 80, 4621, 0, 0, 0, '[1]'),
(5, 'ConsoleFiftyUnitsDistanceRespectTarget', 50, 4621, 0, 0, 0, '[0,2]'),
(6, 'ConsoleForceLocationMaxDistance', 80, 4621, 0, 0, 0, '[3,0]'),
(7, 'ConsoleFiftyFiveUnitsDistanceRespectTarget', 55, 4621, 0, 0, 0, '[0,2]');
