-- Client table DelveBiomes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "DelveBiomes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "WorldAreasKeys" JSON,
    "UIImage" TEXT,
    "SpawnWeight_Depth" JSON,
    "SpawnWeight_Values" JSON,
    "__col6" JSON,
    "__col7" JSON,
    "Art2D" TEXT,
    "AchievementItemsKeys" JSON,
    "__col10" BOOLEAN,
    "__col11" JSON
);
