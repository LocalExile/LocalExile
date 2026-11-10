-- Client table HellscapeAreaPacks (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HellscapeAreaPacks" (
    "_row" INTEGER PRIMARY KEY,
    "WorldArea" INTEGER,
    "MonsterPacks" JSON,
    "__col2" INTEGER,
    "__col3" INTEGER
);
