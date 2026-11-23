-- Client table BestiaryNets (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BestiaryNets" (
    "_row" INTEGER PRIMARY KEY,
    "BaseItemTypesKey" INTEGER,
    "__col1" INTEGER,
    "CaptureMinLevel" INTEGER,
    "CaptureMaxLevel" INTEGER,
    "DropMinLevel" INTEGER,
    "DropMaxLevel" INTEGER,
    "__col6" INTEGER,
    "IsEnabled" BOOLEAN
);
