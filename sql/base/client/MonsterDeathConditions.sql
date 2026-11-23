-- Client table MonsterDeathConditions (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "MonsterDeathConditions" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "BuffDefinition" JSON,
    "__col2" BOOLEAN,
    "__col3" INTEGER,
    "Stat" JSON,
    "__col5" BOOLEAN,
    "__col6" INTEGER,
    "Rarity" INTEGER,
    "__col8" BOOLEAN,
    "Mods" JSON,
    "__col10" INTEGER,
    "Animation" JSON,
    "__col12" INTEGER,
    "ActiveSkill1" INTEGER,
    "ActiveSkill2" INTEGER,
    "__col15" INTEGER,
    "__col16" INTEGER
);
