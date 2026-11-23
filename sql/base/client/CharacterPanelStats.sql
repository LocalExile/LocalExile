-- Client table CharacterPanelStats (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "CharacterPanelStats" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Text" TEXT,
    "StatsKeys1" JSON,
    "CharacterPanelDescriptionModesKey" INTEGER,
    "StatsKeys2" JSON,
    "StatsKeys3" JSON,
    "CharacterPanelTabsKey" INTEGER,
    "__col7" BOOLEAN,
    "__col8" JSON,
    "__col9" INTEGER,
    "__col10" BOOLEAN
);
