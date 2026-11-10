-- Client table InvasionMonstersPerArea (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "InvasionMonstersPerArea" (
    "_row" INTEGER PRIMARY KEY,
    "WorldAreasKey" INTEGER,
    "__col1" INTEGER,
    "__col2" JSON,
    "MonsterVarietiesKeys1" JSON,
    "MonsterVarietiesKeys2" JSON,
    "__col5" INTEGER,
    "__col6" INTEGER,
    "__col7" INTEGER,
    "__col8" INTEGER
);
