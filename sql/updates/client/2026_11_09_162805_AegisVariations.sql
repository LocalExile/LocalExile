-- Client table AegisVariations: all 6 rows of the 3.29.3c client. Written by scripts/dev/client-sql.ps1.
INSERT INTO "AegisVariations" ("_row", "Name", "DefendsAgainstPhysical", "DefendsAgainstFire", "DefendsAgainstCold", "DefendsAgainstLightning", "DefendsAgainstChaos", "ActiveBuff", "DepletedBuff", "__col8", "AnimatedEffect", "__col10") VALUES
(0, 'Fire', 0, 1, 0, 0, 0, 956, 961, 966, 1741, '[]'),
(1, 'Cold', 0, 0, 1, 0, 0, 957, 962, 967, 1742, '[]'),
(2, 'Elemental', 0, 1, 1, 1, 0, 958, 963, 968, 1743, '[6663]'),
(3, 'Lightning', 0, 0, 0, 1, 0, 959, 964, 969, 1744, '[]'),
(4, 'Physical', 1, 0, 0, 0, 0, 960, 965, 970, 1745, '[]'),
(5, 'Primal', 0, 1, 1, 1, 0, 1702, 1703, 1704, 5088, '[]');
