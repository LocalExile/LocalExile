-- Client table IndexableSkillGems (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "IndexableSkillGems" (
    "_row" INTEGER PRIMARY KEY,
    "Index" INTEGER,
    "SkillGem1" JSON,
    "Name1" TEXT,
    "SkillGem2" JSON,
    "Name2" TEXT,
    "IndexRuthless" INTEGER
);
