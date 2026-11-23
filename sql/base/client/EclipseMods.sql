-- Client table EclipseMods (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "EclipseMods" (
    "_row" INTEGER PRIMARY KEY,
    "Key" TEXT,
    "SpawnWeight_TagsKeys" JSON,
    "SpawnWeight_Values" JSON,
    "ModsKey" INTEGER,
    "MinLevel" INTEGER,
    "MaxLevel" INTEGER,
    "IsPrefix" BOOLEAN
);
