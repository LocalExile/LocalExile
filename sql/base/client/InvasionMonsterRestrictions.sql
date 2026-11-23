-- Client table InvasionMonsterRestrictions (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "InvasionMonsterRestrictions" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "WorldAreasKey" INTEGER,
    "MonsterVarietiesKeys" JSON,
    "__col3" JSON
);
