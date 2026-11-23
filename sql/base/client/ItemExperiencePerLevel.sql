-- Client table ItemExperiencePerLevel (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "ItemExperiencePerLevel" (
    "_row" INTEGER PRIMARY KEY,
    "ItemExperienceType" INTEGER,
    "ItemCurrentLevel" INTEGER,
    "Experience" INTEGER
);
