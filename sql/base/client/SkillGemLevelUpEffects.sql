-- Client table SkillGemLevelUpEffects (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SkillGemLevelUpEffects" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "StrGemAnimation" INTEGER,
    "DexGemAnimation" INTEGER,
    "IntGemAnimation" INTEGER,
    "OtherGemAnimation" INTEGER
);
