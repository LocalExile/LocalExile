-- Client table WarbandsPackMonsters (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "WarbandsPackMonsters" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "__col1" INTEGER,
    "__col2" BOOLEAN,
    "__col3" BOOLEAN,
    "__col4" BOOLEAN,
    "__col5" BOOLEAN,
    "__col6" INTEGER,
    "Tier4_MonsterVarietiesKeys" JSON,
    "Tier3_MonsterVarietiesKeys" JSON,
    "Tier2_MonsterVarietiesKeys" JSON,
    "Tier1_MonsterVarietiesKeys" JSON,
    "Tier1Name" TEXT,
    "Tier2Name" TEXT,
    "Tier3Name" TEXT,
    "Tier4Name" TEXT,
    "Tier1Art" TEXT,
    "Tier2Art" TEXT,
    "Tier3Art" TEXT,
    "Tier4Art" TEXT
);
