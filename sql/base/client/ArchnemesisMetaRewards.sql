-- Client table ArchnemesisMetaRewards (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ArchnemesisMetaRewards" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "RewardText" TEXT,
    "RewardGroup" INTEGER,
    "ScriptArgument" TEXT,
    "MinLevel" INTEGER,
    "MaxLevel" INTEGER
);
