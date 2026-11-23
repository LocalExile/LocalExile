-- Client table MapFragmentMods (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MapFragmentMods" (
    "_row" INTEGER PRIMARY KEY,
    "BaseItemTypesKey" INTEGER,
    "ModsKeys" JSON,
    "AchievementItemsKey" JSON,
    "MapFragmentFamilies" INTEGER,
    "__col4" BOOLEAN,
    "__col5" BOOLEAN,
    "__col6" BOOLEAN,
    "__col7" BOOLEAN,
    "__col8" BOOLEAN,
    "__col9" BOOLEAN,
    "MinAreaLevel" INTEGER,
    "__col11" INTEGER,
    "MapFragmentLimit" INTEGER,
    "__col13" BOOLEAN,
    "__col14" JSON
);
