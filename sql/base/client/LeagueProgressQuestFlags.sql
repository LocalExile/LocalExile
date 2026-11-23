-- Client table LeagueProgressQuestFlags (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "LeagueProgressQuestFlags" (
    "_row" INTEGER PRIMARY KEY,
    "QuestFlag" INTEGER,
    "CompletionString" INTEGER,
    "Boss" TEXT,
    "__col3" BOOLEAN
);
