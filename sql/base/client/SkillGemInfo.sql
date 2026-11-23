-- Client table SkillGemInfo (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "SkillGemInfo" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Description" TEXT,
    "VideoURL1" TEXT,
    "SkillGemsKey" INTEGER,
    "VideoURL2" TEXT,
    "CharactersKeys" JSON
);
