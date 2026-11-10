-- Client table CostTypes: all 13 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "CostTypes" ("_row", "Id", "StatsKey", "FormatText", "Divisor") VALUES
(0, 'Mana', 11890, '{0} Mana', 1),
(1, 'Life', 14181, '{0} Life', 1),
(2, 'ES', 15031, '{0} Energy Shield', 1),
(3, 'Rage', 14218, '{0} Rage', 1),
(4, 'ManaPercent', 14180, '{0}% Mana', 1),
(5, 'LifePercent', 14182, '{0}% Life', 1),
(6, 'UnreservedManaPercent', 14448, '[DNT] {0}% Unreserved Mana ', 1),
(7, 'ManaPerMinute', 14869, '{0} Mana per second', 60),
(8, 'LifePerMinute', 14870, '{0} Life per second', 60),
(9, 'ManaPercentPerMinute', 14873, '{0}% Mana per second', 60),
(10, 'LifePercentPerMinute', 14874, '{0}% Life per second', 60),
(11, 'ESPerMinute', 14942, '{0} Energy Shield per second', 60),
(12, 'ESPercentPerMinute', 14943, '{0}% Energy Shield per second', 60);
