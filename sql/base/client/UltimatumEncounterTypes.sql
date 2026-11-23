-- Client table UltimatumEncounterTypes (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "UltimatumEncounterTypes" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "ProgressBarText" TEXT,
    "__col3" BOOLEAN,
    "__col4" BOOLEAN,
    "NormalAchievements" JSON,
    "InscribedAchievement" INTEGER,
    "EncounterChanceStat" INTEGER
);
