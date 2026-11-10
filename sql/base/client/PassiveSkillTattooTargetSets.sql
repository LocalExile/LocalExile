-- Client table PassiveSkillTattooTargetSets (3.29.3c); its rows come from sql/updates/client/. Written by scripts/dev/client-sql.ps1.
CREATE TABLE "PassiveSkillTattooTargetSets" (
    "_row" INTEGER PRIMARY KEY,
    "Id" TEXT,
    "Set" JSON,
    "Name" TEXT,
    "Qualifier" TEXT
);
