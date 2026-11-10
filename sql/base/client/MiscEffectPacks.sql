-- Client table MiscEffectPacks (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MiscEffectPacks" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "EPKFile" TEXT,
    "__col2" INTEGER,
    "__col3" INTEGER,
    "__col4" INTEGER,
    "PreloadGroups" JSON,
    "__col6" BOOLEAN,
    "PlayerOnly_EPKFile" TEXT,
    "__col8" BOOLEAN
);
