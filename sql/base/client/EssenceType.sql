-- Client table EssenceType (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "EssenceType" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "EssenceType" INTEGER,
    "IsCorruptedEssence" BOOLEAN,
    "WordsKey" INTEGER,
    "ChanceForAdditionalEssenceStat" INTEGER
);
