-- Client table DeepwaterRooms (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "DeepwaterRooms" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "ArmFiles" TEXT,
    "__col2" INTEGER,
    "Name" TEXT,
    "__col4" INTEGER,
    "__col5" INTEGER,
    "__col6" INTEGER,
    "Biome" INTEGER,
    "__col8" INTEGER,
    "__col9" JSON,
    "__col10" INTEGER,
    "__col11" INTEGER,
    "Stats" JSON,
    "StatValues" JSON,
    "__col14" INTEGER,
    "Special" BOOLEAN,
    "AchievementItems" JSON,
    "__col17" BOOLEAN
);
