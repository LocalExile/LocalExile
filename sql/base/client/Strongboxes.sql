-- Client table Strongboxes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "Strongboxes" (
    "_row" INTEGER PRIMARY KEY,
    "ChestsKey" INTEGER,
    "SpawnWeight" INTEGER,
    "__col2" INTEGER,
    "__col3" BOOLEAN,
    "__col4" BOOLEAN,
    "SpawnWeightIncrease" INTEGER,
    "SpawnWeightHardmode" INTEGER,
    "__col7" BOOLEAN,
    "__col8" BOOLEAN
);
