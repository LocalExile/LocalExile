-- Client table GemEffects (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "GemEffects" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Name" TEXT,
    "GrantedEffect" INTEGER,
    "GrantedEffectHardmode" INTEGER,
    "GrantedEffect2" INTEGER,
    "GrantedEffect2Hardmode" INTEGER,
    "SupportText" TEXT,
    "SupportName" TEXT,
    "GemTags" JSON,
    "Consumed_ModsKey" INTEGER,
    "ItemColor" INTEGER
);
