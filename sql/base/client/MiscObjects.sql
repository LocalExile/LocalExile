-- Client table MiscObjects (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MiscObjects" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "EffectVirtualPath" TEXT,
    "PreloadGroupsKeys" JSON,
    "EffectHASH32" INTEGER,
    "__col4" INTEGER,
    "ObjectHASH32" INTEGER
);
