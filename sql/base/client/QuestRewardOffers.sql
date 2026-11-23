-- Client table QuestRewardOffers (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "QuestRewardOffers" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "QuestKey" INTEGER,
    "QuestFlag" INTEGER,
    "__col3" INTEGER,
    "RewardWindowTake" INTEGER,
    "__col5" BOOLEAN,
    "__col6" BOOLEAN,
    "RewardWindowTitle" INTEGER,
    "__col8" BOOLEAN,
    "__col9" BOOLEAN,
    "__col10" BOOLEAN,
    "__col11" BOOLEAN,
    "__col12" JSON
);
