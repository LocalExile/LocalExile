-- Client table HideoutDoodads (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "HideoutDoodads" (
    "_row" INTEGER PRIMARY KEY,
    "MtxTypeGameSpecific" INTEGER,
    "Variation_AOFiles" JSON,
    "IsNonMasterDoodad" BOOLEAN,
    "InheritsFrom" TEXT,
    "__col4" BOOLEAN,
    "IsCraftingBench" BOOLEAN,
    "Tags" JSON,
    "__col7" BOOLEAN,
    "Hideout" INTEGER,
    "Category" INTEGER,
    "__col10" INTEGER,
    "__col11" BOOLEAN,
    "AchievementReward" INTEGER,
    "__col13" BOOLEAN,
    "HideoutSpecial" INTEGER,
    "HideoutItems" JSON,
    "HideoutItemLink" INTEGER,
    "__col17" BOOLEAN,
    "__col18" BOOLEAN,
    "__col19" BOOLEAN
);
