-- Client table QuestRewards (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "QuestRewards" (
    "_row" INTEGER PRIMARY KEY,
    "RewardOffer" INTEGER,
    "__col1" INTEGER,
    "Characters" JSON,
    "Reward" INTEGER,
    "RewardLevel" INTEGER,
    "__col5" INTEGER,
    "__col6" INTEGER,
    "__col7" TEXT,
    "__col8" JSON,
    "RewardStack" INTEGER,
    "__col10" BOOLEAN,
    "__col11" BOOLEAN,
    "__col12" JSON,
    "__col13" INTEGER,
    "__col14" INTEGER,
    "__col15" JSON,
    "__col16" INTEGER,
    "__col17" INTEGER,
    "__col18" INTEGER,
    "__col19" JSON
);
