-- Client table BetrayalTargets (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "BetrayalTargets" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "BetrayalRanksKey" INTEGER,
    "MonsterVarietiesKey" INTEGER,
    "BetrayalJobsKey" INTEGER,
    "Art" TEXT,
    "__col5" BOOLEAN,
    "ItemClasses" INTEGER,
    "FullName" TEXT,
    "Safehouse_ARMFile" TEXT,
    "ShortName" TEXT,
    "__col10" INTEGER,
    "SafehouseLeader_AchievementItemsKey" JSON,
    "Level3_AchievementItemsKey" JSON,
    "__col13" INTEGER,
    "__col14" INTEGER,
    "__col15" INTEGER,
    "UltimatumAchievementItemsKey" INTEGER,
    "ScriptArgument" TEXT,
    "__col18" JSON,
    "__col19" JSON
);
