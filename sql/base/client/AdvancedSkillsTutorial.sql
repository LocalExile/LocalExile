-- Client table AdvancedSkillsTutorial (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "AdvancedSkillsTutorial" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "SkillGemInfoKey1" JSON,
    "SkillGemInfoKey2" JSON,
    "Description" TEXT,
    "International_BK2File" TEXT,
    "SkillGemsKey" INTEGER,
    "China_BK2File" TEXT,
    "CharactersKey" JSON
);
