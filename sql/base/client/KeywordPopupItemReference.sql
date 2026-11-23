-- Client table KeywordPopupItemReference (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "KeywordPopupItemReference" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "BaseItemType" INTEGER,
    "UniqueName" JSON,
    "ItemVisualIdentity" JSON,
    "FlavourText" JSON,
    "Rarity" JSON,
    "ItemLevel" INTEGER,
    "__col7" BOOLEAN
);
